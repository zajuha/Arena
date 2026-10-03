using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;
using WinRepair.Services.Infrastructure;

namespace WinRepair.Services.Backup;

/// <summary>
/// Полный журнал операций с возможностью отката по каждому пункту (п. 3.6 и п. 4.8 ТЗ).
/// Хранится в %ProgramData%\WinRepair\Journal\operations.json (сохраняется при удалении приложения).
/// </summary>
public sealed class OperationJournalService : IOperationJournalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IQuarantineService _quarantineService;
    private readonly IBackupService _backupService;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<OperationJournalService> _logger;
    private readonly string _journalFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OperationJournalService(
        IQuarantineService quarantineService,
        IBackupService backupService,
        IProcessRunner processRunner,
        ILogger<OperationJournalService> logger,
        string? customJournalDirectory = null)
    {
        _quarantineService = quarantineService;
        _backupService = backupService;
        _processRunner = processRunner;
        _logger = logger;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\ProgramData";
        }

        var dir = customJournalDirectory ?? Path.Combine(programData, "WinRepair", "Journal");
        Directory.CreateDirectory(dir);
        _journalFilePath = Path.Combine(dir, "operations.json");
    }

    public async Task<OperationLog> RecordAsync(
        OperationLog entry,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await ReadAllInternalAsync(cancellationToken).ConfigureAwait(false);
            list.Insert(0, entry);

            await SaveAllInternalAsync(list, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Журнал операций [{Id}]: {Title} | Статус: {Outcome} | Карантин: {BatchId}",
                entry.Id,
                entry.TitleRu,
                entry.OutcomeRu,
                entry.QuarantineBatchId ?? "-");

            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<OperationLog>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAllInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string MessageRu)> RollbackOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        OperationLog? target;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await ReadAllInternalAsync(cancellationToken).ConfigureAwait(false);
            target = list.FirstOrDefault(x => x.Id == operationId);
        }
        finally
        {
            _lock.Release();
        }

        if (target is null)
        {
            return (false, "Запись операции не найдена в журнале.");
        }

        if (target.IsRolledBack)
        {
            return (false, "Данная операция уже была откачена ранее.");
        }

        if (!target.CanRollback)
        {
            return (false, "Для данной операции автоматический поштучный откат не предусмотрен (используйте созданную точку восстановления системы).");
        }

        var messages = new List<string>();
        var anySuccess = false;

        // 1. Восстановление файлов из карантина, если привязан пакет карантина
        if (!string.IsNullOrWhiteSpace(target.QuarantineBatchId))
        {
            var (qSuccess, _, qMsg) = await _quarantineService.RestoreBatchAsync(target.QuarantineBatchId, cancellationToken).ConfigureAwait(false);
            messages.Add(qMsg);
            if (qSuccess)
            {
                anySuccess = true;
            }
        }

        // 2. Импорт резервной копии ветки реестра (.reg)
        if (!string.IsNullOrWhiteSpace(target.RegistryBackupFilePath))
        {
            var (regSuccess, regMsg) = await _backupService.ImportRegistryBackupAsync(target.RegistryBackupFilePath, cancellationToken).ConfigureAwait(false);
            messages.Add(regMsg);
            if (regSuccess)
            {
                anySuccess = true;
            }
        }

        // 3. Обратная системная команда (например, возврат режима запуска службы или схемы питания)
        if (!string.IsNullOrWhiteSpace(target.RestoreReverseCommand))
        {
            var res = await _processRunner.RunAsync("cmd.exe", $"/c {target.RestoreReverseCommand}", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (res.ExitCode == 0)
            {
                anySuccess = true;
                messages.Add("Выполнена команда возврата исходных параметров.");
            }
            else
            {
                messages.Add($"Команда отката вернула код {res.ExitCode}: {res.CombinedOutput.Trim()}");
            }
        }

        if (anySuccess)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var list = await ReadAllInternalAsync(cancellationToken).ConfigureAwait(false);
                var idx = list.FindIndex(x => x.Id == operationId);
                if (idx >= 0)
                {
                    list[idx] = list[idx] with
                    {
                        Outcome = OperationOutcome.RolledBack,
                        IsRolledBack = true
                    };
                    await SaveAllInternalAsync(list, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        var combinedMessage = string.Join(" ", messages);
        _logger.LogInformation("Откат операции {Id}: Succeeded={Success}, Details={Details}", operationId, anySuccess, combinedMessage);
        return (anySuccess, combinedMessage);
    }

    private async Task<List<OperationLog>> ReadAllInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalFilePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_journalFilePath);
            var items = await JsonSerializer.DeserializeAsync<List<OperationLog>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return items ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка чтения файла журнала операций {File}", _journalFilePath);
            return [];
        }
    }

    private async Task SaveAllInternalAsync(List<OperationLog> items, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_journalFilePath);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
