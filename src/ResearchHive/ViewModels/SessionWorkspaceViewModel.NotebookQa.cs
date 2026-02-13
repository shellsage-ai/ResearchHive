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
    // ---- Notebook Commands ----
    [RelayCommand]
    private void AddNote()
    {
        if (string.IsNullOrWhiteSpace(NewNoteTitle)) return;
        var note = new NotebookEntry
        {
            SessionId = _sessionId,
            Title = NewNoteTitle,
            Content = NewNoteContent
        };
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.SaveNotebookEntry(note);
        NotebookEntries.Insert(0, new NotebookEntryViewModel(note));
        NewNoteTitle = "";
        NewNoteContent = "";
    }

    // ---- Q&A Commands ----
    [RelayCommand]
    private async Task AskFollowUpAsync()
    {
        var question = QaQuestion?.Trim();
        if (string.IsNullOrEmpty(question) || IsQaRunning) return;

        IsQaRunning = true;
        QaQuestion = "";

        var msg = new QaMessageViewModel { Question = question, Answer = "Thinking…" };
        QaMessages.Add(msg);

        try
        {
            // Build context based on scope
            string context;
            if (QaScope == "session")
            {
                var results = await _retrievalService.HybridSearchAsync(_sessionId, question, 10, CancellationToken.None);
                context = string.Join("\n\n", results.Select(r => r.Chunk.Text));
            }
            else
            {
                // Scope is a report ID — search that report's content
                var report = Reports.FirstOrDefault(r => r.Report.Id == QaScope);
                if (report != null)
                {
                    var results = await _retrievalService.SearchReportContentAsync(
                        report.Report.Content, question, 5, CancellationToken.None);
                    context = string.Join("\n\n", results.Select(r => r.Chunk.Text));
                }
                else
                {
                    context = "(No report content found for this scope.)";
                }
            }

            if (string.IsNullOrWhiteSpace(context))
                context = "(No relevant context found.)";

            var prompt = $"Answer the following question using ONLY the provided context.\n\n" +
                         $"Context:\n{context}\n\nQuestion: {question}\n\nAnswer:";

            var answer = await _llmService.GenerateAsync(prompt, ct: CancellationToken.None);
            msg.Answer = string.IsNullOrWhiteSpace(answer) ? "No answer could be generated." : answer.Trim();

            // Persist to DB
            var db = _sessionManager.GetSessionDb(_sessionId);
            db.SaveQaMessage(new QaMessage
            {
                SessionId = _sessionId, Question = question,
                Answer = msg.Answer, Scope = QaScope,
                ModelUsed = _llmService.LastModelUsed
            });
        }
        catch (Exception ex)
        {
            msg.Answer = $"Error: {ex.Message}";
        }
        finally
        {
            IsQaRunning = false;
        }
    }
}
