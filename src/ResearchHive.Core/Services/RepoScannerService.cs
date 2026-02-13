using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Scans GitHub repos via REST API to build a RepoProfile: metadata, README, dependencies,
/// then uses LLM to analyze strengths, gaps, and frameworks.
/// </summary>
public class RepoScannerService
{
    private readonly HttpClient _http;
    private readonly LlmService _llmService;
    private readonly AppSettings _settings;

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

    public async Task<RepoProfile> ScanAsync(string repoUrl, CancellationToken ct = default)
    {
        var (owner, repo) = ParseRepoUrl(repoUrl);
        var profile = new RepoProfile { RepoUrl = repoUrl, Owner = owner, Name = repo };

        // 1. Fetch repo metadata
        var repoJson = await FetchJsonAsync($"https://api.github.com/repos/{owner}/{repo}", ct);
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

        // 2. Fetch languages
        var langsJson = await FetchJsonAsync($"https://api.github.com/repos/{owner}/{repo}/languages", ct);
        if (langsJson != null)
            profile.Languages = langsJson.Value.EnumerateObject().Select(p => p.Name).ToList();

        // 3. Fetch README
        var readmeJson = await FetchJsonAsync($"https://api.github.com/repos/{owner}/{repo}/readme", ct);
        if (readmeJson != null)
        {
            var content = GetString(readmeJson, "content");
            var encoding = GetString(readmeJson, "encoding");
            if (encoding == "base64" && !string.IsNullOrEmpty(content))
                profile.ReadmeContent = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
        }

        // 4. Detect and fetch dependency files
        var rootContents = await FetchJsonArrayAsync($"https://api.github.com/repos/{owner}/{repo}/contents/", ct);
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
            // Check src/ folder for .csproj files
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
                            if (nm.EndsWith(".csproj"))
                            {
                                var fc = await FetchFileContent($"https://api.github.com/repos/{owner}/{repo}/contents/src/{nm}", ct);
                                if (fc != null) depFiles[$"src/{nm}"] = fc;
                            }
                        }
                    }
                }
            }
        }

        profile.Dependencies = ParseDependencies(depFiles);

        // 5. LLM analysis — strengths, gaps, frameworks
        var analysisPrompt = BuildAnalysisPrompt(profile, depFiles);
        var analysis = await _llmService.GenerateAsync(analysisPrompt,
            "You are a software architecture analyst. Respond in the exact format requested. Be specific and actionable.", ct: ct);
        ParseAnalysis(analysis, profile);

        return profile;
    }

    private string BuildAnalysisPrompt(RepoProfile profile, Dictionary<string, string> depFiles)
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
            sb.AppendLine("## README (truncated):");
            sb.AppendLine(profile.ReadmeContent.Length > 3000 ? profile.ReadmeContent[..3000] + "\n[truncated]" : profile.ReadmeContent);
            sb.AppendLine();
        }
        foreach (var (file, content) in depFiles)
        {
            sb.AppendLine($"## {file} (truncated):");
            sb.AppendLine(content.Length > 1500 ? content[..1500] + "\n[truncated]" : content);
            sb.AppendLine();
        }
        sb.AppendLine("Respond with EXACTLY this format:");
        sb.AppendLine("## Frameworks");
        sb.AppendLine("- framework1");
        sb.AppendLine("- framework2");
        sb.AppendLine("## Strengths");
        sb.AppendLine("- strength1");
        sb.AppendLine("- strength2");
        sb.AppendLine("(list at least 5 specific strengths)");
        sb.AppendLine("## Gaps");
        sb.AppendLine("- gap1");
        sb.AppendLine("- gap2");
        sb.AppendLine("(list at least 5 specific weaknesses, missing features, or improvement opportunities)");
        return sb.ToString();
    }

    private static void ParseAnalysis(string analysis, RepoProfile profile)
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
                    case "frameworks": profile.Frameworks.Add(text); break;
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

    private async Task<JsonElement?> FetchJsonAsync(string url, CancellationToken ct)
    {
        try
        {
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
}
