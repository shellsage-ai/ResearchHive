using ResearchHive.Core.Configuration;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace ResearchHive.Core.Services;

/// <summary>
/// Clones repositories via git (shallow clone) and discovers indexable files.
/// Falls back to GitHub API ZIP download if git CLI is unavailable.
/// </summary>
public class RepoCloneService
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;

    private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".java", ".go", ".rs", ".rb", ".php", ".swift", ".kt",
        ".md", ".json", ".yaml", ".yml", ".toml", ".xml", ".csproj", ".sln",
        ".txt", ".cfg", ".ini", ".env", ".dockerfile", ".sh", ".ps1", ".bat"
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "dist", "build", ".vs",
        "__pycache__", ".mypy_cache", ".pytest_cache", "packages",
        "vendor", "target", ".gradle", ".idea", "coverage", ".next"
    };

    public RepoCloneService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ResearchHive", "1.0"));
        if (!string.IsNullOrEmpty(settings.GitHubPat))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.GitHubPat);
    }

    /// <summary>
    /// Clone or update a repo. Returns the local clone path.
    /// </summary>
    public async Task<string> CloneOrUpdateAsync(string repoUrl, CancellationToken ct = default)
    {
        var (owner, name) = RepoScannerService.ParseRepoUrl(repoUrl);
        var clonePath = Path.Combine(_settings.RepoClonePath, $"{owner}_{name}");

        // If already cloned, try to update
        if (Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            try
            {
                await RunGitAsync("pull --ff-only", clonePath, ct);
                return clonePath;
            }
            catch
            {
                // If pull fails, wipe and re-clone
                try { Directory.Delete(clonePath, true); } catch { }
            }
        }

        // Try git clone --depth 1
        if (await IsGitAvailableAsync(ct))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(clonePath)!);
            var gitUrl = $"https://github.com/{owner}/{name}.git";
            await RunGitAsync($"clone --depth 1 \"{gitUrl}\" \"{clonePath}\"", null, ct);
            return clonePath;
        }

        // Fallback: download ZIP from GitHub API
        return await DownloadAsZipAsync(owner, name, clonePath, ct);
    }

    /// <summary>
    /// Discover indexable files in a repo directory, prioritized and limited.
    /// Returns list of (absolutePath, relativePath) tuples.
    /// </summary>
    public List<(string AbsolutePath, string RelativePath)> DiscoverFiles(string repoPath)
    {
        var allFiles = new List<(string abs, string rel, int priority)>();
        WalkDirectory(repoPath, repoPath, allFiles);

        // Sort by priority (lower = more important), then by path
        allFiles.Sort((a, b) =>
        {
            var p = a.priority.CompareTo(b.priority);
            return p != 0 ? p : string.Compare(a.rel, b.rel, StringComparison.OrdinalIgnoreCase);
        });

        return allFiles
            .Take(_settings.RepoMaxFiles)
            .Select(f => (f.abs, f.rel))
            .ToList();
    }

    /// <summary>Get the current HEAD commit SHA for cache invalidation.</summary>
    public async Task<string?> GetTreeShaAsync(string repoPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(repoPath, ".git"))) return null;
        try
        {
            var result = await RunGitAsync("rev-parse HEAD", repoPath, ct);
            return result?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private void WalkDirectory(string rootPath, string currentPath, List<(string abs, string rel, int priority)> results)
    {
        foreach (var dir in Directory.EnumerateDirectories(currentPath))
        {
            var dirName = Path.GetFileName(dir);
            if (SkipDirectories.Contains(dirName)) continue;
            WalkDirectory(rootPath, dir, results);
        }

        foreach (var file in Directory.EnumerateFiles(currentPath))
        {
            var ext = Path.GetExtension(file);
            if (!IndexableExtensions.Contains(ext)) continue;

            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > _settings.RepoMaxFileSizeBytes) continue;
                if (fi.Length == 0) continue;
            }
            catch { continue; }

            var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            var priority = GetFilePriority(rel);
            results.Add((file, rel, priority));
        }
    }

    private static int GetFilePriority(string relativePath)
    {
        var lower = relativePath.ToLowerInvariant();
        if (lower.Contains("readme")) return 0;
        if (lower.StartsWith("docs/") || lower.StartsWith("doc/")) return 1;
        if (lower.Contains("program.cs") || lower.Contains("main.") || lower.Contains("app.") || lower.Contains("index.")) return 2;
        if (lower.Contains("startup") || lower.Contains("serviceregistration") || lower.Contains("appsettings")) return 3;
        if (lower.StartsWith("src/") || lower.StartsWith("lib/")) return 4;
        if (lower.Contains("test") || lower.Contains("spec")) return 6;
        return 5;
    }

    private static async Task<bool> IsGitAvailableAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunGitAsync("--version", null, ct);
            return !string.IsNullOrEmpty(result);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> RunGitAsync(string arguments, string? workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;

        using var process = Process.Start(psi);
        if (process == null) throw new InvalidOperationException("Failed to start git process.");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"git {arguments} failed: {error}");
        }

        return output;
    }

    private async Task<string> DownloadAsZipAsync(string owner, string name, string targetPath, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{name}/zipball/HEAD";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var tempZip = Path.GetTempFileName();
        try
        {
            await using (var fs = File.OpenWrite(tempZip))
                await response.Content.CopyToAsync(fs, ct);

            if (Directory.Exists(targetPath))
                Directory.Delete(targetPath, true);

            ZipFile.ExtractToDirectory(tempZip, targetPath);

            // GitHub ZIPs have a top-level directory — flatten it
            var innerDirs = Directory.GetDirectories(targetPath);
            if (innerDirs.Length == 1)
            {
                var inner = innerDirs[0];
                foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
                {
                    var dest = Path.Combine(targetPath, Path.GetFileName(entry));
                    if (Directory.Exists(entry)) Directory.Move(entry, dest);
                    else File.Move(entry, dest);
                }
                Directory.Delete(inner, true);
            }

            return targetPath;
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }
}
