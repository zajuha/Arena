using System.Globalization;
using System.Text.RegularExpressions;

namespace WinRepair.Core.Diagnostics;

public enum SfcScanOutcome
{
    NoIntegrityViolations = 0,
    CorruptFilesRepaired = 1,
    CorruptFilesUnableToRepair = 2,
    PendingRebootRequired = 3,
    CouldNotPerformOperation = 4,
    Unknown = 5
}

public sealed record SfcParsedResult(
    SfcScanOutcome Outcome,
    bool IsHealthy,
    bool RepairedSuccessfully,
    bool RequiresDismRepair,
    string SummaryRu,
    string? CbsLogPath);

public enum DismHealthStatus
{
    Healthy = 0,
    Repairable = 1,
    NonRepairableOrCorrupt = 2,
    RestoreSucceeded = 3,
    ComponentCleanupSucceeded = 4,
    SourceNotFoundError = 5,
    UnknownError = 6
}

public sealed record DismParsedResult(
    DismHealthStatus Status,
    bool Succeeded,
    bool IsRepairable,
    string? ErrorCodeHex,
    string SummaryRu);

/// <summary>
/// Чистый детерминированный парсер консольного вывода системных утилит Windows (SFC, DISM, CHKDSK).
/// Поддерживает как русскую, так и английскую локализации Windows 10 (1809+) и Windows 11 (21H2+).
/// </summary>
public static partial class SystemOutputParser
{
    [GeneratedRegex(@"0x[0-9a-fA-F]{8}", RegexOptions.CultureInvariant)]
    private static partial Regex HexErrorCodeRegex();

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentageRegex();

    /// <summary>
    /// Разбирает вывод утилиты sfc /scannow (включая распакованный UTF-16LE и OEM866 текст).
    /// </summary>
    public static SfcParsedResult ParseSfcOutput(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new SfcParsedResult(
                SfcScanOutcome.Unknown,
                IsHealthy: false,
                RepairedSuccessfully: false,
                RequiresDismRepair: false,
                SummaryRu: "Вывод утилиты SFC пуст или процесс не вернул данных.",
                CbsLogPath: null);
        }

        // SFC на некоторых сборках выводит UTF-16 с нулевыми байтами между символами
        var cleaned = rawOutput.Replace("\0", string.Empty).Trim();
        var cbsPath = @"C:\Windows\Logs\CBS\CBS.log";

        // 1. Нарушений целостности не обнаружено
        if (ContainsAnyIgnoreCase(cleaned,
                "не обнаружила нарушений целостности",
                "did not find any integrity violations"))
        {
            return new SfcParsedResult(
                SfcScanOutcome.NoIntegrityViolations,
                IsHealthy: true,
                RepairedSuccessfully: true,
                RequiresDismRepair: false,
                SummaryRu: "Защита ресурсов Windows не обнаружила нарушений целостности системных файлов.",
                CbsLogPath: cbsPath);
        }

        // 2. Повреждённые файлы найдены и успешно восстановлены
        if (ContainsAnyIgnoreCase(cleaned,
                "обнаружила поврежденные файлы и успешно восстановила их",
                "обнаружила повреждённые файлы и успешно восстановила их",
                "found corrupt files and successfully repaired them"))
        {
            return new SfcParsedResult(
                SfcScanOutcome.CorruptFilesRepaired,
                IsHealthy: true,
                RepairedSuccessfully: true,
                RequiresDismRepair: false,
                SummaryRu: "Защита ресурсов Windows обнаружила повреждённые системные файлы и успешно восстановила их. Подробности записаны в журнал CBS.log.",
                CbsLogPath: cbsPath);
        }

        // 3. Повреждённые файлы найдены, но восстановить не удалось -> требуется DISM /RestoreHealth
        if (ContainsAnyIgnoreCase(cleaned,
                "не смогла восстановить некоторые из них",
                "unable to fix some of them"))
        {
            return new SfcParsedResult(
                SfcScanOutcome.CorruptFilesUnableToRepair,
                IsHealthy: false,
                RepairedSuccessfully: false,
                RequiresDismRepair: true,
                SummaryRu: "Обнаружены повреждённые системные файлы, которые не удалось восстановить через SFC. Рекомендуется выполнить восстановление хранилища компонентов через DISM /RestoreHealth.",
                CbsLogPath: cbsPath);
        }

        // 4. Ожидается перезагрузка после предыдущего восстановления
        if (ContainsAnyIgnoreCase(cleaned,
                "ожидает завершения операции восстановления",
                "system repair pending"))
        {
            return new SfcParsedResult(
                SfcScanOutcome.PendingRebootRequired,
                IsHealthy: false,
                RepairedSuccessfully: false,
                RequiresDismRepair: false,
                SummaryRu: "Обнаружена незавершённая операция обслуживания системы. Перезагрузите компьютер и повторите проверку SFC.",
                CbsLogPath: cbsPath);
        }

        // 5. Не удалось выполнить операцию
        if (ContainsAnyIgnoreCase(cleaned,
                "не может выполнить запрошенную операцию",
                "could not perform the requested operation"))
        {
            return new SfcParsedResult(
                SfcScanOutcome.CouldNotPerformOperation,
                IsHealthy: false,
                RepairedSuccessfully: false,
                RequiresDismRepair: true,
                SummaryRu: "Защита ресурсов Windows не смогла выполнить проверку целостности. Проверьте службу «Установщик модулей Windows» (TrustedInstaller).",
                CbsLogPath: cbsPath);
        }

        return new SfcParsedResult(
            SfcScanOutcome.Unknown,
            IsHealthy: false,
            RepairedSuccessfully: false,
            RequiresDismRepair: false,
            SummaryRu: "Проверка SFC завершилась с нестандартным ответом. Проверьте журнал операций.",
            CbsLogPath: cbsPath);
    }

    /// <summary>
    /// Разбирает вывод утилиты DISM (/CheckHealth, /ScanHealth, /RestoreHealth, /StartComponentCleanup).
    /// </summary>
    public static DismParsedResult ParseDismOutput(string? rawOutput, int exitCode = 0)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new DismParsedResult(
                DismHealthStatus.UnknownError,
                Succeeded: false,
                IsRepairable: false,
                ErrorCodeHex: null,
                SummaryRu: "Утилита DISM не вернула текстового вывода.");
        }

        var cleaned = rawOutput.Replace("\0", string.Empty).Trim();
        var hexMatch = HexErrorCodeRegex().Match(cleaned);
        var errorCode = hexMatch.Success ? hexMatch.Value.ToLowerInvariant() : null;

        // Проверка на известную ошибку отсутствия исходных файлов (0x800f081f / CBS_E_SOURCE_MISSING)
        if (string.Equals(errorCode, "0x800f081f", StringComparison.OrdinalIgnoreCase) ||
            ContainsAnyIgnoreCase(cleaned, "не удалось найти исходные файлы", "source files could not be found"))
        {
            return new DismParsedResult(
                DismHealthStatus.SourceNotFoundError,
                Succeeded: false,
                IsRepairable: true,
                ErrorCodeHex: "0x800f081f",
                SummaryRu: "Ошибка DISM 0x800f081f: не удалось найти исходные файлы для восстановления. Укажите локальный образ install.wim или проверьте доступность Центра обновления Windows.");
        }

        // Успешное восстановление хранилища (/RestoreHealth)
        if (ContainsAnyIgnoreCase(cleaned,
                "восстановление выполнено успешно",
                "the restore operation completed successfully"))
        {
            return new DismParsedResult(
                DismHealthStatus.RestoreSucceeded,
                Succeeded: true,
                IsRepairable: false,
                ErrorCodeHex: null,
                SummaryRu: "Хранилище компонентов Windows (WinSxS) успешно восстановлено утилитой DISM.");
        }

        // Повреждений хранилища компонентов не обнаружено (/CheckHealth, /ScanHealth)
        if (ContainsAnyIgnoreCase(cleaned,
                "повреждение хранилища компонентов не обнаружено",
                "no component store corruption detected"))
        {
            return new DismParsedResult(
                DismHealthStatus.Healthy,
                Succeeded: true,
                IsRepairable: false,
                ErrorCodeHex: null,
                SummaryRu: "Повреждений хранилища компонентов Windows не обнаружено. Состояние системы в норме.");
        }

        // Хранилище компонентов подлежит восстановлению
        if (ContainsAnyIgnoreCase(cleaned,
                "хранилище компонентов подлежит восстановлению",
                "component store is repairable"))
        {
            return new DismParsedResult(
                DismHealthStatus.Repairable,
                Succeeded: true,
                IsRepairable: true,
                ErrorCodeHex: null,
                SummaryRu: "Обнаружено повреждение хранилища компонентов Windows, но оно подлежит автоматическому исправлению через DISM /RestoreHealth.");
        }

        // Хранилище компонентов не подлежит восстановлению
        if (ContainsAnyIgnoreCase(cleaned,
                "хранилище компонентов не подлежит восстановлению",
                "component store is not repairable"))
        {
            return new DismParsedResult(
                DismHealthStatus.NonRepairableOrCorrupt,
                Succeeded: false,
                IsRepairable: false,
                ErrorCodeHex: errorCode,
                SummaryRu: "Критическое повреждение хранилища компонентов Windows. Требуется обновление системы поверх (In-Place Upgrade) с сохранением данных.");
        }

        // Успешная очистка устаревших обновлений (/StartComponentCleanup)
        if (exitCode == 0 && ContainsAnyIgnoreCase(cleaned,
                "операция успешно завершена",
                "the operation completed successfully"))
        {
            return new DismParsedResult(
                DismHealthStatus.ComponentCleanupSucceeded,
                Succeeded: true,
                IsRepairable: false,
                ErrorCodeHex: null,
                SummaryRu: "Операция обслуживания хранилища компонентов DISM успешно завершена.");
        }

        return new DismParsedResult(
            DismHealthStatus.UnknownError,
            Succeeded: exitCode == 0,
            IsRepairable: false,
            ErrorCodeHex: errorCode,
            SummaryRu: errorCode is not null
                ? $"Выполнение DISM завершилось с кодом ошибки {errorCode}. Подробности в C:\\Windows\\Logs\\DISM\\dism.log."
                : $"Выполнение DISM завершилось с кодом возврата {exitCode}.");
    }

    /// <summary>
    /// Извлекает текущий процент выполнения из строки вывода DISM или SFC.
    /// </summary>
    public static double? TryParseProgressPercentage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var cleaned = line.Replace("\0", string.Empty);
        var match = PercentageRegex().Match(cleaned);
        if (!match.Success)
        {
            return null;
        }

        var numeric = match.Groups[1].Value.Replace(',', '.');
        if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return Math.Clamp(value, 0.0, 100.0);
        }

        return null;
    }

    private static bool ContainsAnyIgnoreCase(string text, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
