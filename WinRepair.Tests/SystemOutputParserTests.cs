using FluentAssertions;
using WinRepair.Core.Diagnostics;
using Xunit;

namespace WinRepair.Tests;

public sealed class SystemOutputParserTests
{
    [Fact]
    public void ParseSfcOutput_ShouldRecognize_NoIntegrityViolations_RussianAndEnglish()
    {
        var ruOutput = "Проверка 100% завершена.\r\nЗащита ресурсов Windows не обнаружила нарушений целостности.";
        var enOutput = "Verification 100% complete.\r\nWindows Resource Protection did not find any integrity violations.";

        var ruParsed = SystemOutputParser.ParseSfcOutput(ruOutput);
        var enParsed = SystemOutputParser.ParseSfcOutput(enOutput);

        ruParsed.Outcome.Should().Be(SfcScanOutcome.NoIntegrityViolations);
        ruParsed.IsHealthy.Should().BeTrue();
        ruParsed.RequiresDismRepair.Should().BeFalse();

        enParsed.Outcome.Should().Be(SfcScanOutcome.NoIntegrityViolations);
        enParsed.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void ParseSfcOutput_ShouldRecognize_CorruptFilesRepaired_IncludingUtf16Nulls()
    {
        var rawWithNulls = "З\0а\0щ\0и\0т\0а\0 ресурсов Windows обнаружила поврежденные файлы и успешно восстановила их.";

        var parsed = SystemOutputParser.ParseSfcOutput(rawWithNulls);

        parsed.Outcome.Should().Be(SfcScanOutcome.CorruptFilesRepaired);
        parsed.IsHealthy.Should().BeTrue();
        parsed.RepairedSuccessfully.Should().BeTrue();
        parsed.RequiresDismRepair.Should().BeFalse();
    }

    [Fact]
    public void ParseSfcOutput_ShouldFlagDismRequired_WhenUnableToFixSomeFiles()
    {
        var output = "Защита ресурсов Windows обнаружила поврежденные файлы, но не смогла восстановить некоторые из них.";

        var parsed = SystemOutputParser.ParseSfcOutput(output);

        parsed.Outcome.Should().Be(SfcScanOutcome.CorruptFilesUnableToRepair);
        parsed.IsHealthy.Should().BeFalse();
        parsed.RequiresDismRepair.Should().BeTrue();
    }

    [Fact]
    public void ParseDismOutput_ShouldRecognize_Healthy_Repairable_And_SourceMissingError()
    {
        var healthy = SystemOutputParser.ParseDismOutput(
            "Повреждение хранилища компонентов не обнаружено.\r\nОперация успешно завершена.", 0);
        healthy.Status.Should().Be(DismHealthStatus.Healthy);
        healthy.Succeeded.Should().BeTrue();

        var repairable = SystemOutputParser.ParseDismOutput(
            "Хранилище компонентов подлежит восстановлению.\r\nThe component store is repairable.", 0);
        repairable.Status.Should().Be(DismHealthStatus.Repairable);
        repairable.IsRepairable.Should().BeTrue();

        var sourceMissing = SystemOutputParser.ParseDismOutput(
            "Ошибка: 0x800f081f\r\nНе удалось найти исходные файлы.", -2146498529);
        sourceMissing.Status.Should().Be(DismHealthStatus.SourceNotFoundError);
        sourceMissing.ErrorCodeHex.Should().Be("0x800f081f");
        sourceMissing.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    [InlineData("Проверка 45,5% завершена.", 45.5)]
    [InlineData("Verification 72% complete.", 72.0)]
    public void TryParseProgressPercentage_ShouldExtractNumericProgress(string line, double expected)
    {
        var pct = SystemOutputParser.TryParseProgressPercentage(line);

        pct.Should().BeApproximately(expected, 0.01);
    }
}
