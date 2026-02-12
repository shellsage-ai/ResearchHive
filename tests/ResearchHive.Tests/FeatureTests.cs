using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the 4 new features: Source Quality Ranking, Report Templates,
/// Search Time Range (date filters), and Retrieval Report Section Splitting.
/// </summary>
public class FeatureTests
{
    // ───────────────── Source Quality Scorer Tests ─────────────────

    [Theory]
    [InlineData("https://pubmed.ncbi.nlm.nih.gov/12345", 1.0)]
    [InlineData("https://nature.com/articles/abc", 1.0)]
    [InlineData("https://arxiv.org/abs/2301.0001", 1.0)]
    [InlineData("https://pnas.org/content/120/1", 1.0)]
    public void SourceQualityScorer_Tier1_ReturnsMaxScore(string url, double expected)
    {
        SourceQualityScorer.ScoreUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Test", 0.7)]
    [InlineData("https://stackoverflow.com/questions/123", 0.7)]
    [InlineData("https://learn.microsoft.com/docs", 0.7)]
    [InlineData("https://developer.mozilla.org/en-US/docs", 0.7)]
    public void SourceQualityScorer_Tier2_Returns07(string url, double expected)
    {
        SourceQualityScorer.ScoreUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://www.nytimes.com/2024/article", 0.5)]
    [InlineData("https://www.bbc.com/news/world", 0.5)]
    [InlineData("https://arstechnica.com/gadgets", 0.5)]
    [InlineData("https://www.reuters.com/business", 0.5)]
    public void SourceQualityScorer_Tier3_Returns05(string url, double expected)
    {
        SourceQualityScorer.ScoreUrl(url).Should().Be(expected);
    }

    [Fact]
    public void SourceQualityScorer_UnknownDomain_ReturnsDefaultTier()
    {
        SourceQualityScorer.ScoreUrl("https://randomsite.xyz/page").Should().Be(0.2);
    }

    [Fact]
    public void SourceQualityScorer_PathBoost_AddsPointOne()
    {
        // Path contains "research", so 0.2 + 0.1 = 0.3
        SourceQualityScorer.ScoreUrl("https://randomsite.xyz/research/paper123").Should().BeApproximately(0.3, 0.001);
    }

    [Fact]
    public void SourceQualityScorer_ParentDomain_MatchesTier()
    {
        // pubs.acs.org → parent domain acs.org → Tier 1
        SourceQualityScorer.ScoreUrl("https://pubs.acs.org/doi/abs/10.1021/some-paper").Should().Be(1.0);
    }

    [Fact]
    public void SourceQualityScorer_GovTld_Returns09()
    {
        SourceQualityScorer.ScoreUrl("https://data.somestate.gov/dataset").Should().Be(0.9);
    }

    [Fact]
    public void SourceQualityScorer_EduTld_Returns085()
    {
        SourceQualityScorer.ScoreUrl("https://www.mit.edu/research").Should().Be(0.85);
    }

    [Fact]
    public void SourceQualityScorer_OrgTld_Returns05()
    {
        SourceQualityScorer.ScoreUrl("https://someorganization.org/about").Should().Be(0.5);
    }

    [Fact]
    public void SourceQualityScorer_InvalidUrl_ReturnsDefault()
    {
        SourceQualityScorer.ScoreUrl("not-a-url").Should().Be(0.2);
    }

    [Fact]
    public void SourceQualityScorer_WwwPrefix_IsStripped()
    {
        // www.nature.com should resolve to nature.com → Tier 1
        SourceQualityScorer.ScoreUrl("https://www.nature.com/articles/s41586-024").Should().Be(1.0);
    }

    [Theory]
    [InlineData(1.0, "Academic")]
    [InlineData(0.9, "Academic")]
    [InlineData(0.7, "Authoritative")]
    [InlineData(0.5, "News/Reputable")]
    [InlineData(0.3, "General")]
    [InlineData(0.2, "Unranked")]
    [InlineData(0.0, "Unranked")]
    public void SourceQualityScorer_GetTierLabel_CorrectLabels(double score, string expected)
    {
        SourceQualityScorer.GetTierLabel(score).Should().Be(expected);
    }

    // ───────────────── Report Template Service Tests ─────────────────

    [Fact]
    public void ReportTemplate_GeneralResearch_HasDefaultSections()
    {
        var template = ReportTemplateService.GetResearchTemplate(DomainPack.GeneralResearch);

        template.Sections.Should().HaveCountGreaterOrEqualTo(6);
        template.Sections.Select(s => s.Heading).Should().Contain("Key Findings");
        template.Sections.Select(s => s.Heading).Should().Contain("Most Supported View");
        template.Sections.Select(s => s.Heading).Should().Contain("Detailed Analysis");
        template.Sections.Select(s => s.Heading).Should().Contain("Limitations");
        template.Sections.Select(s => s.Heading).Should().Contain("Sources");
    }

    [Fact]
    public void ReportTemplate_Chemistry_HasSafetySection()
    {
        var template = ReportTemplateService.GetResearchTemplate(DomainPack.ChemistrySafe);

        template.Sections.Select(s => s.Heading).Should().Contain("Safety & Handling");
    }

    [Fact]
    public void ReportTemplate_Maker_HasPracticalSection()
    {
        var template = ReportTemplateService.GetResearchTemplate(DomainPack.MakerMaterials);

        template.Sections.Select(s => s.Heading).Should().Contain("Practical Considerations");
    }

    [Fact]
    public void ReportTemplate_Programming_HasPatentSection()
    {
        var template = ReportTemplateService.GetResearchTemplate(DomainPack.ProgrammingResearchIP);

        template.Sections.Select(s => s.Heading).Should().Contain("Patent & IP Landscape");
    }

    [Fact]
    public void ReportTemplate_AllSections_HavePositiveTokenBudgets()
    {
        foreach (DomainPack pack in Enum.GetValues<DomainPack>())
        {
            var template = ReportTemplateService.GetResearchTemplate(pack);
            foreach (var section in template.Sections)
            {
                section.TargetTokens.Should().BeGreaterThan(0,
                    $"Section '{section.Heading}' for {pack} should have a positive token budget");
                section.Heading.Should().NotBeNullOrWhiteSpace();
                section.Instruction.Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void ReportTemplate_TotalTokens_AreReasonable()
    {
        var template = ReportTemplateService.GetResearchTemplate(DomainPack.GeneralResearch);

        // We expect total >= 3000 tokens across all sections for a comprehensive report
        template.TotalTargetTokens.Should().BeGreaterOrEqualTo(3000);
    }

    [Fact]
    public void ReportTemplate_GeneralResearch_NoExtraDomainSection()
    {
        var template = ReportTemplateService.GetResearchTemplate(DomainPack.GeneralResearch);

        template.Sections.Select(s => s.Heading).Should().NotContain("Safety & Handling");
        template.Sections.Select(s => s.Heading).Should().NotContain("Practical Considerations");
        template.Sections.Select(s => s.Heading).Should().NotContain("Patent & IP Landscape");
    }

    // ───────────────── Date Filter Tests (BrowserSearchService) ─────────────────

    [Theory]
    [InlineData("bing", "day", "&freshness=Day")]
    [InlineData("bing", "week", "&freshness=Week")]
    [InlineData("bing", "month", "&freshness=Month")]
    [InlineData("brave", "day", "&tf=pd")]
    [InlineData("brave", "week", "&tf=pw")]
    [InlineData("brave", "month", "&tf=pm")]
    [InlineData("brave", "year", "&tf=py")]
    [InlineData("yahoo", "day", "&age=1d")]
    [InlineData("yahoo", "week", "&age=1w")]
    [InlineData("yahoo", "month", "&age=1m")]
    public void GetDateFilter_ReturnsCorrectFilter(string engine, string timeRange, string expected)
    {
        BrowserSearchService.GetDateFilter(engine, timeRange).Should().Be(expected);
    }

    [Theory]
    [InlineData("bing", null)]
    [InlineData("bing", "any")]
    [InlineData("brave", null)]
    [InlineData("brave", "any")]
    [InlineData("yahoo", null)]
    [InlineData("unknown", "week")]
    public void GetDateFilter_ReturnsEmpty_WhenNoFilterOrUnknownEngine(string engine, string? timeRange)
    {
        BrowserSearchService.GetDateFilter(engine, timeRange).Should().BeEmpty();
    }

    [Fact]
    public void GetDateFilter_Scholar_Day_UsesCurrentYear()
    {
        var result = BrowserSearchService.GetDateFilter("scholar", "day");
        result.Should().Contain($"&as_ylo={DateTime.UtcNow.Year}");
    }

    [Fact]
    public void GetDateFilter_Scholar_Year_UsesPreviousYear()
    {
        var result = BrowserSearchService.GetDateFilter("scholar", "year");
        result.Should().Contain($"&as_ylo={DateTime.UtcNow.Year - 1}");
    }

    // ───────────────── Report Section Splitting Tests ─────────────────

    [Fact]
    public void SplitReportIntoSections_ParsesMarkdownHeadings()
    {
        var markdown = """
            Some introduction text that is more than twenty characters long.

            ## Key Findings
            Finding 1: Something important was discovered about the topic.
            Finding 2: Another important finding.

            ## Detailed Analysis
            A deep dive into the topic reveals many interesting aspects and considerations that should be explored.

            ## Sources
            [1] Example Source — https://example.com
            This source provides background information.
            """;

        var sections = RetrievalService.SplitReportIntoSections(markdown);

        sections.Should().HaveCountGreaterOrEqualTo(3);
        sections.Select(s => s.Heading).Should().Contain("Key Findings");
        sections.Select(s => s.Heading).Should().Contain("Detailed Analysis");
        sections.Select(s => s.Heading).Should().Contain("Sources");
    }

    [Fact]
    public void SplitReportIntoSections_IncludesIntroductionIfLongEnough()
    {
        var markdown = """
            This is a lengthy introduction that sets up the report context and provides enough content.

            ## Analysis
            Some analysis text that is definitely more than twenty characters to be included.
            """;

        var sections = RetrievalService.SplitReportIntoSections(markdown);

        // First section should be "Introduction" (default heading)
        sections.First().Heading.Should().Be("Introduction");
    }

    [Fact]
    public void SplitReportIntoSections_SkipsShortSections()
    {
        var markdown = """
            ## Short
            Tiny.

            ## Long Section
            This section has enough content to be included in the results because it exceeds twenty characters.
            """;

        var sections = RetrievalService.SplitReportIntoSections(markdown);

        // "Short" section is < 20 chars so skipped
        sections.Select(s => s.Heading).Should().NotContain("Short");
        sections.Select(s => s.Heading).Should().Contain("Long Section");
    }

    [Fact]
    public void SplitReportIntoSections_EmptyMarkdown_ReturnsEmpty()
    {
        RetrievalService.SplitReportIntoSections("").Should().BeEmpty();
    }

    // ───────────────── AppSettings Defaults ─────────────────

    [Fact]
    public void AppSettings_SourceQualityRanking_DefaultsFalse()
    {
        var settings = new AppSettings();
        settings.SourceQualityRanking.Should().BeFalse();
    }

    [Fact]
    public void AppSettings_SearchTimeRange_DefaultsToAny()
    {
        var settings = new AppSettings();
        settings.SearchTimeRange.Should().Be("any");
    }

    [Fact]
    public void AppSettings_SectionalReports_DefaultsTrue()
    {
        var settings = new AppSettings();
        settings.SectionalReports.Should().BeTrue();
    }

    // ───────────────── QaMessage Model ─────────────────

    [Fact]
    public void QaMessage_HasRequiredProperties()
    {
        var msg = new QaMessage
        {
            Id = "qa-1",
            SessionId = "session-1",
            Question = "What is X?",
            Answer = "X is Y.",
            Scope = "session",
            TimestampUtc = DateTime.UtcNow
        };

        msg.Id.Should().Be("qa-1");
        msg.SessionId.Should().Be("session-1");
        msg.Question.Should().Be("What is X?");
        msg.Answer.Should().Be("X is Y.");
        msg.Scope.Should().Be("session");
    }

    [Fact]
    public void QaMessage_Scope_CanBeReportId()
    {
        var msg = new QaMessage { Scope = "rpt-abc123" };
        msg.Scope.Should().Be("rpt-abc123");
    }
}
