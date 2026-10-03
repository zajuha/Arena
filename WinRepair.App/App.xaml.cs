using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Wpf.Ui.Appearance;
using WinRepair.App.ViewModels;
using WinRepair.App.Views;
using WinRepair.Services;
using WinRepair.Services.Infrastructure;

namespace WinRepair.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\ProgramData";
        }

        var logsDir = Path.Combine(programData, "WinRepair", "Logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logsDir, "winrepair-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Запуск приложения «Ремонт и очистка Windows» v1.0.0");

        // Проверка прав администратора (п. 6.2 и п. 8 ТЗ): при запуске без прав не падаем,
        // а показываем понятное русское сообщение с предложением перезапуска с повышением привилегий.
        if (!AdminPrivilegeChecker.IsRunningAsAdministrator())
        {
            Log.Warning("Приложение запущено без прав администратора. Предложен перезапуск с повышением привилегий.");
            var choice = MessageBox.Show(
                "Для диагностики дисков, проверки целостности системных файлов (DISM/SFC), создания точек восстановления и очистки системных папок программе «Ремонт и очистка Windows» требуются права администратора.\n\n" +
                "Нажмите «Да», чтобы перезапустить программу от имени администратора.",
                "Требуются права администратора — Ремонт и очистка Windows",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Yes)
            {
                AdminPrivilegeChecker.TryRestartElevated(e.Args);
            }

            Shutdown(0);
            return;
        }

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddWinRepairServices();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var mainVm = _host.Services.GetRequiredService<MainViewModel>();
        mainVm.ThemeChangeRequested = themeName =>
        {
            var targetTheme = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(targetTheme);
        };

        await mainVm.InitializeAsync();

        // Поддержка автоматического запуска безопасной очистки из Планировщика заданий Windows
        if (e.Args.Any(a => string.Equals(a, "--scheduled-safe-clean", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Information("Выполняется плановая безопасная очистка по расписанию Task Scheduler...");
            await mainVm.Overview.FixAllSafeInternalAsync(CancellationToken.None);
            await _host.StopAsync();
            Shutdown(0);
            return;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
