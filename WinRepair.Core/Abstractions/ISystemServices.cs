using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.Core.Abstractions;

/// <summary>
/// Сервис комплексной диагностики системы (только чтение, ничего не изменяет — п. 3.1 ТЗ).
/// </summary>
public interface IDiagnosticService
{
    Task<ScanResult> RunFullScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Сервис создания точек восстановления системы и экспорта веток реестра (.reg).
/// </summary>
public interface IBackupService
{
    Task<(bool Succeeded, string MessageRu)> EnsureSystemProtectionEnabledAsync(
        string systemDrive = "C:",
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string DescriptionRu)> CreateRestorePointAsync(
        string descriptionRu,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string BackupFilePath, string MessageRu)> ExportRegistryKeyAsync(
        string fullRegistryKeyPath,
        string reasonSlug,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> ImportRegistryBackupAsync(
        string regFilePath,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string ExportFilePath, string MessageRu)> ExportBcdStoreAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Гарант безопасности сессии (п. 4.1 ТЗ):
/// перед первой изменяющей операцией в сессии автоматически создаёт точку восстановления.
/// Если точку создать не удалось — изменяющая операция блокируется.
/// </summary>
public interface ISessionSafetyGuard
{
    bool HasSessionRestorePoint { get; }
    string? SessionRestorePointDescription { get; }
    DateTimeOffset? CreatedAtUtc { get; }

    Task<(bool Allowed, string ReasonRu)> EnsureSafeToMutateSystemAsync(
        string initiatingOperationTitleRu,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Сервис карантина (%ProgramData%\WinRepair\Quarantine\<дата>\).
/// </summary>
public interface IQuarantineService
{
    string QuarantineRootDirectory { get; }

    Task<(bool Succeeded, QuarantineItemEntry? Entry, string ErrorMessageRu)> MoveFileToQuarantineAsync(
        string sourceFilePath,
        string batchId,
        string moduleId,
        string moduleNameRu,
        int retentionDays = 14,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuarantineBatchManifest>> GetQuarantineBatchesAsync(
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, int RestoredCount, string MessageRu)> RestoreBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> RestoreSingleItemAsync(
        string batchId,
        string itemId,
        CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredBatchesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Полный журнал операций с возможностью отката по каждому пункту (п. 3.6 и п. 4.8 ТЗ).
/// </summary>
public interface IOperationJournalService
{
    Task<OperationLog> RecordAsync(
        OperationLog entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationLog>> GetHistoryAsync(
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> RollbackOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Сканер больших и дублирующихся файлов (п. 3.2 ТЗ: только показывает список, удаление вручную).
/// </summary>
public interface ILargeAndDuplicateFileScanner
{
    Task<IReadOnlyList<LargeFileEntry>> FindLargeFilesAsync(
        string rootDirectory,
        long minSizeBytes = 100L * 1024 * 1024, // 100 МБ по умолчанию
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DuplicateFileGroup>> FindDuplicateFilesAsync(
        string rootDirectory,
        long minSizeBytes = 1024 * 1024, // 1 МБ по умолчанию
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Сервис оптимизации автозагрузки, служб, задач, схем питания, визуальных эффектов и ОЗУ (п. 3.4 ТЗ).
/// </summary>
public interface IOptimizationService
{
    Task<IReadOnlyList<StartupEntry>> GetStartupEntriesAsync(CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> SetStartupEntryEnabledAsync(
        StartupEntry entry,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceOptimizationEntry>> GetOptimizableServicesAsync(CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> ApplyServiceStartModeAsync(
        ServiceOptimizationEntry service,
        ServiceStartMode targetMode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledTaskEntry>> GetNonCriticalScheduledTasksAsync(CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> SetScheduledTaskEnabledAsync(
        ScheduledTaskEntry task,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PowerPlanEntry>> GetPowerPlansAsync(CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> ActivatePowerPlanAsync(
        string schemeGuid,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> ApplyVisualEffectsProfileAsync(
        bool optimizeForPerformance,
        CancellationToken cancellationToken = default);

    Task<MemoryTrimSummary> TrimProcessesWorkingSetAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Сервис управления установленными программами и поиска остатков удалённых программ (п. 3.5 ТЗ).
/// </summary>
public interface IProgramManagerService
{
    Task<IReadOnlyList<InstalledProgramEntry>> GetInstalledProgramsAsync(
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string MessageRu)> LaunchStandardUninstallerAsync(
        InstalledProgramEntry program,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProgramRemnantEntry>> ScanForUninstalledProgramRemnantsAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, int RemovedCount, string MessageRu)> RemoveSelectedRemnantsAsync(
        IReadOnlyList<ProgramRemnantEntry> selectedRemnants,
        bool moveToQuarantine = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Генерация и экспорт отчётов в форматах HTML (для чтения в браузере) и JSON (п. 3.6 ТЗ).
/// </summary>
public interface IReportService
{
    Task<string> ExportHtmlReportAsync(
        ScanResult? scanResult,
        IReadOnlyList<OperationLog> recentOperations,
        string outputFilePath,
        CancellationToken cancellationToken = default);

    Task<string> ExportJsonReportAsync(
        ScanResult? scanResult,
        IReadOnlyList<OperationLog> recentOperations,
        string outputFilePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Сервис настройки расписания автоматической очистки через Планировщик заданий Windows (Task Scheduler)
/// и экспорта/импорта профиля настроек (п. 3.6 ТЗ).
/// </summary>
public interface ISchedulerAndProfileService
{
    Task<(bool Succeeded, string MessageRu)> ConfigureScheduledCleanupTaskAsync(
        CleanupScheduleConfig config,
        string executablePath,
        CancellationToken cancellationToken = default);

    Task<AppSettingsProfile> LoadProfileAsync(CancellationToken cancellationToken = default);

    Task SaveProfileAsync(AppSettingsProfile profile, CancellationToken cancellationToken = default);

    Task ExportProfileToFileAsync(AppSettingsProfile profile, string destinationFilePath, CancellationToken cancellationToken = default);

    Task<AppSettingsProfile> ImportProfileFromFileAsync(string sourceFilePath, CancellationToken cancellationToken = default);
}
