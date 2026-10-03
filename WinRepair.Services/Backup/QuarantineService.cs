using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;
using WinRepair.Core.Safety;

namespace WinRepair.Services.Backup;

/// <summary>
/// Реализация безопасного карантина в %ProgramData%\WinRepair\Quarantine\<дата>\.
/// Сохраняет манифест с исходными путями и контрольными суммами SHA-256 для точного отката.
/// </summary>
public sealed class QuarantineService : IQuarantineService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly PathValidator _pathValidator;
    private readonly ILogger<QuarantineService> _logger;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    public string QuarantineRootDirectory { get; }

    public QuarantineService(
        PathValidator pathValidator,
        ILogger<QuarantineService> logger,
        string? customQuarantineRoot = null)
    {
        _pathValidator = pathValidator;
        _logger = logger;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\ProgramData";
        }

        QuarantineRootDirectory = customQuarantineRoot ?? Path.Combine(programData, "WinRepair", "Quarantine");
        Directory.CreateDirectory(QuarantineRootDirectory);
    }

    public async Task<(bool Succeeded, QuarantineItemEntry? Entry, string ErrorMessageRu)> MoveFileToQuarantineAsync(
        string sourceFilePath,
        string batchId,
        string moduleId,
        string moduleNameRu,
        int retentionDays = 14,
        CancellationToken cancellationToken = default)
    {
        // Обязательная проверка по белому списку и симлинкам перед перемещением
        var validation = _pathValidator.ValidateForDeletion(sourceFilePath);
        if (!validation.IsAllowed)
        {
            _logger.LogWarning("QuarantineService отклонил путь {Path}: {Reason}", sourceFilePath, validation.ReasonRu);
            return (false, null, validation.ReasonRu);
        }

        if (!File.Exists(sourceFilePath))
        {
            return (false, null, $"Файл не существует или уже удалён: {sourceFilePath}");
        }

        try
        {
            var dateFolder = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var batchDirectory = Path.Combine(QuarantineRootDirectory, dateFolder, batchId);
            Directory.CreateDirectory(batchDirectory);

            var fileInfo = new FileInfo(sourceFilePath);
            var sizeBytes = fileInfo.Length;
            var itemId = Guid.NewGuid().ToString("N")[..12];
            var quarantinedFileName = $"{itemId}_{ SanitizeFileName(fileInfo.Name) }.qfile";
            var destinationPath = Path.Combine(batchDirectory, quarantinedFileName);

            var sha256 = await ComputeSha256Async(sourceFilePath, cancellationToken).ConfigureAwait(false);

            // Снимаем атрибут ReadOnly при необходимости и перемещаем в карантин
            if ((fileInfo.Attributes & FileAttributes.ReadOnly) != 0)
            {
                fileInfo.Attributes &= ~FileAttributes.ReadOnly;
            }

            File.Move(sourceFilePath, destinationPath, overwrite: true);

            var now = DateTimeOffset.UtcNow;
            var entry = new QuarantineItemEntry(
                ItemId: itemId,
                OriginalFullPath: validation.NormalizedPath,
                QuarantinedFileName: quarantinedFileName,
                BatchId: batchId,
                ModuleId: moduleId,
                SizeBytes: sizeBytes,
                QuarantinedAtUtc: now,
                ExpiresAtUtc: now.AddDays(Math.Clamp(retentionDays, 1, 365)),
                Sha256Hash: sha256);

            await AppendEntryToBatchManifestAsync(
                batchDirectory,
                batchId,
                dateFolder,
                moduleId,
                moduleNameRu,
                retentionDays,
                entry,
                cancellationToken).ConfigureAwait(false);

            return (true, entry, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось переместить файл {Path} в карантин", sourceFilePath);
            return (false, null, $"Не удалось переместить файл в карантин (возможно, занят другим процессом): {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<QuarantineBatchManifest>> GetQuarantineBatchesAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<QuarantineBatchManifest>();
        if (!Directory.Exists(QuarantineRootDirectory))
        {
            return results;
        }

        foreach (var manifestFile in Directory.EnumerateFiles(QuarantineRootDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(manifestFile);
                var manifest = await JsonSerializer.DeserializeAsync<QuarantineBatchManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (manifest is not null && manifest.Items.Count > 0)
                {
                    results.Add(manifest);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка чтения манифеста карантина {File}", manifestFile);
            }
        }

        return results.OrderByDescending(b => b.CreatedAtUtc).ToList();
    }

    public async Task<(bool Succeeded, int RestoredCount, string MessageRu)> RestoreBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batches = await GetQuarantineBatchesAsync(cancellationToken).ConfigureAwait(false);
        var batch = batches.FirstOrDefault(b => string.Equals(b.BatchId, batchId, StringComparison.OrdinalIgnoreCase));
        if (batch is null)
        {
            return (false, 0, $"Пакет карантина «{batchId}» не найден.");
        }

        var batchDirectory = Path.Combine(QuarantineRootDirectory, batch.DateFolder, batch.BatchId);
        var restored = 0;
        var remaining = new List<QuarantineItemEntry>();

        foreach (var item in batch.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quarantinedPath = Path.Combine(batchDirectory, item.QuarantinedFileName);
            if (!File.Exists(quarantinedPath))
            {
                continue;
            }

            try
            {
                var targetDir = Path.GetDirectoryName(item.OriginalFullPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Move(quarantinedPath, item.OriginalFullPath, overwrite: true);
                restored++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось восстановить файл из карантина в {Target}", item.OriginalFullPath);
                remaining.Add(item);
            }
        }

        await SaveUpdatedManifestAsync(batchDirectory, batch with { Items = remaining }, cancellationToken).ConfigureAwait(false);

        return (restored > 0, restored,
            $"Восстановлено файлов из карантина: {restored} из {batch.Items.Count}.");
    }

    public async Task<(bool Succeeded, string MessageRu)> RestoreSingleItemAsync(
        string batchId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var batches = await GetQuarantineBatchesAsync(cancellationToken).ConfigureAwait(false);
        var batch = batches.FirstOrDefault(b => string.Equals(b.BatchId, batchId, StringComparison.OrdinalIgnoreCase));
        var item = batch?.Items.FirstOrDefault(i => string.Equals(i.ItemId, itemId, StringComparison.OrdinalIgnoreCase));

        if (batch is null || item is null)
        {
            return (false, "Запрошенный файл не найден в карантине.");
        }

        var batchDirectory = Path.Combine(QuarantineRootDirectory, batch.DateFolder, batch.BatchId);
        var quarantinedPath = Path.Combine(batchDirectory, item.QuarantinedFileName);
        if (!File.Exists(quarantinedPath))
        {
            return (false, $"Физический файл в карантине отсутствует: {quarantinedPath}");
        }

        try
        {
            var targetDir = Path.GetDirectoryName(item.OriginalFullPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Move(quarantinedPath, item.OriginalFullPath, overwrite: true);

            var remaining = batch.Items.Where(i => i.ItemId != item.ItemId).ToList();
            await SaveUpdatedManifestAsync(batchDirectory, batch with { Items = remaining }, cancellationToken).ConfigureAwait(false);

            return (true, $"Файл успешно восстановлен по исходному пути: {item.OriginalFullPath}");
        }
        catch (Exception ex)
        {
            return (false, $"Не удалось восстановить файл: {ex.Message}");
        }
    }

    public async Task<int> PurgeExpiredBatchesAsync(CancellationToken cancellationToken = default)
    {
        var batches = await GetQuarantineBatchesAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var purgedFiles = 0;

        foreach (var batch in batches)
        {
            var batchDirectory = Path.Combine(QuarantineRootDirectory, batch.DateFolder, batch.BatchId);
            var expiredItems = batch.Items.Where(i => i.ExpiresAtUtc <= now).ToList();
            if (expiredItems.Count == 0)
            {
                continue;
            }

            foreach (var expired in expiredItems)
            {
                var qPath = Path.Combine(batchDirectory, expired.QuarantinedFileName);
                try
                {
                    if (File.Exists(qPath))
                    {
                        File.Delete(qPath);
                        purgedFiles++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось удалить просроченный файл карантина {File}", qPath);
                }
            }

            var remaining = batch.Items.Except(expiredItems).ToList();
            await SaveUpdatedManifestAsync(batchDirectory, batch with { Items = remaining }, cancellationToken).ConfigureAwait(false);
        }

        return purgedFiles;
    }

    private async Task AppendEntryToBatchManifestAsync(
        string batchDirectory,
        string batchId,
        string dateFolder,
        string moduleId,
        string moduleNameRu,
        int retentionDays,
        QuarantineItemEntry newEntry,
        CancellationToken cancellationToken)
    {
        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifestPath = Path.Combine(batchDirectory, "manifest.json");
            QuarantineBatchManifest manifest;

            if (File.Exists(manifestPath))
            {
                await using var readStream = File.OpenRead(manifestPath);
                var existing = await JsonSerializer.DeserializeAsync<QuarantineBatchManifest>(readStream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    var items = existing.Items.ToList();
                    items.Add(newEntry);
                    manifest = existing with { Items = items };
                }
                else
                {
                    manifest = new QuarantineBatchManifest(batchId, dateFolder, DateTimeOffset.UtcNow, moduleId, moduleNameRu, retentionDays, [newEntry]);
                }
            }
            else
            {
                manifest = new QuarantineBatchManifest(batchId, dateFolder, DateTimeOffset.UtcNow, moduleId, moduleNameRu, retentionDays, [newEntry]);
            }

            await using var writeStream = File.Create(manifestPath);
            await JsonSerializer.SerializeAsync(writeStream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private async Task SaveUpdatedManifestAsync(
        string batchDirectory,
        QuarantineBatchManifest updated,
        CancellationToken cancellationToken)
    {
        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifestPath = Path.Combine(batchDirectory, "manifest.json");
            if (updated.Items.Count == 0)
            {
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
                if (Directory.Exists(batchDirectory) && !Directory.EnumerateFileSystemEntries(batchDirectory).Any())
                {
                    Directory.Delete(batchDirectory);
                }
                return;
            }

            await using var writeStream = File.Create(manifestPath);
            await JsonSerializer.SerializeAsync(writeStream, updated, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "UNAVAILABLE";
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
