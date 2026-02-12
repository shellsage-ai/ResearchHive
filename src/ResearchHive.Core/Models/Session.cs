using System.Text.Json.Serialization;

namespace ResearchHive.Core.Models;

public enum SessionStatus { Active, Paused, Completed, Archived }

public enum DomainPack
{
    GeneralResearch,
    HistoryPhilosophy,
    Math,
    MakerMaterials,
    ChemistrySafe,
    ProgrammingResearchIP
}

public static class EnumDisplayNames
{
    public static string ToDisplayName(this DomainPack pack) => pack switch
    {
        DomainPack.GeneralResearch => "General Research",
        DomainPack.HistoryPhilosophy => "History & Philosophy",
        DomainPack.Math => "Math",
        DomainPack.MakerMaterials => "Maker / Materials",
        DomainPack.ChemistrySafe => "Chemistry (Safe)",
        DomainPack.ProgrammingResearchIP => "Programming Research & IP",
        _ => pack.ToString()
    };

    public static string ToDisplayName(this SessionStatus status) => status switch
    {
        SessionStatus.Active => "Active",
        SessionStatus.Paused => "Paused",
        SessionStatus.Completed => "Completed",
        SessionStatus.Archived => "Archived",
        _ => status.ToString()
    };
}

public class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DomainPack Pack { get; set; } = DomainPack.GeneralResearch;
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string WorkspacePath { get; set; } = string.Empty;
    public string? LastReportSummary { get; set; }
    public string? LastReportPath { get; set; }
}
