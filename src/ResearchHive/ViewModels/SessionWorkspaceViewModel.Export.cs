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
    private void CopyTextToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        System.Windows.Clipboard.SetText(text);
    }

    // ---- Export Commands ----
    [RelayCommand]
    private async Task ExportSessionAsync()
    {
        try
        {
            ResearchStatus = "Exporting session archive…";
            var outputPath = Path.Combine(Session.WorkspacePath, "Exports");
            Directory.CreateDirectory(outputPath);
            var zipPath = await Task.Run(() => _exportService.ExportSessionToZip(_sessionId, outputPath));
            ResearchStatus = $"Exported to: {zipPath}";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportReportAsHtmlAsync()
    {
        if (SelectedReport == null) { ResearchStatus = "Select a report first"; return; }
        try
        {
            var outputPath = Path.Combine(Session.WorkspacePath, "Exports");
            Directory.CreateDirectory(outputPath);
            var filePath = await _exportService.ExportReportAsHtmlAsync(_sessionId, SelectedReport.Report.Id, outputPath);
            ResearchStatus = $"HTML exported: {filePath}";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"HTML export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportResearchPacketAsync()
    {
        try
        {
            var outputPath = Path.Combine(Session.WorkspacePath, "Exports");
            Directory.CreateDirectory(outputPath);
            var packetFolder = await _exportService.ExportResearchPacketAsync(_sessionId, outputPath);
            ResearchStatus = $"Research Packet exported: {packetFolder}";

            // Open the index.html directly in the default browser
            var indexPath = Path.Combine(packetFolder, "index.html");
            if (File.Exists(indexPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = indexPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Packet export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        if (Directory.Exists(Session.WorkspacePath))
        {
            System.Diagnostics.Process.Start("explorer.exe", Session.WorkspacePath);
        }
    }


    private void ViewLogs()
    {
        var logPath = Path.Combine(Session.WorkspacePath, "Logs");
        if (Directory.Exists(logPath))
        {
            var logFiles = Directory.GetFiles(logPath, "*.jsonl");
            if (logFiles.Any())
                LogContent = File.ReadAllText(logFiles.First());
            else
                LogContent = "No log files yet.";
        }
        else
        {
            LogContent = "Log directory not found.";
        }
    }
}
