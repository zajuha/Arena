using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinRepair.Services.Infrastructure;

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool WasCancelled)
{
    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : $"{StandardOutput}{Environment.NewLine}{StandardError}";
}

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Безопасный исполнитель системных процессов (DISM, SFC, netsh, powercfg, reg, schtasks)
/// с поддержкой кодировок консоли Windows (CP866 / UTF-8 / UTF-16) и мягкой отменой.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    static ProcessRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Запуск системной команды: {FileName} {Arguments}", fileName, arguments);

        Encoding consoleEncoding;
        try
        {
            // Для русской консоли Windows стандартной OEM-кодировкой является CP866
            consoleEncoding = Encoding.GetEncoding(866);
        }
        catch
        {
            consoleEncoding = Encoding.UTF8;
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = consoleEncoding,
            StandardErrorEncoding = consoleEncoding
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var cleanLine = e.Data.Replace("\0", string.Empty);
            lock (stdOutBuilder)
            {
                stdOutBuilder.AppendLine(cleanLine);
            }

            onOutputLine?.Invoke(cleanLine);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var cleanLine = e.Data.Replace("\0", string.Empty);
            lock (stdErrBuilder)
            {
                stdErrBuilder.AppendLine(cleanLine);
            }

            onOutputLine?.Invoke(cleanLine);
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessExecutionResult(-1, string.Empty, "Не удалось запустить системный процесс.", false);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Команда {FileName} завершилась с кодом {ExitCode}", fileName, process.ExitCode);
            return new ProcessExecutionResult(
                process.ExitCode,
                stdOutBuilder.ToString(),
                stdErrBuilder.ToString(),
                WasCancelled: false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Операция {FileName} {Arguments} отменена пользователем. Выполняется корректная остановка процесса.", fileName, arguments);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception killEx)
            {
                _logger.LogWarning(killEx, "Не удалось принудительно завершить дочерний процесс {FileName}", fileName);
            }

            return new ProcessExecutionResult(
                -1,
                stdOutBuilder.ToString(),
                stdErrBuilder.ToString(),
                WasCancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при выполнении системной команды {FileName} {Arguments}", fileName, arguments);
            return new ProcessExecutionResult(-1, stdOutBuilder.ToString(), ex.Message, WasCancelled: false);
        }
    }
}

/// <summary>
/// Проверка прав администратора и перезапуск с повышением привилегий (UAC).
/// </summary>
public static class AdminPrivilegeChecker
{
    public static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRestartElevated(string[]? args = null)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args is { Length: > 0 } ? string.Join(" ", args) : string.Empty,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
