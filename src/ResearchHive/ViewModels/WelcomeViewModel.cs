using CommunityToolkit.Mvvm.ComponentModel;

namespace ResearchHive.ViewModels;

public partial class WelcomeViewModel : ObservableObject
{
    public string WelcomeText => @"Welcome to ResearchHive — Agentic Research + Discovery Studio

Create or select a session from the sidebar to begin.

Features:
• Agentic Research — automated evidence gathering with citations
• Discovery Studio — idea generation with novelty checks
• Materials Explorer — property-based material search with safety labels
• Programming Research + IP — approach analysis with license awareness  
• Idea Fusion — combine research outputs with provenance tracking

All research is session-scoped with immutable evidence snapshots.";
}
