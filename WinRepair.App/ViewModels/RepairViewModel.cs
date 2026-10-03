using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.App.ViewModels;

public sealed partial class RepairModuleItemViewModel : ObservableObject
{
    public IRepairModule Module { get; }

    public string Id => Module.Id;
    public string NameRu => Module.NameRu;
    public string DescriptionRu => Module.DescriptionRu;
    public string RiskDisplayRu => Module.Risk.ToRussianDisplayName();
    public bool IsHighRisk => Module.Risk == RiskLevel.High;
    public string? ConfirmationPhraseRu => Module.ConfirmationPhraseRu;

    [ObservableProperty]
    private string _scanStateRu = "Нажмите «Проверить» для диагностики подсистемы";

    [ObservableProperty]
    private RepairPreview? _currentPreview;

    public RepairModuleItemViewModel(IRepairModule module)
    {
        Module = module;
    }
}

/// <summary>
/// ViewModel раздела «Восстановление»: 9 модулей восстановления ОС, предпросмотр команд
/// и двойное подтверждение с ручным вводом фразы для операций высокого риска (п. 3.3 и п. 4.6 ТЗ).
/// </summary>
public sealed partial class RepairViewModel : ObservableObject
{
    private readonly Action<ProgressReport> _onProgress;
    private readonly Action<string> _onSummaryUpdated;

    public ObservableCollection<RepairModuleItemViewModel> Modules { get; } = [];

    [ObservableProperty]
    private RepairModuleItemViewModel? _selectedModule;

    [ObservableProperty]
    private string _confirmationPhraseInput = string.Empty;

    [ObservableProperty]
    private string _customInstallWimPath = string.Empty;

    [ObservableProperty]
    private bool _scheduleChkdskOnReboot;

    [ObservableProperty]
    private string _lastRepairOutputLogRu = string.Empty;

    public RepairViewModel(
        IEnumerable<IRepairModule> repairModules,
        Action<ProgressReport> onProgress,
        Action<string> onSummaryUpdated)
    {
        _onProgress = onProgress;
        _onSummaryUpdated = onSummaryUpdated;

        foreach (var m in repairModules)
        {
            Modules.Add(new RepairModuleItemViewModel(m));
        }

        SelectedModule = Modules.FirstOrDefault();
    }

    public async Task PrepareSelectedPreviewAsync(CancellationToken cancellationToken)
    {
        if (SelectedModule is null)
        {
            return;
        }

        var progress = new Progress<ProgressReport>(_onProgress);
        var finding = await SelectedModule.Module.ScanAsync(progress, cancellationToken);
        SelectedModule.ScanStateRu = finding.CurrentStateRu;
        SelectedModule.CurrentPreview = await SelectedModule.Module.PreviewAsync(cancellationToken);

        _onSummaryUpdated($"Сформирован план восстановления «{SelectedModule.NameRu}»: {SelectedModule.ScanStateRu}");
    }

    public async Task ExecuteSelectedRepairAsync(CancellationToken cancellationToken)
    {
        if (SelectedModule is null)
        {
            return;
        }

        var preview = SelectedModule.CurrentPreview ?? await SelectedModule.Module.PreviewAsync(cancellationToken);
        SelectedModule.CurrentPreview = preview;

        var progress = new Progress<ProgressReport>(_onProgress);
        var options = new RepairExecutionOptions(
            UserConfirmationPhrase: ConfirmationPhraseInput,
            CustomInstallWimSourcePath: string.IsNullOrWhiteSpace(CustomInstallWimPath) ? null : CustomInstallWimPath,
            ScheduleChkdskOnNextReboot: ScheduleChkdskOnReboot,
            EnsureRestorePointCreated: true);

        var res = await SelectedModule.Module.ExecuteAsync(preview, options, progress, cancellationToken);
        LastRepairOutputLogRu = res.DetailedOutputRu;
        ConfirmationPhraseInput = string.Empty;

        _onSummaryUpdated(res.SummaryRu);
    }
}
