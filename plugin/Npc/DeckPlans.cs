using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;

public static partial class DeckPlans
{
    // Class-only priors, not inferred opponent decklists. Unknown classes use Midrange in consumers.
    private static IReadOnlyList<MatchupClasses> DefaultMatchups => Array.AsReadOnly(new[] {
        // The vendor enum predates Demon Hunter; the client/source class id is 14.
        new MatchupClasses(Matchup.Aggro, CardClass.HUNTER, (CardClass)14, CardClass.SHAMAN),
        new MatchupClasses(Matchup.Midrange, CardClass.WARRIOR, CardClass.PALADIN, CardClass.ROGUE, CardClass.DEATHKNIGHT),
        new MatchupClasses(Matchup.Control, CardClass.MAGE, CardClass.PRIEST, CardClass.WARLOCK, CardClass.DRUID)
    });
    public static IReadOnlyList<DeckPlan> All => Array.AsReadOnly(new[] { DragonWarrior, PurePaladin, ZeeShaman });
    // Seam diagnostics (NPC World): a throwing hook is logged here and treated as "no plan" (legacy search, empty
    // mulligan) so a bad world deck can never abort match start. Hosts may point SeamLog at their log.
    public static Action<string> SeamLog { get; set; }
    public static string LastSeamError { get; private set; }
    // Static hand-written plans first; NPC World derived/overlaid plans ("auto:<deckId>") only through the seam.
    public static DeckPlan ForId(string planId)
    {
        var plan = All.SingleOrDefault(p => p.Id == planId);
        if (plan != null) return plan;
        var hook = NpcWorldHooks.PlanForId;
        if (hook == null) return null;
        try { return hook(planId); }
        catch (Exception error)
        {
            Fail("DeckPlans.ForId(" + planId + ") hook failed: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }
    internal static void Fail(string message)
    {
        LastSeamError = message;
        try { SeamLog?.Invoke(message); } catch (Exception) { }
    }
}
