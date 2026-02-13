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
    // ---- Delete Commands (remove clutter) ----

    [RelayCommand]
    private void DeleteJob(JobViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete job \"{item.Title}\" and all its reports, citations, and data?", "Delete Job"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteJob(item.Job.Id);
        LoadSessionData();
        ResearchStatus = "Job deleted.";
    }

    [RelayCommand]
    private void DeleteReport(ReportViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete report \"{item.Title}\"?", "Delete Report"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteReport(item.Report.Id);
        // Also remove the file on disk
        if (File.Exists(item.Report.FilePath))
            try { File.Delete(item.Report.FilePath); } catch { }
        Reports.Remove(item);
        if (SelectedReport == item) { SelectedReport = null; ReportContent = ""; }
        ResearchStatus = "Report deleted.";
    }

    [RelayCommand]
    private void DeleteSnapshot(SnapshotViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete snapshot \"{item.Title}\"?", "Delete Snapshot"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteSnapshot(item.Snapshot.Id);
        // Remove files on disk
        foreach (var path in new[] { item.Snapshot.HtmlPath, item.Snapshot.TextPath, item.Snapshot.BundlePath })
        {
            if (File.Exists(path)) try { File.Delete(path); } catch { }
        }
        Snapshots.Remove(item);
        ResearchStatus = "Snapshot deleted.";
    }

    [RelayCommand]
    private void DeleteNotebookEntry(NotebookEntryViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete note \"{item.Title}\"?", "Delete Note"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteNotebookEntry(item.Entry.Id);
        NotebookEntries.Remove(item);
        ResearchStatus = "Note deleted.";
    }

    [RelayCommand]
    private void DeleteIdeaCard(IdeaCardViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteIdeaCard(item.Card.Id);
        IdeaCards.Remove(item);
        ResearchStatus = "Idea card removed.";
    }

    [RelayCommand]
    private void DeleteMaterialCandidate(MaterialCandidateViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteMaterialCandidate(item.Candidate.Id);
        MaterialCandidates.Remove(item);
        ResearchStatus = "Material removed.";
    }

    [RelayCommand]
    private void DeleteFusionResult(FusionResultViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteFusionResult(item.Result.Id);
        FusionResults.Remove(item);
        ResearchStatus = "Fusion result removed.";
    }

    [RelayCommand]
    private void DeleteArtifact(ArtifactViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete artifact \"{item.Name}\"?", "Delete Artifact"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteArtifact(item.Artifact.Id);
        // Remove stored file
        if (File.Exists(item.Artifact.StorePath))
            try { File.Delete(item.Artifact.StorePath); } catch { }
        Artifacts.Remove(item);
        ResearchStatus = "Artifact deleted.";
    }

    [RelayCommand]
    private void DeleteCapture(CaptureViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteCapture(item.Capture.Id);
        Captures.Remove(item);
        ResearchStatus = "Capture deleted.";
    }

    // ---- Report Commands ----
    partial void OnSelectedReportChanged(ReportViewModel? value)
    {
        ReportContent = value?.Report.Content ?? "";

        // Load replay entries for the report's job
        if (value != null)
        {
            ReplayEntries.Clear();
            var db = _sessionManager.GetSessionDb(_sessionId);
            var job = db.GetJob(value.Report.JobId);
            if (job != null)
            {
                foreach (var re in job.ReplayEntries)
                    ReplayEntries.Add(new ReplayEntryViewModel(re));
            }
        }
    }

    // ---- Artifact Ingestion ----
    [RelayCommand]
    private async Task IngestFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            var artifact = _artifactStore.IngestFile(_sessionId, filePath);
            await _indexService.IndexArtifactAsync(_sessionId, artifact);
            Artifacts.Insert(0, new ArtifactViewModel(artifact));
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Ingest error: {ex.Message}";
        }
    }

    /// <summary>
    /// Handle files dropped via drag-and-drop.
    /// </summary>
    public async Task HandleDroppedFilesAsync(string[] filePaths)
    {
        int ingested = 0;
        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                await IngestFileAsync(path);
                ingested++;
            }
        }
        if (ingested > 0)
        {
            ResearchStatus = $"Ingested {ingested} file(s) via drag-and-drop";
            ActiveTab = "Artifacts";
        }
    }

}
