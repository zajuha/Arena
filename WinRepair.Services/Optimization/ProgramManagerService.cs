using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.Services.Optimization;

/// <summary>
/// Сервис управления установленными программами и безопасного поиска остатков удалённых программ (п. 3.5 ТЗ).
/// </summary>
public sealed class ProgramManagerService : IProgramManagerService
{
    private readonly IBackupService _backupService;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;
    private readonly ILogger<ProgramManagerService> _logger;

    public ProgramManagerService(
        IBackupService backupService,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService,
        ILogger<ProgramManagerService> logger)
    {
        _backupService = backupService;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
        _logger = logger;
    }

    public Task<IReadOnlyList<InstalledProgramEntry>> GetInstalledProgramsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<InstalledProgramEntry>>(() =>
        {
            var results = new List<InstalledProgramEntry>();
            if (!OperatingSystem.IsWindows())
            {
                return results;
            }

            ReadUninstallHive(
                Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                "HKLM",
                is64Bit: true,
                results,
                cancellationToken);

            ReadUninstallHive(
                Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                "HKLM",
                is64Bit: false,
                results,
                cancellationToken);

            ReadUninstallHive(
                Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                "HKCU",
                is64Bit: true,
                results,
                cancellationToken);

            return results
                .GroupBy(p => $"{p.DisplayName}|{p.DisplayVersion}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public async Task<(bool Succeeded, string MessageRu)> LaunchStandardUninstallerAsync(
        InstalledProgramEntry program,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(program.UninstallString))
        {
            return (false, $"Для программы «{program.DisplayName}» не указана команда штатного деинсталлятора.");
        }

        var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(
            $"Деинсталляция программы «{program.DisplayName}»",
            cancellationToken).ConfigureAwait(false);

        if (!allowed)
        {
            return (false, reasonRu);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {program.UninstallString}",
                UseShellExecute = true
            };

            Process.Start(psi);

            await _journalService.RecordAsync(new OperationLog(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                OperationType.ProgramUninstallOrRemnantCleanup,
                "programs.uninstall",
                $"Запуск деинсталлятора: {program.DisplayName}",
                $"Запущен штатный деинсталлятор программы «{program.DisplayName}» ({program.DisplayVersion}).",
                RiskLevel.Low,
                OperationOutcome.Succeeded,
                0, null, null, null, CanRollback: false), cancellationToken).ConfigureAwait(false);

            return (true, $"Штатный деинсталлятор программы «{program.DisplayName}» успешно запущен.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить деинсталлятор программы {Program}", program.DisplayName);
            return (false, $"Ошибка запуска деинсталлятора: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<ProgramRemnantEntry>> ScanForUninstalledProgramRemnantsAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ProgramRemnantEntry>>(() =>
        {
            var remnants = new List<ProgramRemnantEntry>();
            if (!OperatingSystem.IsWindows())
            {
                return remnants;
            }

            progress?.Report(new ProgressReport("Поиск остатков программ", "Анализ пустых папок в Program Files...", 30));

            // 1. Поиск полностью пустых осиротевших папок верхнего уровня в Program Files и Program Files (x86)
            var pfDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            var protectedVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Common Files", "Internet Explorer", "Microsoft", "ModifiableWindowsApps",
                "MSBuild", "Reference Assemblies", "Windows Defender", "Windows Defender Advanced Threat Protection",
                "Windows Mail", "Windows Media Player", "Windows Multimedia Platform", "Windows NT",
                "Windows Photo Viewer", "Windows Portable Devices", "WindowsPowerShell", "Windows Security",
                "WinRepair"
            };

            foreach (var pfRoot in pfDirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var subDir in Directory.EnumerateDirectories(pfRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var folderName = Path.GetFileName(subDir);
                    if (protectedVendors.Contains(folderName))
                    {
                        continue;
                    }

                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(subDir, "*", SearchOption.AllDirectories).Any())
                        {
                            remnants.Add(new ProgramRemnantEntry(
                                Id: Guid.NewGuid().ToString("N"),
                                FormerApplicationName: folderName,
                                Kind: RemnantKind.FileSystemFolder,
                                KindDisplayRu: "Пустая папка в Program Files",
                                PathOrKey: subDir,
                                SizeBytes: 0,
                                Risk: RiskLevel.Low,
                                EvidenceReasonRu: "Папка в Program Files полностью пуста и не содержит исполняемых файлов или библиотек."));
                        }
                    }
                    catch
                    {
                        // Пропускаем защищённые каталоги
                    }
                }
            }

            progress?.Report(new ProgressReport("Поиск остатков программ", "Проверка недействительных записей Uninstall в реестре...", 80));

            // 2. Поиск битых записей в ветке Uninstall, где ни InstallLocation, ни исполняемый файл деинсталлятора больше не существуют
            FindOrphanedUninstallKeys(
                Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                "HKLM",
                remnants,
                cancellationToken);

            progress?.Report(new ProgressReport("Поиск остатков программ", $"Проверка завершена. Найдено элементов: {remnants.Count}", 100));
            return remnants;
        }, cancellationToken);
    }

    public async Task<(bool Succeeded, int RemovedCount, string MessageRu)> RemoveSelectedRemnantsAsync(
        IReadOnlyList<ProgramRemnantEntry> selectedRemnants,
        bool moveToQuarantine = true,
        CancellationToken cancellationToken = default)
    {
        var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync("Удаление остатков удалённых программ", cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return (false, 0, reasonRu);
        }

        var removed = 0;
        string? lastRegBackup = null;

        foreach (var item in selectedRemnants.Where(r => r.IsSelected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (item.Kind == RemnantKind.FileSystemFolder)
                {
                    if (Directory.Exists(item.PathOrKey) &&
                        !Directory.EnumerateFileSystemEntries(item.PathOrKey, "*", SearchOption.AllDirectories).Any())
                    {
                        Directory.Delete(item.PathOrKey, recursive: false);
                        removed++;
                    }
                }
                else if (item.Kind == RemnantKind.RegistryKey && OperatingSystem.IsWindows())
                {
                    // Обязательный .reg бэкап перед удалением ключа реестра (п. 4.7 ТЗ)
                    var (exported, backupFile, _) = await _backupService.ExportRegistryKeyAsync(
                        item.PathOrKey,
                        $"remnant_{item.FormerApplicationName}",
                        cancellationToken).ConfigureAwait(false);

                    if (exported)
                    {
                        lastRegBackup = backupFile;
                        DeleteRegistryKeySafe(item.PathOrKey);
                        removed++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить остаток программы {Path}", item.PathOrKey);
            }
        }

        var summaryRu = $"Удалено остатков удалённых программ: {removed} из {selectedRemnants.Count}.";
        await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.ProgramUninstallOrRemnantCleanup,
            "programs.remnants",
            "Удаление остатков программ",
            summaryRu,
            RiskLevel.Low,
            removed > 0 ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            0, null, lastRegBackup, null, CanRollback: lastRegBackup is not null), cancellationToken).ConfigureAwait(false);

        return (removed > 0 || selectedRemnants.Count == 0, removed, summaryRu);
    }

    private static void ReadUninstallHive(
        RegistryKey rootHive,
        string uninstallSubKeyPath,
        string rootPrefix,
        bool is64Bit,
        List<InstalledProgramEntry> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            using var uninstallKey = rootHive.OpenSubKey(uninstallSubKeyPath, writable: false);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                if (appKey is null)
                {
                    continue;
                }

                var displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var sysComponent = Convert.ToInt32(appKey.GetValue("SystemComponent", 0));
                if (sysComponent == 1)
                {
                    continue;
                }

                var publisher = appKey.GetValue("Publisher")?.ToString()?.Trim() ?? "Не указан";
                var version = appKey.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "—";
                var installLocation = appKey.GetValue("InstallLocation")?.ToString()?.Trim();
                var uninstallString = appKey.GetValue("UninstallString")?.ToString()?.Trim();
                var quietUninstall = appKey.GetValue("QuietUninstallString")?.ToString()?.Trim();

                // EstimatedSize в реестре Windows хранится в килобайтах (DWORD)
                long sizeBytes = 0;
                if (appKey.GetValue("EstimatedSize") is int sizeKb && sizeKb > 0)
                {
                    sizeBytes = (long)sizeKb * 1024L;
                }

                DateTimeOffset? installDate = null;
                var rawDate = appKey.GetValue("InstallDate")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(rawDate) &&
                    DateTime.TryParseExact(rawDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                {
                    installDate = new DateTimeOffset(dt);
                }

                destination.Add(new InstalledProgramEntry(
                    Id: $"{rootPrefix}_{subKeyName}",
                    DisplayName: displayName,
                    Publisher: publisher,
                    DisplayVersion: version,
                    InstallDate: installDate,
                    EstimatedSizeBytes: sizeBytes,
                    InstallLocation: installLocation,
                    UninstallString: uninstallString,
                    QuietUninstallString: quietUninstall,
                    RegistryKeyPath: $@"{rootPrefix}\{uninstallSubKeyPath}\{subKeyName}",
                    Is64Bit: is64Bit));
            }
        }
        catch
        {
            // Игнорируем недоступные ветки
        }
    }

    private static void FindOrphanedUninstallKeys(
        RegistryKey rootHive,
        string uninstallSubKeyPath,
        string rootPrefix,
        List<ProgramRemnantEntry> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            using var uninstallKey = rootHive.OpenSubKey(uninstallSubKeyPath, writable: false);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                if (appKey is null)
                {
                    continue;
                }

                var displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                var installLocation = appKey.GetValue("InstallLocation")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation))
                {
                    continue;
                }

                // Проверяем только те записи, у которых явно указан каталог установки, но самого каталога на диске уже нет
                if (installLocation.Length > 3 && !Directory.Exists(installLocation))
                {
                    destination.Add(new ProgramRemnantEntry(
                        Id: Guid.NewGuid().ToString("N"),
                        FormerApplicationName: displayName,
                        Kind: RemnantKind.RegistryKey,
                        KindDisplayRu: "Устаревшая запись Uninstall в реестре",
                        PathOrKey: $@"{rootPrefix}\{uninstallSubKeyPath}\{subKeyName}",
                        SizeBytes: 0,
                        Risk: RiskLevel.Low,
                        EvidenceReasonRu: $"Каталог установки «{installLocation}» больше не существует на диске."));
                }
            }
        }
        catch
        {
            // Игнорируем ошибку
        }
    }

    private static void DeleteRegistryKeySafe(string fullKeyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (fullKeyPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
        {
            var sub = fullKeyPath[5..];
            Registry.LocalMachine.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
        else if (fullKeyPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
        {
            var sub = fullKeyPath[5..];
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }
}
