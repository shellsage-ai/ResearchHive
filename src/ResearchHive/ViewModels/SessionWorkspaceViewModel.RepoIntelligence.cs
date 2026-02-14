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
    // ---- Repo Intelligence Commands ----

    [RelayCommand]
    private async Task ScanRepoAsync()
    {
        if (string.IsNullOrWhiteSpace(RepoUrl)) return;
        IsRepoScanning = true;
        RepoScanStatus = "Scanning repository…";
        _repoScanCts = new CancellationTokenSource();
        try
        {
            await _repoRunner.RunAnalysisAsync(_sessionId, RepoUrl.Trim(), _repoScanCts.Token);
            RepoScanStatus = "Scan complete.";
            _notificationService.NotifyRepoScanComplete(RepoUrl.Trim());
            RepoUrl = "";
            LoadSessionData();
        }
        catch (OperationCanceledException)
        {
            RepoScanStatus = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            RepoScanStatus = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsRepoScanning = false;
            _repoScanCts = null;
        }
    }

    [RelayCommand]
    private async Task ScanMultiRepoAsync()
    {
        if (string.IsNullOrWhiteSpace(RepoUrlList))
        {
            RepoScanStatus = "Enter one or more URLs or paths to scan.";
            return;
        }
        var urls = RepoUrlList.Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        if (urls.Count == 0)
        {
            RepoScanStatus = "No valid URLs found — enter one per line.";
            return;
        }

        IsRepoScanning = true;
        RepoScanStatus = $"Scanning {urls.Count} repo(s)…";
        _repoScanCts = new CancellationTokenSource();
        int succeeded = 0;
        var failures = new List<string>();
        try
        {
            for (int i = 0; i < urls.Count; i++)
            {
                _repoScanCts.Token.ThrowIfCancellationRequested();
                RepoScanStatus = $"Scanning {urls[i]} ({i + 1}/{urls.Count})…";
                try
                {
                    await _repoRunner.RunAnalysisAsync(_sessionId, urls[i], _repoScanCts.Token);
                    succeeded++;
                    _notificationService.NotifyRepoScanComplete(urls[i]);
                }
                catch (OperationCanceledException) { throw; } // propagate cancellation
                catch (Exception ex)
                {
                    failures.Add($"{urls[i]}: {ex.Message}");
                    RepoScanStatus = $"Error on {urls[i]} — continuing with remaining repos…";
                }
            }

            if (failures.Count == 0)
            {
                RepoScanStatus = $"All {urls.Count} repo(s) scanned successfully.";
            }
            else
            {
                RepoScanStatus = $"Batch complete: {succeeded}/{urls.Count} succeeded, {failures.Count} failed.\n" +
                                 string.Join("\n", failures);
            }
            RepoUrlList = "";
            LoadSessionData();
        }
        catch (OperationCanceledException)
        {
            RepoScanStatus = $"Batch scan cancelled ({succeeded}/{urls.Count} completed).";
            LoadSessionData(); // load any already-completed scans
        }
        catch (Exception ex)
        {
            RepoScanStatus = $"Multi-scan error: {ex.Message}";
            LoadSessionData(); // load any partial results
        }
        finally
        {
            IsRepoScanning = false;
            _repoScanCts = null;
        }
    }

    [RelayCommand]
    private void CancelRepoScan()
    {
        if (!_dialogService.Confirm("Are you sure you want to cancel the running scan?", "Cancel Scan"))
            return;
        _repoScanCts?.Cancel();
        RepoScanStatus = "Cancelling…";
    }

    [RelayCommand]
    private void DeleteRepoProfile(RepoProfileViewModel? vm)
    {
        if (vm == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteRepoProfile(vm.Profile.Id);
        RepoProfiles.Remove(vm);
        RefreshFusionInputOptions();
    }

    [RelayCommand]
    private async Task AskAboutRepoAsync()
    {
        if (string.IsNullOrWhiteSpace(RepoAskUrl) || string.IsNullOrWhiteSpace(RepoAskQuestion)) return;
        IsRepoAsking = true;
        RepoAskAnswer = "";
        var questionText = RepoAskQuestion;
        var urlText = RepoAskUrl.Trim();
        try
        {
            var answer = await _repoRunner.AskAboutRepoAsync(_sessionId, urlText, questionText);
            RepoAskAnswer = answer;
            RepoQaHistory.Insert(0, new RepoQaMessageViewModel
            {
                RepoUrl = urlText,
                Question = questionText,
                Answer = answer
            });
            RepoAskQuestion = "";
            // Refresh profiles in case a new scan happened
            LoadSessionData();
        }
        catch (Exception ex)
        {
            RepoAskAnswer = $"Error: {ex.Message}";
        }
        finally
        {
            IsRepoAsking = false;
        }
    }

    // ---- Project Fusion Commands ----

    partial void OnSelectedProjectFusionTemplateChanged(ProjectFusionTemplate? value)
    {
        if (value == null) return;
        ProjectFusionFocus = value.Prompt;
        ProjectFusionGoal = value.Goal;
    }

    [RelayCommand]
    private async Task RunProjectFusionAsync()
    {
        var selectedInputs = FusionInputOptions.Where(o => o.IsSelected).ToList();
        if (selectedInputs.Count < 1) { RepoScanStatus = "Select at least one input."; return; }

        IsProjectFusing = true;
        RepoScanStatus = "Running project fusion…";
        _fusionCts = new CancellationTokenSource();
        try
        {
            var request = new ProjectFusionRequest
            {
                SessionId = _sessionId,
                Goal = ProjectFusionGoal,
                FocusPrompt = ProjectFusionFocus,
                Inputs = selectedInputs.Select(o => new ProjectFusionInput
                {
                    Id = o.Id,
                    Type = o.InputType,
                    Title = o.Title
                }).ToList()
            };
            await _projectFusionEngine.RunAsync(_sessionId, request, _fusionCts.Token);
            RepoScanStatus = "Project fusion complete.";
            LoadSessionData();
        }
        catch (OperationCanceledException)
        {
            RepoScanStatus = "Fusion cancelled.";
        }
        catch (Exception ex)
        {
            RepoScanStatus = $"Fusion error: {ex.Message}";
        }
        finally
        {
            IsProjectFusing = false;
            _fusionCts = null;
        }
    }

    [RelayCommand]
    private void CancelProjectFusion()
    {
        if (!_dialogService.Confirm("Are you sure you want to cancel the running fusion?", "Cancel Fusion"))
            return;
        _fusionCts?.Cancel();
        RepoScanStatus = "Cancelling fusion…";
    }

    [RelayCommand]
    private void DeleteProjectFusion(ProjectFusionArtifactViewModel? vm)
    {
        if (vm == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteProjectFusion(vm.Artifact.Id);
        ProjectFusions.Remove(vm);
        RefreshFusionInputOptions();
    }



    // ---- Copy to Clipboard Commands ----
    [RelayCommand]
    private void CopyRepoProfile(RepoProfileViewModel? vm)
    {
        if (vm == null) return;
        System.Windows.Clipboard.SetText(vm.FullProfileText);
        RepoScanStatus = "Profile copied to clipboard.";
    }

    [RelayCommand]
    private void CopyProjectFusion(ProjectFusionArtifactViewModel? vm)
    {
        if (vm == null) return;
        System.Windows.Clipboard.SetText(vm.FullFusionText);
        RepoScanStatus = "Fusion artifact copied to clipboard.";
    }

    [RelayCommand]
    private void CopyRepoQa(RepoQaMessageViewModel? vm)
    {
        if (vm == null) return;
        System.Windows.Clipboard.SetText($"Q: {vm.Question}\n\nA: {vm.Answer}");
        RepoScanStatus = "Q&A copied to clipboard.";
    }

}
