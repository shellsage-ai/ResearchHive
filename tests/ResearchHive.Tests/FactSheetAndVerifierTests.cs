using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the deterministic fact sheet pipeline (Phase 19):
/// - RepoFactSheet model & ToPromptSection()
/// - RepoFactSheetBuilder: package classification, capability fingerprinting, type inference
/// - PostScanVerifier: hallucination pruning, complement validation, strength/gap injection
/// - VerificationResult summary generation
/// </summary>
public class FactSheetAndVerifierTests : IDisposable
{
    private readonly string _tempDir;

    public FactSheetAndVerifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"factsheet_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ═══════════════════════════════════════════════════
    //  RepoFactSheet Model Tests
    // ═══════════════════════════════════════════════════

    [Fact]
    public void RepoFactSheet_DefaultValues_AreInitialized()
    {
        var sheet = new RepoFactSheet();
        sheet.ActivePackages.Should().BeEmpty();
        sheet.PhantomPackages.Should().BeEmpty();
        sheet.ProvenCapabilities.Should().BeEmpty();
        sheet.ConfirmedAbsent.Should().BeEmpty();
        sheet.DiagnosticFilesPresent.Should().BeEmpty();
        sheet.DiagnosticFilesMissing.Should().BeEmpty();
        sheet.TestMethodCount.Should().Be(0);
        sheet.TestFileCount.Should().Be(0);
        sheet.TotalSourceFiles.Should().Be(0);
        sheet.AppType.Should().BeEmpty();
        sheet.DatabaseTechnology.Should().BeEmpty();
        sheet.TestFramework.Should().BeEmpty();
        sheet.Ecosystem.Should().BeEmpty();
    }

    [Fact]
    public void RepoFactSheet_ToPromptSection_IncludesActivePackages()
    {
        var sheet = new RepoFactSheet();
        sheet.ActivePackages.Add(new PackageEvidence { PackageName = "xunit", Version = "2.5", Evidence = "using Xunit in tests" });

        var section = sheet.ToPromptSection();
        section.Should().Contain("VERIFIED GROUND TRUTH");
        section.Should().Contain("xunit 2.5");
        section.Should().Contain("using Xunit in tests");
        section.Should().Contain("PACKAGES (installed AND actively used");
    }

    [Fact]
    public void RepoFactSheet_ToPromptSection_IncludesPhantomPackages()
    {
        var sheet = new RepoFactSheet();
        sheet.PhantomPackages.Add(new PackageEvidence { PackageName = "Moq", Version = "4.18", Evidence = "" });

        var section = sheet.ToPromptSection();
        section.Should().Contain("PACKAGES (installed but UNUSED");
        section.Should().Contain("Moq 4.18");
        section.Should().Contain("zero usage detected");
    }

    [Fact]
    public void RepoFactSheet_ToPromptSection_IncludesProvenCapabilities()
    {
        var sheet = new RepoFactSheet();
        sheet.ProvenCapabilities.Add(new CapabilityFingerprint
        {
            Capability = "Circuit breaker",
            Evidence = "LlmCircuitBreaker.cs — matched pattern"
        });

        var section = sheet.ToPromptSection();
        section.Should().Contain("CAPABILITIES PROVEN BY CODE PATTERNS");
        section.Should().Contain("Circuit breaker");
        section.Should().Contain("LlmCircuitBreaker.cs");
    }

    [Fact]
    public void RepoFactSheet_ToPromptSection_IncludesConfirmedAbsent()
    {
        var sheet = new RepoFactSheet();
        sheet.ConfirmedAbsent.Add(new CapabilityFingerprint
        {
            Capability = "OpenTelemetry",
            Evidence = "No OpenTelemetry/distributed tracing found"
        });

        var section = sheet.ToPromptSection();
        section.Should().Contain("CONFIRMED ABSENT");
        section.Should().Contain("OpenTelemetry");
    }

    [Fact]
    public void RepoFactSheet_ToPromptSection_IncludesAppTypeAndDatabase()
    {
        var sheet = new RepoFactSheet
        {
            AppType = "WPF desktop application",
            DatabaseTechnology = "Raw SQLite via Microsoft.Data.Sqlite",
            TestFramework = "xUnit",
            TestMethodCount = 439,
            TestFileCount = 15,
            Ecosystem = ".NET/C#"
        };

        var section = sheet.ToPromptSection();
        section.Should().Contain("APP TYPE: WPF desktop application");
        section.Should().Contain("DATABASE: Raw SQLite");
        section.Should().Contain("TEST FRAMEWORK: xUnit (439 test methods in 15 files)");
        section.Should().Contain("ECOSYSTEM: .NET/C#");
    }

    [Fact]
    public void RepoFactSheet_ToPromptSection_IncludesLlmRules()
    {
        var sheet = new RepoFactSheet();
        var section = sheet.ToPromptSection();
        section.Should().Contain("RULES FOR THE LLM");
        section.Should().Contain("Do NOT list phantom packages");
        section.Should().Contain("Do NOT claim a gap");
        section.Should().Contain("Do NOT claim a strength");
    }

    [Fact]
    public void RepoFactSheet_ToPromptSection_IncludesDiagnosticFiles()
    {
        var sheet = new RepoFactSheet();
        sheet.DiagnosticFilesPresent.Add("CI/CD workflows (.github/workflows/)");
        sheet.DiagnosticFilesPresent.Add("License file");
        sheet.DiagnosticFilesMissing.Add("Dockerfile");

        var section = sheet.ToPromptSection();
        section.Should().Contain("FILES/DIRS PRESENT: CI/CD workflows (.github/workflows/), License file");
        section.Should().Contain("FILES/DIRS MISSING: Dockerfile");
    }

    [Fact]
    public void PackageEvidence_DefaultValues_AreEmpty()
    {
        var pkg = new PackageEvidence();
        pkg.PackageName.Should().BeEmpty();
        pkg.Version.Should().BeEmpty();
        pkg.Evidence.Should().BeEmpty();
    }

    [Fact]
    public void CapabilityFingerprint_DefaultValues_AreEmpty()
    {
        var cap = new CapabilityFingerprint();
        cap.Capability.Should().BeEmpty();
        cap.Evidence.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════
    //  VerificationResult Tests
    // ═══════════════════════════════════════════════════

    [Fact]
    public void VerificationResult_DefaultValues_AreEmpty()
    {
        var result = new VerificationResult();
        result.TotalCorrections.Should().Be(0);
        result.Summary.Should().Be("No corrections needed");
    }

    [Fact]
    public void VerificationResult_TotalCorrections_SumsAllCategories()
    {
        var result = new VerificationResult();
        result.GapsRemoved.Add("gap1");
        result.GapsRemoved.Add("gap2");
        result.StrengthsRemoved.Add("str1");
        result.FrameworksRemoved.Add("fw1");
        result.ComplementsRemoved.Add("comp1");
        result.StrengthsAdded.Add("added1");
        result.GapsAdded.Add("gapAdded1");

        result.TotalCorrections.Should().Be(7);
    }

    [Fact]
    public void VerificationResult_Summary_DescribesCorrections()
    {
        var result = new VerificationResult();
        result.GapsRemoved.Add("gap1");
        result.GapsRemoved.Add("gap2");
        result.StrengthsAdded.Add("str1");

        var summary = result.Summary;
        summary.Should().Contain("2 hallucinated gaps removed");
        summary.Should().Contain("1 proven strengths injected");
    }

    // ═══════════════════════════════════════════════════
    //  PostScanVerifier Tests
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task PostScanVerifier_PrunesHallucinatedGaps_WhenCapabilityProven()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.Gaps.Add("No circuit breaker or resilience pattern found");
        profile.Gaps.Add("Missing rate limiting");

        var factSheet = new RepoFactSheet();
        factSheet.ProvenCapabilities.Add(new CapabilityFingerprint
        {
            Capability = "Circuit breaker",
            Evidence = "LlmCircuitBreaker.cs"
        });

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Gaps.Should().NotContain(g => g.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase));
        result.GapsRemoved.Should().HaveCountGreaterOrEqualTo(1);
        result.GapsRemoved.First().Should().Contain("HALLUCINATED");
    }

    [Fact]
    public async Task PostScanVerifier_PrunesHallucinatedStrengths_WhenCapabilityAbsent()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.Strengths.Add("Excellent OpenTelemetry integration for distributed tracing");

        var factSheet = new RepoFactSheet();
        factSheet.ConfirmedAbsent.Add(new CapabilityFingerprint
        {
            Capability = "OpenTelemetry / distributed tracing",
            Evidence = "No OpenTelemetry found"
        });

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Strengths.Should().NotContain(s => s.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase));
        result.StrengthsRemoved.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task PostScanVerifier_PrunesStrengthsCitingPhantomPackages()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.Strengths.Add("Uses Moq for comprehensive mocking in tests");

        var factSheet = new RepoFactSheet();
        factSheet.PhantomPackages.Add(new PackageEvidence
        {
            PackageName = "Moq",
            Version = "4.18",
            Evidence = ""
        });

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Strengths.Should().NotContain(s => s.Contains("Moq", StringComparison.OrdinalIgnoreCase));
        result.StrengthsRemoved.Should().Contain(s => s.Contains("PHANTOM-DEP"));
    }

    [Fact]
    public async Task PostScanVerifier_PrunesPhantomFrameworks_EFCoreWhenRawSqlite()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.Frameworks.Add("Entity Framework Core");
        profile.Frameworks.Add("CommunityToolkit.Mvvm");

        var factSheet = new RepoFactSheet
        {
            DatabaseTechnology = "Raw SQLite via Microsoft.Data.Sqlite (hand-written SQL, NOT EF Core)",
            AppType = "WPF desktop application"
        };

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Frameworks.Should().NotContain(f => f.Contains("Entity Framework"));
        profile.Frameworks.Should().Contain("CommunityToolkit.Mvvm"); // Should survive
        result.FrameworksRemoved.Should().Contain(s => s.Contains("WRONG-DB"));
    }

    [Fact]
    public async Task PostScanVerifier_PrunesAspNetCoreFramework_WhenAppIsWpf()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.Frameworks.Add("ASP.NET Core");

        var factSheet = new RepoFactSheet { AppType = "WPF desktop application" };

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Frameworks.Should().NotContain("ASP.NET Core");
        result.FrameworksRemoved.Should().Contain(s => s.Contains("WRONG-TYPE"));
    }

    [Fact]
    public async Task PostScanVerifier_InjectsProvenStrengths_WhenLlmMissedThem()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        // Profile has no strengths mentioning circuit breaker

        var factSheet = new RepoFactSheet();
        factSheet.ProvenCapabilities.Add(new CapabilityFingerprint
        {
            Capability = "Circuit breaker",
            Evidence = "LlmCircuitBreaker.cs"
        });

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Strengths.Should().Contain(s => s.Contains("Circuit breaker"));
        result.StrengthsAdded.Should().Contain(s => s.Contains("INJECTED"));
    }

    [Fact]
    public async Task PostScanVerifier_InjectsConfirmedGaps_ForMissingDiagnosticFiles()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();

        var factSheet = new RepoFactSheet();
        factSheet.DiagnosticFilesMissing.Add("Dockerfile");

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Gaps.Should().Contain(g => g.Contains("Dockerfile"));
        result.GapsAdded.Should().Contain(s => s.Contains("INJECTED"));
    }

    [Fact]
    public async Task PostScanVerifier_KeepsLegitimateGaps()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.Gaps.Add("No CI/CD pipeline configured");
        profile.Gaps.Add("Missing integration tests for database layer");

        var factSheet = new RepoFactSheet(); // No proven capabilities that contradict these

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Gaps.Should().Contain("No CI/CD pipeline configured");
        profile.Gaps.Should().Contain("Missing integration tests for database layer");
    }

    [Fact]
    public async Task PostScanVerifier_KeepsLegitimateStrengths()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.Strengths.Add("Excellent test coverage with 439 tests");

        var factSheet = new RepoFactSheet { TestFramework = "xUnit", TestMethodCount = 439 };

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.Strengths.Should().Contain("Excellent test coverage with 439 tests");
    }

    [Fact]
    public async Task PostScanVerifier_NoCorrections_WhenProfileMatchesFactSheet()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        // Add strengths that match proven capabilities  
        profile.Strengths.Add("Robust circuit breaker prevents cascading LLM failures");

        var factSheet = new RepoFactSheet();
        factSheet.ProvenCapabilities.Add(new CapabilityFingerprint
        {
            Capability = "Circuit breaker",
            Evidence = "LlmCircuitBreaker.cs"
        });

        var result = await verifier.VerifyAsync(profile, factSheet);

        // The injection should NOT double-add circuit breaker since it's already mentioned
        result.StrengthsAdded.Should().NotContain(s => s.Contains("Circuit breaker"));
    }

    [Fact]
    public async Task PostScanVerifier_RemovesRedundantTestFrameworkComplements()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.ComplementSuggestions.Add(new ComplementProject
        {
            Name = "NUnit",
            Url = "https://github.com/nunit/nunit",
            Purpose = "Unit testing framework for .NET",
            WhatItAdds = "Alternative test framework",
            Category = "Testing"
        });

        var factSheet = new RepoFactSheet { TestFramework = "xUnit", Ecosystem = ".NET/C#" };

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.ComplementSuggestions.Should().NotContain(c => c.Name == "NUnit");
        result.ComplementsRemoved.Should().Contain(s => s.Contains("REDUNDANT-TEST"));
    }

    [Fact]
    public async Task PostScanVerifier_RemovesWrongEcosystemComplements()
    {
        var verifier = new PostScanVerifier();
        var profile = CreateTestProfile();
        profile.ComplementSuggestions.Add(new ComplementProject
        {
            Name = "Resilience4j",
            Url = "https://github.com/resilience4j/resilience4j",
            Purpose = "Java fault tolerance library",
            WhatItAdds = "Circuit breaker for Java applications",
            Category = "Other"
        });

        var factSheet = new RepoFactSheet { Ecosystem = ".NET/C#" };

        var result = await verifier.VerifyAsync(profile, factSheet);

        profile.ComplementSuggestions.Should().NotContain(c => c.Name == "Resilience4j");
        result.ComplementsRemoved.Should().Contain(s => s.Contains("WRONG-ECOSYSTEM"));
    }

    // ═══════════════════════════════════════════════════
    //  RepoFactSheetBuilder Tests (via temp directory)
    // ═══════════════════════════════════════════════════

    [Fact]
    public void RepoFactSheetBuilder_Build_ClassifiesActiveVsPhantomPackages()
    {
        // Create source files that USE xunit and FluentAssertions, but NOT Moq
        CreateTempFile("Tests/SampleTest.cs", @"
using Xunit;
using FluentAssertions;

namespace MyTests;

public class SampleTest
{
    [Fact]
    public void Test1()
    {
        true.Should().BeTrue();
    }
}
");

        var profile = new RepoProfile
        {
            PrimaryLanguage = "C#",
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "xunit", Version = "2.5" },
                new() { Name = "FluentAssertions", Version = "6.12" },
                new() { Name = "Moq", Version = "4.18" },
            }
        };

        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.ActivePackages.Should().Contain(p => p.PackageName == "xunit");
        sheet.ActivePackages.Should().Contain(p => p.PackageName == "FluentAssertions");
        sheet.PhantomPackages.Should().Contain(p => p.PackageName == "Moq");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_DetectsCircuitBreakerCapability()
    {
        CreateTempFile("Services/LlmCircuitBreaker.cs", @"
namespace MyApp.Services;

public class LlmCircuitBreaker
{
    private CircuitState _state = CircuitState.Closed;
    private int _failureCount;
    private readonly int _threshold = 3;
}
");

        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.ProvenCapabilities.Should().Contain(c => c.Capability == "Circuit breaker");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_DetectsRetryBackoff()
    {
        CreateTempFile("Services/RetryHelper.cs", @"
namespace MyApp.Services;

public class RetryHelper
{
    public TimeSpan BackoffDelay(int retryCount) 
        => TimeSpan.FromSeconds(Math.Pow(2, retryCount));
}
");

        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.ProvenCapabilities.Should().Contain(c => c.Capability == "Retry logic with backoff");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_DetectsRagVectorSearch()
    {
        CreateTempFile("Services/HybridSearch.cs", @"
namespace MyApp.Services;

public class HybridSearch
{
    public List<string> ReciprocalRankFusion(List<List<string>> results) => new();
}
");

        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.ProvenCapabilities.Should().Contain(c => c.Capability == "RAG / vector search");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_InfersWpfAppType()
    {
        CreateTempFile("App.csproj", @"<Project><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>");

        var profile = new RepoProfile
        {
            PrimaryLanguage = "C#",
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "CommunityToolkit.Mvvm", Version = "8.2" }
            }
        };

        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.AppType.Should().Contain("WPF");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_InfersRawSqliteDatabase()
    {
        CreateTempFile("Data/SessionDb.cs", @"
using Microsoft.Data.Sqlite;

namespace MyApp.Data;

public class SessionDb
{
    private SqliteConnection _conn;
    
    public void Init()
    {
        var cmd = new SqliteCommand(""CREATE TABLE..."", _conn);
        cmd.ExecuteNonQuery();
    }
}
");

        var profile = new RepoProfile
        {
            PrimaryLanguage = "C#",
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "Microsoft.Data.Sqlite", Version = "8.0" }
            }
        };

        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.DatabaseTechnology.Should().Contain("Raw SQLite");
        sheet.DatabaseTechnology.Should().Contain("NOT EF Core");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_InfersXunitTestFramework_AndCountsTests()
    {
        CreateTempFile("Tests/Test1.cs", @"
using Xunit;

public class Test1
{
    [Fact]
    public void A() { }

    [Fact]
    public void B() { }

    [Theory]
    public void C() { }
}
");
        CreateTempFile("Tests/Test2.cs", @"
using Xunit;

public class Test2
{
    [Fact]
    public void D() { }
}
");

        var profile = new RepoProfile
        {
            PrimaryLanguage = "C#",
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "xunit", Version = "2.5" }
            }
        };

        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.TestFramework.Should().Be("xUnit");
        sheet.TestMethodCount.Should().Be(4); // 3 in Test1 + 1 in Test2
        sheet.TestFileCount.Should().Be(2);
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_InfersDotNetEcosystem()
    {
        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.Ecosystem.Should().Be(".NET/C#");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_ChecksDiagnosticFiles()
    {
        // Create some diagnostic files
        Directory.CreateDirectory(Path.Combine(_tempDir, ".github", "workflows"));
        File.WriteAllText(Path.Combine(_tempDir, ".github", "workflows", "build.yml"), "name: build");
        File.WriteAllText(Path.Combine(_tempDir, "LICENSE"), "MIT License");
        // Don't create Dockerfile (should be missing)

        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.DiagnosticFilesPresent.Should().Contain(f => f.Contains("CI/CD"));
        sheet.DiagnosticFilesPresent.Should().Contain(f => f.Contains("License"));
        sheet.DiagnosticFilesMissing.Should().Contain(f => f.Contains("Dockerfile"));
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_CountsTotalSourceFiles()
    {
        CreateTempFile("src/File1.cs", "class File1 {}");
        CreateTempFile("src/File2.cs", "class File2 {}");
        CreateTempFile("src/File3.cs", "class File3 {}");

        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.TotalSourceFiles.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_EFCoreDetectedByBothPackageAndDbContext()
    {
        CreateTempFile("Data/AppDbContext.cs", @"
using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
}
");

        var profile = new RepoProfile
        {
            PrimaryLanguage = "C#",
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "Microsoft.EntityFrameworkCore", Version = "8.0" }
            }
        };

        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.DatabaseTechnology.Should().Contain("Entity Framework Core");
        sheet.ActivePackages.Should().Contain(p => p.PackageName == "Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_HandlesMissingClonePath()
    {
        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();

        // Null clone path should not crash
        var sheet = builder.Build(profile, null);
        sheet.TotalSourceFiles.Should().Be(0);
        sheet.AppType.Should().NotBeNull();
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_HandlesNonExistentClonePath()
    {
        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();

        var sheet = builder.Build(profile, @"C:\nonexistent\path\12345");
        sheet.TotalSourceFiles.Should().Be(0);
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_DetectsEmbeddingGeneration()
    {
        CreateTempFile("Services/EmbeddingService.cs", @"
namespace MyApp.Services;

public class EmbeddingService
{
    public float[] GenerateEmbedding(string text) => Array.Empty<float>();
}
");

        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.ProvenCapabilities.Should().Contain(c => c.Capability == "Embedding generation");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_DetectsDpapi()
    {
        CreateTempFile("Services/SecureStore.cs", @"
using System.Security.Cryptography;

namespace MyApp;

public class SecureStore
{
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}
");

        var profile = new RepoProfile { PrimaryLanguage = "C#" };
        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.ProvenCapabilities.Should().Contain(c => c.Capability == "DPAPI / encrypted key storage");
    }

    [Fact]
    public void RepoFactSheetBuilder_Build_AlwaysActivePackagesAreActive()
    {
        // coverlet.collector and typescript are "AlwaysActive" - they're build tools
        CreateTempFile("src/Program.cs", "class Program {}");

        var profile = new RepoProfile
        {
            PrimaryLanguage = "C#",
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "coverlet.collector", Version = "6.0" }
            }
        };

        var builder = CreateFactSheetBuilder();
        var sheet = builder.Build(profile, _tempDir);

        sheet.ActivePackages.Should().Contain(p => p.PackageName == "coverlet.collector");
        sheet.PhantomPackages.Should().NotContain(p => p.PackageName == "coverlet.collector");
    }

    // ═══════════════════════════════════════════════════
    //  Full Pipeline Integration Tests
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task FullPipeline_FactSheetPlusVerifier_CatchesHallucinatedScan()
    {
        // Simulate the llama3.1:8b hallucination scenario:
        // LLM claims EF Core, ASP.NET Core, no circuit breaker — all wrong
        CreateTempFile("Services/LlmCircuitBreaker.cs", @"
public class LlmCircuitBreaker { private CircuitState _state; }
");
        CreateTempFile("Services/HybridSearch.cs", @"
public class HybridSearch { public void ReciprocalRankFusion() {} }
");
        CreateTempFile("Data/SessionDb.cs", @"
using Microsoft.Data.Sqlite;
public class SessionDb { private SqliteConnection _conn; SqliteCommand _cmd; }
");
        CreateTempFile("App.csproj", @"<Project><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>");

        var profile = new RepoProfile
        {
            PrimaryLanguage = "C#",
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "Microsoft.Data.Sqlite", Version = "8.0" },
                new() { Name = "CommunityToolkit.Mvvm", Version = "8.2" },
                new() { Name = "Moq", Version = "4.18" },  // phantom
            },
            // Simulated LLM output with hallucinations:
            Frameworks = new List<string> { "Entity Framework Core", ".NET 8", "ASP.NET Core" },
            Strengths = new List<string>
            {
                "Uses Moq for comprehensive testing",
                "Good ASP.NET Core middleware pipeline",
            },
            Gaps = new List<string>
            {
                "No circuit breaker or resilience patterns",
                "No search or embedding capabilities",
                "No CI/CD pipeline",
            },
            ComplementSuggestions = new List<ComplementProject>
            {
                new() { Name = "NUnit", Url = "https://github.com/nunit/nunit", Purpose = "Testing framework", WhatItAdds = "tests", Category = "Testing" },
                new() { Name = "Resilience4j", Url = "https://github.com/resilience4j/resilience4j", Purpose = "Java fault tolerance library", WhatItAdds = "Circuit breaker for Java", Category = "Other" },
            }
        };

        // Step 1: Build fact sheet
        var builder = CreateFactSheetBuilder();
        var factSheet = builder.Build(profile, _tempDir);
        profile.FactSheet = factSheet;

        // Verify fact sheet correctness
        factSheet.AppType.Should().Contain("WPF");
        factSheet.DatabaseTechnology.Should().Contain("Raw SQLite");
        factSheet.PhantomPackages.Should().Contain(p => p.PackageName == "Moq");
        factSheet.ProvenCapabilities.Should().Contain(c => c.Capability == "Circuit breaker");
        factSheet.ProvenCapabilities.Should().Contain(c => c.Capability.Contains("RAG"));

        // Step 2: Run post-scan verifier
        var verifier = new PostScanVerifier();
        var result = await verifier.VerifyAsync(profile, factSheet);

        // Verify hallucinations were caught:
        profile.Frameworks.Should().NotContain("Entity Framework Core"); // WRONG-DB
        profile.Frameworks.Should().NotContain("ASP.NET Core"); // WRONG-TYPE
        profile.Gaps.Should().NotContain(g => g.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase)); // proven
        profile.Gaps.Should().NotContain(g => g.Contains("search", StringComparison.OrdinalIgnoreCase) && g.Contains("embedding", StringComparison.OrdinalIgnoreCase)); // proven
        profile.Strengths.Should().NotContain(s => s.Contains("Moq", StringComparison.OrdinalIgnoreCase)); // phantom
        profile.ComplementSuggestions.Should().NotContain(c => c.Name == "Resilience4j"); // wrong ecosystem

        // Verify corrections were logged
        result.TotalCorrections.Should().BeGreaterOrEqualTo(4);
        result.Summary.Should().NotBe("No corrections needed");
    }

    // ═══════════════════════════════════════════════════
    //  Prompt Section Tests (fact sheet in prompt builders)
    // ═══════════════════════════════════════════════════

    [Fact]
    public void RepoScannerService_BuildRagAnalysisPrompt_IncludesFactSheet()
    {
        var profile = new RepoProfile
        {
            Owner = "test",
            Name = "repo",
            PrimaryLanguage = "C#",
            FactSheet = new RepoFactSheet
            {
                AppType = "WPF desktop application",
                DatabaseTechnology = "Raw SQLite",
                Ecosystem = ".NET/C#"
            }
        };

        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, "code book", new[] { "chunk1" });

        prompt.Should().Contain("VERIFIED GROUND TRUTH");
        prompt.Should().Contain("WPF desktop application");
        prompt.Should().Contain("Raw SQLite");
    }

    [Fact]
    public void RepoScannerService_BuildConsolidatedAnalysisPrompt_IncludesFactSheet()
    {
        var profile = new RepoProfile
        {
            Owner = "test",
            Name = "repo",
            PrimaryLanguage = "C#",
            FactSheet = new RepoFactSheet
            {
                AppType = "WPF desktop application",
                TestFramework = "xUnit",
                TestMethodCount = 100,
                TestFileCount = 5,
            }
        };

        var prompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, new[] { "chunk1" });

        prompt.Should().Contain("VERIFIED GROUND TRUTH");
        prompt.Should().Contain("WPF desktop application");
        prompt.Should().Contain("xUnit");
    }

    [Fact]
    public void RepoScannerService_BuildFullAgenticPrompt_IncludesFactSheet()
    {
        var profile = new RepoProfile
        {
            Owner = "test",
            Name = "repo",
            PrimaryLanguage = "C#",
            FactSheet = new RepoFactSheet
            {
                AppType = "Console application",
                Ecosystem = ".NET/C#"
            }
        };
        profile.FactSheet.ProvenCapabilities.Add(new CapabilityFingerprint
        {
            Capability = "Structured logging",
            Evidence = "ILogger usage found"
        });

        var prompt = RepoScannerService.BuildFullAgenticPrompt(profile, new[] { "chunk1" });

        prompt.Should().Contain("VERIFIED GROUND TRUTH");
        prompt.Should().Contain("Console application");
        prompt.Should().Contain("Structured logging");
    }

    [Fact]
    public void RepoScannerService_PromptBuilders_SkipFactSheet_WhenNull()
    {
        var profile = new RepoProfile
        {
            Owner = "test",
            Name = "repo",
            PrimaryLanguage = "C#",
            FactSheet = null
        };

        var ragPrompt = RepoScannerService.BuildRagAnalysisPrompt(profile, null, new[] { "chunk1" });
        var consolidatedPrompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, new[] { "chunk1" });
        var agenticPrompt = RepoScannerService.BuildFullAgenticPrompt(profile, new[] { "chunk1" });

        ragPrompt.Should().NotContain("VERIFIED GROUND TRUTH");
        consolidatedPrompt.Should().NotContain("VERIFIED GROUND TRUTH");
        agenticPrompt.Should().NotContain("VERIFIED GROUND TRUTH");
    }

    // ═══════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════

    private RepoProfile CreateTestProfile() => new()
    {
        Owner = "test",
        Name = "test-repo",
        PrimaryLanguage = "C#",
        Strengths = new List<string>(),
        Gaps = new List<string>(),
        Frameworks = new List<string>(),
        ComplementSuggestions = new List<ComplementProject>(),
    };

    private RepoFactSheetBuilder CreateFactSheetBuilder()
    {
        // Create a minimal RepoCloneService with settings that point to our temp dir
        var settings = new AppSettings
        {
            DataRootPath = Path.GetTempPath()
        };
        var cloneService = new RepoCloneService(settings);
        return new RepoFactSheetBuilder(cloneService);
    }

    private void CreateTempFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }
}
