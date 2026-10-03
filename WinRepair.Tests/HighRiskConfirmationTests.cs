using FluentAssertions;
using WinRepair.Core.Safety;
using Xunit;

namespace WinRepair.Tests;

public sealed class HighRiskConfirmationTests
{
    [Fact]
    public void Validate_ShouldDeny_WhenHighRiskPhraseIsEmptyOrMismatched()
    {
        var emptyResult = HighRiskConfirmationValidator.Validate(
            RiskLevel.High,
            HighRiskConfirmationValidator.BootloaderPhrase,
            "");

        emptyResult.IsValid.Should().BeFalse();
        emptyResult.MessageRu.Should().Contain(HighRiskConfirmationValidator.BootloaderPhrase);

        var wrongResult = HighRiskConfirmationValidator.Validate(
            RiskLevel.High,
            HighRiskConfirmationValidator.ChkdskPhrase,
            "ДА");

        wrongResult.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(HighRiskConfirmationValidator.BootloaderPhrase, "ВОССТАНОВИТЬ ЗАГРУЗЧИК")]
    [InlineData(HighRiskConfirmationValidator.BootloaderPhrase, "  восстановить загрузчик  ")]
    [InlineData(HighRiskConfirmationValidator.ChkdskPhrase, "ПРОВЕРИТЬ ДИСК")]
    [InlineData(HighRiskConfirmationValidator.NetworkResetPhrase, "СБРОСИТЬ СЕТЬ")]
    [InlineData(HighRiskConfirmationValidator.PermanentDeletePhrase, "УДАЛИТЬ БЕЗВОЗВРАТНО")]
    public void Validate_ShouldApprove_WhenPhraseMatchesCaseInsensitive(string expected, string userEntered)
    {
        var result = HighRiskConfirmationValidator.Validate(RiskLevel.High, expected, userEntered);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void RiskLevelExtensions_ShouldClassifyAutoFixEligibilityCorrectly()
    {
        RiskLevel.Safe.IsEligibleForSafeAutoFix().Should().BeTrue();
        RiskLevel.Low.IsEligibleForSafeAutoFix().Should().BeTrue();
        RiskLevel.Medium.IsEligibleForSafeAutoFix().Should().BeFalse();
        RiskLevel.High.IsEligibleForSafeAutoFix().Should().BeFalse();
        RiskLevel.High.RequiresManualPhraseConfirmation().Should().BeTrue();
    }
}
