using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;

namespace WinRepair.App.ViewModels;

public enum AppNavigationSection
{
    Overview = 0,
    Cleanup = 1,
    Repair = 2,
    Optimization = 3,
    Programs = 4,
    Journal = 5,
    Settings = 6
}

/// <summary>
/// Главная ViewModel приложения «Ремонт и очистка Windows» (MVVM, без логики в code-behind).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISchedulerAndProfileService _profileService;
    private CancellationTokenSource? _activeCts;

    public OverviewViewModel Overview { get; }
    public CleanupViewModel Cleanup { get; }
    public RepairViewModel Repair { get; }
    public OptimizationViewModel Optimization { get; }
    public ProgramsViewModel Programs { get; }
    public JournalViewModel Journal { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private AppNavigationSection _currentSection = AppNavigationSection.Overview;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _currentProgressPercent;

    [ObservableProperty]
    private string _currentProgressBannerRu = "Система готова к диагностике и обслуживанию";

    [ObservableProperty]
    private string _summaryBannerRu = "Добро пожаловать в «Ремонт и очистка Windows» · Все операции защищены точкой восстановления и карантином";

    [ObservableProperty]
    private bool _isFirstRunDialogVisible;

    [ObservableProperty]
    private double _uiFontScale = 1.0;

    public Action<string>? ThemeChangeRequested { get; set; }

    public MainViewModel(
        IDiagnosticService diagnosticService,
        IEnumerable<ICleanupModule> cleanupModules,
        IEnumerable<IRepairModule> repairModules,
        ILargeAndDuplicateFileScanner largeFileScanner,
        IOptimizationService optimizationService,
        IProgramManagerService programManagerService,
        IOperationJournalService journalService,
        IQuarantineService quarantineService,
        IReportService reportService,
        ISessionSafetyGuard safetyGuard,
        ISchedulerAndProfileService profileService)
    {
        _profileService = profileService;

        var cleanupList = cleanupModules.ToList();
        var repairList = repairModules.ToList();

        Overview = new OverviewViewModel(
            diagnosticService,
            cleanupList,
            repairList,
            safetyGuard,
            OnOperationProgress,
            UpdateSummary);

        Cleanup = new CleanupViewModel(
            cleanupList,
            largeFileScanner,
            safetyGuard,
            OnOperationProgress,
            UpdateSummary);

        Repair = new RepairViewModel(
            repairList,
            OnOperationProgress,
            UpdateSummary);

        Optimization = new OptimizationViewModel(
            optimizationService,
            UpdateSummary);

        Programs = new ProgramsViewModel(
            programManagerService,
            OnOperationProgress,
            UpdateSummary);

        Journal = new JournalViewModel(
            journalService,
            quarantineService,
            reportService,
            () => Overview.LatestScanResult,
            UpdateSummary);

        Settings = new SettingsViewModel(
            profileService,
            theme => ThemeChangeRequested?.Invoke(theme),
            scale => UiFontScale = scale,
            UpdateSummary);
    }

    public async Task InitializeAsync()
    {
        var profile = await _profileService.LoadProfileAsync();
        IsFirstRunDialogVisible = !profile.IsFirstRunCompleted;
        UiFontScale = profile.FontScaleFactor;
        await Settings.LoadProfileAsync(CancellationToken.None);
        await Journal.RefreshHistoryAndQuarantineAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task CompleteFirstRunAsync()
    {
        IsFirstRunDialogVisible = false;
        var profile = await _profileService.LoadProfileAsync();
        await _profileService.SaveProfileAsync(profile with { IsFirstRunCompleted = true });
    }

    [RelayCommand]
    private async Task NavigateAsync(string sectionName)
    {
        if (Enum.TryParse<AppNavigationSection>(sectionName, ignoreCase: true, out var parsed))
        {
            CurrentSection = parsed;

            if (parsed == AppNavigationSection.Optimization && Optimization.StartupEntries.Count == 0)
            {
                await ExecuteGuardedAsync(Optimization.RefreshAllAsync);
            }
            else if (parsed == AppNavigationSection.Programs && Programs.FilteredPrograms.Count == 0)
            {
                await ExecuteGuardedAsync(Programs.LoadInstalledProgramsAsync);
            }
            else if (parsed == AppNavigationSection.Journal)
            {
                await ExecuteGuardedAsync(Journal.RefreshHistoryAndQuarantineAsync);
            }
        }
    }

    [RelayCommand]
    private void CancelActiveOperation()
    {
        _activeCts?.Cancel();
        CurrentProgressBannerRu = "Запрошена безопасная отмена текущей операции...";
    }

    // Команды экрана «Обзор»
    [RelayCommand]
    private Task RunDiagnosticScanAsync() => ExecuteGuardedAsync(Overview.RunScanInternalAsync);

    [RelayCommand]
    private Task FixAllSafeIssuesAsync() => ExecuteGuardedAsync(Overview.FixAllSafeInternalAsync);

    // Команды экрана «Очистка»
    [RelayCommand]
    private Task PreviewCleanupAsync() => ExecuteGuardedAsync(Cleanup.BuildPreviewInternalAsync);

    [RelayCommand]
    private Task ExecuteCleanupAsync() => ExecuteGuardedAsync(Cleanup.ExecuteApprovedCleanupInternalAsync);

    [RelayCommand]
    private Task ScanLargeAndDuplicatesAsync() => ExecuteGuardedAsync(Cleanup.ScanLargeAndDuplicatesInternalAsync);

    // Команды экрана «Восстановление»
    [RelayCommand]
    private Task PrepareRepairPreviewAsync() => ExecuteGuardedAsync(Repair.PrepareSelectedPreviewAsync);

    [RelayCommand]
    private Task ExecuteRepairAsync() => ExecuteGuardedAsync(Repair.ExecuteSelectedRepairAsync);

    // Команды экрана «Оптимизация»
    [RelayCommand]
    private Task RefreshOptimizationAsync() => ExecuteGuardedAsync(Optimization.RefreshAllAsync);

    [RelayCommand]
    private Task TrimMemoryWorkingSetAsync() => ExecuteGuardedAsync(Optimization.TrimWorkingSetAsync);

    [RelayCommand]
    private Task OptimizeVisualFxPerformanceAsync() => ExecuteGuardedAsync(ct => Optimization.ApplyVisualEffectsAsync(true, ct));

    [RelayCommand]
    private Task RestoreDefaultVisualFxAsync() => ExecuteGuardedAsync(ct => Optimization.ApplyVisualEffectsAsync(false, ct));

    // Команды экрана «Программы»
    [RelayCommand]
    private Task LoadProgramsAsync() => ExecuteGuardedAsync(Programs.LoadInstalledProgramsAsync);

    [RelayCommand]
    private Task UninstallSelectedProgramAsync() => ExecuteGuardedAsync(Programs.LaunchUninstallerForSelectedAsync);

    [RelayCommand]
    private Task ScanProgramRemnantsAsync() => ExecuteGuardedAsync(Programs.ScanRemnantsAsync);

    [RelayCommand]
    private Task RemoveConfirmedRemnantsAsync() => ExecuteGuardedAsync(Programs.RemoveConfirmedRemnantsAsync);

    // Команды экрана «Журнал»
    [RelayCommand]
    private Task RollbackSelectedOperationAsync() => ExecuteGuardedAsync(Journal.RollbackSelectedOperationAsync);

    [RelayCommand]
    private Task RestoreSelectedQuarantineBatchAsync() => ExecuteGuardedAsync(Journal.RestoreSelectedQuarantineBatchAsync);

    [RelayCommand]
    private Task ExportHtmlReportAsync() => ExecuteGuardedAsync(Journal.ExportHtmlReportAsync);

    [RelayCommand]
    private Task ExportJsonReportAsync() => ExecuteGuardedAsync(Journal.ExportJsonReportAsync);

    // Команды экрана «Настройки»
    [RelayCommand]
    private Task SaveSettingsAsync() => ExecuteGuardedAsync(Settings.SaveSettingsAndScheduleAsync);

    private async Task ExecuteGuardedAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        _activeCts = new CancellationTokenSource();

        try
        {
            await operation(_activeCts.Token);
        }
        catch (OperationCanceledException)
        {
            SummaryBannerRu = "Операция была безопасно отменена пользователем.";
            CurrentProgressBannerRu = "Операция отменена";
        }
        catch (Exception ex)
        {
            SummaryBannerRu = $"Ошибка при выполнении операции: {ex.Message}";
        }
        finally
        {
            _activeCts.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    private void OnOperationProgress(ProgressReport report)
    {
        CurrentProgressPercent = Math.Clamp(report.Percentage, 0, 100);
        CurrentProgressBannerRu = report.FormattedBannerRu;
    }

    private void UpdateSummary(string summaryRu)
    {
        SummaryBannerRu = summaryRu;
    }
}
