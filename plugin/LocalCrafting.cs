using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public enum CraftDirection { Craft, Disenchant, Upgrade }
public enum CraftIneligibility { None, UnknownCard, NotCollectible, CoreCard, FreeCard, NotInCraftableSets, HeroOrNonCard, Premium, GrantOnly }
public sealed class CraftCardDelta
{
    public string CardId;
    public int Premium;
    public int CountDelta;
    public int CountBefore;
    public int AcquiredBefore;
    public int AcquiredAfter;
}
public sealed class CraftPlan
{
    public bool Ok;
    public string Error;
    public long DustDelta;
    public List<CraftCardDelta> Cards = new List<CraftCardDelta>();
}
public sealed class CraftLedgerView
{
    public long Dust;
    public IReadOnlyDictionary<string, int> Counts;
    public IReadOnlyDictionary<string, int> Acquired;
    public bool PackRevealPending;
}
public static class LocalCrafting
{
    public const int CommonCraft = 40, RareCraft = 100, EpicCraft = 400, LegendaryCraft = 1600;
    private static readonly IReadOnlyDictionary<string, EngineCard> Definitions =
        Cards.All.ToDictionary(card => card.Id, StringComparer.Ordinal);
    private static readonly HashSet<string> CraftableIds = new HashSet<string>(
        // Fabled primaries and sideboard owners are ordinary legendaries here (owner 2026-10-30).
        LocalShop.StandardCollectionCards.Select(card => card.Id), StringComparer.Ordinal);

    private static readonly HashSet<string> GrantOnlyIds = new HashSet<string>(
        LocalWelcomeGift.HistoricalCardIds, StringComparer.Ordinal);

    // Every card the ledger crafts at Normal (and so may upgrade): native registers Normal and Golden collectible
    // entries for these, so unowned copies list as missing and CanUpgradeCardToGolden finds the Golden version.
    // Core, free/starter, grant-only and non-collectible (Fabled companion) cards stay out, exactly as TryGetPrice refuses
    // them. Fabled primaries and sideboard owners are in: ordinary legendaries since owner 2026-10-30.
    internal static IReadOnlyList<string> CraftableCardIds => craftableCardIds.Value;
    private static readonly Lazy<string[]> craftableCardIds = new Lazy<string[]>(() =>
        CraftableIds.Where(id => GetIneligibility(id) == CraftIneligibility.None).OrderBy(id => id, StringComparer.Ordinal).ToArray());

    public static CraftIneligibility GetIneligibility(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return CraftIneligibility.UnknownCard;
        if (GrantOnlyIds.Contains(cardId)) return CraftIneligibility.GrantOnly;
        if (LocalCardCatalog.IsCore(cardId)) return CraftIneligibility.CoreCard;
        if (!Definitions.TryGetValue(cardId, out var card)) return CraftIneligibility.UnknownCard;
        if (card[GameTag.PREMIUM] != 0) return CraftIneligibility.Premium;
        if (card.Type != CardType.MINION && card.Type != CardType.SPELL && card.Type != CardType.WEAPON
            && card.Type != (CardType)39) return CraftIneligibility.HeroOrNonCard; // Location, absent from the legacy engine enum.
        if (!card.Collectible) return CraftIneligibility.NotCollectible;
        if (card.Rarity == Rarity.FREE || LocalEconomy.StarterCollection.ContainsKey(cardId)) return CraftIneligibility.FreeCard;
        if (!CraftableIds.Contains(cardId)) return CraftIneligibility.NotInCraftableSets;
        return CraftIneligibility.None;
    }

    public static bool TryGetPrice(string cardId, out int craftCost, out int disenchantValue)
    {
        craftCost = disenchantValue = 0;
        if (GetIneligibility(cardId) != CraftIneligibility.None) return false;
        switch (Definitions[cardId].Rarity)
        {
            case Rarity.COMMON: craftCost = CommonCraft; break;
            case Rarity.RARE: craftCost = RareCraft; break;
            case Rarity.EPIC: craftCost = EpicCraft; break;
            case Rarity.LEGENDARY: craftCost = LegendaryCraft; break;
            default: return false;
        }
        disenchantValue = craftCost / 2;
        return true;
    }

    // Placeholder #52: all premium qualities use half the retail Golden craft cost.
    private static readonly IReadOnlyDictionary<Rarity, int> PremiumDust = new Dictionary<Rarity, int> {
        [Rarity.COMMON] = 200, [Rarity.RARE] = 400, [Rarity.EPIC] = 800, [Rarity.LEGENDARY] = 1600
    };

    public static bool TryGetUpgradePrice(string cardId, out int cost)
    {
        cost = 0;
        if (!TryGetPrice(cardId, out int normal, out _) || !TryGetPrice(cardId, 1, out _, out _)
            || !LocalClassRewards.IsAvailable(cardId)) return false;
        switch (Definitions[cardId].Rarity)
        {
            case Rarity.COMMON: cost = 400 - normal; break;
            case Rarity.RARE: cost = 800 - normal; break;
            case Rarity.EPIC: cost = 1600 - normal; break;
            case Rarity.LEGENDARY: cost = 3200 - normal; break;
            default: return false;
        }
        return true;
    }

    public static CraftPlan PlanUpgrade(CraftLedgerView view, string cardId, int count, int goldenCount,
        int goldenAcquired, int expectedNormal, int expectedGolden)
    {
        if (!ValidView(view) || goldenCount < 0 || goldenAcquired < 0) return Refuse("Invalid crafting ledger.");
        if (view.PackRevealPending) return Refuse("Finish the pending pack reveal first.");
        if (!TryGetUpgradePrice(cardId, out int cost)) return Refuse("This card cannot be upgraded.");
        int normal = Owned(view.Counts, cardId), acquired = Owned(view.Acquired, cardId);
        if (normal < 0 || acquired < 0) return Refuse("Invalid card counts.");
        if (normal != expectedNormal || goldenCount != expectedGolden) return Refuse("Owned count changed.");
        if (count < 1 || normal < count) return Refuse("Not enough Normal copies to upgrade.");
        if ((long)goldenCount + count > Playset(cardId)) return Refuse("Golden upgrade limit reached.");
        long dustDelta = -(long)count * cost;
        if (view.Dust + dustDelta < 0) return Refuse("Not enough dust.");
        return new CraftPlan { Ok = true, DustDelta = dustDelta, Cards = new List<CraftCardDelta> {
            new CraftCardDelta { CardId = cardId, Premium = 0, CountDelta = -count, CountBefore = normal,
                AcquiredBefore = acquired, AcquiredAfter = acquired },
            new CraftCardDelta { CardId = cardId, Premium = 1, CountDelta = count, CountBefore = goldenCount,
                AcquiredBefore = goldenAcquired, AcquiredAfter = (int)Math.Min(2, (long)goldenAcquired + count) }
        } };
    }

    public static CraftPlan CommitUpgrade(LocalProfile profile, string cardId, int count, int expectedNormal, int expectedGolden)
    {
        if (profile == null || profile.OwnedCount(cardId, 1) != expectedGolden) return Refuse("Owned count changed.");
        try
        {
            return profile.CommitChange(next => {
                var golden = next.PremiumCollection.SingleOrDefault(row => row.Id == cardId && row.Premium == 1);
                var plan = PlanUpgrade(new CraftLedgerView {
                    Dust = profile.InfiniteDust ? ProfileCatalog.UnlimitedDust : next.DustBalance,
                    PackRevealPending = next.PackRevealPending,
                    Counts = next.Collection.ToDictionary(row => row.Id, row => row.Count),
                    Acquired = next.Collection.ToDictionary(row => row.Id, row => row.Acquired)
                }, cardId, count, Math.Max(golden?.Count ?? 0, profile.OwnedCount(cardId, 1)), golden?.Acquired ?? 0,
                    expectedNormal, expectedGolden);
                if (!plan.Ok) throw new InvalidOperationException(plan.Error);
                next.Collection.Single(row => row.Id == cardId).Count -= count;
                if (golden == null) { golden = new LocalCardCount { Id = cardId, Premium = 1 }; next.PremiumCollection.Add(golden); }
                golden.Count = checked(golden.Count + count);
                golden.Acquired = plan.Cards[1].AcquiredAfter;
                var decks = next.CustomDecks.AsEnumerable();
                if (next.CustomDeck != null && !next.CustomDecks.Contains(next.CustomDeck)) decks = decks.Append(next.CustomDeck);
                foreach (var deck in decks)
                    ReconcileUpgradeDeck(deck, cardId, plan.Cards[0].CountBefore - count,
                        Math.Max(golden.Count, profile.OwnedCount(cardId, 1)));
                LocalProfile.SeeOnAcquire(next, cardId, 1, count);
                if (!profile.InfiniteDust) next.DustBalance = checked(next.DustBalance + plan.DustDelta);
                next.CraftSequence = checked(next.CraftSequence + 1);
                next.LastCraft = new LocalCraftReceipt {
                    Sequence = next.CraftSequence, Op = LocalCraftOp.Upgrade, DustDelta = plan.DustDelta, Undoable = false,
                    Cards = plan.Cards.Select(delta => new LocalCraftCard {
                        CardId = cardId, Premium = delta.Premium, CountDelta = delta.CountDelta,
                        AcquiredBefore = Math.Min(Playset(cardId), delta.AcquiredBefore)
                    }).ToList()
                };
                LocalProfile.ReconcileCollectionAchievement(next);
                return plan;
            });
        }
        catch (Exception error) { return Refuse(error.Message); }
    }

    internal static int PremiumQuality(int premium) => premium == 2 ? 3 : premium == 3 ? 2 : premium;

    // Reserve valid explicit versions first; only unavailable slots fall back to the best free version.
    // Main deck and sideboards share the same per-card budget, as CollectionDeck does.
    // preferBest (fresh decks only): ignore the slots' current versions and hand out the best owned versions first.
    internal static List<string> AssignOwnedPremiums(LocalCustomDeck deck, Func<string, int, int> owned, bool preferBest = false)
    {
        var changes = new List<string>();
        var entries = deck.CardEntries.Concat(deck.Sideboards.SelectMany(board => board.CardEntries)).ToList();
        var companions = new HashSet<string>(entries.SelectMany(card => LocalDecks.FabledBundle(card.Id)), StringComparer.Ordinal);
        foreach (var group in entries.Where(card => !companions.Contains(card.Id)).GroupBy(card => card.Id, StringComparer.Ordinal)) {
            var free = Enumerable.Range(0, 4).ToDictionary(premium => premium, premium => owned(group.Key, premium));
            var missing = new List<LocalDeckCard>();
            foreach (var card in group) {
                if (!preferBest && free[card.Premium] > 0) free[card.Premium]--;
                else missing.Add(card);
            }
            foreach (var card in missing) {
                int replacement = free.Keys.Where(premium => free[premium] > 0)
                    .OrderByDescending(PremiumQuality).DefaultIfEmpty(-1).First();
                if (replacement < 0) continue; // Keep genuinely unowned slots; never remove cards or grant copies.
                free[replacement]--;
                if (replacement == card.Premium) continue;
                changes.Add(card.Id + ":" + card.Premium + "->" + replacement);
                card.Premium = replacement;
            }
        }
        foreach (string id in companions.OrderBy(id => id, StringComparer.Ordinal)) {
            var versions = new Queue<int>(entries.Where(card => LocalDecks.FabledBundle(card.Id).Contains(id)).Select(card => card.Premium));
            foreach (var card in entries.Where(card => card.Id == id)) {
                if (versions.Count == 0) break;
                int premium = versions.Dequeue();
                if (card.Premium == premium) continue;
                changes.Add(card.Id + ":" + card.Premium + "->" + premium); card.Premium = premium;
            }
        }
        return changes;
    }

    internal static int MissingDeckSlots(LocalCustomDeck deck, Func<string, int, int> owned, bool normalToo = true)
    {
        var entries = deck.CardEntries.Concat(deck.Sideboards.SelectMany(board => board.CardEntries)).ToList();
        var bundled = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var primary in entries.GroupBy(card => card.Id + ":" + card.Premium, StringComparer.Ordinal)) {
            var card = primary.First();
            int count = Math.Min(primary.Count(), owned(card.Id, card.Premium));
            foreach (string companion in LocalDecks.FabledBundle(card.Id)) {
                string key = companion + ":" + card.Premium;
                bundled[key] = (bundled.TryGetValue(key, out int before) ? before : 0) + count;
            }
        }
        return entries.GroupBy(card => card.Id + ":" + card.Premium, StringComparer.Ordinal).Sum(group => {
            var card = group.First();
            if (!normalToo && card.Premium == 0) return 0;
            return Math.Max(0, group.Count() - owned(card.Id, card.Premium)
                - (bundled.TryGetValue(group.Key, out int count) ? count : 0));
        });
    }

    internal static void ReconcileUpgradeDeck(LocalCustomDeck deck, string cardId, int lower, int owned, int premium = 1)
    {
        var allEntries = deck.CardEntries.Concat(deck.Sideboards.SelectMany(board => board.CardEntries)).ToList();
        var entries = allEntries.Where(card => card.Id == cardId).ToList();
        int convert = Math.Min(Math.Max(0, entries.Count(card => PremiumQuality(card.Premium) < PremiumQuality(premium)) - lower),
            Math.Max(0, owned - entries.Count(card => card.Premium == premium)));
        foreach (var card in entries.Where(card => PremiumQuality(card.Premium) < PremiumQuality(premium)).Take(convert)) {
            card.Premium = premium;
            foreach (string companionId in LocalDecks.FabledBundle(cardId)) {
                var companion = allEntries.FirstOrDefault(entry => entry.Id == companionId && PremiumQuality(entry.Premium) < PremiumQuality(premium));
                if (companion != null) companion.Premium = premium;
            }
        }
    }

    public static bool TryGetPrice(string cardId, int premium, out int craftCost, out int disenchantValue)
    {
        if (premium == 0) return TryGetPrice(cardId, out craftCost, out disenchantValue);
        craftCost = disenchantValue = 0;
        if (GetIneligibility(cardId) == CraftIneligibility.GrantOnly) return false;
        // Premium copies are rewards (including Core cards), so the Normal craftable-set filter does not apply.
        return premium >= 1 && premium <= 3 && !string.IsNullOrEmpty(cardId) && Definitions.TryGetValue(cardId, out var card)
            && card[GameTag.PREMIUM] == 0 && card.Collectible
            && (card.Type == CardType.MINION || card.Type == CardType.SPELL || card.Type == CardType.WEAPON || card.Type == (CardType)39)
            && PremiumDust.TryGetValue(card.Rarity, out disenchantValue);
    }

    // The Normal ledger remains unchanged; premium sales share its planner and atomic profile commit.
    public static CraftPlan CommitPremiumBatch(LocalProfile profile, string cardId, int premium,
        CraftDirection direction, int count, int expectedOwnedCount)
    {
        if (profile == null || premium < 1 || premium > 3 || profile.OwnedCount(cardId, premium) != expectedOwnedCount)
            return Refuse("Invalid premium ownership.");
        if (direction == CraftDirection.Disenchant && count > profile.DisenchantablePremiumCount(cardId, premium))
            return Refuse("Premium cards earned through progression cannot be disenchanted.");
        try
        {
            return profile.CommitChange(next => {
                if (direction == CraftDirection.Disenchant && count > LocalProfile.DisenchantablePremiumCount(next, cardId, premium))
                    throw new InvalidOperationException("Premium cards earned through progression cannot be disenchanted.");
                var rows = next.PremiumCollection.Where(card => card.Premium == premium).ToArray();
                var stored = rows.SingleOrDefault(row => row.Id == cardId);
                int storedCount = stored?.Count ?? 0;
                var plan = PlanBatch(new CraftLedgerView {
                    Dust = profile.InfiniteDust ? ProfileCatalog.UnlimitedDust : next.DustBalance,
                    PackRevealPending = next.PackRevealPending,
                    Counts = rows.ToDictionary(card => card.Id, card => card.Count),
                    Acquired = rows.ToDictionary(card => card.Id, card => card.Acquired)
                }, cardId, direction, count, storedCount, premium);
                if (!plan.Ok) throw new InvalidOperationException(plan.Error);
                var delta = plan.Cards.Single();
                var card = stored ?? throw new InvalidOperationException("No durable premium copy is available.");
                card.Count = checked(card.Count + delta.CountDelta);
                if (!profile.InfiniteDust || plan.DustDelta > 0)
                    next.DustBalance = checked(next.DustBalance + plan.DustDelta);
                next.CraftSequence = checked(next.CraftSequence + 1);
                next.LastCraft = new LocalCraftReceipt {
                    Sequence = next.CraftSequence, Op = LocalCraftOp.Disenchant, DustDelta = plan.DustDelta,
                    Cards = new List<LocalCraftCard> { new LocalCraftCard {
                        CardId = cardId, CountDelta = delta.CountDelta, AcquiredBefore = Math.Min(Playset(cardId), card.Acquired), Premium = premium
                    } }
                };
                return plan;
            });
        }
        catch (Exception error) { return Refuse(error.Message); }
    }

    public static int Playset(string cardId) => cardId != null && Definitions.TryGetValue(cardId, out var card)
        && card.Rarity == Rarity.LEGENDARY ? 1 : 2;

    private static CraftPlan Refuse(string error) => new CraftPlan { Error = error };
    private static int Owned(IReadOnlyDictionary<string, int> counts, string cardId) =>
        counts.TryGetValue(cardId, out int count) ? count : 0;
    private static bool ValidView(CraftLedgerView view) => view != null && view.Dust >= 0
        && view.Counts != null && view.Acquired != null;

    public static CraftPlan PlanBatch(CraftLedgerView view, string cardId, CraftDirection direction, int count, int expectedOwnedCount, int premium = 0)
    {
        if (premium != 0 && direction != CraftDirection.Disenchant) return Refuse("Premium crafting is disabled.");
        if (!ValidView(view)) return Refuse("Invalid crafting ledger.");
        if (view.PackRevealPending) return Refuse("Finish the pending pack reveal first.");
        if (!TryGetPrice(cardId, premium, out int craftCost, out int disenchantValue)) return Refuse("Card is not craftable: " + GetIneligibility(cardId));
        if (count < 1) return Refuse("Count must be positive.");
        if (direction != CraftDirection.Craft && direction != CraftDirection.Disenchant) return Refuse("Invalid crafting direction.");
        int before = Owned(view.Counts, cardId), acquired = Owned(view.Acquired, cardId);
        if (before < 0 || acquired < 0) return Refuse("Invalid card counts.");
        if (before != expectedOwnedCount) return Refuse("Owned count changed.");
        try
        {
            int delta = direction == CraftDirection.Craft ? count : -count;
            int after = checked(before + delta);
            if (after < 0) return Refuse("Not enough owned copies.");
            if (direction == CraftDirection.Craft && after > Playset(cardId)) return Refuse("Craft limit reached.");
            long dustDelta = direction == CraftDirection.Craft ? checked(-(long)count * craftCost)
                : checked((long)count * disenchantValue);
            if (checked(view.Dust + dustDelta) < 0) return Refuse("Not enough dust.");
            return new CraftPlan { Ok = true, DustDelta = dustDelta, Cards = new List<CraftCardDelta> {
                new CraftCardDelta { CardId = cardId, Premium = premium, CountDelta = delta, CountBefore = before,
                    AcquiredBefore = acquired, AcquiredAfter = direction == CraftDirection.Craft
                        ? checked((int)Math.Min(Playset(cardId), checked((long)acquired + count))) : acquired }
            } };
        }
        catch (OverflowException) { return Refuse("Crafting arithmetic overflow."); }
    }

    /// <summary>Normal copies beyond the playset: what PlanMassDisenchant removes for one card (premium never counts).</summary>
    public static int MassExtras(IReadOnlyDictionary<string, int> normalCounts, string cardId) =>
        Math.Max(0, (normalCounts != null && cardId != null && normalCounts.TryGetValue(cardId, out int count) ? count : 0) - Playset(cardId));

    public static CraftPlan PlanMassDisenchant(CraftLedgerView view)
    {
        if (!ValidView(view)) return Refuse("Invalid crafting ledger.");
        if (view.PackRevealPending) return Refuse("Finish the pending pack reveal first.");
        var plan = new CraftPlan();
        try
        {
            foreach (var entry in view.Counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!TryGetPrice(entry.Key, out _, out int value)) continue;
                int acquired = Owned(view.Acquired, entry.Key);
                if (entry.Value < 0 || acquired < 0) return Refuse("Invalid card counts.");
                int extras = checked(entry.Value - Playset(entry.Key));
                if (extras <= 0) continue;
                plan.DustDelta = checked(plan.DustDelta + (long)extras * value);
                plan.Cards.Add(new CraftCardDelta { CardId = entry.Key, CountDelta = -extras,
                    CountBefore = entry.Value, AcquiredBefore = acquired, AcquiredAfter = acquired });
            }
            if (plan.Cards.Count == 0) return Refuse("No extra copies to disenchant.");
            _ = checked(view.Dust + plan.DustDelta);
            plan.Ok = true;
            return plan;
        }
        catch (OverflowException) { return Refuse("Crafting arithmetic overflow."); }
    }
}
