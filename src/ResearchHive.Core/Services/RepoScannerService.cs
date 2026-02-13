using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Scans GitHub repos via REST API to build a RepoProfile: metadata, README, dependencies.
/// Does NOT perform LLM analysis — that happens after indexing in RepoIntelligenceJobRunner
/// so the analysis is grounded against the full codebase via RAG.
/// </summary>
public class RepoScannerService
{
    private readonly HttpClient _http;
    private readonly LlmService _llmService;
    private readonly AppSettings _settings;

    /// <summary>Number of GitHub API calls made during the last ScanAsync invocation.</summary>
    public int LastScanApiCallCount { get; private set; }

    public RepoScannerService(AppSettings settings, LlmService llmService)
    {
        _settings = settings;
        _llmService = llmService;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ResearchHive", "1.0"));
        if (!string.IsNullOrEmpty(settings.GitHubPat))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.GitHubPat);
    }

    /// <summary>Parse "https://github.com/owner/repo" into (owner, repo).</summary>
    public static (string owner, string repo) ParseRepoUrl(string url)
    {
        url = url.TrimEnd('/');
        if (url.EndsWith(".git")) url = url[..^4];
        var uri = new Uri(url);
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2) throw new ArgumentException($"Invalid GitHub URL: {url}");
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Detect whether the input is a local file system path rather than a URL.
    /// Supports Windows absolute paths (C:\...), UNC (\\server\...), Unix absolute (/home/...),
    /// and relative paths (./foo, ../bar).
    /// </summary>
    public static bool IsLocalPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();

        // Obvious URL schemes
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return false;

        // Windows absolute paths: C:\... D:/...
        if (input.Length >= 3 && char.IsLetter(input[0]) && input[1] == ':' && (input[2] == '\\' || input[2] == '/'))
            return true;

        // UNC paths: \\server\share
        if (input.StartsWith("\\\\")) return true;

        // Unix absolute paths
        if (input.StartsWith("/")) return true;

        // Relative paths
        if (input.StartsWith("./") || input.StartsWith(".\\") ||
            input.StartsWith("../") || input.StartsWith("..\\"))
            return true;

        // Last resort: if it exists on disk as a directory, treat it as local
        try { return Directory.Exists(input); } catch { return false; }
    }

    public async Task<RepoProfile> ScanAsync(string repoUrl, CancellationToken ct = default)
    {
        // Route to local scanner if the input is a file system path
        if (IsLocalPath(repoUrl))
            return await ScanLocalAsync(repoUrl, ct);

        LastScanApiCallCount = 0;
        var (owner, repo) = ParseRepoUrl(repoUrl);
        var profile = new RepoProfile { RepoUrl = repoUrl, Owner = owner, Name = repo };

        // Parallel initial API calls — repo metadata, languages, readme, root contents are all independent
        var repoJsonTask = FetchJsonAsync($"https://api.github.com/repos/{owner}/{repo}", ct);
        var langsJsonTask = FetchJsonAsync($"https://api.github.com/repos/{owner}/{repo}/languages", ct);
        var readmeJsonTask = FetchJsonAsync($"https://api.github.com/repos/{owner}/{repo}/readme", ct);
        var rootContentsTask = FetchJsonArrayAsync($"https://api.github.com/repos/{owner}/{repo}/contents/", ct);

        await Task.WhenAll(repoJsonTask, langsJsonTask, readmeJsonTask, rootContentsTask);

        // 1. Process repo metadata
        var repoJson = await repoJsonTask;
        if (repoJson != null)
        {
            var rj = repoJson.Value;
            profile.Description = GetString(rj, "description");
            profile.Stars = GetInt(rj, "stargazers_count");
            profile.Forks = GetInt(rj, "forks_count");
            profile.OpenIssues = GetInt(rj, "open_issues_count");
            profile.PrimaryLanguage = GetString(rj, "language");
            if (rj.TryGetProperty("pushed_at", out var pushed) && pushed.ValueKind == JsonValueKind.String)
                profile.LastCommitUtc = DateTime.TryParse(pushed.GetString(), out var dt) ? dt : null;
            if (rj.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Array)
                profile.Topics = topics.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToList();
        }

        // 2. Process languages
        var langsJson = await langsJsonTask;
        if (langsJson != null)
            profile.Languages = langsJson.Value.EnumerateObject().Select(p => p.Name).ToList();

        // 3. Process README
        var readmeJson = await readmeJsonTask;
        if (readmeJson != null)
        {
            var content = GetString(readmeJson, "content");
            var encoding = GetString(readmeJson, "encoding");
            if (encoding == "base64" && !string.IsNullOrEmpty(content))
                profile.ReadmeContent = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
        }

        // 4. Detect and fetch dependency files
        var rootContents = await rootContentsTask;
        var depFiles = new Dictionary<string, string>();
        var knownManifests = new[] { "package.json", "requirements.txt", "Cargo.toml", "go.mod", "Gemfile", "pom.xml", "build.gradle", "pubspec.yaml", "composer.json" };

        if (rootContents != null)
        {
            // Capture first 3 root-level entries as scan proof
            foreach (var item in rootContents.Take(3))
            {
                var entryName = GetString(item, "name");
                var entryType = GetString(item, "type"); // "file" or "dir"
                if (!string.IsNullOrEmpty(entryName))
                    profile.TopLevelEntries.Add(new RepoEntry { Name = entryName, Type = entryType });
            }

            // Also look for *.csproj files
            foreach (var item in rootContents)
            {
                var name = GetString(item, "name");
                if (knownManifests.Contains(name) || name.EndsWith(".csproj") || name.EndsWith(".sln"))
                {
                    var fileContent = await FetchFileContent($"https://api.github.com/repos/{owner}/{repo}/contents/{name}", ct);
                    if (fileContent != null) depFiles[name] = fileContent;
                }
            }
            // Check src/ folder for .csproj files — recurse into subdirs
            foreach (var item in rootContents)
            {
                if (GetString(item, "name") == "src" && GetString(item, "type") == "dir")
                {
                    var srcContents = await FetchJsonArrayAsync($"https://api.github.com/repos/{owner}/{repo}/contents/src", ct);
                    if (srcContents != null)
                    {
                        foreach (var srcItem in srcContents)
                        {
                            var nm = GetString(srcItem, "name");
                            var tp = GetString(srcItem, "type");
                            if (nm.EndsWith(".csproj"))
                            {
                                var fc = await FetchFileContent($"https://api.github.com/repos/{owner}/{repo}/contents/src/{nm}", ct);
                                if (fc != null) depFiles[$"src/{nm}"] = fc;
                            }
                            // Recurse one level deeper: src/ProjectName/*.csproj
                            else if (tp == "dir")
                            {
                                try
                                {
                                    var subContents = await FetchJsonArrayAsync($"https://api.github.com/repos/{owner}/{repo}/contents/src/{nm}", ct);
                                    if (subContents != null)
                                    {
                                        foreach (var subItem in subContents)
                                        {
                                            var subName = GetString(subItem, "name");
                                            if (subName.EndsWith(".csproj"))
                                            {
                                                var fc = await FetchFileContent($"https://api.github.com/repos/{owner}/{repo}/contents/src/{nm}/{subName}", ct);
                                                if (fc != null) depFiles[$"src/{nm}/{subName}"] = fc;
                                            }
                                        }
                                    }
                                }
                                catch { /* non-fatal — skip subdirs we can't read */ }
                            }
                        }
                    }
                }
            }

            // Also check tests/ folder for .csproj files (common .NET layout)
            foreach (var item in rootContents)
            {
                if (GetString(item, "name") == "tests" && GetString(item, "type") == "dir")
                {
                    try
                    {
                        var testsContents = await FetchJsonArrayAsync($"https://api.github.com/repos/{owner}/{repo}/contents/tests", ct);
                        if (testsContents != null)
                        {
                            foreach (var testItem in testsContents)
                            {
                                var tn = GetString(testItem, "name");
                                var tt = GetString(testItem, "type");
                                if (tn.EndsWith(".csproj"))
                                {
                                    var fc = await FetchFileContent($"https://api.github.com/repos/{owner}/{repo}/contents/tests/{tn}", ct);
                                    if (fc != null) depFiles[$"tests/{tn}"] = fc;
                                }
                                else if (tt == "dir")
                                {
                                    var subContents = await FetchJsonArrayAsync($"https://api.github.com/repos/{owner}/{repo}/contents/tests/{tn}", ct);
                                    if (subContents != null)
                                    {
                                        foreach (var subItem in subContents)
                                        {
                                            var sn = GetString(subItem, "name");
                                            if (sn.EndsWith(".csproj"))
                                            {
                                                var fc = await FetchFileContent($"https://api.github.com/repos/{owner}/{repo}/contents/tests/{tn}/{sn}", ct);
                                                if (fc != null) depFiles[$"tests/{tn}/{sn}"] = fc;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { /* non-fatal */ }
                }
            }
        }

        profile.Dependencies = ParseDependencies(depFiles);
        profile.ManifestContents = depFiles; // Preserve full manifest contents for downstream analysis

        // Deterministic framework detection — ensures key technologies are captured
        // regardless of LLM quality. LLM analysis may add more later.
        var frameworkHints = DetectFrameworkHints(profile.Dependencies, depFiles);
        profile.Frameworks.AddRange(frameworkHints);

        return profile;
    }

    /// <summary>
    /// Scan a local directory as a repository. Reads metadata from the file system instead of GitHub API.
    /// Discovers README, manifest files, languages, and root entries directly from disk.
    /// </summary>
    public async Task<RepoProfile> ScanLocalAsync(string localPath, CancellationToken ct = default)
    {
        LastScanApiCallCount = 0;
        localPath = Path.GetFullPath(localPath.Trim());

        if (!Directory.Exists(localPath))
            throw new DirectoryNotFoundException($"Local path does not exist: {localPath}");

        var dirName = Path.GetFileName(localPath);
        var profile = new RepoProfile
        {
            RepoUrl = localPath,
            Owner = "local",
            Name = dirName,
        };

        // 1. Read README if present
        var readmePaths = new[] { "README.md", "readme.md", "README.txt", "README", "README.rst" };
        foreach (var rp in readmePaths)
        {
            var readmePath = Path.Combine(localPath, rp);
            if (File.Exists(readmePath))
            {
                try { profile.ReadmeContent = await File.ReadAllTextAsync(readmePath, ct); }
                catch { /* non-fatal */ }
                break;
            }
        }

        // 2. Capture root-level entries (up to 3 as scan proof)
        try
        {
            var rootEntries = Directory.EnumerateFileSystemEntries(localPath)
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith(".git"))
                .Take(3)
                .ToList();
            foreach (var entry in rootEntries)
            {
                var fullPath = Path.Combine(localPath, entry!);
                profile.TopLevelEntries.Add(new RepoEntry
                {
                    Name = entry!,
                    Type = Directory.Exists(fullPath) ? "dir" : "file"
                });
            }
        }
        catch { /* non-fatal */ }

        // 3. Detect languages from file extensions
        var langCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(localPath, "*.*", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(file) ?? "";
                if (dir.Contains(".git") || dir.Contains("node_modules") || dir.Contains("bin") || dir.Contains("obj"))
                    continue;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var lang = ext switch
                {
                    ".cs" => "C#", ".py" => "Python", ".js" => "JavaScript", ".ts" => "TypeScript",
                    ".java" => "Java", ".go" => "Go", ".rs" => "Rust", ".rb" => "Ruby",
                    ".php" => "PHP", ".swift" => "Swift", ".kt" => "Kotlin", ".cpp" or ".cc" => "C++",
                    ".c" or ".h" => "C", ".r" => "R", ".scala" => "Scala",
                    _ => null
                };
                if (lang != null)
                    langCounts[lang] = langCounts.GetValueOrDefault(lang) + 1;
            }
        }
        catch { /* non-fatal */ }

        profile.Languages = langCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        profile.PrimaryLanguage = profile.Languages.FirstOrDefault() ?? "Unknown";

        // 4. Find and read manifest files
        var depFiles = new Dictionary<string, string>();
        var manifestNames = new[] { "package.json", "requirements.txt", "Cargo.toml", "go.mod", "Gemfile", "pom.xml", "build.gradle", "pubspec.yaml", "composer.json" };

        // Root level manifests
        foreach (var mf in manifestNames)
        {
            var mfPath = Path.Combine(localPath, mf);
            if (File.Exists(mfPath))
            {
                try { depFiles[mf] = await File.ReadAllTextAsync(mfPath, ct); }
                catch { /* non-fatal */ }
            }
        }

        // Find .csproj / .sln files recursively (skip bin/obj/node_modules)
        try
        {
            foreach (var csproj in Directory.EnumerateFiles(localPath, "*.csproj", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(csproj) ?? "";
                if (dir.Contains("bin") || dir.Contains("obj") || dir.Contains("node_modules")) continue;
                var relPath = Path.GetRelativePath(localPath, csproj).Replace('\\', '/');
                try { depFiles[relPath] = await File.ReadAllTextAsync(csproj, ct); }
                catch { /* non-fatal */ }
            }
            foreach (var sln in Directory.EnumerateFiles(localPath, "*.sln", SearchOption.TopDirectoryOnly))
            {
                var relPath = Path.GetRelativePath(localPath, sln).Replace('\\', '/');
                try { depFiles[relPath] = await File.ReadAllTextAsync(sln, ct); }
                catch { /* non-fatal */ }
            }
        }
        catch { /* non-fatal */ }

        // 5. Try to get last commit date from git
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git", Arguments = "log -1 --format=%cI",
                WorkingDirectory = localPath,
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && DateTime.TryParse(output.Trim(), out var lastCommit))
                    profile.LastCommitUtc = lastCommit.ToUniversalTime();
            }
        }
        catch { /* not a git repo or git unavailable — non-fatal */ }

        profile.Dependencies = ParseDependencies(depFiles);
        profile.ManifestContents = depFiles;

        var frameworkHints = DetectFrameworkHints(profile.Dependencies, depFiles);
        profile.Frameworks.AddRange(frameworkHints);

        return profile;
    }

    /// <summary>
    /// Perform a shallow LLM analysis using only metadata + README + manifests.
    /// Used as fallback when code indexing is unavailable.
    /// </summary>
    public async Task<RepoProfile> AnalyzeShallowAsync(RepoProfile profile, CancellationToken ct = default)
    {
        var analysisPrompt = BuildAnalysisPrompt(profile);
        var analysis = await _llmService.GenerateAsync(analysisPrompt,
            "You are a software architecture analyst. Respond in the exact format requested. Be specific and actionable.", ct: ct);
        ParseAnalysis(analysis, profile);
        return profile;
    }

    private string BuildAnalysisPrompt(RepoProfile profile)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze this GitHub repository and provide a structured assessment.");
        sb.AppendLine();
        sb.AppendLine($"## Repository: {profile.Owner}/{profile.Name}");
        sb.AppendLine($"- Description: {profile.Description}");
        sb.AppendLine($"- Primary Language: {profile.PrimaryLanguage}");
        sb.AppendLine($"- Languages: {string.Join(", ", profile.Languages)}");
        sb.AppendLine($"- Stars: {profile.Stars} | Forks: {profile.Forks} | Open Issues: {profile.OpenIssues}");
        sb.AppendLine($"- Topics: {string.Join(", ", profile.Topics)}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(profile.ReadmeContent))
        {
            sb.AppendLine("## README:");
            sb.AppendLine(profile.ReadmeContent);
            sb.AppendLine();
        }
        foreach (var (file, content) in profile.ManifestContents)
        {
            sb.AppendLine($"## {file}:");
            sb.AppendLine(content);
            sb.AppendLine();
        }
        var factSection = profile.FactSheet?.ToPromptSection();
        if (!string.IsNullOrEmpty(factSection))
        {
            sb.AppendLine(factSection);
            sb.AppendLine();
        }
        AppendFormatInstructions(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Build a RAG-grounded analysis prompt using indexed code chunks + CodeBook.
    /// Called by RepoIntelligenceJobRunner after indexing.
    /// </summary>
    public static string BuildRagAnalysisPrompt(RepoProfile profile, string? codeBook, IReadOnlyList<string> retrievedChunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze this GitHub repository using the ACTUAL SOURCE CODE provided below.");
        sb.AppendLine("Base your assessment on what the code actually does, not surface-level assumptions.");
        sb.AppendLine();
        sb.AppendLine($"## Repository: {profile.Owner}/{profile.Name}");
        sb.AppendLine($"- Description: {profile.Description}");
        sb.AppendLine($"- Primary Language: {profile.PrimaryLanguage}");
        sb.AppendLine($"- Languages: {string.Join(", ", profile.Languages)}");
        sb.AppendLine($"- Dependencies ({profile.Dependencies.Count}): {string.Join(", ", profile.Dependencies.Select(d => d.Name))}");
        sb.AppendLine($"- Indexed: {profile.IndexedFileCount} files, {profile.IndexedChunkCount} chunks");
        sb.AppendLine();
        var factSection = profile.FactSheet?.ToPromptSection();
        if (!string.IsNullOrEmpty(factSection))
        {
            sb.AppendLine(factSection);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(codeBook))
        {
            sb.AppendLine("## Architecture Summary (CodeBook):");
            sb.AppendLine(codeBook);
            sb.AppendLine();
        }

        sb.AppendLine("## Source Code Excerpts (retrieved via semantic + keyword search):");
        sb.AppendLine();
        for (int i = 0; i < retrievedChunks.Count; i++)
        {
            sb.AppendLine($"### Excerpt {i + 1}:");
            sb.AppendLine(retrievedChunks[i]);
            sb.AppendLine("---");
        }
        sb.AppendLine();
        AppendFormatInstructions(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Build a gap verification prompt: for each proposed gap, we provide the relevant
    /// code chunks so the LLM can determine if the gap is real or already addressed.
    /// </summary>
    public static string BuildGapVerificationPrompt(RepoProfile profile, IReadOnlyList<(string gap, IReadOnlyList<string> chunks)> gapEvidence)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are verifying gap claims about the repository {profile.Owner}/{profile.Name}.");
        sb.AppendLine("For each proposed gap, I provide the most relevant source code from the actual codebase.");
        sb.AppendLine("Determine if each gap is REAL (the codebase truly lacks this) or FALSE (the codebase already addresses it).");
        sb.AppendLine("Only keep gaps that are genuinely missing. Remove false positives.");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT RULES:");
        sb.AppendLine("- A gap about something MISSING (no tests, no CI/CD, no docs) is REAL if no code evidence contradicts it.");
        sb.AppendLine("- '(No relevant code found)' for a missing-feature gap means it IS real — do NOT remove it.");
        sb.AppendLine("- A gap that merely critiques HOW an existing feature works is FALSE — remove it.");
        sb.AppendLine("- Keep at least 3 verified gaps. If fewer than 3 survive, keep the most impactful remaining ones.");
        sb.AppendLine();

        foreach (var (gap, chunks) in gapEvidence)
        {
            sb.AppendLine($"### Gap Claim: {gap}");
            if (chunks.Count == 0)
            {
                sb.AppendLine("(No relevant code found — gap is likely real)");
            }
            else
            {
                sb.AppendLine("Relevant code found:");
                foreach (var chunk in chunks)
                {
                    sb.AppendLine(chunk);
                    sb.AppendLine("---");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("Respond with EXACTLY this format:");
        sb.AppendLine("## Verified Gaps");
        sb.AppendLine("- gap description (only gaps that are genuinely missing)");
        sb.AppendLine("## False Positives Removed");
        sb.AppendLine("- gap description: REASON it's already addressed in code");
        return sb.ToString();
    }

    private static void AppendFormatInstructions(System.Text.StringBuilder sb)
    {
        sb.AppendLine("Respond with EXACTLY this format:");
        sb.AppendLine("## Frameworks");
        sb.AppendLine("- framework1");
        sb.AppendLine("- framework2");
        sb.AppendLine("## Strengths");
        sb.AppendLine("- strength1");
        sb.AppendLine("- strength2");
        sb.AppendLine("(list at least 5 specific strengths based on what the code actually implements)");
        sb.AppendLine("## Gaps");
        sb.AppendLine("- gap1");
        sb.AppendLine("- gap2");
        sb.AppendLine("(list at least 5 specific weaknesses, missing features, or improvement opportunities — based on code evidence, not assumptions)");
        sb.AppendLine("IMPORTANT: Gaps must be MISSING capabilities, not critiques of existing features.");
        sb.AppendLine("Bad gap example: 'The search engine assumes X which limits Y' — this critiques something that EXISTS.");
        sb.AppendLine("Good gap example: 'No CI/CD pipeline configuration' — this identifies something ABSENT.");
        sb.AppendLine("Focus on: missing tests, missing docs, missing config, missing security features, missing monitoring, missing error handling patterns, etc.");
    }

    public static void ParseAnalysis(string analysis, RepoProfile profile)
    {
        var lines = analysis.Split('\n').Select(l => l.Trim()).ToList();
        string? currentSection = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("## Frameworks", StringComparison.OrdinalIgnoreCase)) { currentSection = "frameworks"; continue; }
            if (line.StartsWith("## Strengths", StringComparison.OrdinalIgnoreCase)) { currentSection = "strengths"; continue; }
            if (line.StartsWith("## Gaps", StringComparison.OrdinalIgnoreCase)) { currentSection = "gaps"; continue; }
            if (line.StartsWith("## ")) { currentSection = null; continue; }

            if (line.StartsWith("- ") && line.Length > 2)
            {
                var text = line[2..].Trim();
                switch (currentSection)
                {
                    case "frameworks":
                        // Only add if not already present from deterministic detection
                        if (!profile.Frameworks.Any(f => f.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                            f.Contains(text.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                            profile.Frameworks.Add(text);
                        break;
                    case "strengths": profile.Strengths.Add(text); break;
                    case "gaps": profile.Gaps.Add(text); break;
                }
            }
        }
    }

    private static List<RepoDependency> ParseDependencies(Dictionary<string, string> depFiles)
    {
        var deps = new List<RepoDependency>();
        foreach (var (file, content) in depFiles)
        {
            try
            {
                if (file.EndsWith("package.json"))
                {
                    var json = JsonDocument.Parse(content);
                    void ExtractNpmDeps(string section)
                    {
                        if (json.RootElement.TryGetProperty(section, out var d) && d.ValueKind == JsonValueKind.Object)
                            foreach (var p in d.EnumerateObject())
                                deps.Add(new RepoDependency { Name = p.Name, Version = p.Value.GetString() ?? "", ManifestFile = file });
                    }
                    ExtractNpmDeps("dependencies");
                    ExtractNpmDeps("devDependencies");
                }
                else if (file.EndsWith("requirements.txt"))
                {
                    foreach (var line in content.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0 && !trimmed.StartsWith("#"))
                        {
                            var parts = trimmed.Split("==");
                            deps.Add(new RepoDependency { Name = parts[0].Split(">=")[0].Split("<=")[0].Trim(), Version = parts.Length > 1 ? parts[1].Trim() : "", ManifestFile = file });
                        }
                    }
                }
                else if (file.EndsWith(".csproj"))
                {
                    // Simple XML parsing for PackageReference
                    foreach (var line in content.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.Contains("PackageReference"))
                        {
                            var nameMatch = System.Text.RegularExpressions.Regex.Match(t, @"Include=""([^""]+)""");
                            var verMatch = System.Text.RegularExpressions.Regex.Match(t, @"Version=""([^""]+)""");
                            if (nameMatch.Success)
                                deps.Add(new RepoDependency { Name = nameMatch.Groups[1].Value, Version = verMatch.Success ? verMatch.Groups[1].Value : "", ManifestFile = file });
                        }
                    }
                }
                else if (file.EndsWith("Cargo.toml"))
                {
                    bool inDeps = false;
                    foreach (var line in content.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("[dependencies]")) { inDeps = true; continue; }
                        if (t.StartsWith("[")) { inDeps = false; continue; }
                        if (inDeps && t.Contains("="))
                        {
                            var parts = t.Split('=', 2);
                            deps.Add(new RepoDependency { Name = parts[0].Trim(), Version = parts[1].Trim().Trim('"'), ManifestFile = file });
                        }
                    }
                }
                else if (file.EndsWith("go.mod"))
                {
                    foreach (var line in content.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("require") || t == "(" || t == ")") continue;
                        if (t.Contains(" v"))
                        {
                            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                                deps.Add(new RepoDependency { Name = parts[0], Version = parts[1], ManifestFile = file });
                        }
                    }
                }
            }
            catch { /* best effort parsing */ }
        }
        return deps;
    }

    /// <summary>
    /// Deterministic framework detection from dependency names. Maps known packages
    /// to human-readable framework labels. Supplements LLM-inferred frameworks to
    /// ensure key technologies are always captured even when the LLM is generic.
    /// </summary>
    public static List<string> DetectFrameworkHints(List<RepoDependency> deps, Dictionary<string, string> manifests)
    {
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depNames = new HashSet<string>(deps.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        // .NET / C# packages
        if (depNames.Contains("CommunityToolkit.Mvvm")) hints.Add("WPF + MVVM (CommunityToolkit.Mvvm)");
        if (depNames.Contains("Microsoft.Extensions.DependencyInjection")) hints.Add("Microsoft DI (Microsoft.Extensions.DependencyInjection)");
        if (depNames.Contains("Microsoft.AspNetCore.App") || depNames.Any(d => d.StartsWith("Microsoft.AspNetCore"))) hints.Add("ASP.NET Core");
        if (depNames.Contains("Microsoft.EntityFrameworkCore") || depNames.Any(d => d.StartsWith("Microsoft.EntityFrameworkCore"))) hints.Add("Entity Framework Core");
        if (depNames.Contains("Microsoft.Playwright")) hints.Add("Playwright (browser automation)");
        if (depNames.Contains("Selenium.WebDriver") || depNames.Contains("Selenium.Support")) hints.Add("Selenium WebDriver");
        if (depNames.Contains("xunit") || depNames.Contains("xunit.runner.visualstudio")) hints.Add("xUnit");
        if (depNames.Contains("FluentAssertions")) hints.Add("FluentAssertions");
        if (depNames.Contains("Moq")) hints.Add("Moq");
        if (depNames.Contains("coverlet.collector")) hints.Add("Coverlet (code coverage)");
        if (depNames.Contains("Microsoft.Data.Sqlite") || depNames.Contains("System.Data.SQLite") || depNames.Contains("Microsoft.Data.Sqlite.Core")) hints.Add("SQLite");
        if (depNames.Contains("PdfPig")) hints.Add("PdfPig (PDF extraction)");
        if (depNames.Contains("Tesseract")) hints.Add("Tesseract OCR");
        if (depNames.Contains("Newtonsoft.Json")) hints.Add("Newtonsoft.Json");
        if (depNames.Contains("Serilog") || depNames.Any(d => d.StartsWith("Serilog"))) hints.Add("Serilog (structured logging)");
        if (depNames.Contains("NLog")) hints.Add("NLog");
        if (depNames.Contains("Polly")) hints.Add("Polly (resilience)");
        if (depNames.Contains("MediatR")) hints.Add("MediatR (CQRS/mediator)");
        if (depNames.Contains("AutoMapper")) hints.Add("AutoMapper");
        if (depNames.Contains("Dapper")) hints.Add("Dapper (micro-ORM)");
        if (depNames.Contains("Blazor") || depNames.Any(d => d.Contains("Blazor"))) hints.Add("Blazor");

        // JavaScript / TypeScript packages
        if (depNames.Contains("react") || depNames.Contains("react-dom")) hints.Add("React");
        if (depNames.Contains("next")) hints.Add("Next.js");
        if (depNames.Contains("vue")) hints.Add("Vue.js");
        if (depNames.Contains("@angular/core")) hints.Add("Angular");
        if (depNames.Contains("express")) hints.Add("Express.js");
        if (depNames.Contains("typescript")) hints.Add("TypeScript");
        if (depNames.Contains("jest")) hints.Add("Jest");
        if (depNames.Contains("mocha")) hints.Add("Mocha");
        if (depNames.Contains("tailwindcss")) hints.Add("Tailwind CSS");
        if (depNames.Contains("vite")) hints.Add("Vite");
        if (depNames.Contains("webpack")) hints.Add("Webpack");
        if (depNames.Contains("prisma") || depNames.Contains("@prisma/client")) hints.Add("Prisma ORM");

        // Python packages
        if (depNames.Contains("django") || depNames.Contains("Django")) hints.Add("Django");
        if (depNames.Contains("flask") || depNames.Contains("Flask")) hints.Add("Flask");
        if (depNames.Contains("fastapi") || depNames.Contains("FastAPI")) hints.Add("FastAPI");
        if (depNames.Contains("pytorch") || depNames.Contains("torch")) hints.Add("PyTorch");
        if (depNames.Contains("tensorflow")) hints.Add("TensorFlow");
        if (depNames.Contains("pandas")) hints.Add("Pandas");
        if (depNames.Contains("numpy")) hints.Add("NumPy");
        if (depNames.Contains("pytest")) hints.Add("Pytest");

        // Detect target framework from .csproj content
        foreach (var (file, content) in manifests)
        {
            if (!file.EndsWith(".csproj")) continue;
            var tfMatch = System.Text.RegularExpressions.Regex.Match(content, @"<TargetFramework>([^<]+)</TargetFramework>");
            if (tfMatch.Success)
            {
                var tf = tfMatch.Groups[1].Value;
                if (tf.StartsWith("net8.0")) hints.Add(".NET 8");
                else if (tf.StartsWith("net9.0")) hints.Add(".NET 9");
                else if (tf.StartsWith("net7.0")) hints.Add(".NET 7");
                else if (tf.StartsWith("net6.0")) hints.Add(".NET 6");
                else if (tf.StartsWith("netcoreapp")) hints.Add($".NET Core ({tf})");
                else if (tf.StartsWith("netstandard")) hints.Add($".NET Standard ({tf})");

                if (tf.Contains("-windows")) hints.Add("Windows-specific (WPF/WinForms)");
            }
            if (content.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase)) hints.Add("WPF");
            if (content.Contains("<UseWindowsForms>true</UseWindowsForms>", StringComparison.OrdinalIgnoreCase)) hints.Add("Windows Forms");
        }

        return hints.ToList();
    }

    private async Task<JsonElement?> FetchJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            LastScanApiCallCount++;
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch { return null; }
    }

    private async Task<List<JsonElement>?> FetchJsonArrayAsync(string url, CancellationToken ct)
    {
        try
        {
            LastScanApiCallCount++;
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.ValueKind != JsonValueKind.Array) return null;
            return arr.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch { return null; }
    }

    private async Task<string?> FetchFileContent(string url, CancellationToken ct)
    {
        try
        {
            var elem = await FetchJsonAsync(url, ct);
            if (elem == null) return null;
            var content = GetString(elem, "content");
            var encoding = GetString(elem, "encoding");
            if (encoding == "base64" && !string.IsNullOrEmpty(content))
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
            return content;
        }
        catch { return null; }
    }

    private static string GetString(JsonElement? elem, string prop)
    {
        if (elem?.TryGetProperty(prop, out var val) == true && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? "";
        return "";
    }

    private static int GetInt(JsonElement? elem, string prop)
    {
        if (elem?.TryGetProperty(prop, out var val) == true && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return 0;
    }

    /// <summary>Parse verified gaps from the gap verification LLM response.</summary>
    public static List<string> ParseVerifiedGaps(string response)
    {
        var gaps = new List<string>();
        bool inVerifiedSection = false;
        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("## Verified Gaps", StringComparison.OrdinalIgnoreCase)) { inVerifiedSection = true; continue; }
            if (line.StartsWith("## ")) { inVerifiedSection = false; continue; }
            if (inVerifiedSection && line.StartsWith("- ") && line.Length > 2)
                gaps.Add(line[2..].Trim());
        }
        return gaps;
    }

    // ─── Consolidated Analysis (Cloud/Codex — single call for CodeBook + Analysis + Gap Verification) ───

    /// <summary>
    /// Build a consolidated prompt that combines CodeBook generation, strengths/gaps analysis,
    /// and gap self-verification into a single LLM call. For use with large-context cloud models
    /// (Codex, GPT-4, Claude, etc.) to reduce 3 LLM calls to 1.
    /// </summary>
    public static string BuildConsolidatedAnalysisPrompt(RepoProfile profile, IReadOnlyList<string> retrievedChunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a senior software architect performing a comprehensive analysis of a GitHub repository.");
        sb.AppendLine("Analyze the ACTUAL SOURCE CODE provided below and produce ALL sections in a single response.");
        sb.AppendLine();
        sb.AppendLine($"## Repository: {profile.Owner}/{profile.Name}");
        sb.AppendLine($"- Description: {profile.Description}");
        sb.AppendLine($"- Primary Language: {profile.PrimaryLanguage}");
        sb.AppendLine($"- Languages: {string.Join(", ", profile.Languages)}");
        sb.AppendLine($"- Dependencies ({profile.Dependencies.Count}): {string.Join(", ", profile.Dependencies.Select(d => d.Name))}");
        sb.AppendLine($"- Indexed: {profile.IndexedFileCount} files, {profile.IndexedChunkCount} chunks");
        sb.AppendLine();
        var factSection = profile.FactSheet?.ToPromptSection();
        if (!string.IsNullOrEmpty(factSection))
        {
            sb.AppendLine(factSection);
            sb.AppendLine();
        }

        sb.AppendLine("## Source Code Excerpts:");
        sb.AppendLine();
        for (int i = 0; i < retrievedChunks.Count; i++)
        {
            sb.AppendLine($"### Excerpt {i + 1}:");
            sb.AppendLine(retrievedChunks[i]);
            sb.AppendLine("---");
        }
        sb.AppendLine();

        sb.AppendLine("=== PRODUCE ALL FOUR SECTIONS BELOW ===");
        sb.AppendLine();
        sb.AppendLine("## CodeBook");
        sb.AppendLine("A concise architecture reference document (under 1500 words) covering:");
        sb.AppendLine("1. **Purpose** — What the project does");
        sb.AppendLine("2. **Architecture Overview** — Layers, modules, key patterns (MVC, CQRS, etc.)");
        sb.AppendLine("3. **Key Abstractions** — Important classes, interfaces with one-line descriptions");
        sb.AppendLine("4. **Data Flow** — How data moves through the system");
        sb.AppendLine("5. **Extension Points** — How a developer would add features");
        sb.AppendLine("6. **Build & Run** — Commands or steps to build and run");
        sb.AppendLine("7. **Notable Design Decisions**");
        sb.AppendLine("Reference actual class/function names from the code excerpts.");
        sb.AppendLine();
        sb.AppendLine("## Frameworks");
        sb.AppendLine("- framework1");
        sb.AppendLine("- framework2");
        sb.AppendLine();
        sb.AppendLine("## Strengths");
        sb.AppendLine("- strength1 (cite specific code evidence: class names, patterns, services)");
        sb.AppendLine("(list at least 5 specific strengths based on what the code actually implements)");
        sb.AppendLine();
        sb.AppendLine("## Gaps");
        sb.AppendLine("- gap1 (self-verified: explain why this is genuinely missing from the code)");
        sb.AppendLine("(list at least 5 gaps — only MISSING capabilities, not critiques of existing features)");
        sb.AppendLine();
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("- Be specific — reference real class names, service names, and patterns from the code");
        sb.AppendLine("- Strengths MUST cite specific code evidence (e.g., 'LlmService supports 9 providers')");
        sb.AppendLine("- Gaps MUST be MISSING things (no tests, no CI, no docs) — NOT critiques of existing features");
        sb.AppendLine("  Bad gap: 'The search engine assumes X which limits Y' — this critiques something that EXISTS");
        sb.AppendLine("  Good gap: 'No CI/CD pipeline configuration files found' — this identifies something ABSENT");
        sb.AppendLine("- Self-verify each gap: if the code excerpts show the feature exists, REMOVE that gap");
        sb.AppendLine("- Keep at least 5 verified gaps after self-verification");

        return sb.ToString();
    }

    /// <summary>
    /// Parse the consolidated analysis response into CodeBook, Frameworks, Strengths, and Gaps.
    /// The response contains all four sections: ## CodeBook, ## Frameworks, ## Strengths, ## Gaps.
    /// </summary>
    public static (string codeBook, List<string> frameworks, List<string> strengths, List<string> gaps) ParseConsolidatedAnalysis(string response)
    {
        var lines = response.Split('\n');
        string? currentSection = null;
        var codeBookSb = new System.Text.StringBuilder();
        var frameworks = new List<string>();
        var strengths = new List<string>();
        var gaps = new List<string>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();

            // Detect ## level headers (not ### subheadings within CodeBook)
            if (trimmed.StartsWith("## ") && !trimmed.StartsWith("### "))
            {
                if (trimmed.StartsWith("## CodeBook", StringComparison.OrdinalIgnoreCase))
                    currentSection = "codebook";
                else if (trimmed.StartsWith("## Frameworks", StringComparison.OrdinalIgnoreCase))
                    currentSection = "frameworks";
                else if (trimmed.StartsWith("## Strengths", StringComparison.OrdinalIgnoreCase))
                    currentSection = "strengths";
                else if (trimmed.StartsWith("## Gaps", StringComparison.OrdinalIgnoreCase))
                    currentSection = "gaps";
                else
                    currentSection = null;
                continue;
            }

            switch (currentSection)
            {
                case "codebook":
                    codeBookSb.AppendLine(rawLine);
                    break;
                case "frameworks":
                    if (trimmed.StartsWith("- ") && trimmed.Length > 2)
                        frameworks.Add(trimmed[2..].Trim());
                    break;
                case "strengths":
                    if (trimmed.StartsWith("- ") && trimmed.Length > 2)
                        strengths.Add(trimmed[2..].Trim());
                    break;
                case "gaps":
                    if (trimmed.StartsWith("- ") && trimmed.Length > 2)
                        gaps.Add(trimmed[2..].Trim());
                    break;
            }
        }

        return (codeBookSb.ToString().Trim(), frameworks, strengths, gaps);
    }

    // ─── Full Agentic Analysis (Codex 5.3 single call with web search) ───

    /// <summary>
    /// Build a full agentic prompt for Codex that combines code analysis AND web search for complements
    /// into a single call. Codex's native web search capability handles complement discovery autonomously.
    /// This eliminates the separate ComplementResearchService call entirely.
    /// </summary>
    public static string BuildFullAgenticPrompt(RepoProfile profile, IReadOnlyList<string> retrievedChunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a senior software architect with web search capabilities.");
        sb.AppendLine("Perform a COMPLETE repository analysis in a single pass:");
        sb.AppendLine("  1. Analyze the source code excerpts provided below");
        sb.AppendLine("  2. Search the web for real complementary open-source projects that fill identified gaps");
        sb.AppendLine();
        sb.AppendLine($"## Repository: {profile.Owner}/{profile.Name}");
        sb.AppendLine($"- Description: {profile.Description}");
        sb.AppendLine($"- Primary Language: {profile.PrimaryLanguage}");
        sb.AppendLine($"- Languages: {string.Join(", ", profile.Languages)}");
        sb.AppendLine($"- Dependencies ({profile.Dependencies.Count}): {string.Join(", ", profile.Dependencies.Select(d => d.Name))}");
        sb.AppendLine($"- Indexed: {profile.IndexedFileCount} files, {profile.IndexedChunkCount} chunks");
        sb.AppendLine();
        var factSection2 = profile.FactSheet?.ToPromptSection();
        if (!string.IsNullOrEmpty(factSection2))
        {
            sb.AppendLine(factSection2);
            sb.AppendLine();
        }

        sb.AppendLine("## Source Code Excerpts:");
        sb.AppendLine();
        for (int i = 0; i < retrievedChunks.Count; i++)
        {
            sb.AppendLine($"### Excerpt {i + 1}:");
            sb.AppendLine(retrievedChunks[i]);
            sb.AppendLine("---");
        }
        sb.AppendLine();

        sb.AppendLine("=== PRODUCE ALL FIVE SECTIONS BELOW ===");
        sb.AppendLine();
        sb.AppendLine("## CodeBook");
        sb.AppendLine("A concise architecture reference document (under 1500 words) covering:");
        sb.AppendLine("1. **Purpose** — What the project does");
        sb.AppendLine("2. **Architecture Overview** — Layers, modules, key patterns (MVC, CQRS, etc.)");
        sb.AppendLine("3. **Key Abstractions** — Important classes, interfaces with one-line descriptions");
        sb.AppendLine("4. **Data Flow** — How data moves through the system");
        sb.AppendLine("5. **Extension Points** — How a developer would add features");
        sb.AppendLine("6. **Build & Run** — Commands or steps to build and run");
        sb.AppendLine("7. **Notable Design Decisions**");
        sb.AppendLine("Reference actual class/function names from the code excerpts.");
        sb.AppendLine();
        sb.AppendLine("## Frameworks");
        sb.AppendLine("- framework1");
        sb.AppendLine("- framework2");
        sb.AppendLine();
        sb.AppendLine("## Strengths");
        sb.AppendLine("- strength1 (cite specific code evidence: class names, patterns, services)");
        sb.AppendLine("(list at least 5 specific strengths based on what the code actually implements)");
        sb.AppendLine();
        sb.AppendLine("## Gaps");
        sb.AppendLine("- gap1 (self-verified: explain why this is genuinely missing from the code)");
        sb.AppendLine("(list at least 5 gaps — only MISSING capabilities, not critiques of existing features)");
        sb.AppendLine();
        sb.AppendLine("## Complementary Projects");
        sb.AppendLine("SEARCH THE WEB for real open-source projects that address each gap above.");
        sb.AppendLine("For EACH complement, provide:");
        sb.AppendLine("- **Name:** project-name");
        sb.AppendLine("- **URL:** https://github.com/owner/repo (MUST be a real, verifiable URL)");
        sb.AppendLine("- **Stars:** approximate star count from your web search");
        sb.AppendLine("- **License:** MIT/Apache-2.0/etc.");
        sb.AppendLine("- **Purpose:** one sentence description");
        sb.AppendLine("- **What it adds:** what it specifically adds to this repo");
        sb.AppendLine("- **Category:** Testing|Performance|Security|Documentation|Monitoring|DevOps|UI|DataAccess|Other");
        sb.AppendLine("Find at least 5 real projects. Do NOT hallucinate URLs — search the web to verify each one.");
        sb.AppendLine("Ensure category diversity — pick projects from different categories.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("- Be specific — reference real class names, service names, and patterns from the code");
        sb.AppendLine("- Strengths MUST cite specific code evidence");
        sb.AppendLine("- Gaps MUST be MISSING things — NOT critiques of existing features");
        sb.AppendLine("- Self-verify each gap: if the code excerpts show the feature exists, REMOVE that gap");
        sb.AppendLine("- Complementary projects MUST be real — use your web search to find and verify them");
        sb.AppendLine("- Keep at least 5 verified gaps and at least 5 complementary projects");

        return sb.ToString();
    }

    /// <summary>
    /// Parse the full agentic analysis response into CodeBook, Frameworks, Strengths, Gaps, AND Complements.
    /// The response contains five sections: ## CodeBook, ## Frameworks, ## Strengths, ## Gaps, ## Complementary Projects.
    /// </summary>
    public static (string codeBook, List<string> frameworks, List<string> strengths, List<string> gaps, List<ComplementProject> complements) ParseFullAgenticAnalysis(string response)
    {
        var lines = response.Split('\n');
        string? currentSection = null;
        var codeBookSb = new System.Text.StringBuilder();
        var frameworks = new List<string>();
        var strengths = new List<string>();
        var gaps = new List<string>();
        var complements = new List<ComplementProject>();

        // Temporary storage for building complement entries
        ComplementProject? currentComplement = null;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();

            // Detect ## level headers (not ### subheadings within CodeBook)
            if (trimmed.StartsWith("## ") && !trimmed.StartsWith("### "))
            {
                // Flush any in-progress complement
                if (currentSection == "complements" && currentComplement != null && !string.IsNullOrEmpty(currentComplement.Name))
                    complements.Add(currentComplement);
                currentComplement = null;

                if (trimmed.StartsWith("## CodeBook", StringComparison.OrdinalIgnoreCase))
                    currentSection = "codebook";
                else if (trimmed.StartsWith("## Frameworks", StringComparison.OrdinalIgnoreCase))
                    currentSection = "frameworks";
                else if (trimmed.StartsWith("## Strengths", StringComparison.OrdinalIgnoreCase))
                    currentSection = "strengths";
                else if (trimmed.StartsWith("## Gaps", StringComparison.OrdinalIgnoreCase) &&
                         !trimmed.Contains("Complement", StringComparison.OrdinalIgnoreCase))
                    currentSection = "gaps";
                else if (trimmed.Contains("Complement", StringComparison.OrdinalIgnoreCase))
                    currentSection = "complements";
                else
                    currentSection = null;
                continue;
            }

            switch (currentSection)
            {
                case "codebook":
                    codeBookSb.AppendLine(rawLine);
                    break;
                case "frameworks":
                    if (trimmed.StartsWith("- ") && trimmed.Length > 2)
                        frameworks.Add(trimmed[2..].Trim());
                    break;
                case "strengths":
                    if (trimmed.StartsWith("- ") && trimmed.Length > 2)
                        strengths.Add(trimmed[2..].Trim());
                    break;
                case "gaps":
                    if (trimmed.StartsWith("- ") && trimmed.Length > 2)
                        gaps.Add(trimmed[2..].Trim());
                    break;
                case "complements":
                    ParseComplementLine(trimmed, ref currentComplement, complements);
                    break;
            }
        }

        // Flush last complement
        if (currentComplement != null && !string.IsNullOrEmpty(currentComplement.Name))
            complements.Add(currentComplement);

        return (codeBookSb.ToString().Trim(), frameworks, strengths, gaps, complements);
    }

    /// <summary>Parse a single line within the Complementary Projects section.</summary>
    private static void ParseComplementLine(string trimmed, ref ComplementProject? current, List<ComplementProject> complements)
    {
        // Numbered entries like "### 1. ProjectName" or "### ProjectName" start a new complement
        if (trimmed.StartsWith("### ") || (trimmed.StartsWith("**") && trimmed.Contains("Name")))
        {
            if (current != null && !string.IsNullOrEmpty(current.Name))
                complements.Add(current);
            current = new ComplementProject();
            // Try to extract name from "### 1. Name" pattern
            var name = trimmed.TrimStart('#', ' ', '*');
            if (name.Length > 0 && char.IsDigit(name[0]))
                name = name.Contains('.') ? name[(name.IndexOf('.') + 1)..].Trim() : name;
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                current.Name = name;
            return;
        }

        if (current == null) return;

        // Parse "- **Key:** Value" or "- Key: Value" patterns
        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
        {
            var content = trimmed[2..].Trim().TrimStart('*').Trim();
            // Remove markdown bold markers
            content = content.Replace("**", "");

            if (content.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                current.Name = content[5..].Trim();
            else if (content.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
                current.Url = content[4..].Trim();
            else if (content.StartsWith("Stars:", StringComparison.OrdinalIgnoreCase))
            {
                // Try to parse star count for enrichment but store in purpose if no other field
                var starText = content[6..].Trim();
                if (!string.IsNullOrEmpty(current.Purpose))
                    current.Purpose += $" ({starText} stars)";
            }
            else if (content.StartsWith("License:", StringComparison.OrdinalIgnoreCase))
                current.License = content[8..].Trim();
            else if (content.StartsWith("Purpose:", StringComparison.OrdinalIgnoreCase))
                current.Purpose = content[8..].Trim();
            else if (content.StartsWith("What it adds:", StringComparison.OrdinalIgnoreCase))
                current.WhatItAdds = content[13..].Trim();
            else if (content.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
                current.Category = content[9..].Trim();
            else if (content.StartsWith("Maturity:", StringComparison.OrdinalIgnoreCase))
                current.Maturity = content[9..].Trim();
        }
    }

    // ─── JSON-structured prompts for Ollama (smaller models benefit from format enforcement) ───

    /// <summary>
    /// Build a complement evaluation prompt that requests JSON output.
    /// Used with <see cref="LlmService.GenerateJsonAsync"/> to enforce structured output from Ollama.
    /// Cloud models also produce cleaner output with explicit JSON schema requests.
    /// </summary>
    public static string BuildJsonComplementPrompt(RepoProfile profile, IReadOnlyList<(string topic, List<(string url, string description)> entries)> enrichedResults, int minimumComplements)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Analyze the GitHub repo **{profile.Owner}/{profile.Name}** ({profile.PrimaryLanguage}).");
        sb.AppendLine($"Purpose: {profile.Description}");
        sb.AppendLine();
        sb.AppendLine("Below are potential complementary projects found via web search.");
        sb.AppendLine("Select the BEST complementary projects that fill different gaps/needs.");
        sb.AppendLine("IMPORTANT: Use ONLY projects from the URLs provided. Do NOT invent project names.");
        sb.AppendLine("Ensure DIVERSITY — pick projects from different categories (Testing, Security, DevOps, etc.).");
        sb.AppendLine();

        foreach (var (topic, entries) in enrichedResults)
        {
            sb.AppendLine($"### Topic: {topic}");
            foreach (var (url, desc) in entries)
            {
                if (!string.IsNullOrEmpty(desc))
                    sb.AppendLine($"  - {url} — {desc}");
                else
                    sb.AppendLine($"  - {url}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Return a JSON object with exactly this structure (at least {minimumComplements} complements):");
        sb.AppendLine(@"{
  ""complements"": [
    {
      ""name"": ""project-name (from URL path)"",
      ""url"": ""https://github.com/owner/repo"",
      ""purpose"": ""one sentence description"",
      ""what_it_adds"": ""what it specifically adds to the target repo"",
      ""category"": ""Testing|Performance|Security|Documentation|Monitoring|DevOps|UI|DataAccess|Other"",
      ""license"": ""MIT|Apache-2.0|Unknown"",
      ""maturity"": ""Mature|Growing|Early|Unknown""
    }
  ]
}");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Pick the BEST option per topic. If multiple topics yield the same project, pick alternatives.");
        sb.AppendLine("- Ensure category diversity — do NOT suggest multiple projects in the same category.");
        sb.AppendLine("- Derive project names from URL paths (github.com/owner/repo → repo).");
        sb.AppendLine("- Return ONLY valid JSON, no additional text.");

        return sb.ToString();
    }

    /// <summary>
    /// Parse complement projects from JSON output (produced by GenerateJsonAsync).
    /// Falls back to empty list if JSON parsing fails.
    /// </summary>
    public static List<ComplementProject> ParseJsonComplements(string json)
    {
        try
        {
            // Handle potential markdown code block wrapping
            var cleaned = json.Trim();
            if (cleaned.StartsWith("```")) cleaned = cleaned.Split('\n', 2).Length > 1 ? cleaned.Split('\n', 2)[1] : cleaned;
            if (cleaned.EndsWith("```")) cleaned = cleaned[..cleaned.LastIndexOf("```")];
            cleaned = cleaned.Trim();

            using var doc = System.Text.Json.JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            if (!root.TryGetProperty("complements", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return new List<ComplementProject>();

            var results = new List<ComplementProject>();
            foreach (var item in arr.EnumerateArray())
            {
                var c = new ComplementProject
                {
                    Name = GetJsonString(item, "name"),
                    Url = GetJsonString(item, "url"),
                    Purpose = GetJsonString(item, "purpose"),
                    WhatItAdds = GetJsonString(item, "what_it_adds"),
                    Category = GetJsonString(item, "category"),
                    License = GetJsonString(item, "license"),
                    Maturity = GetJsonString(item, "maturity")
                };
                if (!string.IsNullOrEmpty(c.Name))
                    results.Add(c);
            }
            return results;
        }
        catch
        {
            return new List<ComplementProject>();
        }
    }

    private static string GetJsonString(System.Text.Json.JsonElement elem, string prop)
    {
        if (elem.TryGetProperty(prop, out var val) && val.ValueKind == System.Text.Json.JsonValueKind.String)
            return val.GetString() ?? "";
        return "";
    }
}
