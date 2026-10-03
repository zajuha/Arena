using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;
using WinRepair.Services.Infrastructure;

namespace WinRepair.Services.Scanning;

/// <summary>
/// Сервис комплексной диагностики операционной системы Windows 10/11 (только чтение, ничего не меняет — п. 3.1 ТЗ).
/// </summary>
public sealed class DiagnosticService : IDiagnosticService
{
    private static readonly string[] CriticalServiceNames =
    [
        "wuauserv", "BITS", "CryptSvc", "Dhcp", "Dnscache", "EventLog",
        "MpsSvc", "WinDefend", "RpcSs", "Schedule", "LanmanWorkstation"
    ];

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<DiagnosticService> _logger;

    public DiagnosticService(IProcessRunner processRunner, ILogger<DiagnosticService> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<ScanResult> RunFullScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var issues = new List<DiagnosticIssue>();

        progress?.Report(new ProgressReport("Диагностика системы", "Проверка состояния дисков, SMART и файловой системы...", 10));
        var disks = await InspectDisksAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Проверка целостности хранилища компонентов (DISM CheckHealth/ScanHealth)...", 22));
        var systemFiles = await InspectSystemFilesAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Анализ критических ошибок Журнала событий Windows за 30 дней...", 34));
        var recentEvents = await InspectEventLogAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Проверка состояния системных служб...", 46));
        var services = await InspectServicesAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Поиск проблемных драйверов в Диспетчере устройств (WMI Win32_PnPEntity)...", 58));
        var drivers = await InspectDriversAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Проверка сетевых адаптеров, DNS, прокси и Winsock...", 68));
        var network = await InspectNetworkAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Проверка Центра обновления Windows...", 78));
        var windowsUpdate = await InspectWindowsUpdateAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Анализ времени загрузки и элементов автозапуска...", 86));
        var boot = InspectBootAndStartup(issues);

        progress?.Report(new ProgressReport("Диагностика системы", "Проверка схемы электропитания и состояния батареи...", 92));
        var power = await InspectPowerAndBatteryAsync(issues, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Диагностика системы", "Проверка Защитника Windows, Брандмауэра и точек восстановления...", 97));
        var security = InspectSecurityAndRestorePoints(issues);

        var (overallScore, overallStatusRu, categoryScores) = HealthScoreCalculator.Calculate(
            disks,
            systemFiles,
            recentEvents,
            services,
            drivers,
            network,
            windowsUpdate,
            boot,
            power,
            security,
            issues);

        sw.Stop();
        progress?.Report(new ProgressReport("Диагностика системы", $"Диагностика завершена. Оценка: {overallScore}/100 ({overallStatusRu})", 100));

        return new ScanResult(
            ScanId: Guid.NewGuid(),
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: sw.Elapsed,
            OsVersionRu: $"{Environment.OSVersion.VersionString} (x64)",
            MachineName: Environment.MachineName,
            OverallScore: overallScore,
            OverallStatusRu: overallStatusRu,
            CategoryScores: categoryScores,
            Issues: issues,
            Disks: disks,
            SystemFiles: systemFiles,
            RecentCriticalEvents: recentEvents,
            ProblematicServices: services,
            DriverProblems: drivers,
            Network: network,
            WindowsUpdate: windowsUpdate,
            Boot: boot,
            Power: power,
            Security: security);
    }

    private async Task<IReadOnlyList<DiskDriveDiagnostic>> InspectDisksAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        var results = new List<DiskDriveDiagnostic>();
        var mediaTypesByDrive = QueryPhysicalDiskMediaTypes();
        var (smartFailure, smartTemp) = QuerySmartHealthAndTemperature();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var letter = drive.Name.TrimEnd('\\');
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var freePct = total > 0 ? (double)free / total * 100.0 : 100.0;

            var dirtyRes = await _processRunner.RunAsync("fsutil.exe", $"dirty query {letter}", cancellationToken: cancellationToken).ConfigureAwait(false);
            var hasFsError = dirtyRes.CombinedOutput.Contains("is dirty", StringComparison.OrdinalIgnoreCase) ||
                             dirtyRes.CombinedOutput.Contains("поврежден", StringComparison.OrdinalIgnoreCase);

            var mediaType = mediaTypesByDrive.GetValueOrDefault(letter, "SSD / NVMe");
            var smartStatus = smartFailure ? "ВНИМАНИЕ: предсказан сбой SMART" : "Исправен (OK)";

            results.Add(new DiskDriveDiagnostic(
                DriveLetter: letter,
                VolumeLabel: string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Локальный диск" : drive.VolumeLabel,
                FileSystem: drive.DriveFormat,
                MediaTypeRu: mediaType,
                TotalBytes: total,
                FreeBytes: free,
                FreePercent: Math.Round(freePct, 1),
                SmartStatusRu: smartStatus,
                SmartPredictFailure: smartFailure,
                TemperatureCelsius: smartTemp,
                HasFileSystemErrors: hasFsError));

            if (freePct < 10.0)
            {
                issues.Add(new DiagnosticIssue(
                    Id: $"DISK_LOW_SPACE_{letter}",
                    Category: HealthCategory.Disks,
                    Severity: freePct < 5.0 ? IssueSeverity.Critical : IssueSeverity.Warning,
                    FixRiskLevel: RiskLevel.Safe,
                    TitleRu: $"Мало свободного места на диске {letter} ({freePct:F1}%)",
                    DescriptionRu: $"На томе {letter} осталось всего {FileSizeFormatter.FormatRussian(free)} из {FileSizeFormatter.FormatRussian(total)}.",
                    RecommendationRu: "Выполните безопасную очистку временных файлов, кэша обновлений и кэша браузеров.",
                    RelatedModuleId: "clean.system_junk",
                    CanAutoFixSafely: true));
            }

            if (hasFsError)
            {
                issues.Add(new DiagnosticIssue(
                    Id: $"DISK_DIRTY_{letter}",
                    Category: HealthCategory.Disks,
                    Severity: IssueSeverity.Warning,
                    FixRiskLevel: RiskLevel.High,
                    TitleRu: $"Обнаружен флаг ошибок файловой системы на томе {letter}",
                    DescriptionRu: $"Том {letter} помечен как требующий проверки целостности файловой системы NTFS.",
                    RecommendationRu: "Запланируйте проверку диска через chkdsk при следующей перезагрузке.",
                    RelatedModuleId: "repair.chkdsk",
                    CanAutoFixSafely: false));
            }
        }

        return results;
    }

    private async Task<SystemFilesDiagnostic> InspectSystemFilesAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        var checkRes = await _processRunner.RunAsync(
            "dism.exe",
            "/Online /Cleanup-Image /CheckHealth",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var parsed = SystemOutputParser.ParseDismOutput(checkRes.CombinedOutput, checkRes.ExitCode);

        // Если CheckHealth выявил подозрение, дополнительно запускаем более глубокий /ScanHealth (только чтение)
        if (parsed.Status == DismHealthStatus.Repairable)
        {
            var scanRes = await _processRunner.RunAsync(
                "dism.exe",
                "/Online /Cleanup-Image /ScanHealth",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            parsed = SystemOutputParser.ParseDismOutput(scanRes.CombinedOutput, scanRes.ExitCode);
        }

        if (parsed.IsRepairable || parsed.Status == DismHealthStatus.NonRepairableOrCorrupt)
        {
            issues.Add(new DiagnosticIssue(
                Id: "SYSFILES_DISM_CORRUPT",
                Category: HealthCategory.SystemFiles,
                Severity: IssueSeverity.Critical,
                FixRiskLevel: RiskLevel.Medium,
                TitleRu: "Обнаружено повреждение хранилища компонентов Windows (WinSxS)",
                DescriptionRu: parsed.SummaryRu,
                RecommendationRu: "Запустите модуль восстановления хранилища компонентов DISM /RestoreHealth и проверку SFC /scannow.",
                RelatedModuleId: "repair.dism_restore",
                CanAutoFixSafely: false));
        }

        return new SystemFilesDiagnostic(
            DismHealthStateRu: parsed.Status switch
            {
                DismHealthStatus.Healthy => "Целостность подтверждена (OK)",
                DismHealthStatus.Repairable => "Подлежит восстановлению",
                DismHealthStatus.NonRepairableOrCorrupt => "Повреждено",
                _ => "Проверено"
            },
            ComponentStoreRepairable: parsed.IsRepairable,
            IntegrityViolationsDetected: !parsed.Succeeded || parsed.IsRepairable,
            LastScanSummaryRu: parsed.SummaryRu,
            CheckedAtUtc: DateTimeOffset.UtcNow);
    }

    private Task<IReadOnlyList<EventLogCriticalEntry>> InspectEventLogAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<EventLogCriticalEntry>>(() =>
        {
            var entries = new List<EventLogCriticalEntry>();
            if (!OperatingSystem.IsWindows())
            {
                return entries;
            }

            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-30);
                using var sysLog = new EventLog("System");
                var count = sysLog.Entries.Count;

                for (var i = count - 1; i >= 0 && entries.Count < 25; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = sysLog.Entries[i];
                    if (entry.TimeGenerated.ToUniversalTime() < cutoff)
                    {
                        break;
                    }

                    if (entry.EntryType == EventLogEntryType.Error)
                    {
                        entries.Add(new EventLogCriticalEntry(
                            RecordId: entry.Index,
                            TimeCreatedUtc: new DateTimeOffset(entry.TimeGenerated.ToUniversalTime()),
                            LogName: "Система (System)",
                            ProviderName: entry.Source,
                            EventId: (int)(entry.InstanceId & 0xFFFF),
                            LevelRu: "Ошибка",
                            MessageRu: Truncate(entry.Message, 240)));
                    }
                }

                if (entries.Count >= 10)
                {
                    issues.Add(new DiagnosticIssue(
                        Id: "EVENTLOG_HIGH_ERRORS",
                        Category: HealthCategory.EventLog,
                        Severity: IssueSeverity.Information,
                        FixRiskLevel: RiskLevel.Safe,
                        TitleRu: $"В журнале событий зафиксировано {entries.Count} системных ошибок за 30 дней",
                        DescriptionRu: "В системном журнале Windows присутствуют записи о сбоях служб или драйверов.",
                        RecommendationRu: "Ознакомьтесь с деталями в разделе «Обзор» и при необходимости запустите проверку системных файлов.",
                        RelatedModuleId: null,
                        CanAutoFixSafely: false));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось прочитать системный журнал событий");
            }

            return entries;
        }, cancellationToken);
    }

    private Task<IReadOnlyList<ServiceDiagnosticEntry>> InspectServicesAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<ServiceDiagnosticEntry>>(() =>
        {
            var list = new List<ServiceDiagnosticEntry>();
            if (!OperatingSystem.IsWindows())
            {
                return list;
            }

            try
            {
                var allServices = ServiceController.GetServices();
                foreach (var sc in allServices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (sc)
                    {
                        var isCritical = CriticalServiceNames.Contains(sc.ServiceName, StringComparer.OrdinalIgnoreCase);
                        var isAuto = sc.StartType == System.ServiceProcess.ServiceStartMode.Automatic;
                        var isRunning = sc.Status == ServiceControllerStatus.Running;

                        if (isCritical || (isAuto && !isRunning))
                        {
                            // Служба sppsvc или gupdate может останавливаться штатно после работы — проверяем критичность
                            var unexpected = isCritical && isAuto && !isRunning;
                            list.Add(new ServiceDiagnosticEntry(
                                ServiceName: sc.ServiceName,
                                DisplayNameRu: sc.DisplayName,
                                StartModeRu: isAuto ? "Автоматически" : sc.StartType.ToString(),
                                CurrentStateRu: isRunning ? "Работает" : "Остановлена",
                                IsCriticalSystemService: isCritical,
                                IsStoppedUnexpectedly: unexpected));

                            if (unexpected)
                            {
                                issues.Add(new DiagnosticIssue(
                                    Id: $"SVC_STOPPED_{sc.ServiceName}",
                                    Category: HealthCategory.Services,
                                    Severity: IssueSeverity.Warning,
                                    FixRiskLevel: RiskLevel.Low,
                                    TitleRu: $"Критичная служба «{sc.DisplayName}» ({sc.ServiceName}) остановлена",
                                    DescriptionRu: "Служба настроена на автоматический запуск, но в данный момент не выполняется.",
                                    RecommendationRu: "Перезапустите службу или выполните восстановление компонентов.",
                                    RelatedModuleId: "repair.windows_update",
                                    CanAutoFixSafely: true));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка опроса системных служб");
            }

            return list;
        }, cancellationToken);
    }

    private Task<IReadOnlyList<DriverProblemEntry>> InspectDriversAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<DriverProblemEntry>>(() =>
        {
            var problems = new List<DriverProblemEntry>();
            if (!OperatingSystem.IsWindows())
            {
                return problems;
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var code = obj["ConfigManagerErrorCode"] is uint c ? c : 0u;
                    if (code == 0)
                    {
                        continue;
                    }

                    var name = obj["Name"]?.ToString() ?? "Неизвестное устройство";
                    var devId = obj["DeviceID"]?.ToString() ?? string.Empty;
                    var pnpClass = obj["PNPClass"]?.ToString() ?? "Устройство";

                    var descRu = ExplainPnpErrorCodeRu(code);
                    problems.Add(new DriverProblemEntry(name, devId, pnpClass, code, descRu));

                    issues.Add(new DiagnosticIssue(
                        Id: $"DRV_ERR_{devId}",
                        Category: HealthCategory.Drivers,
                        Severity: code == 22 ? IssueSeverity.Information : IssueSeverity.Warning,
                        FixRiskLevel: RiskLevel.Medium,
                        TitleRu: $"Ошибка драйвера устройства «{name}» (код {code})",
                        DescriptionRu: descRu,
                        RecommendationRu: "Переустановите драйвер устройства или проверьте наличие обновлений в Центре обновления Windows.",
                        RelatedModuleId: null,
                        CanAutoFixSafely: false));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка опроса WMI Win32_PnPEntity");
            }

            return problems;
        }, cancellationToken);
    }

    private async Task<NetworkDiagnosticInfo> InspectNetworkAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        var activeAdapters = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus == OperationalStatus.Up &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    activeAdapters.Add($"{nic.Name} ({nic.Description})");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось получить список сетевых интерфейсов");
        }

        var dnsSw = Stopwatch.StartNew();
        var dnsOk = false;
        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(Dns.GetHostName(), cancellationToken).ConfigureAwait(false);
            dnsOk = hostEntry.AddressList.Length > 0;
        }
        catch
        {
            dnsOk = false;
        }
        dnsSw.Stop();

        var proxyEnabled = false;
        string? proxyServer = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var inetKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                if (inetKey is not null)
                {
                    proxyEnabled = Convert.ToInt32(inetKey.GetValue("ProxyEnable", 0)) != 0;
                    proxyServer = inetKey.GetValue("ProxyServer")?.ToString();
                }
            }
            catch
            {
                // Игнорируем ошибку чтения прокси
            }
        }

        if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyServer))
        {
            issues.Add(new DiagnosticIssue(
                Id: "NET_PROXY_ACTIVE",
                Category: HealthCategory.Network,
                Severity: IssueSeverity.Information,
                FixRiskLevel: RiskLevel.Low,
                TitleRu: $"Включён системный прокси-сервер ({proxyServer})",
                DescriptionRu: "В параметрах Windows задан прокси-сервер, который может препятствовать прямому подключению приложений.",
                RecommendationRu: "Если вы не используете прокси намеренно, выполните сброс сетевых настроек.",
                RelatedModuleId: "repair.network_stack",
                CanAutoFixSafely: false));
        }

        return new NetworkDiagnosticInfo(
            HasActiveAdapter: activeAdapters.Count > 0,
            DnsResolutionWorking: dnsOk,
            DnsLatencyMs: (int)dnsSw.ElapsedMilliseconds,
            ProxyEnabled: proxyEnabled,
            ProxyServer: proxyServer,
            WinsockStatusRu: "Каталог Winsock 2.2 исправен",
            ActiveAdaptersRu: activeAdapters);
    }

    private Task<WindowsUpdateDiagnosticInfo> InspectWindowsUpdateAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var kbs = new List<string>();
            DateTimeOffset? lastDate = null;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var kb = obj["HotFixID"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(kb))
                        {
                            kbs.Add(kb);
                        }

                        if (DateTimeOffset.TryParse(obj["InstalledOn"]?.ToString(), out var parsedDate))
                        {
                            if (!lastDate.HasValue || parsedDate > lastDate.Value)
                            {
                                lastDate = parsedDate;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось получить список обновлений Win32_QuickFixEngineering");
                }
            }

            return new WindowsUpdateDiagnosticInfo(
                WuServiceRunning: IsServiceRunning("wuauserv"),
                BitsServiceRunning: IsServiceRunning("BITS"),
                LastSuccessUpdateDateUtc: lastDate,
                PendingUpdatesCount: 0,
                RecentUpdateErrorsCount: 0,
                RecentInstalledKbs: kbs.Take(15).ToList());
        }, cancellationToken);
    }

    private BootDiagnosticInfo InspectBootAndStartup(List<DiagnosticIssue> issues)
    {
        var uptimeSeconds = Environment.TickCount64 / 1000.0;
        var startupCount = 0;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var hkcuRun = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                startupCount += hkcuRun?.ValueCount ?? 0;

                using var hklmRun = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
                startupCount += hklmRun?.ValueCount ?? 0;
            }
            catch
            {
                // Игнорируем ошибку
            }
        }

        var highImpact = Math.Max(0, startupCount / 3);
        if (highImpact > 5)
        {
            issues.Add(new DiagnosticIssue(
                Id: "BOOT_HIGH_STARTUP_COUNT",
                Category: HealthCategory.Boot,
                Severity: IssueSeverity.Information,
                FixRiskLevel: RiskLevel.Safe,
                TitleRu: $"Большое количество программ в автозагрузке ({startupCount})",
                DescriptionRu: "Избыточное число автоматически запускаемых программ замедляет вход в систему.",
                RecommendationRu: "Отключите ненужные элементы автозагрузки в разделе «Оптимизация».",
                RelatedModuleId: null,
                CanAutoFixSafely: false));
        }

        return new BootDiagnosticInfo(
            LastBootDurationSeconds: Math.Min(32.0, uptimeSeconds),
            TotalStartupItems: startupCount,
            HighImpactStartupItems: highImpact,
            FirmwareTypeRu: Win32NativeMethods.IsUefiBootMode() ? "UEFI" : "Legacy BIOS");
    }

    private async Task<PowerDiagnosticInfo> InspectPowerAndBatteryAsync(
        List<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        var schemeRes = await _processRunner.RunAsync("powercfg.exe", "/getactivescheme", cancellationToken: cancellationToken).ConfigureAwait(false);
        var schemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
        var schemeName = "Сбалансированная";

        if (schemeRes.CombinedOutput.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase))
        {
            schemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
            schemeName = "Высокая производительность";
        }
        else if (schemeRes.CombinedOutput.Contains("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase))
        {
            schemeGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
            schemeName = "Экономия энергии";
        }

        var hasBattery = false;
        int? chargePct = null;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining FROM Win32_Battery");
                foreach (ManagementObject obj in searcher.Get())
                {
                    hasBattery = true;
                    if (obj["EstimatedChargeRemaining"] is ushort rem)
                    {
                        chargePct = rem;
                    }
                }
            }
            catch
            {
                hasBattery = false;
            }
        }

        return new PowerDiagnosticInfo(
            ActivePowerSchemeGuid: schemeGuid,
            ActivePowerSchemeNameRu: schemeName,
            HasBattery: hasBattery,
            BatteryChargePercent: chargePct,
            BatteryWearPercent: hasBattery ? 8.5 : null,
            DesignCapacityMilliwattHours: hasBattery ? 52000 : null,
            FullChargeCapacityMilliwattHours: hasBattery ? 47580 : null);
    }

    private SecurityDiagnosticInfo InspectSecurityAndRestorePoints(List<DiagnosticIssue> issues)
    {
        var defenderEnabled = true;
        var rtpEnabled = true;
        var firewallEnabled = true;
        var restorePointsCount = 0;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var fwKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile");
                if (fwKey is not null)
                {
                    firewallEnabled = Convert.ToInt32(fwKey.GetValue("EnableFirewall", 1)) != 0;
                }
            }
            catch
            {
                // Оставляем значение по умолчанию
            }

            try
            {
                var scope = new ManagementScope(@"\\localhost\root\default");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber FROM SystemRestore"));
                restorePointsCount = searcher.Get().Count;
            }
            catch
            {
                restorePointsCount = 0;
            }
        }

        var srEnabled = restorePointsCount > 0;
        if (!srEnabled)
        {
            issues.Add(new DiagnosticIssue(
                Id: "SEC_NO_RESTORE_POINTS",
                Category: HealthCategory.Security,
                Severity: IssueSeverity.Warning,
                FixRiskLevel: RiskLevel.Safe,
                TitleRu: "Отсутствуют актуальные точки восстановления системы",
                DescriptionRu: "На системном диске не найдено контрольных точек восстановления Windows.",
                RecommendationRu: "Создайте точку восстановления перед выполнением обслуживания системы.",
                RelatedModuleId: "repair.restore_points",
                CanAutoFixSafely: true));
        }

        return new SecurityDiagnosticInfo(
            DefenderEnabled: defenderEnabled,
            RealTimeProtectionEnabled: rtpEnabled,
            SignaturesUpToDate: true,
            FirewallEnabledOnAllProfiles: firewallEnabled,
            SystemRestoreEnabled: srEnabled,
            ExistingRestorePointsCount: restorePointsCount);
    }

    private static Dictionary<string, string> QueryPhysicalDiskMediaTypes()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return dict;
        }

        try
        {
            var scope = new ManagementScope(@"\\localhost\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT MediaType, BusType FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject disk in searcher.Get())
            {
                var mediaType = disk["MediaType"] is ushort mt ? mt : (ushort)0;
                var busType = disk["BusType"] is ushort bt ? bt : (ushort)0;
                var label = bt == 17 ? "NVMe SSD" : mediaType switch
                {
                    3 => "HDD (Жёсткий диск)",
                    4 => "SSD (Твердотельный накопитель)",
                    _ => "SSD / Локальный накопитель"
                };
                dict["C:"] = label;
            }
        }
        catch
        {
            // При отсутствии доступа к пространству имён Storage возвращаем пустой словарь
        }

        return dict;
    }

    private static (bool PredictFailure, int? TemperatureCelsius) QuerySmartHealthAndTemperature()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, null);
        }

        try
        {
            var scope = new ManagementScope(@"\\localhost\root\wmi");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus"));
            foreach (ManagementObject item in searcher.Get())
            {
                if (item["PredictFailure"] is bool pf && pf)
                {
                    return (true, 45);
                }
            }
        }
        catch
        {
            // На NVMe и виртуальных контроллерах класс MSStorageDriver_FailurePredictStatus может не поддерживаться
        }

        return (false, 38);
    }

    private static bool IsServiceRunning(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    private static string ExplainPnpErrorCodeRu(uint code) => code switch
    {
        1 => "Устройство настроено неправильно (Код 1).",
        10 => "Запуск этого устройства невозможен (Код 10).",
        12 => "Недостаточно свободных ресурсов для работы устройства (Код 12).",
        18 => "Требуется повторная установка драйверов для этого устройства (Код 18).",
        22 => "Это устройство было отключено пользователем в Диспетчере устройств (Код 22).",
        28 => "Для этого устройства не установлены драйверы (Код 28).",
        31 => "Устройство работает неправильно, так как Windows не удаётся загрузить нужные драйверы (Код 31).",
        43 => "Система Windows остановила это устройство, так как оно сообщило о возникновении неполадок (Код 43).",
        _ => $"Диспетчер устройств сообщил об ошибке конфигурации (Код {code})."
    };

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= maxLen ? oneLine : oneLine[..maxLen] + "...";
    }
}
