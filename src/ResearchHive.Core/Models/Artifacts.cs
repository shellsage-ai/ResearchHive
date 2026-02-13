namespace ResearchHive.Core.Models;

public class Artifact
{
    public string Id { get; set; } = string.Empty;  // content-hash
    public string SessionId { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StorePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTime IngestedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class Snapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BundlePath { get; set; } = string.Empty;
    public string HtmlPath { get; set; } = string.Empty;
    public string TextPath { get; set; } = string.Empty;
    public string ScreenshotPath { get; set; } = string.Empty;
    public string? ExtractionPath { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public int HttpStatus { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
}

public class Capture
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string? OcrText { get; set; }
    public List<OcrBox> Boxes { get; set; } = new();
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string SourceDescription { get; set; } = string.Empty;
}

public class OcrBox
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public float Confidence { get; set; }
}

public class Chunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty; // artifact/snapshot/capture id
    public string SourceType { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public int ChunkIndex { get; set; }
    public float[]? Embedding { get; set; }
}

public enum CitationType
{
    WebSnapshot,
    Pdf,
    OcrImage,
    File
}

/// <summary>
/// Wraps an LLM response with metadata about truncation, finish reason, and which model generated it.
/// Used by GenerateWithMetadataAsync for callers that need truncation awareness and model attribution.
/// </summary>
public record LlmResponse(string Text, bool WasTruncated, string? FinishReason, string? ModelName = null);

/// <summary>
/// A chunk stored in the global memory database with cross-session metadata.
/// </summary>
public class GlobalChunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;
    public string DomainPack { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime PromotedUtc { get; set; } = DateTime.UtcNow;
}

public class Citation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public CitationType Type { get; set; }
    public string SourceId { get; set; } = string.Empty; // snapshot_id / artifact_id / capture_id
    public string? ChunkId { get; set; }
    public int? StartOffset { get; set; }
    public int? EndOffset { get; set; }
    public string? Page { get; set; }
    public OcrBox? Box { get; set; }
    public string Excerpt { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty; // short citation label like "[1]"
}
