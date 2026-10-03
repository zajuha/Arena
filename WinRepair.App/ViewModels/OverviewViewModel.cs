using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.App.ViewModels;

/// <summary>
/// ViewModel экрана «Обзор»: оценка состояния (0..100), плитки категорий,
/// кнопки «Проверить» и «Исправить всё безопасное» (п. 3.1 и п. 5 ТЗ).
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly IDiagnosticService _diagnosticService;
    private readonly IEnumerable<ICleanupModule> _cleanupModules;
    private readonly IEnumerable<IRepairModule> _repairModules;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly Action<ProgressReport> _onProgress;
    private readonly Action<string> _onSummaryUpdated;

    [ObservableProperty]
    private int _overallScore = 92;

    [ObservableProperty]
    private string _overallStatusRu = "Готово к проверке состояния системы";

    [ObservableProperty]
    private string _lastCheckedAtRu = "Ещё не выполнялась в этой сессии";

    [ObservableProperty]
    private int _foundIssuesCount;

    [ObservableProperty]
    private int _safeFixableCount;

    [ObservableProperty]
    private ScanResult? _latestScanResult;

    public ObservableCollection<CategoryHealthScore> CategoryTiles { get; } = [];
    public ObservableCollection<DiagnosticIssue> DiscoveredIssues { get; } = [];
    public ObservableCollection<DiskDriveDiagnostic> Disks { get; } = [];

    public OverviewViewModel(
        IDiagnosticService diagnosticService,
        IEnumerable<ICleanupModule> cleanupModules,
        IEnumerable<IRepairModule> repairModules,
        ISessionSafetyGuard safetyGuard,
        Action<ProgressReport> onProgress,
        Action<string> onSummaryUpdated)
    {
        _diagnosticService = diagnosticService;
        _cleanupModules = cleanupModules;
        _repairModules = repairModules;
        _safetyGuard = safetyGuard;
        _onProgress = onProgress;
        _onSummaryUpdated = onSummaryUpdated;
    }

    public async Task RunScanInternalAsync(CancellationToken cancellationToken)
    {
        var progress = new Progress<ProgressReport>(_onProgress);
        var result = await _diagnosticService.RunFullScanAsync(progress, cancellationToken);

        LatestScanResult = result;
        OverallScore = result.OverallScore;
        OverallStatusRu = result.OverallStatusRu;
        LastCheckedAtRu = $"Проверено: {result.CompletedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss} (за {result.Duration.TotalSeconds:F1} сек.)";
        FoundIssuesCount = result.TotalIssuesCount;
        SafeFixableCount = result.SafeAutoFixableIssuesCount;

        CategoryTiles.Clear();
        foreach (var cat in result.CategoryScores)
        {
            CategoryTiles.Add(cat);
        }

        DiscoveredIssues.Clear();
        foreach (var issue in result.Issues)
        {
            DiscoveredIssues.Add(issue);
        }

        Disks.Clear();
        foreach (var disk in result.Disks)
        {
            Disks.Add(disk);
        }

        _onSummaryUpdated($"Диагностика завершена · Оценка состояния: {result.OverallScore}/100 · Найдено замечаний: {result.TotalIssuesCount} (безопасных к автоисправлению: {result.SafeAutoFixableIssuesCount})");
    }

    public async Task FixAllSafeInternalAsync(CancellationToken cancellationToken)
    {
        var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync("Исправить всё безопасное", cancellationToken);
        if (!allowed)
        {
            _onSummaryUpdated(reasonRu);
            return;
        }

        var progress = new Progress<ProgressReport>(_onProgress);
        long totalFreedBytes = 0;
        var fixedProblems = 0;

        // 1. Выполняем все полностью безопасные модули очистки (с автоматическим формированием предпросмотра и помещением в карантин)
        foreach (var module in _cleanupModules.Where(m => m.Risk == RiskLevel.Safe))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await module.ScanAsync(progress, cancellationToken);
            if (items.Count == 0)
            {
                continue;
            }

            var preview = await module.PreviewAsync(items, cancellationToken);
            var execResult = await module.ExecuteAsync(
                preview,
                new CleanupExecutionOptions(MoveToQuarantine: true, AllowPermanentDeletion: false, EnsureRestorePointCreated: true),
                progress,
                cancellationToken);

            if (execResult.Succeeded)
            {
                totalFreedBytes += execResult.FreedBytes;
                fixedProblems++;
            }
        }

        // 2. Выполняем безопасные задачи восстановления из найденных проблем
        var safeIssueModules = DiscoveredIssues
            .Where(i => i.CanAutoFixSafely && !string.IsNullOrWhiteSpace(i.RelatedModuleId))
            .Select(i => i.RelatedModuleId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var moduleId in safeIssueModules)
        {
            var repairMod = _repairModules.FirstOrDefault(r =>
                string.Equals(r.Id, moduleId, StringComparison.OrdinalIgnoreCase) &&
                r.Risk.IsEligibleForSafeAutoFix());

            if (repairMod is null)
            {
                continue;
            }

            var preview = await repairMod.PreviewAsync(cancellationToken);
            var res = await repairMod.ExecuteAsync(preview, new RepairExecutionOptions(EnsureRestorePointCreated: true), progress, cancellationToken);
            if (res.Succeeded)
            {
                fixedProblems++;
            }
        }

        var rpStatus = _safetyGuard.HasSessionRestorePoint ? "Создана точка восстановления" : "Без точки восстановления";
        _onSummaryUpdated($"Освобождено {FileSizeFormatter.FormatRussian(totalFreedBytes)} · Исправлено проблем: {fixedProblems} · {rpStatus}");
    }
}
