using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public sealed class LocalRewardFacts
{
    public int DistinctCardsPlayed;
    public int EnemyMinionsDefeated;
    public int MinimumHealth = 30;
}

public sealed class LocalPackCard
{
    public string Id;
    public int DuplicateGold;
    public int AcquiredBefore;
    public int Premium;
    public bool ShouldSerializePremium() => Premium != 0;
    [System.Xml.Serialization.XmlIgnore] public string Key => ReceiptKey(Id, Premium);
    internal static string ReceiptKey(string id, int premium) => premium == 0 ? id : id + ":" + premium;
    internal static LocalPackCard FromKey(string key)
    {
        int separator = key?.LastIndexOf(':') ?? -1;
        return separator < 0 ? new LocalPackCard { Id = key } : new LocalPackCard {
            Id = key.Substring(0, separator), Premium = key.Substring(separator + 1) == "1" ? 1 : key.Substring(separator + 1) == "3" ? 3 : -1 };
    }
}

public sealed class LocalPackResult
{
    public int PoolVersion;
    public string PackType;
    public int PackRuleVersion;
    public FairPackCounters CountersBefore;
    public List<LocalPackCard> Cards = new List<LocalPackCard>();
}

internal sealed class LocalReward
{
    public int Gold;
    public int BonusGold;   // V3: the bonus added to whichever currency the roll paid (gold, else dust)
    public bool PackReward;
    public int Dust;
}

public static class LocalEconomy
{
    public const int MatchDust = 0;
    public const int PackPrice = 100;
    internal static readonly string[] PaladinBasicIds = {
        "CS2_087", "CS2_088", "CS2_089", "CS2_091", "CS2_092",
        "CS2_093", "CS2_094", "CS2_097", "EX1_360", "EX1_371"
    };
    public static readonly IReadOnlyList<EngineCard> Catalog;
    public static readonly IReadOnlyDictionary<string, int> StarterCollection;
    internal static readonly IReadOnlyList<EngineCard> LegacyV2Catalog;
    internal static readonly IReadOnlyDictionary<string, int> LegacyV2StarterCollection;
    internal static readonly IReadOnlyList<EngineCard> LegacyV4Catalog;
    internal static readonly IReadOnlyDictionary<string, int> LegacyV4StarterCollection;

    static LocalEconomy()
    {
        Catalog = LocalCardCatalog.Supported;
        LegacyV4Catalog = LocalCardCatalog.LegacyV4;

        var legacyDecks = LocalDecks.All.Where(d => new[] {
            "mage", "warrior", "priest", "mage-frost", "warrior-control", "priest-combo"
        }.Contains(d.Id)).ToArray();
        LegacyV2Catalog = new ReadOnlyCollection<EngineCard>(legacyDecks.SelectMany(deck => deck.CardIds)
            .Distinct(StringComparer.Ordinal).Select(id => Cards.FromId(id) ?? throw new InvalidOperationException("Unknown local card " + id))
            .OrderBy(card => card.Id, StringComparer.Ordinal).ToList());

        var original = legacyDecks.Where(d => d.Id == "mage" || d.Id == "warrior" || d.Id == "priest").ToArray();
        LegacyV2StarterCollection = BuildStarter(LegacyV2Catalog, original);
        LegacyV4StarterCollection = LocalCardCatalog.LegacyV4Starter;
        var currentStarter = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in LegacyV4StarterCollection) currentStarter.Add(entry.Key, entry.Value);
        foreach (string id in LocalCardCatalog.NewBasicIds) currentStarter.Add(id, 2);
        StarterCollection = new ReadOnlyDictionary<string, int>(currentStarter);
    }

    private static IReadOnlyDictionary<string, int> BuildStarter(IEnumerable<EngineCard> catalog, LocalDeck[] original)
    {
        var starter = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in catalog)
        {
            int originalCount = original.Max(deck => deck.CardIds.Count(id => id == card.Id));
            int count = Math.Min(PlayableCap(card), Math.Max(card.Rarity == Rarity.FREE ? 2 : 0, originalCount));
            if (count > 0) starter.Add(card.Id, count);
        }
        return new ReadOnlyDictionary<string, int>(starter);
    }

    internal static int PlayableCap(EngineCard card) => card.Rarity == Rarity.LEGENDARY ? 1 : 2;
    // Largest playset of any rarity. Load validation bounds history by it: copies acquired under an older rarity
    // (an epic pair later corrected to legendary) stay valid; new acquisitions still use PlayableCap.
    internal const int MaximumPlayset = 2;
    // A lifetime-acquired count recorded under some playset the card could have had (smallestCap..2). Callers pass the
    // smallest playset their receipt proves (e.g. two copies gained at once prove a playset of two).
    internal static bool HistoricAcquired(int acquired, int uncapped, int smallestCap = 1)
    {
        for (int cap = Math.Max(1, smallestCap); cap <= MaximumPlayset; cap++)
            if (acquired == Math.Min(cap, uncapped)) return true;
        return false;
    }

    internal static LocalReward Reward(string result, int turns, bool conceded, LocalRewardFacts facts)
    {
        ValidateFacts(facts);
        if (conceded || result == "Abandoned" || turns < 4) return new LocalReward();
        int bonus = 0;
        if (facts.DistinctCardsPlayed >= 6) bonus += 10;
        if (facts.EnemyMinionsDefeated >= 5) bonus += 10;
        if (result == "Won" && facts.MinimumHealth >= 1 && facts.MinimumHealth <= 10) bonus += 20;
        bonus = Math.Min(40, bonus);
        return new LocalReward {
            Gold = checked((result == "Won" ? 50 : 25) + bonus),
            BonusGold = bonus,
            PackReward = result == "Won" && bonus >= 20
        };
    }

    // Reward V3 (owner 2026-10-25): a loss, draw, concede, interruption or <4-turn game pays nothing (rank and class XP
    // still move). A win always rolls: 50% gold 40-80, 25% dust 40-80, 25% both at 30-60 each (uniform, inclusive).
    // The V2 bonus facts stay at half weight (6 distinct cards +5, 5 enemy minions defeated +5, won at 1-10 Health +10,
    // max 20) and go to the gold when the roll paid gold, else to the dust. A great win (bonus >= 10, the V2 >= 20 rule
    // at half weight) still drops a pack, from a Standard set weighted by missing copies (RewardPackSet).
    // No time input: the roll is a pure function of the match Id and Seed (Begin).
    // Expected win, counting 1 dust as 1 gold: 0.5*60 + 0.25*60 + 0.25*(45+45) = 67.5, plus the bonus (typically 5-10)
    // ~ 72-78; V2 paid a typical win 50 + 10-20 = 60-70. A V3 loss pays 0 where V2 paid 25 + bonus.
    public const int RollRewardVersion = 6;
    public static bool IsRolled(int version) => version == 4 || version == 5 || version == 6;

    internal static int RewardRollSeed(string matchId, int seed)
    {
        // Guid bytes, not string.GetHashCode (randomised per process on .NET Core): identical on every load and platform.
        byte[] id = Guid.Parse(matchId).ToByteArray();
        return StableSeed.Derive(seed, BitConverter.ToInt64(id, 0), BitConverter.ToInt64(id, 8), 0x524F4C4C /* ROLL */);
    }

    internal static LocalReward RewardV3(string result, int turns, bool conceded, LocalRewardFacts facts, int rollSeed)
    {
        ValidateFacts(facts);
        if (conceded || result != "Won" || turns < 4) return new LocalReward();
        int bonus = 0;
        if (facts.DistinctCardsPlayed >= 6) bonus += 5;
        if (facts.EnemyMinionsDefeated >= 5) bonus += 5;
        if (facts.MinimumHealth >= 1 && facts.MinimumHealth <= 10) bonus += 10;
        int gold = 0, dust = 0;
        int kind = StableSeed.Index(StableSeed.Derive(rollSeed, 1), 4);
        if (kind < 2) gold = 40 + StableSeed.Index(StableSeed.Derive(rollSeed, 2), 41);
        else if (kind == 2) dust = 40 + StableSeed.Index(StableSeed.Derive(rollSeed, 3), 41);
        else {
            gold = 30 + StableSeed.Index(StableSeed.Derive(rollSeed, 2), 31);
            dust = 30 + StableSeed.Index(StableSeed.Derive(rollSeed, 3), 31);
        }
        if (gold > 0) gold += bonus; else dust += bonus;
        return new LocalReward { Gold = gold, Dust = dust, BonusGold = bonus, PackReward = bonus >= 10 };
    }

    // V4 keeps only V3's deterministic base roll. Achievements pay through separate catalog receipts.
    internal static LocalReward RewardV4(string result, int turns, bool conceded, int rollSeed)
    {
        if (conceded || result != "Won" || turns < 4) return new LocalReward();
        int kind = StableSeed.Index(StableSeed.Derive(rollSeed, 1), 4);
        if (kind < 2) return new LocalReward { Gold = 40 + StableSeed.Index(StableSeed.Derive(rollSeed, 2), 41) };
        if (kind == 2) return new LocalReward { Dust = 40 + StableSeed.Index(StableSeed.Derive(rollSeed, 3), 41) };
        return new LocalReward {
            Gold = 30 + StableSeed.Index(StableSeed.Derive(rollSeed, 2), 31),
            Dust = 30 + StableSeed.Index(StableSeed.Derive(rollSeed, 3), 31)
        };
    }

    // V5 keeps the V4 win roll; a completed loss or draw pays one third before tier scaling. A draw is not a win,
    // but still represents a full game played (owner 2026-10-31). Historical V4 receipts retain their win-only rule.
    internal static LocalReward RewardV5(string result, int turns, bool conceded, int rollSeed)
    {
        if (conceded || turns < 4 || (result != "Won" && result != "Lost" && result != "Tied")) return new LocalReward();
        var reward = RewardV4("Won", turns, false, rollSeed);
        if (result == "Won") return reward;
        return new LocalReward {
            Gold = checked((int)Math.Round(reward.Gold / 3d, MidpointRounding.AwayFromZero)),
            Dust = checked((int)Math.Round(reward.Dust / 3d, MidpointRounding.AwayFromZero))
        };
    }

    // V6 always pays both currencies, mostly gold; retain V5 eligibility and loss/draw thirds.
    internal static LocalReward RewardV6(string result, int turns, bool conceded, int rollSeed)
    {
        if (conceded || turns < 4 || (result != "Won" && result != "Lost" && result != "Tied")) return new LocalReward();
        int gold = 40 + StableSeed.Index(StableSeed.Derive(rollSeed, 2), 31);
        int dust = 10 + StableSeed.Index(StableSeed.Derive(rollSeed, 3), 21);
        return new LocalReward {
            Gold = result == "Won" ? gold : GrindTiers.Scale(gold, 1d / 3),
            Dust = result == "Won" ? dust : GrindTiers.Scale(dust, 1d / 3)
        };
    }

    // V3 great-win pack set: one of the Standard sets, weighted by the playable copies still missing from its pack pool at
    // settlement (a complete set weighs 0); every set complete falls back to the newest set. Drawn from the match's roll seed.
    internal static string RewardPackSet(int rollSeed, IReadOnlyList<KeyValuePair<string, int>> missingBySet, string fallback)
    {
        long total = 0;
        foreach (var set in missingBySet)
        {
            if (set.Value < 0) throw new ArgumentOutOfRangeException(nameof(missingBySet));
            total += set.Value;
        }
        if (total == 0) return fallback;
        int pick = StableSeed.Index(StableSeed.Derive(rollSeed, 4), checked((int)total));
        foreach (var set in missingBySet)
        {
            if (pick < set.Value) return set.Key;
            pick -= set.Value;
        }
        throw new InvalidOperationException("Unreachable pack-set weight.");
    }

    internal static void ValidateFacts(LocalRewardFacts facts)
    {
        if (facts == null || facts.DistinctCardsPlayed < 0 || facts.DistinctCardsPlayed > 1000000
            || facts.EnemyMinionsDefeated < 0 || facts.EnemyMinionsDefeated > 1000000
            || facts.MinimumHealth < 0 || facts.MinimumHealth > 30)
            throw new InvalidOperationException("Invalid local reward facts.");
    }

    internal static bool PackPoolComplete(IReadOnlyDictionary<string, int> collection) =>
        LocalCardCatalog.PackPool(LocalCardCatalog.CurrentPoolVersion)
            .All(card => collection.TryGetValue(card.Id, out int count) && count >= PlayableCap(card));

    internal static LocalPackResult GeneratePack(Dictionary<string, int> collection, int seed, int sequence)
        => GeneratePack(collection, seed, sequence, LocalCardCatalog.CurrentPoolVersion);

    internal static LocalPackResult GeneratePack(Dictionary<string, int> collection, int seed, int sequence, int poolVersion)
    {
        IReadOnlyList<EngineCard> packCatalog = LocalCardCatalog.PackPool(poolVersion);
        var random = new LocalRandom(seed, sequence);
        var result = new LocalPackResult { PoolVersion = poolVersion };
        for (int slot = 0; slot < 5; slot++)
        {
            Rarity rarity = RollRarity(random, slot == 0);
            var exactMissing = packCatalog.Where(c => c.Rarity == rarity && Owned(collection, c.Id) < PlayableCap(c)).ToList();
            var qualifying = packCatalog.Where(c => slot == 0 ? c.Rarity == Rarity.RARE || c.Rarity == Rarity.EPIC
                || c.Rarity == Rarity.LEGENDARY : true).ToList();
            var missing = qualifying.Where(c => Owned(collection, c.Id) < PlayableCap(c)).ToList();
            var choices = exactMissing.Count > 0 ? exactMissing : missing.Count > 0 ? missing : qualifying;
            EngineCard card = choices[random.Next(choices.Count)];
            bool duplicate = Owned(collection, card.Id) >= PlayableCap(card);
            result.Cards.Add(new LocalPackCard { Id = card.Id, DuplicateGold = duplicate ? 10 : 0 });
            if (!duplicate) collection[card.Id] = Owned(collection, card.Id) + 1;
        }
        return result;
    }

    private static int Owned(IReadOnlyDictionary<string, int> collection, string id) =>
        collection.TryGetValue(id, out int count) ? count : 0;

    private static Rarity RollRarity(LocalRandom random, bool guaranteed)
    {
        int roll = random.Next(100);
        if (guaranteed) return roll < 75 ? Rarity.RARE : roll < 95 ? Rarity.EPIC : Rarity.LEGENDARY;
        return roll < 75 ? Rarity.COMMON : roll < 95 ? Rarity.RARE : roll < 99 ? Rarity.EPIC : Rarity.LEGENDARY;
    }

    private sealed class LocalRandom
    {
        private uint state;
        internal LocalRandom(int seed, int sequence)
        {
            state = unchecked((uint)seed * 747796405u + (uint)sequence * 2891336453u + 277803737u);
            if (state == 0) state = 0x9e3779b9u;
        }
        internal int Next(int maximum)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % (uint)maximum);
        }
    }
}

// IMGUI Result panel text for a Reward V3 match (LocalLadder.DrawResult): the gold/dust roll and the half-weight bonus,
// dust-only rolls included. saved = false while settlement has not committed (the roll is not known yet).
internal sealed class LocalResultPanel
{
    public string Total, Gate, Pack;
    public readonly List<KeyValuePair<bool, string>> Rows = new List<KeyValuePair<bool, string>>();

    internal static LocalResultPanel Rolled(string outcome, int turns, bool conceded, LocalRewardFacts facts,
        bool saved, int gold, int dust, int bonus, bool pack, int rewardVersion = LocalEconomy.RollRewardVersion)
    {
        facts = facts ?? new LocalRewardFacts();
        bool rolls = !conceded && outcome == "Won" && turns >= 4;
        int rolledGold = gold > 0 ? gold - bonus : 0, rolledDust = gold > 0 ? dust : dust - bonus;
        var panel = new LocalResultPanel {
            Total = !saved ? "Reward pending" : gold == 0 && dust == 0 ? "No gold or dust"
                : Currency(gold, dust) + (bonus > 0 ? " · bonus +" + bonus + (gold > 0 ? " gold" : " dust") : ""),
            Gate = conceded ? "No rewards: conceded" : outcome == "Abandoned" ? "No rewards: interrupted"
                : turns < 4 ? "No rewards: fewer than four engine turns"
                : rewardVersion >= 5 && (outcome == "Lost" || outcome == "Tied")
                    ? (outcome == "Tied" ? "Draw" : "Loss") + " reward: 1/3 of a victory roll"
                : outcome != "Won" ? "No gold or dust: only victories roll a reward"
                : saved ? "Victory roll: " + Currency(rolledGold, rolledDust) : "Victory roll pending",
            Pack = !saved ? "Pack reward pending" : pack ? "Great win: 1 pack" : "No bonus pack"
        };
        panel.Rows.Add(new KeyValuePair<bool, string>(rolls && facts.DistinctCardsPlayed >= 6, facts.DistinctCardsPlayed >= 6
            ? "6 distinct cards played +5" : Math.Min(6, facts.DistinctCardsPlayed) + "/6 distinct cards played"));
        panel.Rows.Add(new KeyValuePair<bool, string>(rolls && facts.EnemyMinionsDefeated >= 5, facts.EnemyMinionsDefeated >= 5
            ? "5 enemy minions defeated +5" : Math.Min(5, facts.EnemyMinionsDefeated) + "/5 enemy minions defeated"));
        bool low = facts.MinimumHealth >= 1 && facts.MinimumHealth <= 10;
        panel.Rows.Add(new KeyValuePair<bool, string>(rolls && low, rolls && low
            ? "Won after observed 1-10 health +10" : "Minimum health: " + facts.MinimumHealth));
        return panel;
    }

    private static string Currency(int gold, int dust) => gold > 0 && dust > 0 ? "+" + gold + " local gold · +" + dust + " dust"
        : gold > 0 ? "+" + gold + " local gold" : "+" + dust + " dust";
}
