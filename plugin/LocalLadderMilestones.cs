using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class MilestoneGrant
{
    public string Kind { get; }
    public string Ref { get; }
    public int Count { get; }

    internal MilestoneGrant(string kind, string reference, int count)
    {
        Kind = kind; Ref = reference; Count = count;
    }
}

internal sealed class MilestoneDefinition
{
    public string Id { get; }
    public string Kind { get; }
    public int Threshold { get; }
    public bool IsLeague { get; }
    public IReadOnlyList<MilestoneGrant> Grants { get; }

    internal MilestoneDefinition(string id, string kind, int threshold, bool isLeague, IEnumerable<MilestoneGrant> grants)
    {
        Id = id; Kind = kind; Threshold = threshold; IsLeague = isLeague;
        Grants = Array.AsReadOnly(grants.ToArray());
    }
}

internal sealed class MilestoneTierReward
{
    public string MilestoneId { get; }
    public bool FirstTime => true;
    public string OpponentNpcId { get; }
    public string OpponentName { get; }
    public IReadOnlyList<MilestoneGrant> Grants { get; }

    internal MilestoneTierReward(string milestoneId, string opponentNpcId, string opponentName, IEnumerable<MilestoneGrant> grants)
    {
        MilestoneId = milestoneId; OpponentNpcId = opponentNpcId; OpponentName = opponentName;
        Grants = Array.AsReadOnly(grants.ToArray());
    }
}

internal interface IMilestoneContext
{
    string OpponentNpcId { get; }
    string OpponentName { get; }
    string ResolveOpponentRecipe();
    IReadOnlyList<string> ChallengersFor(string milestoneId);
    bool HasLegendDeckSnapshot { get; }
}

internal sealed class EmptyMilestoneContext : IMilestoneContext
{
    internal static readonly EmptyMilestoneContext Instance = new EmptyMilestoneContext();
    private EmptyMilestoneContext() { }
    public string OpponentNpcId => null;
    public string OpponentName => null;
    public string ResolveOpponentRecipe() => null;
    public IReadOnlyList<string> ChallengersFor(string milestoneId) => Array.Empty<string>();
    public bool HasLegendDeckSnapshot => false;
}

internal sealed class MilestoneCatalog
{
    public int Version { get; }
    public int DeckKitGoldCap { get; }
    public IReadOnlyList<MilestoneDefinition> Milestones { get; }

    internal MilestoneCatalog(int version, int deckKitGoldCap, IEnumerable<MilestoneDefinition> milestones)
    {
        Version = version; DeckKitGoldCap = deckKitGoldCap;
        Milestones = Array.AsReadOnly(milestones.ToArray());
    }

    // Compiled mirror of data/ladder/milestones-v1.json; checked field-for-field by LadderTests.
    internal static readonly MilestoneCatalog V1 = CreateV1();

    private static MilestoneCatalog CreateV1()
    {
        var catalog = new MilestoneCatalog(1, 3200, new[] {
            new MilestoneDefinition("floor:bronze-5", "StarIndex", 15, false, new[] {
                new MilestoneGrant("Pack", "STANDARD", 2) }),
            new MilestoneDefinition("floor:silver-10", "StarIndex", 30, true, new[] {
                new MilestoneGrant("Pack", "STANDARD", 3), new MilestoneGrant("Token", "EPIC", 1),
                new MilestoneGrant("CardBack", "cardback:ladder-silver", 1),
                new MilestoneGrant("Recipe", "opponent", 1), new MilestoneGrant("Challengers", "band", 1) }),
            new MilestoneDefinition("floor:silver-5", "StarIndex", 45, false, new[] {
                new MilestoneGrant("Pack", "STANDARD", 2) }),
            new MilestoneDefinition("floor:gold-10", "StarIndex", 60, true, new[] {
                new MilestoneGrant("Pack", "STANDARD", 3), new MilestoneGrant("DeckKit", "kit:ladder-gold", 1),
                new MilestoneGrant("CardBack", "cardback:ladder-gold", 1),
                new MilestoneGrant("Recipe", "opponent", 1), new MilestoneGrant("Challengers", "band", 1) }),
            new MilestoneDefinition("floor:gold-5", "StarIndex", 75, false, new[] {
                new MilestoneGrant("Pack", "STANDARD", 3) }),
            new MilestoneDefinition("floor:platinum-10", "StarIndex", 90, true, new[] {
                new MilestoneGrant("Pack", "STANDARD", 4), new MilestoneGrant("Token", "LEGENDARY", 1),
                new MilestoneGrant("CardBack", "cardback:ladder-platinum", 1),
                new MilestoneGrant("Recipe", "opponent", 1), new MilestoneGrant("Challengers", "band", 1) }),
            new MilestoneDefinition("floor:platinum-5", "StarIndex", 105, false, new[] {
                new MilestoneGrant("Pack", "STANDARD", 3) }),
            new MilestoneDefinition("floor:diamond-10", "StarIndex", 120, true, new[] {
                new MilestoneGrant("Pack", "STANDARD", 4), new MilestoneGrant("Token", "LEGENDARY", 1),
                new MilestoneGrant("CardBack", "cardback:ladder-diamond", 1),
                new MilestoneGrant("Recipe", "opponent", 1), new MilestoneGrant("Challengers", "band", 1) }),
            new MilestoneDefinition("floor:diamond-5", "StarIndex", 135, false, new[] {
                new MilestoneGrant("Pack", "STANDARD", 2), new MilestoneGrant("Token", "LEGENDARY", 1),
                new MilestoneGrant("Challengers", "band", 1) }),
            new MilestoneDefinition("legend", "StarIndex", 150, true, new[] {
                new MilestoneGrant("CardBack", "cardback:ladder-legend", 1), new MilestoneGrant("Token", "LEGENDARY", 1),
                new MilestoneGrant("Trophy", "trophy:legend", 1), new MilestoneGrant("LegendDeck", "Snapshot", 1),
                new MilestoneGrant("Recipe", "opponent", 1), new MilestoneGrant("Challengers", "band", 1) }),
            new MilestoneDefinition("legend:top-100", "LegendNumber", 100, false, new[] {
                new MilestoneGrant("CardBack", "cardback:ladder-elite", 1) }),
            new MilestoneDefinition("legend:1", "LegendNumber", 1, false, new[] {
                new MilestoneGrant("CardBack", "cardback:ladder-champion", 1), new MilestoneGrant("Trophy", "trophy:legend-1", 1) })
        });
        LocalLadderMilestones.Validate(catalog);
        return catalog;
    }
}

internal static class LocalLadderMilestones
{
    internal static IReadOnlyList<MilestoneTierReward> Evaluate(int beforeStarIndex, int afterStarIndex,
        int? legendNumberAfter, IReadOnlyCollection<string> earned, MilestoneCatalog catalog, IMilestoneContext context)
    {
        if (beforeStarIndex < 0) throw new ArgumentOutOfRangeException(nameof(beforeStarIndex));
        if (afterStarIndex < 0) throw new ArgumentOutOfRangeException(nameof(afterStarIndex));
        if (legendNumberAfter.HasValue && legendNumberAfter.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(legendNumberAfter));
        Validate(catalog);
        if (earned == null) throw new ArgumentNullException(nameof(earned));
        var earnedIds = new HashSet<string>(earned, StringComparer.Ordinal);
        context = context ?? EmptyMilestoneContext.Instance;
        var rewards = new List<MilestoneTierReward>();
        foreach (var milestone in catalog.Milestones)
        {
            // Floors pay only when this match crosses them; floors already passed unpaid belong to catch-up.
            // Legend numbers move with NPC ratings too, so they are checked on any outcome.
            bool reached = milestone.Kind == "StarIndex"
                ? beforeStarIndex < milestone.Threshold && afterStarIndex >= milestone.Threshold
                : afterStarIndex >= 150 && legendNumberAfter.HasValue && legendNumberAfter.Value <= milestone.Threshold;
            if (reached && !earnedIds.Contains(milestone.Id)) rewards.Add(Resolve(milestone, context, false));
        }
        return rewards.AsReadOnly();
    }

    // A creation cheat's starting position: every floor at or below it and every Legend-number milestone it already
    // holds, resolved like catch-up (no opponent recipe, no Challengers, Legacy Legend deck).
    internal static IReadOnlyList<MilestoneTierReward> EvaluateCreation(int starIndex, int legendNumber, MilestoneCatalog catalog)
    {
        if (starIndex < 0) throw new ArgumentOutOfRangeException(nameof(starIndex));
        if (legendNumber <= 0) throw new ArgumentOutOfRangeException(nameof(legendNumber));
        Validate(catalog);
        return Array.AsReadOnly(catalog.Milestones.Where(m => m.Kind == "StarIndex" ? m.Threshold <= starIndex
            : starIndex >= 150 && legendNumber <= m.Threshold).Select(m => Resolve(m, EmptyMilestoneContext.Instance, true)).ToArray());
    }

    internal static IReadOnlyList<MilestoneTierReward> EvaluateCatchUp(int bestStarIndex,
        IReadOnlyCollection<string> earned, MilestoneCatalog catalog)
    {
        if (bestStarIndex < 0) throw new ArgumentOutOfRangeException(nameof(bestStarIndex));
        Validate(catalog);
        if (earned == null) throw new ArgumentNullException(nameof(earned));
        var earnedIds = new HashSet<string>(earned, StringComparer.Ordinal);
        return Array.AsReadOnly(catalog.Milestones.Where(m => m.Kind == "StarIndex" && m.Threshold <= bestStarIndex
            && !earnedIds.Contains(m.Id)).Select(m => Resolve(m, EmptyMilestoneContext.Instance, true)).ToArray());
    }

    private static MilestoneTierReward Resolve(MilestoneDefinition milestone, IMilestoneContext context, bool catchUp)
    {
        var grants = new List<MilestoneGrant>();
        foreach (var grant in milestone.Grants)
        {
            switch (grant.Kind)
            {
                case "Recipe":
                    if (catchUp) break;
                    string recipe = context.ResolveOpponentRecipe();
                    if (recipe != null) grants.Add(new MilestoneGrant("Recipe", recipe, grant.Count));
                    break;
                case "Challengers":
                    if (catchUp) break;
                    foreach (string npc in context.ChallengersFor(milestone.Id))
                        grants.Add(new MilestoneGrant("Challengers", npc, grant.Count));
                    break;
                case "LegendDeck":
                    // Legacy: profiles that reached Legend before snapshots existed, or a winning deck that can no
                    // longer be resolved. Never block settlement over it; Legacy is the broader claim.
                    grants.Add(new MilestoneGrant("LegendDeck", !catchUp && context.HasLegendDeckSnapshot ? "Snapshot" : "Legacy", grant.Count));
                    break;
                default:
                    grants.Add(grant);
                    break;
            }
        }
        return new MilestoneTierReward(milestone.Id, context.OpponentNpcId, context.OpponentName, grants);
    }

    // Player-facing milestone name ("Gold", "Gold 5", "Legend #1"); unknown ids are returned unchanged.
    internal static string DisplayName(string milestoneId)
    {
        switch (milestoneId)
        {
            case "legend": return "Legend";
            case "legend:top-100": return "Top 100";
            case "legend:1": return "Legend #1";
        }
        if (milestoneId == null || !milestoneId.StartsWith("floor:", StringComparison.Ordinal)) return milestoneId;
        string[] parts = milestoneId.Substring(6).Split('-');
        if (parts.Length != 2 || parts[0].Length == 0 || (parts[1] != "10" && parts[1] != "5")) return milestoneId;
        string league = char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1);
        return parts[1] == "10" ? league : league + " 5";
    }

    internal static IReadOnlyList<MilestoneGrant> Totals(MilestoneCatalog catalog, bool includeLegendNumbers = false)
    {
        Validate(catalog);
        return Array.AsReadOnly(catalog.Milestones.Where(m => includeLegendNumbers || m.Kind == "StarIndex")
            .SelectMany(m => m.Grants).Where(g => g.Kind != "Recipe" && g.Kind != "Challengers")
            .GroupBy(g => new { g.Kind, g.Ref }).Select(g => new MilestoneGrant(g.Key.Kind, g.Key.Ref, g.Sum(x => x.Count))).ToArray());
    }

    internal static void Validate(MilestoneCatalog catalog)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        if (catalog.Version < 1 || catalog.DeckKitGoldCap <= 0)
            throw new ArgumentException("Invalid milestone catalogue version or deck-kit cap.", nameof(catalog));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int star = 0, legendNumber = int.MaxValue;
        foreach (var milestone in catalog.Milestones)
        {
            if (milestone == null || string.IsNullOrEmpty(milestone.Id) || !ids.Add(milestone.Id))
                throw new ArgumentException("Missing or duplicate milestone id.", nameof(catalog));
            if (milestone.Kind == "StarIndex")
            {
                if (milestone.Threshold != star + 15 || milestone.Threshold > 150)
                    throw new ArgumentException("Star thresholds must be 15, 30, ..., 150.", nameof(catalog));
                star = milestone.Threshold;
                string expectedId = star == 150 ? "legend" : "floor:"
                    + new[] { "bronze", "silver", "gold", "platinum", "diamond" }[star / 30]
                    + (star % 30 == 0 ? "-10" : "-5");
                if (milestone.Id != expectedId || milestone.IsLeague != (star % 30 == 0))
                    throw new ArgumentException("Invalid floor id or league flag.", nameof(catalog));
            }
            else if (milestone.Kind == "LegendNumber")
            {
                if (star != 150) throw new ArgumentException("Legend-number milestones follow every floor.", nameof(catalog));
                // Legend numbers decrease as achievement difficulty increases (top 100, then #1).
                if (milestone.Threshold <= 0 || milestone.Threshold >= legendNumber || milestone.IsLeague
                    || !((milestone.Id == "legend:top-100" && milestone.Threshold == 100)
                        || (milestone.Id == "legend:1" && milestone.Threshold == 1)))
                    throw new ArgumentException("Invalid Legend-number milestone or order.", nameof(catalog));
                legendNumber = milestone.Threshold;
            }
            else throw new ArgumentException("Unknown milestone kind.", nameof(catalog));
            if (milestone.Grants.Count == 0 || milestone.Grants.Any(g => !ValidGrant(g)))
                throw new ArgumentException("Invalid milestone grant.", nameof(catalog));
        }
        if (star != 150) throw new ArgumentException("Missing star floor milestones.", nameof(catalog));
        if (legendNumber != 1 || !ids.Contains("legend:top-100"))
            throw new ArgumentException("Missing Legend-number milestones.", nameof(catalog));
    }

    private static bool ValidGrant(MilestoneGrant grant)
    {
        if (grant == null || grant.Count <= 0) return false;
        switch (grant.Kind)
        {
            case "Pack": return grant.Ref == "STANDARD";
            case "Token": return grant.Ref == "COMMON" || grant.Ref == "RARE" || grant.Ref == "EPIC" || grant.Ref == "LEGENDARY";
            case "DeckKit": return grant.Ref == "kit:ladder-gold";
            case "CardBack": return new[] { "silver", "gold", "platinum", "diamond", "legend", "elite", "champion" }
                .Any(name => grant.Ref == "cardback:ladder-" + name);
            case "Trophy": return grant.Ref == "trophy:legend" || grant.Ref == "trophy:legend-1";
            case "LegendDeck": return grant.Ref == "Snapshot";
            case "Recipe": return grant.Ref == "opponent";
            case "Challengers": return grant.Ref == "band";
            default: return false;
        }
    }
}
