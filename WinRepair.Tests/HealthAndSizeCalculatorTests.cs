using FluentAssertions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;
using Xunit;

namespace WinRepair.Tests;

public sealed class HealthAndSizeCalculatorTests
{
    [Theory]
    [InlineData(0L, "0 Б")]
    [InlineData(512L, "512 Б")]
    [InlineData(1536L, "1,5 КБ")]
    [InlineData(1048576L, "1 МБ")]
    [InlineData(1288490189L, "1,2 ГБ")]
    public void FormatRussian_ShouldFormatBytesWithRussianCommaAndUnits(long bytes, string expected)
    {
        FileSizeFormatter.FormatRussian(bytes).Should().Be(expected);
    }

    [Fact]
    public void SumSelectedBytes_ShouldOnlySumSelectedPositiveItems()
    {
        var items = new (long SizeBytes, bool IsSelected)[]
        {
            (1000L, true),
            (2500L, false),
            (500L, true)
        };

        FileSizeFormatter.SumSelectedBytes(items).Should().Be(1500L);
    }

    [Fact]
    public void HealthScoreCalculator_ShouldReturn100_WhenAllSubsystemsAreHealthy()
    {
        var (score, statusRu, categories) = HealthScoreCalculator.Calculate(
            disks:
            [
                new DiskDriveDiagnostic("C:", "Windows", "NTFS", "NVMe SSD", 512_000_000_000L, 256_000_000_000L, 50.0, "Исправен", false, 36, false)
            ],
            systemFiles: new SystemFilesDiagnostic("OK", false, false, "Повреждений не обнаружено.", DateTimeOffset.UtcNow),
            recentEvents: [],
            problematicServices: [],
            driverProblems: [],
            network: new NetworkDiagnosticInfo(true, true, 12, false, null, "OK", ["Ethernet"]),
            windowsUpdate: new WindowsUpdateDiagnosticInfo(true, true, DateTimeOffset.UtcNow, 0, 0, ["KB5034441"]),
            boot: new BootDiagnosticInfo(18.5, 3, 1, "UEFI"),
            power: new PowerDiagnosticInfo("381b4222-f694-41f0-9685-ff5bb260df2e", "Сбалансированная", false, null, null, null, null),
            security: new SecurityDiagnosticInfo(true, true, true, true, true, 3),
            issues: []);

        score.Should().Be(100);
        statusRu.Should().Contain("Отличное");
        categories.Should().HaveCount(10);
        categories.All(c => c.Score == 100).Should().BeTrue();
    }

    [Fact]
    public void HealthScoreCalculator_ShouldReduceScore_WhenSmartFailureAndComponentCorruptionExist()
    {
        var (score, _, categories) = HealthScoreCalculator.Calculate(
            disks:
            [
                new DiskDriveDiagnostic("C:", "Windows", "NTFS", "HDD", 500_000_000_000L, 15_000_000_000L, 3.0, "Сбой SMART", true, 68, true)
            ],
            systemFiles: new SystemFilesDiagnostic("Повреждено", true, true, "Требуется DISM", DateTimeOffset.UtcNow),
            recentEvents: [],
            problematicServices: [],
            driverProblems: [],
            network: new NetworkDiagnosticInfo(true, true, 15, false, null, "OK", ["Ethernet"]),
            windowsUpdate: new WindowsUpdateDiagnosticInfo(true, true, DateTimeOffset.UtcNow, 0, 0, []),
            boot: new BootDiagnosticInfo(22.0, 4, 1, "UEFI"),
            power: new PowerDiagnosticInfo("381b4222-f694-41f0-9685-ff5bb260df2e", "Сбалансированная", false, null, null, null, null),
            security: new SecurityDiagnosticInfo(false, false, true, false, false, 0),
            issues: []);

        score.Should().BeLessThan(75);
        categories.Single(c => c.Category == HealthCategory.Disks).Score.Should().Be(0);
        categories.Single(c => c.Category == HealthCategory.SystemFiles).Score.Should().Be(40);
    }
}
