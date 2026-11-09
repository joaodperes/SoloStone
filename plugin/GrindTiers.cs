using System;
using System.Collections.Generic;
using System.Linq;

// Grind tiers (owner 2026-10-31, docs/design/2026-10-31-grind-tiers.md): one per-save progression pace, chosen when the save
// is created and changeable later. A tier scales per-match income (gold/dust, class XP), mastery points and the ladder pace; one-time
// grants (welcome gift, achievements, ladder milestones, rival gold, class rewards) and prices stay the same for every tier.
// Every settled match records the tier it was played under (LocalMatchRecord.GrindTier), so a change applies from the next
// match and never rewrites earned progress. Rows are append-only once a save can hold them: retune by adding a new id.
// Display hours are tools/grind_pace.py medians at MinutesPerGame; ladder figures stay in the design doc.
public sealed class GrindTier
{
    public string Id { get; }
    public string Name { get; }
    public string Summary { get; }
    public double RewardScale { get; }   // gold and dust of a match roll
    public double ClassXpScale { get; }  // class XP per match
    public int MasteryPointsPerWin { get; }
    public LadderPace Ladder { get; }
    public double HoursPerMetaDeck { get; } // median fresh-profile shop quote / recurring (gold/h + 0.5 * dust/h), no grants
    public int HoursToHalfCollection { get; }

    internal GrindTier(string id, string name, string summary, double rewardScale, double classXpScale, LadderPace ladder,
        double hoursPerMetaDeck, int hoursToHalfCollection, int masteryPointsPerWin)
    {
        Id = id; Name = name; Summary = summary; RewardScale = rewardScale; ClassXpScale = classXpScale; Ladder = ladder;
        HoursPerMetaDeck = hoursPerMetaDeck; HoursToHalfCollection = hoursToHalfCollection; MasteryPointsPerWin = masteryPointsPerWin;
    }

    public int Gold(int baseGold) => GrindTiers.Scale(baseGold, RewardScale);
    public int Dust(int baseDust) => GrindTiers.Scale(baseDust, RewardScale);
    public int ClassXp(int baseXp) => GrindTiers.Scale(baseXp, ClassXpScale);
}

// Display names (owner 2026-10-31): Dad-gamer, Normie, Grinder, Retail, fastest to slowest.
public static class GrindTiers
{
    public const string RelaxedId = "relaxed", StandardId = "standard", GrinderId = "grinder", RetailId = "retail";
    public const string Relaxed2xId = "relaxed-2x", Standard2xId = "standard-2x";
    public const string Relaxed6xId = "relaxed-6x";
    public const int MinutesPerGame = 10;

    public static readonly GrindTier Relaxed = new GrindTier(RelaxedId, "Dad-gamer",
        "Faster ladder and 1.5x match rewards and class XP.", 1.5, 1.5, new LadderPace(1.5, 2, 1.5), 10.4, 110, 6);
    public static readonly GrindTier Standard = new GrindTier(StandardId, "Normie",
        "Retail-shaped ladder, 1x match rewards and class XP.", 1, 1, LadderPace.Standard, 15.7, 167, 4);
    public static readonly GrindTier Relaxed2x = new GrindTier(Relaxed2xId, "Dad-gamer",
        "Faster ladder and 3x match rewards and class XP.", 3, 3, Relaxed.Ladder, 5.2, 55, 6);
    public static readonly GrindTier Standard2x = new GrindTier(Standard2xId, "Normie",
        "Retail-shaped ladder, 2x match rewards and class XP.", 2, 2, Standard.Ladder, 7.8, 87, 4);
    public static readonly GrindTier Grinder = new GrindTier(GrinderId, "Grinder",
        "A long road: no bonus stars, 0.75x match rewards and class XP.", 0.75, 0.75, new LadderPace(0, 1, 0.75), 20.8, 230, 3);

    public static readonly GrindTier Retail = new GrindTier(RetailId, "Retail",
        "0.4x match rewards and class XP: close to the live game's free-to-play income at about 2 h of play a day.",
        0.4, 0.4, LadderPace.Standard, 39.2, 441, 2);

    public static readonly GrindTier Relaxed6x = new GrindTier(Relaxed6xId, "Dad-gamer",
        "Faster ladder, 6x match rewards and 3x class XP.", 6, 3, Relaxed.Ladder, 2.6, 31, 6);

    public static readonly IReadOnlyList<GrindTier> All = Array.AsReadOnly(new[] { Relaxed, Standard, Relaxed2x, Standard2x, Grinder, Retail, Relaxed6x });
    public static readonly IReadOnlyList<GrindTier> Selectable = Array.AsReadOnly(new[] { Relaxed6x, Standard2x, Grinder, Retail });
    public static GrindTier Default => Standard2x;

    // Upgrade only the profile's current choice on load; historical and pending stamps keep their original rows.
    // Missing current tier predates pace choices and succeeds legacy Standard, just like an explicit standard id.
    public static GrindTier Successor(string id)
    {
        var tier = Find(id);
        return tier == Relaxed || tier == Relaxed2x ? Relaxed6x : tier == Standard ? Standard2x : tier;
    }

    // Absent (saves and receipts from before tiers) = Standard; an unknown id is a corrupt or future save.
    public static GrindTier Find(string id) => string.IsNullOrEmpty(id) ? Standard
        : All.SingleOrDefault(tier => tier.Id == id) ?? throw new InvalidOperationException("Unknown grind tier: " + id);

    public static bool IsKnown(string id) => All.Any(tier => tier.Id == id);

    // Whole units, half away from zero: 45 x 1.5 = 68, 45 x 0.75 = 34. Zero stays zero.
    internal static int Scale(int amount, double scale) =>
        amount == 0 ? 0 : checked((int)Math.Round(amount * scale, MidpointRounding.AwayFromZero));
}
