using WinRepair.Core.Diagnostics;
using WinRepair.Core.Safety;

namespace WinRepair.Core.Models;

public enum CleanupCategory
{
    SystemJunk = 0,
    RecycleBin = 1,
    WindowsUpdateCache = 2,
    BrowserCache = 3,
    ThumbnailsAndIcons = 4,
    DumpsAndLogs = 5,
    DirectXAndShaders = 6,
    UninstalledProgramRemnants = 7,
    WindowsErrorReporting = 8,
    OldWindowsUpdatesDism = 9
}

public static class CleanupCategoryExtensions
{
    public static string ToRussianTitle(this CleanupCategory category) => category switch
    {
        CleanupCategory.SystemJunk => "Системный мусор (%TEMP%, Prefetch)",
        CleanupCategory.RecycleBin => "Корзина на всех томах",
        CleanupCategory.WindowsUpdateCache => "Кэш обновлений Windows",
        CleanupCategory.BrowserCache => "Кэш браузеров (Edge, Chrome, Firefox, Яндекс, Opera)",
        CleanupCategory.ThumbnailsAndIcons => "Эскизы и кэш иконок проводника",
        CleanupCategory.DumpsAndLogs => "Аварийные дампы памяти и системные логи",
        CleanupCategory.DirectXAndShaders => "Кэш DirectX и шейдеров видеокарты",
        CleanupCategory.UninstalledProgramRemnants => "Остатки удалённых программ",
        CleanupCategory.WindowsErrorReporting => "Отчёты об ошибках Windows (WER, CBS)",
        CleanupCategory.OldWindowsUpdatesDism => "Устаревшие компоненты обновлений (DISM)",
        _ => "Прочее"
    };
}

/// <summary>
/// Единичный элемент, найденный модулем очистки и обязательный к показу в предпросмотре.
/// </summary>
public sealed record CleanupItem(
    string Id,
    string FullPath,
    string ModuleId,
    string ModuleNameRu,
    CleanupCategory Category,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    RiskLevel Risk,
    string DescriptionRu,
    bool IsSelected = true,
    bool IsRegistryEntry = false)
{
    public string FormattedSizeRu => FileSizeFormatter.FormatRussian(SizeBytes);
}

/// <summary>
/// Обязательный предпросмотр перед выполнением любой очистки (п. 3.2 и п. 4.2 ТЗ).
/// </summary>
public sealed record CleanupPreview(
    Guid PreviewId,
    string ModuleId,
    string ModuleNameRu,
    CleanupCategory Category,
    RiskLevel Risk,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<CleanupItem> Items,
    long TotalBytes,
    string SafetyNoticeRu,
    bool UsesDismComponentCleanup = false)
{
    public int SelectedCount => Items.Count(i => i.IsSelected);
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
    public string FormattedTotalSizeRu => FileSizeFormatter.FormatRussian(TotalBytes);
    public string FormattedSelectedSizeRu => FileSizeFormatter.FormatRussian(SelectedBytes);
}

/// <summary>
/// Параметры запуска очистки. По умолчанию включён карантин (п. 4.5 ТЗ).
/// </summary>
public sealed record CleanupExecutionOptions(
    bool MoveToQuarantine = true,
    bool AllowPermanentDeletion = false,
    string? PermanentDeletionConfirmationPhrase = null,
    bool EnsureRestorePointCreated = true);

public sealed record OperationItemError(
    string TargetPath,
    string ErrorMessageRu,
    string TechnicalDetails);

public sealed record CleanupExecutionResult(
    string ModuleId,
    string ModuleNameRu,
    bool Succeeded,
    bool WasCancelled,
    long FreedBytes,
    int ProcessedItemsCount,
    int QuarantinedItemsCount,
    int FailedItemsCount,
    string? QuarantineBatchId,
    Guid OperationLogId,
    bool RestorePointVerified,
    string SummaryRu,
    IReadOnlyList<OperationItemError> Errors)
{
    public string FormattedFreedSizeRu => FileSizeFormatter.FormatRussian(FreedBytes);
}

/// <summary>
/// Запись о крупном файле (только для отображения; удаление выполняется только вручную пользователем).
/// </summary>
public sealed record LargeFileEntry(
    string FullPath,
    string FileName,
    string DirectoryPath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string Extension)
{
    public string FormattedSizeRu => FileSizeFormatter.FormatRussian(SizeBytes);
}

/// <summary>
/// Группа дублирующихся файлов по размеру и криптографическому хешу SHA-256.
/// Только показывается в списке (п. 3.2 ТЗ).
/// </summary>
public sealed record DuplicateFileGroup(
    string Sha256Hash,
    long FileSizeBytes,
    IReadOnlyList<LargeFileEntry> Files)
{
    public long ReclaimableBytes => FileSizeBytes * Math.Max(0, Files.Count - 1);
    public string FormattedFileSizeRu => FileSizeFormatter.FormatRussian(FileSizeBytes);
    public string FormattedReclaimableSizeRu => FileSizeFormatter.FormatRussian(ReclaimableBytes);
}
