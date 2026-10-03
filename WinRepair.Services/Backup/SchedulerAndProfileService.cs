using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;
using WinRepair.Services.Infrastructure;

namespace WinRepair.Services.Backup;

/// <summary>
/// Сервис настройки планировщика автоматической очистки (Windows Task Scheduler)
/// и экспорта/импорта профиля настроек пользователя (п. 3.6 ТЗ).
/// </summary>
public sealed class SchedulerAndProfileService : ISchedulerAndProfileService
{
    private const string ScheduledTaskName = @"\WinRepair\Автоматическая очистка системы";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<SchedulerAndProfileService> _logger;
    private readonly string _profileFilePath;

    public SchedulerAndProfileService(
        IProcessRunner processRunner,
        ILogger<SchedulerAndProfileService> logger,
        string? customConfigDirectory = null)
    {
        _processRunner = processRunner;
        _logger = logger;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\ProgramData";
        }

        var configDir = customConfigDirectory ?? Path.Combine(programData, "WinRepair", "Config");
        Directory.CreateDirectory(configDir);
        _profileFilePath = Path.Combine(configDir, "profile.json");
    }

    public async Task<(bool Succeeded, string MessageRu)> ConfigureScheduledCleanupTaskAsync(
        CleanupScheduleConfig config,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (!config.IsEnabled || config.Frequency == ScheduleFrequency.Disabled)
        {
            var deleteRes = await _processRunner.RunAsync(
                "schtasks.exe",
                $"/Delete /TN \"{ScheduledTaskName}\" /F",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Расписание автоматической очистки отключено (код schtasks: {Code})", deleteRes.ExitCode);
            return (true, "Автоматическая очистка по расписанию отключена в Планировщике заданий Windows.");
        }

        var scheduleFlag = config.Frequency switch
        {
            ScheduleFrequency.Daily => "/SC DAILY",
            ScheduleFrequency.Weekly => $"/SC WEEKLY /D {ToSchtasksDay(config.DayOfWeek)}",
            ScheduleFrequency.Monthly => "/SC MONTHLY /D 1",
            _ => "/SC WEEKLY /D SUN"
        };

        var startTime = $"{Math.Clamp(config.HourOfDay, 0, 23):D2}:{Math.Clamp(config.MinuteOfHour, 0, 59):D2}";
        var safeExePath = string.IsNullOrWhiteSpace(executablePath)
            ? @"C:\Program Files\WinRepair\WinRepair.App.exe"
            : executablePath;

        var arguments = $"/Create /TN \"{ScheduledTaskName}\" /TR \"\\\"{safeExePath}\\\" --scheduled-safe-clean\" {scheduleFlag} /ST {startTime} /RL HIGHEST /F";
        var res = await _processRunner.RunAsync("schtasks.exe", arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (res.ExitCode == 0)
        {
            _logger.LogInformation("Задача автоматической очистки зарегистрирована в Планировщике заданий на {Time}", startTime);
            return (true, $"Задача «{ScheduledTaskName}» успешно настроена в Планировщике заданий Windows (время запуска: {startTime}).");
        }

        return (false, $"Не удалось создать задание в Планировщике Windows: {res.CombinedOutput.Trim()}");
    }

    public async Task<AppSettingsProfile> LoadProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_profileFilePath))
        {
            var defaultProfile = AppSettingsProfile.CreateDefault();
            await SaveProfileAsync(defaultProfile, cancellationToken).ConfigureAwait(false);
            return defaultProfile;
        }

        try
        {
            await using var stream = File.OpenRead(_profileFilePath);
            var profile = await JsonSerializer.DeserializeAsync<AppSettingsProfile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return profile ?? AppSettingsProfile.CreateDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось загрузить профиль настроек, используются значения по умолчанию");
            return AppSettingsProfile.CreateDefault();
        }
    }

    public async Task SaveProfileAsync(AppSettingsProfile profile, CancellationToken cancellationToken = default)
    {
        var updated = profile with { ExportedAtUtc = DateTimeOffset.UtcNow };
        await using var stream = File.Create(_profileFilePath);
        await JsonSerializer.SerializeAsync(stream, updated, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportProfileToFileAsync(
        AppSettingsProfile profile,
        string destinationFilePath,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var updated = profile with { ExportedAtUtc = DateTimeOffset.UtcNow };
        await using var stream = File.Create(destinationFilePath);
        await JsonSerializer.SerializeAsync(stream, updated, JsonOptions, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Профиль настроек экспортирован в {File}", destinationFilePath);
    }

    public async Task<AppSettingsProfile> ImportProfileFromFileAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(sourceFilePath);
        var imported = await JsonSerializer.DeserializeAsync<AppSettingsProfile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException("Файл профиля настроек повреждён или имеет неверный формат.");

        await SaveProfileAsync(imported, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Профиль настроек импортирован из {File}", sourceFilePath);
        return imported;
    }

    private static string ToSchtasksDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN"
    };
}
