using ResearchHive.Core.Models;
using System.Text;

namespace ResearchHive.Core.Services;

/// <summary>
/// Generates a structured "CodeBook" — a concise architecture summary of an indexed repo.
/// Pulls the top architecture-relevant chunks from the vector store, sends them to the LLM,
/// and asks for a structured summary covering: purpose, architecture, key abstractions,
/// data flow, extension points, and build/run instructions.
/// </summary>
public class CodeBookGenerator
{
    private readonly RetrievalService _retrieval;
    private readonly LlmService _llm;

    private static readonly string[] ArchitectureQueries = new[]
    {
        "main entry point program startup initialization",
        "architecture modules services dependency injection",
        "data model schema database entities",
        "API endpoints routes controllers",
        "configuration settings environment",
        "build deploy dockerfile CI pipeline"
    };

    public CodeBookGenerator(RetrievalService retrieval, LlmService llm)
    {
        _retrieval = retrieval;
        _llm = llm;
    }

    /// <summary>
    /// Generate a CodeBook for the given repo profile.
    /// Assumes the repo has already been indexed (chunks exist in the session DB).
    /// Returns the Markdown CodeBook text.
    /// </summary>
    public async Task<string> GenerateAsync(string sessionId, RepoProfile profile, CancellationToken ct = default)
    {
        // Pull top architecture-relevant chunks via hybrid search
        var repoFilter = new[] { "repo_code", "repo_doc" };
        var allChunks = new List<RetrievalResult>();

        foreach (var q in ArchitectureQueries)
        {
            var hits = await _retrieval.HybridSearchAsync(sessionId, q, repoFilter, topK: 5, ct);
            allChunks.AddRange(hits);
        }

        // Deduplicate and take top 20 by score
        var topChunks = allChunks
            .DistinctBy(r => r.Chunk.Id)
            .OrderByDescending(r => r.Score)
            .Take(20)
            .ToList();

        if (topChunks.Count == 0)
            return $"# CodeBook: {profile.Owner}/{profile.Name}\n\n_No indexed code available._";

        // Build context block
        var context = new StringBuilder();
        context.AppendLine($"Repository: {profile.Owner}/{profile.Name}");
        context.AppendLine($"Description: {profile.Description}");
        context.AppendLine($"Primary Language: {profile.PrimaryLanguage}");
        context.AppendLine($"Frameworks: {string.Join(", ", profile.Frameworks)}");
        context.AppendLine($"Dependencies: {string.Join(", ", profile.Dependencies.Take(20).Select(d => d.Name))}");
        context.AppendLine();
        context.AppendLine("--- CODE EXCERPTS ---");
        context.AppendLine();

        foreach (var r in topChunks)
        {
            context.AppendLine(r.Chunk.Text);
            context.AppendLine("---");
        }

        var systemPrompt = @"You are a senior software architect. Given code excerpts from a repository,
produce a structured CodeBook — a concise architecture reference document.
Use Markdown. Include these sections:
1. **Purpose** — One paragraph describing what the project does.
2. **Architecture Overview** — Layers, modules, key patterns (MVC, CQRS, etc.)
3. **Key Abstractions** — Important classes, interfaces, traits with one-line descriptions.
4. **Data Flow** — How data moves through the system (request → service → storage).
5. **Extension Points** — How a developer would add a new feature or plugin.
6. **Build & Run** — Commands or steps to build and run the project.
7. **Notable Design Decisions** — Anything unusual or worth highlighting.

Be specific. Reference actual class/function names from the code excerpts.
Keep it under 1500 words. Do NOT repeat the code — summarize and explain.";

        var userPrompt = $@"Generate a CodeBook for this repository:

{context}

Produce the structured Markdown CodeBook now.";

        var response = await _llm.GenerateWithMetadataAsync(userPrompt, systemPrompt, maxTokens: 2000, ct: ct);
        return $"# CodeBook: {profile.Owner}/{profile.Name}\n\n{response.Text}";
    }
}
