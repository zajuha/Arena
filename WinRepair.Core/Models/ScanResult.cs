using WinRepair.Core.Safety;

namespace WinRepair.Core.Models;

public enum HealthCategory
{
    Disks = 0,
    SystemFiles = 1,
    EventLog = 2,
    Services = 3,
    Drivers = 4,
    Network = 5,
    WindowsUpdate = 6,
    Boot = 7,
    Power = 8,
    Security = 9
}

public enum IssueSeverity
{
    Healthy = 0,
    Information = 1,
    Warning = 2,
    Critical = 3
}

public static class HealthCategoryExtensions
{
    public static string ToRussianTitle(this HealthCategory category) => category switch
    {
        HealthCategory.Disks => "Диски и SMART",
        HealthCategory.SystemFiles => "Целостность системных файлов",
        HealthCategory.EventLog => "Журнал событий Windows",
        HealthCategory.Services => "Системные службы",
        HealthCategory.Drivers => "Драйверы и устройства",
        HealthCategory.Network => "Сеть и DNS",
        HealthCategory.WindowsUpdate => "Центр обновления Windows",
        HealthCategory.Boot => "Загрузка и автозапуск",
        HealthCategory.Power => "Электропитание и батарея",
        HealthCategory.Security => "Безопасность и защита",
        _ => "Прочее"
    };

    public static string ToRussianShortStatus(this IssueSeverity severity) => severity switch
    {
        IssueSeverity.Healthy => "В норме",
        IssueSeverity.Information => "Сведения",
        IssueSeverity.Warning => "Требует внимания",
        IssueSeverity.Critical => "Критическая проблема",
        _ => "Неизвестно"
    };
}

public sealed record CategoryHealthScore(
    HealthCategory Category,
    string TitleRu,
    int Score,
    IssueSeverity Severity,
    string SummaryRu,
    int IssuesCount,
    int SafeFixableCount);

public sealed record DiagnosticIssue(
    string Id,
    HealthCategory Category,
    IssueSeverity Severity,
    RiskLevel FixRiskLevel,
    string TitleRu,
    string DescriptionRu,
    string RecommendationRu,
    string? RelatedModuleId,
    bool CanAutoFixSafely);

public sealed record DiskDriveDiagnostic(
    string DriveLetter,
    string VolumeLabel,
    string FileSystem,
    string MediaTypeRu, // SSD / HDD / NVMe
    long TotalBytes,
    long FreeBytes,
    double FreePercent,
    string SmartStatusRu,
    bool SmartPredictFailure,
    int? TemperatureCelsius,
    bool HasFileSystemErrors);

public sealed record SystemFilesDiagnostic(
    string DismHealthStateRu,
    bool ComponentStoreRepairable,
    bool IntegrityViolationsDetected,
    string LastScanSummaryRu,
    DateTimeOffset CheckedAtUtc);

public sealed record EventLogCriticalEntry(
    long RecordId,
    DateTimeOffset TimeCreatedUtc,
    string LogName,
    string ProviderName,
    int EventId,
    string LevelRu,
    string MessageRu);

public sealed record ServiceDiagnosticEntry(
    string ServiceName,
    string DisplayNameRu,
    string StartModeRu,
    string CurrentStateRu,
    bool IsCriticalSystemService,
    bool IsStoppedUnexpectedly);

public sealed record DriverProblemEntry(
    string DeviceName,
    string DeviceId,
    string ClassName,
    uint ConfigManagerErrorCode,
    string ErrorDescriptionRu);

public sealed record NetworkDiagnosticInfo(
    bool HasActiveAdapter,
    bool DnsResolutionWorking,
    int DnsLatencyMs,
    bool ProxyEnabled,
    string? ProxyServer,
    string WinsockStatusRu,
    IReadOnlyList<string> ActiveAdaptersRu);

public sealed record WindowsUpdateDiagnosticInfo(
    bool WuServiceRunning,
    bool BitsServiceRunning,
    DateTimeOffset? LastSuccessUpdateDateUtc,
    int PendingUpdatesCount,
    int RecentUpdateErrorsCount,
    IReadOnlyList<string> RecentInstalledKbs);

public sealed record BootDiagnosticInfo(
    double LastBootDurationSeconds,
    int TotalStartupItems,
    int HighImpactStartupItems,
    string FirmwareTypeRu); // UEFI / Legacy BIOS

public sealed record PowerDiagnosticInfo(
    string ActivePowerSchemeGuid,
    string ActivePowerSchemeNameRu,
    bool HasBattery,
    int? BatteryChargePercent,
    double? BatteryWearPercent,
    int? DesignCapacityMilliwattHours,
    int? FullChargeCapacityMilliwattHours);

public sealed record SecurityDiagnosticInfo(
    bool DefenderEnabled,
    bool RealTimeProtectionEnabled,
    bool SignaturesUpToDate,
    bool FirewallEnabledOnAllProfiles,
    bool SystemRestoreEnabled,
    int ExistingRestorePointsCount);

/// <summary>
/// Полный результат комплексной диагностики состояния Windows (только чтение).
/// </summary>
public sealed record ScanResult(
    Guid ScanId,
    DateTimeOffset CompletedAtUtc,
    TimeSpan Duration,
    string OsVersionRu,
    string MachineName,
    int OverallScore,
    string OverallStatusRu,
    IReadOnlyList<CategoryHealthScore> CategoryScores,
    IReadOnlyList<DiagnosticIssue> Issues,
    IReadOnlyList<DiskDriveDiagnostic> Disks,
    SystemFilesDiagnostic SystemFiles,
    IReadOnlyList<EventLogCriticalEntry> RecentCriticalEvents,
    IReadOnlyList<ServiceDiagnosticEntry> ProblematicServices,
    IReadOnlyList<DriverProblemEntry> DriverProblems,
    NetworkDiagnosticInfo Network,
    WindowsUpdateDiagnosticInfo WindowsUpdate,
    BootDiagnosticInfo Boot,
    PowerDiagnosticInfo Power,
    SecurityDiagnosticInfo Security)
{
    public int TotalIssuesCount => Issues.Count;

    public int SafeAutoFixableIssuesCount => Issues.Count(i => i.CanAutoFixSafely && i.FixRiskLevel.IsEligibleForSafeAutoFix());
}
