using ResearchHive.Core.Services;

Console.WriteLine("=== Full Search Engine Test (Playwright + Google UndetectedChromeDriver) ===\n");

var query = "quantum computing applications 2024";
int totalUrls = 0;

// ── Playwright engines (Bing, Yahoo, Scholar, Brave) ──
var service = new BrowserSearchService();
foreach (var (name, template) in BrowserSearchService.SearchEngines)
{
    Console.Write($"[{name}] Searching... ");
    try
    {
        var results = await service.SearchAsync(query, name, template);
        totalUrls += results.Count;
        Console.WriteLine($"Found {results.Count} URLs");
        foreach (var url in results.Take(3))
            Console.WriteLine($"  -> {url}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
    }
    Console.WriteLine();
}

// ── Google via UndetectedChromeDriver ──
Console.Write("[google] Searching via UndetectedChromeDriver... ");
using var googleService = new GoogleSearchService();
try
{
    var googleResults = await googleService.SearchAsync(query);
    totalUrls += googleResults.Count;
    Console.WriteLine($"Found {googleResults.Count} URLs");
    foreach (var url in googleResults.Take(3))
        Console.WriteLine($"  -> {url}");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
}

Console.WriteLine($"\nTOTAL: {totalUrls} URLs across all engines (incl. Google)");
await service.DisposeAsync();
