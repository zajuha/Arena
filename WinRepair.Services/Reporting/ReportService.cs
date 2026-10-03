using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using WinRepair.Core.Abstractions;
using WinRepair.Core.Diagnostics;
using WinRepair.Core.Models;

namespace WinRepair.Services.Reporting;

/// <summary>
/// Генератор отчётов диагностики и журнала операций в форматах HTML (для чтения в браузере)
/// и JSON (для машинной обработки) — п. 3.6 и п. 8 ТЗ.
/// </summary>
public sealed class ReportService : IReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)
    };

    private readonly ILogger<ReportService> _logger;

    public ReportService(ILogger<ReportService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExportHtmlReportAsync(
        ScanResult? scanResult,
        IReadOnlyList<OperationLog> recentOperations,
        string outputFilePath,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var html = BuildHtmlContent(scanResult, recentOperations);
        await File.WriteAllTextAsync(outputFilePath, html, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("HTML-отчёт успешно экспортирован в {File}", outputFilePath);
        return outputFilePath;
    }

    public async Task<string> ExportJsonReportAsync(
        ScanResult? scanResult,
        IReadOnlyList<OperationLog> recentOperations,
        string outputFilePath,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Application = "Ремонт и очистка Windows (WinRepair) v1.0.0",
            DiagnosticScan = scanResult,
            OperationsHistory = recentOperations
        };

        await using var stream = File.Create(outputFilePath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("JSON-отчёт успешно экспортирован в {File}", outputFilePath);
        return outputFilePath;
    }

    public static string BuildHtmlContent(ScanResult? scan, IReadOnlyList<OperationLog> operations)
    {
        var sb = new StringBuilder(8192);
        var nowRu = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ru\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine("  <title>Отчёт состояния системы — Ремонт и очистка Windows</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --bg: #0f172a; --card: #1e293b; --text: #f8fafc; --muted: #94a3b8; --accent: #38bdf8; --ok: #22c55e; --warn: #f59e0b; --err: #ef4444; }");
        sb.AppendLine("    body { font-family: 'Segoe UI Variable Text', 'Segoe UI', sans-serif; background: var(--bg); color: var(--text); margin: 0; padding: 32px; line-height: 1.5; }");
        sb.AppendLine("    .container { max-width: 1120px; margin: 0 auto; }");
        sb.AppendLine("    .header { background: var(--card); border-radius: 12px; padding: 24px 28px; margin-bottom: 24px; border: 1px solid #334155; display: flex; justify-content: space-between; align-items: center; }");
        sb.AppendLine("    .score-badge { font-size: 36px; font-weight: 700; color: var(--ok); background: rgba(34,197,94,0.12); padding: 12px 24px; border-radius: 12px; }");
        sb.AppendLine("    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 16px; margin-bottom: 28px; }");
        sb.AppendLine("    .card { background: var(--card); border-radius: 10px; padding: 18px; border: 1px solid #334155; }");
        sb.AppendLine("    .card h3 { margin: 0 0 8px 0; font-size: 16px; color: var(--accent); }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; background: var(--card); border-radius: 10px; overflow: hidden; margin-bottom: 28px; }");
        sb.AppendLine("    th, td { text-align: left; padding: 12px 16px; border-bottom: 1px solid #334155; font-size: 14px; }");
        sb.AppendLine("    th { background: #172033; color: var(--muted); font-weight: 600; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"container\">");
        sb.AppendLine("    <div class=\"header\">");
        sb.AppendLine("      <div>");
        sb.AppendLine("        <h1 style=\"margin:0 0 6px 0; font-size:24px;\">Отчёт утилиты «Ремонт и очистка Windows»</h1>");
        sb.AppendLine($"        <div style=\"color:var(--muted);\">Дата формирования: {Escape(nowRu)} · Компьютер: {Escape(scan?.MachineName ?? Environment.MachineName)} · ОС: {Escape(scan?.OsVersionRu ?? Environment.OSVersion.VersionString)}</div>");
        sb.AppendLine("      </div>");
        if (scan is not null)
        {
            sb.AppendLine($"      <div class=\"score-badge\">{scan.OverallScore} / 100</div>");
        }
        sb.AppendLine("    </div>");

        if (scan is not null)
        {
            sb.AppendLine("    <h2>Оценка состояния по категориям</h2>");
            sb.AppendLine("    <div class=\"grid\">");
            foreach (var cat in scan.CategoryScores)
            {
                sb.AppendLine("      <div class=\"card\">");
                sb.AppendLine($"        <h3>{Escape(cat.TitleRu)} — {cat.Score}/100</h3>");
                sb.AppendLine($"        <div style=\"font-size:13px; color:var(--muted);\">{Escape(cat.SummaryRu)}</div>");
                sb.AppendLine("      </div>");
            }
            sb.AppendLine("    </div>");

            sb.AppendLine("    <h2>Обнаруженные замечания и рекомендации</h2>");
            sb.AppendLine("    <table>");
            sb.AppendLine("      <thead><tr><th>Категория</th><th>Проблема</th><th>Рекомендация</th><th>Риск исправления</th></tr></thead>");
            sb.AppendLine("      <tbody>");
            if (scan.Issues.Count == 0)
            {
                sb.AppendLine("        <tr><td colspan=\"4\">Отклонений в состоянии операционной системы не обнаружено.</td></tr>");
            }
            else
            {
                foreach (var issue in scan.Issues)
                {
                    sb.AppendLine($"        <tr><td>{Escape(issue.Category.ToRussianTitle())}</td><td><strong>{Escape(issue.TitleRu)}</strong><br/><span style=\"color:var(--muted);\">{Escape(issue.DescriptionRu)}</span></td><td>{Escape(issue.RecommendationRu)}</td><td>{Escape(issue.FixRiskLevel.ToRussianDisplayName())}</td></tr>");
                }
            }
            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
        }

        sb.AppendLine("    <h2>Журнал выполненных операций</h2>");
        sb.AppendLine("    <table>");
        sb.AppendLine("      <thead><tr><th>Дата и время</th><th>Операция</th><th>Результат</th><th>Освобождено</th><th>Подробности</th></tr></thead>");
        sb.AppendLine("      <tbody>");
        if (operations.Count == 0)
        {
            sb.AppendLine("        <tr><td colspan=\"5\">В текущей сессии изменяющие операции ещё не выполнялись.</td></tr>");
        }
        else
        {
            foreach (var op in operations)
            {
                sb.AppendLine($"        <tr><td>{op.TimestampUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}</td><td>{Escape(op.TitleRu)}</td><td>{Escape(op.OutcomeRu)}</td><td>{Escape(FileSizeFormatter.FormatRussian(op.FreedBytes))}</td><td>{Escape(op.DescriptionRu)}</td></tr>");
            }
        }
        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static string Escape(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
