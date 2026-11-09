using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public sealed partial class LocalProfile
{
    internal const int AchievementCollectionRosterVersion = 1;
    private static readonly string[] AchievementExpansionRosterKeys = {
        "EMERALD_DREAM", "THE_LOST_CITY", "TIME_TRAVEL", "CATACLYSM", "ESCAPEFROM_VIOLET_HOLD", "BE"
    };
    private static readonly string[] AchievementClasses = {
        "DEATHKNIGHT", "DEMONHUNTER", "DRUID", "HUNTER", "MAGE", "PALADIN",
        "PRIEST", "ROGUE", "SHAMAN", "WARLOCK", "WARRIOR"
    };

    private static LocalAchievementState NewAchievementState() => new LocalAchievementState {
        CatalogVersion = LocalAchievementCatalog.Version,
        Career = new LocalAchievementCareer {
            ClassWins = AchievementClasses.Select(hero => new LocalAchievementClassWins { Hero = hero }).ToList()
        }
    };

    internal LocalAchievementPendingContext AchievementContextFor(LocalDeck deck, bool personal,
        string npcId, double npcRating, string npcBand, IEnumerable<LocalDeckCard> premiumEntries = null, IEnumerable<LocalSideboard> premiumSideboards = null)
    {
        if (deck == null) throw new ArgumentNullException(nameof(deck));
        var owned = new HashSet<string>(RealOwnedCollection(data).Where(row => row.Value > 0)
            .Select(row => LocalCardCatalog.CopyGroup(row.Key)), StringComparer.Ordinal);
        return new LocalAchievementPendingContext {
            EligibleWorldMatch = true,
            PersonalDeck = personal,
            StartingHero = deck.Hero,
            StartingHeroClass = Cards.FromId(deck.Hero).Class.ToString(),
            MainDeck = AchievementCards(deck.CardIds, premiumEntries),
            Sideboards = AchievementCards(deck.Sideboards.SelectMany(row => row.Value), premiumSideboards?.SelectMany(row => row.CardEntries)),
            CraftedAndOwnedBaseCardIds = data.Achievements.Career.CraftedBaseCardIds
                .Where(owned.Contains).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            QueuedNpcId = npcId,
            QueuedNpcRating = npcRating,
            QueuedNpcBand = npcBand
        };
    }

    private static List<LocalAchievementDeckCard> AchievementCards(IEnumerable<string> ids, IEnumerable<LocalDeckCard> premiumEntries = null)
    {
        var versions = (premiumEntries ?? Enumerable.Empty<LocalDeckCard>()).GroupBy(row => row.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(row => row.Premium)), StringComparer.Ordinal);
        return (ids ?? Enumerable.Empty<string>()).Select(id => {
            var card = AchievementCard(Cards.FromId(id), 1);
            card.Premium = versions.TryGetValue(id, out var entries) && entries.Count > 0 ? entries.Dequeue() : 0;
            return card;
        }).GroupBy(AchievementCardTuple, StringComparer.Ordinal)
            .Select(group => { var row = group.First(); row.Copies = group.Count(); return row; })
            .OrderBy(AchievementCardTuple, StringComparer.Ordinal).ToList();
    }

    private static LocalAchievementDeckCard AchievementCard(EngineCard card, int copies) => new LocalAchievementDeckCard {
        BaseCardId = card.Id, Copies = copies, CardType = card.Type.ToString(),
        CardClass = card.Class.ToString(), BaseCost = card.Cost, Rarity = card.Rarity.ToString(), CardSet = card.Set.ToString()
    };

    private static string AchievementCardTuple(LocalAchievementDeckCard row) => string.Join("\u001F", new[] {
        row.BaseCardId, row.CardType, row.CardClass, row.BaseCost.ToString(CultureInfo.InvariantCulture), row.Rarity, row.CardSet
    }) + (row.Premium == 0 ? "" : "\u001F" + row.Premium);

    private static void SettleAchievements(LocalProfileData next, LocalMatchRecord match, LocalAchievementMatchEvidence evidence)
    {
        var pending = match.AchievementContext;
        if (pending == null) {
            if (evidence != null) throw new InvalidOperationException("Ineligible match supplied achievement evidence.");
            return;
        }
        if (evidence == null || !evidence.Available || string.IsNullOrEmpty(evidence.Fingerprint)
            || pending.MatchId != match.Id || evidence.MatchId != match.Id || evidence.Result != match.Result
            || evidence.StartingHero != pending.StartingHero || evidence.OpponentNpcId != pending.QueuedNpcId
            || evidence.OpponentBand != pending.QueuedNpcBand || evidence.PlayerConceded != match.Conceded
            || evidence.OpponentConceded != (match.NpcConceded == true)
            || evidence.ContextBinding != LocalAchievementContextBinding.Create(pending)
            || !ValidFingerprint(evidence.SourceFingerprint)
            || evidence.Fingerprint != LocalAchievementFingerprint.Create(evidence.ContextBinding, evidence.SourceFingerprint,
                evidence.Result, evidence.StartingHero, evidence.OpponentNpcId, evidence.OpponentBand,
                evidence.PlayerConceded, evidence.OpponentConceded, evidence.NaturalDraw))
            throw new InvalidOperationException("Achievement evidence does not match the pending match.");
        // Linked achievements are awarded only by the match that earned the tier receipt. Fresh V14 has no catch-up.
        var milestoneIds = (match.TierRewards ?? new List<LocalTierReward>()).Select(row => row.MilestoneId);
        var ledger = new LocalAchievementLedgerContext(next.Achievements.Career.CompletedExpansion,
            next.Achievements.Career.CompletedExpansionCatalogVersion, milestoneIds, next.MasteryGrants, match.Turns);
        var evaluation = LocalAchievementEvaluator.Evaluate(pending, evidence, next.Achievements.Career,
            next.Achievements.Unlocks.Select(row => row.Id).ToArray(), ledger);
        ApplyCareer(next.Achievements.Career, evaluation.CareerDelta);
        foreach (var candidate in evaluation.Earned)
        {
            var definition = LocalAchievementCatalog.Get(candidate.Id);
            string provenance = definition.Reward.LinkedMilestoneId == null ? "Match" : "Ladder";
            if (provenance == "Ladder" && !(match.TierRewards ?? new List<LocalTierReward>())
                .Any(row => row.MilestoneId == definition.Reward.LinkedMilestoneId))
                throw new InvalidOperationException("Linked achievement lacks this match's milestone receipt.");
            ApplyAchievementGrant(next, candidate.Id, provenance, match, Witness(candidate.Witness));
        }
        if (match.AchievementGrants.Count > 0) next.PendingAchievementToastMatchIds.Add(match.Id);
        match.AchievementEvidenceFingerprint = evidence.Fingerprint;
    }

    private static void ApplyCareer(LocalAchievementCareer career, LocalAchievementCareerDelta delta)
    {
        career.EligibleWins = Math.Min(100, checked(career.EligibleWins + delta.EligibleWins));
        if (delta.WinningHeroClass != null)
        {
            var row = career.ClassWins.Single(item => item.Hero == delta.WinningHeroClass);
            row.Wins = Math.Min(10, checked(row.Wins + 1));
        }
        career.PlayedCollectibleBaseCardIds = career.PlayedCollectibleBaseCardIds
            .Concat(delta.PlayedCollectibleBaseCardIds).Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).Take(250).ToList();
        career.CreditedEnemyMinionDeaths = Math.Min(1000, checked(career.CreditedEnemyMinionDeaths + delta.CreditedEnemyMinionDeaths));
        if (delta.DefeatedNpcId != null) career.DefeatedNpcIds = career.DefeatedNpcIds.Concat(new[] { delta.DefeatedNpcId })
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).Take(25).ToList();
    }

    private static bool ApplyAchievementGrant(LocalProfileData next, string id, string provenance,
        LocalMatchRecord match, LocalAchievementWitnessRecord witness)
    {
        if (next.Achievements.Unlocks.Any(row => row.Id == id)) return false;
        var receipt = LocalAchievementGrantReceipt.FromCatalog(id);
        next.GoldBalance = checked(next.GoldBalance + receipt.Gold);
        next.DustBalance = checked(next.DustBalance + receipt.Dust);
        if (receipt.StandardPacks > 0) AddPacks(next, FairPackRules.Standard, receipt.StandardPacks);
        if (receipt.CardBackId > 0 && !next.OwnedCardBacks.Contains(receipt.CardBackId)) next.OwnedCardBacks.Add(receipt.CardBackId);
        if (receipt.HeroSkinId != null) GrantHeroSkin(next, receipt.HeroSkinId);
        next.Achievements.Unlocks.Add(new LocalAchievementUnlock { Id = id, CatalogVersion = LocalAchievementCatalog.Version,
            UnlockedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), MatchId = match?.Id,
            Provenance = provenance, Witness = witness });
        next.Achievements.Grants.Add(Copy(receipt));
        if (match != null) match.AchievementGrants.Add(Copy(receipt));
        return true;
    }

    internal static void TrackCraftedAchievementCards(LocalProfileData next, IEnumerable<string> cardIds)
    {
        if (next.Achievements.Unlocks.Any(row => row.Id == "made-to-measure")) return;
        next.Achievements.Career.CraftedBaseCardIds = next.Achievements.Career.CraftedBaseCardIds
            .Concat(cardIds.Select(LocalCardCatalog.CopyGroup)).Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).Take(MaximumRecords).ToList();
    }

    // Creation cheats start with what their state already accomplishes: granted with normal rewards in the creation
    // transaction, without a toast (the browser shows them unlocked). Everything else is earned normally.
    private static void GrantCreationAchievements(LocalProfileData next, ProfileOptions options)
    {
        if (options.StartLegendOne)
        {
            var milestones = new HashSet<string>(next.MilestoneCatchUp.TierRewards.Select(row => row.MilestoneId), StringComparer.Ordinal);
            foreach (var definition in LocalAchievementCatalog.All.Where(row => row.Reward.LinkedMilestoneId != null
                && milestones.Contains(row.Reward.LinkedMilestoneId)))
                ApplyAchievementGrant(next, definition.Id, "Creation", null, new LocalAchievementWitnessRecord {
                    Kind = "Career", Value = 1, Identity = definition.Reward.LinkedMilestoneId });
        }
        // Sandbox All decks grants what buying the decks would (Purchase reconciles the collection achievement too); like
        // All cards, a satisfied collection achievement is granted silently at creation.
        if (options.AllCards || options.AllDecks) { ReconcileCollectionAchievement(next); next.PendingNonMatchAchievementNoticeId = null; }
    }

    internal static void ReconcileCollectionAchievement(LocalProfileData next)
    {
        if (next.Achievements.Unlocks.Any(row => row.Id == "the-complete-works")) return;
        var owned = RealOwnedCollection(next);
        foreach (var roster in ExpansionRosters())
        {
            if (!roster.Value.All(card => owned.TryGetValue(LocalCardCatalog.CopyGroup(card.Id), out int count)
                && count >= Playset(card.Id))) continue;
            next.Achievements.Career.CompletedExpansion = roster.Key;
            next.Achievements.Career.CompletedExpansionCatalogVersion = AchievementCollectionRosterVersion;
            var witness = new LocalAchievementWitnessRecord { Kind = "Career", Value = AchievementCollectionRosterVersion, Identity = roster.Key };
            if (ApplyAchievementGrant(next, "the-complete-works", "Collection", null, witness))
                next.PendingNonMatchAchievementNoticeId = "the-complete-works";
            return;
        }
    }

    // An absent optional queue adopts only an old still-pending receipt, never acknowledged history.
    private static IEnumerable<string> AchievementToastIds(LocalProfileData profile)
    {
        if (profile.PendingAchievementToastMatchIdsSpecified) return profile.PendingAchievementToastMatchIds;
        if (profile.PendingRewardPresentationId == null) return Enumerable.Empty<string>();
        return profile.Matches.Where(row => row.Id == profile.PendingRewardPresentationId && row.AchievementGrants?.Count > 0).Select(row => row.Id);
    }

    internal string PendingAchievementToastMatchId => AchievementToastIds(data).FirstOrDefault();

    // Independent of reward boxes: a failed/covered toast must survive Done and a restart.
    internal LocalMatchRecord PendingAchievementToast
    {
        get
        {
            string id = PendingAchievementToastMatchId;
            return id == null ? null : Copy(data.Matches.Single(row => row.Id == id));
        }
    }

    internal bool HasPendingAchievementToast(string matchId) => AchievementToastIds(data).Contains(matchId);

    internal bool AcknowledgeAchievementToast(string matchId)
    {
        if (!data.Matches.Any(row => row.Id == matchId && row.AchievementGrants.Count > 0))
            throw new InvalidOperationException("Unknown match achievement acknowledgement.");
        if (!HasPendingAchievementToast(matchId)) return false;
        var next = Snapshot();
        next.PendingAchievementToastMatchIds.Remove(matchId);
        Commit(next);
        return true;
    }

    public bool AcknowledgeNonMatchAchievementNotice(string id)
    {
        if (id != "the-complete-works" || !data.Achievements.Unlocks.Any(row => row.Id == id && row.Provenance == "Collection"))
            throw new InvalidOperationException("Unknown non-match achievement acknowledgement.");
        if (data.PendingNonMatchAchievementNoticeId == null) return false;
        if (data.PendingNonMatchAchievementNoticeId != id)
            throw new InvalidOperationException("Non-match achievement acknowledgement does not match.");
        if (data.PackRevealPending)
            throw new InvalidOperationException("Finish the pending pack reveal before acknowledging an achievement.");
        var next = Snapshot(); next.PendingNonMatchAchievementNoticeId = null; Commit(next); return true;
    }

    // Achievement ownership is durable collection inventory only. Core grants and deckbuilding loans are playable,
    // but neither proves that the player owns or still owns the card required by #55/#57.
    internal IReadOnlyDictionary<string, int> AchievementOwnedCollection => RealOwnedCollection(data);

    private static Dictionary<string, int> RealOwnedCollection(LocalProfileData profile)
    {
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in profile.Collection.Concat(profile.PremiumCollection))
        {
            string id = LocalCardCatalog.CopyGroup(row.Id);
            owned[id] = checked((owned.TryGetValue(id, out int count) ? count : 0) + row.Count);
        }
        return owned;
    }

    private static IReadOnlyDictionary<string, EngineCard[]> ExpansionRosters()
    {
#if ENGINE_SOURCE
        var all = LocalShop.StandardCollectionCards.Where(card => card != null && !LocalCardCatalog.IsCore(card.Id)).ToArray();
        var result = new SortedDictionary<string, EngineCard[]>(StringComparer.Ordinal);
        foreach (string type in FairPackRules.PackTypes.Where(type => type != FairPackRules.Standard && type != FairPackRules.Legacy))
        {
            var sets = new HashSet<CardSet>(FairPackRules.Eligible(type, FairPackRules.RuleVersion).Select(card => card.Set));
            var roster = all.Where(card => sets.Contains(card.Set))
                .GroupBy(card => LocalCardCatalog.CopyGroup(card.Id), StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(card => LocalCardCatalog.CopyGroup(card.Id), StringComparer.Ordinal).ToArray();
            if (roster.Length != 0) result.Add(type, roster);
        }
        return result;
#else
        return new Dictionary<string, EngineCard[]>();
#endif
    }

    private static LocalAchievementWitnessRecord Witness(LocalAchievementWitness witness)
    {
        var row = new LocalAchievementWitnessRecord { Sequence = witness.Sequence };
        switch (witness)
        {
            case LocalAchievementTerminalWitness _: row.Kind = "Terminal"; break;
            case LocalAchievementResolutionWitness value:
                row.Kind = "Resolution"; row.ResolutionId = value.ResolutionId; row.CastId = value.CastId;
                row.EntityIds = value.EntityIds.ToList(); break;
            case LocalAchievementAttackWitness value:
                row.Kind = "Attack"; row.AttackId = value.AttackId; row.EntityId = value.EntityId; row.Incarnation = value.Incarnation; break;
            case LocalAchievementTurnWitness value: row.Kind = "Turn"; row.Turn = value.Turn; break;
            case LocalAchievementEntityWitness value:
                row.Kind = "Entity"; row.EntityId = value.EntityId; row.Incarnation = value.Incarnation; break;
            case LocalAchievementDeckWitness value: row.Kind = "Deck"; row.BaseCardIds = value.BaseCardIds.ToList(); break;
            case LocalAchievementCareerWitness value: row.Kind = "Career"; row.Value = value.Value; row.Identity = value.Identity; break;
            case LocalAchievementThresholdWitness value: row.Kind = "Threshold"; row.Value = value.Value; break;
            default: throw new InvalidOperationException("Unknown achievement witness type.");
        }
        return row;
    }

    private static void ValidateAchievements(LocalProfileData profile)
    {
        var state = profile.Achievements;
        if (profile.Version != CurrentVersion || state == null || state.CatalogVersion != LocalAchievementCatalog.Version
            || state.Unlocks == null || state.Grants == null || state.Career == null
            || state.Unlocks.Count > LocalAchievementCatalog.All.Count || state.Grants.Count != state.Unlocks.Count)
            throw new InvalidOperationException("Invalid achievement state.");
        var known = new HashSet<string>(LocalAchievementCatalog.All.Select(row => row.Id), StringComparer.Ordinal);
        if (state.Unlocks.Any(row => row == null || !known.Contains(row.Id) || row.CatalogVersion != LocalAchievementCatalog.Version
                || !ValidAchievementDate(row.UnlockedUtc) || !ValidAchievementWitness(row.Id, row.Witness))
            || state.Unlocks.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != state.Unlocks.Count
            || state.Grants.Any(row => !ValidAchievementGrant(row))
            || state.Grants.Any(row => row.CardBackId > 0 && !AchievementExclusiveCosmetics.ContainsCardBack(row.CardBackId)
                || row.HeroSkinId != null && !AchievementExclusiveCosmetics.ContainsHeroSkin(row.HeroSkinId))
            || state.Grants.Select(row => row.AchievementId).Distinct(StringComparer.Ordinal).Count() != state.Grants.Count
            || !state.Unlocks.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(state.Grants.Select(row => row.AchievementId).OrderBy(id => id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Invalid achievement ledger.");
        if (profile.PendingAchievementToastMatchIdsSpecified && profile.PendingAchievementToastMatchIds == null)
            throw new InvalidOperationException("Invalid pending achievement toast queue.");
        var toastIds = AchievementToastIds(profile).ToArray();
        if (toastIds.Length > LocalAchievementCatalog.All.Count
            || toastIds.Distinct(StringComparer.Ordinal).Count() != toastIds.Length
            || toastIds.Any(id => string.IsNullOrEmpty(id)
                || !profile.Matches.Any(match => match.Id == id && match.AchievementGrants?.Count > 0)))
            throw new InvalidOperationException("Invalid pending achievement toast queue.");
        ValidateAchievementCareer(state.Career);
        foreach (var match in profile.Matches.Concat(profile.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { profile.Pending }))
            ValidateAchievementMatch(match, profile.Pending == match, state);
        foreach (var unlock in state.Unlocks)
        {
            if (unlock.Provenance == "Collection") {
                if (unlock.Id != "the-complete-works" || unlock.MatchId != null) throw new InvalidOperationException("Invalid collection achievement provenance.");
                continue;
            }
            if (unlock.Provenance == "Creation") {
                string linked = LocalAchievementCatalog.Get(unlock.Id).Reward.LinkedMilestoneId;
                if (unlock.MatchId != null || linked == null || profile.MilestoneCatchUp?.Id != CreationReceiptId
                    || !profile.MilestoneCatchUp.TierRewards.Any(row => row.MilestoneId == linked))
                    throw new InvalidOperationException("Creation achievement lacks its creation milestone receipt.");
                continue;
            }
            if (unlock.Provenance != "Match" && unlock.Provenance != "Ladder") throw new InvalidOperationException("Invalid achievement provenance.");
            var match = profile.Matches.SingleOrDefault(row => row.Id == unlock.MatchId);
            if (match == null || !match.AchievementGrants.Any(row => row.AchievementId == unlock.Id))
                throw new InvalidOperationException("Achievement unlock lacks match receipt.");
            var definition = LocalAchievementCatalog.Get(unlock.Id);
            if (unlock.Provenance == "Ladder" && (definition.Reward.LinkedMilestoneId == null
                || !(match.TierRewards ?? new List<LocalTierReward>()).Any(row => row.MilestoneId == definition.Reward.LinkedMilestoneId)))
                throw new InvalidOperationException("Linked achievement lacks milestone receipt.");
            if (unlock.Provenance == "Match" && definition.Reward.LinkedMilestoneId != null)
                throw new InvalidOperationException("Linked achievement has match provenance.");
        }
        if (profile.PendingNonMatchAchievementNoticeId != null && (profile.PendingNonMatchAchievementNoticeId != "the-complete-works"
            || !state.Unlocks.Any(row => row.Id == profile.PendingNonMatchAchievementNoticeId && row.Provenance == "Collection")))
            throw new InvalidOperationException("Invalid pending non-match achievement notice.");
        bool completed = state.Career.CompletedExpansion != null;
        if (completed != state.Unlocks.Any(row => row.Id == "the-complete-works")
            || completed && (state.Career.CompletedExpansionCatalogVersion != AchievementCollectionRosterVersion
                || !AchievementExpansionRosterKeys.Contains(state.Career.CompletedExpansion, StringComparer.Ordinal)))
            throw new InvalidOperationException("Invalid collection achievement state.");
    }

    private static void ValidateAchievementCareer(LocalAchievementCareer career)
    {
        if (career.EligibleWins < 0 || career.EligibleWins > 100 || career.ClassWins == null || career.ClassWins.Count != AchievementClasses.Length
            || !career.ClassWins.Select(row => row?.Hero).SequenceEqual(AchievementClasses)
            || career.ClassWins.Any(row => row.Wins < 0 || row.Wins > 10)
            || career.CreditedEnemyMinionDeaths < 0 || career.CreditedEnemyMinionDeaths > 1000
            || !ValidSortedIds(career.PlayedCollectibleBaseCardIds, 250, ValidAchievementCardId)
            || !ValidSortedIds(career.DefeatedNpcIds, 25, id => ValidName(id, 128))
            || !ValidSortedIds(career.CraftedBaseCardIds, MaximumRecords, ValidAchievementCardId)
            || (career.CompletedExpansion == null) != (career.CompletedExpansionCatalogVersion == 0))
            throw new InvalidOperationException("Invalid achievement career.");
    }

    // Copy-group identities are durable career keys recorded from the card data of their day. History is checked
    // for shape only: a later card-data fix, retired print or CopyGroup remap must never make a save unloadable.
    internal static bool ValidAchievementCardId(string id) => id != null && id.Length > 0 && id.Length <= 64
        && id.All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_');

    private static bool ValidSortedIds(List<string> ids, int maximum, Func<string, bool> valid) => ids != null && ids.Count <= maximum
        && ids.All(valid) && ids.SequenceEqual(ids.OrderBy(id => id, StringComparer.Ordinal))
        && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;

    private static void ValidateAchievementMatch(LocalMatchRecord match, bool pending, LocalAchievementState state)
    {
        if (!LocalEconomy.IsRolled(match.RewardVersion) || match.AchievementGrants == null
            || match.AchievementGrants.Select(row => row?.AchievementId).Distinct(StringComparer.Ordinal).Count() != match.AchievementGrants.Count
            || match.AchievementGrants.Any(row => !ValidAchievementGrant(row)))
            throw new InvalidOperationException("Invalid rolled achievement match receipt.");
        if (pending) {
            if (match.AchievementEvidenceFingerprint != null || match.AchievementGrants.Count != 0) throw new InvalidOperationException("Pending match has terminal achievement fields.");
        } else if (match.Result == "Abandoned") {
            if (match.AchievementEvidenceFingerprint != null || match.AchievementGrants.Count != 0) throw new InvalidOperationException("Abandoned match has achievement grants.");
        } else if (match.AchievementContext != null) {
            if (!ValidFingerprint(match.AchievementEvidenceFingerprint)) throw new InvalidOperationException("Eligible match lacks achievement fingerprint.");
        } else if (match.AchievementEvidenceFingerprint != null || match.AchievementGrants.Count != 0) {
            throw new InvalidOperationException("Ineligible match has achievement receipt.");
        }
        if (match.AchievementContext != null) ValidateAchievementContext(match.AchievementContext, match, state.Career);
        foreach (var receipt in match.AchievementGrants) {
            var unlock = state.Unlocks.SingleOrDefault(row => row.Id == receipt.AchievementId);
            if (unlock == null || unlock.MatchId != match.Id || (unlock.Provenance != "Match" && unlock.Provenance != "Ladder"))
                throw new InvalidOperationException("Match achievement receipt lacks global unlock.");
        }
    }

    private static void ValidateAchievementContext(LocalAchievementPendingContext context, LocalMatchRecord match, LocalAchievementCareer career)
    {
        if (!context.EligibleWorldMatch || context.MatchId != match.Id || context.StartingHero != match.DeckHero
            || !ValidStartingHeroClass(context.StartingHero, context.StartingHeroClass)
            || context.PersonalDeck != (match.Deck == "custom" || match.Deck.StartsWith("custom:", StringComparison.Ordinal))
            || context.QueuedNpcId != match.NpcId
            || context.QueuedNpcRating != match.NpcRating || (context.QueuedNpcBand != "Bronze" && context.QueuedNpcBand != "Silver"
                && context.QueuedNpcBand != "Gold" && context.QueuedNpcBand != "Platinum"
                && context.QueuedNpcBand != "Diamond" && context.QueuedNpcBand != "Legend")
            || !ValidAchievementCards(context.MainDeck) || !ValidAchievementCards(context.Sideboards)
            || !ValidSortedIds(context.CraftedAndOwnedBaseCardIds, MaximumRecords, ValidAchievementCardId)
            || context.CraftedAndOwnedBaseCardIds.Except(career.CraftedBaseCardIds, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Invalid frozen achievement context.");
    }

    private static bool ValidAchievementCards(List<LocalAchievementDeckCard> cards) => cards != null && cards.Count <= MaximumRecords
        && cards.All(row => row != null && row.Copies > 0 && row.Copies <= 30 && MatchesAchievementCardPrint(row)
            && row.Premium >= 0 && row.Premium <= 3
            && Enum.TryParse(row.CardType, out CardType _) && Enum.TryParse(row.CardClass, out CardClass _)
            && Enum.TryParse(row.Rarity, out Rarity _) && Enum.TryParse(row.CardSet, out CardSet _) && row.BaseCost >= 0)
        && cards.Select(AchievementCardTuple).Distinct(StringComparer.Ordinal).Count() == cards.Count
        && cards.Select(AchievementCardTuple).SequenceEqual(cards.Select(AchievementCardTuple).OrderBy(key => key, StringComparer.Ordinal));

    private static bool ValidStartingHeroClass(string hero, string heroClass)
    {
        try { return Cards.FromId(hero)?.Class.ToString() == heroClass && AchievementClasses.Contains(heroClass); }
        catch (KeyNotFoundException) { return false; }
    }

    // Frozen deck rows keep the cost/rarity/set they had at match start; never compare them with today's card data.
    private static bool MatchesAchievementCardPrint(LocalAchievementDeckCard row) => ValidAchievementCardId(row.BaseCardId);

    internal static bool ValidAchievementDate(string value) => DateTimeOffset.TryParseExact(value, "o", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var parsed) && parsed.Offset == TimeSpan.Zero;
    private static bool ValidFingerprint(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F');

    private static bool ValidAchievementGrant(LocalAchievementGrantReceipt receipt)
    {
        if (receipt == null) return false;
        LocalAchievementGrantReceipt expected;
        try { expected = LocalAchievementGrantReceipt.FromCatalog(receipt.AchievementId); } catch (ArgumentException) { return false; }
        return receipt.CatalogVersion == expected.CatalogVersion && receipt.Gold == expected.Gold && receipt.Dust == expected.Dust
            && receipt.StandardPacks == expected.StandardPacks && receipt.CardBackId == expected.CardBackId
            && receipt.HeroSkinId == expected.HeroSkinId && receipt.LinkedMilestoneId == expected.LinkedMilestoneId;
    }

    private static bool ValidWitness(LocalAchievementWitnessRecord row)
    {
        if (row == null || row.Sequence < 0 || row.ResolutionId < 0 || row.CastId < 0 || row.AttackId < 0
            || row.EntityId < 0 || row.Incarnation < 0 || row.Turn < 0 || row.Value < 0 || row.EntityIds == null || row.BaseCardIds == null
            || row.EntityIds.Any(id => id <= 0) || row.EntityIds.Distinct().Count() != row.EntityIds.Count
            || !ValidSortedIds(row.BaseCardIds, MaximumRecords, ValidAchievementCardId)) return false;
        bool noEntities = row.EntityIds.Count == 0, noCards = row.BaseCardIds.Count == 0;
        bool noText = row.Identity == null;
        switch (row.Kind) {
            case "Terminal": return row.Sequence > 0 && row.ResolutionId == 0 && row.CastId == 0 && row.AttackId == 0
                && row.EntityId == 0 && row.Incarnation == 0 && row.Turn == 0 && row.Value == 0 && noText && noEntities && noCards;
            case "Resolution": return row.Sequence > 0 && row.AttackId == 0 && row.EntityId == 0 && row.Incarnation == 0
                && row.Turn == 0 && row.Value == 0 && noText && row.EntityIds.Count > 0 && noCards;
            case "Attack": return row.Sequence > 0 && row.AttackId > 0 && row.EntityId > 0 && row.ResolutionId == 0
                && row.CastId == 0 && row.Turn == 0 && row.Value == 0 && noText && noEntities && noCards;
            case "Turn": return row.Sequence > 0 && row.Turn > 0 && row.ResolutionId == 0 && row.CastId == 0
                && row.AttackId == 0 && row.EntityId == 0 && row.Incarnation == 0 && row.Value == 0 && noText && noEntities && noCards;
            case "Entity": return row.Sequence > 0 && row.EntityId > 0 && row.ResolutionId == 0 && row.CastId == 0
                && row.AttackId == 0 && row.Turn == 0 && row.Value == 0 && noText && noEntities && noCards;
            case "Deck": return row.Sequence == 0 && row.ResolutionId == 0 && row.CastId == 0 && row.AttackId == 0
                && row.EntityId == 0 && row.Incarnation == 0 && row.Turn == 0 && row.Value == 0 && noText && noEntities
                && row.BaseCardIds.Count > 0;
            case "Career": return row.Sequence == 0 && row.ResolutionId == 0 && row.CastId == 0 && row.AttackId == 0
                && row.EntityId == 0 && row.Incarnation == 0 && row.Turn == 0 && row.Value > 0 && noEntities && noCards
                && (row.Identity == null || ValidName(row.Identity, 128));
            case "Threshold": return row.Sequence > 0 && row.Value > 0 && row.ResolutionId == 0 && row.CastId == 0
                && row.AttackId == 0 && row.EntityId == 0 && row.Incarnation == 0 && row.Turn == 0 && noText && noEntities && noCards;
            default: return false;
        }
    }

    private static bool ValidAchievementWitness(string id, LocalAchievementWitnessRecord row)
    {
        if (!ValidWitness(row) || row.Kind != ExpectedWitnessKind(id)) return false;
        if (id == "the-complete-works") return row.Value == AchievementCollectionRosterVersion
            && AchievementExpansionRosterKeys.Contains(row.Identity, StringComparer.Ordinal);
        string milestone = LocalAchievementCatalog.Get(id).Reward.LinkedMilestoneId;
        if (milestone != null) return row.Value == 1 && row.Identity == milestone;
        return row.Kind != "Career" || row.Identity == null;
    }

    private static string ExpectedWitnessKind(string id)
    {
        switch (id)
        {
            case "calculated": case "out-of-cards-not-ideas": case "untouchable": case "no-time-to-explain":
                return "Terminal";
            case "seven-card-pickup": case "mutually-assured-destruction": case "absolutely-unnecessary":
            case "shaman-the-whole-band": case "warlock-damage-is-a-resource": case "respectfully-no":
            case "one-of-everything": case "family-reunion": case "topdecking-is-a-skill": case "hostile-takeover":
            case "no-touching": case "power-trip": case "return-to-sender":
                return "Resolution";
            case "thanks-ill-take-it-from-here": case "fine-ill-do-it-myself": case "demon-hunter-face-value":
            case "hunter-whos-a-good-boy": case "paladin-entry-level-position": case "warrior-good-stats-for-the-cost":
            case "pocket-sized-problem": case "not-today":
                return "Attack";
            case "call-an-ambulance": case "mage-final-exam": case "rogue-combo-meal": case "from-downtown":
            case "population-boom": case "acceptable-losses": case "chain-reaction": case "special-delivery":
            case "borrowed-time":
                return "Turn";
            case "death-knight-grave-expenditure": case "druid-ahead-of-the-curve": case "priest-we-can-rebuild-you":
            case "armored-division": case "running-on-fumes":
                return "Threshold";
            case "aggressively-average": case "no-copies-no-problem": case "creature-feature": case "words-not-violence":
            case "no-famous-people": case "home-team": case "neutral-ground": case "small-ball": case "heavy-reading":
            case "all-premium-deck": case "one-expansion-stand": case "perfect-curve":
                return "Deck";
            case "a-hundred-good-games": case "class-act": case "well-read": case "the-board-is-a-workplace-hazard":
            case "the-complete-works": case "world-tour": case "made-to-measure": case "welcome-to-legend":
            case "first-signature-mastery": case "first-diamond-mastery": case "fifty-golden-masteries":
            case "among-the-hundred": case "there-can-be-only-one":
                return "Career";
            default: return null;
        }
    }

    private static LocalAchievementState Copy(LocalAchievementState state) => state == null ? null : new LocalAchievementState {
        CatalogVersion = state.CatalogVersion, Unlocks = state.Unlocks?.Select(Copy).ToList(),
        Grants = state.Grants?.Select(Copy).ToList(), Career = Copy(state.Career)
    };
    private static LocalAchievementUnlock Copy(LocalAchievementUnlock row) => row == null ? null : new LocalAchievementUnlock {
        Id = row.Id, CatalogVersion = row.CatalogVersion, UnlockedUtc = row.UnlockedUtc, MatchId = row.MatchId,
        Provenance = row.Provenance, Witness = Copy(row.Witness)
    };
    private static LocalAchievementGrantReceipt Copy(LocalAchievementGrantReceipt row) => row == null ? null : new LocalAchievementGrantReceipt {
        AchievementId = row.AchievementId, CatalogVersion = row.CatalogVersion, Gold = row.Gold, Dust = row.Dust,
        StandardPacks = row.StandardPacks, CardBackId = row.CardBackId, HeroSkinId = row.HeroSkinId, LinkedMilestoneId = row.LinkedMilestoneId
    };
    private static LocalAchievementCareer Copy(LocalAchievementCareer row) => row == null ? null : new LocalAchievementCareer {
        EligibleWins = row.EligibleWins, ClassWins = row.ClassWins?.Select(value => new LocalAchievementClassWins { Hero = value.Hero, Wins = value.Wins }).ToList(),
        PlayedCollectibleBaseCardIds = row.PlayedCollectibleBaseCardIds?.ToList(), CreditedEnemyMinionDeaths = row.CreditedEnemyMinionDeaths,
        DefeatedNpcIds = row.DefeatedNpcIds?.ToList(), CraftedBaseCardIds = row.CraftedBaseCardIds?.ToList(),
        CompletedExpansion = row.CompletedExpansion, CompletedExpansionCatalogVersion = row.CompletedExpansionCatalogVersion
    };
    private static LocalAchievementWitnessRecord Copy(LocalAchievementWitnessRecord row) => row == null ? null : new LocalAchievementWitnessRecord {
        Kind = row.Kind, Sequence = row.Sequence, ResolutionId = row.ResolutionId, CastId = row.CastId, AttackId = row.AttackId,
        EntityId = row.EntityId, Incarnation = row.Incarnation, Turn = row.Turn, Value = row.Value, Identity = row.Identity,
        EntityIds = row.EntityIds?.ToList(), BaseCardIds = row.BaseCardIds?.ToList()
    };
    private static LocalAchievementPendingContext Copy(LocalAchievementPendingContext row) => row == null ? null : new LocalAchievementPendingContext {
        MatchId = row.MatchId, EligibleWorldMatch = row.EligibleWorldMatch, PersonalDeck = row.PersonalDeck,
        StartingHero = row.StartingHero, StartingHeroClass = row.StartingHeroClass,
        MainDeck = row.MainDeck?.Select(Copy).ToList(), Sideboards = row.Sideboards?.Select(Copy).ToList(),
        CraftedAndOwnedBaseCardIds = row.CraftedAndOwnedBaseCardIds?.ToList(), QueuedNpcId = row.QueuedNpcId,
        QueuedNpcRating = row.QueuedNpcRating, QueuedNpcBand = row.QueuedNpcBand
    };
    private static LocalAchievementDeckCard Copy(LocalAchievementDeckCard row) => row == null ? null : new LocalAchievementDeckCard {
        BaseCardId = row.BaseCardId, Copies = row.Copies, CardType = row.CardType, CardClass = row.CardClass,
        BaseCost = row.BaseCost, Rarity = row.Rarity, CardSet = row.CardSet, Premium = row.Premium
    };
}
