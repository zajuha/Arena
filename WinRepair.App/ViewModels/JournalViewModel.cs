using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;

namespace WinRepair.App.ViewModels;

/// <summary>
/// ViewModel раздела «Журнал»: полный журнал операций с возможностью отката,
/// управление карантином и экспорт отчётов в HTML/JSON (п. 3.6 и п. 4.8 ТЗ).
/// </summary>
public sealed partial class JournalViewModel : ObservableObject
{
    private readonly IOperationJournalService _journalService;
    private readonly IQuarantineService _quarantineService;
    private readonly IReportService _reportService;
    private readonly Func<ScanResult?> _latestScanProvider;
    private readonly Action<string> _onSummaryUpdated;

    public ObservableCollection<OperationLog> Operations { get; } = [];
    public ObservableCollection<QuarantineBatchManifest> QuarantineBatches { get; } = [];

    [ObservableProperty]
    private OperationLog? _selectedOperation;

    [ObservableProperty]
    private QuarantineBatchManifest? _selectedQuarantineBatch;

    public string QuarantineRootPath => _quarantineService.QuarantineRootDirectory;

    public JournalViewModel(
        IOperationJournalService journalService,
        IQuarantineService quarantineService,
        IReportService reportService,
        Func<ScanResult?> latestScanProvider,
        Action<string> onSummaryUpdated)
    {
        _journalService = journalService;
        _quarantineService = quarantineService;
        _reportService = reportService;
        _latestScanProvider = latestScanProvider;
        _onSummaryUpdated = onSummaryUpdated;
    }

    public async Task RefreshHistoryAndQuarantineAsync(CancellationToken cancellationToken)
    {
        Operations.Clear();
        foreach (var op in await _journalService.GetHistoryAsync(cancellationToken))
        {
            Operations.Add(op);
        }

        QuarantineBatches.Clear();
        foreach (var batch in await _quarantineService.GetQuarantineBatchesAsync(cancellationToken))
        {
            QuarantineBatches.Add(batch);
        }
    }

    public async Task RollbackSelectedOperationAsync(CancellationToken cancellationToken)
    {
        if (SelectedOperation is null)
        {
            return;
        }

        var (_, msg) = await _journalService.RollbackOperationAsync(SelectedOperation.Id, cancellationToken);
        await RefreshHistoryAndQuarantineAsync(cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task RestoreSelectedQuarantineBatchAsync(CancellationToken cancellationToken)
    {
        if (SelectedQuarantineBatch is null)
        {
            return;
        }

        var (_, _, msg) = await _quarantineService.RestoreBatchAsync(SelectedQuarantineBatch.BatchId, cancellationToken);
        await RefreshHistoryAndQuarantineAsync(cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task ExportHtmlReportAsync(CancellationToken cancellationToken)
    {
        var reportsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinRepair",
            "Reports");

        Directory.CreateDirectory(reportsDir);
        var path = Path.Combine(reportsDir, $"Отчёт_WinRepair_{DateTime.Now:yyyyMMdd_HHmmss}.html");

        await _reportService.ExportHtmlReportAsync(_latestScanProvider(), Operations.ToList(), path, cancellationToken);

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // Игнорируем ошибку автооткрытия браузера
        }

        _onSummaryUpdated($"HTML-отчёт сформирован и открыт в браузере: {path}");
    }

    public async Task ExportJsonReportAsync(CancellationToken cancellationToken)
    {
        var reportsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinRepair",
            "Reports");

        Directory.CreateDirectory(reportsDir);
        var path = Path.Combine(reportsDir, $"Отчёт_WinRepair_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        await _reportService.ExportJsonReportAsync(_latestScanProvider(), Operations.ToList(), path, cancellationToken);
        _onSummaryUpdated($"JSON-отчёт успешно экспортирован: {path}");
    }
}
