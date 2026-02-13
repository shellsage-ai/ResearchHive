using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResearchHive.Core.Services;
using ResearchHive.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace ResearchHive.ViewModels;

public partial class SessionWorkspaceViewModel
{
    // ---- Project Discovery Commands ----

    [RelayCommand]
    private async Task DiscoverySearchAsync()
    {
        if (string.IsNullOrWhiteSpace(DiscoveryQuery)) return;
        IsDiscoverySearching = true;
        DiscoveryStatus = "Searching GitHub…";
        DiscoveryResults.Clear();
        try
        {
            var results = await _discoveryService.SearchAsync(
                DiscoveryQuery.Trim(),
                string.IsNullOrWhiteSpace(DiscoveryLanguageFilter) ? null : DiscoveryLanguageFilter.Trim(),
                DiscoveryMinStars);

            foreach (var r in results)
                DiscoveryResults.Add(new DiscoveryResultViewModel(r));

            DiscoveryStatus = results.Count > 0
                ? $"Found {results.Count} repositories"
                : "No repositories matched your search";
        }
        catch (Exception ex)
        {
            DiscoveryStatus = $"Search error: {ex.Message}";
        }
        finally
        {
            IsDiscoverySearching = false;
        }
    }

    /// <summary>Scan all selected discovery results (one-click batch scan).</summary>
    [RelayCommand]
    private async Task DiscoveryScanSelectedAsync()
    {
        var selected = DiscoveryResults.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;

        IsRepoScanning = true;
        RepoScanStatus = $"Scanning {selected.Count} discovered repo(s)…";
        try
        {
            int done = 0;
            foreach (var item in selected)
            {
                done++;
                RepoScanStatus = $"Scanning {done}/{selected.Count}: {item.FullName}…";
                await _repoRunner.RunAnalysisAsync(_sessionId, item.HtmlUrl);
                _notificationService.NotifyRepoScanComplete(item.HtmlUrl);
                item.IsSelected = false;
            }

            RepoScanStatus = $"Scanned {selected.Count} discovered repo(s).";
            LoadSessionData();
        }
        catch (Exception ex)
        {
            RepoScanStatus = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsRepoScanning = false;
        }
    }

    /// <summary>Copy a discovery result URL to clipboard for manual use.</summary>
    [RelayCommand]
    private void CopyDiscoveryUrl(DiscoveryResultViewModel? vm)
    {
        if (vm == null) return;
        Clipboard.SetText(vm.HtmlUrl);
    }

    /// <summary>Select/deselect all visible discovery results.</summary>
    [RelayCommand]
    private void DiscoverySelectAll()
    {
        bool shouldSelect = !DiscoveryResults.All(r => r.IsSelected);
        foreach (var r in DiscoveryResults)
            r.IsSelected = shouldSelect;
    }
}
