using WinRepair.Core.Diagnostics;
using WinRepair.Core.Safety;

namespace WinRepair.Core.Models;

public enum OperationType
{
    DiagnosticScan = 0,
    Cleanup = 1,
    Repair = 2,
    Optimization = 3,
    ProgramUninstallOrRemnantCleanup = 4,
    RestorePointCreation = 5,
    QuarantineRestore = 6,
    Rollback = 7
}

public enum OperationOutcome
{
    Succeeded = 0,
    PartiallySucceeded = 1,
    Failed = 2,
    Cancelled = 3,
    RolledBack = 4
}

/// <summary>
/// Запись полного журнала операций с информацией для отката (п. 3.6 и п. 4.8 ТЗ).
/// </summary>
public sealed record OperationLog(
    Guid Id,
    DateTimeOffset TimestampUtc,
    OperationType Type,
    string ModuleId,
    string TitleRu,
    string DescriptionRu,
    RiskLevel Risk,
    OperationOutcome Outcome,
    long FreedBytes,
    string? QuarantineBatchId,
    string? RegistryBackupFilePath,
    string? RestoreReverseCommand,
    bool CanRollback,
    bool IsRolledBack = false)
{
    public string OutcomeRu => Outcome switch
    {
        OperationOutcome.Succeeded => "Успешно",
        OperationOutcome.PartiallySucceeded => "Частично выполнено",
        OperationOutcome.Failed => "Ошибка",
        OperationOutcome.Cancelled => "Отменено пользователем",
        OperationOutcome.RolledBack => "Откачено",
        _ => "Неизвестно"
    };
}

/// <summary>
/// Описание одного элемента, помещённого в карантин (%ProgramData%\WinRepair\Quarantine\<дата>\).
/// </summary>
public sealed record QuarantineItemEntry(
    string ItemId,
    string OriginalFullPath,
    string QuarantinedFileName,
    string BatchId,
    string ModuleId,
    long SizeBytes,
    DateTimeOffset QuarantinedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Sha256Hash)
{
    public string FormattedSizeRu => FileSizeFormatter.FormatRussian(SizeBytes);
}

public sealed record QuarantineBatchManifest(
    string BatchId,
    string DateFolder,
    DateTimeOffset CreatedAtUtc,
    string ModuleId,
    string ModuleNameRu,
    int RetentionDays,
    IReadOnlyList<QuarantineItemEntry> Items);

// ==================== МОДЕЛИ ОПТИМИЗАЦИИ (п. 3.4 ТЗ) ====================

public enum StartupSourceType
{
    RegistryCurrentUserRun = 0,
    RegistryLocalMachineRun = 1,
    RegistryRunOnce = 2,
    StartupFolderUser = 3,
    StartupFolderCommon = 4,
    TaskSchedulerLogon = 5
}

public enum StartupImpactLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

public sealed record StartupEntry(
    string Id,
    string Name,
    string CommandOrTarget,
    string PublisherRu,
    StartupSourceType Source,
    string SourceLocationDisplayRu,
    bool IsEnabled,
    StartupImpactLevel Impact,
    string ImpactDisplayRu);

public enum ServiceStartMode
{
    Boot = 0,
    System = 1,
    Automatic = 2,
    AutomaticDelayed = 3,
    Manual = 4,
    Disabled = 5
}

public static class ServiceStartModeExtensions
{
    public static string ToRussianName(this ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Boot => "Загрузчик (Boot)",
        ServiceStartMode.System => "Ядро (System)",
        ServiceStartMode.Automatic => "Автоматически",
        ServiceStartMode.AutomaticDelayed => "Автоматически (отложенный запуск)",
        ServiceStartMode.Manual => "Вручную (по требованию)",
        ServiceStartMode.Disabled => "Отключена",
        _ => "Неизвестно"
    };
}

public sealed record ServiceOptimizationEntry(
    string ServiceName,
    string DisplayNameRu,
    string DescriptionRu,
    string CurrentStatusRu,
    ServiceStartMode CurrentStartMode,
    ServiceStartMode RecommendedStartMode,
    string RecommendationReasonRu,
    bool IsSafeToChange,
    RiskLevel Risk);

public sealed record ScheduledTaskEntry(
    string TaskPath,
    string TaskName,
    string Author,
    string DescriptionRu,
    string StateRu,
    bool IsEnabled,
    bool IsMicrosoftCoreTask,
    string NextRunTimeRu);

public sealed record PowerPlanEntry(
    string SchemeGuid,
    string NameRu,
    string DescriptionRu,
    bool IsActive);

public sealed record MemoryTrimSummary(
    int TrimmedProcessesCount,
    int SkippedSystemProcessesCount,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long ReclaimedBytes,
    string HonestTechnicalNoteRu)
{
    public string FormattedReclaimedRu => FileSizeFormatter.FormatRussian(Math.Max(0, ReclaimedBytes));
}

// ==================== МОДЕЛИ УПРАВЛЕНИЯ ПРОГРАММАМИ (п. 3.5 ТЗ) ====================

public sealed record InstalledProgramEntry(
    string Id,
    string DisplayName,
    string Publisher,
    string DisplayVersion,
    DateTimeOffset? InstallDate,
    long EstimatedSizeBytes,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    string RegistryKeyPath,
    bool Is64Bit)
{
    public string FormattedSizeRu => EstimatedSizeBytes > 0
        ? FileSizeFormatter.FormatRussian(EstimatedSizeBytes)
        : "Не указан";

    public string FormattedInstallDateRu => InstallDate.HasValue
        ? InstallDate.Value.ToString("dd.MM.yyyy")
        : "Неизвестно";
}

public enum RemnantKind
{
    FileSystemFolder = 0,
    RegistryKey = 1,
    OrphanedService = 2
}

public sealed record ProgramRemnantEntry(
    string Id,
    string FormerApplicationName,
    RemnantKind Kind,
    string KindDisplayRu,
    string PathOrKey,
    long SizeBytes,
    RiskLevel Risk,
    string EvidenceReasonRu,
    bool IsSelected = true)
{
    public string FormattedSizeRu => Kind == RemnantKind.FileSystemFolder
        ? FileSizeFormatter.FormatRussian(SizeBytes)
        : "Ключ / Служба";
}

// ==================== ПЛАНИРОВЩИК И ПРОФИЛЬ НАСТРОЕК (п. 3.6 ТЗ) ====================

public enum ScheduleFrequency
{
    Disabled = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}

public sealed record CleanupScheduleConfig(
    bool IsEnabled,
    ScheduleFrequency Frequency,
    int HourOfDay,
    int MinuteOfHour,
    DayOfWeek DayOfWeek,
    IReadOnlyList<CleanupCategory> EnabledCategories,
    bool CreateRestorePointBeforeScheduledRun);

public sealed record AppSettingsProfile(
    string ProfileVersion,
    string ThemeName, // "Dark" или "Light"
    double FontScaleFactor,
    bool IsFirstRunCompleted,
    bool UseQuarantineByDefault,
    int QuarantineRetentionDays,
    bool AutoCreateRestorePointInSession,
    CleanupScheduleConfig Schedule,
    DateTimeOffset ExportedAtUtc)
{
    public static AppSettingsProfile CreateDefault() => new(
        ProfileVersion: "1.0.0",
        ThemeName: "Dark",
        FontScaleFactor: 1.0,
        IsFirstRunCompleted: false,
        UseQuarantineByDefault: true,
        QuarantineRetentionDays: 14,
        AutoCreateRestorePointInSession: true,
        Schedule: new CleanupScheduleConfig(
            IsEnabled: false,
            Frequency: ScheduleFrequency.Weekly,
            HourOfDay: 12,
            MinuteOfHour: 0,
            DayOfWeek: DayOfWeek.Sunday,
            EnabledCategories:
            [
                CleanupCategory.SystemJunk,
                CleanupCategory.BrowserCache,
                CleanupCategory.ThumbnailsAndIcons,
                CleanupCategory.DumpsAndLogs
            ],
            CreateRestorePointBeforeScheduledRun: true),
        ExportedAtUtc: DateTimeOffset.UtcNow);
}
