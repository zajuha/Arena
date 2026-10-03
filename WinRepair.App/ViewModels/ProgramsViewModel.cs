using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;

namespace WinRepair.App.ViewModels;

/// <summary>
/// ViewModel раздела «Программы»: установленные приложения из реестра Uninstall,
/// запуск штатного деинсталлятора и поиск остатков удалённых программ (п. 3.5 ТЗ).
/// </summary>
public sealed partial class ProgramsViewModel : ObservableObject
{
    private readonly IProgramManagerService _programManager;
    private readonly Action<ProgressReport> _onProgress;
    private readonly Action<string> _onSummaryUpdated;
    private List<InstalledProgramEntry> _allPrograms = [];

    public ObservableCollection<InstalledProgramEntry> FilteredPrograms { get; } = [];
    public ObservableCollection<ProgramRemnantEntry> DiscoveredRemnants { get; } = [];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private InstalledProgramEntry? _selectedProgram;

    public ProgramsViewModel(
        IProgramManagerService programManager,
        Action<ProgressReport> onProgress,
        Action<string> onSummaryUpdated)
    {
        _programManager = programManager;
        _onProgress = onProgress;
        _onSummaryUpdated = onSummaryUpdated;
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplySearchFilter();
    }

    public async Task LoadInstalledProgramsAsync(CancellationToken cancellationToken)
    {
        _allPrograms = (await _programManager.GetInstalledProgramsAsync(cancellationToken)).ToList();
        ApplySearchFilter();
        _onSummaryUpdated($"Найдено установленных программ: {_allPrograms.Count}.");
    }

    public async Task LaunchUninstallerForSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedProgram is null)
        {
            return;
        }

        var (_, msg) = await _programManager.LaunchStandardUninstallerAsync(SelectedProgram, cancellationToken);
        _onSummaryUpdated(msg);
    }

    public async Task ScanRemnantsAsync(CancellationToken cancellationToken)
    {
        var progress = new Progress<ProgressReport>(_onProgress);
        DiscoveredRemnants.Clear();

        var found = await _programManager.ScanForUninstalledProgramRemnantsAsync(progress, cancellationToken);
        foreach (var item in found)
        {
            DiscoveredRemnants.Add(item);
        }

        _onSummaryUpdated($"Проверка остатков удалённых программ завершена: найдено элементов для подтверждения: {DiscoveredRemnants.Count}.");
    }

    public async Task RemoveConfirmedRemnantsAsync(CancellationToken cancellationToken)
    {
        var list = DiscoveredRemnants.Where(r => r.IsSelected).ToList();
        var (_, _, msg) = await _programManager.RemoveSelectedRemnantsAsync(list, moveToQuarantine: true, cancellationToken);
        await ScanRemnantsAsync(cancellationToken);
        _onSummaryUpdated(msg);
    }

    private void ApplySearchFilter()
    {
        FilteredPrograms.Clear();
        var q = SearchQuery.Trim();

        var source = string.IsNullOrEmpty(q)
            ? _allPrograms
            : _allPrograms.Where(p =>
                p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var p in source)
        {
            FilteredPrograms.Add(p);
        }
    }
}
