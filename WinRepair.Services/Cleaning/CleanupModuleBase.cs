using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.Services.Cleaning;

/// <summary>
/// Базовый класс для всех файловых модулей очистки, обеспечивающий:
/// 1) обязательный цикл ScanAsync -> PreviewAsync -> ExecuteAsync;
/// 2) проверку каждого пути через PathValidator (белый список + защита от симлинков);
/// 3) создание точки восстановления через ISessionSafetyGuard перед первой модификацией;
/// 4) перемещение в карантин по умолчанию (или безвозвратное удаление только при вводе фразы);
/// 5) запись в журнал операций с возможностью отката.
/// </summary>
public abstract class CleanupModuleBase : ICleanupModule
{
    protected readonly PathValidator PathValidator;
    protected readonly ISessionSafetyGuard SafetyGuard;
    protected readonly IQuarantineService QuarantineService;
    protected readonly IOperationJournalService JournalService;
    protected readonly ILogger Logger;

    protected CleanupModuleBase(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger logger)
    {
        PathValidator = pathValidator;
        SafetyGuard = safetyGuard;
        QuarantineService = quarantineService;
        JournalService = journalService;
        Logger = logger;
    }

    public abstract string Id { get; }
    public abstract string NameRu { get; }
    public abstract string DescriptionRu { get; }
    public abstract CleanupCategory Category { get; }
    public abstract RiskLevel Risk { get; }

    protected abstract IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories();

    public virtual Task<IReadOnlyList<CleanupItem>> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CleanupItem>>(() =>
        {
            var items = new List<CleanupItem>();
            var targets = GetTargetDirectories().ToList();
            var dirIndex = 0;

            foreach (var (dirPath, pattern, recurse) in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dirIndex++;

                progress?.Report(new ProgressReport(
                    $"Сканирование: {NameRu}",
                    $"Проверка каталога {dirPath}",
                    targets.Count > 0 ? (double)dirIndex / targets.Count * 100.0 : 100.0));

                if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
                {
                    continue;
                }

                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = recurse,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(dirPath, pattern, enumOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var validation = PathValidator.ValidateForDeletion(filePath);
                        if (!validation.IsAllowed)
                        {
                            continue;
                        }

                        try
                        {
                            var fi = new FileInfo(filePath);
                            if (!fi.Exists)
                            {
                                continue;
                            }

                            items.Add(new CleanupItem(
                                Id: Guid.NewGuid().ToString("N"),
                                FullPath: validation.NormalizedPath,
                                ModuleId: Id,
                                ModuleNameRu: NameRu,
                                Category: Category,
                                SizeBytes: fi.Length,
                                LastModifiedUtc: new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                                Risk: Risk,
                                DescriptionRu: $"{NameRu} ({fi.Name})",
                                IsSelected: true));
                        }
                        catch
                        {
                            // Файл мог быть удалён параллельно другим процессом
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Не удалось просканировать каталог {Directory} в модуле {Module}", dirPath, Id);
                }
            }

            return items;
        }, cancellationToken);
    }

    public virtual async Task<CleanupPreview> PreviewAsync(
        IReadOnlyList<CleanupItem>? preScannedItems = null,
        CancellationToken cancellationToken = default)
    {
        var items = preScannedItems ?? await ScanAsync( null, cancellationToken).ConfigureAwait(false);
        var totalBytes = items.Sum(i => i.SizeBytes);

        return new CleanupPreview(
            PreviewId: Guid.NewGuid(),
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Items: items,
            TotalBytes: totalBytes,
            SafetyNoticeRu: $"Перед удалением все выбранные файлы ({items.Count} шт., {FileSizeFormatter.FormatRussian(totalBytes)}) проверяются по белому списку и по умолчанию перемещаются в карантин с возможностью полного возврата.");
    }

    public virtual async Task<CleanupExecutionResult> ExecuteAsync(
        CleanupPreview approvedPreview,
        CleanupExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedPreview);

        // 1. Проверка двойного подтверждения, если пользователь выбрал безвозвратное удаление без карантина (п. 4.5 ТЗ)
        if (!options.MoveToQuarantine && options.AllowPermanentDeletion)
        {
            var confirm = HighRiskConfirmationValidator.Validate(
                RiskLevel.High,
                HighRiskConfirmationValidator.PermanentDeletePhrase,
                options.PermanentDeletionConfirmationPhrase);

            if (!confirm.IsValid)
            {
                return new CleanupExecutionResult(
                    Id, NameRu, Succeeded: false, WasCancelled: false,
                    FreedBytes: 0, ProcessedItemsCount: 0, QuarantinedItemsCount: 0, FailedItemsCount: 0,
                    QuarantineBatchId: null, OperationLogId: Guid.Empty, RestorePointVerified: false,
                    SummaryRu: confirm.MessageRu, Errors: []);
            }
        }

        // 2. Обязательная точка восстановления перед первой изменяющей операцией в сессии (п. 4.1 ТЗ)
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await SafetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new CleanupExecutionResult(
                    Id, NameRu, Succeeded: false, WasCancelled: false,
                    FreedBytes: 0, ProcessedItemsCount: 0, QuarantinedItemsCount: 0, FailedItemsCount: 0,
                    QuarantineBatchId: null, OperationLogId: Guid.Empty, RestorePointVerified: false,
                    SummaryRu: reasonRu, Errors: []);
            }
        }

        var selectedItems = approvedPreview.Items.Where(i => i.IsSelected).ToList();
        var totalTargetBytes = selectedItems.Sum(i => i.SizeBytes);
        long freedBytes = 0;
        var processedCount = 0;
        var quarantinedCount = 0;
        var errors = new List<OperationItemError>();
        var batchId = $"q_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Id.Replace('.', '_')}";
        var wasCancelled = false;

        for (var i = 0; i < selectedItems.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
                break;
            }

            var item = selectedItems[i];
            var pct = selectedItems.Count > 0 ? (double)(i + 1) / selectedItems.Count * 100.0 : 100.0;

            progress?.Report(new ProgressReport(
                NameRu,
                Path.GetFileName(item.FullPath),
                pct,
                freedBytes,
                totalTargetBytes));

            // Повторная проверка валидатором путей непосредственно перед операцией (защита от TOCTOU и подмены симлинком)
            var validation = PathValidator.ValidateForDeletion(item.FullPath);
            if (!validation.IsAllowed)
            {
                errors.Add(new OperationItemError(item.FullPath, validation.ReasonRu, "PathValidationDenied"));
                continue;
            }

            try
            {
                if (options.MoveToQuarantine)
                {
                    var (qSuccess, _, qErr) = await QuarantineService.MoveFileToQuarantineAsync(
                        validation.NormalizedPath,
                        batchId,
                        Id,
                        NameRu,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (qSuccess)
                    {
                        freedBytes += item.SizeBytes;
                        processedCount++;
                        quarantinedCount++;
                    }
                    else
                    {
                        errors.Add(new OperationItemError(item.FullPath, qErr, "QuarantineMoveFailed"));
                    }
                }
                else
                {
                    if (File.Exists(validation.NormalizedPath))
                    {
                        var fi = new FileInfo(validation.NormalizedPath);
                        if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                        {
                            fi.Attributes &= ~FileAttributes.ReadOnly;
                        }

                        File.Delete(validation.NormalizedPath);
                        freedBytes += item.SizeBytes;
                        processedCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                // Ошибка одного файла не прерывает остальные (п. 4.11 ТЗ)
                Logger.LogWarning(ex, "Не удалось обработать файл {File} при очистке {Module}", item.FullPath, Id);
                errors.Add(new OperationItemError(
                    item.FullPath,
                    "Файл используется другим процессом или заблокирован системой.",
                    ex.Message));
            }
        }

        var outcome = wasCancelled
            ? OperationOutcome.Cancelled
            : errors.Count == 0
                ? OperationOutcome.Succeeded
                : processedCount > 0
                    ? OperationOutcome.PartiallySucceeded
                    : OperationOutcome.Failed;

        var summaryRu = wasCancelled
            ? $"{NameRu}: операция прервана пользователем. Освобождено {FileSizeFormatter.FormatRussian(freedBytes)} ({processedCount} файлов)."
            : $"{NameRu}: освобождено {FileSizeFormatter.FormatRussian(freedBytes)}, обработано файлов: {processedCount}, пропущено (занято): {errors.Count}.";

        var logEntry = await JournalService.RecordAsync(new OperationLog(
            Id: Guid.NewGuid(),
            TimestampUtc: DateTimeOffset.UtcNow,
            Type: OperationType.Cleanup,
            ModuleId: Id,
            TitleRu: NameRu,
            DescriptionRu: summaryRu,
            Risk: Risk,
            Outcome: outcome,
            FreedBytes: freedBytes,
            QuarantineBatchId: quarantinedCount > 0 ? batchId : null,
            RegistryBackupFilePath: null,
            RestoreReverseCommand: null,
            CanRollback: quarantinedCount > 0), cancellationToken).ConfigureAwait(false);

        return new CleanupExecutionResult(
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Succeeded: !wasCancelled && (processedCount > 0 || selectedItems.Count == 0),
            WasCancelled: wasCancelled,
            FreedBytes: freedBytes,
            ProcessedItemsCount: processedCount,
            QuarantinedItemsCount: quarantinedCount,
            FailedItemsCount: errors.Count,
            QuarantineBatchId: quarantinedCount > 0 ? batchId : null,
            OperationLogId: logEntry.Id,
            RestorePointVerified: SafetyGuard.HasSessionRestorePoint,
            SummaryRu: summaryRu,
            Errors: errors);
    }
}
