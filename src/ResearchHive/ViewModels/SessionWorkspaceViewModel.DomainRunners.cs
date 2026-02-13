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
    // ---- Discovery Studio Commands ----
    [RelayCommand]
    private async Task RunDiscoveryAsync()
    {
        if (string.IsNullOrWhiteSpace(DiscoveryProblem)) return;
        IsDiscoveryRunning = true;
        try
        {
            var job = await _discoveryRunner.RunAsync(_sessionId, DiscoveryProblem, DiscoveryConstraints);
            LoadSessionData();
            _notificationService.NotifyDiscoveryComplete(Session.Title ?? "Discovery", IdeaCards.Count);
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Discovery error: {ex.Message}";
        }
        finally
        {
            IsDiscoveryRunning = false;
        }
    }

    // ---- Materials Explorer Commands ----
    [RelayCommand]
    private async Task RunMaterialsSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(MaterialProperties)) return;
        IsMaterialsRunning = true;
        try
        {
            var query = new MaterialsQuery();
            foreach (var prop in MaterialProperties.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = prop.Split(':', '=');
                if (parts.Length >= 2)
                    query.PropertyTargets[parts[0].Trim()] = parts[1].Trim();
            }
            if (!string.IsNullOrWhiteSpace(MaterialFilters))
            {
                foreach (var f in MaterialFilters.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = f.Split(':', '=');
                    if (parts.Length >= 2)
                        query.Filters[parts[0].Trim()] = parts[1].Trim();
                }
            }
            if (!string.IsNullOrWhiteSpace(MaterialAvoid))
                query.AvoidMaterials = MaterialAvoid.Split(',').Select(s => s.Trim()).ToList();
            if (!string.IsNullOrWhiteSpace(MaterialInclude))
                query.IncludeMaterials = MaterialInclude.Split(',').Select(s => s.Trim()).ToList();

            var job = await _materialsRunner.RunAsync(_sessionId, query);
            LoadSessionData();
            BuildMaterialComparisonTable();
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Materials error: {ex.Message}";
        }
        finally
        {
            IsMaterialsRunning = false;
        }
    }

    // ---- Programming Research Commands ----
    [RelayCommand]
    private async Task RunProgrammingResearchAsync()
    {
        if (string.IsNullOrWhiteSpace(ProgrammingProblem)) return;
        IsProgrammingRunning = true;
        try
        {
            var job = await _programmingRunner.RunAsync(_sessionId, ProgrammingProblem);
            ProgrammingReport = job.FullReport ?? "";
            LoadSessionData();
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Programming error: {ex.Message}";
        }
        finally
        {
            IsProgrammingRunning = false;
        }
    }

    // ---- Fusion Commands ----

    partial void OnSelectedFusionTemplateChanged(FusionPromptTemplate? value)
    {
        if (value == null) return;
        FusionPrompt = value.Prompt;
        FusionMode = value.Mode;
    }

    [RelayCommand]
    private async Task RunFusionAsync()
    {
        if (string.IsNullOrWhiteSpace(FusionPrompt)) return;
        IsFusionRunning = true;
        try
        {
            var request = new FusionRequest
            {
                SessionId = _sessionId,
                Prompt = FusionPrompt,
                Mode = FusionMode
            };
            var job = await _fusionRunner.RunAsync(_sessionId, request);
            LoadSessionData();
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Fusion error: {ex.Message}";
        }
        finally
        {
            IsFusionRunning = false;
        }
    }


    // ===== Materials Property Comparison Table =====
    private void BuildMaterialComparisonTable()
    {
        if (MaterialCandidates.Count < 2)
        {
            MaterialComparisonTable = "";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Property Comparison\n");

        // Collect all property keys across all candidates
        var allKeys = MaterialCandidates
            .SelectMany(c => c.Candidate.Properties.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        // Header row
        sb.Append("| Property |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.Name} |");
        sb.AppendLine();

        // Separator
        sb.Append("|----------|");
        foreach (var _ in MaterialCandidates)
            sb.Append("----------|");
        sb.AppendLine();

        // Property rows
        foreach (var key in allKeys)
        {
            sb.Append($"| **{key}** |");
            foreach (var c in MaterialCandidates)
            {
                var val = c.Candidate.Properties.TryGetValue(key, out var v) ? v : "—";
                sb.Append($" {val} |");
            }
            sb.AppendLine();
        }

        // Summary rows
        sb.Append("| **Fit Score** |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.FitScore} |");
        sb.AppendLine();

        sb.Append("| **Safety** |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.Safety} |");
        sb.AppendLine();

        sb.Append("| **DIY** |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.Candidate.DiyFeasibility} |");
        sb.AppendLine();

        MaterialComparisonTable = sb.ToString();
    }
}
