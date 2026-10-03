using System.Collections.ObjectModel;

namespace WinRepair.Core.Safety;

/// <summary>
/// Политика белых и чёрных списков путей для всех операций очистки и удаления.
/// Удаление разрешено ТОЛЬКО внутри явно перечисленных корней белого списка,
/// и КАТЕГОРИЧЕСКИ ЗАПРЕЩЕНО для системных ядерных каталогов и личных папок пользователя.
/// </summary>
public sealed class WhitelistPolicy
{
    private readonly List<string> _allowedRoots;
    private readonly List<string> _forbiddenRoots;
    private readonly HashSet<string> _forbiddenFileNames;
    private readonly List<string> _protectedUserSubfolders;

    public WhitelistPolicy(
        IEnumerable<string>? additionalAllowedRoots = null,
        string systemDrive = @"C:",
        string? userProfilePath = null,
        string? localAppDataPath = null,
        string? appDataRoamingPath = null)
    {
        var normalizedDrive = systemDrive.TrimEnd('\\', '/');
        if (!normalizedDrive.EndsWith(':'))
        {
            normalizedDrive += ":";
        }

        var defaultUserProfile = !string.IsNullOrWhiteSpace(userProfilePath)
            ? userProfilePath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(defaultUserProfile) || !defaultUserProfile.Contains(':'))
        {
            defaultUserProfile = $@"{normalizedDrive}\Users\DefaultUser";
        }

        var localAppData = !string.IsNullOrWhiteSpace(localAppDataPath)
            ? localAppDataPath
            : $@"{defaultUserProfile}\AppData\Local";

        var roamingAppData = !string.IsNullOrWhiteSpace(appDataRoamingPath)
            ? appDataRoamingPath
            : $@"{defaultUserProfile}\AppData\Roaming";

        _allowedRoots =
        [
            // 1. Системный мусор
            $@"{normalizedDrive}\Windows\Temp",
            $@"{normalizedDrive}\Windows\Prefetch",
            $@"{localAppData}\Temp",

            // 2. Корзина на системном и дополнительных дисках
            $@"{normalizedDrive}\$Recycle.Bin",
            @"D:\$Recycle.Bin",
            @"E:\$Recycle.Bin",
            @"F:\$Recycle.Bin",

            // 3. Кэш обновлений Windows
            $@"{normalizedDrive}\Windows\SoftwareDistribution\Download",

            // 4. Кэши браузеров (ТОЛЬКО папки кэша, не профили, пароли или история)
            $@"{localAppData}\Microsoft\Edge\User Data\Default\Cache",
            $@"{localAppData}\Microsoft\Edge\User Data\Default\Code Cache",
            $@"{localAppData}\Microsoft\Edge\User Data\Default\GPUCache",
            $@"{localAppData}\Google\Chrome\User Data\Default\Cache",
            $@"{localAppData}\Google\Chrome\User Data\Default\Code Cache",
            $@"{localAppData}\Google\Chrome\User Data\Default\GPUCache",
            $@"{localAppData}\Yandex\YandexBrowser\User Data\Default\Cache",
            $@"{localAppData}\Yandex\YandexBrowser\User Data\Default\Code Cache",
            $@"{localAppData}\Yandex\YandexBrowser\User Data\Default\GPUCache",
            $@"{localAppData}\Opera Software\Opera Stable\Cache",
            $@"{localAppData}\Opera Software\Opera Stable\System Cache",
            $@"{localAppData}\Mozilla\Firefox\Profiles",

            // 5. Эскизы и кэш иконок
            $@"{localAppData}\Microsoft\Windows\Explorer",

            // 6. Дампы памяти и отчёты об ошибках
            $@"{normalizedDrive}\Windows\Minidump",
            $@"{normalizedDrive}\Windows\LiveKernelReports",
            $@"{localAppData}\CrashDumps",
            $@"{normalizedDrive}\ProgramData\Microsoft\Windows\WER\ReportArchive",
            $@"{normalizedDrive}\ProgramData\Microsoft\Windows\WER\ReportQueue",
            $@"{normalizedDrive}\ProgramData\Microsoft\Windows\WER\Temp",
            $@"{localAppData}\Microsoft\Windows\WER\ReportArchive",
            $@"{localAppData}\Microsoft\Windows\WER\ReportQueue",

            // 7. Кэш DirectX и шейдеров GPU
            $@"{localAppData}\D3DSCache",
            $@"{localAppData}\NVIDIA\DXCache",
            $@"{localAppData}\NVIDIA\GLCache",
            $@"{localAppData}\AMD\DxCache",
            $@"{localAppData}\Intel\ShaderCache",

            // 8. Логи CBS и установки драйверов (только файлы журналов)
            $@"{normalizedDrive}\Windows\Logs\CBS",
            $@"{normalizedDrive}\Windows\Logs\DISM",
            $@"{normalizedDrive}\Windows\Logs\MoSetup",

            // 9. Карантин и временные папки самой утилиты
            $@"{normalizedDrive}\ProgramData\WinRepair\Temp"
        ];

        if (additionalAllowedRoots is not null)
        {
            foreach (var root in additionalAllowedRoots)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    _allowedRoots.Add(NormalizeWindowsPath(root));
                }
            }
        }

        for (var i = 0; i < _allowedRoots.Count; i++)
        {
            _allowedRoots[i] = NormalizeWindowsPath(_allowedRoots[i]);
        }

        _forbiddenRoots =
        [
            NormalizeWindowsPath($@"{normalizedDrive}\Windows\System32"),
            NormalizeWindowsPath($@"{normalizedDrive}\Windows\SysWOW64"),
            NormalizeWindowsPath($@"{normalizedDrive}\Windows\WinSxS"),
            NormalizeWindowsPath($@"{normalizedDrive}\Windows\Boot"),
            NormalizeWindowsPath($@"{normalizedDrive}\Windows\SystemApps"),
            NormalizeWindowsPath($@"{normalizedDrive}\Windows\servicing"),
            NormalizeWindowsPath($@"{normalizedDrive}\Windows\Fonts"),
            NormalizeWindowsPath($@"{normalizedDrive}\EFI"),
            NormalizeWindowsPath($@"{normalizedDrive}\Boot"),
            NormalizeWindowsPath($@"{normalizedDrive}\Recovery"),
            NormalizeWindowsPath($@"{normalizedDrive}\System Volume Information"),
            NormalizeWindowsPath($@"{roamingAppData}\Microsoft\Credentials"),
            NormalizeWindowsPath($@"{localAppData}\Microsoft\Credentials")
        ];

        _protectedUserSubfolders =
        [
            "Documents",
            "My Documents",
            "Документы",
            "Desktop",
            "Рабочий стол",
            "Pictures",
            "Изображения",
            "Videos",
            "Видео",
            "Music",
            "Музыка",
            "Downloads",
            "Загрузки",
            "OneDrive",
            "Saved Games",
            "Сохранённые игры",
            "Favorites",
            "Избранное",
            "Contacts",
            "Контакты"
        ];

        _forbiddenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pagefile.sys",
            "hiberfil.sys",
            "swapfile.sys",
            "bootmgr",
            "bootmgr.efi",
            "ntldr",
            "bcd",
            "ntuser.dat",
            "usrclass.dat",
            "sam",
            "security",
            "software",
            "system",
            "default",
            "login data",
            "cookies",
            "web data",
            "bookmarks",
            "places.sqlite",
            "key4.db",
            "logins.json"
        };
    }

    public IReadOnlyList<string> AllowedRoots => new ReadOnlyCollection<string>(_allowedRoots);

    public IReadOnlyList<string> ForbiddenRoots => new ReadOnlyCollection<string>(_forbiddenRoots);

    public IReadOnlySet<string> ForbiddenFileNames => _forbiddenFileNames;

    public IReadOnlyList<string> ProtectedUserSubfolders => new ReadOnlyCollection<string>(_protectedUserSubfolders);

    /// <summary>
    /// Нормализует путь в канонический Windows-формат (обратные слэши, без завершающего слэша, кроме корня диска).
    /// </summary>
    public static string NormalizeWindowsPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var path = rawPath.Trim();

        // Убираем префикс длинного пути \\?\ для единообразного сопоставления
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        path = path.Replace('/', '\\');

        // Схлопываем дублирующиеся обратные слэши (кроме UNC-префикса в начале)
        while (path.Contains(@"\\", StringComparison.Ordinal))
        {
            path = path.Replace(@"\\", @"\");
        }

        // Разрешаем сегменты "." и ".." детерминированно
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                // Не позволяем подняться выше корня диска (например C:)
                if (stack.Count > 1)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else
            {
                stack.Add(part);
            }
        }

        if (stack.Count == 0)
        {
            return string.Empty;
        }

        if (stack.Count == 1 && stack[0].EndsWith(':'))
        {
            return stack[0] + @"\";
        }

        return string.Join(@"\", stack);
    }
}
