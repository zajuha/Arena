using CommunityToolkit.Mvvm.ComponentModel;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;

namespace WinRepair.App.ViewModels;

/// <summary>
/// ViewModel раздела «Настройки»: тема оформления (Светлая/Тёмная), масштаб шрифта,
/// срок хранения карантина, планировщик автоматической очистки и экспорт/импорт профиля (п. 3.6 и п. 5 ТЗ).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISchedulerAndProfileService _profileService;
    private readonly Action<string> _onThemeChanged;
    private readonly Action<double> _onFontScaleChanged;
    private readonly Action<string> _onSummaryUpdated;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    [ObservableProperty]
    private double _fontScaleFactor = 1.0;

    [ObservableProperty]
    private bool _useQuarantineByDefault = true;

    [ObservableProperty]
    private int _quarantineRetentionDays = 14;

    [ObservableProperty]
    private bool _scheduleEnabled;

    [ObservableProperty]
    private int _scheduleHour = 12;

    [ObservableProperty]
    private int _scheduleMinute;

    public SettingsViewModel(
        ISchedulerAndProfileService profileService,
        Action<string> onThemeChanged,
        Action<double> onFontScaleChanged,
        Action<string> onSummaryUpdated)
    {
        _profileService = profileService;
        _onThemeChanged = onThemeChanged;
        _onFontScaleChanged = onFontScaleChanged;
        _onSummaryUpdated = onSummaryUpdated;
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        _onThemeChanged(value ? "Dark" : "Light");
    }

    partial void OnFontScaleFactorChanged(double value)
    {
        _onFontScaleChanged(Math.Clamp(value, 0.9, 1.4));
    }

    public async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await _profileService.LoadProfileAsync(cancellationToken);
        IsDarkTheme = !string.Equals(profile.ThemeName, "Light", StringComparison.OrdinalIgnoreCase);
        FontScaleFactor = profile.FontScaleFactor;
        UseQuarantineByDefault = profile.UseQuarantineByDefault;
        QuarantineRetentionDays = profile.QuarantineRetentionDays;
        ScheduleEnabled = profile.Schedule.IsEnabled;
        ScheduleHour = profile.Schedule.HourOfDay;
        ScheduleMinute = profile.Schedule.MinuteOfHour;
    }

    public async Task SaveSettingsAndScheduleAsync(CancellationToken cancellationToken)
    {
        var current = BuildProfileObject();
        await _profileService.SaveProfileAsync(current, cancellationToken);

        var exePath = Environment.ProcessPath ?? @"C:\Program Files\WinRepair\WinRepair.App.exe";
        var (_, schedMsg) = await _profileService.ConfigureScheduledCleanupTaskAsync(current.Schedule, exePath, cancellationToken);

        _onSummaryUpdated($"Настройки сохранены · {schedMsg}");
    }

    public async Task ExportProfileAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var profile = BuildProfileObject();
        await _profileService.ExportProfileToFileAsync(profile, destinationPath, cancellationToken);
        _onSummaryUpdated($"Профиль настроек успешно экспортирован в файл: {destinationPath}");
    }

    public async Task ImportProfileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var imported = await _profileService.ImportProfileFromFileAsync(sourcePath, cancellationToken);
        IsDarkTheme = !string.Equals(imported.ThemeName, "Light", StringComparison.OrdinalIgnoreCase);
        FontScaleFactor = imported.FontScaleFactor;
        UseQuarantineByDefault = imported.UseQuarantineByDefault;
        QuarantineRetentionDays = imported.QuarantineRetentionDays;
        ScheduleEnabled = imported.Schedule.IsEnabled;
        ScheduleHour = imported.Schedule.HourOfDay;
        ScheduleMinute = imported.Schedule.MinuteOfHour;

        _onSummaryUpdated($"Профиль настроек импортирован из файла: {sourcePath}");
    }

    private AppSettingsProfile BuildProfileObject() => new(
        ProfileVersion: "1.0.0",
        ThemeName: IsDarkTheme ? "Dark" : "Light",
        FontScaleFactor: FontScaleFactor,
        IsFirstRunCompleted: true,
        UseQuarantineByDefault: UseQuarantineByDefault,
        QuarantineRetentionDays: Math.Clamp(QuarantineRetentionDays, 1, 90),
        AutoCreateRestorePointInSession: true,
        Schedule: new CleanupScheduleConfig(
            IsEnabled: ScheduleEnabled,
            Frequency: ScheduleEnabled ? ScheduleFrequency.Weekly : ScheduleFrequency.Disabled,
            HourOfDay: Math.Clamp(ScheduleHour, 0, 23),
            MinuteOfHour: Math.Clamp(ScheduleMinute, 0, 59),
            DayOfWeek: DayOfWeek.Sunday,
            EnabledCategories:
            [
                CleanupCategory.SystemJunk,
                CleanupCategory.BrowserCache,
                CleanupCategory.ThumbnailsAndIcons,
                CleanupCategory.DumpsAndLogs
            ],
            CreateRestorePointBeforeScheduledRun: true),
        ExportedAtUtc: DateTimeOffset.UtcNow);
}
