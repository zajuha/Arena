using System.Globalization;

namespace WinRepair.Core.Diagnostics;

/// <summary>
/// Форматирование объёмов данных в русском стандарте (с запятой в качестве разделителя).
/// </summary>
public static class FileSizeFormatter
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] UnitsRu = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    public static string FormatRussian(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 Б";
        }

        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024.0 && unitIndex < UnitsRu.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes.ToString("N0", RussianCulture)} Б"
            : $"{value.ToString("0.#", RussianCulture)} {UnitsRu[unitIndex]}";
    }

    public static long SumSelectedBytes(IEnumerable<(long SizeBytes, bool IsSelected)> items)
    {
        if (items is null)
        {
            return 0L;
        }

        long total = 0;
        foreach (var (size, selected) in items)
        {
            if (selected && size > 0)
            {
                checked
                {
                    total += size;
                }
            }
        }

        return total;
    }
}
