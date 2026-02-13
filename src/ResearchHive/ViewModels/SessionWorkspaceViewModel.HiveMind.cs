using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using ResearchHive.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ResearchHive.ViewModels;

public partial class SessionWorkspaceViewModel
{
    // ===== Cross-Session Search =====
    [RelayCommand]
    private async Task SearchGlobalAsync()
    {
        if (string.IsNullOrWhiteSpace(GlobalSearchQuery)) return;
        IsGlobalSearchRunning = true;
        GlobalSearchStatus = "Searching across all sessions...";
        try
        {
            if (GlobalSearchReports)
            {
                var reportResults = await Task.Run(() => _crossSearch.SearchReports(GlobalSearchQuery));
                Application.Current.Dispatcher.Invoke(() =>
                {
                    GlobalSearchResults.Clear();
                    GlobalReportResults.Clear();
                    foreach (var r in reportResults)
                        GlobalReportResults.Add(new CrossSessionReportResultViewModel(r));
                    GlobalSearchStatus = $"Found {reportResults.Count} report match(es) across sessions";
                });
            }
            else
            {
                var results = await Task.Run(() => _crossSearch.SearchAll(GlobalSearchQuery));
                Application.Current.Dispatcher.Invoke(() =>
                {
                    GlobalSearchResults.Clear();
                    GlobalReportResults.Clear();
                    foreach (var r in results)
                        GlobalSearchResults.Add(new CrossSessionResultViewModel(r));
                    GlobalSearchStatus = $"Found {results.Count} evidence result(s) across sessions";
                });
            }
        }
        catch (Exception ex)
        {
            GlobalSearchStatus = $"Search failed: {ex.Message}";
        }
        finally { IsGlobalSearchRunning = false; }
    }

    [RelayCommand]
    private async Task LoadGlobalStatsAsync()
    {
        try
        {
            var stats = await Task.Run(() => _crossSearch.GetGlobalStats());
            var domainSummary = string.Join(", ", stats.SessionsByDomain.Select(kv => $"{kv.Key}: {kv.Value}"));
            GlobalStatsText = $"📊 {stats.TotalSessions} sessions • {stats.TotalEvidence:N0} evidence chunks • " +
                              $"{stats.TotalReports} reports • {stats.TotalSnapshots} snapshots\n" +
                              $"Domains: {domainSummary}";
        }
        catch (Exception ex)
        {
            GlobalStatsText = $"Stats unavailable: {ex.Message}";
        }
    }

    // ===== Hive Mind =====

    [RelayCommand]
    private async Task AskHiveMindAsync()
    {
        if (string.IsNullOrWhiteSpace(HiveMindQuestion)) return;
        IsHiveMindBusy = true;
        HiveMindStatus = "Searching Hive Mind knowledge base...";
        HiveMindAnswer = "";
        try
        {
            var answer = await Task.Run(() =>
                _globalMemory.AskHiveMindAsync(HiveMindQuestion, MemoryScope.HiveMind, domainPackFilter: null));
            Application.Current.Dispatcher.Invoke(() =>
            {
                HiveMindAnswer = answer;
                HiveMindStatus = "Answer generated from cross-session knowledge";
            });
        }
        catch (Exception ex)
        {
            HiveMindStatus = $"Hive Mind error: {ex.Message}";
        }
        finally { IsHiveMindBusy = false; }
    }

    [RelayCommand]
    private async Task PromoteSessionAsync()
    {
        IsHiveMindBusy = true;
        HiveMindStatus = "Promoting session knowledge to Hive Mind...";
        try
        {
            var session = Session;
            await Task.Run(() => _globalMemory.PromoteSessionChunks(_sessionId, session.Pack.ToString()));
            var stats = _globalMemory.GetStats();
            HiveMindStatus = $"✅ Session promoted! Hive Mind now has {stats.TotalChunks} chunks, {stats.StrategyCount} strategies";
            HiveMindStatsText = $"🧠 {stats.TotalChunks} total chunks • {stats.StrategyCount} strategies";
        }
        catch (Exception ex)
        {
            HiveMindStatus = $"Promote failed: {ex.Message}";
        }
        finally { IsHiveMindBusy = false; }
    }

    [RelayCommand]
    private void LoadHiveMindStats()
    {
        try
        {
            var stats = _globalMemory.GetStats();
            HiveMindStatsText = $"🧠 {stats.TotalChunks} total chunks • {stats.StrategyCount} strategies";
        }
        catch (Exception ex)
        {
            HiveMindStatsText = $"Stats unavailable: {ex.Message}";
        }
    }

    // ===== Hive Mind Curation =====

    [RelayCommand]
    private void BrowseHiveMindChunks()
    {
        HiveMindPageIndex = 0;
        LoadHiveMindChunksPage();
    }

    [RelayCommand]
    private void HiveMindNextPage()
    {
        HiveMindPageIndex++;
        LoadHiveMindChunksPage();
    }

    [RelayCommand]
    private void HiveMindPrevPage()
    {
        if (HiveMindPageIndex > 0) HiveMindPageIndex--;
        LoadHiveMindChunksPage();
    }

    [RelayCommand]
    private void DeleteHiveMindChunk(GlobalChunkViewModel? vm)
    {
        if (vm == null) return;
        if (!_dialogService.Confirm($"Delete this {vm.SourceType} chunk from Hive Mind?", "Delete Chunk"))
            return;
        _globalMemory.DeleteChunk(vm.Id);
        HiveMindChunks.Remove(vm);
        HiveMindStatus = "Chunk deleted from Hive Mind.";
        var stats = _globalMemory.GetStats();
        HiveMindStatsText = $"🧠 {stats.TotalChunks} total chunks • {stats.StrategyCount} strategies";
    }

    private const int HiveMindPageSize = 50;

    private void LoadHiveMindChunksPage()
    {
        try
        {
            var filter = string.IsNullOrWhiteSpace(HiveMindSourceTypeFilter) ? null : HiveMindSourceTypeFilter;
            var chunks = _globalMemory.BrowseChunks(
                offset: HiveMindPageIndex * HiveMindPageSize,
                limit: HiveMindPageSize + 1,  // fetch one extra to detect next page
                sourceTypeFilter: filter);

            HiveMindHasMorePages = chunks.Count > HiveMindPageSize;
            var pageChunks = chunks.Take(HiveMindPageSize).ToList();

            HiveMindChunks.Clear();
            foreach (var c in pageChunks)
                HiveMindChunks.Add(new GlobalChunkViewModel(c));

            HiveMindStatus = $"Showing {pageChunks.Count} chunks (page {HiveMindPageIndex + 1})";
        }
        catch (Exception ex)
        {
            HiveMindStatus = $"Browse failed: {ex.Message}";
        }
    }

}
