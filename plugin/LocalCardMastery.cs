using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public sealed class LocalMasteryGrant
{
    public string CardId;
    public int Premium, Copies, Wins, Points, ThresholdVersion;
    public string MatchId;
    public string GrantedUtc;
    public bool ShouldSerializePoints() => ThresholdVersion >= 2;
    public bool ShouldSerializeGrantedUtc() => GrantedUtc != null;
}

internal sealed class LocalMasteryProgress
{
    internal string CardId;
    internal int Premium, Points, Threshold, Wins, RequiredWins;

    internal void DisplayWins(GrindTier pace)
    {
        int step = pace.MasteryPointsPerWin;
        RequiredWins = (Threshold + step - 1) / step;
        int remaining = (Math.Max(0, Threshold - Points) + step - 1) / step;
        Wins = RequiredWins - remaining;
    }
}

internal static class LocalCardMastery
{
    internal static readonly int ThresholdVersion;
    private static readonly Dictionary<string, int> Golden;
    private static readonly int Signature, Diamond;
    private static readonly Dictionary<string, EngineCard> Definitions = Cards.All.Where(card => card.Collectible)
        .ToDictionary(card => card.Id, StringComparer.Ordinal);

    static LocalCardMastery()
    {
        using (var stream = typeof(LocalCardMastery).Assembly.GetManifestResourceStream("SoloStone.Progression.card-mastery.json")) {
            if (stream == null) throw new InvalidDataException("Missing card mastery data.");
            var root = EmbeddedJson.Object(EmbeddedJson.Read(stream));
            if (EmbeddedJson.Integer(root, "schemaVersion") != 1) throw new InvalidDataException("Unknown card mastery schema.");
            ThresholdVersion = EmbeddedJson.Integer(root, "thresholdVersion") ?? 0;
            var thresholds = EmbeddedJson.Object(root["thresholds"]);
            var golden = EmbeddedJson.Object(thresholds["golden"]);
            Golden = new[] { "FREE", "COMMON", "RARE", "EPIC", "LEGENDARY" }
                .ToDictionary(rarity => rarity, rarity => EmbeddedJson.Integer(golden, rarity) ?? 0, StringComparer.Ordinal);
            Signature = EmbeddedJson.Integer(thresholds, "signature") ?? 0;
            Diamond = EmbeddedJson.Integer(thresholds, "diamond") ?? 0;
            if (ThresholdVersion < 1 || Golden.Values.Any(value => value < 1) || Signature <= Golden.Values.Max() || Diamond <= Signature)
                throw new InvalidDataException("Invalid card mastery thresholds.");
            LocalCardPremium.Validate();
        }
    }

    internal static bool EligibleWin(LocalMatchRecord match) => match != null && match.Result == "Won" && !match.Conceded
        && match.Turns >= 4 && (match.Mode == null || match.Mode == "Ranked" || CasualRules.IsCasual(match.Mode))
        && match.AchievementContext != null && match.AchievementContext.EligibleWorldMatch && match.AchievementContext.PersonalDeck;

    internal static Dictionary<string, int> WinCounts(IEnumerable<LocalMatchRecord> history) => history.Where(EligibleWin)
        .SelectMany(match => (match.AchievementContext.MainDeck ?? new List<LocalAchievementDeckCard>())
            .Select(card => card.BaseCardId).Distinct(StringComparer.Ordinal))
        .GroupBy(id => id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    internal static Dictionary<string, int> PointCounts(IEnumerable<LocalMatchRecord> history) => history.Where(EligibleWin)
        .SelectMany(match => (match.AchievementContext.MainDeck ?? new List<LocalAchievementDeckCard>())
            .Select(card => card.BaseCardId).Distinct(StringComparer.Ordinal)
            .Select(id => new { Id = id, Points = GrindTiers.Find(match.GrindTier).MasteryPointsPerWin }))
        .GroupBy(row => row.Id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Sum(row => row.Points), StringComparer.Ordinal);

    internal static IEnumerable<LocalMasteryProgress> Tiers(string id, int points, GrindTier pace = null)
    {
        if (!Definitions.TryGetValue(id, out var card) || !Golden.TryGetValue(card.Rarity.ToString(), out int golden)) yield break;
        foreach (int premium in new[] { 1, 3, 2 }) {
            if (premium == 3 && !LocalCardPremium.HasSignature(id, LocalClassRewards.ActiveMeta)
                || premium == 2 && !LocalCardPremium.HasDiamond(id, LocalClassRewards.ActiveMeta)) continue;
            var tier = new LocalMasteryProgress { CardId = id, Premium = premium, Points = points,
                Threshold = premium == 1 ? golden : premium == 3 ? Signature : Diamond };
            tier.DisplayWins(pace ?? GrindTiers.Standard);
            yield return tier;
        }
    }

    internal static bool Grant(LocalProfileData profile, LocalMatchRecord match)
    {
        if (profile.PackRevealPending) return false;
        var history = profile.Matches.AsEnumerable();
        if (match != null && !profile.Matches.Any(row => row.Id == match.Id)) history = history.Append(match);
        var wins = WinCounts(history);
        var points = PointCounts(history);
        var claimed = new HashSet<string>(profile.MasteryGrants.Select(Key), StringComparer.Ordinal);
        var grants = new List<LocalMasteryGrant>();
        foreach (var pair in points.OrderBy(row => row.Key, StringComparer.Ordinal))
            foreach (var tier in Tiers(pair.Key, pair.Value)) {
                if (tier.Points < tier.Threshold || !claimed.Add(tier.CardId + ":" + tier.Premium)) continue;
                int owned = profile.PremiumCollection.Where(row => row.Id == tier.CardId
                    && LocalCrafting.PremiumQuality(row.Premium) >= LocalCrafting.PremiumQuality(tier.Premium))
                    .Sum(row => LocalProfile.ProgressionGrantedCount(profile, row.Id, row.Premium));
                var grant = new LocalMasteryGrant { CardId = tier.CardId, Premium = tier.Premium,
                    Copies = Math.Max(0, LocalCrafting.Playset(tier.CardId) - owned), Wins = wins[tier.CardId], Points = tier.Points,
                    ThresholdVersion = ThresholdVersion, MatchId = match?.Id, GrantedUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) };
                profile.MasteryGrants.Add(grant);
                grants.Add(Clone(grant));
                if (grant.Copies > 0) LocalProfile.AddPremium(profile, grant.CardId, grant.Premium, grant.Copies);
                int count = profile.PremiumCollection.SingleOrDefault(row => row.Id == grant.CardId && row.Premium == grant.Premium)?.Count ?? 0;
                var decks = profile.CustomDecks.AsEnumerable();
                if (profile.CustomDeck != null && !profile.CustomDecks.Contains(profile.CustomDeck)) decks = decks.Append(profile.CustomDeck);
                foreach (var deck in decks) {
                    LocalCrafting.ReconcileUpgradeDeck(deck, grant.CardId, 0, count, grant.Premium);
                }
            }
        if (match != null && (grants.Count > 0 || match.MasteryGrants == null)) match.MasteryGrants = grants;
        return grants.Count > 0;
    }

    private static int OwnedAtLeast(IEnumerable<LocalCardCount> owned, LocalMasteryProgress tier) => owned.Where(row => row.Id == tier.CardId
        && LocalCrafting.PremiumQuality(row.Premium) >= LocalCrafting.PremiumQuality(tier.Premium)).Sum(row => row.Count);

    // Results pass progression counts; sellable pack/crafted copies never skip a tier.
    internal static LocalMasteryProgress[] Next(IEnumerable<LocalMatchRecord> history, LocalMatchRecord match, IEnumerable<LocalCardCount> owned, GrindTier pace = null)
    {
        owned = owned ?? Array.Empty<LocalCardCount>();
        if (match.AchievementContext == null || !match.AchievementContext.PersonalDeck || !match.AchievementContext.EligibleWorldMatch)
            return Array.Empty<LocalMasteryProgress>();
        var points = PointCounts(history);
        return match.AchievementContext.MainDeck.Select(card => card.BaseCardId).Distinct(StringComparer.Ordinal)
            .Select(id => Tiers(id, points.TryGetValue(id, out int count) ? count : 0, pace)
                .FirstOrDefault(tier => tier.Points < tier.Threshold && OwnedAtLeast(owned, tier) < LocalCrafting.Playset(id)))
            .Where(tier => tier != null).OrderByDescending(tier => (double)tier.Points / tier.Threshold)
            .ThenBy(tier => tier.CardId, StringComparer.Ordinal).Take(2).ToArray();
    }

    private static string Key(LocalMasteryGrant row) => row.CardId + ":" + row.Premium;
    internal static void Validate(LocalProfileData profile)
    {
        var matches = new HashSet<string>(profile.Matches.Select(match => match.Id), StringComparer.Ordinal);
        if (profile.MasteryGrants == null || profile.MasteryGrants.Count > 100000
            || profile.MasteryGrants.Any(row => row == null || !LocalProfile.ValidAchievementCardId(row.CardId)
                || row.Premium < 1 || row.Premium > 3 || row.Copies < 0 || row.Copies > LocalCrafting.Playset(row.CardId)
                || row.GrantedUtc != null && !LocalProfile.ValidAchievementDate(row.GrantedUtc)
                || row.Wins < 1 || row.Points < 0 || row.ThresholdVersion < 1 || row.ThresholdVersion > ThresholdVersion
                || row.ThresholdVersion == ThresholdVersion && !ValidPoints(row) || row.MatchId != null && !matches.Contains(row.MatchId))
            || profile.MasteryGrants.Select(Key).Distinct(StringComparer.Ordinal).Count() != profile.MasteryGrants.Count)
            throw new InvalidOperationException("Invalid card mastery ledger.");
        var ledger = profile.MasteryGrants.ToDictionary(Key, StringComparer.Ordinal);
        foreach (var match in profile.Matches.Concat(profile.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { profile.Pending })) {
            if (match.MasteryGrants == null || match.MasteryGrants.Count == 0) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (match == profile.Pending || match.Result == null) throw new InvalidOperationException("Pending match has mastery grants.");
            foreach (var row in match.MasteryGrants)
                if (row == null || !seen.Add(Key(row)) || !ledger.TryGetValue(Key(row), out var grant)
                    || row.MatchId != match.Id || grant.MatchId != row.MatchId || row.Copies != grant.Copies
                    || row.Wins != grant.Wins || row.Points != grant.Points || row.ThresholdVersion != grant.ThresholdVersion || row.GrantedUtc != grant.GrantedUtc)
                    throw new InvalidOperationException("Invalid card mastery receipt.");
        }
    }

    // Older grants are historical facts; never apply today's thresholds or active-meta availability to them.
    private static bool ValidPoints(LocalMasteryGrant row)
    {
        if (!Definitions.TryGetValue(row.CardId, out var card) || !Golden.TryGetValue(card.Rarity.ToString(), out int golden)) return false;
        int threshold = row.Premium == 1 ? golden : row.Premium == 3 ? Signature : Diamond;
        return row.Points >= threshold;
    }

    internal static LocalMasteryGrant Clone(LocalMasteryGrant row) => new LocalMasteryGrant {
        CardId = row.CardId, Premium = row.Premium, Copies = row.Copies, Wins = row.Wins, Points = row.Points,
        ThresholdVersion = row.ThresholdVersion, MatchId = row.MatchId, GrantedUtc = row.GrantedUtc
    };
}
