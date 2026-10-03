using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.Core.Diagnostics;

/// <summary>
/// Детерминированный калькулятор оценки состояния системы (0..100) по 10 категориям.
/// Не генерирует фиктивных проблем: оценка снижается исключительно при наличии реальных отклонений.
/// </summary>
public static class HealthScoreCalculator
{
    public static (int OverallScore, string OverallStatusRu, IReadOnlyList<CategoryHealthScore> Categories) Calculate(
        IReadOnlyList<DiskDriveDiagnostic> disks,
        SystemFilesDiagnostic systemFiles,
        IReadOnlyList<EventLogCriticalEntry> recentEvents,
        IReadOnlyList<ServiceDiagnosticEntry> problematicServices,
        IReadOnlyList<DriverProblemEntry> driverProblems,
        NetworkDiagnosticInfo network,
        WindowsUpdateDiagnosticInfo windowsUpdate,
        BootDiagnosticInfo boot,
        PowerDiagnosticInfo power,
        SecurityDiagnosticInfo security,
        IReadOnlyList<DiagnosticIssue> issues)
    {
        var categories = new List<CategoryHealthScore>(10);

        // 1. Диски и SMART
        var diskScore = 100;
        foreach (var disk in disks)
        {
            if (disk.SmartPredictFailure)
            {
                diskScore -= 60;
            }

            if (disk.HasFileSystemErrors)
            {
                diskScore -= 30;
            }

            if (disk.FreePercent < 5.0)
            {
                diskScore -= 25;
            }
            else if (disk.FreePercent < 12.0)
            {
                diskScore -= 10;
            }

            if (disk.TemperatureCelsius is > 65)
            {
                diskScore -= 15;
            }
        }
        categories.Add(BuildCategory(HealthCategory.Disks, diskScore, issues,
            disks.Count > 0
                ? $"Проверено томов: {disks.Count}. Свободно на системном диске: {disks[0].FreePercent:F1}% ({disks[0].MediaTypeRu})."
                : "Диски проверены."));

        // 2. Целостность системных файлов
        var sysFilesScore = 100;
        if (systemFiles.IntegrityViolationsDetected)
        {
            sysFilesScore -= 35;
        }
        if (systemFiles.ComponentStoreRepairable)
        {
            sysFilesScore -= 25;
        }
        categories.Add(BuildCategory(HealthCategory.SystemFiles, sysFilesScore, issues, systemFiles.LastScanSummaryRu));

        // 3. Журнал событий Windows
        var eventScore = Math.Max(40, 100 - (recentEvents.Count * 5));
        categories.Add(BuildCategory(HealthCategory.EventLog, eventScore, issues,
            recentEvents.Count == 0
                ? "Критичных сбоев за последние 30 дней не зафиксировано."
                : $"За последние 30 дней обнаружено критических событий: {recentEvents.Count}."));

        // 4. Службы Windows
        var stoppedAutoCount = problematicServices.Count(s => s.IsStoppedUnexpectedly);
        var serviceScore = Math.Max(40, 100 - (stoppedAutoCount * 15));
        categories.Add(BuildCategory(HealthCategory.Services, serviceScore, issues,
            stoppedAutoCount == 0
                ? "Все критичные системные службы работают штатно."
                : $"Остановлено важных служб с автозапуском: {stoppedAutoCount}."));

        // 5. Драйверы и устройства
        var driverScore = Math.Max(30, 100 - (driverProblems.Count * 20));
        categories.Add(BuildCategory(HealthCategory.Drivers, driverScore, issues,
            driverProblems.Count == 0
                ? "Все устройства в Диспетчере устройств работают без ошибок."
                : $"Устройств с кодами ошибок в Диспетчере устройств: {driverProblems.Count}."));

        // 6. Сеть и DNS
        var netScore = 100;
        if (!network.HasActiveAdapter)
        {
            netScore -= 50;
        }
        else if (!network.DnsResolutionWorking)
        {
            netScore -= 35;
        }
        if (network.ProxyEnabled && !string.IsNullOrWhiteSpace(network.ProxyServer))
        {
            netScore -= 10;
        }
        categories.Add(BuildCategory(HealthCategory.Network, netScore, issues,
            network.DnsResolutionWorking
                ? $"Сетевые адаптеры активны, разрешение DNS работает ({network.DnsLatencyMs} мс)."
                : "Обнаружены проблемы с разрешением DNS или сетевым подключением."));

        // 7. Центр обновления Windows
        var wuScore = 100;
        if (windowsUpdate.RecentUpdateErrorsCount > 0)
        {
            wuScore -= Math.Min(40, windowsUpdate.RecentUpdateErrorsCount * 12);
        }
        if (!windowsUpdate.WuServiceRunning && windowsUpdate.RecentUpdateErrorsCount > 0)
        {
            wuScore -= 15;
        }
        categories.Add(BuildCategory(HealthCategory.WindowsUpdate, wuScore, issues,
            windowsUpdate.RecentUpdateErrorsCount == 0
                ? $"Ошибок обновления не обнаружено. Установлено недавних пакетов: {windowsUpdate.RecentInstalledKbs.Count}."
                : $"Зафиксировано ошибок установки обновлений: {windowsUpdate.RecentUpdateErrorsCount}."));

        // 8. Загрузка и автозапуск
        var bootScore = 100;
        if (boot.HighImpactStartupItems > 4)
        {
            bootScore -= Math.Min(30, (boot.HighImpactStartupItems - 4) * 6);
        }
        if (boot.LastBootDurationSeconds > 45.0)
        {
            bootScore -= 15;
        }
        categories.Add(BuildCategory(HealthCategory.Boot, bootScore, issues,
            $"Режим прошивки: {boot.FirmwareTypeRu}. Элементов автозагрузки: {boot.TotalStartupItems} (высокое влияние: {boot.HighImpactStartupItems})."));

        // 9. Питание и батарея
        var powerScore = 100;
        if (power.HasBattery && power.BatteryWearPercent is > 35.0)
        {
            powerScore -= 25;
        }
        categories.Add(BuildCategory(HealthCategory.Power, powerScore, issues,
            power.HasBattery
                ? $"Активная схема: {power.ActivePowerSchemeNameRu}. Заряд: {power.BatteryChargePercent ?? 100}%, износ батареи: {power.BatteryWearPercent ?? 0:F1}%."
                : $"Активная схема питания: {power.ActivePowerSchemeNameRu} (стационарный ПК)."));

        // 10. Безопасность и защита
        var secScore = 100;
        if (!security.DefenderEnabled || !security.RealTimeProtectionEnabled)
        {
            secScore -= 35;
        }
        if (!security.FirewallEnabledOnAllProfiles)
        {
            secScore -= 25;
        }
        if (!security.SystemRestoreEnabled || security.ExistingRestorePointsCount == 0)
        {
            secScore -= 20;
        }
        categories.Add(BuildCategory(HealthCategory.Security, secScore, issues,
            security.DefenderEnabled && security.FirewallEnabledOnAllProfiles && security.SystemRestoreEnabled
                ? $"Защитник Windows, Брандмауэр и Защита системы активны (точек восстановления: {security.ExistingRestorePointsCount})."
                : "Требуется внимание к настройкам Защитника Windows, Брандмауэра или точек восстановления."));

        // Расчёт средневзвешенного балла
        var weightedSum =
            categories[0].Score * 1.3 + // Диски
            categories[1].Score * 1.4 + // Целостность файлов
            categories[2].Score * 0.8 + // Журнал событий
            categories[3].Score * 1.0 + // Службы
            categories[4].Score * 1.0 + // Драйверы
            categories[5].Score * 1.0 + // Сеть
            categories[6].Score * 0.9 + // Обновления
            categories[7].Score * 0.7 + // Автозагрузка
            categories[8].Score * 0.6 + // Питание
            categories[9].Score * 1.3;  // Безопасность

        var overall = (int)Math.Round(weightedSum / 10.0);
        overall = Math.Clamp(overall, 0, 100);

        var overallStatusRu = overall switch
        {
            >= 90 => "Отличное состояние",
            >= 75 => "Хорошее состояние (есть мелкие замечания)",
            >= 55 => "Требуется обслуживание",
            _ => "Критическое состояние — рекомендуется восстановление"
        };

        return (overall, overallStatusRu, categories);
    }

    private static CategoryHealthScore BuildCategory(
        HealthCategory category,
        int rawScore,
        IReadOnlyList<DiagnosticIssue> allIssues,
        string summaryRu)
    {
        var clamped = Math.Clamp(rawScore, 0, 100);
        var categoryIssues = allIssues.Where(i => i.Category == category).ToList();
        var safeCount = categoryIssues.Count(i => i.CanAutoFixSafely && i.FixRiskLevel.IsEligibleForSafeAutoFix());

        var severity = clamped switch
        {
            >= 90 => IssueSeverity.Healthy,
            >= 75 => IssueSeverity.Information,
            >= 55 => IssueSeverity.Warning,
            _ => IssueSeverity.Critical
        };

        return new CategoryHealthScore(
            category,
            category.ToRussianTitle(),
            clamped,
            severity,
            summaryRu,
            categoryIssues.Count,
            safeCount);
    }
}
