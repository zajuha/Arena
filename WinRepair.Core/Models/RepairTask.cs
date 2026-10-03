using WinRepair.Core.Diagnostics;
using WinRepair.Core.Safety;

namespace WinRepair.Core.Models;

public enum RepairCategory
{
    SystemFilesSfc = 0,
    ComponentStoreDism = 1,
    NetworkStack = 2,
    WindowsUpdateReset = 3,
    FileAssociations = 4,
    PowerSchemes = 5,
    DiskCheckChkdsk = 6,
    BootloaderBcd = 7,
    SystemRestorePoints = 8
}

/// <summary>
/// Результат проверки (ScanAsync) для модуля восстановления.
/// </summary>
public sealed record RepairScanFinding(
    string ModuleId,
    string ModuleNameRu,
    RepairCategory Category,
    bool NeedsRepair,
    IssueSeverity Severity,
    RiskLevel Risk,
    string CurrentStateRu,
    string RecommendationRu);

/// <summary>
/// Предпросмотр действий модуля восстановления перед запуском (PreviewAsync).
/// </summary>
public sealed record RepairPreview(
    string ModuleId,
    string ModuleNameRu,
    RepairCategory Category,
    RiskLevel Risk,
    bool RequiresReboot,
    string? RequiredConfirmationPhraseRu,
    IReadOnlyList<string> PlannedCommands,
    string BackupStrategyRu,
    string RollbackStrategyRu,
    string WarningMessageRu);

public sealed record RepairExecutionOptions(
    string? UserConfirmationPhrase = null,
    string? CustomInstallWimSourcePath = null,
    bool ScheduleChkdskOnNextReboot = false,
    bool EnsureRestorePointCreated = true);

public sealed record RepairExecutionResult(
    string ModuleId,
    string ModuleNameRu,
    bool Succeeded,
    bool WasCancelled,
    bool RebootRequired,
    string SummaryRu,
    string DetailedOutputRu,
    string? BackupArtifactPath,
    Guid OperationLogId,
    IReadOnlyList<OperationItemError> Errors);

/// <summary>
/// Универсальный отчёт о прогрессе выполнения операции (п. 5 ТЗ).
/// Пример: «Очистка кэша браузеров: 1,2 ГБ из 3,4 ГБ»
/// </summary>
public sealed record ProgressReport(
    string OperationTitleRu,
    string CurrentStepRu,
    double Percentage,
    long? ProcessedBytes = null,
    long? TotalBytes = null)
{
    public string FormattedBannerRu =>
        ProcessedBytes.HasValue && TotalBytes.HasValue && TotalBytes.Value > 0
            ? $"{OperationTitleRu}: {FileSizeFormatter.FormatRussian(ProcessedBytes.Value)} из {FileSizeFormatter.FormatRussian(TotalBytes.Value)}"
            : $"{OperationTitleRu}: {CurrentStepRu} ({Percentage:F0}%)";
}
