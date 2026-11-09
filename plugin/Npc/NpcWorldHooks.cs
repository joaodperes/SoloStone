using System;
using System.Collections.Generic;

// NPC World v1 seams (docs/superpowers/specs/2026-10-25-npc-world-v1-design.md).
// Lives in plugin/Npc so every project that links plugin/Npc/*.cs compiles it; it may reference only
// types those projects already have (GoldenContracts, BotDriver). Null hooks keep today's behaviour exactly.
// World code (plugin/Npc/World) installs the hooks at startup; the seams below call them and nothing else.
public static class NpcWorldHooks
{
    // Seam: NpcCatalog.ForDeck. The active encounter's synthesized profile for this deck id, else null.
    public static Func<string, NpcProfile> ProfileForDeck { get; set; }

    // Seam: DeckPlans.ForId. A derived (AutoDeckPlan) or overlaid plan for a plan id the static catalog lacks.
    public static Func<string, DeckPlan> PlanForId { get; set; }

    // Seam: NpcPilots.Create. Wraps a constructed golden pilot with the encounter's skill (mistakes); may return it unchanged.
    public static Func<NpcProfile, IGoldenPilot, IGoldenPilot> DecoratePilot { get; set; }

    // Seam: NativeMatch bot mulligan (two call sites). Skill-aware policy for this profile/plan, else null = DeckPlanMulligan.
    public static Func<NpcProfile, DeckPlan, MulliganPolicy> MulliganFor { get; set; }

    // Seam: DeckInference. The public NPC deck population (admitted library lists) for fair deck inference, else null
    // = the CardFrequencies decks; DeckInference adds the Core starter lists to either. Read only by profiles whose
    // BrainOptions ask for the inferred reply prior.
    public static Func<IReadOnlyList<PopulationDeck>> Population { get; set; }

    // Clears every hook (tests, shutdown).
    public static void Reset()
    {
        ProfileForDeck = null; PlanForId = null; DecoratePilot = null; MulliganFor = null; Population = null;
    }
}
