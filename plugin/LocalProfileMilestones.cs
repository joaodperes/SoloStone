using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public sealed partial class LocalProfile
{
    // NPC world seams (spec S9). The plugin wires them to NpcWorldRuntime (LocalProfileNpcWorld.cs); tests may set fakes.
    // Null = legacy: no Legend numbers, no Challengers, deck-named opponents. A failing seam never blocks a settlement.
    internal static Func<double, int, int?> NpcLegendNumber;            // (rating after, settled match count) -> Legend number
    internal static Func<int, double> NpcLegendTopRating;               // settled match count -> #1 field rating
    internal static Func<string, IReadOnlyList<string>> NpcChallengers; // milestone id -> notable NPC ids of that band
    internal static Func<string, string> NpcDisplayName;                // NPC id -> display name

    private static T Seam<T>(Func<T> lookup) where T : class
    {
        try { return lookup(); } catch (Exception) { return null; }
    }

    // Legend numbers count the NPC field at the ranked match count after this settlement (the match is not yet in history).
    // An abandoned match (RecoverInterrupted) never pays them: its grants would never be presented.
    private static int? LegendNumberAfter(LocalProfileData next, LocalMatchRecord match)
    {
        var lookup = NpcLegendNumber;
        if (lookup == null || match.Result == "Abandoned" || match.Ladder.StarsAfter < LadderRules.LegendIndex || !match.Ladder.RatingAfter.HasValue) return null;
        try {
            int? number = lookup(match.Ladder.RatingAfter.Value, RankedMatches(next).Count + 1);
            return number.HasValue && number.Value >= 1 ? number : null;
        } catch (Exception) { return null; }
    }

    public LocalMilestoneCatchUp PendingMilestoneCatchUp => data.MilestoneCatchUp == null || data.MilestoneCatchUp.Acknowledged
        ? null : Clone(data.MilestoneCatchUp);

    public bool AcknowledgeMilestoneCatchUp(string id)
    {
        if (data.MilestoneCatchUp == null || data.MilestoneCatchUp.Id != id)
            throw new InvalidOperationException("Unknown milestone catch-up acknowledgement.");
        if (data.MilestoneCatchUp.Acknowledged) return false;
        var next = Snapshot();
        next.MilestoneCatchUp.Acknowledged = true;
        Commit(next);
        return true;
    }

    // Runs on every load: floors at or below the best that are still unpaid (pre-catalogue progress, or floors a
    // later catalogue adds) are paid once; an existing receipt keeps its grants and becomes unacknowledged again.
    private void ApplyMilestoneCatchUp()
    {
        // A seeded Legend tier is not earned historical progress, including after its first settlement.
        if (profileOptions.StartLegendOne) return;
        var ladder = LadderOf(data);
        var rewards = LocalLadderMilestones.EvaluateCatchUp(Math.Max(data.LadderState?.BestStarIndex ?? 0, ladder.BestStarIndex),
            data.LadderState?.FloorsRewarded ?? new List<string>(), MilestoneCatalog.V1);
        if (rewards.Count == 0) return;
        var next = Snapshot();
        if (next.LadderState == null) next.LadderState = new LocalLadderState { StarIndex = ladder.StarIndex, BestStarIndex = ladder.BestStarIndex };
        var granted = ApplyMilestoneGrants(next, rewards, null, null);
        if (next.MilestoneCatchUp == null) next.MilestoneCatchUp = new LocalMilestoneCatchUp { Id = "catchup:v1" };
        next.MilestoneCatchUp.TierRewards.AddRange(granted);
        next.MilestoneCatchUp.Acknowledged = false;
        Commit(next);
    }

    internal const string CreationReceiptId = "creation:v1";

    // Start at Legend #1 already holds every floor, Legend, top 100 and #1: pay them once inside the creation
    // transaction, acknowledged (no catch-up popup), so the first settled match never pays them, win or loss.
    private static void GrantLegendOneCreation(LocalProfileData next)
    {
        var rewards = LocalLadderMilestones.EvaluateCreation(LadderRules.LegendIndex, 1, MilestoneCatalog.V1);
        next.LadderState = new LocalLadderState { StarIndex = LadderRules.LegendIndex, BestStarIndex = LadderRules.LegendIndex, LegendReached = true };
        var granted = ApplyMilestoneGrants(next, rewards, null, null, CreationReceiptId,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        next.MilestoneCatchUp = new LocalMilestoneCatchUp { Id = CreationReceiptId, TierRewards = granted, Acknowledged = true };
    }

    private static List<LocalTierReward> SettleMilestones(LocalProfileData next, LocalMatchRecord match)
    {
        var context = new MatchMilestoneContext(next, match);
        var rewards = LocalLadderMilestones.Evaluate(match.Ladder.StarsBefore, match.Ladder.StarsAfter,
            LegendNumberAfter(next, match), next.LadderState.FloorsRewarded, MilestoneCatalog.V1, context);
        if (match.Ladder.LadderRuleVersion >= 2) {
            // Ladder receipts store floor indices, omit Legend, and report only new best floors.
            var expected = match.Ladder.FloorsReached.Select(floor => MilestoneCatalog.V1.Milestones.Single(m =>
                m.Kind == "StarIndex" && m.Threshold.ToString(CultureInfo.InvariantCulture) == floor).Id)
                .Concat(match.Ladder.LegendReached ? new[] { "legend" } : Array.Empty<string>())
                .Where(id => !next.LadderState.FloorsRewarded.Contains(id));
            // Legend-number milestones are not in the receipt; they follow the NPC field.
            if (!expected.SequenceEqual(rewards.Select(r => r.MilestoneId).Where(id => MilestoneCatalog.V1.Milestones
                .Single(m => m.Id == id).Kind == "StarIndex")))
                throw new InvalidOperationException("Milestone rewards differ from the ladder receipt.");
        }
        return ApplyMilestoneGrants(next, rewards, match,
            rewards.Any(r => r.MilestoneId == "legend") ? context.DeckSnapshot : null);
    }

    private static List<LocalTierReward> ApplyMilestoneGrants(LocalProfileData next,
        IReadOnlyList<MilestoneTierReward> rewards, LocalMatchRecord match, LocalLegendDeck snapshot,
        string origin = "catchup:v1", string originUtc = "1970-01-01T00:00:00.0000000Z")
    {
        var receipts = new List<LocalTierReward>();
        foreach (var reward in rewards) {
            var receipt = new LocalTierReward { MilestoneId = reward.MilestoneId, FirstTime = reward.FirstTime,
                OpponentNpcId = reward.OpponentNpcId, OpponentName = reward.OpponentName };
            foreach (var grant in reward.Grants) {
                var kind = (LocalTierGrantKind)Enum.Parse(typeof(LocalTierGrantKind), grant.Kind);
                string reference = grant.Ref;
                switch (kind) {
                    case LocalTierGrantKind.Pack:
                        // match.Packs is reserved for the economy's one-pack bonus; milestone packs have their own receipt.
                        AddPacks(next, FairPackRules.Standard, grant.Count);
                        break;
                    case LocalTierGrantKind.Token:
                        switch (grant.Ref) {
                            case "COMMON": next.CardTokens.Common = checked(next.CardTokens.Common + grant.Count); break;
                            case "RARE": next.CardTokens.Rare = checked(next.CardTokens.Rare + grant.Count); break;
                            case "EPIC": next.CardTokens.Epic = checked(next.CardTokens.Epic + grant.Count); break;
                            case "LEGENDARY": next.CardTokens.Legendary = checked(next.CardTokens.Legendary + grant.Count); break;
                            default: throw new InvalidOperationException("Unknown milestone token rarity.");
                        }
                        break;
                    case LocalTierGrantKind.DeckKit:
                        next.DeckKits.Add(new LocalDeckKit { Id = "kit:" + reward.MilestoneId,
                            MilestoneId = reward.MilestoneId, GoldCap = MilestoneCatalog.V1.DeckKitGoldCap });
                        break;
                    case LocalTierGrantKind.CardBack:
                        int back = LocalShop.LadderExclusiveCardBacks.Single(pair =>
                            "cardback:ladder-" + pair.Key.ToLowerInvariant() == grant.Ref).Value;
                        reference = back.ToString(CultureInfo.InvariantCulture);
                        if (!next.OwnedCardBacks.Contains(back)) next.OwnedCardBacks.Add(back);
                        break;
                    case LocalTierGrantKind.Trophy:
                        // V10 requires both fields, even for catch-up where no historical match is known.
                        next.Trophies.Add(new LocalTrophy { Id = grant.Ref, MilestoneId = reward.MilestoneId,
                            MatchId = match?.Id ?? origin, Utc = match?.StartedUtc ?? originUtc });
                        break;
                    case LocalTierGrantKind.LegendDeck:
                        if (next.LegendDeck == null) next.LegendDeck = match == null || snapshot == null || grant.Ref == "Legacy"
                            ? new LocalLegendDeck { Legacy = true } : Clone(snapshot);
                        break;
                    case LocalTierGrantKind.Recipe:
                        if (!next.OwnedRecipes.Contains(grant.Ref)) next.OwnedRecipes.Add(grant.Ref);
                        break;
                    case LocalTierGrantKind.Challengers:
                        break;
                    default: throw new InvalidOperationException("Unknown milestone grant kind.");
                }
                // Native card-back ids make presentation a direct integer parse; recipes retain Count when already owned.
                receipt.Grants.Add(new LocalTierGrant { Kind = kind, Ref = reference, Count = grant.Count });
            }
            next.LadderState.FloorsRewarded.Add(reward.MilestoneId);
            receipts.Add(receipt);
        }
        return receipts;
    }

    // Lookups here must never throw: a match must always settle. The deck snapshot is resolved only when a
    // Legend promotion asks for it, with the same rules as ResolveDeckHero; unresolvable means a Legacy claim.
    private sealed class MatchMilestoneContext : IMilestoneContext
    {
        private readonly LocalProfileData profile;
        private readonly LocalMatchRecord match;
        private readonly LocalDeck opponent;
        private LocalLegendDeck snapshot;
        private bool resolved;
        private readonly string npcName;
        public string OpponentNpcId { get; }
        // A world NPC is named only by the world (null when unresolved), never by a legacy deck or its raw id.
        public string OpponentName => match.NpcId != null ? npcName : opponent?.Opponent ?? OpponentNpcId;
        public bool HasLegendDeckSnapshot => DeckSnapshot != null;
        public IReadOnlyList<string> ChallengersFor(string milestoneId)
        {
            var lookup = NpcChallengers;
            return (lookup == null ? null : Seam(() => lookup(milestoneId)?.Where(LocalShop.ValidId).Distinct(StringComparer.Ordinal).ToArray()))
                ?? Array.Empty<string>();
        }
        private static IEnumerable<LocalDeck> Decks => LocalDecks.All.Concat(LocalDecks.TestLoaners).Concat(LocalDecks.TestOpponents)
            .Concat(LocalDecks.PlayerStarterDecks).Concat(LocalDecks.NpcDecks);

        internal MatchMilestoneContext(LocalProfileData profile, LocalMatchRecord match)
        {
            this.profile = profile; this.match = match;
            OpponentNpcId = match.NpcId ?? match.Opponent;
            opponent = Decks.FirstOrDefault(d => d.Id == match.Opponent);
            var name = NpcDisplayName;
            if (match.NpcId != null && name != null) {
                string value = Seam(() => name(match.NpcId));
                npcName = string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ? null : value;
            }
        }

        public LocalLegendDeck DeckSnapshot
        {
            get
            {
                if (!resolved) { snapshot = ResolveSnapshot(); resolved = true; }
                return snapshot;
            }
        }

        private LocalLegendDeck ResolveSnapshot()
        {
            string key = match.Deck;
            if (key == null) return null;
            LocalCustomDeck saved = null;
            if (key == "custom") saved = profile.CustomDecks.Count == 1 ? profile.CustomDecks[0] : null;
            else if (key.StartsWith("custom:", StringComparison.Ordinal)
                && long.TryParse(key.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out long id))
                saved = profile.CustomDecks.FirstOrDefault(d => d.Id == id);
            if (saved != null) return new LocalLegendDeck { MatchId = match.Id, DeckName = saved.Name,
                Hero = saved.Hero, Cards = saved.Cards.ToList(), Sideboards = saved.Sideboards.Select(Clone).ToList() };
            var deck = LocalDecks.Resolve(Decks, key);
            return deck == null ? null : new LocalLegendDeck { MatchId = match.Id, DeckName = deck.Name,
                Hero = deck.Hero, Cards = deck.CardIds.ToList(), Sideboards = deck.Sideboards.Select(pair =>
                    new LocalSideboard { Source = pair.Key, Cards = pair.Value.ToList() }).ToList() };
        }

        public string ResolveOpponentRecipe()
        {
            if (opponent == null) return null;
            // shortcut: recipes have no source-deck/archetype mapping; use exact lists until the library supplies one.
            return LocalShop.Default.Recipes.FirstOrDefault(recipe => recipe.Hero == opponent.Hero
                && Sorted(Expand(recipe.Cards)).SequenceEqual(Sorted(opponent.CardIds))
                && recipe.Sideboards.Count == opponent.Sideboards.Count
                && recipe.Sideboards.All(board => opponent.Sideboards.TryGetValue(board.Owner, out var cards)
                    && Sorted(Expand(board.Cards)).SequenceEqual(Sorted(cards))))?.Id;
        }

        private static IEnumerable<string> Expand(IEnumerable<RecipeCard> rows) => rows.SelectMany(row => Enumerable.Repeat(row.Id, row.Count));
        private static IEnumerable<string> Sorted(IEnumerable<string> ids) => ids.OrderBy(id => id, StringComparer.Ordinal);
    }
}
