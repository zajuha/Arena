namespace WinRepair.Core.Safety;

/// <summary>
/// Уровень риска системной операции.
/// </summary>
public enum RiskLevel
{
    /// <summary>
    /// Полностью безопасная операция (временные файлы, кэши, диагностика).
    /// Допускается автоматическое выполнение кнопкой «Исправить всё безопасное».
    /// </summary>
    Safe = 0,

    /// <summary>
    /// Низкий риск (сброс DNS, очистка кэша обновлений, перезапуск служб).
    /// </summary>
    Low = 1,

    /// <summary>
    /// Средний риск (восстановление хранилища компонентов DISM, SFC, переключение служб).
    /// Требует точки восстановления и явного запуска пользователем.
    /// </summary>
    Medium = 2,

    /// <summary>
    /// Высокий риск (загрузчик BCD/MBR, chkdsk с перезагрузкой, полный сброс стека сети,
    /// удаление служб, изменение нетривиальных веток реестра).
    /// Требует двойного подтверждения с ручным вводом контрольной фразы.
    /// </summary>
    High = 3
}

public static class RiskLevelExtensions
{
    public static string ToRussianDisplayName(this RiskLevel riskLevel) => riskLevel switch
    {
        RiskLevel.Safe => "Безопасно",
        RiskLevel.Low => "Низкий риск",
        RiskLevel.Medium => "Средний риск",
        RiskLevel.High => "Высокий риск (двойное подтверждение)",
        _ => "Неизвестно"
    };

    public static bool RequiresManualPhraseConfirmation(this RiskLevel riskLevel) =>
        riskLevel == RiskLevel.High;

    public static bool IsEligibleForSafeAutoFix(this RiskLevel riskLevel) =>
        riskLevel is RiskLevel.Safe or RiskLevel.Low;
}
