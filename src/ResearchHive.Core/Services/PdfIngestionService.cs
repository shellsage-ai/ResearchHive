using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ResearchHive.Core.Services;

/// <summary>
/// Extracts text from PDFs using PdfPig (text layer) with OCR fallback for scanned/image pages.
/// Two-tier strategy:
///   Tier 1 — PdfPig text extraction (near 100% accurate on text-layer PDFs)
///   Tier 2 — Windows.Media.Ocr fallback per page when text layer yields &lt; 50 chars
/// Each page result is tagged with extraction method so downstream consumers know OCR-sourced text may have minor errors.
/// </summary>
public class PdfIngestionService
{
    private readonly OcrService _ocrService;
    private const int OcrThresholdChars = 50;

    public PdfIngestionService(OcrService ocrService)
    {
        _ocrService = ocrService;
    }

    /// <summary>
    /// Extract text from all pages of a PDF file.
    /// Returns per-page results with extraction method metadata.
    /// </summary>
    public async Task<PdfExtractionResult> ExtractTextAsync(string pdfPath, CancellationToken ct = default)
    {
        if (!File.Exists(pdfPath))
            return new PdfExtractionResult([], "", 0);

        var pages = new List<PdfPageResult>();

        try
        {
            using var document = PdfDocument.Open(pdfPath);

            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                var pageResult = await ExtractPageAsync(pdfPath, page, ct);
                pages.Add(pageResult);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // If PdfPig can't open the file at all, try full-file OCR as last resort
            if (pages.Count == 0)
            {
                var ocrText = await _ocrService.OcrImageFileAsync(pdfPath);
                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    pages.Add(new PdfPageResult(1, ocrText, ExtractionMethod.Ocr));
                }
                else
                {
                    pages.Add(new PdfPageResult(1, $"[PDF extraction failed: {ex.Message}]", ExtractionMethod.Failed));
                }
            }
        }

        var fullText = BuildFullText(pages);
        return new PdfExtractionResult(pages, fullText, pages.Count);
    }

    private async Task<PdfPageResult> ExtractPageAsync(string pdfPath, Page page, CancellationToken ct)
    {
        // Tier 1: Extract text layer via PdfPig
        string textLayerText;
        try
        {
            textLayerText = page.Text ?? "";
        }
        catch
        {
            textLayerText = "";
        }

        // If text layer has sufficient content, use it directly
        if (textLayerText.Length >= OcrThresholdChars)
        {
            return new PdfPageResult(page.Number, textLayerText.Trim(), ExtractionMethod.TextLayer);
        }

        // Tier 2: OCR fallback — text layer is too short, likely a scanned page
        ct.ThrowIfCancellationRequested();

        try
        {
            var ocrText = await _ocrService.OcrImageFileAsync(pdfPath);
            if (!string.IsNullOrWhiteSpace(ocrText) && ocrText.Length >= OcrThresholdChars)
            {
                return new PdfPageResult(page.Number, ocrText.Trim(), ExtractionMethod.Ocr);
            }
        }
        catch
        {
            // OCR unavailable — fall through
        }

        // Use whatever we got from the text layer, even if short
        if (!string.IsNullOrWhiteSpace(textLayerText))
        {
            return new PdfPageResult(page.Number, textLayerText.Trim(), ExtractionMethod.TextLayer);
        }

        return new PdfPageResult(page.Number, "", ExtractionMethod.Failed);
    }

    private static string BuildFullText(List<PdfPageResult> pages)
    {
        var sb = new StringBuilder();
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Text)) continue;

            sb.AppendLine($"--- Page {page.PageNumber} [{page.Method}] ---");
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }
}

// ── Models ──

public enum ExtractionMethod
{
    TextLayer,
    Ocr,
    Failed
}

public record PdfPageResult(int PageNumber, string Text, ExtractionMethod Method);

public record PdfExtractionResult(List<PdfPageResult> Pages, string FullText, int PageCount);
