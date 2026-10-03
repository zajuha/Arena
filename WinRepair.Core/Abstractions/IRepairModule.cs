using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.Core.Abstractions;

/// <summary>
/// Базовый контракт любого модуля восстановления работоспособности ОС.
/// Обязательный жизненный цикл: ScanAsync() -> PreviewAsync() -> ExecuteAsync().
/// </summary>
public interface IRepairModule
{
    string Id { get; }

    string NameRu { get; }

    string DescriptionRu { get; }

    RepairCategory Category { get; }

    RiskLevel Risk { get; }

    /// <summary>
    /// Контрольная фраза для ручного ввода при операциях высокого риска (RiskLevel.High), иначе null.
    /// </summary>
    string? ConfirmationPhraseRu { get; }

    /// <summary>
    /// Шаг 1: диагностика состояния подсистемы без внесения изменений.
    /// </summary>
    Task<RepairScanFinding> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Шаг 2: формирование предпросмотра запланированных команд, резервного копирования и предупреждений.
    /// </summary>
    Task<RepairPreview> PreviewAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Шаг 3: выполнение восстановления (с проверкой точки восстановления, бэкапом и двойным подтверждением).
    /// </summary>
    Task<RepairExecutionResult> ExecuteAsync(
        RepairPreview approvedPreview,
        RepairExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}
