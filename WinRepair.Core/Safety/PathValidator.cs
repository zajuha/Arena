using System.Text.RegularExpressions;

namespace WinRepair.Core.Safety;

public enum PathValidationFailureReason
{
    None = 0,
    EmptyOrInvalidSyntax = 1,
    RelativeOrDevicePathNotAllowed = 2,
    PathTraversalAttempt = 3,
    ProtectedSystemFile = 4,
    ProtectedUserPersonalFolder = 5,
    ForbiddenSystemDirectory = 6,
    OutsideWhitelistRoot = 7,
    CannotDeleteWhitelistRootItself = 8,
    BrowserProfileNonCacheFile = 9,
    ExplorerNonCacheFile = 10,
    SymlinkOrJunctionEscapeDetected = 11
}

public sealed record PathValidationResult(
    bool IsAllowed,
    string NormalizedPath,
    string? MatchedWhitelistRoot,
    PathValidationFailureReason FailureReason,
    string ReasonRu)
{
    public static PathValidationResult Allowed(string normalizedPath, string whitelistRoot) =>
        new(true, normalizedPath, whitelistRoot, PathValidationFailureReason.None,
            $"Путь разрешён политикой безопасности (корень: {whitelistRoot}).");

    public static PathValidationResult Denied(
        string normalizedPath,
        PathValidationFailureReason reason,
        string reasonRu) =>
        new(false, normalizedPath, null, reason, reasonRu);
}

/// <summary>
/// Строгий валидатор путей с проверкой белого списка, защищённых папок пользователя,
/// системных файлов и обхода через символические ссылки (Symlink) и точки соединения (Junction).
/// </summary>
public sealed partial class PathValidator
{
    private readonly WhitelistPolicy _policy;
    private readonly IFileSystemInspector _fileSystemInspector;

    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRootRegex();

    [GeneratedRegex(@"^(thumbcache|iconcache)_[a-zA-Z0-9_]+\.db$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplorerCacheFileRegex();

    public PathValidator(WhitelistPolicy policy, IFileSystemInspector? fileSystemInspector = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _fileSystemInspector = fileSystemInspector ?? new PhysicalFileSystemInspector();
    }

    public PathValidationResult ValidateForDeletion(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || candidatePath.Contains('\0'))
        {
            return PathValidationResult.Denied(
                candidatePath ?? string.Empty,
                PathValidationFailureReason.EmptyOrInvalidSyntax,
                "Путь пуст или содержит недопустимые управляющие символы.");
        }

        var trimmed = candidatePath.Trim();

        // Запрещаем относительные обращения с '..' в исходной строке до нормализации (защита от обхода)
        var rawSegments = trimmed.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Any(s => s == ".."))
        {
            return PathValidationResult.Denied(
                trimmed,
                PathValidationFailureReason.PathTraversalAttempt,
                "Обнаружена попытка обхода каталога через относительный сегмент '..'.");
        }

        // Запрещаем сетевые UNC-пути и пути к устройствам \\.\
        if (trimmed.StartsWith(@"\\.\") ||
            (trimmed.StartsWith(@"\\") && !trimmed.StartsWith(@"\\?\")))
        {
            return PathValidationResult.Denied(
                trimmed,
                PathValidationFailureReason.RelativeOrDevicePathNotAllowed,
                "Сетевые пути (UNC) и прямые пути устройств Windows запрещены.");
        }

        var normalized = WhitelistPolicy.NormalizeWindowsPath(trimmed);
        if (!DriveRootRegex().IsMatch(normalized))
        {
            return PathValidationResult.Denied(
                normalized,
                PathValidationFailureReason.RelativeOrDevicePathNotAllowed,
                "Разрешены только полные абсолютные локальные пути с указанием буквы диска.");
        }

        // 1. Проверяем сам путь по правилам политики
        var coreCheck = ValidateNormalizedTarget(normalized);
        if (!coreCheck.IsAllowed)
        {
            return coreCheck;
        }

        // 2. Проверяем каждый сегмент пути от корня диска до конечного элемента на наличие Symlink / Junction
        var symlinkCheck = InspectReparsePointsAlongHierarchy(normalized);
        if (!symlinkCheck.IsAllowed)
        {
            return symlinkCheck;
        }

        return coreCheck;
    }

    private PathValidationResult ValidateNormalizedTarget(string normalized)
    {
        var fileName = GetWindowsFileName(normalized);

        // Проверка защищённых системных файлов и баз паролей/истории браузеров
        if (!string.IsNullOrEmpty(fileName) && _policy.ForbiddenFileNames.Contains(fileName))
        {
            return PathValidationResult.Denied(
                normalized,
                PathValidationFailureReason.ProtectedSystemFile,
                $"Файл «{fileName}» входит в список неприкосновенных системных или пользовательских файлов.");
        }

        // Проверка личных каталогов пользователя: C:\Users\<User>\<ProtectedSubfolder>
        if (IsInsideProtectedUserDirectory(normalized, out var matchedUserFolder))
        {
            return PathValidationResult.Denied(
                normalized,
                PathValidationFailureReason.ProtectedUserPersonalFolder,
                $"Запрещено трогать личные данные пользователя в каталоге «{matchedUserFolder}».");
        }

        // Проверка строго запрещённых системных корней (System32, WinSxS, EFI, Boot и др.)
        foreach (var forbiddenRoot in _policy.ForbiddenRoots)
        {
            if (IsSameOrSubPath(normalized, forbiddenRoot))
            {
                return PathValidationResult.Denied(
                    normalized,
                    PathValidationFailureReason.ForbiddenSystemDirectory,
                    $"Путь находится внутри защищённого системного каталога «{forbiddenRoot}».");
            }
        }

        // Поиск подходящего корня в белом списке
        string? matchedWhitelistRoot = null;
        foreach (var allowedRoot in _policy.AllowedRoots)
        {
            if (string.Equals(normalized, allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return PathValidationResult.Denied(
                    normalized,
                    PathValidationFailureReason.CannotDeleteWhitelistRootItself,
                    $"Запрещено удалять сам корневой каталог белого списка «{allowedRoot}» — удаляются только вложенные элементы.");
            }

            if (IsStrictSubPath(normalized, allowedRoot))
            {
                matchedWhitelistRoot = allowedRoot;
                break;
            }
        }

        if (matchedWhitelistRoot is null)
        {
            return PathValidationResult.Denied(
                normalized,
                PathValidationFailureReason.OutsideWhitelistRoot,
                "Путь не входит ни в один из разрешённых каталогов белого списка очистки.");
        }

        // Дополнительное правило для профилей Firefox: разрешены ТОЛЬКО папки кэша внутри профиля
        if (matchedWhitelistRoot.EndsWith(@"Mozilla\Firefox\Profiles", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsSafeFirefoxCachePath(normalized, matchedWhitelistRoot))
            {
                return PathValidationResult.Denied(
                    normalized,
                    PathValidationFailureReason.BrowserProfileNonCacheFile,
                    "В профиле Firefox разрешена очистка только подкаталогов кэша (cache2, startupCache, shader-cache). Пароли, закладки и история защищены.");
            }
        }

        // Дополнительное правило для папки Explorer: разрешены только thumbcache_*.db и iconcache_*.db
        if (matchedWhitelistRoot.EndsWith(@"Microsoft\Windows\Explorer", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(fileName) || !ExplorerCacheFileRegex().IsMatch(fileName))
            {
                return PathValidationResult.Denied(
                    normalized,
                    PathValidationFailureReason.ExplorerNonCacheFile,
                    "В каталоге проводника Windows разрешено очищать только файлы кэша эскизов и иконок (thumbcache_*.db, iconcache_*.db).");
            }
        }

        return PathValidationResult.Allowed(normalized, matchedWhitelistRoot);
    }

    private PathValidationResult InspectReparsePointsAlongHierarchy(string normalizedPath)
    {
        var segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return PathValidationResult.Denied(
                normalizedPath,
                PathValidationFailureReason.OutsideWhitelistRoot,
                "Недопустимая глубина пути.");
        }

        var currentPath = segments[0]; // например "C:"
        for (var i = 1; i < segments.Length; i++)
        {
            currentPath = currentPath + @"\" + segments[i];

            if (!_fileSystemInspector.IsReparsePoint(currentPath))
            {
                continue;
            }

            var resolvedTarget = _fileSystemInspector.ResolveLinkTarget(currentPath);
            if (string.IsNullOrWhiteSpace(resolvedTarget))
            {
                return PathValidationResult.Denied(
                    normalizedPath,
                    PathValidationFailureReason.SymlinkOrJunctionEscapeDetected,
                    $"Обнаружена символическая ссылка или точка соединения «{currentPath}», назначение которой не удалось безопасно разрешить.");
            }

            // Собираем полный целевой путь с учётом оставшихся сегментов
            var normalizedTarget = WhitelistPolicy.NormalizeWindowsPath(resolvedTarget);
            if (i < segments.Length - 1)
            {
                var remainingSuffix = string.Join(@"\", segments[(i + 1)..]);
                normalizedTarget = WhitelistPolicy.NormalizeWindowsPath(normalizedTarget + @"\" + remainingSuffix);
            }

            var targetValidation = ValidateNormalizedTarget(normalizedTarget);
            if (!targetValidation.IsAllowed)
            {
                return PathValidationResult.Denied(
                    normalizedPath,
                    PathValidationFailureReason.SymlinkOrJunctionEscapeDetected,
                    $"Заблокирован переход по символической ссылке или junction «{currentPath}» -> «{normalizedTarget}»: целевой путь выходит за пределы разрешённой зоны ({targetValidation.ReasonRu}).");
            }
        }

        return PathValidationResult.Allowed(normalizedPath, string.Empty);
    }

    private bool IsInsideProtectedUserDirectory(string normalizedPath, out string matchedFolder)
    {
        matchedFolder = string.Empty;
        var segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // Формат C:\Users\<Username>\<Subfolder>...
        if (segments.Length >= 4 &&
            string.Equals(segments[1], "Users", StringComparison.OrdinalIgnoreCase))
        {
            var userSubfolder = segments[3];
            foreach (var protectedName in _policy.ProtectedUserSubfolders)
            {
                if (string.Equals(userSubfolder, protectedName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedFolder = string.Join(@"\", segments[..4]);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSafeFirefoxCachePath(string normalizedPath, string firefoxProfilesRoot)
    {
        var relative = normalizedPath[(firefoxProfilesRoot.Length + 1)..];
        var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // Ожидаем: <ProfileName>\cache2\... или <ProfileName>\startupCache\... или <ProfileName>\shader-cache\...
        if (parts.Length < 3)
        {
            return false;
        }

        var subFolder = parts[1];
        return string.Equals(subFolder, "cache2", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(subFolder, "startupCache", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(subFolder, "shader-cache", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(subFolder, "jumpListCache", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameOrSubPath(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsStrictSubPath(candidate, root);
    }

    public static bool IsStrictSubPath(string candidate, string root)
    {
        var rootWithSlash = root.EndsWith('\\') ? root : root + @"\";
        return candidate.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) &&
               candidate.Length > rootWithSlash.Length;
    }

    private static string GetWindowsFileName(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('\\');
        return lastSlash >= 0 && lastSlash < normalizedPath.Length - 1
            ? normalizedPath[(lastSlash + 1)..]
            : normalizedPath;
    }
}
