using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.App.ViewModels;

public sealed partial class CleanupModuleItemViewModel : ObservableObject
{
    public ICleanupModule Module { get; }

    public string Id => Module.Id;
    public string NameRu => Module.NameRu;
    public string DescriptionRu => Module.DescriptionRu;
    public string RiskDisplayRu => Module.Risk.ToRussianDisplayName();

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _previewSizeSummaryRu = "Требуется предпросмотр";

    [ObservableProperty]
    private int _previewItemsCount;

    public CleanupPreview? LatestPreview { get; set; }

    public CleanupModuleItemViewModel(ICleanupModule module)
    {
        Module = module;
    }
}

/// <summary>
/// ViewModel раздела «Очистка»: 10 модулей с обязательным предпросмотром,
/// карантин по умолчанию и ручной анализ больших и дублирующихся файлов (п. 3.2 и п. 4.2 ТЗ).
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ILargeAndDuplicateFileScanner _largeFileScanner;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly Action<ProgressReport> _onProgress;
    private readonly Action<string> _onSummaryUpdated;

    public ObservableCollection<CleanupModuleItemViewModel> Modules { get; } = [];
    public ObservableCollection<CleanupItem> PreviewItems { get; } = [];
    public ObservableCollection<LargeFileEntry> LargeFiles { get; } = [];
    public ObservableCollection<DuplicateFileGroup> DuplicateGroups { get; } = [];

    [ObservableProperty]
    private bool _isPreviewReady;

    [ObservableProperty]
    private string _totalPreviewSizeRu = "0 Б";

    [ObservableProperty]
    private bool _moveToQuarantine = true;

    [ObservableProperty]
    private string _permanentDeletePhraseInput = string.Empty;

    [ObservableProperty]
    private string _scanDirectoryForLargeFiles = @"C:\";

    public CleanupViewModel(
        IEnumerable<ICleanupModule> modules,
        ILargeAndDuplicateFileScanner largeFileScanner,
        ISessionSafetyGuard safetyGuard,
        Action<ProgressReport> onProgress,
        Action<string> onSummaryUpdated)
    {
        _largeFileScanner = largeFileScanner;
        _safetyGuard = safetyGuard;
        _onProgress = onProgress;
        _onSummaryUpdated = onSummaryUpdated;

        foreach (var m in modules)
        {
            Modules.Add(new CleanupModuleItemViewModel(m));
        }
    }

    public async Task BuildPreviewInternalAsync(CancellationToken cancellationToken)
    {
        IsPreviewReady = false;
        PreviewItems.Clear();

        var progress = new Progress<ProgressReport>(_onProgress);
        long grandTotal = 0;

        foreach (var modVm in Modules.Where(m => m.IsSelected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanned = await modVm.Module.ScanAsync(progress, cancellationToken);
            var preview = await modVm.Module.PreviewAsync(scanned, cancellationToken);

            modVm.LatestPreview = preview;
            modVm.PreviewItemsCount = preview.Items.Count;
            modVm.PreviewSizeSummaryRu = preview.FormattedTotalSizeRu;
            grandTotal += preview.TotalBytes;

            foreach (var item in preview.Items)
            {
                PreviewItems.Add(item);
            }
        }

        TotalPreviewSizeRu = FileSizeFormatter.FormatRussian(grandTotal);
        IsPreviewReady = true;
        _onSummaryUpdated($"Предпросмотр сформирован: найдено {PreviewItems.Count} объектов общим объёмом {TotalPreviewSizeRu}. Проверьте список перед очисткой.");
    }

    public async Task ExecuteApprovedCleanupInternalAsync(CancellationToken cancellationToken)
    {
        if (!IsPreviewReady)
        {
            _onSummaryUpdated("Очистка невозможна без предварительного расчёта и показа списка файлов (нажмите «Рассчитать предпросмотр»).");
            return;
        }

        var progress = new Progress<ProgressReport>(_onProgress);
        long totalFreed = 0;
        var totalQuarantined = 0;
        var modulesSucceeded = 0;

        var options = new CleanupExecutionOptions(
            MoveToQuarantine: MoveToQuarantine,
            AllowPermanentDeletion: !MoveToQuarantine,
            PermanentDeletionConfirmationPhrase: PermanentDeletePhraseInput,
            EnsureRestorePointCreated: true);

        foreach (var modVm in Modules.Where(m => m.IsSelected && m.LatestPreview is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var res = await modVm.Module.ExecuteAsync(modVm.LatestPreview!, options, progress, cancellationToken);
            if (!res.Succeeded && res.ProcessedItemsCount == 0 && !string.IsNullOrWhiteSpace(res.SummaryRu))
            {
                _onSummaryUpdated(res.SummaryRu);
                return;
            }

            totalFreed += res.FreedBytes;
            totalQuarantined += res.QuarantinedItemsCount;
            if (res.Succeeded)
            {
                modulesSucceeded++;
            }
        }

        IsPreviewReady = false;
        var rpNote = _safetyGuard.HasSessionRestorePoint ? "Создана точка восстановления" : "Точка восстановления проверена";
        _onSummaryUpdated($"Освобождено {FileSizeFormatter.FormatRussian(totalFreed)} · Помещено в карантин: {totalQuarantined} файлов · Выполнено модулей: {modulesSucceeded} · {rpNote}");
    }

    public async Task ScanLargeAndDuplicatesInternalAsync(CancellationToken cancellationToken)
    {
        var progress = new Progress<ProgressReport>(_onProgress);
        LargeFiles.Clear();
        DuplicateGroups.Clear();

        var large = await _largeFileScanner.FindLargeFilesAsync(
            ScanDirectoryForLargeFiles,
            minSizeBytes: 50L * 1024 * 1024,
            progress,
            cancellationToken);

        foreach (var item in large)
        {
            LargeFiles.Add(item);
        }

        var dups = await _largeFileScanner.FindDuplicateFilesAsync(
            ScanDirectoryForLargeFiles,
            minSizeBytes: 5L * 1024 * 1024,
            progress,
            cancellationToken);

        foreach (var group in dups)
        {
            DuplicateGroups.Add(group);
        }

        _onSummaryUpdated($"Анализ завершён (только просмотр): найдено крупных файлов (>50 МБ): {LargeFiles.Count}, групп дубликатов: {DuplicateGroups.Count}. Удаление выполняется только вручную.");
    }
}
