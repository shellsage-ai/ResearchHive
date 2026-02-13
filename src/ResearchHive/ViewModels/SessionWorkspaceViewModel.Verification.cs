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
    // ===== Citation Verification =====
    [RelayCommand]
    private async Task VerifyCitationsAsync()
    {
        if (SelectedReport == null) return;
        IsCitationVerifying = true;
        VerificationSummaryText = "Verifying citations (quick mode)...";
        try
        {
            var jobId = SelectedJob?.Job.Id ?? "";
            var verifications = await Task.Run(() =>
                _citationVerifier.VerifyReportQuick(_sessionId, SelectedReport.Report.Content ?? "", jobId));
            var summary = CitationVerificationService.Summarize(verifications);
            Application.Current.Dispatcher.Invoke(() =>
            {
                CitationVerifications.Clear();
                foreach (var v in verifications)
                    CitationVerifications.Add(new CitationVerificationViewModel(v));
                VerificationSummaryText = summary.StatusLabel;
            });
        }
        catch (Exception ex)
        {
            VerificationSummaryText = $"Verification failed: {ex.Message}";
        }
        finally { IsCitationVerifying = false; }
    }

    [RelayCommand]
    private async Task DeepVerifyCitationsAsync()
    {
        if (SelectedReport == null) return;
        IsDeepVerifying = true;
        VerificationSummaryText = "Deep-verifying citations with LLM...";
        try
        {
            var jobId = SelectedJob?.Job.Id ?? "";
            var verifications = await _citationVerifier.VerifyReportAsync(
                _sessionId, SelectedReport.Report.Content ?? "", jobId);
            var summary = CitationVerificationService.Summarize(verifications);
            Application.Current.Dispatcher.Invoke(() =>
            {
                CitationVerifications.Clear();
                foreach (var v in verifications)
                    CitationVerifications.Add(new CitationVerificationViewModel(v));
                VerificationSummaryText = $"[Deep] {summary.StatusLabel}";
            });
        }
        catch (Exception ex)
        {
            VerificationSummaryText = $"Deep verification failed: {ex.Message}";
        }
        finally { IsDeepVerifying = false; }
    }

    // ===== Contradiction Detection =====
    [RelayCommand]
    private async Task DetectContradictionsAsync()
    {
        IsContradictionRunning = true;
        ContradictionStatus = "Scanning evidence for contradictions (fast mode)...";
        try
        {
            var results = await Task.Run(() => _contradictionDetector.DetectQuick(_sessionId));
            Application.Current.Dispatcher.Invoke(() =>
            {
                Contradictions.Clear();
                foreach (var c in results)
                    Contradictions.Add(new ContradictionViewModel(c));
                ContradictionStatus = results.Count > 0
                    ? $"Found {results.Count} potential contradiction(s)"
                    : "No contradictions detected — evidence is consistent";
            });
        }
        catch (Exception ex)
        {
            ContradictionStatus = $"Detection failed: {ex.Message}";
        }
        finally { IsContradictionRunning = false; }
    }

    [RelayCommand]
    private async Task DeepDetectContradictionsAsync()
    {
        IsDeepContradictionRunning = true;
        ContradictionStatus = "Deep-scanning with embedding similarity + LLM verification...";
        try
        {
            _jobCts = new CancellationTokenSource();
            var results = await _contradictionDetector.DetectAsync(_sessionId, ct: _jobCts.Token);
            if (results.Count > 0)
            {
                var verified = await _contradictionDetector.VerifyWithLlmAsync(_sessionId, results, _jobCts.Token);
                results = verified;
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                Contradictions.Clear();
                foreach (var c in results)
                    Contradictions.Add(new ContradictionViewModel(c));
                var llmCount = results.Count(c => c.LlmVerified);
                ContradictionStatus = results.Count > 0
                    ? $"[Deep] Found {results.Count} contradiction(s), {llmCount} LLM-confirmed"
                    : "No contradictions detected — evidence is consistent";
            });
        }
        catch (Exception ex)
        {
            ContradictionStatus = $"Deep detection failed: {ex.Message}";
        }
        finally { IsDeepContradictionRunning = false; }
    }


    // ===== Incremental Research (Continue) =====
    [RelayCommand]
    private async Task ContinueResearchAsync()
    {
        if (SelectedJob == null) return;
        IsContinueRunning = true;
        ResearchStatus = "Continuing research with additional sources...";
        try
        {
            _jobCts = new CancellationTokenSource();
            await _researchRunner.ContinueResearchAsync(
                _sessionId,
                SelectedJob.Job.Id,
                string.IsNullOrWhiteSpace(ContinuePrompt) ? null : ContinuePrompt,
                AdditionalSources);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResearchStatus = "Research continued — new report generated";
                LoadSessionData();
            });
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Continue failed: {ex.Message}";
        }
        finally { IsContinueRunning = false; }
    }

    // ===== Research Comparison =====
    [RelayCommand]
    private async Task CompareResearchAsync()
    {
        if (SelectedCompareA?.Job == null || SelectedCompareB?.Job == null) return;
        IsComparing = true;
        ComparisonResultMarkdown = "Comparing research runs...";
        try
        {
            var comparison = await Task.Run(() =>
                _comparisonService.CompareInSession(_sessionId, SelectedCompareA.Job.Id, SelectedCompareB.Job.Id));
            Application.Current.Dispatcher.Invoke(() =>
            {
                ComparisonResultMarkdown = comparison.SummaryMarkdown;
            });
        }
        catch (Exception ex)
        {
            ComparisonResultMarkdown = $"Comparison failed: {ex.Message}";
        }
        finally { IsComparing = false; }
    }
}
