using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Models;

namespace WinRepair.Services.Scanning;

/// <summary>
/// Сканер больших и дублирующихся файлов (п. 3.2 ТЗ).
/// Строго ТОЛЬКО ЧТЕНИЕ: никогда не удаляет файлы автоматически, возвращает список для ручного анализа пользователем.
/// </summary>
public sealed class LargeAndDuplicateFileScanner : ILargeAndDuplicateFileScanner
{
    private readonly ILogger<LargeAndDuplicateFileScanner> _logger;

    public LargeAndDuplicateFileScanner(ILogger<LargeAndDuplicateFileScanner> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<LargeFileEntry>> FindLargeFilesAsync(
        string rootDirectory,
        long minSizeBytes = 100L * 1024 * 1024,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<LargeFileEntry>>(() =>
        {
            var results = new List<LargeFileEntry>();
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return results;
            }

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };

            var scannedCount = 0;
            foreach (var filePath in Directory.EnumerateFiles(rootDirectory, "*", enumOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedCount++;

                if (scannedCount % 250 == 0)
                {
                    progress?.Report(new ProgressReport(
                        "Поиск крупных файлов",
                        $"Проверено файлов: {scannedCount}, найдено крупных: {results.Count}",
                        Math.Min(95, scannedCount / 100.0)));
                }

                try
                {
                    var fi = new FileInfo(filePath);
                    if (fi.Length >= minSizeBytes)
                    {
                        results.Add(new LargeFileEntry(
                            FullPath: fi.FullName,
                            FileName: fi.Name,
                            DirectoryPath: fi.DirectoryName ?? string.Empty,
                            SizeBytes: fi.Length,
                            LastModifiedUtc: new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                            Extension: fi.Extension.ToLowerInvariant()));
                    }
                }
                catch
                {
                    // Пропускаем недоступные или заблокированные файлы
                }
            }

            progress?.Report(new ProgressReport("Поиск крупных файлов", $"Завершено. Найдено файлов: {results.Count}", 100));
            return results.OrderByDescending(f => f.SizeBytes).Take(500).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<DuplicateFileGroup>> FindDuplicateFilesAsync(
        string rootDirectory,
        long minSizeBytes = 1024 * 1024,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var allCandidates = await FindLargeFilesAsync(rootDirectory, minSizeBytes, progress, cancellationToken).ConfigureAwait(false);

        // Шаг 1: группируем по точному размеру файла (быстрое отсечение уникальных размеров)
        var sizeGroups = allCandidates
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        var duplicateGroups = new List<DuplicateFileGroup>();
        var groupIndex = 0;

        foreach (var sizeGroup in sizeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groupIndex++;

            progress?.Report(new ProgressReport(
                "Поиск дубликатов файлов (SHA-256)",
                $"Проверка группы размеров {groupIndex} из {sizeGroups.Count}",
                sizeGroups.Count > 0 ? (double)groupIndex / sizeGroups.Count * 100.0 : 100.0));

            var byHash = new Dictionary<string, List<LargeFileEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = await ComputeFileSha256SafeAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(hash))
                {
                    continue;
                }

                if (!byHash.TryGetValue(hash, out var list))
                {
                    list = [];
                    byHash[hash] = list;
                }

                list.Add(file);
            }

            foreach (var (hash, matchingFiles) in byHash)
            {
                if (matchingFiles.Count > 1)
                {
                    duplicateGroups.Add(new DuplicateFileGroup(
                        Sha256Hash: hash,
                        FileSizeBytes: sizeGroup.Key,
                        Files: matchingFiles));
                }
            }
        }

        _logger.LogInformation("Поиск дубликатов в {Root} завершён: найдено групп {Count}", rootDirectory, duplicateGroups.Count);
        return duplicateGroups.OrderByDescending(g => g.ReclaimableBytes).ToList();
    }

    private static async Task<string?> ComputeFileSha256SafeAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }
}
