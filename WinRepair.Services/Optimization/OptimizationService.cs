using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;
using WinRepair.Services.Infrastructure;
using ServiceStartMode = WinRepair.Core.Models.ServiceStartMode;

namespace WinRepair.Services.Optimization;

/// <summary>
/// Сервис оптимизации автозагрузки, служб, планировщика заданий, схем питания,
/// визуальных эффектов и рабочего набора памяти процессов (п. 3.4 ТЗ).
/// </summary>
public sealed class OptimizationService : IOptimizationService
{
    private readonly IProcessRunner _processRunner;
    private readonly IBackupService _backupService;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;
    private readonly ILogger<OptimizationService> _logger;

    public OptimizationService(
        IProcessRunner processRunner,
        IBackupService backupService,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService,
        ILogger<OptimizationService> logger)
    {
        _processRunner = processRunner;
        _backupService = backupService;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
        _logger = logger;
    }

    public Task<IReadOnlyList<StartupEntry>> GetStartupEntriesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<StartupEntry>>(() =>
        {
            var entries = new List<StartupEntry>();
            if (!OperatingSystem.IsWindows())
            {
                return entries;
            }

            // 1. HKCU Run
            ReadRegistryRunEntries(
                Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                StartupSourceType.RegistryCurrentUserRun,
                "Реестр текущего пользователя (HKCU\\...\\Run)",
                entries);

            // 2. HKLM Run
            ReadRegistryRunEntries(
                Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                StartupSourceType.RegistryLocalMachineRun,
                "Общий реестр системы (HKLM\\...\\Run)",
                entries);

            // 3. HKCU RunOnce
            ReadRegistryRunEntries(
                Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
                null,
                StartupSourceType.RegistryRunOnce,
                "Однократный запуск (HKCU\\...\\RunOnce)",
                entries);

            // 4. Папка «Автозагрузка» пользователя
            var userStartupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            ReadStartupFolderEntries(userStartupDir, StartupSourceType.StartupFolderUser, "Папка «Автозагрузка» пользователя", entries);

            // 5. Общая папка «Автозагрузка»
            var commonStartupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
            ReadStartupFolderEntries(commonStartupDir, StartupSourceType.StartupFolderCommon, "Общая папка «Автозагрузка»", entries);

            return entries;
        }, cancellationToken);
    }

    public async Task<(bool Succeeded, string MessageRu)> SetStartupEntryEnabledAsync(
        StartupEntry entry,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync($"Изменение автозагрузки «{entry.Name}»", cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return (false, reasonRu);
        }

        if (!OperatingSystem.IsWindows())
        {
            return (false, "Операция доступна только в среде Windows.");
        }

        try
        {
            var isMachine = entry.Source == StartupSourceType.RegistryLocalMachineRun;
            var rootName = isMachine ? "HKLM" : "HKCU";
            var approvedSubKey = isMachine
                ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
                : @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

            var (_, backupPath, _) = await _backupService.ExportRegistryKeyAsync(
                $@"{rootName}\{approvedSubKey}",
                $"startup_{entry.Name}",
                cancellationToken).ConfigureAwait(false);

            var hive = isMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = hive.CreateSubKey(approvedSubKey, writable: true);
            if (key is null)
            {
                return (false, "Не удалось открыть ветку StartupApproved в реестре Windows.");
            }

            // Формат Windows StartupApproved: 12 байт; 0x02 = включено, 0x03 = отключено
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            key.SetValue(entry.Name, data, RegistryValueKind.Binary);

            var actionRu = enabled ? "включён" : "отключён";
            var summaryRu = $"Элемент автозагрузки «{entry.Name}» успешно {actionRu}.";

            await _journalService.RecordAsync(new OperationLog(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                OperationType.Optimization,
                "opt.startup",
                $"Автозагрузка: {entry.Name}",
                summaryRu,
                RiskLevel.Low,
                OperationOutcome.Succeeded,
                0, null, backupPath, null, CanRollback: true), cancellationToken).ConfigureAwait(false);

            return (true, summaryRu);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка изменения состояния автозагрузки {Name}", entry.Name);
            return (false, $"Не удалось изменить состояние элемента автозагрузки: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<ServiceOptimizationEntry>> GetOptimizableServicesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ServiceOptimizationEntry>>(() =>
        {
            // Каталог проверенных некритичных служб, которые безопасно переводить в ручной режим запуска
            var catalog = new (string Name, string TitleRu, string DescRu, ServiceStartMode Recommended, string ReasonRu)[]
            {
                ("DiagTrack", "Функциональные возможности для подключённых пользователей и телеметрия",
                    "Служба сбора диагностических данных использования системы.",
                    ServiceStartMode.Manual, "Безопасно перевести в ручной режим для снижения фоновой нагрузки."),
                ("Fax", "Факс (Fax)",
                    "Служба отправки и приёма факсимильных сообщений.",
                    ServiceStartMode.Manual, "На современных ПК не используется; рекомендуется ручной запуск."),
                ("MapsBroker", "Диспетчер скачанных карт (MapsBroker)",
                    "Фоновая служба обслуживания автономных карт Bing.",
                    ServiceStartMode.Manual, "Рекомендуется запуск по требованию, если вы не используете встроенные Карты."),
                ("RetailDemo", "Служба розничной демонстрации (RetailDemo)",
                    "Демонстрационный режим для витрин магазинов.",
                    ServiceStartMode.Disabled, "Не нужна домашним и офисным пользователям."),
                ("WSearch", "Поиск Windows (Windows Search)",
                    "Индексирование файлов для мгновенного поиска в проводнике и меню «Пуск».",
                    ServiceStartMode.AutomaticDelayed, "Рекомендуется отложенный автозапуск для ускорения старта системы."),
                ("SysMain", "SysMain (Superfetch)",
                    "Предварительная подгрузка часто используемых приложений в ОЗУ.",
                    ServiceStartMode.Automatic, "На ПК с SSD можно перевести в ручной режим при нехватке ОЗУ."),
                ("Spooler", "Диспетчер печати (Print Spooler)",
                    "Очередь заданий для локальных и сетевых принтеров.",
                    ServiceStartMode.Manual, "Если принтер не подключён, можно перевести в режим «Вручную».")
            };

            var results = new List<ServiceOptimizationEntry>();

            foreach (var item in catalog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentStatusRu = "Остановлена";
                var currentMode = ServiceStartMode.Manual;

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        using var sc = new ServiceController(item.Name);
                        currentStatusRu = sc.Status == ServiceControllerStatus.Running ? "Работает" : "Остановлена";
                        currentMode = sc.StartType switch
                        {
                            System.ServiceProcess.ServiceStartMode.Automatic => ServiceStartMode.Automatic,
                            System.ServiceProcess.ServiceStartMode.Manual => ServiceStartMode.Manual,
                            System.ServiceProcess.ServiceStartMode.Disabled => ServiceStartMode.Disabled,
                            _ => ServiceStartMode.Manual
                        };
                    }
                    catch
                    {
                        continue;
                    }
                }

                results.Add(new ServiceOptimizationEntry(
                    ServiceName: item.Name,
                    DisplayNameRu: item.TitleRu,
                    DescriptionRu: item.DescRu,
                    CurrentStatusRu: currentStatusRu,
                    CurrentStartMode: currentMode,
                    RecommendedStartMode: item.Recommended,
                    RecommendationReasonRu: item.ReasonRu,
                    IsSafeToChange: true,
                    Risk: RiskLevel.Low));
            }

            return results;
        }, cancellationToken);
    }

    public async Task<(bool Succeeded, string MessageRu)> ApplyServiceStartModeAsync(
        ServiceOptimizationEntry service,
        ServiceStartMode targetMode,
        CancellationToken cancellationToken = default)
    {
        var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync($"Настройка службы «{service.DisplayNameRu}»", cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return (false, reasonRu);
        }

        // Бэкап ветки службы в реестре перед изменением (п. 4.7 ТЗ)
        var regKey = $@"HKLM\SYSTEM\CurrentControlSet\Services\{service.ServiceName}";
        var (_, regBackupPath, _) = await _backupService.ExportRegistryKeyAsync(regKey, $"service_{service.ServiceName}", cancellationToken).ConfigureAwait(false);

        var scTarget = ToScStartFlag(targetMode);
        var scOld = ToScStartFlag(service.CurrentStartMode);

        var res = await _processRunner.RunAsync(
            "sc.exe",
            $"config \"{service.ServiceName}\" start= {scTarget}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var ok = res.ExitCode == 0;
        var summaryRu = ok
            ? $"Режим запуска службы «{service.DisplayNameRu}» ({service.ServiceName}) изменён на «{targetMode.ToRussianName()}»."
            : $"Не удалось изменить режим запуска службы {service.ServiceName}: {res.CombinedOutput.Trim()}";

        if (ok)
        {
            await _journalService.RecordAsync(new OperationLog(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                OperationType.Optimization,
                "opt.services",
                $"Служба: {service.DisplayNameRu}",
                summaryRu,
                service.Risk,
                OperationOutcome.Succeeded,
                0,
                null,
                regBackupPath,
                $"sc.exe config \"{service.ServiceName}\" start= {scOld}",
                CanRollback: true), cancellationToken).ConfigureAwait(false);
        }

        return (ok, summaryRu);
    }

    public async Task<IReadOnlyList<ScheduledTaskEntry>> GetNonCriticalScheduledTasksAsync(
        CancellationToken cancellationToken = default)
    {
        var list = new List<ScheduledTaskEntry>();
        var res = await _processRunner.RunAsync(
            "schtasks.exe",
            "/Query /FO CSV /NH",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            return list;
        }

        foreach (var rawLine in res.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cols = rawLine.Split("\",\"");
            if (cols.Length < 3)
            {
                continue;
            }

            var taskFullName = cols[0].Trim('"');
            var nextRun = cols[1].Trim('"');
            var status = cols[2].Trim('"');

            // Исключаем ядерные системные задачи Windows, оставляя задачи обновления стороннего ПО и телеметрии
            var isCore = taskFullName.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) &&
                         !taskFullName.Contains("Customer Experience", StringComparison.OrdinalIgnoreCase) &&
                         !taskFullName.Contains("Application Experience", StringComparison.OrdinalIgnoreCase);

            if (isCore)
            {
                continue;
            }

            var shortName = Path.GetFileName(taskFullName);
            var enabled = !status.Contains("Disabled", StringComparison.OrdinalIgnoreCase) &&
                          !status.Contains("Отключено", StringComparison.OrdinalIgnoreCase);

            list.Add(new ScheduledTaskEntry(
                TaskPath: taskFullName,
                TaskName: string.IsNullOrWhiteSpace(shortName) ? taskFullName : shortName,
                Author: "Планировщик заданий",
                DescriptionRu: $"Запланированная задача: {taskFullName}",
                StateRu: enabled ? "Включена" : "Отключена",
                IsEnabled: enabled,
                IsMicrosoftCoreTask: false,
                NextRunTimeRu: nextRun));
        }

        return list.Take(50).ToList();
    }

    public async Task<(bool Succeeded, string MessageRu)> SetScheduledTaskEnabledAsync(
        ScheduledTaskEntry task,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync($"Переключение задачи «{task.TaskName}»", cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return (false, reasonRu);
        }

        var flag = enabled ? "/ENABLE" : "/DISABLE";
        var reverseFlag = enabled ? "/DISABLE" : "/ENABLE";

        var res = await _processRunner.RunAsync(
            "schtasks.exe",
            $"/Change /TN \"{task.TaskPath}\" {flag}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var ok = res.ExitCode == 0;
        var summaryRu = ok
            ? $"Запланированная задача «{task.TaskName}» успешно {(enabled ? "включена" : "отключена")}."
            : $"Не удалось изменить состояние задачи «{task.TaskName}»: {res.CombinedOutput.Trim()}";

        if (ok)
        {
            await _journalService.RecordAsync(new OperationLog(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                OperationType.Optimization,
                "opt.tasks",
                $"Задача: {task.TaskName}",
                summaryRu,
                RiskLevel.Low,
                OperationOutcome.Succeeded,
                0, null, null,
                $"schtasks.exe /Change /TN \"{task.TaskPath}\" {reverseFlag}",
                CanRollback: true), cancellationToken).ConfigureAwait(false);
        }

        return (ok, summaryRu);
    }

    public async Task<IReadOnlyList<PowerPlanEntry>> GetPowerPlansAsync(CancellationToken cancellationToken = default)
    {
        var activeRes = await _processRunner.RunAsync("powercfg.exe", "/getactivescheme", cancellationToken: cancellationToken).ConfigureAwait(false);
        var activeOutput = activeRes.CombinedOutput;

        return
        [
            new PowerPlanEntry(
                "381b4222-f694-41f0-9685-ff5bb260df2e",
                "Сбалансированная (рекомендуется)",
                "Автоматический баланс между производительностью процессора и энергопотреблением.",
                activeOutput.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase)),
            new PowerPlanEntry(
                "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                "Высокая производительность",
                "Поддерживает повышенные частоты процессора и отключает глубокое энергосбережение шин.",
                activeOutput.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase)),
            new PowerPlanEntry(
                "a1841308-3541-4fab-bc81-f71556f20b4a",
                "Экономия энергии",
                "Снижает энергопотребление для максимального времени автономной работы ноутбука.",
                activeOutput.Contains("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase))
        ];
    }

    public async Task<(bool Succeeded, string MessageRu)> ActivatePowerPlanAsync(
        string schemeGuid,
        CancellationToken cancellationToken = default)
    {
        var res = await _processRunner.RunAsync("powercfg.exe", $"/setactive {schemeGuid}", cancellationToken: cancellationToken).ConfigureAwait(false);
        var ok = res.ExitCode == 0;
        var summaryRu = ok
            ? $"Активная схема электропитания успешно переключена ({schemeGuid})."
            : $"Ошибка переключения схемы электропитания: {res.CombinedOutput.Trim()}";

        if (ok)
        {
            await _journalService.RecordAsync(new OperationLog(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                OperationType.Optimization,
                "opt.power",
                "Схема электропитания",
                summaryRu,
                RiskLevel.Safe,
                OperationOutcome.Succeeded,
                0, null, null,
                "powercfg.exe /setactive 381b4222-f694-41f0-9685-ff5bb260df2e",
                CanRollback: true), cancellationToken).ConfigureAwait(false);
        }

        return (ok, summaryRu);
    }

    public async Task<(bool Succeeded, string MessageRu)> ApplyVisualEffectsProfileAsync(
        bool optimizeForPerformance,
        CancellationToken cancellationToken = default)
    {
        var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync("Изменение визуальных эффектов Windows", cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return (false, reasonRu);
        }

        const string visualFxRegPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
        var (_, backupPath, _) = await _backupService.ExportRegistryKeyAsync(visualFxRegPath, "visual_effects", cancellationToken).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", writable: true);
                // 0 = По умолчанию Windows, 1 = Наилучший вид, 2 = Наилучшее быстродействие
                key?.SetValue("VisualFXSetting", optimizeForPerformance ? 2 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                return (false, $"Не удалось изменить настройки визуальных эффектов: {ex.Message}");
            }
        }

        var modeRu = optimizeForPerformance
            ? "«Обеспечить наилучшее быстродействие»"
            : "«Восстановить значения по умолчанию Windows»";

        var summaryRu = $"Профиль визуальных эффектов переключён в режим {modeRu}.";

        await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Optimization,
            "opt.visual_fx",
            "Визуальные эффекты проводника",
            summaryRu,
            RiskLevel.Safe,
            OperationOutcome.Succeeded,
            0, null, backupPath, null, CanRollback: true), cancellationToken).ConfigureAwait(false);

        return (true, summaryRu);
    }

    public Task<MemoryTrimSummary> TrimProcessesWorkingSetAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var trimmed = 0;
            var skipped = 0;
            long beforeBytes = 0;
            long afterBytes = 0;

            if (OperatingSystem.IsWindows())
            {
                var processes = Process.GetProcesses();
                foreach (var proc in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (proc)
                    {
                        try
                        {
                            if (proc.Id <= 4)
                            {
                                skipped++;
                                continue;
                            }

                            beforeBytes += proc.WorkingSet64;
                            if (Win32NativeMethods.EmptyWorkingSet(proc.Handle))
                            {
                                trimmed++;
                            }
                            else
                            {
                                skipped++;
                            }

                            proc.Refresh();
                            afterBytes += proc.WorkingSet64;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }
            }

            var reclaimed = Math.Max(0L, beforeBytes - afterBytes);
            return new MemoryTrimSummary(
                TrimmedProcessesCount: trimmed,
                SkippedSystemProcessesCount: skipped,
                WorkingSetBeforeBytes: beforeBytes,
                WorkingSetAfterBytes: afterBytes,
                ReclaimedBytes: reclaimed,
                HonestTechnicalNoteRu:
                    "Внимание: Выполнена штатная функция Win32 API EmptyWorkingSet (выгрузка неактивных страниц из рабочего набора процессов в список ожидания ОЗУ). " +
                    "Это полезно перед запуском ресурсоёмкого приложения, но не заменяет физическое увеличение объёма оперативной памяти.");
        }, cancellationToken);
    }

    private static void ReadRegistryRunEntries(
        RegistryKey rootHive,
        string runSubKey,
        string? approvedSubKey,
        StartupSourceType sourceType,
        string locationDisplayRu,
        List<StartupEntry> destination)
    {
        try
        {
            using var runKey = rootHive.OpenSubKey(runSubKey, writable: false);
            if (runKey is null)
            {
                return;
            }

            using var approvedKey = approvedSubKey is not null ? rootHive.OpenSubKey(approvedSubKey, writable: false) : null;

            foreach (var valueName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName))
                {
                    continue;
                }

                var cmd = runKey.GetValue(valueName)?.ToString() ?? string.Empty;
                var isEnabled = true;

                if (approvedKey?.GetValue(valueName) is byte[] statusBytes && statusBytes.Length > 0)
                {
                    // Нечётный первый байт (например 0x03) означает отключённый автозапуск
                    isEnabled = (statusBytes[0] & 1) == 0;
                }

                var impact = EstimateStartupImpact(cmd);
                destination.Add(new StartupEntry(
                    Id: $"{sourceType}_{valueName}",
                    Name: valueName,
                    CommandOrTarget: cmd,
                    PublisherRu: "Установленное приложение",
                    Source: sourceType,
                    SourceLocationDisplayRu: locationDisplayRu,
                    IsEnabled: isEnabled,
                    Impact: impact,
                    ImpactDisplayRu: impact switch
                    {
                        StartupImpactLevel.High => "Высокое влияние",
                        StartupImpactLevel.Medium => "Среднее влияние",
                        _ => "Низкое влияние"
                    }));
            }
        }
        catch
        {
            // Пропускаем недоступный раздел реестра
        }
    }

    private static void ReadStartupFolderEntries(
        string folderPath,
        StartupSourceType sourceType,
        string locationDisplayRu,
        List<StartupEntry> destination)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                destination.Add(new StartupEntry(
                    Id: $"{sourceType}_{fileName}",
                    Name: Path.GetFileNameWithoutExtension(fileName),
                    CommandOrTarget: file,
                    PublisherRu: "Ярлык автозагрузки",
                    Source: sourceType,
                    SourceLocationDisplayRu: locationDisplayRu,
                    IsEnabled: !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    Impact: StartupImpactLevel.Medium,
                    ImpactDisplayRu: "Среднее влияние"));
            }
        }
        catch
        {
            // Игнорируем ошибку обхода каталога
        }
    }

    private static StartupImpactLevel EstimateStartupImpact(string commandLine)
    {
        if (commandLine.Contains("electron", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("discord", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("teams", StringComparison.OrdinalIgnoreCase))
        {
            return StartupImpactLevel.High;
        }

        if (commandLine.Contains("update", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("helper", StringComparison.OrdinalIgnoreCase))
        {
            return StartupImpactLevel.Low;
        }

        return StartupImpactLevel.Medium;
    }

    private static string ToScStartFlag(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => "auto",
        ServiceStartMode.AutomaticDelayed => "delayed-auto",
        ServiceStartMode.Manual => "demand",
        ServiceStartMode.Disabled => "disabled",
        _ => "demand"
    };
}
