using FluentAssertions;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for SearchResultExtractor — engine-specific HTML parsing.
/// </summary>
public class SearchResultExtractorTests
{
    [Fact]
    public void Extract_DuckDuckGo_FindsResultLinks()
    {
        var html = """
        <div class="result results_links">
            <div class="result__body">
                <a class="result__a" href="https://example.com/article1">Article 1</a>
            </div>
        </div>
        <div class="result results_links">
            <div class="result__body">
                <a class="result__a" href="https://example.com/article2">Article 2</a>
            </div>
        </div>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Should().Contain("https://example.com/article1");
        urls.Should().Contain("https://example.com/article2");
    }

    [Fact]
    public void Extract_DuckDuckGo_DecodesRedirectUrls()
    {
        var html = """
        <a class="result__a" href="https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&rut=abc">Result</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Should().Contain("https://example.com/page");
    }

    [Fact]
    public void Extract_Brave_FindsResultHeaders()
    {
        var html = """
        <a class="result-header" href="https://brave-result.com/page1">Result 1</a>
        <a class="result-header" href="https://brave-result.com/page2">Result 2</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://search.brave.com/search?q=test");
        urls.Should().Contain("https://brave-result.com/page1");
        urls.Should().Contain("https://brave-result.com/page2");
    }

    [Fact]
    public void Extract_Bing_FindsAlgoResults()
    {
        var html = """
        <li class="b_algo">
            <h2><a href="https://bing-result.com/page1">Title</a></h2>
        </li>
        <li class="b_algo">
            <h2><a href="https://bing-result.com/page2">Title 2</a></h2>
        </li>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://www.bing.com/search?q=test");
        urls.Should().Contain("https://bing-result.com/page1");
        urls.Should().Contain("https://bing-result.com/page2");
    }

    [Fact]
    public void Extract_GenericFallback_FindsHrefs()
    {
        var html = """
        <a href="https://generic-result.com/page1">Link 1</a>
        <a href="https://generic-result.com/page2">Link 2</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://unknown-search.com/?q=test");
        urls.Should().Contain("https://generic-result.com/page1");
        urls.Should().Contain("https://generic-result.com/page2");
    }

    [Fact]
    public void Extract_FiltersBlockedDomains()
    {
        var html = """
        <a href="https://duckduckgo.com/something">DDG internal</a>
        <a href="https://google.com/search?q=test">Google</a>
        <a href="https://example.com/valid">Valid</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://unknown.com/search");
        urls.Should().Contain("https://example.com/valid");
        urls.Should().NotContain(u => u.Contains("duckduckgo.com"));
        urls.Should().NotContain(u => u.Contains("google.com"));
    }

    [Fact]
    public void Extract_IgnoresJavascriptUrls()
    {
        var html = """
        <a href="javascript:void(0)">Invalid</a>
        <a href="https://valid.com/page">Valid</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://unknown.com/search");
        urls.Should().Contain("https://valid.com/page");
        urls.Should().NotContain(u => u.Contains("javascript"));
    }

    [Fact]
    public void Extract_EmptyHtml_ReturnsEmpty()
    {
        var urls = SearchResultExtractor.Extract("", "https://duckduckgo.com/html/?q=test");
        urls.Should().BeEmpty();
    }

    [Fact]
    public void Extract_NullHtml_Throws()
    {
        var act = () => SearchResultExtractor.Extract(null!, "https://html.duckduckgo.com/html/?q=test");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Extract_DeduplicatesUrls()
    {
        var html = """
        <a class="result__a" href="https://example.com/page">Same Link 1</a>
        <a class="result__a" href="https://example.com/page">Same Link 2</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Where(u => u == "https://example.com/page").Should().HaveCount(1);
    }

    // ---- New pattern tests ----

    [Fact]
    public void Extract_DuckDuckGo_DataResultPattern()
    {
        var html = """
        <a data-result="url" href="https://example.com/data-result">Data Result</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Should().Contain("https://example.com/data-result");
    }

    [Fact]
    public void Extract_DuckDuckGo_ResultLinkPattern()
    {
        var html = """
        <a class="result-link" href="https://example.com/result-link">Result Link</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Should().Contain("https://example.com/result-link");
    }

    [Fact]
    public void Extract_DuckDuckGo_ResultUrlSpan()
    {
        var html = """
        <span class="result__url"><a href="https://example.com/via-url-span">Link</a></span>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Should().Contain("https://example.com/via-url-span");
    }

    [Fact]
    public void Extract_Brave_SnippetTitlePattern()
    {
        var html = """
        <a class="snippet-title" href="https://brave-snippet.com/page">Snippet</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://search.brave.com/search?q=test");
        urls.Should().Contain("https://brave-snippet.com/page");
    }

    [Fact]
    public void Extract_Brave_ArticleLinkPattern()
    {
        var html = """
        <article><a href="https://brave-article.com/page">Article</a></article>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://search.brave.com/search?q=test");
        urls.Should().Contain("https://brave-article.com/page");
    }

    [Fact]
    public void Extract_Bing_DecodesBingRedirectUrls()
    {
        var html = """
        <li class="b_algo">
            <h2><a href="https://www.bing.com/ck/a?u=a1https%3A%2F%2Fexample.com%2Fbing-redirect&other=stuff">Bing Result</a></h2>
        </li>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://www.bing.com/search?q=test");
        urls.Should().Contain("https://example.com/bing-redirect");
    }

    [Fact]
    public void Extract_Bing_BTitlePattern()
    {
        var html = """
        <a class="b_title" href="https://bing-title.com/page">Title Link</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://www.bing.com/search?q=test");
        urls.Should().Contain("https://bing-title.com/page");
    }

    [Fact]
    public void Extract_Bing_TilkPattern()
    {
        var html = """
        <a class="tilk" href="https://bing-tilk.com/page">Tilk Link</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://www.bing.com/search?q=test");
        urls.Should().Contain("https://bing-tilk.com/page");
    }

    [Fact]
    public void Extract_EngineSpecificFallsBackToGenericWhenNoMatches()
    {
        // DDG patterns won't match but generic <a href> should
        var html = """
        <div class="unusual-layout">
            <a href="https://fallback-result.com/page">Fallback</a>
        </div>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Should().Contain("https://fallback-result.com/page");
    }

    [Fact]
    public void Extract_FiltersRedditSearchUrl()
    {
        var html = """
        <a href="https://reddit.com/search?q=test">Reddit Search</a>
        <a href="https://example.com/valid">Valid</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://unknown.com/search");
        urls.Should().Contain("https://example.com/valid");
        urls.Should().NotContain(u => u.Contains("reddit.com/search"));
    }

    [Fact]
    public void Extract_FiltersLinkedInSearchUrl()
    {
        var html = """
        <a href="https://linkedin.com/search/results/all/?keywords=test">LinkedIn Search</a>
        <a href="https://example.com/valid">Valid</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "https://unknown.com/search");
        urls.Should().Contain("https://example.com/valid");
        urls.Should().NotContain(u => u.Contains("linkedin.com/search"));
    }

    [Fact]
    public void Extract_LimitsTo10Results()
    {
        var html = string.Join("\n",
            Enumerable.Range(1, 20).Select(i =>
                $"""<a class="result__a" href="https://example.com/page{i}">Page {i}</a>"""));

        var urls = SearchResultExtractor.Extract(html, "https://html.duckduckgo.com/html/?q=test");
        urls.Should().HaveCountLessOrEqualTo(10);
    }

    // ---- Yahoo Search tests ----

    [Fact]
    public void Extract_Yahoo_DecodesRURedirectUrls()
    {
        var html = """
        <a href="https://r.search.yahoo.com/_ylt=xxx/RV=2/RE=123/RO=10/RU=https%3a%2f%2fexample.com%2fyahoo-result/RK=2/RS=abc">Result</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "yahoo");
        urls.Should().Contain("https://example.com/yahoo-result");
    }

    [Fact]
    public void Extract_Yahoo_MultipleResults()
    {
        var html = """
        <a href="https://r.search.yahoo.com/xxx/RU=https%3a%2f%2fsite1.com%2fpage/RK=0">R1</a>
        <a href="https://r.search.yahoo.com/xxx/RU=https%3a%2f%2fsite2.com%2fpage/RK=0">R2</a>
        <a href="https://r.search.yahoo.com/xxx/RU=https%3a%2f%2fsite3.com%2fpage/RK=0">R3</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "yahoo");
        urls.Should().HaveCountGreaterOrEqualTo(3);
        urls.Should().Contain("https://site1.com/page");
        urls.Should().Contain("https://site2.com/page");
        urls.Should().Contain("https://site3.com/page");
    }

    [Fact]
    public void Extract_Yahoo_FiltersYahooInternalUrls()
    {
        var html = """
        <a href="https://r.search.yahoo.com/xxx/RU=https%3a%2f%2fwww.yahoo.com%2f/RK=0">Yahoo Home</a>
        <a href="https://r.search.yahoo.com/xxx/RU=https%3a%2f%2fexample.com%2fgood/RK=0">Good</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "yahoo");
        urls.Should().Contain("https://example.com/good");
        urls.Should().NotContain(u => u.Contains("yahoo.com"));
    }

    // ---- Google Scholar tests ----

    [Fact]
    public void Extract_Scholar_FindsGsRtLinks()
    {
        var html = """
        <div class="gs_ri">
            <h3 class="gs_rt"><a href="https://academic.com/paper1">Paper Title</a></h3>
        </div>
        <div class="gs_ri">
            <h3 class="gs_rt"><a href="https://journal.org/paper2">Another Paper</a></h3>
        </div>
        """;

        var urls = SearchResultExtractor.Extract(html, "scholar");
        urls.Should().Contain("https://academic.com/paper1");
        urls.Should().Contain("https://journal.org/paper2");
    }

    [Fact]
    public void Extract_Scholar_FindsPdfLinks()
    {
        var html = """
        <div class="gs_or_ggsm"><a href="https://papers.org/doc.pdf">PDF</a></div>
        """;

        var urls = SearchResultExtractor.Extract(html, "scholar");
        urls.Should().Contain("https://papers.org/doc.pdf");
    }

    [Fact]
    public void Extract_Scholar_DataClkLinks()
    {
        var html = """
        <a data-clk="hl=en" href="https://research-site.com/article">Article</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "scholar");
        urls.Should().Contain("https://research-site.com/article");
    }

    // ---- CleanUrl tests for Yahoo redirects ----

    [Fact]
    public void Extract_Yahoo_CleanUrlDecodesYahooRedirect()
    {
        // Test via generic fallback with Yahoo redirect URL in an href
        var html = """
        <a href="https://r.search.yahoo.com/xxx/RU=https%3A%2F%2Fexample.com%2Ftest-page/RK=0">Link</a>
        """;

        var urls = SearchResultExtractor.Extract(html, "yahoo");
        urls.Should().Contain("https://example.com/test-page");
    }
}
