using ResearchHive.Core.Models;
using System.Security.Cryptography;

namespace ResearchHive.Core.Services;

/// <summary>
/// Content-addressed immutable artifact store
/// </summary>
public class ArtifactStore
{
    private readonly string _basePath;
    private readonly SessionManager _sessionManager;

    public ArtifactStore(string basePath, SessionManager sessionManager)
    {
        _basePath = basePath;
        _sessionManager = sessionManager;
    }

    public Artifact IngestFile(string sessionId, string filePath)
    {
        var session = _sessionManager.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var bytes = File.ReadAllBytes(filePath);
        var hash = ComputeHash(bytes);
        var ext = Path.GetExtension(filePath);
        var storePath = Path.Combine(session.WorkspacePath, "Artifacts", $"{hash}{ext}");

        if (!File.Exists(storePath))
        {
            File.WriteAllBytes(storePath, bytes);
        }

        var artifact = new Artifact
        {
            Id = hash,
            SessionId = sessionId,
            OriginalName = Path.GetFileName(filePath),
            ContentType = GetContentType(ext),
            SizeBytes = bytes.Length,
            StorePath = storePath,
            ContentHash = hash,
            IngestedUtc = DateTime.UtcNow
        };

        var db = _sessionManager.GetSessionDb(sessionId);
        db.SaveArtifact(artifact);
        return artifact;
    }

    public Artifact IngestBytes(string sessionId, string fileName, byte[] data)
    {
        var session = _sessionManager.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var hash = ComputeHash(data);
        var ext = Path.GetExtension(fileName);
        var storePath = Path.Combine(session.WorkspacePath, "Artifacts", $"{hash}{ext}");

        if (!File.Exists(storePath))
        {
            File.WriteAllBytes(storePath, data);
        }

        var artifact = new Artifact
        {
            Id = hash,
            SessionId = sessionId,
            OriginalName = fileName,
            ContentType = GetContentType(ext),
            SizeBytes = data.Length,
            StorePath = storePath,
            ContentHash = hash,
            IngestedUtc = DateTime.UtcNow
        };

        var db = _sessionManager.GetSessionDb(sessionId);
        db.SaveArtifact(artifact);
        return artifact;
    }

    public List<Artifact> GetArtifacts(string sessionId)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        return db.GetArtifacts();
    }

    public byte[]? ReadArtifact(Artifact artifact)
    {
        return File.Exists(artifact.StorePath) ? File.ReadAllBytes(artifact.StorePath) : null;
    }

    public static string ComputeHash(byte[] data)
    {
        var hashBytes = SHA256.HashData(data);
        return Convert.ToHexString(hashBytes)[..24].ToLowerInvariant();
    }

    private static string GetContentType(string ext) => ext.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
