using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Export service for packaging sessions and producing portable outputs
/// </summary>
public class ExportService
{
    private readonly SessionManager _sessionManager;

    /// <summary>Maximum blockquote nesting depth allowed before flattening.</summary>
    private const int MaxBlockquoteDepth = 4;
    /// <summary>Maximum list nesting depth allowed before flattening.</summary>
    private const int MaxListIndentSpaces = 12; // 3 levels of 4-space indent

    public ExportService(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    // ────────────────────────────────────────────────────────
    //  Safe markdown → HTML with depth-flattening + fallback
    // ────────────────────────────────────────────────────────

    /// <summary>Maximum pipe-table data rows before splitting into sub-tables.</summary>
    private const int MaxPipeTableRows = 20;

    /// <summary>
    /// Strip a leading H1 heading (# Title) from markdown content to prevent
    /// duplicate H1 when the HTML wrapper already provides its own &lt;h1&gt;.
    /// Only strips when the H1 text matches <paramref name="wrapperTitle"/> (case-insensitive)
    /// or when no title is supplied (unconditional strip).
    /// </summary>
    internal static string StripLeadingH1(string markdown, string? wrapperTitle = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        // Skip leading blank lines, then check for a single-# H1
        var trimmed = markdown.TrimStart('\n', '\r');
        if (trimmed.StartsWith("# ") && !trimmed.StartsWith("## "))
        {
            var newlineIdx = trimmed.IndexOf('\n');
            var h1Text = newlineIdx >= 0 ? trimmed[2..newlineIdx].Trim() : trimmed[2..].Trim();

            // Only strip if no title specified (always) or the H1 matches the wrapper title
            if (wrapperTitle == null || h1Text.Equals(wrapperTitle, StringComparison.OrdinalIgnoreCase))
            {
                if (newlineIdx >= 0)
                    return trimmed[(newlineIdx + 1)..].TrimStart('\n', '\r');
                return string.Empty;
            }
        }
        return markdown;
    }

    /// <summary>
    /// Convert markdown to HTML safely.  Pre-processes the source to flatten
    /// excessive nesting that can trip Markdig's internal depth limit, and
    /// falls back to section-by-section rendering (then HTML-escaped &lt;pre&gt;)
    /// if conversion still fails.
    /// </summary>
    internal static string SafeMarkdownToHtml(string markdown, MarkdownPipeline pipeline)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var flattened = FlattenMarkdownNesting(markdown);

        try
        {
            return Markdig.Markdown.ToHtml(flattened, pipeline);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExportService] Markdig failed on full document ({ex.GetType().Name}), retrying section-by-section");

            // ── Retry: split at heading boundaries, render each section independently ──
            // This keeps per-section inline depth below Markdig's limit while
            // preserving all content and formatting.
            try
            {
                return RenderBySection(flattened, pipeline);
            }
            catch (Exception ex2)
            {
                System.Diagnostics.Debug.WriteLine($"[ExportService] Section-by-section also failed ({ex2.GetType().Name}), falling back to <pre>");
                // Last resort: render as preformatted, HTML-escaped text
                return $"<pre>{EscapeHtml(markdown)}</pre>";
            }
        }
    }

    /// <summary>
    /// Split markdown at heading boundaries (## ) and render each section
    /// independently through Markdig, then concatenate results.
    /// This prevents large tables or deeply nested constructs in one section
    /// from exceeding the renderer's inline-depth limit.
    /// </summary>
    private static string RenderBySection(string markdown, MarkdownPipeline pipeline)
    {
        // Split at lines that start with # (any heading level)
        var sections = Regex.Split(markdown, @"(?=^#{1,6}\s)", RegexOptions.Multiline);

        var sb = new StringBuilder(markdown.Length);
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section))
                continue;

            try
            {
                sb.Append(Markdig.Markdown.ToHtml(section, pipeline));
            }
            catch
            {
                // If even a single section is too large, try splitting its
                // pipe tables into smaller chunks and retry once more.
                try
                {
                    var split = SplitLargePipeTables(section);
                    sb.Append(Markdig.Markdown.ToHtml(split, pipeline));
                }
                catch
                {
                    // Section-level fallback: render as escaped text
                    sb.Append($"<pre>{EscapeHtml(section)}</pre>");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Split any pipe table in the markdown that has more than
    /// <see cref="MaxPipeTableRows"/> data rows into multiple smaller tables
    /// by repeating the header row + separator every N rows.
    /// </summary>
    internal static string SplitLargePipeTables(string markdown)
    {
        var lines = markdown.Split('\n');
        var result = new StringBuilder(markdown.Length);

        string? headerRow = null;
        string? separatorRow = null;
        int dataRowCount = 0;
        bool inTable = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (!inTable)
            {
                // Detect start of a pipe table: a row starting with |, followed
                // by a separator row (|---|...|)
                if (trimmed.StartsWith('|') && i + 1 < lines.Length)
                {
                    var nextTrimmed = lines[i + 1].TrimEnd('\r').TrimStart();
                    if (Regex.IsMatch(nextTrimmed, @"^\|[\s:|-]+\|\s*$"))
                    {
                        inTable = true;
                        headerRow = line;
                        separatorRow = lines[i + 1].TrimEnd('\r');
                        dataRowCount = 0;
                        result.AppendLine(line);
                        result.AppendLine(separatorRow);
                        i++; // skip separator
                        continue;
                    }
                }
                result.AppendLine(line);
            }
            else
            {
                // Inside a table — check if this is still a data row
                if (IsPipeTableRow(line) || Regex.IsMatch(trimmed, @"^\|[\s:|-]+\|\s*$"))
                {
                    dataRowCount++;
                    if (dataRowCount > MaxPipeTableRows && dataRowCount % MaxPipeTableRows == 1)
                    {
                        // Break the table: blank line, then repeat header + separator
                        result.AppendLine();
                        result.AppendLine(headerRow);
                        result.AppendLine(separatorRow);
                    }
                    result.AppendLine(line);
                }
                else
                {
                    // Non-table line — end the table
                    inTable = false;
                    headerRow = null;
                    separatorRow = null;
                    result.AppendLine(line);
                }
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Reduce nesting depth of blockquotes and indented list items so that
    /// Markdig's parser doesn't exceed its internal depth limit.
    /// </summary>
    internal static string FlattenMarkdownNesting(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        // ── Phase 1: Convert setext-style headers to ATX and remove orphaned separator lines ──
        markdown = ConvertSetextToAtx(markdown);

        var sb = new StringBuilder(markdown.Length);
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine;

            // ── Flatten deep blockquotes ( >>>>> → >>>> ) ──
            int quoteDepth = 0;
            int idx = 0;
            while (idx < line.Length && (line[idx] == '>' || line[idx] == ' '))
            {
                if (line[idx] == '>') quoteDepth++;
                idx++;
            }
            if (quoteDepth > MaxBlockquoteDepth)
            {
                // Rebuild with capped depth
                var remainder = line[idx..];
                line = new string('>', MaxBlockquoteDepth) + " " + remainder.TrimStart();
            }

            // ── Flatten deep list indentation ──
            // Detect leading spaces/tabs before a list marker (-, *, +, or digit.)
            var listMatch = Regex.Match(line, @"^(\s+)([-*+]|\d+[.)]) ");
            if (listMatch.Success)
            {
                int indent = 0;
                foreach (char c in listMatch.Groups[1].Value)
                    indent += c == '\t' ? 4 : 1;

                if (indent > MaxListIndentSpaces)
                {
                    var marker = listMatch.Groups[2].Value;
                    var rest = line[listMatch.Length..];
                    line = new string(' ', MaxListIndentSpaces) + marker + " " + rest;
                }
            }

            // ── Escape orphaned brackets inside pipe-table cells ──
            // Bracket notation like [2.8.0] or [5.0.0,) inside table cells
            // can be mis-parsed as link references / footnotes by Markdig's
            // UseAdvancedExtensions() pipeline, causing exceptions.
            if (IsPipeTableRow(line))
                line = EscapeTableCellBrackets(line);

            sb.Append(line);
            sb.Append('\n');
        }

        // Remove trailing extra newline we added
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
            sb.Length--;

        return sb.ToString();
    }

    /// <summary>
    /// Returns true if a line looks like a pipe-table data row (not a separator).
    /// </summary>
    private static bool IsPipeTableRow(string line)
    {
        var t = line.TrimStart();
        // Must start with | and contain at least two |
        if (t.Length < 3 || t[0] != '|') return false;
        // Separator rows (|---|---| etc.) are not data rows
        if (Regex.IsMatch(t, @"^\|[\s:|-]+\|\s*$")) return false;
        return t.IndexOf('|', 1) > 0;
    }

    /// <summary>
    /// In pipe-table data cells, escape bare [ that could be mis-parsed as link references.
    /// Only escapes brackets that are NOT already inside backtick code spans.
    /// </summary>
    internal static string EscapeTableCellBrackets(string line)
    {
        // Split by | preserving the leading/trailing pipes
        var parts = line.Split('|');
        for (int i = 0; i < parts.Length; i++)
        {
            var cell = parts[i];
            // Skip cells that are already code-wrapped (contain backticks)
            if (cell.Contains('`')) continue;
            // Escape [ that starts a bracket group like [1.2.3] or [5.0.0,)
            // but only if not already backslash-escaped
            parts[i] = Regex.Replace(cell, @"(?<!\\)\[", "\\[");
        }
        return string.Join('|', parts);
    }

    /// <summary>
    /// Convert setext-style headers (underlines of === or ---) to ATX-style (# / ##).
    /// Also removes orphaned separator lines that have no preceding header text.
    /// </summary>
    internal static string ConvertSetextToAtx(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        var lines = markdown.Split('\n');
        var result = new List<string>(lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();

            // Check if this line is a setext underline (=== or ---).
            // Note: a bare "---" is also a thematic break; we only treat it as
            // a setext H2 underline when there's a preceding header-text line.
            bool isH1Underline = trimmed.Length >= 3 && trimmed.All(c => c == '=');
            bool isH2Underline = trimmed.Length >= 3 && trimmed.All(c => c == '-');

            if (isH1Underline || isH2Underline)
            {
                // Look back for preceding non-empty text line (allow at most 1 blank line gap)
                string? headerText = null;
                int headerIdx = -1;

                for (int back = result.Count - 1; back >= Math.Max(0, result.Count - 2); back--)
                {
                    var candidate = result[back].Trim();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        // Must not itself look like a list item, blockquote, or header
                        if (!candidate.StartsWith('#') && !candidate.StartsWith('>') &&
                            !candidate.StartsWith('-') && !candidate.StartsWith('*') &&
                            !Regex.IsMatch(candidate, @"^\d+[.)]"))
                        {
                            headerText = candidate;
                            headerIdx = back;
                        }
                        break;
                    }
                }

                if (headerText != null && headerIdx >= 0)
                {
                    // Replace the text line with ATX header and skip the underline
                    var prefix = isH1Underline ? "# " : "## ";
                    result[headerIdx] = prefix + headerText;
                    // Don't add the underline line
                }
                // else: orphaned separator — drop it entirely
                continue;
            }

            result.Add(lines[i].TrimEnd('\r'));
        }

        return string.Join('\n', result);
    }

    public string ExportSessionToZip(string sessionId, string outputPath)
    {
        var session = _sessionManager.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var zipPath = Path.Combine(outputPath, $"ResearchHive_{session.Title.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd}.zip");

        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(session.WorkspacePath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);

        return zipPath;
    }

    public async Task ExportReportAsync(string sessionId, string jobId, string outputPath, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var reports = db.GetReports(jobId);

        foreach (var report in reports)
        {
            var fileName = $"{report.ReportType}_{report.Id}.{(report.Format == "markdown" ? "md" : "json")}";
            var filePath = Path.Combine(outputPath, fileName);
            await File.WriteAllTextAsync(filePath, report.Content, ct);
        }
    }

    /// <summary>
    /// Export a single report as a standalone HTML file with embedded CSS.
    /// </summary>
    public async Task<string> ExportReportAsHtmlAsync(string sessionId, string reportId, string outputPath, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var reports = db.GetReports();
        var report = reports.FirstOrDefault(r => r.Id == reportId)
            ?? throw new InvalidOperationException($"Report {reportId} not found");

        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var htmlBody = SafeMarkdownToHtml(StripLeadingH1(report.Content, report.Title), pipeline);

        var fullHtml = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>{{EscapeHtml(report.Title)}}</title>
<style>
  body { font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; max-width: 800px; margin: 0 auto; padding: 2rem; background: #fafafa; color: #212121; line-height: 1.6; }
  h1 { color: #1976D2; border-bottom: 2px solid #BBDEFB; padding-bottom: 0.4rem; }
  h2 { color: #0D47A1; margin-top: 2rem; }
  h3 { color: #1565C0; }
  code { background: #f5f5f5; padding: 2px 6px; border-radius: 3px; font-family: Consolas, monospace; font-size: 0.9em; }
  pre { background: #263238; color: #ECEFF1; padding: 1rem; border-radius: 6px; overflow-x: auto; }
  pre code { background: transparent; color: inherit; }
  blockquote { border-left: 4px solid #1976D2; margin: 1rem 0; padding: 0.5rem 1rem; background: #E3F2FD; }
  table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
  th, td { border: 1px solid #E0E0E0; padding: 8px 12px; text-align: left; }
  th { background: #1976D2; color: white; }
  a { color: #1976D2; text-decoration: none; }
  a:hover { text-decoration: underline; }
  .meta { color: #757575; font-size: 0.85rem; margin-bottom: 2rem; }
</style>
</head>
<body>
<h1>{{EscapeHtml(report.Title)}}</h1>
<div class="meta">
  <strong>Type:</strong> {{EscapeHtml(report.ReportType)}} &middot; 
  <strong>Generated:</strong> {{report.CreatedUtc.ToString("yyyy-MM-dd HH:mm")}} UTC &middot;
  <strong>Exported by:</strong> ResearchHive
</div>
{{htmlBody}}
<hr/>
<p style="color:#9e9e9e;font-size:0.8rem;text-align:center;">
  Generated by ResearchHive &mdash; Agentic Research Studio
</p>
</body>
</html>
""";

        var session = _sessionManager.GetSession(sessionId)!;
        var fileName = $"{SanitizeFileName(report.Title)}_{DateTime.Now:yyyyMMdd}.html";
        var filePath = Path.Combine(outputPath, fileName);
        await File.WriteAllTextAsync(filePath, fullHtml, Encoding.UTF8, ct);
        return filePath;
    }

    /// <summary>
    /// Export a Research Packet — a self-contained folder with index.html, evidence, sources, and reports.
    /// Returns the folder path (not the zip) so the user can browse it directly.
    /// Also creates a .zip alongside it for sharing.
    /// </summary>
    public async Task<string> ExportResearchPacketAsync(string sessionId, string outputPath, CancellationToken ct = default)
    {
        var session = _sessionManager.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");
        var db = _sessionManager.GetSessionDb(sessionId);

        var packetDir = Path.Combine(outputPath, $"ResearchPacket_{SanitizeFileName(session.Title)}_{DateTime.Now:yyyyMMdd}");
        if (Directory.Exists(packetDir))
            packetDir += $"_{DateTime.Now:HHmmss}";
        Directory.CreateDirectory(packetDir);
        Directory.CreateDirectory(Path.Combine(packetDir, "reports"));
        Directory.CreateDirectory(Path.Combine(packetDir, "evidence"));

        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        // ── Export all reports as HTML ──
        var reports = db.GetReports();
        var reportLinks = new StringBuilder();
        foreach (var report in reports)
        {
            var htmlBody = SafeMarkdownToHtml(StripLeadingH1(report.Content, report.Title), pipeline);
            var reportHtml = WrapHtml(report.Title, htmlBody, report.ReportType, report.CreatedUtc);
            var reportFileName = $"{SanitizeFileName(report.Title)}_{ShortId(report.Id)}.html";
            await File.WriteAllTextAsync(Path.Combine(packetDir, "reports", reportFileName), reportHtml, Encoding.UTF8, ct);
            var encodedName = Uri.EscapeDataString(reportFileName);
            reportLinks.AppendLine($"<li><a href=\"reports/{encodedName}\">{EscapeHtml(report.Title)}</a> <span class=\"meta\">({EscapeHtml(report.ReportType)}, {report.CreatedUtc:yyyy-MM-dd})</span></li>");
        }

        // ── Export evidence files from snapshots ──
        var snapshots = db.GetSnapshots();
        var evidenceLinks = new StringBuilder();
        int evidenceCount = 0;
        foreach (var snap in snapshots)
        {
            if (snap.IsBlocked || string.IsNullOrEmpty(snap.BundlePath)) continue;

            var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(snap.Title) ? snap.Id : snap.Title);

            // Copy text extraction if available
            if (!string.IsNullOrEmpty(snap.TextPath) && File.Exists(snap.TextPath))
            {
                var destName = $"{safeName}_{ShortId(snap.Id)}.txt";
                File.Copy(snap.TextPath, Path.Combine(packetDir, "evidence", destName), overwrite: true);
                evidenceCount++;
                var encodedDest = Uri.EscapeDataString(destName);
                evidenceLinks.AppendLine($"<li><a href=\"evidence/{encodedDest}\">{EscapeHtml(snap.Title)}</a> <span class=\"meta\">({snap.HttpStatus}, {snap.CapturedUtc:yyyy-MM-dd})</span></li>");
            }

            // Copy HTML snapshot if available
            if (!string.IsNullOrEmpty(snap.HtmlPath) && File.Exists(snap.HtmlPath))
            {
                var htmlDestName = $"{safeName}_{ShortId(snap.Id)}.html";
                File.Copy(snap.HtmlPath, Path.Combine(packetDir, "evidence", htmlDestName), overwrite: true);
            }

            // Copy extraction JSON if available
            if (!string.IsNullOrEmpty(snap.ExtractionPath) && File.Exists(snap.ExtractionPath))
            {
                var extDestName = $"{safeName}_{ShortId(snap.Id)}_extraction.json";
                File.Copy(snap.ExtractionPath, Path.Combine(packetDir, "evidence", extDestName), overwrite: true);
            }
        }

        // ── Export sources.csv ──
        var csv = new StringBuilder();
        csv.AppendLine("URL,Title,CapturedUtc,HttpStatus,IsBlocked,BlockReason");
        foreach (var snap in snapshots)
        {
            csv.AppendLine($"\"{EscapeCsv(snap.Url)}\",\"{EscapeCsv(snap.Title)}\",\"{snap.CapturedUtc:O}\",{snap.HttpStatus},{snap.IsBlocked},\"{EscapeCsv(snap.BlockReason ?? "")}\"");
        }
        await File.WriteAllTextAsync(Path.Combine(packetDir, "sources.csv"), csv.ToString(), Encoding.UTF8, ct);

        // ── Export notebook entries ──
        var notebookEntries = db.GetNotebookEntries();
        if (notebookEntries.Any())
        {
            var notebookMd = new StringBuilder("# Research Notebook\n\n");
            foreach (var entry in notebookEntries)
            {
                notebookMd.AppendLine($"## {entry.Title}");
                notebookMd.AppendLine($"*Updated: {entry.UpdatedUtc:yyyy-MM-dd HH:mm}*\n");
                notebookMd.AppendLine(entry.Content);
                notebookMd.AppendLine("\n---\n");
            }
            var notebookHtml = WrapHtml("Research Notebook", SafeMarkdownToHtml(notebookMd.ToString(), pipeline), "notebook", DateTime.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(packetDir, "notebook.html"), notebookHtml, Encoding.UTF8, ct);
        }

        // ── Copy session logs if available ──
        var logsDir = Path.Combine(session.WorkspacePath, "Logs");
        if (Directory.Exists(logsDir))
        {
            var destLogsDir = Path.Combine(packetDir, "logs");
            Directory.CreateDirectory(destLogsDir);
            foreach (var logFile in Directory.GetFiles(logsDir))
                File.Copy(logFile, Path.Combine(destLogsDir, Path.GetFileName(logFile)), overwrite: true);
        }

        // ── Copy artifacts index if any ──
        var artifactsDir = Path.Combine(session.WorkspacePath, "Artifacts");
        if (Directory.Exists(artifactsDir) && Directory.GetFiles(artifactsDir).Length > 0)
        {
            var destArtDir = Path.Combine(packetDir, "artifacts");
            Directory.CreateDirectory(destArtDir);
            foreach (var artFile in Directory.GetFiles(artifactsDir))
                File.Copy(artFile, Path.Combine(destArtDir, Path.GetFileName(artFile)), overwrite: true);
        }

        // ── Create index.html ──
        var notebookLink = notebookEntries.Any()
            ? "<li><a href=\"notebook.html\">Research Notebook</a> &mdash; All notebook entries</li>"
            : "";
        var evidenceSection = evidenceCount > 0
            ? $"<h2>Evidence ({evidenceCount} files)</h2>\n<ul>\n{evidenceLinks}</ul>"
            : "<h2>Evidence</h2>\n<p class=\"meta\">No evidence files captured (sources may have been blocked).</p>";

        var packDisplay = session.Pack.ToDisplayName();
        var statusDisplay = session.Status.ToDisplayName();

        var indexHtml = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>{{EscapeHtml(session.Title)}} &mdash; Research Packet</title>
<style>
  * { box-sizing: border-box; }
  body { font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; max-width: 860px; margin: 0 auto; padding: 2rem; background: #fafafa; color: #212121; line-height: 1.6; }
  h1 { color: #1976D2; margin-bottom: 0.3rem; }
  h2 { color: #0D47A1; margin-top: 2rem; border-bottom: 1px solid #E0E0E0; padding-bottom: 0.3rem; }
  a { color: #1976D2; text-decoration: none; }
  a:hover { text-decoration: underline; }
  .meta { color: #757575; font-size: 0.85rem; }
  .stats { display: flex; gap: 1.5rem; margin: 1.2rem 0; flex-wrap: wrap; }
  .stat { background: white; border: 1px solid #E0E0E0; border-radius: 8px; padding: 1rem 1.5rem; text-align: center; min-width: 100px; }
  .stat-value { font-size: 1.5rem; font-weight: bold; color: #1976D2; }
  .stat-label { font-size: 0.85rem; color: #757575; }
  ul { list-style: none; padding: 0; margin: 0.5rem 0; }
  li { padding: 0.6rem 0.4rem; border-bottom: 1px solid #f0f0f0; }
  li a { font-weight: 500; }
  footer { margin-top: 2rem; padding-top: 1rem; border-top: 1px solid #E0E0E0; text-align: center; }
</style>
</head>
<body>
<h1>{{EscapeHtml(session.Title)}}</h1>
<p class="meta">Pack: {{EscapeHtml(packDisplay)}} &middot; Created: {{session.CreatedUtc.ToString("yyyy-MM-dd")}} &middot; Status: {{EscapeHtml(statusDisplay)}}</p>
<div class="stats">
  <div class="stat"><div class="stat-value">{{reports.Count}}</div><div class="stat-label">Reports</div></div>
  <div class="stat"><div class="stat-value">{{snapshots.Count}}</div><div class="stat-label">Sources</div></div>
  <div class="stat"><div class="stat-value">{{evidenceCount}}</div><div class="stat-label">Evidence</div></div>
  <div class="stat"><div class="stat-value">{{notebookEntries.Count}}</div><div class="stat-label">Notes</div></div>
</div>

<h2>Reports</h2>
<ul>
{{reportLinks}}</ul>

{{evidenceSection}}

<h2>Data Files</h2>
<ul>
  <li><a href="sources.csv">sources.csv</a> &mdash; All captured sources ({{snapshots.Count}} entries)</li>
  {{notebookLink}}
</ul>

<footer>
<p class="meta">Generated by ResearchHive &mdash; Agentic Research Studio</p>
</footer>
</body>
</html>
""";
        await File.WriteAllTextAsync(Path.Combine(packetDir, "index.html"), indexHtml, Encoding.UTF8, ct);

        // Also create a ZIP for easy sharing
        var zipPath = packetDir + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(packetDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);

        // Return the folder path so user can open it directly in a browser
        return packetDir;
    }

    private static string WrapHtml(string title, string bodyHtml, string type, DateTime created)
    {
        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<title>{{EscapeHtml(title)}}</title>
<style>
  body { font-family: 'Segoe UI', system-ui, sans-serif; max-width: 800px; margin: 0 auto; padding: 2rem; background: #fafafa; color: #212121; line-height: 1.6; }
  h1 { color: #1976D2; border-bottom: 2px solid #BBDEFB; padding-bottom: 0.4rem; }
  h2 { color: #0D47A1; }
  code { background: #f5f5f5; padding: 2px 6px; border-radius: 3px; font-family: Consolas, monospace; }
  pre { background: #263238; color: #ECEFF1; padding: 1rem; border-radius: 6px; overflow-x: auto; }
  pre code { background: transparent; color: inherit; }
  blockquote { border-left: 4px solid #1976D2; margin: 1rem 0; padding: 0.5rem 1rem; background: #E3F2FD; }
  table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
  th, td { border: 1px solid #E0E0E0; padding: 8px 12px; text-align: left; }
  th { background: #1976D2; color: white; }
  a { color: #1976D2; }
  .meta { color: #757575; font-size: 0.85rem; margin-bottom: 1rem; }
</style>
</head>
<body>
<h1>{{EscapeHtml(title)}}</h1>
<p class="meta">{{type}} &middot; {{created.ToString("yyyy-MM-dd HH:mm")}} UTC &middot; ResearchHive</p>
{{bodyHtml}}
</body>
</html>
""";
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscapeCsv(string text) =>
        text.Replace("\"", "\"\"");

    private static string SanitizeFileName(string name)
    {
        var sanitized = string.Join("_", name.Split(Path.GetInvalidFileNameChars()))
            .Replace(' ', '_');
        // Collapse multiple underscores and trim trailing ones
        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");
        sanitized = sanitized.Trim('_');
        if (sanitized.Length > 60)
            sanitized = sanitized[..60].TrimEnd('_');
        return sanitized;
    }

    public string PackageApplication(string outputPath)
    {
        var packageDir = Path.Combine(outputPath, "ResearchHive_Package");
        Directory.CreateDirectory(packageDir);

        // Create run script
        var runScript = @"@echo off
echo ====================================
echo  ResearchHive - Agentic Research Studio
echo ====================================
echo.
echo Prerequisites:
echo   - .NET 8 Runtime (https://dotnet.microsoft.com/download/dotnet/8.0)
echo   - (Optional) Ollama for local AI (https://ollama.ai)
echo     After installing Ollama, run:
echo       ollama pull llama3.1:8b
echo       ollama pull nomic-embed-text
echo.
echo Starting ResearchHive...
dotnet ResearchHive.dll
if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: Failed to start. Ensure .NET 8 Runtime is installed.
    echo Download from: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
)
";
        File.WriteAllText(Path.Combine(packageDir, "run.bat"), runScript);

        // Create config template
        var configContent = System.Text.Json.JsonSerializer.Serialize(
            new Configuration.AppSettings(),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(packageDir, "appsettings.json"), configContent);

        var readmePath = Path.Combine(packageDir, "README.md");
        File.WriteAllText(readmePath, @"# ResearchHive — Agentic Research + Discovery Studio

## Quick Start

1. **Install .NET 8 Runtime** from https://dotnet.microsoft.com/download/dotnet/8.0
2. **(Optional) Install Ollama** for local AI: https://ollama.ai
   - Pull required models:
     ```
     ollama pull llama3.1:8b
     ollama pull nomic-embed-text
     ```
3. **Run the app**: Double-click `run.bat` or run `dotnet ResearchHive.dll`

## Configuration

Edit `appsettings.json` to customize:

| Setting | Default | Description |
|---------|---------|-------------|
| DataRootPath | %LOCALAPPDATA%\ResearchHive | Where sessions, artifacts, DBs are stored |
| OllamaBaseUrl | http://localhost:11434 | Ollama API endpoint |
| EmbeddingModel | nomic-embed-text | Model for semantic search embeddings |
| SynthesisModel | llama3.1:8b | Model for text synthesis/analysis |
| UsePaidProvider | false | Enable paid API (OpenAI-compatible) |
| MaxConcurrentFetches | 2 | Global concurrent HTTP request limit |
| MinDomainDelaySeconds | 1.5 | Minimum delay between same-domain requests |
| MaxDomainDelaySeconds | 3.0 | Maximum delay with jitter |

## Features

### Sessions Hub
Create and manage isolated research sessions. Each session has its own workspace folder, database, and artifact store.

### Agentic Research
Automated research loop: plan → search → snapshot → extract/index → evaluate → refine → report.
Outputs include **Most Supported View** and **Credible Alternatives / Broader Views** with citations.

### Discovery Studio
Generate hypothesis-driven idea cards with mechanisms, test plans, falsification criteria, novelty checks, and multi-dimensional scoring.

### Materials Explorer
Property-based material search with safety labels (hazards, PPE, environment). Includes ranked candidates with test checklists.

### Programming Research + IP
Multi-approach comparison matrix with license/IP analysis, risk flags, design-arounds, and implementation plans.

### Idea Fusion Engine
Combine research outputs using Blend, CrossApply, Substitute, or Optimize modes with full provenance mapping.

### Polite Browsing
- Max 2 concurrent fetches globally, max 1 per domain
- 1.5–3.0 second delays with jitter between requests
- Exponential backoff on 429/503 responses
- Circuit breaker after repeated failures
- URL deduplication within jobs

## Architecture

- **WPF .NET 8** with MVVM (CommunityToolkit.Mvvm)
- **SQLite** per-session databases
- **Content-addressed** immutable artifact storage (SHA256)
- **Local-first AI** via Ollama (free, no API keys required)
- All browsing follows polite/courtesy rules

## License & Disclaimer

Research outputs include citations but may contain errors. Always verify findings independently. IP assessments are informational, not legal advice.
");

        return packageDir;
    }

    private static string ShortId(string id) =>
        id.Length >= 8 ? id[..8] : id;
}
