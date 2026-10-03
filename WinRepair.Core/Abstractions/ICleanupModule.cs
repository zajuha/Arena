using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.Core.Abstractions;

/// <summary>
/// Базовый контракт любого модуля очистки системы.
/// Обязательный жизненный цикл: ScanAsync() -> PreviewAsync() -> ExecuteAsync().
/// </summary>
public interface ICleanupModule
{
    string Id { get; }

    string NameRu { get; }

    string DescriptionRu { get; }

    CleanupCategory Category { get; }

    RiskLevel Risk { get; }

    /// <summary>
    /// Шаг 1: сканирование файловой системы/кэша (только чтение, подсчёт объёма и списка путей).
    /// </summary>
    Task<IReadOnlyList<CleanupItem>> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Шаг 2: формирование обязательного предпросмотра для показа пользователю до удаления.
    /// </summary>
    Task<CleanupPreview> PreviewAsync(
        IReadOnlyList<CleanupItem>? preScannedItems = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Шаг 3: выполнение очистки по утверждённому предпросмотру (с карантином по умолчанию,
    /// валидацией белого списка и созданием точки восстановления).
    /// </summary>
    Task<CleanupExecutionResult> ExecuteAsync(
        CleanupPreview approvedPreview,
        CleanupExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}
