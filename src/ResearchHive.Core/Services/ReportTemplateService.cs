using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Defines report templates with per-section prompts and token budgets.
/// Enables section-by-section generation for longer, more detailed reports.
/// </summary>
public class ReportTemplateService
{
    /// <summary>
    /// Gets the default research report template. Sections are generated independently
    /// and concatenated. Each section receives its own targeted evidence retrieval.
    /// </summary>
    public static ReportTemplate GetResearchTemplate(DomainPack pack)
    {
        var sections = new List<TemplateSection>
        {
            new("Key Findings",
                "Write 5-8 major findings as a bulleted list. Each bullet must include an inline citation [N]. " +
                "**Bold** the key term or concept in each bullet point. " +
                "Be specific with data points, numbers, and concrete examples. No filler.",
                500),

            new("Most Supported View",
                "Start with a > blockquote containing a single-sentence key takeaway. " +
                "Then write the primary, evidence-weighted analysis in 3-5 paragraphs. " +
                "Explain WHY the evidence supports this view, not just WHAT the evidence says. " +
                "Include specific data, statistics, and source comparisons. Cite every claim. " +
                "**Bold** critical conclusions and important terms on first mention.",
                800),

            new("Detailed Analysis",
                "Write a topic-by-topic deep dive organized around the sub-questions. For each topic: " +
                "state the finding with data, cite the specific evidence, compare sources where they agree " +
                "or disagree, and note the strength of the evidence. Be thorough and detailed. " +
                "Use a comparison table (| Feature | Option A | Option B |) when comparing 3+ items. " +
                "**Bold** key terms. Use `code formatting` for technical names and commands.",
                1200),

            new("Credible Alternatives / Broader Views",
                "Present alternative interpretations and competing viewpoints with citations. " +
                "Explain why the most-supported view is favored over these alternatives. " +
                "Include any minority positions that have credible evidence. " +
                "**Bold** the name of each alternative approach or viewpoint.",
                600),
        };

        // Domain-specific sections
        switch (pack)
        {
            case DomainPack.ChemistrySafe:
                sections.Add(new TemplateSection(
                    "Safety & Handling",
                    "Cover required PPE, safe handling procedures, disposal protocols, known health hazards, " +
                    "exposure limits, and emergency procedures. Cite regulatory sources (OSHA, SDS data, GHS classifications).",
                    500));
                break;

            case DomainPack.MakerMaterials:
                sections.Add(new TemplateSection(
                    "Practical Considerations",
                    "Cover material availability, cost, processing requirements, equipment needed, " +
                    "common failure modes, and practical tips from maker community experience.",
                    500));
                break;

            case DomainPack.ProgrammingResearchIP:
                sections.Add(new TemplateSection(
                    "Patent & IP Landscape",
                    "Describe any relevant patents, their status (active/expired), key claims, " +
                    "and open-source implementations with license types. Note freedom-to-operate considerations.",
                    500));
                break;

            default:
                // General/History/Math — no extra domain section
                break;
        }

        // Common closing sections for all packs
        sections.Add(new TemplateSection(
            "Limitations",
            "What would change the conclusion? Note evidence gaps, methodological caveats, " +
            "conflicting data, areas needing further research, and any biases in the available evidence.",
            300));

        sections.Add(new TemplateSection(
            "Sources",
            "List every cited source with format: [N] Title — URL. " +
            "Use the URLs provided alongside each evidence chunk. Number sequentially.",
            300));

        return new ReportTemplate
        {
            Name = "Research Report",
            Sections = sections
        };
    }
}

/// <summary>
/// A report template defining sections for sequential generation.
/// </summary>
public class ReportTemplate
{
    public string Name { get; set; } = "";
    public List<TemplateSection> Sections { get; set; } = new();

    /// <summary>Total target tokens across all sections.</summary>
    public int TotalTargetTokens => Sections.Sum(s => s.TargetTokens);
}

/// <summary>
/// A single section in a report template with heading, instruction, and token budget.
/// </summary>
public class TemplateSection
{
    public string Heading { get; set; }
    public string Instruction { get; set; }
    public int TargetTokens { get; set; }

    public TemplateSection(string heading, string instruction, int targetTokens)
    {
        Heading = heading;
        Instruction = instruction;
        TargetTokens = targetTokens;
    }
}
