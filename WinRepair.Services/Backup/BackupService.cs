using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WinRepair.Core.Abstractions;
using WinRepair.Services.Infrastructure;

namespace WinRepair.Services.Backup;

/// <summary>
/// Сервис резервного копирования: создание точек восстановления Windows (WMI SystemRestore),
/// экспорт/импорт ключей реестра (.reg) и резервное копирование хранилища загрузчика (BCD).
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<BackupService> _logger;
    private readonly string _backupRootDirectory;

    public BackupService(IProcessRunner processRunner, ILogger<BackupService> logger, string? customBackupRoot = null)
    {
        _processRunner = processRunner;
        _logger = logger;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\ProgramData";
        }

        _backupRootDirectory = customBackupRoot ?? Path.Combine(programData, "WinRepair", "Backups");
        Directory.CreateDirectory(_backupRootDirectory);
    }

    public async Task<(bool Succeeded, string MessageRu)> EnsureSystemProtectionEnabledAsync(
        string systemDrive = "C:",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedDrive = systemDrive.EndsWith('\\') ? systemDrive : systemDrive + @"\";
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Enable-ComputerRestore -Drive '{normalizedDrive}'\"";
            var res = await _processRunner.RunAsync("powershell.exe", args, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (res.ExitCode == 0)
            {
                _logger.LogInformation("Защита системы (System Restore) успешно включена для диска {Drive}", normalizedDrive);
                return (true, $"Защита системы включена для тома {normalizedDrive}.");
            }

            _logger.LogWarning("Не удалось включить защиту системы через PowerShell: {Error}", res.CombinedOutput);
            return (false, $"Не удалось включить защиту системы для {normalizedDrive}: {res.CombinedOutput.Trim()}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка включения защиты системы");
            return (false, $"Ошибка включения защиты системы: {ex.Message}");
        }
    }

    public async Task<(bool Succeeded, string DescriptionRu)> CreateRestorePointAsync(
        string descriptionRu,
        CancellationToken cancellationToken = default)
    {
        var safeDescription = string.IsNullOrWhiteSpace(descriptionRu)
            ? $"WinRepair — точка восстановления ({DateTime.Now:dd.MM.yyyy HH:mm})"
            : descriptionRu;

        _logger.LogInformation("Создание точки восстановления Windows: {Description}", safeDescription);

        // По умолчанию Windows 10/11 ограничивает создание точек 1 разом в 24 часа.
        // Временно выставляем SystemRestorePointCreationFrequency = 0, чтобы точка гарантированно создалась.
        int? previousFrequency = null;
        const string srRegSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var srKey = Registry.LocalMachine.OpenSubKey(srRegSubKey, writable: true);
                if (srKey is not null)
                {
                    var existing = srKey.GetValue("SystemRestorePointCreationFrequency");
                    if (existing is int freq)
                    {
                        previousFrequency = freq;
                    }

                    srKey.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                }
            }

            // Попытка 1: через WMI класс root\default:SystemRestore
            if (OperatingSystem.IsWindows())
            {
                var wmiCreated = await Task.Run(() =>
                {
                    try
                    {
                        var scope = new ManagementScope(@"\\localhost\root\default");
                        scope.Connect();
                        using var srClass = new ManagementClass(scope, new ManagementPath("SystemRestore"), new ObjectGetOptions());
                        using var inParams = srClass.GetMethodParameters("CreateRestorePoint");
                        inParams["Description"] = safeDescription;
                        inParams["RestorePointType"] = 12u; // MODIFY_SETTINGS
                        inParams["EventType"] = 100u;       // BEGIN_SYSTEM_CHANGE

                        using var outParams = srClass.InvokeMethod("CreateRestorePoint", inParams, null);
                        var returnValue = outParams?["ReturnValue"] is uint code ? code : 1u;
                        return returnValue == 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "WMI CreateRestorePoint завершился с исключением, пробуем Checkpoint-Computer");
                        return false;
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (wmiCreated)
                {
                    _logger.LogInformation("Точка восстановления «{Description}» успешно создана через WMI.", safeDescription);
                    return (true, safeDescription);
                }
            }

            // Попытка 2: включение защиты и вызов Checkpoint-Computer
            await EnsureSystemProtectionEnabledAsync("C:", cancellationToken).ConfigureAwait(false);
            var escapedDesc = safeDescription.Replace("'", "''");
            var psArgs = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description '{escapedDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop\"";
            var psResult = await _processRunner.RunAsync("powershell.exe", psArgs, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (psResult.ExitCode == 0)
            {
                _logger.LogInformation("Точка восстановления «{Description}» успешно создана через PowerShell.", safeDescription);
                return (true, safeDescription);
            }

            return (false, $"Не удалось создать точку восстановления системы. Убедитесь, что служба «Теневое копирование тома» (VSS) не отключена и защита диска C: включена. Технические детали: {psResult.CombinedOutput.Trim()}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при создании точки восстановления");
            return (false, $"Ошибка создания точки восстановления: {ex.Message}");
        }
        finally
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var srKey = Registry.LocalMachine.OpenSubKey(srRegSubKey, writable: true);
                    if (srKey is not null)
                    {
                        if (previousFrequency.HasValue)
                        {
                            srKey.SetValue("SystemRestorePointCreationFrequency", previousFrequency.Value, RegistryValueKind.DWord);
                        }
                        else
                        {
                            srKey.DeleteValue("SystemRestorePointCreationFrequency", throwOnMissingValue: false);
                        }
                    }
                }
            }
            catch
            {
                // Игнорируем ошибку возврата частоты
            }
        }
    }

    public async Task<(bool Succeeded, string BackupFilePath, string MessageRu)> ExportRegistryKeyAsync(
        string fullRegistryKeyPath,
        string reasonSlug,
        CancellationToken cancellationToken = default)
    {
        var regDir = Path.Combine(_backupRootDirectory, "Registry", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(regDir);

        var safeSlug = string.Concat(reasonSlug.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
        if (string.IsNullOrWhiteSpace(safeSlug))
        {
            safeSlug = "regbackup";
        }

        var fileName = $"{DateTime.Now:HHmmss}_{safeSlug}.reg";
        var fullFilePath = Path.Combine(regDir, fileName);

        var result = await _processRunner.RunAsync(
            "reg.exe",
            $"export \"{fullRegistryKeyPath}\" \"{fullFilePath}\" /y",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0 && File.Exists(fullFilePath))
        {
            _logger.LogInformation("Ветка реестра {Key} экспортирована в {File}", fullRegistryKeyPath, fullFilePath);
            return (true, fullFilePath, $"Создана резервная копия ветки реестра: {fullFilePath}");
        }

        return (false, fullFilePath, $"Не удалось экспортировать ветку реестра «{fullRegistryKeyPath}»: {result.CombinedOutput.Trim()}");
    }

    public async Task<(bool Succeeded, string MessageRu)> ImportRegistryBackupAsync(
        string regFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(regFilePath) || !File.Exists(regFilePath))
        {
            return (false, $"Файл резервной копии реестра не найден: {regFilePath}");
        }

        var result = await _processRunner.RunAsync(
            "reg.exe",
            $"import \"{regFilePath}\"",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Резервная копия реестра успешно импортирована из {File}", regFilePath);
            return (true, $"Значения реестра успешно восстановлены из файла {Path.GetFileName(regFilePath)}.");
        }

        return (false, $"Ошибка импорта резервной копии реестра: {result.CombinedOutput.Trim()}");
    }

    public async Task<(bool Succeeded, string ExportFilePath, string MessageRu)> ExportBcdStoreAsync(
        CancellationToken cancellationToken = default)
    {
        var bcdDir = Path.Combine(_backupRootDirectory, "BCD", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(bcdDir);
        var exportFile = Path.Combine(bcdDir, $"bcd_backup_{DateTime.Now:HHmmss}.bak");

        var result = await _processRunner.RunAsync(
            "bcdedit.exe",
            $"/export \"{exportFile}\"",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Конфигурация загрузчика BCD экспортирована в {File}", exportFile);
            return (true, exportFile, $"Резервная копия BCD сохранена: {exportFile}");
        }

        return (false, exportFile, $"Не удалось экспортировать хранилище BCD: {result.CombinedOutput.Trim()}");
    }
}
