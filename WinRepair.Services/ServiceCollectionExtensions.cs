using Microsoft.Extensions.DependencyInjection;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Safety;
using WinRepair.Services.Backup;
using WinRepair.Services.Cleaning;
using WinRepair.Services.Infrastructure;
using WinRepair.Services.Optimization;
using WinRepair.Services.Repairing;
using WinRepair.Services.Reporting;
using WinRepair.Services.Scanning;

namespace WinRepair.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWinRepairServices(this IServiceCollection services)
    {
        // Ядро безопасности
        services.AddSingleton<WhitelistPolicy>(_ => new WhitelistPolicy());
        services.AddSingleton<IFileSystemInspector, PhysicalFileSystemInspector>();
        services.AddSingleton<PathValidator>();

        // Инфраструктура и резервное копирование
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<ISessionSafetyGuard, SessionSafetyGuard>();
        services.AddSingleton<IQuarantineService, QuarantineService>();
        services.AddSingleton<IOperationJournalService, OperationJournalService>();
        services.AddSingleton<ISchedulerAndProfileService, SchedulerAndProfileService>();

        // Сканеры и сервисы
        services.AddSingleton<IDiagnosticService, DiagnosticService>();
        services.AddSingleton<ILargeAndDuplicateFileScanner, LargeAndDuplicateFileScanner>();
        services.AddSingleton<IOptimizationService, OptimizationService>();
        services.AddSingleton<IProgramManagerService, ProgramManagerService>();
        services.AddSingleton<IReportService, ReportService>();

        // 10 модулей очистки (ICleanupModule)
        services.AddSingleton<ICleanupModule, SystemJunkCleanupModule>();
        services.AddSingleton<ICleanupModule, RecycleBinCleanupModule>();
        services.AddSingleton<ICleanupModule, WindowsUpdateCacheCleanupModule>();
        services.AddSingleton<ICleanupModule, BrowserCacheCleanupModule>();
        services.AddSingleton<ICleanupModule, ThumbnailsAndIconsCleanupModule>();
        services.AddSingleton<ICleanupModule, DumpsAndLogsCleanupModule>();
        services.AddSingleton<ICleanupModule, DirectXShaderCacheCleanupModule>();
        services.AddSingleton<ICleanupModule, OrphanedProgramRemnantsCleanupModule>();
        services.AddSingleton<ICleanupModule, WindowsErrorReportingCleanupModule>();
        services.AddSingleton<ICleanupModule, OldWindowsUpdatesDismCleanupModule>();

        // 9 модулей восстановления (IRepairModule)
        services.AddSingleton<IRepairModule, SystemFilesSfcRepairModule>();
        services.AddSingleton<IRepairModule, ComponentStoreDismRepairModule>();
        services.AddSingleton<IRepairModule, NetworkStackRepairModule>();
        services.AddSingleton<IRepairModule, WindowsUpdateRepairModule>();
        services.AddSingleton<IRepairModule, FileAssociationsRepairModule>();
        services.AddSingleton<IRepairModule, PowerSchemesRepairModule>();
        services.AddSingleton<IRepairModule, DiskCheckChkdskRepairModule>();
        services.AddSingleton<IRepairModule, BootloaderRepairModule>();
        services.AddSingleton<IRepairModule, SystemRestorePointsRepairModule>();

        return services;
    }
}
