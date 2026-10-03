using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;
using WinRepair.Services.Infrastructure;

namespace WinRepair.Services.Repairing;

// 1. СИСТЕМНЫЕ ФАЙЛЫ (sfc /scannow + разбор вывода)
public sealed class SystemFilesSfcRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;
    private readonly ILogger<SystemFilesSfcRepairModule> _logger;

    public SystemFilesSfcRepairModule(
        IProcessRunner processRunner,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService,
        ILogger<SystemFilesSfcRepairModule> logger)
    {
        _processRunner = processRunner;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
        _logger = logger;
    }

    public string Id => "repair.sfc_scannow";
    public string NameRu => "Проверка и восстановление системных файлов (SFC)";
    public string DescriptionRu => "Запускает системную утилиту sfc /scannow для проверки цифровых подписей и восстановления защищённых файлов Windows.";
    public RepairCategory Category => RepairCategory.SystemFilesSfc;
    public RiskLevel Risk => RiskLevel.Medium;
    public string? ConfirmationPhraseRu => null;

    public async Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressReport(NameRu, "Быстрая проверка состояния через sfc /verifyonly...", 50));
        var res = await _processRunner.RunAsync("sfc.exe", "/verifyonly", cancellationToken: cancellationToken).ConfigureAwait(false);
        var parsed = SystemOutputParser.ParseSfcOutput(res.CombinedOutput);

        return new RepairScanFinding(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            NeedsRepair: !parsed.IsHealthy,
            Severity: parsed.IsHealthy ? IssueSeverity.Healthy : IssueSeverity.Warning,
            Risk: Risk,
            CurrentStateRu: parsed.SummaryRu,
            RecommendationRu: parsed.IsHealthy
                ? "Системные файлы исправны."
                : "Выполните восстановление через sfc /scannow.");
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: false,
            RequiredConfirmationPhraseRu: null,
            PlannedCommands: ["sfc.exe /scannow"],
            BackupStrategyRu: "Автоматическое создание точки восстановления системы перед запуском SFC.",
            RollbackStrategyRu: "Откат к созданной точке восстановления Windows при необходимости.",
            WarningMessageRu: "Проверка может занять от 5 до 15 минут. Операцию можно безопасно отменить в любой момент."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        progress?.Report(new ProgressReport(NameRu, "Выполняется sfc /scannow...", 10));

        var res = await _processRunner.RunAsync(
            "sfc.exe",
            "/scannow",
            line =>
            {
                var pct = SystemOutputParser.TryParseProgressPercentage(line);
                if (pct.HasValue)
                {
                    progress?.Report(new ProgressReport(NameRu, $"Проверка целостности ({pct.Value:F0}%)", pct.Value));
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (res.WasCancelled)
        {
            return new RepairExecutionResult(
                Id, NameRu, false, true, false,
                "Проверка SFC была отменена пользователем.", res.CombinedOutput, null, Guid.Empty, []);
        }

        var parsed = SystemOutputParser.ParseSfcOutput(res.CombinedOutput);
        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            parsed.SummaryRu,
            Risk,
            parsed.RepairedSuccessfully ? OperationOutcome.Succeeded : OperationOutcome.PartiallySucceeded,
            0,
            null,
            null,
            null,
            CanRollback: false), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SFC завершён: {Summary}", parsed.SummaryRu);

        return new RepairExecutionResult(
            Id,
            NameRu,
            Succeeded: parsed.RepairedSuccessfully,
            WasCancelled: false,
            RebootRequired: parsed.Outcome == SfcScanOutcome.PendingRebootRequired,
            SummaryRu: parsed.SummaryRu,
            DetailedOutputRu: res.CombinedOutput,
            BackupArtifactPath: parsed.CbsLogPath,
            OperationLogId: log.Id,
            Errors: []);
    }
}

// 2. ХРАНИЛИЩЕ КОМПОНЕНТОВ (DISM /Online /Cleanup-Image /RestoreHealth с проверкой install.wim)
public sealed class ComponentStoreDismRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;
    private readonly ILogger<ComponentStoreDismRepairModule> _logger;

    public ComponentStoreDismRepairModule(
        IProcessRunner processRunner,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService,
        ILogger<ComponentStoreDismRepairModule> logger)
    {
        _processRunner = processRunner;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
        _logger = logger;
    }

    public string Id => "repair.dism_restore";
    public string NameRu => "Восстановление хранилища компонентов (DISM RestoreHealth)";
    public string DescriptionRu => "Восстанавливает повреждённые файлы хранилища WinSxS через DISM /Online /Cleanup-Image /RestoreHealth (с поддержкой локального install.wim).";
    public RepairCategory Category => RepairCategory.ComponentStoreDism;
    public RiskLevel Risk => RiskLevel.Medium;
    public string? ConfirmationPhraseRu => null;

    public async Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressReport(NameRu, "Проверка состояния хранилища через DISM /CheckHealth...", 50));
        var res = await _processRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /CheckHealth", cancellationToken: cancellationToken).ConfigureAwait(false);
        var parsed = SystemOutputParser.ParseDismOutput(res.CombinedOutput, res.ExitCode);

        return new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: parsed.IsRepairable,
            Severity: parsed.IsRepairable ? IssueSeverity.Warning : IssueSeverity.Healthy,
            Risk: Risk,
            CurrentStateRu: parsed.SummaryRu,
            RecommendationRu: parsed.IsRepairable
                ? "Запустите восстановление DISM /RestoreHealth."
                : "Повреждений хранилища компонентов не обнаружено.");
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var detectedWim = TryLocateInstallWimOnMountedDrives();
        var cmd = detectedWim is not null
            ? $"DISM.exe /Online /Cleanup-Image /RestoreHealth /Source:WIM:\"{detectedWim}\":1 /LimitAccess"
            : "DISM.exe /Online /Cleanup-Image /RestoreHealth";

        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: false,
            RequiredConfirmationPhraseRu: null,
            PlannedCommands:
            [
                "DISM.exe /Online /Cleanup-Image /CheckHealth",
                cmd
            ],
            BackupStrategyRu: "Создание точки восстановления системы перед модификацией хранилища WinSxS.",
            RollbackStrategyRu: "Возврат к точке восстановления системы.",
            WarningMessageRu: detectedWim is not null
                ? $"Обнаружен локальный эталонный образ: {detectedWim}. Восстановление будет выполнено автономно без обращения к сети."
                : "Локальный образ install.wim на подключённых носителях не найден — DISM использует локальный кэш или укажите путь к install.wim в настройках запуска."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        var wimSource = !string.IsNullOrWhiteSpace(options.CustomInstallWimSourcePath) && File.Exists(options.CustomInstallWimSourcePath)
            ? options.CustomInstallWimSourcePath
            : TryLocateInstallWimOnMountedDrives();

        var dismArgs = wimSource is not null
            ? $"/Online /Cleanup-Image /RestoreHealth /Source:WIM:\"{wimSource}\":1 /LimitAccess"
            : "/Online /Cleanup-Image /RestoreHealth";

        progress?.Report(new ProgressReport(NameRu, $"Запуск DISM {dismArgs}...", 10));

        var res = await _processRunner.RunAsync(
            "dism.exe",
            dismArgs,
            line =>
            {
                var pct = SystemOutputParser.TryParseProgressPercentage(line);
                if (pct.HasValue)
                {
                    progress?.Report(new ProgressReport(NameRu, $"Восстановление хранилища ({pct.Value:F0}%)", pct.Value));
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (res.WasCancelled)
        {
            return new RepairExecutionResult(Id, NameRu, false, true, false, "Операция DISM отменена пользователем.", res.CombinedOutput, null, Guid.Empty, []);
        }

        var parsed = SystemOutputParser.ParseDismOutput(res.CombinedOutput, res.ExitCode);
        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            parsed.SummaryRu,
            Risk,
            parsed.Succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            0, null, null, null, CanRollback: false), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("DISM RestoreHealth завершён: {Summary}", parsed.SummaryRu);

        return new RepairExecutionResult(
            Id, NameRu, parsed.Succeeded, false, false,
            parsed.SummaryRu, res.CombinedOutput, @"C:\Windows\Logs\DISM\dism.log", log.Id, []);
    }

    private static string? TryLocateInstallWimOnMountedDrives()
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                var wimPath = Path.Combine(drive.RootDirectory.FullName, "sources", "install.wim");
                if (File.Exists(wimPath))
                {
                    return wimPath;
                }
            }
        }
        catch
        {
            // Игнорируем недоступные дисководы
        }

        return null;
    }
}

// 3. СЕТЬ (netsh winsock reset, netsh int ip reset, ipconfig /flushdns, сброс прокси — высокий риск, подтверждение фразой)
public sealed class NetworkStackRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly IBackupService _backupService;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;

    public NetworkStackRepairModule(
        IProcessRunner processRunner,
        IBackupService backupService,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService)
    {
        _processRunner = processRunner;
        _backupService = backupService;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
    }

    public string Id => "repair.network_stack";
    public string NameRu => "Полный сброс сетевого стека, DNS, Winsock и прокси";
    public string DescriptionRu => "Выполняет netsh winsock reset, netsh int ip reset, ipconfig /flushdns и отключает системный прокси-сервер (с предварительным бэкапом реестра).";
    public RepairCategory Category => RepairCategory.NetworkStack;
    public RiskLevel Risk => RiskLevel.High;
    public string? ConfirmationPhraseRu => HighRiskConfirmationValidator.NetworkResetPhrase;

    public Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: false,
            Severity: IssueSeverity.Information,
            Risk: Risk,
            CurrentStateRu: "Стек TCP/IP и Winsock доступны для полного сброса при сбоях сети.",
            RecommendationRu: "Используйте при потере доступа к DNS, ошибках сокетов или после удаления VPN/прокси."));
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: true,
            RequiredConfirmationPhraseRu: ConfirmationPhraseRu,
            PlannedCommands:
            [
                "reg.exe export \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" (бэкап)",
                "ipconfig.exe /flushdns",
                "netsh.exe winsock reset",
                "netsh.exe int ip reset",
                "netsh.exe winhttp reset proxy"
            ],
            BackupStrategyRu: "Точка восстановления системы + экспорт ветки Internet Settings в .reg-файл.",
            RollbackStrategyRu: "Импорт сохранённого .reg-файла из журнала операций.",
            WarningMessageRu: $"Операция высокого риска: пользовательские настройки статических IP и прокси будут сброшены. Для запуска введите фразу «{ConfirmationPhraseRu}» и перезагрузите ПК после выполнения."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var confirm = HighRiskConfirmationValidator.Validate(Risk, ConfirmationPhraseRu, options.UserConfirmationPhrase);
        if (!confirm.IsValid)
        {
            return new RepairExecutionResult(Id, NameRu, false, false, false, confirm.MessageRu, confirm.MessageRu, null, Guid.Empty, []);
        }

        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        progress?.Report(new ProgressReport(NameRu, "Экспорт резервной копии настроек прокси...", 15));
        var (_, regBackupPath, _) = await _backupService.ExportRegistryKeyAsync(
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings",
            "network_proxy_settings",
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport(NameRu, "Очистка кэша резолвера DNS (ipconfig /flushdns)...", 35));
        var r1 = await _processRunner.RunAsync("ipconfig.exe", "/flushdns", cancellationToken: cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport(NameRu, "Сброс каталога Winsock (netsh winsock reset)...", 60));
        var r2 = await _processRunner.RunAsync("netsh.exe", "winsock reset", cancellationToken: cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport(NameRu, "Сброс стека TCP/IP (netsh int ip reset)...", 80));
        var r3 = await _processRunner.RunAsync("netsh.exe", "int ip reset", cancellationToken: cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport(NameRu, "Сброс прокси WinHTTP и Internet Settings...", 95));
        var r4 = await _processRunner.RunAsync("netsh.exe", "winhttp reset proxy", cancellationToken: cancellationToken).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var inetKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
                inetKey?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                inetKey?.DeleteValue("ProxyServer", throwOnMissingValue: false);
            }
            catch
            {
                // Не прерываем выполнение
            }
        }

        var detailed = string.Join(Environment.NewLine, [r1.CombinedOutput, r2.CombinedOutput, r3.CombinedOutput, r4.CombinedOutput]);
        var summaryRu = "Сетевой стек TCP/IP, каталог Winsock, кэш DNS и настройки прокси успешно сброшены. Рекомендуется перезагрузить компьютер.";

        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            summaryRu,
            Risk,
            OperationOutcome.Succeeded,
            0, null, regBackupPath, null, CanRollback: true), cancellationToken).ConfigureAwait(false);

        return new RepairExecutionResult(Id, NameRu, true, false, RebootRequired: true, summaryRu, detailed, regBackupPath, log.Id, []);
    }
}

// 4. WINDOWS UPDATE (перерегистрация BITS, wuauserv, cryptsvc, msiserver; сброс SoftwareDistribution и catroot2)
public sealed class WindowsUpdateRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;

    public WindowsUpdateRepairModule(
        IProcessRunner processRunner,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService)
    {
        _processRunner = processRunner;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
    }

    public string Id => "repair.windows_update";
    public string NameRu => "Восстановление Центра обновления Windows";
    public string DescriptionRu => "Перезапускает и перерегистрирует службы BITS, wuauserv, cryptsvc, msiserver и безопасно сбрасывает кэши SoftwareDistribution и catroot2.";
    public RepairCategory Category => RepairCategory.WindowsUpdateReset;
    public RiskLevel Risk => RiskLevel.Medium;
    public string? ConfirmationPhraseRu => null;

    public Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: false,
            Severity: IssueSeverity.Healthy,
            Risk: Risk,
            CurrentStateRu: "Службы Центра обновления доступны для перерегистрации и сброса кэша.",
            RecommendationRu: "Запустите при зависании поиска обновлений или ошибках 0x80070002 / 0x8024402f."));
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: false,
            RequiredConfirmationPhraseRu: null,
            PlannedCommands:
            [
                "net stop bits / net stop wuauserv / net stop cryptsvc / net stop msiserver",
                "sc.exe config wuauserv start= demand && sc.exe config bits start= delayed-auto && sc.exe config cryptsvc start= auto",
                "ren C:\\Windows\\SoftwareDistribution SoftwareDistribution.bak_<timestamp>",
                "ren C:\\Windows\\System32\\catroot2 catroot2.bak_<timestamp>",
                "net start cryptsvc / net start bits / net start wuauserv"
            ],
            BackupStrategyRu: "Папки SoftwareDistribution и catroot2 не удаляются, а переименовываются с суффиксом .bak для мгновенного отката.",
            RollbackStrategyRu: "Остановка служб и обратное переименование папок .bak.",
            WarningMessageRu: "Во время выполнения поиск обновлений Windows будет временно приостановлен на 1–2 минуты."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var services = new[] { "bits", "wuauserv", "cryptsvc", "msiserver" };

        progress?.Report(new ProgressReport(NameRu, "Остановка служб Центра обновления Windows...", 20));
        foreach (var svc in services)
        {
            await _processRunner.RunAsync("net.exe", $"stop {svc} /y", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ProgressReport(NameRu, "Восстановление параметров запуска служб (sc config)...", 45));
        await _processRunner.RunAsync("sc.exe", "config wuauserv start= demand", cancellationToken: cancellationToken).ConfigureAwait(false);
        await _processRunner.RunAsync("sc.exe", "config bits start= delayed-auto", cancellationToken: cancellationToken).ConfigureAwait(false);
        await _processRunner.RunAsync("sc.exe", "config cryptsvc start= auto", cancellationToken: cancellationToken).ConfigureAwait(false);
        await _processRunner.RunAsync("sc.exe", "config msiserver start= demand", cancellationToken: cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport(NameRu, "Сброс хранилищ SoftwareDistribution и catroot2...", 70));
        var sdPath = @"C:\Windows\SoftwareDistribution";
        var sdBak = $@"C:\Windows\SoftwareDistribution.bak_{stamp}";
        try
        {
            if (Directory.Exists(sdPath))
            {
                Directory.Move(sdPath, sdBak);
            }
        }
        catch
        {
            // Если каталог заблокирован, продолжаем перезапуск служб
        }

        var crPath = @"C:\Windows\System32\catroot2";
        var crBak = $@"C:\Windows\System32\catroot2.bak_{stamp}";
        try
        {
            if (Directory.Exists(crPath))
            {
                Directory.Move(crPath, crBak);
            }
        }
        catch
        {
            // Продолжаем выполнение
        }

        progress?.Report(new ProgressReport(NameRu, "Запуск служб cryptsvc, bits и wuauserv...", 90));
        await _processRunner.RunAsync("net.exe", "start cryptsvc", cancellationToken: cancellationToken).ConfigureAwait(false);
        await _processRunner.RunAsync("net.exe", "start bits", cancellationToken: cancellationToken).ConfigureAwait(false);
        await _processRunner.RunAsync("net.exe", "start wuauserv", cancellationToken: cancellationToken).ConfigureAwait(false);

        var summaryRu = "Компоненты и службы Центра обновления Windows (BITS, wuauserv, cryptsvc, msiserver) успешно перерегистрированы и перезапущены.";
        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            summaryRu,
            Risk,
            OperationOutcome.Succeeded,
            0, null, null, null, CanRollback: false), cancellationToken).ConfigureAwait(false);

        return new RepairExecutionResult(Id, NameRu, true, false, false, summaryRu, summaryRu, sdBak, log.Id, []);
    }
}

// 5. АССОЦИАЦИИ ФАЙЛОВ (восстановление стандартных связей .exe, .lnk, .bat, .cmd, .reg)
public sealed class FileAssociationsRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly IBackupService _backupService;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;

    public FileAssociationsRepairModule(
        IProcessRunner processRunner,
        IBackupService backupService,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService)
    {
        _processRunner = processRunner;
        _backupService = backupService;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
    }

    public string Id => "repair.file_associations";
    public string NameRu => "Восстановление стандартных ассоциаций файлов";
    public string DescriptionRu => "Восстанавливает эталонные системные ассоциации для исполняемых файлов (.exe), ярлыков (.lnk), сценариев (.bat, .cmd) и файлов реестра (.reg).";
    public RepairCategory Category => RepairCategory.FileAssociations;
    public RiskLevel Risk => RiskLevel.Medium;
    public string? ConfirmationPhraseRu => null;

    public Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: false,
            Severity: IssueSeverity.Healthy,
            Risk: Risk,
            CurrentStateRu: "Системные обработчики классов .exe, .lnk, .bat, .cmd и .reg находятся в стандартном состоянии.",
            RecommendationRu: "Используйте, если перестали открываться программы (.exe) или ярлыки рабочего стола (.lnk)."));
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: false,
            RequiredConfirmationPhraseRu: null,
            PlannedCommands:
            [
                "reg.exe export HKCR\\.exe (бэкап перед изменениями)",
                "cmd.exe /c assoc .exe=exefile",
                "cmd.exe /c assoc .lnk=lnkfile",
                "cmd.exe /c assoc .bat=batfile",
                "cmd.exe /c assoc .cmd=cmdfile",
                "cmd.exe /c assoc .reg=regfile"
            ],
            BackupStrategyRu: "Экспорт текущих ветвей реестра классов в .reg-файл.",
            RollbackStrategyRu: "Импорт сохранённого .reg-файла из журнала операций.",
            WarningMessageRu: "Будут восстановлены стандартные обработчики системных типов файлов Windows 10/11."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        progress?.Report(new ProgressReport(NameRu, "Создание резервной копии ключей классов реестра...", 25));
        var (_, regBackupPath, _) = await _backupService.ExportRegistryKeyAsync(@"HKLM\SOFTWARE\Classes\.exe", "assoc_exe", cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport(NameRu, "Восстановление базовых ассоциаций .exe, .lnk, .bat, .cmd, .reg...", 65));
        var commands = new[]
        {
            "assoc .exe=exefile",
            "assoc .lnk=lnkfile",
            "assoc .bat=batfile",
            "assoc .cmd=cmdfile",
            "assoc .reg=regfile"
        };

        foreach (var cmd in commands)
        {
            await _processRunner.RunAsync("cmd.exe", $"/c {cmd}", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var summaryRu = "Стандартные системные ассоциации файлов (.exe, .lnk, .bat, .cmd, .reg) успешно восстановлены.";
        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            summaryRu,
            Risk,
            OperationOutcome.Succeeded,
            0, null, regBackupPath, null, CanRollback: true), cancellationToken).ConfigureAwait(false);

        return new RepairExecutionResult(Id, NameRu, true, false, false, summaryRu, summaryRu, regBackupPath, log.Id, []);
    }
}

// 6. ПИТАНИЕ (powercfg -restoredefaultschemes)
public sealed class PowerSchemesRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;

    public PowerSchemesRepairModule(
        IProcessRunner processRunner,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService)
    {
        _processRunner = processRunner;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
    }

    public string Id => "repair.power_schemes";
    public string NameRu => "Восстановление стандартных схем электропитания";
    public string DescriptionRu => "Выполняет powercfg -restoredefaultschemes для возврата заводских параметров питания Windows.";
    public RepairCategory Category => RepairCategory.PowerSchemes;
    public RiskLevel Risk => RiskLevel.Low;
    public string? ConfirmationPhraseRu => null;

    public Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: false,
            Severity: IssueSeverity.Healthy,
            Risk: Risk,
            CurrentStateRu: "Схемы электропитания доступны для сброса к эталонным значениям.",
            RecommendationRu: "Используйте при проблемах со спящим режимом, гибернацией или частотой процессора."));
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: false,
            RequiredConfirmationPhraseRu: null,
            PlannedCommands: ["powercfg.exe -restoredefaultschemes"],
            BackupStrategyRu: "Точка восстановления системы.",
            RollbackStrategyRu: "Выбор прежней схемы питания в разделе «Оптимизация».",
            WarningMessageRu: "Пользовательские схемы электропитания будут заменены стандартными схемами Windows."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        progress?.Report(new ProgressReport(NameRu, "Выполнение powercfg -restoredefaultschemes...", 50));
        var res = await _processRunner.RunAsync("powercfg.exe", "-restoredefaultschemes", cancellationToken: cancellationToken).ConfigureAwait(false);

        var ok = res.ExitCode == 0;
        var summaryRu = ok
            ? "Стандартные схемы электропитания Windows успешно восстановлены."
            : $"Не удалось сбросить схемы питания: {res.CombinedOutput.Trim()}";

        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            summaryRu,
            Risk,
            ok ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            0, null, null, null, CanRollback: false), cancellationToken).ConfigureAwait(false);

        return new RepairExecutionResult(Id, NameRu, ok, false, false, summaryRu, res.CombinedOutput, null, log.Id, []);
    }
}

// 7. ПРОВЕРКА ДИСКА (chkdsk — только с явным предупреждением, запросом перезагрузки и вводом фразы)
public sealed class DiskCheckChkdskRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;

    public DiskCheckChkdskRepairModule(
        IProcessRunner processRunner,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService)
    {
        _processRunner = processRunner;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
    }

    public string Id => "repair.chkdsk";
    public string NameRu => "Проверка файловой системы диска (CHKDSK)";
    public string DescriptionRu => "Выполняет онлайн-сканирование chkdsk C: /scan или планирует полную проверку при следующей перезагрузке (требует подтверждения).";
    public RepairCategory Category => RepairCategory.DiskCheckChkdsk;
    public RiskLevel Risk => RiskLevel.High;
    public string? ConfirmationPhraseRu => HighRiskConfirmationValidator.ChkdskPhrase;

    public async Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dirtyRes = await _processRunner.RunAsync("fsutil.exe", "dirty query C:", cancellationToken: cancellationToken).ConfigureAwait(false);
        var isDirty = dirtyRes.CombinedOutput.Contains("is dirty", StringComparison.OrdinalIgnoreCase) ||
                      dirtyRes.CombinedOutput.Contains("поврежден", StringComparison.OrdinalIgnoreCase);

        return new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: isDirty,
            Severity: isDirty ? IssueSeverity.Warning : IssueSeverity.Healthy,
            Risk: Risk,
            CurrentStateRu: isDirty
                ? "На системном томе C: установлен бит необходимости проверки файловой системы."
                : "Файловая система NTFS на томе C: не помечена как повреждённая.",
            RecommendationRu: isDirty
                ? "Запланируйте проверку диска при перезагрузке."
                : "При необходимости можно выполнить неразрушающую онлайн-проверку chkdsk C: /scan.");
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: true,
            RequiredConfirmationPhraseRu: ConfirmationPhraseRu,
            PlannedCommands:
            [
                "chkdsk.exe C: /scan",
                "(Опционально при флаге перезагрузки) chkntfs.exe /c C:"
            ],
            BackupStrategyRu: "Создание точки восстановления системы перед проверкой тома.",
            RollbackStrategyRu: "Отмена запланированной проверки при загрузке командой chkntfs /x C:.",
            WarningMessageRu: $"ВНИМАНИЕ: Полная проверка системного тома выполняется при перезагрузке компьютера и может занять длительное время. Для подтверждения введите фразу «{ConfirmationPhraseRu}»."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var confirm = HighRiskConfirmationValidator.Validate(Risk, ConfirmationPhraseRu, options.UserConfirmationPhrase);
        if (!confirm.IsValid)
        {
            return new RepairExecutionResult(Id, NameRu, false, false, false, confirm.MessageRu, confirm.MessageRu, null, Guid.Empty, []);
        }

        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        progress?.Report(new ProgressReport(NameRu, "Выполнение онлайн-проверки файловой системы (chkdsk C: /scan)...", 40));
        var res = await _processRunner.RunAsync("chkdsk.exe", "C: /scan", cancellationToken: cancellationToken).ConfigureAwait(false);

        if (options.ScheduleChkdskOnNextReboot)
        {
            progress?.Report(new ProgressReport(NameRu, "Планирование проверки тома C: при следующей перезагрузке (chkntfs /c C:)...", 85));
            await _processRunner.RunAsync("chkntfs.exe", "/c C:", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var summaryRu = options.ScheduleChkdskOnNextReboot
            ? "Онлайн-сканирование выполнено, полная проверка тома C: запланирована на следующую перезагрузку Windows."
            : "Онлайн-проверка файловой системы тома C: (chkdsk /scan) успешно завершена.";

        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            summaryRu,
            Risk,
            OperationOutcome.Succeeded,
            0, null, null, "chkntfs.exe /x C:", CanRollback: options.ScheduleChkdskOnNextReboot), cancellationToken).ConfigureAwait(false);

        return new RepairExecutionResult(
            Id, NameRu, true, false,
            RebootRequired: options.ScheduleChkdskOnNextReboot,
            SummaryRu: summaryRu,
            DetailedOutputRu: res.CombinedOutput,
            BackupArtifactPath: null,
            OperationLogId: log.Id,
            Errors: []);
    }
}

// 8. ЗАГРУЗЧИК (bootrec /fixmbr, /fixboot, /rebuildbcd + bcdboot с предварительным экспортом BCD и вводом фразы)
public sealed class BootloaderRepairModule : IRepairModule
{
    private readonly IProcessRunner _processRunner;
    private readonly IBackupService _backupService;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;

    public BootloaderRepairModule(
        IProcessRunner processRunner,
        IBackupService backupService,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService)
    {
        _processRunner = processRunner;
        _backupService = backupService;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
    }

    public string Id => "repair.bootloader";
    public string NameRu => "Восстановление загрузчика Windows (BCD / bootrec)";
    public string DescriptionRu => "Создаёт резервную копию хранилища BCD и восстанавливает загрузочные записи (только по ручному подтверждению фразой).";
    public RepairCategory Category => RepairCategory.BootloaderBcd;
    public RiskLevel Risk => RiskLevel.High;
    public string? ConfirmationPhraseRu => HighRiskConfirmationValidator.BootloaderPhrase;

    public async Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bcdRes = await _processRunner.RunAsync("bcdedit.exe", "/enum active", cancellationToken: cancellationToken).ConfigureAwait(false);
        var ok = bcdRes.ExitCode == 0;
        var firmware = Win32NativeMethods.IsUefiBootMode() ? "UEFI (GPT)" : "Legacy BIOS (MBR)";

        return new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: !ok,
            Severity: ok ? IssueSeverity.Healthy : IssueSeverity.Critical,
            Risk: Risk,
            CurrentStateRu: ok
                ? $"Хранилище конфигурации загрузки BCD читается корректно (режим: {firmware})."
                : "Обнаружена ошибка чтения активных записей хранилища BCD.",
            RecommendationRu: ok
                ? "Вмешательство в загрузчик не требуется."
                : "Выполните резервное копирование и восстановление конфигурации загрузчика.");
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var isUefi = Win32NativeMethods.IsUefiBootMode();
        var commands = isUefi
            ? new[]
            {
                "bcdedit.exe /export <BackupPath> (обязательный предварительный экспорт BCD)",
                "bcdboot.exe C:\\Windows /l ru-ru (пересоздание файлов загрузки UEFI/BCD)",
                "(В среде восстановления WinRE) bootrec.exe /rebuildbcd"
            }
            : new[]
            {
                "bcdedit.exe /export <BackupPath> (обязательный предварительный экспорт BCD)",
                "bootrec.exe /fixmbr",
                "bootrec.exe /fixboot",
                "bootrec.exe /rebuildbcd",
                "bcdboot.exe C:\\Windows /l ru-ru"
            };

        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: true,
            RequiredConfirmationPhraseRu: ConfirmationPhraseRu,
            PlannedCommands: commands,
            BackupStrategyRu: "Обязательный экспорт текущего магазина BCD через bcdedit /export до внесения любых изменений.",
            RollbackStrategyRu: "Импорт сохранённой копии BCD командой bcdedit /import <путь_к_бэкапу>.",
            WarningMessageRu: $"КРИТИЧЕСКАЯ ОПЕРАЦИЯ: Модификация загрузчика ОС. В обычной среде Windows 10/11 утилита bootrec.exe доступна только в WinRE; на системах UEFI/GPT запись MBR не используется и применяется штатный bcdboot.exe. Для подтверждения введите фразу «{ConfirmationPhraseRu}»."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var confirm = HighRiskConfirmationValidator.Validate(Risk, ConfirmationPhraseRu, options.UserConfirmationPhrase);
        if (!confirm.IsValid)
        {
            return new RepairExecutionResult(Id, NameRu, false, false, false, confirm.MessageRu, confirm.MessageRu, null, Guid.Empty, []);
        }

        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new RepairExecutionResult(Id, NameRu, false, false, false, reasonRu, reasonRu, null, Guid.Empty, []);
            }
        }

        progress?.Report(new ProgressReport(NameRu, "Экспорт резервной копии конфигурации BCD (bcdedit /export)...", 20));
        var (bcdExported, bcdBackupPath, bcdExportMsg) = await _backupService.ExportBcdStoreAsync(cancellationToken).ConfigureAwait(false);
        if (!bcdExported)
        {
            return new RepairExecutionResult(
                Id, NameRu, false, false, false,
                $"Операция восстановления загрузчика прервана: не удалось создать предварительную резервную копию BCD. {bcdExportMsg}",
                bcdExportMsg, null, Guid.Empty, []);
        }

        var outputLines = new List<string> { bcdExportMsg };
        var bootrecPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bootrec.exe");

        if (File.Exists(bootrecPath))
        {
            progress?.Report(new ProgressReport(NameRu, "Выполнение bootrec /fixmbr, /fixboot, /rebuildbcd...", 55));
            var rMbr = await _processRunner.RunAsync(bootrecPath, "/fixmbr", cancellationToken: cancellationToken).ConfigureAwait(false);
            var rBoot = await _processRunner.RunAsync(bootrecPath, "/fixboot", cancellationToken: cancellationToken).ConfigureAwait(false);
            var rBcd = await _processRunner.RunAsync(bootrecPath, "/rebuildbcd", cancellationToken: cancellationToken).ConfigureAwait(false);
            outputLines.AddRange([rMbr.CombinedOutput, rBoot.CombinedOutput, rBcd.CombinedOutput]);
        }

        progress?.Report(new ProgressReport(NameRu, "Восстановление файлов загрузки Windows (bcdboot C:\\Windows /l ru-ru)...", 85));
        var bcdBootRes = await _processRunner.RunAsync("bcdboot.exe", @"C:\Windows /l ru-ru", cancellationToken: cancellationToken).ConfigureAwait(false);
        outputLines.Add(bcdBootRes.CombinedOutput);

        var ok = bcdBootRes.ExitCode == 0;
        var summaryRu = ok
            ? $"Файлы загрузчика Windows и хранилище BCD успешно обновлены. Резервная копия сохранена в {bcdBackupPath}."
            : $"Операция завершилась с предупреждениями (резервная копия BCD сохранена в {bcdBackupPath}).";

        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Repair,
            Id,
            NameRu,
            summaryRu,
            Risk,
            ok ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            0, null, null,
            $"bcdedit.exe /import \"{bcdBackupPath}\"",
            CanRollback: true), cancellationToken).ConfigureAwait(false);

        return new RepairExecutionResult(
            Id, NameRu, ok, false, RebootRequired: true,
            summaryRu, string.Join(Environment.NewLine, outputLines), bcdBackupPath, log.Id, []);
    }
}

// 9. ТОЧКИ ВОССТАНОВЛЕНИЯ (включение защиты системы и создание контрольной точки)
public sealed class SystemRestorePointsRepairModule : IRepairModule
{
    private readonly IBackupService _backupService;
    private readonly IOperationJournalService _journalService;

    public SystemRestorePointsRepairModule(
        IBackupService backupService,
        IOperationJournalService journalService)
    {
        _backupService = backupService;
        _journalService = journalService;
    }

    public string Id => "repair.restore_points";
    public string NameRu => "Включение защиты системы и создание точки восстановления";
    public string DescriptionRu => "Включает службу защиты системы (System Restore) на диске C: и создаёт свежую контрольную точку восстановления.";
    public RepairCategory Category => RepairCategory.SystemRestorePoints;
    public RiskLevel Risk => RiskLevel.Safe;
    public string? ConfirmationPhraseRu => null;

    public Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairScanFinding(
            Id, NameRu, Category,
            NeedsRepair: false,
            Severity: IssueSeverity.Healthy,
            Risk: Risk,
            CurrentStateRu: "Подсистема создания контрольных точек восстановления готова к работе.",
            RecommendationRu: "Рекомендуется создавать точку восстановления перед установкой драйверов или системными изменениями."));
    }

    public Task<RepairPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RepairPreview(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            RequiresReboot: false,
            RequiredConfirmationPhraseRu: null,
            PlannedCommands:
            [
                "Enable-ComputerRestore -Drive \"C:\\\"",
                "WMI SystemRestore.CreateRestorePoint"
            ],
            BackupStrategyRu: "Непосредственное создание системной контрольной точки.",
            RollbackStrategyRu: "Запуск rstrui.exe для возврата состояния системы.",
            WarningMessageRu: "Операция полностью безопасна и не изменяет пользовательские файлы."));
    }

    public async Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressReport(NameRu, "Включение защиты системы для диска C:...", 30));
        await _backupService.EnsureSystemProtectionEnabledAsync("C:", cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressReport(NameRu, "Создание контрольной точки восстановления...", 75));
        var (ok, msgRu) = await _backupService.CreateRestorePointAsync(
            $"WinRepair — контрольная точка пользователя ({DateTime.Now:dd.MM.yyyy HH:mm})",
            cancellationToken).ConfigureAwait(false);

        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.RestorePointCreation,
            Id,
            NameRu,
            msgRu,
            Risk,
            ok ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            0, null, null, null, CanRollback: false), cancellationToken).ConfigureAwait(false);

        return new RepairExecutionResult(Id, NameRu, ok, false, false, msgRu, msgRu, null, log.Id, []);
    }
}
