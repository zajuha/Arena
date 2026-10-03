namespace WinRepair.Core.Safety;

public sealed record ConfirmationValidationResult(
    bool IsValid,
    string ExpectedPhrase,
    string MessageRu);

/// <summary>
/// Валидатор двойного подтверждения для операций высокого уровня риска (RiskLevel.High).
/// Требует ручного ввода контрольной фразы без ошибок.
/// </summary>
public static class HighRiskConfirmationValidator
{
    public const string BootloaderPhrase = "ВОССТАНОВИТЬ ЗАГРУЗЧИК";
    public const string ChkdskPhrase = "ПРОВЕРИТЬ ДИСК";
    public const string NetworkResetPhrase = "СБРОСИТЬ СЕТЬ";
    public const string DeleteServicePhrase = "УДАЛИТЬ СЛУЖБУ";
    public const string RegistryCleanupPhrase = "ОЧИСТИТЬ РЕЕСТР";
    public const string PermanentDeletePhrase = "УДАЛИТЬ БЕЗВОЗВРАТНО";

    public static ConfirmationValidationResult Validate(
        RiskLevel riskLevel,
        string? expectedPhrase,
        string? userEnteredPhrase)
    {
        if (riskLevel != RiskLevel.High && string.IsNullOrWhiteSpace(expectedPhrase))
        {
            return new ConfirmationValidationResult(
                true,
                string.Empty,
                "Операция не требует ручного ввода контрольной фразы.");
        }

        var required = string.IsNullOrWhiteSpace(expectedPhrase)
            ? "ПОДТВЕРЖДАЮ"
            : expectedPhrase.Trim();

        if (string.IsNullOrWhiteSpace(userEnteredPhrase))
        {
            return new ConfirmationValidationResult(
                false,
                required,
                $"Для выполнения операции высокого риска введите фразу «{required}» вручную.");
        }

        var normalizedInput = userEnteredPhrase.Trim();
        if (!string.Equals(normalizedInput, required, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfirmationValidationResult(
                false,
                required,
                $"Введённая фраза «{normalizedInput}» не совпадает с требуемой «{required}». Операция отменена.");
        }

        return new ConfirmationValidationResult(
            true,
            required,
            "Контрольная фраза подтверждена.");
    }
}
