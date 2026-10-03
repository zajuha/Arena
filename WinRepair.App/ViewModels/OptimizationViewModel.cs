using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;

namespace WinRepair.App.ViewModels;

/// <summary>
/// ViewModel раздела «Оптимизация»: автозагрузка, службы, задачи планировщика,
/// схемы питания, визуальные эффекты и честная очистка рабочего набора памяти (п. 3.4 ТЗ).
/// </summary>
public sealed partial class OptimizationViewModel : ObservableObject
{
    private readonly IOptimizationService _optimizationService;
    private readonly Action<string> _onSummaryUpdated;

    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];
    public ObservableCollection<ServiceOptimizationEntry> Services { get; } = [];
    public ObservableCollection<ScheduledTaskEntry> ScheduledTasks { get; } = [];
    public ObservableCollection<PowerPlanEntry> PowerPlans { get; } = [];

    [ObservableProperty]
    private string _memoryTrimTechnicalNoteRu =
        "Очистка рабочего набора вызывает системную функцию Win32 EmptyWorkingSet для освобождения неактивных страниц ОЗУ без фиктивных «ускорителей».";

    public OptimizationViewModel(
        IOptimizationService optimizationService,
        Action<string> onSummaryUpdated)
    {
        _optimizationService = optimizationService;
        _onSummaryUpdated = onSummaryUpdated;
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        StartupEntries.Clear();
        foreach (var s in await _optimizationService.GetStartupEntriesAsync(cancellationToken))
        {
            StartupEntries.Add(s);
        }

        Services.Clear();
        foreach (var svc in await _optimizationService.GetOptimizableServicesAsync(cancellationToken))
        {
            Services.Add(svc);
        }

        ScheduledTasks.Clear();
        foreach (var task in await _optimizationService.GetNonCriticalScheduledTasksAsync(cancellationToken))
        {
            ScheduledTasks.Add(task);
        }

        PowerPlans.Clear();
        foreach (var plan in await _optimizationService.GetPowerPlansAsync(cancellationToken))
        {
            PowerPlans.Add(plan);
        }

        _onSummaryUpdated($"Загружено элементов автозагрузки: {StartupEntries.Count}, служб: {Services.Count}, задач: {ScheduledTasks.Count}.");
    }

    public async Task ToggleStartupAsync(StartupEntry entry, CancellationToken cancellationToken)
    {
        var (_, msg) = await _optimizationService.SetStartupEntryEnabledAsync(entry, !entry.IsEnabled, cancellationToken);
        await RefreshAllAsync(cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task ApplyRecommendedServiceModeAsync(ServiceOptimizationEntry service, CancellationToken cancellationToken)
    {
        var (_, msg) = await _optimizationService.ApplyServiceStartModeAsync(service, service.RecommendedStartMode, cancellationToken);
        await RefreshAllAsync(cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task ToggleScheduledTaskAsync(ScheduledTaskEntry task, CancellationToken cancellationToken)
    {
        var (_, msg) = await _optimizationService.SetScheduledTaskEnabledAsync(task, !task.IsEnabled, cancellationToken);
        await RefreshAllAsync(cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task ActivatePowerPlanAsync(PowerPlanEntry plan, CancellationToken cancellationToken)
    {
        var (_, msg) = await _optimizationService.ActivatePowerPlanAsync(plan.SchemeGuid, cancellationToken);
        await RefreshAllAsync(cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task ApplyVisualEffectsAsync(bool bestPerformance, CancellationToken cancellationToken)
    {
        var (_, msg) = await _optimizationService.ApplyVisualEffectsProfileAsync(bestPerformance, cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task TrimWorkingSetAsync(CancellationToken cancellationToken)
    {
        var summary = await _optimizationService.TrimProcessesWorkingSetAsync(cancellationToken);
        MemoryTrimTechnicalNoteRu = summary.HonestTechnicalNoteRu;
        _onSummaryUpdated($"Рабочий набор ОЗУ оптимизирован: обработано процессов {summary.TrimmedProcessesCount}, выгружено неактивных страниц: {summary.FormattedReclaimedRu}.");
    }
}
