using FluentAssertions;
using WinRepair.Core.Safety;
using Xunit;

namespace WinRepair.Tests;

public sealed class FakeFileSystemInspector : IFileSystemInspector
{
    private readonly Dictionary<string, string?> _reparsePoints = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterSymlinkOrJunction(string linkPath, string? targetPath)
    {
        _reparsePoints[WhitelistPolicy.NormalizeWindowsPath(linkPath)] = targetPath;
    }

    public bool IsReparsePoint(string path) =>
        _reparsePoints.ContainsKey(WhitelistPolicy.NormalizeWindowsPath(path));

    public string? ResolveLinkTarget(string path) =>
        _reparsePoints.GetValueOrDefault(WhitelistPolicy.NormalizeWindowsPath(path));

    public bool Exists(string path) => true;
}

public sealed class PathValidatorTests
{
    private const string TestUserProfile = @"C:\Users\Ivan";
    private const string TestLocalAppData = @"C:\Users\Ivan\AppData\Local";
    private const string TestRoamingAppData = @"C:\Users\Ivan\AppData\Roaming";

    private static PathValidator CreateValidator(IFileSystemInspector? inspector = null)
    {
        var policy = new WhitelistPolicy(
            systemDrive: "C:",
            userProfilePath: TestUserProfile,
            localAppDataPath: TestLocalAppData,
            appDataRoamingPath: TestRoamingAppData);

        return new PathValidator(policy, inspector ?? new FakeFileSystemInspector());
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\install_log_01.tmp")]
    [InlineData(@"C:\Windows\Prefetch\CHROME.EXE-1234ABCD.pf")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Temp\scoped_dir_99\cache.bin")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download\9a8b7c\windows11-kb5034441.cab")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_0001a")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Yandex\YandexBrowser\User Data\Default\Cache\data_1")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Mozilla\Firefox\Profiles\abcd1234.default-release\cache2\entries\A1B2C3")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Microsoft\Windows\Explorer\iconcache_32.db")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\D3DSCache\8a9b0c\shader.val")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1000\$R12345.tmp")]
    public void ValidateForDeletion_ShouldAllow_WhitelistedPaths(string allowedPath)
    {
        var validator = CreateValidator();

        var result = validator.ValidateForDeletion(allowedPath);

        result.IsAllowed.Should().BeTrue($"путь {allowedPath} входит в белый список: {result.ReasonRu}");
        result.FailureReason.Should().Be(PathValidationFailureReason.None);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\System32\drivers\tcpip.sys")]
    [InlineData(@"C:\Windows\SysWOW64\ntdll.dll")]
    [InlineData(@"C:\Windows\WinSxS\amd64_microsoft-windows-core\file.dll")]
    [InlineData(@"C:\EFI\Microsoft\Boot\bootmgfw.efi")]
    [InlineData(@"C:\Boot\BCD")]
    public void ValidateForDeletion_ShouldReject_ProtectedSystemDirectories(string systemPath)
    {
        var validator = CreateValidator();

        var result = validator.ValidateForDeletion(systemPath);

        result.IsAllowed.Should().BeFalse();
        result.FailureReason.Should().BeOneOf(
            PathValidationFailureReason.ForbiddenSystemDirectory,
            PathValidationFailureReason.ProtectedSystemFile);
    }

    [Theory]
    [InlineData(@"C:\Users\Ivan\Documents\Финансовый_отчёт.xlsx")]
    [InlineData(@"C:\Users\Ivan\Документы\Договор.pdf")]
    [InlineData(@"C:\Users\Ivan\Desktop\Проект.zip")]
    [InlineData(@"C:\Users\Ivan\Рабочий стол\Скан.png")]
    [InlineData(@"C:\Users\Ivan\Pictures\Отпуск\IMG_001.jpg")]
    [InlineData(@"C:\Users\Ivan\Videos\Запись.mp4")]
    [InlineData(@"C:\Users\Ivan\Music\Трек.flac")]
    [InlineData(@"C:\Users\Ivan\Downloads\setup.exe")]
    [InlineData(@"C:\Users\Ivan\Загрузки\archive.7z")]
    [InlineData(@"C:\Users\OtherUser\Documents\Passport.pdf")]
    public void ValidateForDeletion_ShouldReject_UserPersonalFolders(string personalPath)
    {
        var validator = CreateValidator();

        var result = validator.ValidateForDeletion(personalPath);

        result.IsAllowed.Should().BeFalse();
        result.FailureReason.Should().Be(PathValidationFailureReason.ProtectedUserPersonalFolder);
    }

    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"C:\bootmgr")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Google\Chrome\User Data\Default\Login Data")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Google\Chrome\User Data\Default\Cookies")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Mozilla\Firefox\Profiles\abcd.default\places.sqlite")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Mozilla\Firefox\Profiles\abcd.default\key4.db")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Mozilla\Firefox\Profiles\abcd.default\logins.json")]
    public void ValidateForDeletion_ShouldReject_ProtectedSystemAndBrowserCredentialFiles(string protectedFile)
    {
        var validator = CreateValidator();

        var result = validator.ValidateForDeletion(protectedFile);

        result.IsAllowed.Should().BeFalse();
        result.FailureReason.Should().BeOneOf(
            PathValidationFailureReason.ProtectedSystemFile,
            PathValidationFailureReason.BrowserProfileNonCacheFile);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\..\..\Users\Ivan\Documents\secret.txt")]
    [InlineData(@"C:\Users\Ivan\AppData\Local\Temp\..\Google\Chrome\User Data\Default\Bookmarks")]
    public void ValidateForDeletion_ShouldReject_PathTraversalAttempts(string traversalPath)
    {
        var validator = CreateValidator();

        var result = validator.ValidateForDeletion(traversalPath);

        result.IsAllowed.Should().BeFalse();
        result.FailureReason.Should().Be(PathValidationFailureReason.PathTraversalAttempt);
    }

    [Fact]
    public void ValidateForDeletion_ShouldReject_DeletingWhitelistRootDirectoryItself()
    {
        var validator = CreateValidator();

        var result = validator.ValidateForDeletion(@"C:\Windows\Temp");

        result.IsAllowed.Should().BeFalse();
        result.FailureReason.Should().Be(PathValidationFailureReason.CannotDeleteWhitelistRootItself);
    }

    [Fact]
    public void ValidateForDeletion_ShouldReject_SymlinkOrJunctionEscapingToUserDocuments()
    {
        var fakeInspector = new FakeFileSystemInspector();
        // Злоумышленник или некорректный софт создал junction внутри C:\Windows\Temp\junction_docs -> C:\Users\Ivan\Documents
        fakeInspector.RegisterSymlinkOrJunction(
            @"C:\Windows\Temp\junction_docs",
            @"C:\Users\Ivan\Documents");

        var validator = CreateValidator(fakeInspector);

        var result = validator.ValidateForDeletion(@"C:\Windows\Temp\junction_docs\ImportantReport.docx");

        result.IsAllowed.Should().BeFalse();
        result.FailureReason.Should().Be(PathValidationFailureReason.SymlinkOrJunctionEscapeDetected);
        result.ReasonRu.Should().Contain("Заблокирован переход по символической ссылке");
    }

    [Fact]
    public void ValidateForDeletion_ShouldReject_SymlinkPointingToSystem32()
    {
        var fakeInspector = new FakeFileSystemInspector();
        fakeInspector.RegisterSymlinkOrJunction(
            @"C:\Users\Ivan\AppData\Local\Temp\sys_link",
            @"C:\Windows\System32");

        var validator = CreateValidator(fakeInspector);

        var result = validator.ValidateForDeletion(@"C:\Users\Ivan\AppData\Local\Temp\sys_link\cmd.exe");

        result.IsAllowed.Should().BeFalse();
        result.FailureReason.Should().Be(PathValidationFailureReason.SymlinkOrJunctionEscapeDetected);
    }

    [Fact]
    public void ValidateForDeletion_ShouldAllow_SymlinkStayingStrictlyInsideWhitelistRoot()
    {
        var fakeInspector = new FakeFileSystemInspector();
        fakeInspector.RegisterSymlinkOrJunction(
            @"C:\Windows\Temp\sub_link",
            @"C:\Windows\Temp\real_sub");

        var validator = CreateValidator(fakeInspector);

        var result = validator.ValidateForDeletion(@"C:\Windows\Temp\sub_link\safe_temp.tmp");

        result.IsAllowed.Should().BeTrue();
    }
}
