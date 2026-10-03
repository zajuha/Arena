using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;

namespace WinRepair.Services.Backup;

/// <summary>
/// Гарант создания точки восстановления перед первой изменяющей операцией в сессии (п. 4.1 ТЗ).
/// Если создать точку восстановления не удалось, любая изменяющая операцию блокируется.
/// </summary>
public sealed class SessionSafetyGuard : ISessionSafetyGuard
{
    private readonly IBackupService _backupService;
    private readonly ILogger<SessionSafetyGuard> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public bool HasSessionRestorePoint { get; private set; }
    public string? SessionRestorePointDescription { get; private set; }
    public DateTimeOffset? CreatedAtUtc { get; private set; }

    public SessionSafetyGuard(IBackupService backupService, ILogger<SessionSafetyGuard> logger)
    {
        _backupService = backupService;
        _logger = logger;
    }

    public async Task<(bool Allowed, string ReasonRu)> EnsureSafeToMutateSystemAsync(
        string initiatingOperationTitleRu,
        CancellationToken cancellationToken = default)
    {
        if (HasSessionRestorePoint)
        {
            return (true, $"Точка восстановления для текущей сессии уже создана: «{SessionRestorePointDescription}».");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasSessionRestorePoint)
            {
                return (true, $"Точка восстановления для текущей сессии уже создана: «{SessionRestorePointDescription}».");
            }

            var description = $"WinRepair: перед операцией «{initiatingOperationTitleRu}» ({DateTime.Now:dd.MM.yyyy HH:mm})";
            var (succeeded, messageRu) = await _backupService.CreateRestorePointAsync(description, cancellationToken).ConfigureAwait(false);

            if (!succeeded)
            {
                _logger.LogError(
                    "Операция «{Operation}» заблокирована: не удалось создать обязательную точку восстановления. Причина: {Reason}",
                    initiatingOperationTitleRu,
                    messageRu);

                return (false,
                    $"Операция «{initiatingOperationTitleRu}» отменена в целях безопасности: не удалось создать контрольную точку восстановления Windows. {messageRu}");
            }

            HasSessionRestorePoint = true;
            SessionRestorePointDescription = description;
            CreatedAtUtc = DateTimeOffset.UtcNow;

            return (true, $"Создана точка восстановления системы: «{description}».");
        }
        finally
        {
            _mutex.Release();
        }
    }
}
