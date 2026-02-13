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
    [RelayCommand]
    private void SortSnapshots(string mode)
    {
        SnapshotSortMode = mode;
        var sorted = mode switch
        {
            "Oldest" => Snapshots.OrderBy(s => s.Snapshot.CapturedUtc).ToList(),
            "A-Z" => Snapshots.OrderBy(s => s.Snapshot.Title ?? s.Snapshot.Url).ToList(),
            _ => Snapshots.OrderByDescending(s => s.Snapshot.CapturedUtc).ToList() // Newest
        };
        Snapshots.Clear();
        foreach (var s in sorted) Snapshots.Add(s);
    }

    [RelayCommand]
    private void SortEvidence(string mode)
    {
        EvidenceSortMode = mode;
        var sorted = mode switch
        {
            "A-Z" => EvidenceResults.OrderBy(e => e.Text).ToList(),
            "Source" => EvidenceResults.OrderBy(e => e.SourceId).ToList(),
            _ => EvidenceResults.OrderByDescending(e => e.Score).ToList() // Score
        };
        EvidenceResults.Clear();
        foreach (var e in sorted) EvidenceResults.Add(e);
    }

    [RelayCommand]
    private async Task CaptureSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(SnapshotUrl)) return;
        try
        {
            var snapshot = await _snapshotService.CaptureUrlAsync(_sessionId, SnapshotUrl);
            if (!snapshot.IsBlocked)
            {
                await _indexService.IndexSnapshotAsync(_sessionId, snapshot);
            }
            Snapshots.Insert(0, new SnapshotViewModel(snapshot));
            SnapshotUrl = "";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Snapshot error: {ex.Message}";
        }
    }

    partial void OnIsStreamlinedCodexChanged(bool value)
    {
        _appSettings.StreamlinedCodexMode = value;
    }

    partial void OnIsSourceQualityOnChanged(bool value)
    {
        _appSettings.SourceQualityRanking = value;
    }

    partial void OnSelectedTimeRangeChanged(string value)
    {
        _appSettings.SearchTimeRange = value switch
        {
            "Past year" => "year",
            "Past month" => "month",
            "Past week" => "week",
            "Past day" => "day",
            _ => "any"
        };
    }

    partial void OnSelectedSnapshotChanged(SnapshotViewModel? value)
    {
        // Fire-and-forget async load — never block the UI thread with file I/O
        _ = LoadSnapshotContentAsync(value);
    }

    private async Task LoadSnapshotContentAsync(SnapshotViewModel? value)
    {
        try
        {
            if (value != null && File.Exists(value.Snapshot.TextPath))
            {
                SnapshotViewerContent = await File.ReadAllTextAsync(value.Snapshot.TextPath);
            }
            else if (value != null && File.Exists(value.Snapshot.HtmlPath))
            {
                var html = await File.ReadAllTextAsync(value.Snapshot.HtmlPath);
                SnapshotViewerContent = await Task.Run(() => SnapshotService.ExtractReadableText(html));
            }
            else
            {
                SnapshotViewerContent = value?.Snapshot.IsBlocked == true
                    ? $"Blocked: {value.Snapshot.BlockReason}"
                    : "No content available";
            }
        }
        catch (Exception ex)
        {
            SnapshotViewerContent = $"Error loading snapshot: {ex.Message}";
        }
    }

    // ---- OCR / Capture Commands ----
    [RelayCommand]
    private async Task CaptureScreenshotAsync()
    {
        if (string.IsNullOrWhiteSpace(CaptureImagePath) || !File.Exists(CaptureImagePath)) return;
        try
        {
            var capture = await _ocrService.CaptureScreenshotAsync(_sessionId, CaptureImagePath, "Manual capture");
            await _indexService.IndexCaptureAsync(_sessionId, capture);
            Captures.Insert(0, new CaptureViewModel(capture));
            CaptureImagePath = "";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"OCR error: {ex.Message}";
        }
    }

    // ---- Search/Evidence Commands ----
    [RelayCommand]
    private async Task SearchEvidenceAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        try
        {
            var results = await _retrievalService.HybridSearchAsync(_sessionId, SearchQuery);
            var db = _sessionManager.GetSessionDb(_sessionId);

            // Build a cache of SourceId → URL to avoid repeated lookups
            var urlCache = new Dictionary<string, string>();

            EvidenceResults.Clear();
            foreach (var r in results)
            {
                string sourceUrl = "";
                if (!urlCache.TryGetValue(r.SourceId, out sourceUrl!))
                {
                    var snapshot = db.GetSnapshot(r.SourceId);
                    sourceUrl = snapshot?.Url ?? "";
                    urlCache[r.SourceId] = sourceUrl;
                }

                EvidenceResults.Add(new EvidenceItemViewModel
                {
                    SourceId = r.SourceId,
                    SourceType = r.SourceType,
                    Score = r.Score,
                    Text = r.Chunk.Text,
                    ChunkId = r.Chunk.Id,
                    SourceUrl = sourceUrl
                });
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Search error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PinEvidence(EvidenceItemViewModel? item)
    {
        if (item != null && !PinnedEvidence.Contains(item))
        {
            PinnedEvidence.Add(item);
            var db = _sessionManager.GetSessionDb(_sessionId);
            db.SavePinnedEvidence(new PinnedEvidence
            {
                SessionId = _sessionId, ChunkId = item.ChunkId, SourceId = item.SourceId,
                SourceType = item.SourceType, Text = item.Text, Score = item.Score,
                SourceUrl = item.SourceUrl
            });
            StatPins = PinnedEvidence.Count;
        }
    }

    [RelayCommand]
    private void UnpinEvidence(EvidenceItemViewModel? item)
    {
        if (item != null)
        {
            PinnedEvidence.Remove(item);
            // Remove from DB by matching chunk ID
            var db = _sessionManager.GetSessionDb(_sessionId);
            var persisted = db.GetPinnedEvidence().FirstOrDefault(p => p.ChunkId == item.ChunkId);
            if (persisted != null)
                db.DeletePinnedEvidence(persisted.Id);
            StatPins = PinnedEvidence.Count;
        }
    }
}
