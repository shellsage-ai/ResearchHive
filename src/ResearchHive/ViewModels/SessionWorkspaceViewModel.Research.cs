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
    // ---- Research Commands ----
    [RelayCommand]
    private async Task RunResearchAsync()
    {
        if (string.IsNullOrWhiteSpace(ResearchPrompt)) return;
        IsResearchRunning = true;
        IsResearchComplete = false;
        ShowLiveProgress = true;
        ResearchStatus = "Starting research...";
        _jobCts = new CancellationTokenSource();

        // Clear live progress
        LiveLogLines.Clear();
        SourceHealthItems.Clear();
        ProgressStep = "Initializing…";
        ProgressSourcesFound = 0;
        ProgressSourcesFailed = 0;
        ProgressTargetSources = TargetSources;
        ProgressCoverage = 0;
        ProgressCoverageDisplay = "0%";
        ProgressIteration = 0;
        ProgressMaxIterations = 0;

        // Subscribe to progress
        _researchRunner.ProgressChanged += OnResearchProgress;

        try
        {
            var job = await _researchRunner.RunAsync(_sessionId, ResearchPrompt, JobType.Research, TargetSources, _jobCts.Token);
            ResearchStatus = $"Completed: {job.State}";
            LoadSessionData();

            // Auto-select the latest full report so the user sees the new results immediately
            var latestFullReport = Reports
                .Where(r => r.Report.ReportType == "full")
                .OrderByDescending(r => r.Report.CreatedUtc)
                .FirstOrDefault();
            if (latestFullReport != null)
            {
                SelectedReport = latestFullReport;
                ActiveTab = "Reports";
            }

            // Show discoverability hints after successful research
            if (job.State == JobState.Completed && Snapshots.Count > 0)
            {
                ShowPostResearchTips();
                _notificationService.NotifyResearchComplete(
                    Session.Title ?? "Research",
                    ResearchPrompt,
                    job.AcquiredSourceIds.Count);
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Error: {ex.Message}";
        }
        finally
        {
            _researchRunner.ProgressChanged -= OnResearchProgress;
            IsResearchRunning = false;
            IsResearchComplete = true;
            IterationDisplay = ProgressIteration > 0
                ? $"Done in {ProgressIteration} iteration{(ProgressIteration > 1 ? "s" : "")}"
                : "";
            // ShowLiveProgress stays true — user can still see final stats & log
        }
    }

    private void OnResearchProgress(object? sender, JobProgressEventArgs e)
    {
        // Must dispatch to UI thread
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            ProgressStep = e.StepDescription;
            ProgressSourcesFound = e.SourcesFound;
            ProgressSourcesFailed = e.SourcesFailed;
            ProgressTargetSources = e.TargetSources;
            ProgressCoverage = e.CoverageScore;
            ProgressCoverageDisplay = $"{e.CoverageScore:P0}";
            ProgressIteration = e.CurrentIteration;
            ProgressMaxIterations = e.MaxIterations;
            IterationDisplay = e.CurrentIteration > 0
                ? $"Iteration {e.CurrentIteration} / {e.MaxIterations}"
                : "";
            ResearchStatus = e.StepDescription;

            // C1: Enhanced progress fields
            SubQuestionsTotal = e.SubQuestionsTotal;
            SubQuestionsAnswered = e.SubQuestionsAnswered;
            GroundingScore = e.GroundingScore;
            GroundingScoreDisplay = e.SubQuestionsTotal > 0 || e.GroundingScore > 0
                ? $"{e.GroundingScore:P0}" : "—";
            SubQuestionStatus = e.SubQuestionsTotal > 0 ? $"{e.SubQuestionsAnswered}/{e.SubQuestionsTotal}" : "";
            BrowserPoolAvailable = e.BrowserPoolAvailable;
            BrowserPoolTotal = e.BrowserPoolTotal;

            if (!string.IsNullOrEmpty(e.LogMessage))
            {
                LiveLogLines.Add($"[{DateTime.Now:HH:mm:ss}] {e.LogMessage}");
                // Keep log manageable
                while (LiveLogLines.Count > 200)
                    LiveLogLines.RemoveAt(0);
            }

            // Update source health
            if (e.SourceHealth?.Count > 0)
            {
                SourceHealthItems.Clear();
                foreach (var sh in e.SourceHealth)
                    SourceHealthItems.Add(new SourceHealthViewModel(sh));
            }

            // Update search engine health
            if (e.SearchEngineHealth?.Count > 0)
            {
                SearchEngineHealthItems.Clear();
                foreach (var eh in e.SearchEngineHealth)
                    SearchEngineHealthItems.Add(new SearchEngineHealthViewModel(eh));
            }
        });
    }

    [RelayCommand]
    private void CancelResearch()
    {
        if (!_dialogService.Confirm("Are you sure you want to cancel the running research?", "Cancel Research"))
            return;

        if (SelectedJob != null)
        {
            _researchRunner.CancelJob(_sessionId, SelectedJob.Job.Id);
        }
        _jobCts?.Cancel();
        ResearchStatus = "Cancelled";
    }

    [RelayCommand]
    private void PauseResearch()
    {
        if (SelectedJob != null)
        {
            _researchRunner.PauseJob(_sessionId, SelectedJob.Job.Id);
            ResearchStatus = "Paused";
            ProgressStep = "Paused";
            LoadSessionData();
        }
    }

    [RelayCommand]
    private async Task ResumeResearchAsync()
    {
        if (SelectedJob == null) return;
        IsResearchRunning = true;
        IsResearchComplete = false;
        ShowLiveProgress = true;
        _jobCts = new CancellationTokenSource();
        ProgressStep = "Resuming…";
        _researchRunner.ProgressChanged += OnResearchProgress;

        try
        {
            var job = await _researchRunner.ResumeAsync(_sessionId, SelectedJob.Job.Id, _jobCts.Token);
            ResearchStatus = $"Completed: {job?.State}";
            LoadSessionData();

            // Auto-select the latest full report
            var latestResume = Reports
                .Where(r => r.Report.ReportType == "full")
                .OrderByDescending(r => r.Report.CreatedUtc)
                .FirstOrDefault();
            if (latestResume != null)
            {
                SelectedReport = latestResume;
                ActiveTab = "Reports";
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Error: {ex.Message}";
        }
        finally
        {
            _researchRunner.ProgressChanged -= OnResearchProgress;
            IsResearchRunning = false;
            IsResearchComplete = true;
        }
    }

    // ---- Snapshot Commands ----

    private void ShowPostResearchTips()
    {
        var tips = new List<string>();
        tips.Add("Research complete! Here's what you can do next:");
        tips.Add("  • Evidence tab — Search across all indexed content with hybrid BM25 + semantic search");
        tips.Add("  • Reports tab — View synthesized final and interim reports with citations");

        // Check which tabs are visible to suggest appropriate features
        var tabTags = VisibleTabs.Select(t => t.Tag).ToHashSet();
        if (tabTags.Contains("Discovery"))
            tips.Add("  • Discovery Studio — Generate novel hypotheses and cross-domain ideas");
        if (tabTags.Contains("Fusion"))
            tips.Add("  • Idea Fusion — Blend or contrast findings across different angles");
        if (tabTags.Contains("Artifacts"))
            tips.Add("  • Artifacts — Review auto-generated tables, formulas, and code snippets");
        if (tabTags.Contains("Programming"))
            tips.Add("  • Programming IP — Analyze patents and prior art for technical approaches");
        if (tabTags.Contains("Materials"))
            tips.Add("  • Materials Explorer — Search for candidate materials matching your criteria");

        PostResearchTip = string.Join("\n", tips);
        HasPostResearchTip = true;
    }

    [RelayCommand]
    private void DismissTip()
    {
        HasPostResearchTip = false;
        PostResearchTip = "";
    }
}
