using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Code-aware text chunker. Splits source code at semantic boundaries
/// (class/method/function declarations) and documentation at paragraph/heading boundaries.
/// No Roslyn dependency — uses regex patterns that work across all languages.
/// </summary>
public class CodeChunker
{
    private readonly AppSettings _settings;

    // Regex patterns for code boundary detection (top-level declarations)
    private static readonly Regex CodeBoundaryPattern = new(
        @"^(?:\s*(?:public|private|protected|internal|static|abstract|sealed|override|virtual|async|export|default|def|fn|func|class|struct|enum|interface|trait|impl|module|package)\b)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Broader pattern for function-like boundaries in dynamic languages
    private static readonly Regex FunctionBoundaryPattern = new(
        @"^(?:(?:async\s+)?function\s|(?:const|let|var)\s+\w+\s*=\s*(?:async\s+)?\(|def\s+\w+|class\s+\w+|module\s+|pub\s+(?:fn|struct|enum|trait|impl)|fn\s+\w+|func\s+\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".java", ".go", ".rs", ".rb", ".php", ".swift", ".kt",
        ".sh", ".ps1", ".bat"
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".xml", ".cfg", ".ini", ".env",
        ".csproj", ".sln", ".dockerfile"
    };

    public CodeChunker(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Chunk a file's content using code-aware or document-aware splitting.
    /// </summary>
    /// <param name="relativePath">File's path relative to repo root (used in chunk context header).</param>
    /// <param name="content">Full file content.</param>
    /// <param name="sessionId">Session to tag chunks with.</param>
    /// <param name="sourceId">Source identifier (e.g., "owner/repo").</param>
    /// <returns>List of chunks with context headers.</returns>
    public List<Chunk> ChunkFile(string relativePath, string content, string sessionId, string sourceId)
    {
        var ext = Path.GetExtension(relativePath);
        var chunkSize = _settings.RepoChunkSize;
        var overlap = _settings.RepoChunkOverlap;

        List<string> rawChunks;

        if (CodeExtensions.Contains(ext))
            rawChunks = ChunkCode(content, chunkSize, overlap);
        else
            rawChunks = ChunkDocument(content, chunkSize, overlap);

        var result = new List<Chunk>();
        for (int i = 0; i < rawChunks.Count; i++)
        {
            var text = $"// File: {relativePath}\n{rawChunks[i]}";
            result.Add(new Chunk
            {
                SessionId = sessionId,
                SourceId = sourceId,
                SourceType = CodeExtensions.Contains(ext) ? "repo_code" : "repo_doc",
                Text = text,
                ChunkIndex = i,
                StartOffset = 0,
                EndOffset = text.Length
            });
        }

        return result;
    }

    /// <summary>Split code at semantic boundaries (class/function declarations).</summary>
    private List<string> ChunkCode(string content, int chunkSizeWords, int overlapWords)
    {
        var lines = content.Split('\n');
        var boundaries = new List<int> { 0 }; // Start at line 0

        for (int i = 1; i < lines.Length; i++)
        {
            if (CodeBoundaryPattern.IsMatch(lines[i]) || FunctionBoundaryPattern.IsMatch(lines[i]))
                boundaries.Add(i);
        }

        // Build segments between boundaries
        var segments = new List<string>();
        for (int b = 0; b < boundaries.Count; b++)
        {
            int start = boundaries[b];
            int end = b + 1 < boundaries.Count ? boundaries[b + 1] : lines.Length;
            var segment = string.Join('\n', lines[start..end]);
            if (!string.IsNullOrWhiteSpace(segment))
                segments.Add(segment);
        }

        // Merge small segments, split large ones
        return MergeAndSplit(segments, chunkSizeWords, overlapWords);
    }

    /// <summary>Split documents (markdown, JSON, etc.) by word count with overlap.</summary>
    private static List<string> ChunkDocument(string content, int chunkSizeWords, int overlapWords)
    {
        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= chunkSizeWords)
            return new List<string> { content };

        var chunks = new List<string>();
        int pos = 0;
        while (pos < words.Length)
        {
            var end = Math.Min(pos + chunkSizeWords, words.Length);
            chunks.Add(string.Join(' ', words[pos..end]));
            pos += chunkSizeWords - overlapWords;
            if (pos >= words.Length) break;
        }
        return chunks;
    }

    /// <summary>Merge small segments together and split oversized ones.</summary>
    private static List<string> MergeAndSplit(List<string> segments, int chunkSizeWords, int overlapWords)
    {
        var result = new List<string>();
        var current = new List<string>();
        int currentWords = 0;

        foreach (var seg in segments)
        {
            var segWords = CountWords(seg);

            // If a single segment is too large, split it by word count
            if (segWords > chunkSizeWords * 2)
            {
                // Flush current buffer first
                if (current.Count > 0)
                {
                    result.Add(string.Join("\n\n", current));
                    current.Clear();
                    currentWords = 0;
                }

                // Split the oversized segment
                result.AddRange(ChunkDocument(seg, chunkSizeWords, overlapWords));
                continue;
            }

            if (currentWords + segWords > chunkSizeWords && current.Count > 0)
            {
                result.Add(string.Join("\n\n", current));
                // Keep last segment for overlap
                var last = current.Last();
                current.Clear();
                currentWords = 0;
                if (overlapWords > 0 && CountWords(last) <= overlapWords)
                {
                    current.Add(last);
                    currentWords = CountWords(last);
                }
            }

            current.Add(seg);
            currentWords += segWords;
        }

        if (current.Count > 0)
            result.Add(string.Join("\n\n", current));

        return result;
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
