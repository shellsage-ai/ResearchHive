using ResearchHive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Builds a deterministic, zero-LLM fact sheet from code analysis.
/// Runs between metadata scan + indexing (Phase 2) and LLM analysis (Phase 3+).
/// The fact sheet is injected into every LLM prompt to prevent hallucinations.
///
/// Layers:
///   1. Package manifest parsing (already done by RepoScannerService)
///   2. Used-vs-installed detection (cross-reference deps with using/import statements)
///   3. Capability fingerprinting via regex patterns
///   4. Diagnostic file/directory existence checks
///   5. App type + database + test framework inference
/// </summary>
public class RepoFactSheetBuilder
{
    private readonly RepoCloneService _cloneService;
    private readonly ILogger<RepoFactSheetBuilder>? _logger;

    public RepoFactSheetBuilder(RepoCloneService cloneService, ILogger<RepoFactSheetBuilder>? logger = null)
    {
        _cloneService = cloneService;
        _logger = logger;
    }

    /// <summary>
    /// Build a complete fact sheet from the RepoProfile (metadata + manifests) and the cloned source files.
    /// This is pure deterministic analysis — no LLM calls, no network requests.
    /// </summary>
    public RepoFactSheet Build(RepoProfile profile, string? clonePath)
    {
        var sheet = new RepoFactSheet();

        // Discover all source files from the clone (if available)
        var sourceFiles = new List<(string AbsolutePath, string RelativePath)>();
        if (!string.IsNullOrEmpty(clonePath) && Directory.Exists(clonePath))
        {
            sourceFiles = _cloneService.DiscoverFiles(clonePath);
            sheet.TotalSourceFiles = sourceFiles.Count;
        }

        // Build a full-text index of all source content for pattern scanning
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (absPath, relPath) in sourceFiles)
        {
            try
            {
                var content = File.ReadAllText(absPath);
                if (!string.IsNullOrWhiteSpace(content))
                    fileContents[relPath] = content;
            }
            catch { /* skip unreadable files */ }
        }

        // Collect all using/import statements across all files
        var allImports = ExtractImports(fileContents, profile.PrimaryLanguage);

        // ── Layer 1-2: Used vs. Installed package detection ──
        ClassifyPackages(profile, allImports, fileContents, sheet);

        // ── Layer 3: Capability fingerprinting via regex ──
        DetectCapabilities(fileContents, profile.PrimaryLanguage, sheet);

        // ── Layer 4: Diagnostic file/directory existence ──
        if (!string.IsNullOrEmpty(clonePath) && Directory.Exists(clonePath))
            CheckDiagnosticFiles(clonePath, sheet);

        // ── Layer 5: App type, database, test framework, ecosystem ──
        InferAppType(profile, fileContents, sheet);
        InferDatabaseTechnology(profile, fileContents, sheet);
        InferTestFramework(profile, fileContents, sheet);
        InferEcosystem(profile, sheet);

        _logger?.LogInformation(
            "FactSheet built: {Active} active packages, {Phantom} phantom, {Proven} capabilities proven, " +
            "{Absent} absent, {Tests} test methods, AppType={AppType}",
            sheet.ActivePackages.Count, sheet.PhantomPackages.Count,
            sheet.ProvenCapabilities.Count, sheet.ConfirmedAbsent.Count,
            sheet.TestMethodCount, sheet.AppType);

        return sheet;
    }

    // ═══════════════════════════════════════════════════════
    //  LAYER 1-2: Package Classification (Active vs Phantom)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Cross-reference every dependency with actual code usage.
    /// A package is "active" if its namespace/module is imported somewhere,
    /// or if known code patterns for that package appear in source.
    /// </summary>
    private static void ClassifyPackages(
        RepoProfile profile,
        HashSet<string> allImports,
        Dictionary<string, string> fileContents,
        RepoFactSheet sheet)
    {
        // Map of package names to their usage detection rules
        // Key: NuGet/npm/pip package name
        // Value: (importPatterns, codePatterns, friendlyLabel)
        var rules = BuildPackageUsageRules(profile.PrimaryLanguage);
        var allText = string.Join("\n", fileContents.Values);

        foreach (var dep in profile.Dependencies)
        {
            var name = dep.Name;
            bool foundUsage = false;
            string evidence = "";

            // Check custom rules first
            if (rules.TryGetValue(name, out var rule))
            {
                // Build tools (e.g., coverlet) are always active by definition
                if (rule.AlwaysActive)
                {
                    foundUsage = true;
                    evidence = $"{rule.FriendlyLabel} — build/tooling package (always active)";
                }

                // Check import patterns
                if (!foundUsage)
                {
                    foreach (var importNs in rule.ImportPatterns)
                    {
                        if (allImports.Any(i => i.StartsWith(importNs, StringComparison.OrdinalIgnoreCase)))
                        {
                            var matchingFile = FindFileWithPattern(fileContents, importNs);
                            evidence = $"using {importNs} found in {matchingFile}";
                            foundUsage = true;
                            break;
                        }
                    }
                }

                // Check code patterns (regex) if no import match
                if (!foundUsage)
                {
                    foreach (var pattern in rule.CodePatterns)
                    {
                        var matchFile = FindFileWithRegex(fileContents, pattern);
                        if (matchFile != null)
                        {
                            evidence = $"{pattern} found in {matchFile}";
                            foundUsage = true;
                            break;
                        }
                    }
                }

                // Use friendly label
                if (foundUsage)
                    evidence = $"{rule.FriendlyLabel} — {evidence}";
            }
            else
            {
                // Generic detection: check if the package namespace appears in imports
                // For NuGet, namespace usually matches package name
                if (allImports.Any(i => i.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
                                        i.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    foundUsage = true;
                    evidence = $"namespace {name} found in imports";
                }
                // Also check if the package name appears in code (e.g., attribute usage)
                else if (allText.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    // Exclude false positives from manifest files themselves
                    var codeOnlyText = string.Join("\n",
                        fileContents.Where(kv => !kv.Key.EndsWith(".csproj") &&
                                                  !kv.Key.EndsWith("package.json") &&
                                                  !kv.Key.EndsWith("requirements.txt"))
                                    .Select(kv => kv.Value));
                    if (codeOnlyText.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        foundUsage = true;
                        evidence = $"referenced in source code";
                    }
                }
            }

            var pkg = new PackageEvidence
            {
                PackageName = dep.Name,
                Version = dep.Version,
                Evidence = evidence
            };

            if (foundUsage)
                sheet.ActivePackages.Add(pkg);
            else
                sheet.PhantomPackages.Add(pkg);
        }
    }

    // ═══════════════════════════════════════════════════
    //  LAYER 3: Capability Fingerprinting via Regex
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Scan all source files for patterns that prove specific capabilities exist 
    /// (or confirm they're absent). This catches things the LLM otherwise hallucinates about.
    /// </summary>
    private static void DetectCapabilities(
        Dictionary<string, string> fileContents,
        string primaryLanguage,
        RepoFactSheet sheet)
    {
        var fingerprints = GetCapabilityFingerprints(primaryLanguage);
        var allText = string.Join("\n", fileContents.Values);

        foreach (var (capability, patterns, absenceLabel) in fingerprints)
        {
            bool found = false;
            string evidence = "";

            foreach (var pattern in patterns)
            {
                try
                {
                    var match = Regex.Match(allText, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
                    if (match.Success)
                    {
                        // Find which file contains this match
                        var file = FindFileWithRegex(fileContents, pattern);
                        evidence = $"{file ?? "source"} — matched pattern: {TruncatePattern(pattern)}";
                        found = true;
                        break;
                    }
                }
                catch (RegexMatchTimeoutException) { /* skip slow patterns */ }
            }

            if (found)
            {
                sheet.ProvenCapabilities.Add(new CapabilityFingerprint
                {
                    Capability = capability,
                    Evidence = evidence
                });
            }
            else if (!string.IsNullOrEmpty(absenceLabel))
            {
                sheet.ConfirmedAbsent.Add(new CapabilityFingerprint
                {
                    Capability = capability,
                    Evidence = absenceLabel
                });
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  LAYER 4: Diagnostic File/Directory Checks
    // ═══════════════════════════════════════════════════

    private static void CheckDiagnosticFiles(string clonePath, RepoFactSheet sheet)
    {
        var diagnostics = new (string path, string label)[]
        {
            (".github/workflows", "CI/CD workflows (.github/workflows/)"),
            (".github/dependabot.yml", "Dependabot config"),
            (".github/renovate.json", "Renovate config"),
            ("Dockerfile", "Dockerfile"),
            ("docker-compose.yml", "Docker Compose"),
            ("docker-compose.yaml", "Docker Compose"),
            (".dockerignore", ".dockerignore"),
            ("LICENSE", "License file"),
            ("LICENSE.md", "License file"),
            (".editorconfig", ".editorconfig"),
            (".eslintrc.json", "ESLint config"),
            (".eslintrc.js", "ESLint config"),
            ("tsconfig.json", "TypeScript config"),
            ("jest.config.js", "Jest config"),
            ("jest.config.ts", "Jest config"),
            ("renovate.json", "Renovate config"),
        };

        var found = new HashSet<string>();
        foreach (var (path, label) in diagnostics)
        {
            if (found.Contains(label)) continue;

            var fullPath = Path.Combine(clonePath, path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                sheet.DiagnosticFilesPresent.Add(label);
                found.Add(label);
            }
        }

        // Report key missing files (only the most important ones)
        var keyMissing = new (string label, string[] paths)[]
        {
            ("CI/CD workflows (.github/workflows/)", new[] { ".github/workflows" }),
            ("Dependabot/Renovate config", new[] { ".github/dependabot.yml", "renovate.json", ".github/renovate.json" }),
            ("Dockerfile", new[] { "Dockerfile" }),
            ("License file", new[] { "LICENSE", "LICENSE.md", "LICENSE.txt" }),
        };

        foreach (var (label, paths) in keyMissing)
        {
            if (!found.Contains(label) && !paths.Any(p => File.Exists(Path.Combine(clonePath, p)) || Directory.Exists(Path.Combine(clonePath, p))))
                sheet.DiagnosticFilesMissing.Add(label);
        }
    }

    // ═══════════════════════════════════════════════════
    //  LAYER 5: Type Inference (App type, DB, Tests, Ecosystem)
    // ═══════════════════════════════════════════════════

    private static void InferAppType(RepoProfile profile, Dictionary<string, string> fileContents, RepoFactSheet sheet)
    {
        var allText = string.Join("\n", fileContents.Values);

        // Check for WPF markers
        bool hasWpf = profile.Dependencies.Any(d => d.Name == "CommunityToolkit.Mvvm") ||
                      fileContents.Values.Any(c => c.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase));
        bool hasWebApp = Regex.IsMatch(allText, @"WebApplication\.Create|app\.MapGet|app\.UseRouting|Startup\.Configure", RegexOptions.IgnoreCase);
        bool hasConsole = !hasWpf && !hasWebApp && fileContents.Keys.Any(f => f.Contains("Program.cs")) &&
                          allText.Contains("static void Main", StringComparison.OrdinalIgnoreCase);

        if (hasWpf)
            sheet.AppType = "WPF desktop application";
        else if (hasWebApp)
            sheet.AppType = "ASP.NET Core web application";
        else if (hasConsole)
            sheet.AppType = "Console application";
        else if (profile.Dependencies.Any(d => d.Name.Contains("react", StringComparison.OrdinalIgnoreCase)))
            sheet.AppType = "React web application";
        else if (profile.Dependencies.Any(d => d.Name.Contains("Django", StringComparison.OrdinalIgnoreCase)))
            sheet.AppType = "Django web application";
        else if (!string.IsNullOrEmpty(profile.PrimaryLanguage))
            sheet.AppType = $"{profile.PrimaryLanguage} application";
    }

    private static void InferDatabaseTechnology(RepoProfile profile, Dictionary<string, string> fileContents, RepoFactSheet sheet)
    {
        var allText = string.Join("\n", fileContents.Values);

        bool hasEfCore = profile.Dependencies.Any(d => d.Name.StartsWith("Microsoft.EntityFrameworkCore")) &&
                         Regex.IsMatch(allText, @"class\s+\w+\s*:\s*DbContext|\.UseSqlite|\.UseSqlServer", RegexOptions.IgnoreCase);
        bool hasRawSqlite = profile.Dependencies.Any(d => d.Name is "Microsoft.Data.Sqlite" or "Microsoft.Data.Sqlite.Core") &&
                            Regex.IsMatch(allText, @"SqliteConnection|SqliteCommand", RegexOptions.IgnoreCase);
        bool hasDapper = profile.Dependencies.Any(d => d.Name == "Dapper") &&
                         allText.Contains(".QueryAsync", StringComparison.OrdinalIgnoreCase);

        if (hasEfCore && hasRawSqlite)
            sheet.DatabaseTechnology = "Entity Framework Core + raw SQLite (Microsoft.Data.Sqlite)";
        else if (hasEfCore)
            sheet.DatabaseTechnology = "Entity Framework Core";
        else if (hasRawSqlite)
            sheet.DatabaseTechnology = "Raw SQLite via Microsoft.Data.Sqlite (hand-written SQL, NOT EF Core)";
        else if (hasDapper)
            sheet.DatabaseTechnology = "Dapper micro-ORM";
        else if (profile.Dependencies.Any(d => d.Name.Contains("MongoDB")))
            sheet.DatabaseTechnology = "MongoDB";
        else if (profile.Dependencies.Any(d => d.Name.Contains("Npgsql")))
            sheet.DatabaseTechnology = "PostgreSQL (Npgsql)";
    }

    private static void InferTestFramework(RepoProfile profile, Dictionary<string, string> fileContents, RepoFactSheet sheet)
    {
        var deps = profile.Dependencies.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Determine test framework
        if (deps.Contains("xunit") || deps.Contains("xunit.runner.visualstudio"))
            sheet.TestFramework = "xUnit";
        else if (deps.Contains("NUnit") || deps.Contains("NUnit3TestAdapter"))
            sheet.TestFramework = "NUnit";
        else if (deps.Contains("MSTest.TestFramework") || deps.Contains("Microsoft.VisualStudio.TestTools.UnitTesting"))
            sheet.TestFramework = "MSTest";
        else if (deps.Contains("jest") || deps.Contains("@jest/core"))
            sheet.TestFramework = "Jest";
        else if (deps.Contains("mocha"))
            sheet.TestFramework = "Mocha";
        else if (deps.Contains("pytest"))
            sheet.TestFramework = "Pytest";

        // Count test methods by scanning for framework-specific attributes/functions
        int testCount = 0;
        int testFileCount = 0;

        foreach (var (path, content) in fileContents)
        {
            bool isTestFile = false;
            int fileTests = 0;

            // xUnit/NUnit/MSTest
            var factMatches = Regex.Matches(content, @"\[(Fact|Theory|Test|TestMethod|TestCase)\]", RegexOptions.IgnoreCase);
            fileTests += factMatches.Count;

            // Jest/Mocha: it("...", () => or test("...", () =>
            if (sheet.TestFramework is "Jest" or "Mocha")
            {
                var jsTestMatches = Regex.Matches(content, @"\b(it|test)\s*\(", RegexOptions.IgnoreCase);
                fileTests += jsTestMatches.Count;
            }

            // Pytest: def test_
            if (sheet.TestFramework == "Pytest")
            {
                var pyTestMatches = Regex.Matches(content, @"^def\s+test_", RegexOptions.Multiline);
                fileTests += pyTestMatches.Count;
            }

            if (fileTests > 0)
            {
                isTestFile = true;
                testCount += fileTests;
            }
            if (isTestFile) testFileCount++;
        }

        sheet.TestMethodCount = testCount;
        sheet.TestFileCount = testFileCount;
    }

    private static void InferEcosystem(RepoProfile profile, RepoFactSheet sheet)
    {
        sheet.Ecosystem = profile.PrimaryLanguage?.ToLowerInvariant() switch
        {
            "c#" => ".NET/C#",
            "f#" => ".NET/F#",
            "visual basic" or "visual basic .net" => ".NET/VB",
            "java" or "kotlin" => "JVM",
            "javascript" or "typescript" => "Node.js/JavaScript",
            "python" => "Python",
            "rust" => "Rust/Cargo",
            "go" => "Go",
            "ruby" => "Ruby",
            "php" => "PHP",
            "swift" => "Swift/Apple",
            "dart" => "Dart/Flutter",
            _ => profile.PrimaryLanguage ?? ""
        };
    }

    // ═══════════════════════════════════════════════════
    //  Import Extraction
    // ═══════════════════════════════════════════════════

    /// <summary>Extract all import/using statements across all source files.</summary>
    private static HashSet<string> ExtractImports(Dictionary<string, string> fileContents, string primaryLanguage)
    {
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lang = primaryLanguage?.ToLowerInvariant() ?? "";

        foreach (var (path, content) in fileContents)
        {
            var lines = content.Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // C#: using System.Text; or using static X;
                if (lang is "c#" or "" && line.StartsWith("using ") && line.EndsWith(";"))
                {
                    var ns = line[6..^1].Trim();
                    if (ns.StartsWith("static ")) ns = ns[7..].Trim();
                    if (!string.IsNullOrEmpty(ns)) imports.Add(ns);
                }
                // JavaScript/TypeScript: import ... from '...'; or require('...')
                else if (lang is "javascript" or "typescript" or "")
                {
                    var importMatch = Regex.Match(line, @"from\s+['""]([^'""]+)['""]");
                    if (importMatch.Success) imports.Add(importMatch.Groups[1].Value);

                    var requireMatch = Regex.Match(line, @"require\s*\(\s*['""]([^'""]+)['""]\s*\)");
                    if (requireMatch.Success) imports.Add(requireMatch.Groups[1].Value);
                }
                // Python: import X or from X import Y
                else if (lang == "python")
                {
                    var pyImport = Regex.Match(line, @"^(?:from\s+(\S+)|import\s+(\S+))");
                    if (pyImport.Success)
                    {
                        var mod = pyImport.Groups[1].Success ? pyImport.Groups[1].Value : pyImport.Groups[2].Value;
                        imports.Add(mod);
                    }
                }
                // Java/Kotlin: import com.example.Foo;
                else if (lang is "java" or "kotlin" && line.StartsWith("import "))
                {
                    var ns = line[7..].TrimEnd(';').Trim();
                    if (!string.IsNullOrEmpty(ns)) imports.Add(ns);
                }
                // Rust: use crate::foo; or use std::collections;
                else if (lang == "rust" && line.StartsWith("use "))
                {
                    var ns = line[4..].TrimEnd(';').Trim();
                    if (!string.IsNullOrEmpty(ns)) imports.Add(ns);
                }
                // Go: import "fmt"
                else if (lang == "go")
                {
                    var goImport = Regex.Match(line, @"import\s+[""(]([^""(]+)");
                    if (goImport.Success) imports.Add(goImport.Groups[1].Value);
                }
            }
        }

        return imports;
    }

    // ═══════════════════════════════════════════════════
    //  Package Usage Rules
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Map package names to their expected import namespaces and code patterns.
    /// This is the key lookup table that prevents phantom dependency classification.
    /// </summary>
    private static Dictionary<string, PackageUsageRule> BuildPackageUsageRules(string primaryLanguage)
    {
        var rules = new Dictionary<string, PackageUsageRule>(StringComparer.OrdinalIgnoreCase);

        // ─── .NET packages ───
        rules["Microsoft.EntityFrameworkCore"] = new(
            new[] { "Microsoft.EntityFrameworkCore" },
            new[] { @":\s*DbContext", @"\.UseSqlite\(", @"\.UseSqlServer\(" },
            "Entity Framework Core");

        rules["Microsoft.Data.Sqlite"] = new(
            new[] { "Microsoft.Data.Sqlite" },
            new[] { @"SqliteConnection", @"SqliteCommand" },
            "Raw SQLite (Microsoft.Data.Sqlite)");
        rules["Microsoft.Data.Sqlite.Core"] = rules["Microsoft.Data.Sqlite"];

        rules["Microsoft.Extensions.Http"] = new(
            new[] { "Microsoft.Extensions.Http" },
            new[] { @"IHttpClientFactory", @"\.CreateClient\(" },
            "HttpClientFactory");

        rules["Microsoft.Playwright"] = new(
            new[] { "Microsoft.Playwright" },
            new[] { @"IPlaywright", @"IBrowser", @"LaunchAsync" },
            "Playwright (browser automation)");

        rules["Moq"] = new(
            new[] { "Moq" },
            new[] { @"new\s+Mock<", @"\.Setup\(", @"\.Verify\(" },
            "Moq (mocking)");

        rules["xunit"] = new(
            new[] { "Xunit" },
            new[] { @"\[Fact\]", @"\[Theory\]" },
            "xUnit (testing)");
        rules["xunit.runner.visualstudio"] = rules["xunit"];

        rules["FluentAssertions"] = new(
            new[] { "FluentAssertions" },
            new[] { @"\.Should\(\)", @"\.BeEquivalentTo\(" },
            "FluentAssertions");

        rules["coverlet.collector"] = new(
            Array.Empty<string>(),
            Array.Empty<string>(),
            "Coverlet (code coverage collector — no code usage expected)");
        // Coverlet is a build tool — presence in csproj = active
        rules["coverlet.collector"] = new(
            new[] { "coverlet" },
            new[] { @"coverlet" },
            "Coverlet (code coverage)") { AlwaysActive = true };

        rules["CommunityToolkit.Mvvm"] = new(
            new[] { "CommunityToolkit.Mvvm" },
            new[] { @"\[ObservableProperty\]", @"\[RelayCommand\]", @"ObservableObject" },
            "CommunityToolkit.Mvvm (MVVM)");

        rules["Microsoft.Extensions.DependencyInjection"] = new(
            new[] { "Microsoft.Extensions.DependencyInjection" },
            new[] { @"AddSingleton", @"AddTransient", @"AddScoped", @"IServiceCollection" },
            "Microsoft DI");

        rules["PdfPig"] = new(
            new[] { "UglyToad.PdfPig" },
            new[] { @"PdfDocument\.Open" },
            "PdfPig (PDF extraction)");

        rules["Polly"] = new(
            new[] { "Polly" },
            new[] { @"Policy\.", @"RetryAsync", @"CircuitBreakerAsync" },
            "Polly (resilience)");

        rules["Dapper"] = new(
            new[] { "Dapper" },
            new[] { @"\.QueryAsync", @"\.ExecuteAsync" },
            "Dapper (micro-ORM)");

        rules["Serilog"] = new(
            new[] { "Serilog" },
            new[] { @"Log\.(Information|Warning|Error|Debug)", @"LoggerConfiguration" },
            "Serilog (structured logging)");

        rules["AutoMapper"] = new(
            new[] { "AutoMapper" },
            new[] { @"IMapper", @"CreateMap\<" },
            "AutoMapper");

        rules["MediatR"] = new(
            new[] { "MediatR" },
            new[] { @"IMediator", @"IRequest<", @"IRequestHandler" },
            "MediatR (mediator/CQRS)");

        rules["Newtonsoft.Json"] = new(
            new[] { "Newtonsoft.Json" },
            new[] { @"JsonConvert\.", @"JObject\.", @"JToken\." },
            "Newtonsoft.Json");

        rules["System.Security.Cryptography.ProtectedData"] = new(
            new[] { "System.Security.Cryptography" },
            new[] { @"ProtectedData\.Protect", @"ProtectedData\.Unprotect" },
            "DPAPI (Windows data protection)");

        rules["Microsoft.Extensions.Logging"] = new(
            new[] { "Microsoft.Extensions.Logging" },
            new[] { @"ILogger<", @"LogInformation\(", @"LogWarning\(" },
            "Microsoft.Extensions.Logging");
        rules["Microsoft.Extensions.Logging.Abstractions"] = rules["Microsoft.Extensions.Logging"];

        // ─── JavaScript/TypeScript packages ───
        rules["react"] = new(new[] { "react" }, new[] { @"import.*from\s+['""]react['""]", @"React\.createElement" }, "React");
        rules["react-dom"] = new(new[] { "react-dom" }, new[] { @"ReactDOM\.render", @"createRoot" }, "React DOM");
        rules["next"] = new(new[] { "next" }, new[] { @"import.*from\s+['""]next", @"getServerSideProps" }, "Next.js");
        rules["express"] = new(new[] { "express" }, new[] { @"express\(\)", @"app\.(get|post|put|delete)\(" }, "Express.js");
        rules["jest"] = new(new[] { "jest" }, new[] { @"\b(describe|it|test|expect)\(", @"jest\." }, "Jest");
        rules["typescript"] = new(Array.Empty<string>(), Array.Empty<string>(), "TypeScript") { AlwaysActive = true };

        // ─── Python packages ───
        rules["django"] = new(new[] { "django" }, new[] { @"from\s+django", @"INSTALLED_APPS" }, "Django");
        rules["Django"] = rules["django"];
        rules["flask"] = new(new[] { "flask" }, new[] { @"from\s+flask", @"Flask\(__name__\)" }, "Flask");
        rules["Flask"] = rules["flask"];
        rules["fastapi"] = new(new[] { "fastapi" }, new[] { @"from\s+fastapi", @"FastAPI\(\)" }, "FastAPI");
        rules["FastAPI"] = rules["fastapi"];
        rules["pytest"] = new(new[] { "pytest" }, new[] { @"def\s+test_", @"import\s+pytest" }, "Pytest");

        return rules;
    }

    // ═══════════════════════════════════════════════════
    //  Capability Fingerprints
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Define regex patterns that prove specific capabilities exist in the codebase.
    /// Each entry: (capabilityName, provePatterns[], absenceLabel or null)
    /// </summary>
    private static List<(string Capability, string[] Patterns, string? AbsenceLabel)> GetCapabilityFingerprints(string primaryLanguage)
    {
        var fingerprints = new List<(string, string[], string?)>
        {
            // ── Resilience & error handling ──
            ("Circuit breaker",
                new[] { @"CircuitBreaker|CircuitState|CircuitBreakerAsync", @"class\s+\w*CircuitBreaker" },
                "No circuit breaker implementation found"),

            ("Retry logic with backoff",
                new[] { @"BackoffDelay|RetryCount|exponential.*backoff|backoff.*jitter", @"RetryAsync|\.Retry\(" },
                "No retry/backoff logic found"),

            ("Rate limiting / courtesy policy",
                new[] { @"CourtesyPolicy|RateLimi|429.*retry|503.*retry" },
                null),  // Not always expected

            // ── Search & retrieval ──
            ("RAG / vector search",
                new[] { @"RetrievalService|HybridSearch|SemanticSearch|ReciprocalRank|BM25" },
                "No RAG/vector search implementation found"),

            ("Embedding generation",
                new[] { @"EmbeddingService|GenerateEmbedding|GetEmbeddingBatch|embedding.*vector" },
                "No embedding generation found"),

            ("Full-text search indexing",
                new[] { @"FTS5|MATCH\s|CreateIndex|IndexService|SearchIndex" },
                null),

            // ── Security ──
            ("DPAPI / encrypted key storage",
                new[] { @"ProtectedData\.Protect|ProtectedData\.Unprotect|SecureKeyStore|DataProtectionScope" },
                null),

            ("Authentication / auth system",
                new[] { @"Authenticate|IAuthService|JwtBearer|UseAuthentication|OAuth" },
                null),

            // ── Testing quality ──
            ("Integration tests (real DB/HTTP)",
                new[] { @"WebApplicationFactory|IClassFixture|TestServer|CreateClient\(\)|InMemoryDatabase" },
                null),

            // ── Infrastructure ──
            ("OpenTelemetry / distributed tracing",
                new[] { @"OpenTelemetry|ActivitySource|DiagnosticSource|AddOpenTelemetry" },
                "No OpenTelemetry/distributed tracing found"),

            ("Benchmark suite",
                new[] { @"\[Benchmark\]|BenchmarkRunner|BenchmarkDotNet" },
                "No benchmark suite (BenchmarkDotNet) found"),

            ("Plugin / extensibility system",
                new[] { @"IPlugin|PluginLoader|LoadPlugin|AssemblyLoadContext.*plugin|MEF|ExportAttribute" },
                null), // Not always expected

            // ── CI/CD (redundant with file checks but catches inline scripts) ──
            ("CI/CD pipeline definition",
                new[] { @"name:\s*(build|ci|deploy|test)\s", @"jobs:\s*\n\s+\w+:" },
                null),  // File check in Layer 4 is primary

            // ── Documentation ──
            ("API documentation generation",
                new[] { @"swagger|OpenApi|Swashbuckle|<GenerateDocumentationFile>" },
                null),

            // ── Citation / verification ──
            ("Citation verification",
                new[] { @"CitationVerif|VerifyReport|VerifyCitation|GroundingScore" },
                null),

            // ── Logging ──
            ("Structured logging framework",
                new[] { @"ILogger<|Serilog|NLog|Log\.(Information|Warning|Error)" },
                null),
        };

        return fingerprints;
    }

    // ═══════════════════════════════════════════════════
    //  Helper Methods
    // ═══════════════════════════════════════════════════

    /// <summary>Find the first file containing a simple text pattern.</summary>
    private static string? FindFileWithPattern(Dictionary<string, string> fileContents, string pattern)
    {
        foreach (var (path, content) in fileContents)
        {
            if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(path);
        }
        return null;
    }

    /// <summary>Find the first file matching a regex pattern.</summary>
    private static string? FindFileWithRegex(Dictionary<string, string> fileContents, string pattern)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            foreach (var (path, content) in fileContents)
            {
                if (regex.IsMatch(content))
                    return Path.GetFileName(path);
            }
        }
        catch { /* pattern issue — skip */ }
        return null;
    }

    private static string TruncatePattern(string pattern)
        => pattern.Length > 50 ? pattern[..50] + "…" : pattern;

    /// <summary>Package usage detection rule: what imports and code patterns indicate actual usage.</summary>
    public class PackageUsageRule
    {
        public string[] ImportPatterns { get; }
        public string[] CodePatterns { get; }
        public string FriendlyLabel { get; }
        /// <summary>If true, the package is considered active just by being in the manifest (e.g., build tools, coverlet).</summary>
        public bool AlwaysActive { get; set; }

        public PackageUsageRule(string[] importPatterns, string[] codePatterns, string friendlyLabel)
        {
            ImportPatterns = importPatterns;
            CodePatterns = codePatterns;
            FriendlyLabel = friendlyLabel;
        }
    }
}
