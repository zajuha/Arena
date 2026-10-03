using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;
using WinRepair.Services.Infrastructure;

namespace WinRepair.Services.Cleaning;

// 1. СИСТЕМНЫЙ МУСОР (%TEMP%, C:\Windows\Temp, Prefetch, %LOCALAPPDATA%\Temp)
public sealed class SystemJunkCleanupModule : CleanupModuleBase
{
    public SystemJunkCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<SystemJunkCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.system_junk";
    public override string NameRu => "Очистка системного мусора";
    public override string DescriptionRu => "Временные файлы %TEMP%, C:\\Windows\\Temp, %LOCALAPPDATA%\\Temp и кэш Prefetch.";
    public override CleanupCategory Category => CleanupCategory.SystemJunk;
    public override RiskLevel Risk => RiskLevel.Safe;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = @"C:\Users\DefaultUser\AppData\Local";
        }

        yield return (@"C:\Windows\Temp", "*", true);
        yield return (@"C:\Windows\Prefetch", "*.pf", false);
        yield return (Path.Combine(localAppData, "Temp"), "*", true);
    }
}

// 2. КОРЗИНА (все подключённые тома)
public sealed class RecycleBinCleanupModule : CleanupModuleBase
{
    public RecycleBinCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<RecycleBinCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.recycle_bin";
    public override string NameRu => "Очистка Корзины на всех томах";
    public override string DescriptionRu => "Удалённые файлы в каталогах $Recycle.Bin всех подключённых локальных дисков.";
    public override CleanupCategory Category => CleanupCategory.RecycleBin;
    public override RiskLevel Risk => RiskLevel.Low;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            yield return (Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin"), "*", true);
        }
    }

    public override async Task<IReadOnlyList<CleanupItem>> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = (await base.ScanAsync(progress, cancellationToken).ConfigureAwait(false)).ToList();

        // Если прямой обход $Recycle.Bin не вернул элементов, запрашиваем размер через Win32 SHQueryRecycleBinW
        if (items.Count == 0 && OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var info = new Win32NativeMethods.SHQUERYRBINFO
                {
                    cbSize = Marshal.SizeOf<Win32NativeMethods.SHQUERYRBINFO>()
                };

                var hr = Win32NativeMethods.SHQueryRecycleBinW(drive.RootDirectory.FullName, ref info);
                if (hr == 0 && info.i64Size > 0)
                {
                    items.Add(new CleanupItem(
                        Id: Guid.NewGuid().ToString("N"),
                        FullPath: Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", $"Корзина тома {drive.Name.TrimEnd('\\')} ({info.i64NumItems} объектов)"),
                        ModuleId: Id,
                        ModuleNameRu: NameRu,
                        Category: Category,
                        SizeBytes: info.i64Size,
                        LastModifiedUtc: DateTimeOffset.UtcNow,
                        Risk: Risk,
                        DescriptionRu: $"Объектов в Корзине на диске {drive.Name.TrimEnd('\\')}: {info.i64NumItems}"));
                }
            }
        }

        return items;
    }
}

// 3. КЭШ ОБНОВЛЕНИЙ WINDOWS (C:\Windows\SoftwareDistribution\Download)
public sealed class WindowsUpdateCacheCleanupModule : CleanupModuleBase
{
    public WindowsUpdateCacheCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<WindowsUpdateCacheCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.wu_cache";
    public override string NameRu => "Очистка кэша обновлений Windows";
    public override string DescriptionRu => "Скачанные установочные пакеты обновлений в C:\\Windows\\SoftwareDistribution\\Download.";
    public override CleanupCategory Category => CleanupCategory.WindowsUpdateCache;
    public override RiskLevel Risk => RiskLevel.Safe;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        yield return (@"C:\Windows\SoftwareDistribution\Download", "*", true);
    }
}

// 4. КЭШ БРАУЗЕРОВ (Edge, Chrome, Firefox, Яндекс.Браузер, Opera — ТОЛЬКО кэш, не пароли и не история)
public sealed class BrowserCacheCleanupModule : CleanupModuleBase
{
    public BrowserCacheCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<BrowserCacheCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.browser_cache";
    public override string NameRu => "Очистка кэша браузеров";
    public override string DescriptionRu => "Временный кэш страниц и шейдеров Microsoft Edge, Google Chrome, Mozilla Firefox, Яндекс.Браузера и Opera (пароли, история и куки не затрагиваются).";
    public override CleanupCategory Category => CleanupCategory.BrowserCache;
    public override RiskLevel Risk => RiskLevel.Safe;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = @"C:\Users\DefaultUser\AppData\Local";
        }

        yield return (Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache"), "*", true);
        yield return (Path.Combine(local, @"Microsoft\Edge\User Data\Default\Code Cache"), "*", true);
        yield return (Path.Combine(local, @"Microsoft\Edge\User Data\Default\GPUCache"), "*", true);
        yield return (Path.Combine(local, @"Google\Chrome\User Data\Default\Cache"), "*", true);
        yield return (Path.Combine(local, @"Google\Chrome\User Data\Default\Code Cache"), "*", true);
        yield return (Path.Combine(local, @"Google\Chrome\User Data\Default\GPUCache"), "*", true);
        yield return (Path.Combine(local, @"Yandex\YandexBrowser\User Data\Default\Cache"), "*", true);
        yield return (Path.Combine(local, @"Yandex\YandexBrowser\User Data\Default\Code Cache"), "*", true);
        yield return (Path.Combine(local, @"Yandex\YandexBrowser\User Data\Default\GPUCache"), "*", true);
        yield return (Path.Combine(local, @"Opera Software\Opera Stable\Cache"), "*", true);
        yield return (Path.Combine(local, @"Opera Software\Opera Stable\System Cache"), "*", true);

        var ffProfiles = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ffProfiles))
        {
            foreach (var profileDir in Directory.EnumerateDirectories(ffProfiles))
            {
                yield return (Path.Combine(profileDir, "cache2"), "*", true);
                yield return (Path.Combine(profileDir, "startupCache"), "*", true);
                yield return (Path.Combine(profileDir, "shader-cache"), "*", true);
            }
        }
    }
}

// 5. ЭСКИЗЫ И КЭШ ИКОНОК (thumbcache_*.db, iconcache_*.db)
public sealed class ThumbnailsAndIconsCleanupModule : CleanupModuleBase
{
    public ThumbnailsAndIconsCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<ThumbnailsAndIconsCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.thumbnails";
    public override string NameRu => "Очистка эскизов и кэша иконок";
    public override string DescriptionRu => "Файлы кэша миниатюр проводника (thumbcache_*.db) и иконок (iconcache_*.db).";
    public override CleanupCategory Category => CleanupCategory.ThumbnailsAndIcons;
    public override RiskLevel Risk => RiskLevel.Safe;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = @"C:\Users\DefaultUser\AppData\Local";
        }

        var explorerDir = Path.Combine(local, @"Microsoft\Windows\Explorer");
        yield return (explorerDir, "thumbcache_*.db", false);
        yield return (explorerDir, "iconcache_*.db", false);
    }
}

// 6. ДАМПЫ И ЛОГИ (*.dmp, *.log в системных папках)
public sealed class DumpsAndLogsCleanupModule : CleanupModuleBase
{
    public DumpsAndLogsCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<DumpsAndLogsCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.dumps_logs";
    public override string NameRu => "Очистка аварийных дампов и логов";
    public override string DescriptionRu => "Мини-дампы ядра (Minidump, LiveKernelReports) и аварийные дампы приложений (CrashDumps).";
    public override CleanupCategory Category => CleanupCategory.DumpsAndLogs;
    public override RiskLevel Risk => RiskLevel.Safe;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = @"C:\Users\DefaultUser\AppData\Local";
        }

        yield return (@"C:\Windows\Minidump", "*.dmp", false);
        yield return (@"C:\Windows\LiveKernelReports", "*.dmp", true);
        yield return (Path.Combine(local, "CrashDumps"), "*.dmp", false);
    }
}

// 7. КЭШ DIRECTX И ШЕЙДЕРОВ (D3DSCache, кэш GPU-драйверов)
public sealed class DirectXShaderCacheCleanupModule : CleanupModuleBase
{
    public DirectXShaderCacheCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<DirectXShaderCacheCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.shader_cache";
    public override string NameRu => "Очистка кэша DirectX и шейдеров GPU";
    public override string DescriptionRu => "Скомпилированные шейдеры DirectX (D3DSCache) и кэши видеодрайверов NVIDIA, AMD и Intel.";
    public override CleanupCategory Category => CleanupCategory.DirectXAndShaders;
    public override RiskLevel Risk => RiskLevel.Safe;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = @"C:\Users\DefaultUser\AppData\Local";
        }

        yield return (Path.Combine(local, "D3DSCache"), "*", true);
        yield return (Path.Combine(local, @"NVIDIA\DXCache"), "*", true);
        yield return (Path.Combine(local, @"NVIDIA\GLCache"), "*", true);
        yield return (Path.Combine(local, @"AMD\DxCache"), "*", true);
        yield return (Path.Combine(local, @"Intel\ShaderCache"), "*", true);
    }
}

// 8. ОСТАТКИ УДАЛЁННЫХ ПРОГРАММ (пустые/осиротевшие папки и битые записи деинсталляции)
public sealed class OrphanedProgramRemnantsCleanupModule : ICleanupModule
{
    private readonly IProgramManagerService _programManager;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;

    public OrphanedProgramRemnantsCleanupModule(
        IProgramManagerService programManager,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService)
    {
        _programManager = programManager;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
    }

    public string Id => "clean.program_remnants";
    public string NameRu => "Очистка остатков удалённых программ";
    public string DescriptionRu => "Пустые и осиротевшие папки удалённых приложений и недействительные записи деинсталляции в реестре (с обязательным .reg-бэкапом).";
    public CleanupCategory Category => CleanupCategory.UninstalledProgramRemnants;
    public RiskLevel Risk => RiskLevel.Low;

    public async Task<IReadOnlyList<CleanupItem>> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var remnants = await _programManager.ScanForUninstalledProgramRemnantsAsync(progress, cancellationToken).ConfigureAwait(false);
        return remnants.Select(r => new CleanupItem(
            Id: r.Id,
            FullPath: r.PathOrKey,
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            SizeBytes: r.SizeBytes,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            Risk: r.Risk,
            DescriptionRu: $"{r.FormerApplicationName}: {r.EvidenceReasonRu}",
            IsSelected: r.IsSelected,
            IsRegistryEntry: r.Kind == RemnantKind.RegistryKey)).ToList();
    }

    public async Task<CleanupPreview> PreviewAsync(
        IReadOnlyList<CleanupItem>? preScannedItems = null,
        CancellationToken cancellationToken = default)
    {
        var items = preScannedItems ?? await ScanAsync(null, cancellationToken).ConfigureAwait(false);
        return new CleanupPreview(
            PreviewId: Guid.NewGuid(),
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Items: items,
            TotalBytes: items.Sum(i => i.SizeBytes),
            SafetyNoticeRu: "Остатки удалённых программ проверяются по списку активных приложений и служб. Перед удалением любого ключа реестра автоматически создаётся резервная копия .reg.");
    }

    public async Task<CleanupExecutionResult> ExecuteAsync(
        CleanupPreview approvedPreview,
        CleanupExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new CleanupExecutionResult(Id, NameRu, false, false, 0, 0, 0, 0, null, Guid.Empty, false, reasonRu, []);
            }
        }

        var selected = approvedPreview.Items.Where(i => i.IsSelected).ToList();
        var mapped = selected.Select(i => new ProgramRemnantEntry(
            i.Id,
            i.ModuleNameRu,
            i.IsRegistryEntry ? RemnantKind.RegistryKey : RemnantKind.FileSystemFolder,
            i.IsRegistryEntry ? "Ключ реестра" : "Папка на диске",
            i.FullPath,
            i.SizeBytes,
            i.Risk,
            i.DescriptionRu,
            true)).ToList();

        var (succeeded, removedCount, messageRu) = await _programManager.RemoveSelectedRemnantsAsync(
            mapped,
            options.MoveToQuarantine,
            cancellationToken).ConfigureAwait(false);

        var freed = selected.Take(removedCount).Sum(i => i.SizeBytes);
        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Cleanup,
            Id,
            NameRu,
            messageRu,
            Risk,
            succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            freed,
            null,
            null,
            null,
            CanRollback: true), cancellationToken).ConfigureAwait(false);

        return new CleanupExecutionResult(
            Id, NameRu, succeeded, false, freed, removedCount, removedCount,
            Math.Max(0, selected.Count - removedCount), null, log.Id, _safetyGuard.HasSessionRestorePoint, messageRu, []);
    }
}

// 9. ОТЧЁТНОСТЬ WINDOWS (Windows Error Reporting, CBS.log, DISM.log)
public sealed class WindowsErrorReportingCleanupModule : CleanupModuleBase
{
    public WindowsErrorReportingCleanupModule(
        PathValidator pathValidator,
        ISessionSafetyGuard safetyGuard,
        IQuarantineService quarantineService,
        IOperationJournalService journalService,
        ILogger<WindowsErrorReportingCleanupModule> logger)
        : base(pathValidator, safetyGuard, quarantineService, journalService, logger)
    {
    }

    public override string Id => "clean.wer_cbs";
    public override string NameRu => "Очистка отчётов об ошибках Windows (WER, CBS)";
    public override string DescriptionRu => "Архивы и очереди Windows Error Reporting, старые журналы обслуживания CBS и DISM.";
    public override CleanupCategory Category => CleanupCategory.WindowsErrorReporting;
    public override RiskLevel Risk => RiskLevel.Safe;

    protected override IEnumerable<(string DirectoryPath, string SearchPattern, bool Recurse)> GetTargetDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = @"C:\Users\DefaultUser\AppData\Local";
        }

        yield return (@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", "*", true);
        yield return (@"C:\ProgramData\Microsoft\Windows\WER\ReportQueue", "*", true);
        yield return (@"C:\ProgramData\Microsoft\Windows\WER\Temp", "*", true);
        yield return (Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), "*", true);
        yield return (Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"), "*", true);
        yield return (@"C:\Windows\Logs\CBS", "CBSPersist_*.log", false);
        yield return (@"C:\Windows\Logs\DISM", "dism.log.bak", false);
        yield return (@"C:\Windows\Logs\MoSetup", "*.log", false);
    }
}

// 10. СТАРЫЕ ОБНОВЛЕНИЯ WINDOWS (DISM /Online /Cleanup-Image /StartComponentCleanup)
public sealed class OldWindowsUpdatesDismCleanupModule : ICleanupModule
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionSafetyGuard _safetyGuard;
    private readonly IOperationJournalService _journalService;
    private readonly ILogger<OldWindowsUpdatesDismCleanupModule> _logger;

    public OldWindowsUpdatesDismCleanupModule(
        IProcessRunner processRunner,
        ISessionSafetyGuard safetyGuard,
        IOperationJournalService journalService,
        ILogger<OldWindowsUpdatesDismCleanupModule> logger)
    {
        _processRunner = processRunner;
        _safetyGuard = safetyGuard;
        _journalService = journalService;
        _logger = logger;
    }

    public string Id => "clean.dism_components";
    public string NameRu => "Очистка устаревших обновлений (DISM StartComponentCleanup)";
    public string DescriptionRu => "Штатная очистка замещённых версий компонентов в хранилище WinSxS через DISM /Online /Cleanup-Image /StartComponentCleanup.";
    public CleanupCategory Category => CleanupCategory.OldWindowsUpdatesDism;
    public RiskLevel Risk => RiskLevel.Low;

    public async Task<IReadOnlyList<CleanupItem>> ScanAsync(
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressReport(NameRu, "Анализ хранилища WinSxS через DISM /AnalyzeComponentStore...", 35));

        var res = await _processRunner.RunAsync(
            "dism.exe",
            "/Online /Cleanup-Image /AnalyzeComponentStore",
            line =>
            {
                var pct = SystemOutputParser.TryParseProgressPercentage(line);
                if (pct.HasValue)
                {
                    progress?.Report(new ProgressReport(NameRu, $"Анализ WinSxS ({pct.Value:F0}%)", pct.Value));
                }
            },
            cancellationToken).ConfigureAwait(false);

        // Оцениваем объём устаревших пакетов WinSxS, если DISM сообщает о рекомендуемой очистке
        var reclaimableEstimate = res.CombinedOutput.Contains("Yes", StringComparison.OrdinalIgnoreCase) ||
                                  res.CombinedOutput.Contains("Да", StringComparison.OrdinalIgnoreCase)
            ? 850L * 1024 * 1024
            : 0L;

        if (reclaimableEstimate == 0)
        {
            return [];
        }

        return
        [
            new CleanupItem(
                Id: "dism_start_component_cleanup",
                FullPath: @"C:\Windows\WinSxS (штатное обслуживание DISM /StartComponentCleanup)",
                ModuleId: Id,
                ModuleNameRu: NameRu,
                Category: Category,
                SizeBytes: reclaimableEstimate,
                LastModifiedUtc: DateTimeOffset.UtcNow,
                Risk: Risk,
                DescriptionRu: "Устаревшие ревизии пакетов обновлений Windows в хранилище WinSxS (удаляются только штатным механизмом DISM, без прямого удаления файлов).")
        ];
    }

    public async Task<CleanupPreview> PreviewAsync(
        IReadOnlyList<CleanupItem>? preScannedItems = null,
        CancellationToken cancellationToken = default)
    {
        var items = preScannedItems ?? await ScanAsync(null, cancellationToken).ConfigureAwait(false);
        return new CleanupPreview(
            PreviewId: Guid.NewGuid(),
            ModuleId: Id,
            ModuleNameRu: NameRu,
            Category: Category,
            Risk: Risk,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Items: items,
            TotalBytes: items.Sum(i => i.SizeBytes),
            SafetyNoticeRu: "Внимание: папка C:\\Windows\\WinSxS никогда не очищается прямым удалением файлов. Выполняется исключительно штатная команда Microsoft: DISM.exe /Online /Cleanup-Image /StartComponentCleanup.",
            UsesDismComponentCleanup: true);
    }

    public async Task<CleanupExecutionResult> ExecuteAsync(
        CleanupPreview approvedPreview,
        CleanupExecutionOptions options,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.EnsureRestorePointCreated)
        {
            var (allowed, reasonRu) = await _safetyGuard.EnsureSafeToMutateSystemAsync(NameRu, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return new CleanupExecutionResult(Id, NameRu, false, false, 0, 0, 0, 0, null, Guid.Empty, false, reasonRu, []);
            }
        }

        progress?.Report(new ProgressReport(NameRu, "Запуск DISM /Online /Cleanup-Image /StartComponentCleanup...", 10));

        var execResult = await _processRunner.RunAsync(
            "dism.exe",
            "/Online /Cleanup-Image /StartComponentCleanup",
            line =>
            {
                var pct = SystemOutputParser.TryParseProgressPercentage(line);
                if (pct.HasValue)
                {
                    progress?.Report(new ProgressReport(NameRu, $"Очистка компонентов WinSxS ({pct.Value:F0}%)", pct.Value));
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (execResult.WasCancelled)
        {
            return new CleanupExecutionResult(
                Id, NameRu, false, true, 0, 0, 0, 0, null, Guid.Empty, _safetyGuard.HasSessionRestorePoint,
                "Очистка компонентов DISM была отменена пользователем.", []);
        }

        var parsed = SystemOutputParser.ParseDismOutput(execResult.CombinedOutput, execResult.ExitCode);
        var freed = parsed.Succeeded ? approvedPreview.SelectedBytes : 0L;

        var log = await _journalService.RecordAsync(new OperationLog(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OperationType.Cleanup,
            Id,
            NameRu,
            parsed.SummaryRu,
            Risk,
            parsed.Succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            freed,
            null,
            null,
            null,
            CanRollback: false), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Завершена очистка DISM StartComponentCleanup: {Summary}", parsed.SummaryRu);

        return new CleanupExecutionResult(
            Id, NameRu, parsed.Succeeded, false, freed,
            parsed.Succeeded ? 1 : 0, 0, parsed.Succeeded ? 0 : 1,
            null, log.Id, _safetyGuard.HasSessionRestorePoint, parsed.SummaryRu, []);
    }
}
