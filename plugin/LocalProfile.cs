using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

// Pet XP events one match recorded, by counter (NativePetLikes). Handed to LocalProfile.Settle.
public sealed class LocalPetXpCounts
{
    public int Small, Medium, Large;
}

public sealed class LocalMatchRecord
{
    public string Id;
    public string Deck;
    public string DeckHero;
    public string Opponent;
    public int Seed;
    // Absent in older XML: those matches always started with the human. No schema migration needed.
    public int FirstPlayer = 1;
    public bool ShouldSerializeFirstPlayer() => FirstPlayer != 1;
    public string StartedUtc;
    public string SettledUtc;
    public bool ShouldSerializeSettledUtc() => SettledUtc != null;
    public string Result;
    public int Turns;
    public bool Conceded;
    public int Gold;
    public int RewardVersion;
    public string GrindTier;
    public bool ShouldSerializeGrindTier() => GrindTier != null;
    public int BonusGold;
    public bool PackReward;
    public List<LocalPackCount> Packs = new List<LocalPackCount>();
    public int DustReward;
    public LocalLadderReceipt Ladder;
    public int? ClassXpBefore;
    public int? ClassXpAfter;
    [XmlIgnore] public List<string> UnlockedCardIds;
    // XmlSerializer constructs empty List fields even for xsi:nil; arrays preserve absent legacy receipts.
    [XmlArray("UnlockedCardIds", IsNullable = true)] public string[] UnlockedCardIdsXml {
        get => UnlockedCardIds?.ToArray(); set => UnlockedCardIds = value?.ToList();
    }
    public List<LocalMasteryGrant> MasteryGrants;
    public bool ShouldSerializeMasteryGrants() => MasteryGrants?.Count > 0;
    [XmlIgnore] public List<LocalClassRewardGrant> ClassRewardGrants;
    [XmlArray("ClassRewardGrants", IsNullable = true)] public LocalClassRewardGrant[] ClassRewardGrantsXml {
        get => ClassRewardGrants?.ToArray(); set => ClassRewardGrants = value?.ToList();
    }
    [XmlIgnore] public List<LocalPortraitGrant> PortraitGrants;
    [XmlArray("PortraitGrants", IsNullable = true)] public LocalPortraitGrant[] PortraitGrantsXml {
        get => PortraitGrants?.ToArray(); set => PortraitGrants = value?.ToList();
    }
    public int? CosmeticCoin;
    public string CosmeticCoinCardId;
    [XmlIgnore] public bool CosmeticCoinSpecified { get => CosmeticCoin.HasValue; set { } }
    public string Pet;
    public int? PetXpBefore;
    public int? PetXpAfter;
    [XmlIgnore] public bool PetXpBeforeSpecified { get => PetXpBefore.HasValue; set { } }
    [XmlIgnore] public bool PetXpAfterSpecified { get => PetXpAfter.HasValue; set { } }
    // Pet growth rules used at settlement (LocalShop.PetRules); null on receipts settled before stamping.
    public string PetRules;
    // V15: pet event counters (likes and treat feeds, by counter) recorded at settlement; only under event growth rules (rule 2 or 3).
    public int? PetSmall;
    public int? PetMedium;
    public int? PetLarge;
    // Test-only pacing percent frozen at Begin (PetPacingKnob, test-control lanes only). Pending records only: settlement
    // moves it into the receipt's rule string (mult=) and clears it.
    public int? PetPacingPercent;
    [XmlIgnore] public bool PetPacingPercentSpecified { get => PetPacingPercent.HasValue; set { } }
    [XmlIgnore] public bool PetSmallSpecified { get => PetSmall.HasValue; set { } }
    [XmlIgnore] public bool PetMediumSpecified { get => PetMedium.HasValue; set { } }
    [XmlIgnore] public bool PetLargeSpecified { get => PetLarge.HasValue; set { } }
    public int ProgressionRuleVersion;
    [XmlIgnore] public List<LocalTierReward> TierRewards;
    [XmlArray("TierRewards", IsNullable = true)] public LocalTierReward[] TierRewardsXml {
        get => TierRewards?.ToArray(); set => TierRewards = value?.ToList();
    }
    public LocalRewardFacts RewardFacts;
    // True only on a Pending committed by BeginLaunch until the engine game actually starts (MarkStarted). Optional
    // element, written only while true: old saves and every settled record read as started (no version bump).
    public bool Unstarted;
    public bool ShouldSerializeUnstarted() => Unstarted;
    // A debug scenario match (DebugScenario.cs): Pending records only. It never settles and never recovers as Abandoned: no
    // rewards, ladder, XP, quest/achievement, pet, rival or stats change. Written only while true (no version bump).
    public bool DebugScenario;
    public bool ShouldSerializeDebugScenario() => DebugScenario;
    // NPC world encounter (additive, optional; absent in older saves). Opponent stays the deck id.
    public string NpcId;
    public string NpcDeckId;
    public double? NpcRating;   // the encounter's rating; Settle falls back to it when the caller passes none
    public bool? NpcConceded;   // the NPC conceded (player Won); null = not recorded
    // XmlSerializer writes xsi:nil for a null Nullable<T>; *Specified omits the element so older saves stay byte-identical.
    [XmlIgnore] public bool NpcRatingSpecified { get => NpcRating.HasValue; set { } }
    [XmlIgnore] public bool NpcConcededSpecified { get => NpcConceded.HasValue; set { } }
    // Rivals-lite: the one-time gold paid by this win for settling a rivalry with NpcId (LocalNpcRecord.RivalSettleGold),
    // else 0. Additive optional element, written only when nonzero: older saves stay byte-identical (no version bump).
    // Accepted: a downgrade to a build without this field drops the element, so a later upgrade can pay a rivalry twice.
    public int RivalGold;
    public bool ShouldSerializeRivalGold() => RivalGold != 0;
    public LocalAchievementPendingContext AchievementContext;
    public string AchievementEvidenceFingerprint;
    public List<LocalAchievementGrantReceipt> AchievementGrants = new List<LocalAchievementGrantReceipt>();
    // #37 Standard Casual: "Casual", or absent for Ranked. Additive and optional like Unstarted (no version bump; null
    // fields are not written, so older saves stay byte-identical). A settled Casual record has no ladder receipt and no
    // tier rewards; Casual holds its hidden-MMR step (CasualRules).
    public string Mode;
    public LocalCasualReceipt Casual;
}

public sealed class LocalCasualReceipt
{
    public double RatingBefore;
    public double RatingAfter;
    public double OpponentRating;
}

// Rivals-lite head-to-head with one NPC, derived from the settled ledger (never stored), from the player's side:
// Won = W; Lost (incl. the player's concession) and Abandoned = L (as for ladder stars); Tied = T.
public sealed class LocalNpcRecord
{
    public const int RivalSettleGold = 50;
    public static readonly LocalNpcRecord None = new LocalNpcRecord(0, 0, 0, null);
    public int Wins { get; }
    public int Losses { get; }
    public int Ties { get; }
    public int Games => Wins + Losses + Ties;
    // The player's result in the latest settled meeting ("Won", "Lost" or "Tied"; Abandoned reads as "Lost"); null = none.
    public string LastResult { get; }
    public bool IsRival => IsRivalRecord(Wins, Losses);
    public LocalNpcRecord(int wins, int losses, int ties, string lastResult)
    { Wins = wins; Losses = losses; Ties = ties; LastResult = lastResult; }
    // The NPC is the player's rival while it leads the head-to-head (0-1 already counts; 1-1 does not).
    public static bool IsRivalRecord(int wins, int losses) => losses > wins;
    // Folds settled matches in ledger order; other NPCs, unsettled records and legacy (null NpcId) matches are skipped.
    // Same W/L/T counting as HeadToHead.For (plugin/Npc/World/Matchmaker.cs); kept separate because LocalProfile.cs is
    // linked by test projects without plugin/Npc/World. Change both together.
    public static LocalNpcRecord Of(IEnumerable<LocalMatchRecord> settled, string npcId)
    {
        if (string.IsNullOrEmpty(npcId) || settled == null) return None;
        int wins = 0, losses = 0, ties = 0; string last = null;
        foreach (var match in settled)
        {
            if (match == null || match.NpcId != npcId) continue;
            switch (match.Result)
            {
                case "Won": wins++; last = "Won"; break;
                case "Lost": case "Abandoned": losses++; last = "Lost"; break;
                case "Tied": ties++; last = "Tied"; break;
            }
        }
        return new LocalNpcRecord(wins, losses, ties, last);
    }
}

public sealed class LocalCardCount
{
    public int Premium;
    public string Id;
    public int Count;
    public int Acquired;
}

public sealed class LocalCustomDeck : LocalDeckCardContainer
{
    public string SourceRecipeId;
    public long Id;
    public long SortOrder;
    public string Name;
    public string Hero;
    public List<LocalSideboard> Sideboards = new List<LocalSideboard>();
    // Per-deck cosmetics (native deck tray). Null/false = the profile favourite; absent in older saves.
    public int? Coin;
    public bool RandomCoinUseFavorite = true;
    public int? CardBack;
    public bool RandomCardBack;
    public string HeroSkin;
    public bool RandomHeroUseFavorite;
    public string PetFamily;
    public string PetForm;
    public bool RandomPetUseFavorite = true;
    [XmlIgnore] public bool RandomPetUseFavoriteSpecified { get => !RandomPetUseFavorite; set { } }
    [XmlIgnore] public bool CoinSpecified { get => Coin.HasValue; set { } }
    [XmlIgnore] public bool RandomCoinUseFavoriteSpecified { get => !RandomCoinUseFavorite; set { } }
    [XmlIgnore] public bool CardBackSpecified { get => CardBack.HasValue; set { } }
    [XmlIgnore] public bool RandomCardBackSpecified { get => RandomCardBack; set { } }
    [XmlIgnore] public bool RandomHeroUseFavoriteSpecified { get => RandomHeroUseFavorite; set { } }
    [XmlIgnore] public LocalDeckCosmetics Cosmetics => new LocalDeckCosmetics {
        Coin = Coin, RandomCoinUseFavorite = RandomCoinUseFavorite,
        CardBack = CardBack, RandomCardBack = RandomCardBack, HeroSkin = HeroSkin, RandomHeroUseFavorite = RandomHeroUseFavorite,
        PetFamily = PetFamily, PetForm = PetForm, RandomPetUseFavorite = RandomPetUseFavorite };
}

public sealed class LocalDeckCosmetics
{
    public string PetFamily;
    public string PetForm;
    public bool RandomPetUseFavorite = true;
    public int? Coin;
    public bool RandomCoinUseFavorite = true;
    public int? CardBack;
    public bool RandomCardBack;
    public string HeroSkin;
    public bool RandomHeroUseFavorite;
    public bool SameAs(LocalDeckCosmetics other) => other != null && Coin == other.Coin && RandomCoinUseFavorite == other.RandomCoinUseFavorite && CardBack == other.CardBack
        && RandomCardBack == other.RandomCardBack && HeroSkin == other.HeroSkin && RandomHeroUseFavorite == other.RandomHeroUseFavorite
        && PetFamily == other.PetFamily && PetForm == other.PetForm && RandomPetUseFavorite == other.RandomPetUseFavorite;
}

public sealed class LocalClassXp
{
    public string Hero;
    public int Xp;
}

public sealed class LocalProfileData
{
    public string GrindTier;
    public bool ShouldSerializeGrindTier() => GrindTier != null;
    public int Version;
    public List<LocalMasteryGrant> MasteryGrants = new List<LocalMasteryGrant>();
    public bool ShouldSerializeMasteryGrants() => MasteryGrants.Count > 0;
    public List<LocalClassRewardGrant> ClassRewardGrants = new List<LocalClassRewardGrant>();
    public List<LocalPortraitGrant> PortraitGrants = new List<LocalPortraitGrant>();
    public int CoreScheduleVersion;
    public List<LocalCardCount> CoreGrants = new List<LocalCardCount>();
    public List<LocalClassXp> ClassXp = new List<LocalClassXp>();
    public LocalMatchRecord Pending;
    public List<LocalMatchRecord> Matches = new List<LocalMatchRecord>();
    public int GoldBalance;
    [XmlIgnore] public List<LocalPackCount> UnopenedPacks = new List<LocalPackCount>();
    [XmlIgnore] public int LegacyUnopenedPacks;
    // Read the original scalar without changing the bytes saved by EnsureBackup.
    [XmlAnyElement("UnopenedPacks")]
    public XmlElement UnopenedPacksXml {
        get {
            var doc = new XmlDocument(); var root = doc.CreateElement("UnopenedPacks");
            if (Version < 10) root.InnerText = LegacyUnopenedPacks.ToString(CultureInfo.InvariantCulture);
            else foreach (var pack in UnopenedPacks) {
                var row = doc.CreateElement("Pack"); root.AppendChild(row);
                var type = doc.CreateElement("PackType"); type.InnerText = pack.PackType; row.AppendChild(type);
                var count = doc.CreateElement("Count"); count.InnerText = pack.Count.ToString(CultureInfo.InvariantCulture); row.AppendChild(count);
            }
            return root;
        }
        set {
            if (!value.ChildNodes.OfType<XmlElement>().Any()) LegacyUnopenedPacks = string.IsNullOrWhiteSpace(value.InnerText) ? 0 : int.Parse(value.InnerText, CultureInfo.InvariantCulture);
            else {
                UnopenedPacks = new List<LocalPackCount>();
                foreach (XmlElement row in value.ChildNodes.OfType<XmlElement>()) {
                    if (row.Name != "Pack" || row.ChildNodes.OfType<XmlElement>().Count() != 2 || row["PackType"] == null || row["Count"] == null)
                        throw new InvalidOperationException("Invalid pack inventory.");
                    UnopenedPacks.Add(new LocalPackCount { PackType = row["PackType"].InnerText, Count = int.Parse(row["Count"].InnerText, CultureInfo.InvariantCulture) });
                }
            }
        }
    }
    public List<LocalPackProgress> PackCounters = new List<LocalPackProgress>();
    public long DustBalance;
    public long CraftSequence;
    public LocalCraftReceipt LastCraft;
    public LocalLadderState LadderState;
    public int LadderRuleVersion;
    public LocalLadderCalibration LadderCalibration;
    // #37 hidden Casual matchmaking rating; absent until the first Casual match settles (CasualRules).
    public double? CasualRating;
    [XmlIgnore] public bool CasualRatingSpecified { get => CasualRating.HasValue; set { } }
    // Last play mode picked in the native format picker: "Casual", or absent for Standard ranked.
    public string PlayMode;
    public LocalAchievementState Achievements = new LocalAchievementState();
    public string PendingNonMatchAchievementNoticeId;
    public List<string> PendingAchievementToastMatchIds = new List<string>();
    [XmlIgnore] public bool PendingAchievementToastMatchIdsSpecified;
    public LocalCardTokens CardTokens = new LocalCardTokens();
    public List<LocalDeckKit> DeckKits = new List<LocalDeckKit>();
    public List<LocalTrophy> Trophies = new List<LocalTrophy>();
    public LocalLegendDeck LegendDeck;
    public LocalMilestoneCatchUp MilestoneCatchUp;
    public long PurchaseSequence;
    public List<LocalPurchaseReceipt> Purchases = new List<LocalPurchaseReceipt>();
    public List<string> OwnedRecipes = new List<string>();
    // Arrays replace the initialized defaults on XML read; XmlSerializer appends to List fields instead.
    [XmlIgnore] public List<int> OwnedCoins = new List<int> { 1 };
    [XmlIgnore] public List<int> FavoriteCoins = new List<int> { 1 };
    [XmlArray("OwnedCoins")] public int[] OwnedCoinsXml { get => OwnedCoins?.ToArray(); set { OwnedCoins = value?.ToList(); } }
    [XmlArray("FavoriteCoins")] public int[] FavoriteCoinsXml { get => FavoriteCoins?.ToArray(); set { FavoriteCoins = value?.ToList(); } }
    public List<int> OwnedCardBacks = new List<int>();
    public List<string> OwnedHeroSkins = new List<string>();
    public List<int> FavoriteCardBacks = new List<int>();
    public int? SelectedCardBack;
    [XmlIgnore] public bool SelectedCardBackSpecified { get => false; set { } }
    public List<LocalHeroFavorite> FavoriteHeroSkins = new List<LocalHeroFavorite>();
    // V12 (owner 2026-10-25): bought main-menu themes and boards; Classic and boards 1-7 are implicit.
    public List<string> OwnedHubThemes = new List<string>();
    public string SelectedHubTheme;
    public List<int> OwnedBoards = new List<int>();
    public int SelectedBoard;
    // V14: paid pet families; selected card identifies an unlocked form, null means None.
    public List<string> OwnedPets = new List<string>();
    public string SelectedPet;
    public List<LocalPetProgress> PetProgress = new List<LocalPetProgress>();
    public bool ShouldSerializePetProgress() => Version >= 14 || PetProgress?.Count > 0;
    public bool ShouldSerializeOwnedPets() => Version >= 14 || OwnedPets?.Count > 0;
    public bool ShouldSerializeSelectedPet() => SelectedPet != null;
    public int PackSeed;
    public int PackSequence;
    // Keep existing pack/crafting code on its Normal-only view. XML has one (id, premium) collection.
    [XmlIgnore] public List<LocalCardCount> Collection = new List<LocalCardCount>();
    [XmlIgnore] public List<LocalCardCount> PremiumCollection = new List<LocalCardCount>();
    [XmlArray("Collection"), XmlArrayItem("LocalCardCount")]
    public LocalCardCount[] CollectionXml {
        get => Collection?.Concat(PremiumCollection).ToArray();
        set {
            Collection = value?.Where(card => card == null || card.Premium == 0).ToList();
            PremiumCollection = value?.Where(card => card != null && card.Premium != 0).ToList() ?? new List<LocalCardCount>();
        }
    }
    public LocalCustomDeck CustomDeck;
    public List<LocalCustomDeck> CustomDecks = new List<LocalCustomDeck>();
    public long NextCustomDeckId;
    public LocalPackResult LastPack;
    public bool PackRevealPending;
    // Native mass pack opening (owner 2026-10-29): while a multi-pack reveal is pending, every pack of that one batch commit
    // in draw order; LastPack is the last of them. Optional, no version bump: absent (every single-pack reveal) means none.
    public List<LocalPackResult> PackBatch = new List<LocalPackResult>();
    public bool ShouldSerializePackBatch() => PackBatch != null && PackBatch.Count != 0;
    public string PendingRewardPresentationId;
    // Collection "NEW" state: per (card, version), how many owned copies the player has seen. Additive and optional,
    // with no version bump: absent rows (every save before this field, and every save an older build rewrites) read as
    // fully seen, so a migration never floods the collection with NEW markers. Commit keeps it normalized.
    public List<LocalCardSeen> UnseenCards = new List<LocalCardSeen>();
    public bool ShouldSerializeUnseenCards() => UnseenCards != null && UnseenCards.Count != 0;
    // Transient, per change: copies seen at acquisition (crafting), or int.MaxValue when the player viewed the card.
    [XmlIgnore] internal Dictionary<string, int> SeenGrants;
    // #57 welcome gift receipt: optional, no version bump. Absent on every save created before #57 (never eligible).
    public LocalWelcome Welcome;
    public bool ShouldSerializeWelcome() => Welcome != null;
    // Starter loaners this save hid (owner 2026-10-28: deleting a Core starter in the tray hides it for this save only;
    // cards and class progress are untouched). Optional, no version bump: absent means every starter shows.
    public List<string> HiddenLoaners = new List<string>();
    public bool ShouldSerializeHiddenLoaners() => HiddenLoaners != null && HiddenLoaners.Count != 0;
    public bool SeenMassPackTooltip;
    public bool ShouldSerializeSeenMassPackTooltip() => SeenMassPackTooltip;
}

// #57 (owner 2026-10-28): what a new save's first commit granted. Seen only hides the popup; it never grants anything.
public sealed class LocalWelcome
{
    public int Version;
    public string PackType;
    public int Packs;
    public int Gold;
    public int Dust;
    public string KitId;
    public int KitGoldCap;
    public bool Seen;
    // V1 receipts omit cards. V2 records actual additions, including zero when already at the cap.
    public List<LocalOwnedCardGrant> Cards = new List<LocalOwnedCardGrant>();
    public bool ShouldSerializeCards() => Version >= 2 || Cards?.Count > 0;
}

public sealed class LocalOwnedCardGrant
{
    public string CardId;
    public int Premium;
    public int Count;
    public int OwnedBefore;
}

internal static class LocalWelcomeGift
{
    // Owner-approved V1 amounts. A different gift needs a new Version: saved receipts must keep validating.
    internal static int Version => tables.Keys.Max();
    internal const string PackType = FairPackRules.Standard;
    internal const int Packs = 10, Gold = 500, Dust = 400;
    internal const string KitId = "kit:welcome", KitMilestoneId = "welcome";
    internal const int KitGoldCap = 2500;

    // V2 free event-track amounts, plus the Normal-only login grant. Keep historical tables when adding a version.
    private static readonly (string Id, int Normal, int Golden)[] cardsV2 = {
        ("CATA_EVENT_000", 2, 2), ("CATA_EVENT_001", 2, 2), ("CATA_EVENT_002", 2, 2),
        ("CATA_EVENT_110", 1, 1), ("CATA_EVENT_400", 2, 2), ("CATA_EVENT_401", 2, 2), ("CATA_EVENT_402", 2, 2),
        ("EDR_950", 2, 2), ("JAIL_EVENT_100", 1, 1), ("JAIL_EVENT_101", 2, 2), ("JAIL_EVENT_102", 2, 2),
        ("TIME_EVENT_300", 2, 2), ("TIME_EVENT_301", 2, 2), ("TIME_EVENT_997", 2, 2),
        ("TIME_EVENT_998", 1, 1), ("TIME_EVENT_999", 2, 2), ("TLC_EVENT_400", 2, 2), ("TLC_EVENT_402", 1, 1),
        ("BE_EVENT_100", 2, 2), ("BE_EVENT_101", 2, 2), ("BE_EVENT_102", 2, 2), ("BE_036", 1, 0)
    };
    private static readonly Dictionary<int, (string Id, int Normal, int Golden)[]> tables =
        new Dictionary<int, (string Id, int Normal, int Golden)[]> {
            [1] = Array.Empty<(string, int, int)>(), [2] = cardsV2
        };
    internal static IEnumerable<string> HistoricalCardIds => tables.Values.SelectMany(table => table).Select(card => card.Id);
    internal static IReadOnlyList<LocalOwnedCardGrant> Cards => Table(Version);
    internal static IReadOnlyList<LocalOwnedCardGrant> Table(int version) => tables.TryGetValue(version, out var table) ? table.SelectMany(card => new[] {
        new LocalOwnedCardGrant { CardId = card.Id, Premium = 0, Count = card.Normal },
        new LocalOwnedCardGrant { CardId = card.Id, Premium = 1, Count = card.Golden }
    }).Where(card => card.Count > 0).ToArray() : null;

    private static bool HasAmounts(LocalWelcome welcome) => welcome != null
        && welcome.PackType == PackType && welcome.Packs == Packs && welcome.Gold == Gold && welcome.Dust == Dust
        && welcome.KitId == KitId && welcome.KitGoldCap == KitGoldCap;

    internal static bool IsKnown(LocalWelcome welcome) => HasAmounts(welcome) && tables.ContainsKey(welcome.Version);

    // The welcome scroll has only four native item slots and a short description, so the actual card additions are
    // summarised in one line; the collection shows the cards themselves.
    internal static string CardSummary(LocalWelcome welcome)
    {
        var added = welcome.Cards.Where(card => card.Count > 0).ToArray();
        if (added.Length == 0) return "";
        int cards = added.Select(card => card.CardId).Distinct(StringComparer.Ordinal).Count(), copies = added.Sum(card => card.Count);
        return cards.ToString(CultureInfo.InvariantCulture) + (cards == 1 ? " card (" : " cards (")
            + copies.ToString(CultureInfo.InvariantCulture) + (copies == 1 ? " copy" : " copies") + ") added to your collection.";
    }
}

public sealed class LocalCardSeen
{
    public string Id;
    public int Premium;
    public int Seen;
}

public sealed partial class LocalProfile : IDisposable
{
    private const int CurrentVersion = 15;
    // Personal decks only; loaners (NativeUIProjection.PlayerLoanerCapacity) sit above this cap. Raising it never
    // invalidates a save: load accepts any library size up to MaximumRecords and only shows the first drafts.
    public const int MaximumCustomDecks = 100;
    internal static string PersonalDeckLimitMessage => "The personal deck limit is " + MaximumCustomDecks + ".";

    // Hideable: a Core starter loaner (LocalDecks.PlayerCoreStarterDecks); builds without the source engine list none, so
    // they accept the starter id shape only and never lose a save that hid starters.
    internal const string StarterLoanerPrefix = "player-core-";
    internal static bool IsHideableLoaner(string key) => key != null && (LocalDecks.PlayerCoreStarterDecks.Count != 0
        ? LocalDecks.PlayerCoreStarterDecks.Any(deck => deck.Id == key)
        : IsStarterLoanerShape(key));
    // Stored hides keep the shape check only: a later build that renames or drops a starter must not make the save
    // unloadable; an id with no current deck simply hides nothing.
    private static bool IsStarterLoanerShape(string key) =>
        key != null && key.StartsWith(StarterLoanerPrefix, StringComparison.Ordinal) && key.Length <= 64 && ValidName(key, 64);
    public IReadOnlyList<string> HiddenLoaners => data.HiddenLoaners.ToArray();
    public bool IsLoanerHidden(string key) => key != null && data.HiddenLoaners.Contains(key);
    // Match-derived XP always reads the receipt, including replay and history reconciliation.
    internal static int MatchClassXp(LocalMatchRecord match) => GrindTiers.Find(match.GrindTier).ClassXp(LocalCoreProgression.Xp(match.Result, match.Conceded));

    public GrindTier GrindTier => GrindTiers.Find(data.GrindTier);
    // The file this instance holds the lock on: what "the open save" means to the catalog (ProfileCatalog.IsOpenSave).
    internal string FilePath => path;

    public bool SetGrindTier(string id)
    {
        var tier = GrindTiers.Find(id);
        if (GrindTier.Id == tier.Id) return false;
        var next = Snapshot(); next.GrindTier = tier.Id; Commit(next);
        return true;
    }

    // Native tray delete of a starter: hide it for this save. Idempotent; refuses anything but a Core starter.
    public bool HideLoaner(string key)
    {
        if (!IsHideableLoaner(key)) throw new InvalidOperationException("Only starter decks can be hidden.");
        if (data.HiddenLoaners.Contains(key)) return false;
        var next = Snapshot(); next.HiddenLoaners.Add(key); Commit(next);
        return true;
    }
    // Game Menu "Restore Starter Decks": every hidden starter shows again (from the next native deck-list rebuild).
    public bool RestoreLoaners()
    {
        if (data.HiddenLoaners.Count == 0) return false;
        var next = Snapshot(); next.HiddenLoaners.Clear(); Commit(next);
        return true;
    }
    public bool SeenMassPackTooltip => data.SeenMassPackTooltip;
    public bool MarkMassPackTooltipSeen()
    {
        if (data.SeenMassPackTooltip) return false;
        var next = Snapshot(); next.SeenMassPackTooltip = true; Commit(next);
        return true;
    }
    public const long LegacyCustomDeckId = 910000001L;
    public const long FirstAllocatedCustomDeckId = 910000010L;
    private const long MaximumProfileBytes = 32L * 1024 * 1024;
    private const int MaximumRecords = 100000;
    private const int MaximumTurns = 1000000;
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(LocalProfileData));
    private readonly string path;
    // Used only to keep roguelike-run.xml beside this save; it is not a profile ledger field.
    internal string DungeonProfilePath => path;
    private readonly FileStream lease;
    private readonly bool backups;
    private readonly ProfileOptions profileOptions;
    private LocalProfileData data;
    // True only when this instance started from a missing profile.xml (NewProfile in memory, nothing written yet).
    private readonly bool bornMissing;
    internal static Action<string> CommitFaultHook = null;

    public LocalMatchRecord Pending => Copy(data.Pending);
    public IReadOnlyList<LocalMatchRecord> History => data.Matches.Select(Copy).ToArray();
    internal LocalMatchRecord LastSettledMatch => Copy(data.Matches.LastOrDefault());
    // Head-to-head with one NPC from SETTLED matches only; the pending match never counts.
    public LocalNpcRecord NpcRecord(string npcId) => LocalNpcRecord.Of(data.Matches, npcId);
    public int Gold => profileOptions.InfiniteGold ? ProfileCatalog.UnlimitedGold : data.GoldBalance;
    internal bool IsSandbox => profileOptions.IsSandbox();
    internal int StoredGoldBalance => data.GoldBalance;
    public int UnopenedPacks => data.UnopenedPacks.Sum(pack => pack.Count);
    public IReadOnlyList<LocalPackCount> PackInventory => data.UnopenedPacks.Select(Copy).ToArray();
    public long DustBalance => profileOptions.InfiniteDust ? ProfileCatalog.UnlimitedDust : data.DustBalance;
    public IReadOnlyDictionary<string, int> Acquired => data.Collection.ToDictionary(card => card.Id, card => card.Acquired);
    public IReadOnlyList<LocalPackProgress> PackCounters => data.PackCounters.Select(Copy).ToArray();
    public int PackSequence => data.PackSequence;
    public IReadOnlyDictionary<string, int> ClassXp => XpMap(data);
    public IReadOnlyDictionary<string, int> Collection => OwnedCollection(data);
    // Raw stored Normal counts, before OwnedCollection adds Core grants. Pack draws write here; the projection is what
    // native shows. No card is pinned (virtual ownership removed, owner 2026-10-30).
    internal IReadOnlyDictionary<string, int> StoredCollection => CollectionMap(data.Collection);
    public int ClassLevel(string hero) => LocalCoreProgression.Level(ClassXp.TryGetValue(hero, out int xp) ? xp : 0);
    public bool PackPoolComplete => LocalEconomy.PackPoolComplete(CollectionMap(data.Collection));
    public IReadOnlyList<LocalCustomDeck> CustomDecks => VisibleDrafts(data).Select(Copy).ToArray();
    public long NextCustomDeckId => data.NextCustomDeckId;
    public LocalCustomDeck CustomDeck {
        get {
            var decks = VisibleDrafts(data).Take(2).ToArray();
            return decks.Length == 1 ? Copy(decks[0]) : null;
        }
    }
    public LocalPackResult LastPack => Copy(data.LastPack);
    // Non-copying shop status queries: native availability runs them per product on the main thread.
    public bool HasDeckReceipt(string recipeId) => data.Purchases.Any(r => r.Kind == "Deck" && r.Granted.RecipeId == recipeId);
    public bool OwnsRecipe(string recipeId) => data.OwnedRecipes.Contains(recipeId);
    public bool OwnsCardBack(int cardBack) => data.OwnedCardBacks.Contains(cardBack)
        || profileOptions.AllCosmetics && LocalShop.SupportedCheatCardBacks.Contains(cardBack);
    public bool OwnsCoin(int coin) => data.OwnedCoins.Contains(coin)
        || profileOptions.AllCosmetics && LocalShop.SupportedCheatCoins.Contains(coin);
    public bool OwnsHeroSkin(string heroSkin) => data.OwnedHeroSkins.Contains(heroSkin)
        || profileOptions.AllCosmetics && LocalShop.SupportedCheatHeroSkins.Contains(heroSkin);
    public bool HasPendingMatch => data.Pending != null;
    public bool HasPendingRewardPresentation => data.PendingRewardPresentationId != null
        && data.Matches.Any(match => match.Id == data.PendingRewardPresentationId);
    public int CustomDeckCount => data.CustomDecks.Count;
    public long HighestNumericPurchaseRequestId => data.Purchases.Select(r =>
        long.TryParse(r.RequestId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0L).DefaultIfEmpty(0).Max();
    public bool PackRevealPending => data.PackRevealPending;
    public LocalMatchRecord PendingRewardPresentation => data.PendingRewardPresentationId == null ? null
        : Copy(data.Matches.SingleOrDefault(match => match.Id == data.PendingRewardPresentationId));
    public int Stars => Ladder.StarIndex;
    public string Rank => RankName(Stars);
    public LocalAchievementState Achievements => Copy(data.Achievements);
    internal LocalProfileData FeatsSnapshot() => Snapshot();
    public string PendingNonMatchAchievementNoticeId => data.PendingNonMatchAchievementNoticeId;

#if STANDARD_PROOF
    internal static LocalProfile OpenSnapshot(string path)
    {
        var profile = new LocalProfile(path, true);
        if (profile.Pending == null) return profile;
        profile.Dispose();
        throw new InvalidOperationException("Snapshot has a pending match; refusing automatic recovery.");
    }
#endif

    // Versions the constructor loads (older ones are upgraded in place). The startup preflight shares this so the two cannot drift.
    internal static bool IsSupportedVersion(int version) => version == 13 || version == 14 || version == CurrentVersion;

    public LocalProfile(string path) : this(path, false) { }

    internal LocalProfile(string path, Action<string> failure) : this(path, false, true, failure) { }

    internal LocalProfile(string path, ProfileOptions options) : this(path, false, true, null, options) { }

    private LocalProfile(string path, bool snapshot) : this(path, snapshot, true, null) { }

    private LocalProfile(string path, bool snapshot, bool backups) : this(path, snapshot, backups, null) { }

    private LocalProfile(string path, bool snapshot, bool backups, Action<string> failure, ProfileOptions options = null)
    {
        SaveHold.Require("opening a save");
        CommitFailure = failure;
        this.path = Path.GetFullPath(path);
        profileOptions = options ?? ProfileCatalog.OptionsBeside(this.path);
        this.backups = backups && !snapshot;
        Directory.CreateDirectory(Path.GetDirectoryName(this.path));
        lease = new FileStream(this.path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!File.Exists(this.path))
            {
                if (snapshot) throw new InvalidOperationException("Snapshot requires an existing V15 profile.");
                data = NewProfile();
                bornMissing = true;
            }
            else
            {
                LocalProfileData loaded = ReadFile(this.path);
                if (loaded == null || !IsSupportedVersion(loaded.Version))
                    throw new InvalidOperationException("Unsupported local profile version; create a fresh V15 profile.");
                if (snapshot && loaded.Version != CurrentVersion)
                    throw new InvalidOperationException("Snapshot requires an existing V15 profile.");
                if (loaded.FavoriteCardBacks != null && loaded.FavoriteCardBacks.Count == 0)
                    loaded.FavoriteCardBacks.Add(loaded.SelectedCardBack ?? 0);
                loaded.SelectedCardBack = null;
                int fromVersion = loaded.Version;
                bool upgrade = fromVersion < CurrentVersion;
                if (upgrade && (loaded.Matches == null || loaded.Matches.Any(match => match == null)))
                    throw new InvalidOperationException("V" + fromVersion + " profile has an invalid match list.");
                if (upgrade && (loaded.Matches.Concat(loaded.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { loaded.Pending })
                        .Any(match => match.PetSmall.HasValue || match.PetMedium.HasValue || match.PetLarge.HasValue || match.PetPacingPercent.HasValue
                            || match.PetRules != null && (match.PetRules.StartsWith("rule=2;", StringComparison.Ordinal) || match.PetRules.StartsWith("rule=3;", StringComparison.Ordinal)))))
                    throw new InvalidOperationException("V" + fromVersion + " cannot contain pet event counters.");
                if (upgrade && fromVersion == 13) {
                    if (loaded.OwnedPets == null || loaded.OwnedPets.Count != 0 || loaded.SelectedPet != null
                        || loaded.PetProgress == null || loaded.PetProgress.Count != 0
                        || loaded.Purchases == null || loaded.Matches == null || loaded.Matches.Any(match => match == null)
                        || loaded.Purchases.Any(receipt => receipt?.Kind == "Pet" || receipt?.Granted?.Pet != null)
                        || loaded.Matches.Concat(loaded.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { loaded.Pending })
                            .Any(match => match.Pet != null || match.PetXpBefore.HasValue || match.PetXpAfter.HasValue))
                        throw new InvalidOperationException("V13 cannot contain pet state.");
                }
                if (upgrade) loaded.Version = CurrentVersion;
                ValidateV14(loaded, profileOptions.AllCosmetics, profileOptions.LegendRating, profileOptions.AllDecks);
                if (upgrade && !snapshot) {
                    EnsureBackup(File.ReadAllBytes(this.path), fromVersion);
                    Commit(loaded);
                }
                if (this.backups) LocalProfileBackups.Automatic(this.path, "startup", loaded.Version);
                var successor = GrindTiers.Successor(loaded.GrindTier);
                bool paceChanged = successor.Id != loaded.GrindTier;
                if (paceChanged && !snapshot) loaded.GrindTier = successor.Id;
                var repaired = RepairDeckPremiums(loaded, profileOptions.AllCosmetics);
                bool granted = AdoptAchievementHeroSkins(loaded) | GrantCoreUnlocks(loaded) | GrantClassRewards(loaded, null) | LocalCardMastery.Grant(loaded, null);
                if ((granted || paceChanged || repaired.Count > 0) && !snapshot) { Commit(loaded); LogDeckRepairs(repaired); }
                else data = loaded;
            }
            if (!snapshot) {
                if (data.Pending == null && data.LadderCalibration == null && LegacyPrefix(data) > 0) {
                    var next = Snapshot();
                    if (TryCalibrateLadder(next)) Commit(next);
                }
                ApplyMilestoneCatchUp();
            }
        }
        catch { lease.Dispose(); throw; }
    }

    public LocalMatchRecord Begin(string deck, string opponent, int seed) => Begin(deck, opponent, seed, null, null, null);

    // NPC world encounter fields (LocalProfileNpcWorld.cs maps an NpcEncounter here); null = legacy opponent.
    public LocalMatchRecord Begin(string deck, string opponent, int seed, string npcId, string npcDeckId, double? npcRating)
        => Begin(deck, opponent, seed, npcId, npcDeckId, npcRating, false, null, null);

    // mode: null = Ranked, CasualRules.Mode = Standard Casual (no ladder accounting).
    public LocalMatchRecord Begin(string deck, string opponent, int seed, string npcId, string npcDeckId, double? npcRating, string mode)
        => Begin(deck, opponent, seed, npcId, npcDeckId, npcRating, false, null, mode);

    // Test seam (docs/design/pet-xp.md): the pacing percent for the NEXT Begin only. Never persisted; the caller gates it on test control.
    private int? armedPetPacing;
    internal void ArmPetPacing(int percent) {
        if (percent < PetPacingKnob.Min || percent > PetPacingKnob.Max) throw new InvalidOperationException("Pet pacing percent must be " + PetPacingKnob.Min + ".." + PetPacingKnob.Max + ".");
        armedPetPacing = percent;
    }

    // One-shot like the pacing above: the NEXT Begin is a debug scenario match (never settles). The caller disarms in a finally.
    private int? armedFirstPlayer;
    internal void ArmFirstPlayer(int firstPlayer) {
        if (firstPlayer != 1 && firstPlayer != 2) throw new ArgumentOutOfRangeException(nameof(firstPlayer));
        armedFirstPlayer = firstPlayer;
    }
    private bool armedDebugScenario;
    internal void ArmDebugScenario() { armedDebugScenario = true; }
    internal void DisarmDebugScenario() { armedDebugScenario = false; armedFirstPlayer = null; }

    private LocalMatchRecord Begin(string deck, string opponent, int seed, string npcId, string npcDeckId, double? npcRating,
        bool unstarted, LocalAchievementPendingContext achievementContext, string mode)
    {
        // One-shot: the armed test pacing applies to this Begin only, whether or not it succeeds.
        int? pacing = armedPetPacing; armedPetPacing = null;
        bool debugScenario = armedDebugScenario; armedDebugScenario = false;
        int firstPlayer = armedFirstPlayer ?? StableSeed.MatchFirstPlayer(seed); armedFirstPlayer = null;
        if (!ValidNpcFields(npcId, npcDeckId, npcRating)) throw new ArgumentException("Invalid NPC encounter.");
        if (mode != null && !CasualRules.IsCasual(mode)) throw new ArgumentException("Unknown match mode.");
        if (data.Pending != null) throw new InvalidOperationException("A local match is already pending.");
        if (data.PendingRewardPresentationId != null)
            throw new InvalidOperationException("The previous local reward must be presented first.");
        if (!ValidName(deck, 64) || !ValidName(opponent, 64) || seed < 0)
            throw new ArgumentException("Invalid match setup.");
        if (deck == "custom" || deck.StartsWith("custom:", StringComparison.Ordinal)) {
            LocalCustomDeck saved = deck == "custom" ? CustomDeck : data.CustomDecks.SingleOrDefault(d => "custom:" + d.Id == deck);
            if (saved == null || !IsDeckComplete(saved.Id)) throw new InvalidOperationException("The saved deck is incomplete.");
        }
        var chosenPet = DeckPet(deck, seed);
        var next = Snapshot();
        next.Pending = new LocalMatchRecord {
            Id = Guid.NewGuid().ToString(), Deck = deck, DeckHero = ResolveDeckHero(deck, data), Opponent = opponent, Seed = seed, FirstPlayer = firstPlayer,
            StartedUtc = DateTime.UtcNow.ToString("o"), RewardVersion = LocalEconomy.RollRewardVersion, GrindTier = GrindTier.Id, RewardFacts = DefaultFacts(), Unstarted = unstarted, DebugScenario = debugScenario,
            NpcId = npcId, NpcDeckId = npcDeckId, NpcRating = npcRating, Mode = mode,
            Pet = chosenPet, PetXpBefore = chosenPet != null && data.OwnedPets.Contains(LocalShop.Default.PetFamily(chosenPet))
                ? (int?)PetXp(LocalShop.Default.PetFamily(chosenPet)) : null
        };
        if (pacing.HasValue && next.Pending.PetXpBefore.HasValue && !PetXpPolicy.Current.Flat) next.Pending.PetPacingPercent = pacing;
        if (achievementContext != null) {
            next.Pending.AchievementContext = Copy(achievementContext);
            next.Pending.AchievementContext.MatchId = next.Pending.Id;
        }
        Commit(next);
        return Pending;
    }

    // Begin commits Pending before the native launch. A launch that throws never started a match: its Pending is
    // cleared in one commit (no history, ladder, XP or reward change) and the launch error is rethrown.
    public LocalMatchRecord BeginLaunch(string deck, string opponent, int seed, Action<LocalMatchRecord> launch)
        => BeginLaunch(deck, opponent, seed, (string)null, (string)null, (double?)null, launch);

    public LocalMatchRecord BeginLaunch(string deck, string opponent, int seed, string npcId, string npcDeckId, double? npcRating,
        Action<LocalMatchRecord> launch)
    {
        if (launch == null) throw new ArgumentNullException(nameof(launch));
        LocalMatchRecord record = Begin(deck, opponent, seed, npcId, npcDeckId, npcRating, true, null, null);
        try { launch(record); }
        catch (Exception failure)
        {
            try { CancelUnstarted(record.Id); }
            catch (Exception cancel)
            { throw new InvalidOperationException(failure.Message + " The pending match could not be cleared: " + cancel.Message, failure); }
            throw;
        }
        return record;
    }

    internal LocalMatchRecord BeginLaunch(string deck, string opponent, int seed, string npcId, string npcDeckId, double? npcRating,
        LocalAchievementPendingContext achievementContext, Action<LocalMatchRecord> launch)
        => BeginLaunch(deck, opponent, seed, npcId, npcDeckId, npcRating, achievementContext, null, launch);

    internal LocalMatchRecord BeginLaunch(string deck, string opponent, int seed, string npcId, string npcDeckId, double? npcRating,
        LocalAchievementPendingContext achievementContext, string mode, Action<LocalMatchRecord> launch)
    {
        if (launch == null) throw new ArgumentNullException(nameof(launch));
        LocalMatchRecord record = Begin(deck, opponent, seed, npcId, npcDeckId, npcRating, true, achievementContext, mode);
        try { launch(record); }
        catch (Exception failure)
        {
            try { CancelUnstarted(record.Id); }
            catch (Exception cancel)
            { throw new InvalidOperationException(failure.Message + " The pending match could not be cleared: " + cancel.Message, failure); }
            throw;
        }
        return record;
    }

    // The engine game exists and the match is real from here: recovery abandons it instead of cancelling.
    internal void MarkStarted(string id, int? firstPlayer = null)
    {
        if (data.Pending == null || data.Pending.Id != id) throw new InvalidOperationException("No matching pending match.");
        if (firstPlayer.HasValue && firstPlayer != 1 && firstPlayer != 2) throw new ArgumentOutOfRangeException(nameof(firstPlayer));
        if (!data.Pending.Unstarted && (!firstPlayer.HasValue || data.Pending.FirstPlayer == firstPlayer)) return;
        var next = Snapshot();
        next.Pending.Unstarted = false;
        // Start-of-game card rules (Aya) may force the second seat after the ordinary roll.
        if (firstPlayer.HasValue) next.Pending.FirstPlayer = firstPlayer.Value;
        Commit(next);
    }

    // Only for a Pending whose native match never started (Unstarted, no result, no turns); a started match settles or
    // is abandoned instead. Records not committed by BeginLaunch read as started and are refused.
    internal void CancelUnstarted(string id)
    {
        if (data.Pending == null || data.Pending.Id != id) throw new InvalidOperationException("No matching pending match.");
        if (!data.Pending.Unstarted || data.Pending.Result != null || data.Pending.Turns != 0)
            throw new InvalidOperationException("The pending match already started.");
        var next = Snapshot();
        next.Pending = null;
        Commit(next);
    }

    // opponentRating: the NPC's rating; null = the pending record's NpcRating, else the player's own rating.
    // npcConceded: the NPC conceded (NativeMatch.OpponentConceded); null = not recorded.
    public LocalMatchRecord Settle(string id, string result, int turns, bool conceded, LocalRewardFacts facts = null, double? opponentRating = null,
        bool? npcConceded = null, LocalAchievementMatchEvidence achievementEvidence = null, LocalPetXpCounts petXp = null)
    {
        if ((result != "Won" && result != "Lost" && result != "Tied") || turns < 0 || turns > MaximumTurns
            || (conceded && result != "Lost") || (npcConceded == true && (result != "Won" || conceded)))
            throw new InvalidOperationException("Invalid terminal result.");
        // The one central settlement gate for debug scenario matches: the pending record is dropped and nothing else moves
        // (no Matches row, gold, dust, packs, ladder, XP, quests/achievements, pets, rivals, stats, backup, reward presentation).
        if (data.Pending != null && data.Pending.Id == id && data.Pending.DebugScenario)
        {
            var unsettled = Copy(data.Pending);
            unsettled.SettledUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            unsettled.Unstarted = false; unsettled.Result = result; unsettled.Turns = turns; unsettled.Conceded = conceded;
            var dropped = Snapshot();
            dropped.Pending = null;
            Commit(dropped);
            return unsettled;
        }
        var suppliedFacts = Copy(facts ?? DefaultFacts());
        LocalEconomy.ValidateFacts(suppliedFacts);
        var old = data.Matches.SingleOrDefault(match => match.Id == id);
        if (old != null)
        {
            if (old.Result != result || old.Turns != turns || old.Conceded != conceded || !Same(old.RewardFacts, suppliedFacts)
                || (old.Ladder?.OpponentRating != null && (opponentRating ?? old.NpcRating ?? old.Ladder.RatingBefore) != old.Ladder.OpponentRating)
                || (old.Casual != null && (opponentRating ?? old.NpcRating ?? old.Casual.RatingBefore) != old.Casual.OpponentRating)
                || (npcConceded.HasValue && (old.NpcConceded ?? false) != npcConceded.Value)
                || (petXp != null && old.PetSmall.HasValue && (old.PetSmall != petXp.Small || old.PetMedium != petXp.Medium || old.PetLarge != petXp.Large))
                || (old.AchievementContext == null ? achievementEvidence != null
                    : achievementEvidence == null || old.AchievementEvidenceFingerprint != achievementEvidence.Fingerprint))
                throw new InvalidOperationException("Conflicting result for a settled match.");
            return Copy(old);
        }
        if (data.Pending == null || data.Pending.Id != id)
            throw new InvalidOperationException("No matching pending match.");

        var next = Snapshot();
        var match = next.Pending;
        LocalReward reward = MatchReward(match, result, turns, conceded, suppliedFacts);
        match.Unstarted = false;
        match.SettledUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        match.Result = result;
        match.Turns = turns;
        match.Conceded = conceded;
        match.Gold = reward.Gold;
        match.BonusGold = reward.BonusGold;
        match.PackReward = reward.PackReward;
        match.RewardFacts = suppliedFacts;
        match.NpcConceded = npcConceded;
        // Offered counts are only the input of ApplyPetReceipt: they stay on the receipt under event rules (rule 2 or 3) with a pet recipient.
        match.PetSmall = petXp?.Small; match.PetMedium = petXp?.Medium; match.PetLarge = petXp?.Large;
        ApplyMatchReceipt(next, match, opponentRating, reward.Dust);
        SettleAchievements(next, match, achievementEvidence);
        match.RivalGold = RivalGoldFor(next.Matches, match);
        next.GoldBalance = checked(next.GoldBalance + match.Gold + match.RivalGold);
        if (match.PackReward) {
            // V1/V2 keep the newest set; V3 weights the Standard sets by missing copies (LocalEconomy.RewardPackSet).
            string packType = LocalEconomy.IsRolled(match.RewardVersion) ? RewardPackSet(next, match) : FairPackRules.NewestSet;
            match.Packs.Add(new LocalPackCount { PackType = packType, Count = 1 });
            AddPacks(next, packType, 1);
        }
        next.Pending = null;
        next.Matches.Add(match);
        next.PendingRewardPresentationId = match.Id;
        Commit(next);
        Backup("match");
        return Copy(match);
    }

    public bool AcknowledgeRewardPresentation(string expectedMatchId)
    {
        if (string.IsNullOrWhiteSpace(expectedMatchId))
            throw new InvalidOperationException("A reward acknowledgement requires a match ID.");
        bool known = data.Matches.Any(match => match.Id == expectedMatchId && match.RewardVersion >= 2
            && match.Result != "Abandoned");
        if (data.PendingRewardPresentationId == null)
        {
            if (known) return false;
            throw new InvalidOperationException("Unknown reward acknowledgement.");
        }
        if (data.PendingRewardPresentationId != expectedMatchId)
            throw new InvalidOperationException("Reward acknowledgement does not match the pending presentation.");
        var next = Snapshot();
        next.PendingRewardPresentationId = null;
        Commit(next);
        return true;
    }

    // A win that brings W >= L against an NPC that was the player's rival before this match settles the rivalry; the first
    // settlement per NPC pays once. `settled` is the ledger before this match is appended (never contains it).
    private static int RivalGoldFor(IEnumerable<LocalMatchRecord> settled, LocalMatchRecord match)
    {
        if (match.Result != "Won" || string.IsNullOrEmpty(match.NpcId)) return 0;
        var before = LocalNpcRecord.Of(settled, match.NpcId);
        if (!before.IsRival || before.Wins + 1 < before.Losses) return 0;
        return settled.Any(row => row.NpcId == match.NpcId && row.RivalGold > 0) ? 0 : LocalNpcRecord.RivalSettleGold;
    }

    public void RecoverInterrupted()
    {
        if (data.Pending == null) return;
        if (data.Pending.DebugScenario)
        {
            // A scenario match interrupted by a quit or crash is dropped, never booked as an Abandoned ladder loss.
            var dropped = Snapshot();
            dropped.Pending = null;
            Commit(dropped);
            return;
        }
        if (data.Pending.Unstarted && data.Pending.Result == null && data.Pending.Turns == 0)
        {
            // The match never began (stage timeout, failed engine start, crash before the game existed): no penalty.
            var cancelled = Snapshot();
            cancelled.Pending = null;
            Commit(cancelled);
            return;
        }
        var next = Snapshot();
        next.Pending.Unstarted = false;
        next.Pending.SettledUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        next.Pending.Result = "Abandoned";
        next.Pending.RewardFacts = DefaultFacts();
        // An abandoned match is a ladder loss against the stored NPC rating, applied once: this commit also clears Pending.
        // An abandoned Casual match is a Casual loss instead (hidden Casual rating only; never the ladder).
        ApplyMatchReceipt(next, next.Pending, next.Pending.NpcRating, LocalEconomy.MatchDust);
        next.Matches.Add(next.Pending);
        next.Pending = null;
        Commit(next);
        Backup("abandon");
    }

    // V2 rewards (and every abandoned match) pay LocalEconomy.MatchDust = 0; a V3 win may roll dust. The dust lands in the
    // same snapshot as the gold, pack, ladder and XP, so one Commit pays the whole receipt or nothing.
    // A Casual match (#37) pays the same rewards and class XP but moves only the hidden Casual rating: no ladder receipt,
    // no milestones, no win-portrait count (XpWin).
    private void ApplyMatchReceipt(LocalProfileData next, LocalMatchRecord match, double? opponentRating, int dust)
    {
        bool casual = CasualRules.IsCasual(match.Mode);
        if (casual) match.Casual = SettleCasual(next, match.Result, opponentRating ?? match.NpcRating);
        else match.Ladder = SettleLadder(next, match, match.Result, opponentRating ?? match.NpcRating);
        var before = XpMap(next);
        match.ClassXpBefore = match.DeckHero == null ? 0 : before[match.DeckHero];
        match.ClassXpAfter = checked(match.ClassXpBefore.Value + (match.DeckHero == null ? 0 : LocalProfile.MatchClassXp(match)));
        if (match.DeckHero != null) next.ClassXp.Single(entry => entry.Hero == match.DeckHero).Xp = match.ClassXpAfter.Value;
        var oldCards = LocalCoreProgression.Owned(before);
        match.UnlockedCardIds = LocalCoreProgression.Owned(XpMap(next)).Keys.Where(id => !oldCards.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        match.ProgressionRuleVersion = LocalCoreProgression.ScheduleVersion;
        GrantCoreUnlocks(next);
        match.TierRewards = casual ? null : SettleMilestones(next, match);
        GrantClassRewards(next, match);
        LocalCardMastery.Grant(next, match);
        ApplyPetReceipt(next, match);
        match.DustReward = dust;
        next.DustBalance = checked(next.DustBalance + match.DustReward);
    }

    public void BuyPack() => Purchase("pack-standard", 1, LocalEconomy.PackPrice, Guid.NewGuid().ToString());

    private string NextPackType() => new[] { FairPackRules.Legacy }.Concat(FairPackRules.PackTypes)
        .FirstOrDefault(type => data.UnopenedPacks.Any(pack => pack.PackType == type && pack.Count > 0))
        ?? throw new InvalidOperationException("No local packs are available.");

    public LocalPackResult OpenPack() => OpenPack(NextPackType());
    public LocalPackResult OpenPack(string packType)
    {
        if (data.PackRevealPending) throw new InvalidOperationException("A native pack reveal is pending.");
        return CommitPack(packType, false);
    }

    public LocalPackResult BeginNativePack() => BeginNativePack(data.PackRevealPending ? data.LastPack.PackType : NextPackType());
    public LocalPackResult BeginNativePack(string packType)
    {
        if (data.PackRevealPending) {
            if (packType != data.LastPack.PackType) throw new InvalidOperationException("A different pack reveal is pending.");
            return Copy(data.LastPack);
        }
        return CommitPack(packType, true);
    }

    // Packs of the pending native reveal in draw order: the batch of a mass opening, else the one LastPack (empty when none).
    public IReadOnlyList<LocalPackResult> PendingPacks => !data.PackRevealPending ? Array.Empty<LocalPackResult>()
        : data.PackBatch.Count != 0 ? data.PackBatch.Select(Copy).ToArray() : new[] { Copy(data.LastPack) };
    // Mass opening: count packs of one fair type in ONE commit, each drawn exactly as a single native open would be (same
    // seed sequence, counters and acquisition, in order). A pending reveal resumes instead, whatever count.
    public IReadOnlyList<LocalPackResult> BeginNativePacks(string packType, int count)
    {
        if (data.PackRevealPending) {
            if (packType != data.LastPack.PackType) throw new InvalidOperationException("A different pack reveal is pending.");
            return PendingPacks;
        }
        if (count == 1) return new[] { CommitPack(packType, true) };
        if (count < 2 || count > MassPackLimit) throw new InvalidOperationException("Invalid mass pack count.");
        if (packType == FairPackRules.Legacy || !FairPackRules.IsKnown(packType)
            || (data.UnopenedPacks.SingleOrDefault(pack => pack.PackType == packType)?.Count ?? 0) < count)
            throw new InvalidOperationException("Not enough local packs of that type for a mass opening.");
        var next = Snapshot();
        var results = new List<LocalPackResult>();
        for (int i = 0; i < count; i++) results.Add(DrawPack(next, packType));
        next.LastPack = Copy(results[results.Count - 1]);
        next.PackBatch = results.Select(Copy).ToList();
        next.PackRevealPending = true;
        Commit(next);
        Backup("pack");
        return results.Select(Copy).ToArray();
    }
    // Retail picks packs up in chunks of 5, up to 40 per opening (NativeUIProbe publishes the same values to the client).
    public const int MassPackLimit = 40, MassPackChunk = 5;

    public bool CompleteNativePackReveal(int expectedSequence, IEnumerable<string> expectedIds)
    {
        string[] ids = expectedIds?.ToArray() ?? throw new ArgumentException("Expected native receipt is required.");
        var packs = data.PackBatch.Count != 0 ? data.PackBatch : new List<LocalPackResult> { data.LastPack };
        string[] saved = packs.Any(pack => pack?.Cards == null) ? null : packs.SelectMany(pack => pack.Cards.Select(card => card.Key)).ToArray();
        if (expectedSequence != data.PackSequence || saved == null || !saved.SequenceEqual(ids, StringComparer.Ordinal))
            throw new InvalidOperationException("Native pack acknowledgement does not match the current receipt.");
        if (!data.PackRevealPending) return false;
        var next = Snapshot();
        next.PackRevealPending = false;
        next.PackBatch.Clear();
        Commit(next);
        return true;
    }

    private LocalPackResult CommitPack(string packType, bool pendingReveal)
    {
        var next = Snapshot();
        var result = DrawPack(next, packType);
        next.LastPack = Copy(result);
        next.PackRevealPending = pendingReveal;
        Commit(next);
        Backup("pack");
        return Copy(result);
    }

    // One pack drawn into next (inventory, sequence, counters, collection, acquisition, gold, achievements).
    private static LocalPackResult DrawPack(LocalProfileData next, string packType)
    {
        if (!FairPackRules.IsKnown(packType) || !next.UnopenedPacks.Any(pack => pack.PackType == packType && pack.Count > 0))
            throw new InvalidOperationException("No local packs of that type are available.");
        // Acquisition as it stood before this pack (a single open reads the committed save here).
        var prior = next.Collection.Concat(next.PremiumCollection).ToDictionary(card => LocalPackCard.ReceiptKey(card.Id, card.Premium), card => card.Acquired, StringComparer.Ordinal);
        int AcquiredBefore(string id) => prior.TryGetValue(id, out int acquired) ? acquired : 0;
        LocalPackResult result;
        var progress = next.PackCounters.Single(entry => entry.PackType == packType);
        var before = progress.Counters.Copy();
        if (packType == FairPackRules.Legacy) {
            var collection = CollectionMap(next.Collection);
            result = LocalEconomy.GeneratePack(collection, next.PackSeed, next.PackSequence);
            foreach (var entry in collection) {
                var card = GetCollectionCard(next, entry.Key);
                card.Count = entry.Value;
            }
        } else {
            var acquired = next.Collection.Concat(next.PremiumCollection).GroupBy(card => card.Id)
                .ToDictionary(group => group.Key, group => group.Sum(card => card.Acquired), StringComparer.Ordinal);
            var draw = FairPackRules.Generate(packType, FairPackRules.RuleVersion, (ulong)next.PackSeed, next.PackSequence,
                before.Copy(), id => acquired.TryGetValue(id, out int count) ? count : 0);
            result = new LocalPackResult { PackRuleVersion = FairPackRules.RuleVersion,
                Cards = draw.CardIds.Select((id, slot) => new LocalPackCard { Id = id, Premium = draw.Premiums[slot] }).ToList() };
            foreach (var drawn in result.Cards) {
                var card = GetCollectionCard(next, drawn.Id, drawn.Premium);
                card.Count = checked(card.Count + 1);
            }
            progress.Counters = draw.After.Copy();
        }
        result.PackType = packType;
        result.CountersBefore = before;
        foreach (var card in result.Cards) card.AcquiredBefore = AcquiredBefore(card.Key);
        foreach (var group in result.Cards.GroupBy(card => card.Key)) {
            var drawn = group.First();
            var card = GetCollectionCard(next, drawn.Id, drawn.Premium);
            int gained = group.Count(c => c.DuplicateGold == 0);
            card.Acquired = Math.Min(card.Premium == 0 ? Playset(card.Id) : 2, checked(AcquiredBefore(group.Key) + gained));
        }
        if (packType == FairPackRules.Legacy) progress.Counters = CountersAfter(before, result.Cards.Select(card => card.Id));
        next.PackSequence = checked(next.PackSequence + 1);
        next.UnopenedPacks.Single(pack => pack.PackType == packType).Count--;
        next.GoldBalance = checked(next.GoldBalance + result.Cards.Sum(card => card.DuplicateGold));
        ReconcileCollectionAchievement(next);
        return result;
    }

    public void SaveDeck(string name, string hero, IEnumerable<string> ids)
    {
        LocalDeck deck = LedgerDeck(name, hero, ids, null, true);
        var next = Snapshot();
        if (next.CustomDecks.Count > 1)
            throw new InvalidOperationException("The legacy custom slot is ambiguous with multiple saved decks.");
        if (next.CustomDecks.Count == 0)
        {
            EnsureAllocatorAvailable(next.NextCustomDeckId);
            long id = next.NextCustomDeckId;
            next.NextCustomDeckId = checked(id + 1);
            next.CustomDecks.Add(new LocalCustomDeck { Id = id, SortOrder = id, Name = deck.Name, Hero = deck.Hero, Cards = deck.CardIds });
        }
        else
        {
            var saved = next.CustomDecks[0];
            saved.Name = deck.Name; saved.Hero = deck.Hero; saved.Cards = deck.CardIds;
        }
        Commit(next);
    }

    public LocalCustomDeck GetDraft(long id) => Copy(data.CustomDecks.SingleOrDefault(deck => deck.Id == id));

    public LocalCustomDeck CreateDraft(string name, string hero)
    {
        LocalDeck draft = LocalDecks.Draft(name, hero, Array.Empty<string>());
        if (CustomDecks.Count >= MaximumCustomDecks)
            throw new InvalidOperationException(PersonalDeckLimitMessage);
        EnsureAllocatorAvailable(data.NextCustomDeckId);
        var next = Snapshot();
        long id = next.NextCustomDeckId;
        next.NextCustomDeckId = checked(id + 1);
        next.CustomDecks.Add(new LocalCustomDeck {
            Id = id, SortOrder = id, Name = draft.Name, Hero = draft.Hero, Cards = draft.CardIds
        });
        Commit(next);
        return GetDraft(id);
    }

    public LocalCustomDeck SaveDeckFromRecipe(string recipeId, LocalCustomDeck deck)
    {
        if (!data.OwnedRecipes.Contains(recipeId)) throw new InvalidOperationException("Recipe is not owned.");
        LocalShop.Default.Recipe(recipeId);
        if (deck == null) throw new ArgumentNullException(nameof(deck));
        LocalDeck draft = LedgerDeck(deck.Name, deck.Hero, deck.Cards, deck.Sideboards, false);
        if (!LocalCardCatalog.WithinCopyLimits(draft.CardIds)) throw new InvalidOperationException("Deck exceeds the playable copy limit across printings.");
        var matching = MatchingRecipeDeck(data, recipeId, draft.Hero, draft.CardIds, deck.Sideboards);
        if (matching != null) return Copy(matching);
        if (data.CustomDecks.Count >= MaximumCustomDecks) throw new InvalidOperationException(PersonalDeckLimitMessage);
        EnsureAllocatorAvailable(data.NextCustomDeckId);
        var next = Snapshot();
        long id = next.NextCustomDeckId++;
        var created = new LocalCustomDeck { Id = id, SortOrder = id, Name = draft.Name, Hero = draft.Hero,
            CardEntries = draft.CardIds.Select(id => new LocalDeckCard { Id = id }).ToList(), Sideboards = deck.Sideboards.Select(Copy).ToList(), SourceRecipeId = recipeId };
        LocalCrafting.AssignOwnedPremiums(created, DeckOwnedCount(next, profileOptions.AllCosmetics), true); // fresh deck: best owned versions
        next.CustomDecks.Add(created);
        Commit(next);
        return GetDraft(id);
    }

    private static void EnsureAllocatorAvailable(long nextId)
    {
        if (nextId >= long.MaxValue - 1)
            throw new InvalidOperationException("The personal deck ID allocator is exhausted.");
    }

    public void SaveDraft(long id, string name, string hero, IEnumerable<string> ids, long sortOrder, IEnumerable<LocalSideboard> sideboards = null,
        LocalDeckCosmetics cosmetics = null)
    {
        if (sortOrder < 0) throw new ArgumentOutOfRangeException(nameof(sortOrder));
        var boards = (sideboards ?? GetDraft(id)?.Sideboards ?? Enumerable.Empty<LocalSideboard>()).Select(Copy).ToList();
        LocalDeck draft = LedgerDeck(name, hero, ids, boards, false);
        if (!LocalCardCatalog.WithinCopyLimits(draft.CardIds))
            throw new InvalidOperationException("Deck exceeds the playable copy limit across printings.");
        var next = Snapshot();
        LocalCustomDeck saved = next.CustomDecks.SingleOrDefault(deck => deck.Id == id)
            ?? throw new InvalidOperationException("Saved draft does not exist.");
        if (cosmetics != null) ApplyCosmetics(saved, ValidDeckCosmetics(next, draft.Hero, cosmetics, profileOptions.AllCosmetics));
        saved.Name = draft.Name; saved.Hero = draft.Hero; saved.Cards = draft.CardIds; saved.SortOrder = sortOrder;
        saved.Sideboards = boards;
        Commit(next);
    }

    // Name and per-deck cosmetics only (the editor changed no card): the saved cards, versions and sideboards stay
    // byte-for-byte as they are, so the projection's choice of versions can never rewrite or block them.
    public void SaveDraftCosmetics(long id, string name, LocalDeckCosmetics cosmetics)
    {
        var next = Snapshot();
        LocalCustomDeck saved = next.CustomDecks.SingleOrDefault(deck => deck.Id == id)
            ?? throw new InvalidOperationException("Saved draft does not exist.");
        LocalDeck draft = LedgerDeck(name, saved.Hero, saved.Cards, saved.Sideboards, false);
        if (cosmetics != null) ApplyCosmetics(saved, ValidDeckCosmetics(next, draft.Hero, cosmetics, profileOptions.AllCosmetics));
        saved.Name = draft.Name;
        Commit(next);
    }

    // Card back: an owned back, or none (favourite) optionally drawn at random. Hero: an owned skin of the deck's
    // class (or the class hero itself), or none (favourite).
    private LocalDeckCosmetics ValidDeckCosmetics(LocalProfileData profile, string hero, LocalDeckCosmetics cosmetics, bool allCosmetics = false)
    {
        if (cosmetics.CardBack.HasValue && (cosmetics.RandomCardBack || !(profile.OwnedCardBacks.Contains(cosmetics.CardBack.Value)
            || allCosmetics && LocalShop.SupportedCheatCardBacks.Contains(cosmetics.CardBack.Value))))
            throw new InvalidOperationException("The deck card back is not owned.");
        if (cosmetics.Coin.HasValue && !(profile.OwnedCoins.Contains(cosmetics.Coin.Value)
            || allCosmetics && LocalShop.SupportedCheatCoins.Contains(cosmetics.Coin.Value)))
            throw new InvalidOperationException("The deck coin is not owned.");
        if (cosmetics.HeroSkin != null && !ValidFavorite(profile, hero, cosmetics.HeroSkin, allCosmetics))
            throw new InvalidOperationException("The deck hero skin is not owned for its class.");
        if (cosmetics.PetFamily != null && (!OwnsPet(cosmetics.PetFamily)
            || cosmetics.PetForm != null && (LocalShop.Default.PetFamily(cosmetics.PetForm) != cosmetics.PetFamily || !CanUsePetForm(cosmetics.PetForm)))
            || cosmetics.PetFamily == null && cosmetics.PetForm != null)
            throw new InvalidOperationException("Pet is not owned or form is not unlocked.");
        return cosmetics;
    }

    private static void ApplyCosmetics(LocalCustomDeck deck, LocalDeckCosmetics cosmetics)
    {
        deck.Coin = cosmetics.Coin; deck.RandomCoinUseFavorite = cosmetics.RandomCoinUseFavorite;
        deck.CardBack = cosmetics.CardBack; deck.RandomCardBack = cosmetics.RandomCardBack;
        deck.HeroSkin = cosmetics.HeroSkin; deck.RandomHeroUseFavorite = cosmetics.RandomHeroUseFavorite;
        deck.PetFamily = cosmetics.PetFamily; deck.PetForm = cosmetics.PetForm; deck.RandomPetUseFavorite = cosmetics.RandomPetUseFavorite;
    }

    // Match presentation for a personal deck: its own owned back/skin, else the profile favourite. A random back
    // draws from the owned backs.
    // What the deck tray shows: stored cosmetics the profile still owns (anything else reads as the favourite).
    public LocalDeckCosmetics DeckCosmetics(long deckId)
    {
        var deck = data.CustomDecks.SingleOrDefault(d => d.Id == deckId);
        if (deck == null) return null;
        return new LocalDeckCosmetics {
            Coin = deck.Coin is int coin && OwnsCoin(coin) ? deck.Coin : null, RandomCoinUseFavorite = deck.RandomCoinUseFavorite,
            CardBack = deck.CardBack is int back && OwnsCardBack(back) ? deck.CardBack : null,
            RandomCardBack = deck.RandomCardBack && !deck.CardBack.HasValue,
            HeroSkin = deck.HeroSkin != null && ValidFavorite(data, deck.Hero, deck.HeroSkin, profileOptions.AllCosmetics) ? deck.HeroSkin : null,
            RandomHeroUseFavorite = deck.RandomHeroUseFavorite,
            PetFamily = OwnsPet(deck.PetFamily) ? deck.PetFamily : null,
            PetForm = CanUsePetForm(deck.PetForm) ? deck.PetForm : null, RandomPetUseFavorite = deck.RandomPetUseFavorite };
    }

    internal string DeckPet(string deckKey, int seed)
    {
        var deck = deckKey == "custom" ? CustomDeck : data.CustomDecks.SingleOrDefault(d => "custom:" + d.Id == deckKey);
        if (deck == null) return EffectiveSelectedPet;
        if (deck.PetFamily != null && !OwnsPet(deck.PetFamily)) return EffectiveSelectedPet;
        if (deck.PetForm != null) return CanUsePetForm(deck.PetForm) ? deck.PetForm : EffectiveSelectedPet;
        var families = deck.PetFamily == null ? OwnedPets : new[] { deck.PetFamily };
        var forms = families.SelectMany(LocalShop.Default.FormsForPet).Where(CanUsePetForm)
            .Where(form => !deck.RandomPetUseFavorite || form == EffectiveSelectedPet)
            .OrderBy(form => form, StringComparer.Ordinal).ToArray();
        return forms.Length == 0 ? EffectiveSelectedPet : forms[new Random(seed).Next(forms.Length)];
    }

    public int DeckCardBack(long deckId, Random random)
    {
        if (random == null) throw new ArgumentNullException(nameof(random));
        var deck = data.CustomDecks.SingleOrDefault(d => d.Id == deckId);
        if (deck?.CardBack is int back && OwnsCardBack(back)) return back;
        var backs = OwnedCardBacks;
        if (deck != null && deck.RandomCardBack && backs.Count > 0)
            return backs[random.Next(backs.Count)];
        return MatchCardBack(random);
    }

    public int DeckCoin(long deckId, Random random)
    {
        if (random == null) throw new ArgumentNullException(nameof(random));
        var deck = data.CustomDecks.SingleOrDefault(d => d.Id == deckId);
        if (deck?.Coin is int coin && OwnsCoin(coin)) return coin;
        var coins = deck != null && !deck.RandomCoinUseFavorite ? OwnedCoins : FavoriteCoins;
        return coins.Count == 0 ? 1 : coins[random.Next(coins.Count)];
    }

    // Freeze the resolved native identity before sending any match packets; a replay never rerolls cosmetics.
    internal void SnapshotMatchCoin(int coin, string cardId)
    {
        if (data.Pending == null || data.Pending.CosmeticCoin.HasValue) return;
        if (!OwnsCoin(coin)) throw new InvalidOperationException("The match coin is not owned.");
        var next = Snapshot();
        next.Pending.CosmeticCoin = coin;
        next.Pending.CosmeticCoinCardId = cardId;
        Commit(next);
    }

    public string DeckHero(long? deckId, string baseHero)
    {
        var deck = data.CustomDecks.SingleOrDefault(d => d.Id == deckId);
        return ResolveHero(baseHero, deck?.Hero == baseHero ? deck.HeroSkin : null);
    }

    public void ReorderDrafts(IEnumerable<long> orderedIds)
    {
        if (orderedIds == null) throw new ArgumentNullException(nameof(orderedIds));
        long[] order = orderedIds.ToArray();
        var current = new HashSet<long>(CustomDecks.Select(deck => deck.Id));
        if (order.Length != current.Count || order.Distinct().Count() != order.Length || order.Any(id => !current.Contains(id)))
            throw new InvalidOperationException("Personal deck reorder must contain every visible draft exactly once.");
        var next = Snapshot();
        var byId = next.CustomDecks.ToDictionary(deck => deck.Id);
        var slots = next.CustomDecks.OrderBy(deck => deck.SortOrder).ThenBy(deck => deck.Id).ToArray();
        int visible = 0;
        for (int rank = 0; rank < slots.Length; rank++)
            (current.Contains(slots[rank].Id) ? byId[order[visible++]] : slots[rank]).SortOrder = rank;
        Commit(next);
    }

    public void DeleteDraft(long id)
    {
        var next = Snapshot();
        int removed = next.CustomDecks.RemoveAll(deck => deck.Id == id);
        if (removed != 1) throw new InvalidOperationException("Saved draft does not exist.");
        Commit(next);
    }

    // Every saved premium version (main deck and sideboards) is still owned. A disenchanted Golden copy leaves its deck
    // entry behind; such a deck takes the full save path, which writes the version the projection now shows.
    internal bool SavedVersionsOwned(long id)
    {
        var deck = data.CustomDecks.SingleOrDefault(row => row.Id == id);
        if (deck == null) return false;
        try {
            ValidateDeckOwnership(deck.CardEntries.Concat(deck.Sideboards.SelectMany(board => board.CardEntries)), false);
            return true;
        } catch (InvalidOperationException) { return false; }
    }

    public bool IsDeckComplete(long id)
    {
        var deck = GetDraft(id);
        if (deck == null) return false;
        try {
            PlayableDeck(deck.Name, deck.Hero, deck.Cards, deck.Sideboards);
            // Retail CollectionDeck.CanCardBeAddedAsOwned (254477): a sideboard copy is owned only beyond the main deck's
            // copies, for every sideboard (Beatrix and Underbelly contraband alike), so ownership counts both together.
            ValidateDeckOwnership(deck.CardEntries.Concat(deck.Sideboards.SelectMany(board => board.CardEntries)), true);
            return true;
        } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
    }

    public LocalDeck ResolveCustomDeck()
    {
        LocalCustomDeck deck = CustomDeck;
        return deck == null ? null : PlayableDeck(deck.Name, deck.Hero, deck.Cards, deck.Sideboards);
    }

    /// <summary>Owned copies of one version; the default and Collection remain Normal-only.</summary>
    public int OwnedCount(string id, int premium = 0)
    {
        if (premium < 0 || premium > 3) throw new ArgumentOutOfRangeException(nameof(premium));
        if (premium == 0) return id != null && Collection.TryGetValue(id, out int normal) ? normal : 0;
        if (!LocalClassRewards.IsAvailable(id)) return 0;
        int stored = data.PremiumCollection.SingleOrDefault(card => card.Id == id && card.Premium == premium)?.Count ?? 0;
        return profileOptions.AllCosmetics ? Math.Max(stored, CheatPremiumCount(id, premium)) : stored;
    }
    /// <summary>Premium copies earned through progression (class rewards, mastery, golden-hero portraits), never sold.</summary>
    internal static int ProgressionGrantedCount(LocalProfileData profile, string id, int premium) =>
        profile.ClassRewardGrants.Where(grant => grant.CardId == id && grant.Premium == premium).Sum(grant => grant.Copies)
        + profile.MasteryGrants.Where(grant => grant.CardId == id && grant.Premium == premium).Sum(grant => grant.Copies)
        + (premium == 1 ? profile.PortraitGrants.Count(grant => grant.Kind == "golden" && grant.CardId == id) : 0);
    /// <summary>Owned copies of one premium version that crafting may disenchant: owned minus progression-granted, floored at 0.</summary>
    internal static int DisenchantablePremiumCount(LocalProfileData profile, string id, int premium)
    {
        var row = profile.PremiumCollection.SingleOrDefault(card => card.Id == id && card.Premium == premium);
        return Math.Max(0, (row?.Count ?? 0) - ProgressionGrantedCount(profile, id, premium));
    }
    internal static IEnumerable<LocalCardCount> ProgressionCardCounts(LocalProfileData profile) => profile.PremiumCollection
        .Select(row => new LocalCardCount { Id = row.Id, Premium = row.Premium, Count = ProgressionGrantedCount(profile, row.Id, row.Premium) });
    internal IReadOnlyList<LocalCardCount> ProgressionCards => ProgressionCardCounts(data).ToArray();
    internal IReadOnlyList<LocalCardCount> StoredOwnedCards => data.Collection.Concat(data.PremiumCollection).Select(Copy).ToArray();
    internal int StoredOwnedCount(string id, int premium) => (premium == 0 ? data.Collection : data.PremiumCollection)
        .SingleOrDefault(row => row.Id == id && row.Premium == premium)?.Count ?? 0;
    public int ProgressionGrantedCount(string id, int premium) => ProgressionGrantedCount(data, id, premium);
    public int DisenchantablePremiumCount(string id, int premium) =>
        premium >= 1 && premium <= 3 && LocalClassRewards.IsAvailable(id) ? DisenchantablePremiumCount(data, id, premium) : 0;
    public int TotalOwnedCount(string id) => OwnedCards.Where(card => card.Id == id).Sum(card => card.Count);
    public IReadOnlyList<LocalCardCount> OwnedCards {
        get {
            var premium = data.PremiumCollection.Where(card => LocalClassRewards.IsAvailable(card.Id)).Select(Copy).ToList();
            if (profileOptions.AllCosmetics)
            {
                var indexed = premium.ToDictionary(card => PremiumKey(card.Id, card.Premium), StringComparer.Ordinal);
                foreach (var floor in SupportedCheatPremium)
                {
                    string key = PremiumKey(floor.Id, floor.Premium);
                    if (!indexed.TryGetValue(key, out var row)) { row = Copy(floor); premium.Add(row); indexed.Add(key, row); }
                    else { row.Count = Math.Max(row.Count, floor.Count); row.Acquired = Math.Max(row.Acquired, floor.Acquired); }
                }
            }
            return CollectionList(Collection).Concat(premium).ToArray();
        }
    }
    public IReadOnlyList<LocalClassRewardGrant> ClassRewardGrants => data.ClassRewardGrants.Select(Copy).ToArray();
    public IReadOnlyList<LocalPortraitGrant> PortraitGrants => data.PortraitGrants.Select(Copy).ToArray();

    /// <summary>Saves owned per-copy premium choices; Normal slots reconcile at the commit boundary.</summary>
    public void SavePremiumDraft(long id, string name, string hero, IEnumerable<LocalDeckCard> cards, long sortOrder,
        IEnumerable<LocalSideboard> sideboards = null, LocalDeckCosmetics cosmetics = null)
    {
        if (cards == null) throw new ArgumentNullException(nameof(cards));
        if (sortOrder < 0) throw new ArgumentOutOfRangeException(nameof(sortOrder));
        var entries = cards.Select(Copy).ToList();
        ValidateDeckEntries(entries, CurrentVersion);
        var boards = (sideboards ?? GetDraft(id)?.Sideboards ?? new List<LocalSideboard>()).Select(Copy).ToList();
        foreach (var board in boards) ValidateDeckEntries(board?.CardEntries, CurrentVersion);
        var draft = LedgerDeck(name, hero, entries.Select(card => card.Id), boards, false);
        if (!LocalCardCatalog.WithinCopyLimits(draft.CardIds))
            throw new InvalidOperationException("Deck exceeds the playable copy limit across printings.");
        // Normal copies may be missing (drafts keep incomplete decks); only the premium versions must be owned.
        ValidateDeckOwnership(entries.Concat(boards.SelectMany(board => board.CardEntries)), false);
        var next = Snapshot();
        var saved = next.CustomDecks.SingleOrDefault(deck => deck.Id == id)
            ?? throw new InvalidOperationException("Saved draft does not exist.");
        if (cosmetics != null) ApplyCosmetics(saved, ValidDeckCosmetics(next, draft.Hero, cosmetics, profileOptions.AllCosmetics));
        saved.Name = draft.Name; saved.Hero = draft.Hero; saved.SortOrder = sortOrder;
        saved.CardEntries = entries; saved.Sideboards = boards;
        Commit(next);
    }

    // Profile commits are the common boundary for every slot writer and collection mutation.
    internal static Action<string> DeckRepairLog = Console.WriteLine;
    internal static Func<string, int, int> DeckOwnedCount(LocalProfileData profile, bool allCosmetics = false)
    {
        var normal = OwnedCollection(profile);
        var premium = profile.PremiumCollection.ToDictionary(row => PremiumKey(row.Id, row.Premium), row => row.Count, StringComparer.Ordinal);
        return (id, version) => version == 0 ? (normal.TryGetValue(id, out int count) ? count : 0)
            : !LocalClassRewards.IsAvailable(id) ? 0
            : Math.Max(premium.TryGetValue(PremiumKey(id, version), out int stored) ? stored : 0,
                allCosmetics ? CheatPremiumCount(id, version) : 0);
    }

    internal static List<string> RepairDeckPremiums(LocalProfileData profile, bool allCosmetics = false)
    {
        var owned = DeckOwnedCount(profile, allCosmetics);
        var logs = new List<string>();
        var decks = profile.CustomDecks.AsEnumerable();
        if (profile.CustomDeck != null && !profile.CustomDecks.Contains(profile.CustomDeck)) decks = decks.Append(profile.CustomDeck);
        foreach (var deck in decks) {
            var changes = LocalCrafting.AssignOwnedPremiums(deck, owned);
            if (changes.Count > 0) logs.Add("DECK_PREMIUM_REPAIR id=" + deck.Id + " name=" + deck.Name + " cards=" + string.Join(",", changes));
        }
        return logs;
    }

    private static void LogDeckRepairs(IEnumerable<string> lines)
    {
        foreach (string line in lines) { try { DeckRepairLog?.Invoke(line); } catch { } }
    }

    public int MissingDeckSlotCount(long id)
    {
        var deck = data.CustomDecks.SingleOrDefault(row => row.Id == id);
        if (deck == null) throw new InvalidOperationException("Saved draft does not exist.");
        return LocalCrafting.MissingDeckSlots(deck, DeckOwnedCount(data, profileOptions.AllCosmetics));
    }

    private void ValidateDeckOwnership(IEnumerable<LocalDeckCard> cards, bool normalToo)
    {
        var list = cards.ToList();
        var owned = DeckOwnedCount(data, profileOptions.AllCosmetics);
        var deck = new LocalCustomDeck { CardEntries = list };
        if (LocalCrafting.MissingDeckSlots(deck, owned, normalToo) > 0)
            throw new InvalidOperationException(normalToo ? "The local collection does not own this deck card."
                : "The local collection does not own this deck premium.");
    }

    private static LocalProfileData MigrateV10(LocalProfileData profile)
    {
        profile.Version = 11;
        return profile;
    }

    // V12: theme and board ledger. Nothing is owned beyond the free defaults; Classic theme, random owned board.
    private static LocalProfileData MigrateV11(LocalProfileData profile)
    {
        profile.Version = CurrentVersion;
        profile.SelectedHubTheme = LocalHubCosmetics.ClassicTheme;
        profile.SelectedBoard = LocalHubCosmetics.RandomBoard;
        return profile;
    }

    // Win portraits count Ranked wins only (owner 2026-10-28): Casual wins never reach the golden/honored thresholds.
    internal static bool XpWin(LocalMatchRecord match) => match.Result == "Won" && !match.Conceded && match.DeckHero != null
        && !CasualRules.IsCasual(match.Mode)
        && (match.ClassXpBefore.HasValue && match.ClassXpAfter.HasValue
            ? match.ClassXpAfter.Value > match.ClassXpBefore.Value : LocalProfile.MatchClassXp(match) > 0);

    private static bool GrantClassRewards(LocalProfileData profile, LocalMatchRecord match)
    {
        if (profile.PackRevealPending) return false;
        var rewards = new List<LocalClassRewardGrant>();
        var portraits = new List<LocalPortraitGrant>();
        var levels = profile.ClassXp.ToDictionary(row => row.Hero, row => LocalCoreProgression.Level(row.Xp));
        // A (class, level) is claimed once, whatever the active meta: only unclaimed levels use today's schedule.
        var claimed = new HashSet<string>(profile.ClassRewardGrants.Select(ClassGrantKey), StringComparer.Ordinal);
        foreach (var reward in LocalClassRewards.ActiveRewards.Where(row => levels[row.Hero] >= row.Level).OrderBy(row => row.Level)) {
            if (!claimed.Add(ClassGrantKey(reward.Hero, reward.Level))) continue;
            var grant = LocalClassRewards.Resolve(profile, reward.Hero, reward.Level);
            grant.GrantedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            profile.ClassRewardGrants.Add(grant);
            profile.DustBalance = checked(profile.DustBalance + grant.Dust);
            if (grant.Copies > 0) AddPremium(profile, grant.CardId, grant.Premium, grant.Copies);
            rewards.Add(Copy(grant));
        }
        // Settlement's record has not been appended yet. Reconciliation reads only committed history.
        var history = profile.Matches.Concat(match == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { match });
        var wins = history.Where(XpWin).GroupBy(row => row.DeckHero).ToDictionary(group => group.Key, group => group.Count());
        foreach (var hero in LocalClassRewards.Portraits) {
            int count = wins.TryGetValue(hero.Hero, out int value) ? value : 0;
            foreach (string kind in new[] { "golden", "honored" }) {
                int threshold = kind == "golden" ? LocalClassRewards.GoldenPortraitWins : LocalClassRewards.HonoredPortraitWins;
                if (count < threshold || profile.PortraitGrants.Any(row => row.Hero == hero.Hero && row.Kind == kind)) continue;
                var grant = new LocalPortraitGrant { Hero = hero.Hero, Kind = kind, CardId = kind == "golden" ? hero.Hero : hero.Honored, GrantedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
                profile.PortraitGrants.Add(grant);
                if (kind == "golden") AddPremium(profile, grant.CardId, 1, 1);
                else GrantHeroSkin(profile, grant.CardId);
                portraits.Add(Copy(grant));
            }
        }
        if (match != null) { match.ClassRewardGrants = rewards; match.PortraitGrants = portraits; }
        return rewards.Count != 0 || portraits.Count != 0;
    }

    internal static void AddPremium(LocalProfileData profile, string id, int premium, int copies)
    {
        var card = profile.PremiumCollection.SingleOrDefault(row => row.Id == id && row.Premium == premium);
        if (card == null) { card = new LocalCardCount { Id = id, Premium = premium }; profile.PremiumCollection.Add(card); }
        card.Count = checked(card.Count + copies);
        card.Acquired = Math.Min(2, checked(card.Acquired + copies));
    }

    private static void ValidateDeckEntries(IEnumerable<LocalDeckCard> entries, int version)
    {
        if (entries == null || entries.Any(row => row == null || !ValidName(row.Id, 128)
            || row.Premium < 0 || row.Premium > (version < 11 ? 0 : 3)))
            throw new InvalidOperationException("Invalid deck premium entry.");
    }

    private static string ClassGrantKey(string hero, int level) => hero + ":" + level;
    private static string ClassGrantKey(LocalClassRewardGrant row) => ClassGrantKey(row.Hero, row.Level);
    private static string PortraitKey(LocalPortraitGrant row) => row.Hero + ":" + row.Kind;
    private static void ValidatePremium(LocalProfileData profile)
    {
        LocalCardMastery.Validate(profile);
        if (profile.ClassRewardGrants == null || profile.PortraitGrants == null || profile.PremiumCollection == null
            || profile.ClassRewardGrants.Count > MaximumRecords || profile.PortraitGrants.Count > LocalCoreProgression.Heroes.Length * 2
            || profile.PremiumCollection.Count > MaximumRecords || profile.Collection.Any(row => row.Premium != 0))
            throw new InvalidOperationException("Invalid premium ledger.");
        if (profile.Version < 11 && (profile.PremiumCollection.Count != 0 || profile.ClassRewardGrants.Count != 0 || profile.PortraitGrants.Count != 0))
            throw new InvalidOperationException("Premium ownership requires V11.");
        if (profile.PremiumCollection.Any(row => row == null || !ValidName(row.Id, 128) || row.Premium < 1 || row.Premium > 3
                || row.Count < 0 || row.Acquired < Math.Min(2, row.Count) || row.Acquired > 2)
            || profile.PremiumCollection.Select(row => row.Id + ":" + row.Premium).Distinct().Count() != profile.PremiumCollection.Count)
            throw new InvalidOperationException("Invalid premium collection.");
        if (profile.ClassRewardGrants.Any(row => row == null || !LocalCoreProgression.Heroes.Contains(row.Hero)
                || row.Level < 11 || row.Level > 60 || row.ScheduleVersion < 1
                || row.GrantedUtc != null && !ValidAchievementDate(row.GrantedUtc)
                || row.ScheduledCardId != null && !ValidName(row.ScheduledCardId, 128)
                || (row.Dust == 0 ? !ValidName(row.CardId, 128) || row.Premium < 1 || row.Premium > 3
                    || row.Copies < 0 || row.Copies > LocalEconomy.MaximumPlayset
                    : row.Dust != 400 || row.CardId != null || row.Premium != 0 || row.Copies != 0 || row.ScheduledCardId == null))
            || profile.ClassRewardGrants.Select(ClassGrantKey).Distinct().Count() != profile.ClassRewardGrants.Count)
            throw new InvalidOperationException("Invalid class reward ledger.");
        // Stored rows are history: a corrected honored mapping must not fail load, so retired ids stay valid.
        if (profile.PortraitGrants.Any(row => row == null || !LocalClassRewards.ValidPortraitRow(row.Hero, row.Kind, row.CardId)
                || row.GrantedUtc != null && !ValidAchievementDate(row.GrantedUtc))
            || profile.PortraitGrants.Select(PortraitKey).Distinct().Count() != profile.PortraitGrants.Count)
            throw new InvalidOperationException("Invalid portrait ledger.");
        var grants = profile.ClassRewardGrants.ToDictionary(ClassGrantKey);
        var portraits = profile.PortraitGrants.ToDictionary(PortraitKey);
        var seenRewards = new HashSet<string>(); var seenPortraits = new HashSet<string>();
        foreach (var match in profile.Matches.Concat(profile.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { profile.Pending })) {
            bool absent = match.ClassRewardGrants == null && match.PortraitGrants == null;
            if (absent) continue; // Historical V10 receipts predate these fields.
            if (profile.Version < 11 || (match.Ladder == null && match.Casual == null) || match.Result == null || match.ClassRewardGrants == null || match.PortraitGrants == null)
                throw new InvalidOperationException("Partial premium reward receipt.");
            foreach (var row in match.ClassRewardGrants) {
                if (row == null || !grants.TryGetValue(ClassGrantKey(row), out var grant) || !seenRewards.Add(ClassGrantKey(row))
                    || row.CardId != grant.CardId || row.Premium != grant.Premium || row.Copies != grant.Copies
                    || row.ScheduleVersion != grant.ScheduleVersion || row.ScheduledCardId != grant.ScheduledCardId || row.Dust != grant.Dust || row.GrantedUtc != grant.GrantedUtc)
                    throw new InvalidOperationException("Invalid class reward receipt.");
            }
            foreach (var row in match.PortraitGrants)
                if (row == null || !portraits.TryGetValue(PortraitKey(row), out var grant) || row.CardId != grant.CardId || row.GrantedUtc != grant.GrantedUtc
                    || !seenPortraits.Add(PortraitKey(row)))
                    throw new InvalidOperationException("Invalid portrait receipt.");
        }
    }

    public static int NextStars(int stars, string result)
    {
        if (result == "Won") return Math.Min(150, stars + 1);
        if (result != "Lost" && result != "Abandoned") return stars;
        if (stars < 30 || stars >= 150) return stars;
        int floor = Math.Max(stars / 30 * 30, stars >= 135 ? 135 : 0);
        return Math.Max(floor, stars - 1);
    }

    public static string RankName(int stars)
    {
        if (stars >= 150) return "Legend";
        return new[] { "Bronze", "Silver", "Gold", "Platinum", "Diamond" }[stars / 30]
            + " " + (10 - stars % 30 / 3);
    }

    private static int AwardV1(string result, int turns, bool conceded) =>
        conceded || result == "Abandoned" || turns < 4 ? 0 : result == "Won" ? 50 : 25;

    private static LocalProfileData NewProfile()
    {
        var freshLadder = LadderRules.Fresh();
        var profile = new LocalProfileData {
            Version = CurrentVersion,
            GrindTier = GrindTiers.Default.Id,
            ClassXp = EmptyXp(),
            PackSeed = Guid.NewGuid().GetHashCode() & int.MaxValue,
            Collection = CollectionList(LocalEconomy.StarterCollection),
            NextCustomDeckId = FirstAllocatedCustomDeckId,
            PackCounters = new[] { FairPackRules.Legacy }.Concat(FairPackRules.PackTypes)
                .Select(type => new LocalPackProgress { PackType = type }).ToList(),
            LadderRuleVersion = LadderRules.RuleVersion,
            LadderState = new LocalLadderState { StarIndex = freshLadder.StarIndex, BestStarIndex = freshLadder.BestStarIndex,
                Multiplier = freshLadder.Multiplier, StreakWins = freshLadder.StreakWins,
                PlacementGames = freshLadder.PlacementGames, HiddenRating = freshLadder.Rating,
                LegendReached = freshLadder.LegendReached },
            OwnedCoins = new List<int> { 1 }, FavoriteCoins = new List<int> { 1 },
            OwnedCardBacks = new List<int> { 0 },
            FavoriteCardBacks = new List<int> { 0 },
            SelectedHubTheme = LocalHubCosmetics.ClassicTheme,
            SelectedBoard = LocalHubCosmetics.RandomBoard,
            Achievements = NewAchievementState()
        };
        foreach (var card in profile.Collection) card.Acquired = Math.Min(card.Count, Playset(card.Id));
        GrantCoreUnlocks(profile);
        ValidateV14(profile);
        return profile;
    }

    private static LocalProfileData MigrateV1(LocalProfileData old)
    {
        var migrated = new LocalProfileData {
            Version = 9,
            Pending = Copy(old.Pending),
            Matches = old.Matches.Select(Copy).ToList(),
            PackSeed = Guid.NewGuid().GetHashCode() & int.MaxValue,
            Collection = CollectionList(LocalEconomy.StarterCollection),
            NextCustomDeckId = FirstAllocatedCustomDeckId
        };
        migrated.GoldBalance = migrated.Matches.Aggregate(0, (gold, match) => checked(gold + match.Gold));
        foreach (var match in migrated.Matches)
        {
            match.RewardVersion = 1;
            match.RewardFacts = DefaultFacts();
        }
        if (migrated.Pending != null)
        {
            migrated.Pending.RewardVersion = 2;
            migrated.Pending.RewardFacts = DefaultFacts();
        }
        UpgradeProgression(migrated);
        ValidateV8(migrated);
        return migrated;
    }

    private static LocalProfileData MigrateV2(LocalProfileData old)
    {
        var collection = CollectionMap(old.Collection);
        foreach (string id in LocalEconomy.PaladinBasicIds) collection.Add(id, 2);
        AddCurrentBasic(collection);
        var migrated = new LocalProfileData {
            Version = 9,
            Pending = Copy(old.Pending),
            Matches = old.Matches.Select(Copy).ToList(),
            GoldBalance = old.GoldBalance,
            LegacyUnopenedPacks = old.LegacyUnopenedPacks,
            PackSeed = old.PackSeed,
            PackSequence = old.PackSequence,
            Collection = CollectionList(collection),
            CustomDecks = MigratedDecks(old.CustomDeck),
            NextCustomDeckId = FirstAllocatedCustomDeckId,
            LastPack = CopyLegacyPack(old.LastPack),
            PackRevealPending = false
        };
        UpgradeProgression(migrated);
        ValidateV8(migrated);
        return migrated;
    }

    private static LocalProfileData MigrateV3(LocalProfileData old)
    {
        var collection = CollectionMap(old.Collection);
        AddCurrentBasic(collection);
        var migrated = new LocalProfileData {
            Version = 9,
            Pending = Copy(old.Pending),
            Matches = old.Matches.Select(Copy).ToList(),
            GoldBalance = old.GoldBalance,
            LegacyUnopenedPacks = old.LegacyUnopenedPacks,
            PackSeed = old.PackSeed,
            PackSequence = old.PackSequence,
            Collection = CollectionList(collection),
            CustomDecks = MigratedDecks(old.CustomDeck),
            NextCustomDeckId = FirstAllocatedCustomDeckId,
            LastPack = CopyLegacyPack(old.LastPack),
            PackRevealPending = old.PackRevealPending
        };
        UpgradeProgression(migrated);
        ValidateV8(migrated);
        return migrated;
    }

    private static LocalProfileData MigrateV4(LocalProfileData old)
    {
        var collection = CollectionMap(old.Collection);
        AddCurrentBasic(collection);
        var migrated = new LocalProfileData {
            Version = 9,
            Pending = Copy(old.Pending),
            Matches = old.Matches.Select(Copy).ToList(),
            GoldBalance = old.GoldBalance,
            LegacyUnopenedPacks = old.LegacyUnopenedPacks,
            PackSeed = old.PackSeed,
            PackSequence = old.PackSequence,
            Collection = CollectionList(collection),
            CustomDecks = old.CustomDecks.Select(Copy).ToList(),
            NextCustomDeckId = old.NextCustomDeckId,
            LastPack = CopyLegacyPack(old.LastPack),
            PackRevealPending = old.PackRevealPending
        };
        UpgradeProgression(migrated);
        ValidateV8(migrated);
        return migrated;
    }

    private static List<LocalClassXp> EmptyXp() => LocalCoreProgression.Heroes
        .Select(hero => new LocalClassXp { Hero = hero }).ToList();

    private static Dictionary<string, int> XpMap(LocalProfileData profile) => profile.ClassXp
        .ToDictionary(entry => entry.Hero, entry => entry.Xp, StringComparer.Ordinal);

    private static Dictionary<string, int> OwnedCollection(LocalProfileData profile, bool availableOnly = true)
    {
        var owned = CollectionMap(profile.Collection);
        foreach (var card in profile.CoreGrants)
            if (!availableOnly || LocalCoreProgression.IsAvailable(card.Id))
                owned[card.Id] = card.Count;
        return owned;
    }

    private static IEnumerable<LocalCustomDeck> VisibleDrafts(LocalProfileData profile)
    {
        return profile.CustomDecks.Where(deck => LocalDecks.PlayerHeroes.Contains(deck.Hero)
            && deck.Cards.All(id => LedgerCardIndex.ContainsKey(id) || LocalDecks.IsCustomCard(id))
            && deck.Sideboards.SelectMany(row => row.Cards).All(id => LocalCardCatalog.InActiveMeta(SabberStoneCore.Model.Cards.FromId(id))))
            .OrderBy(deck => deck.SortOrder).ThenBy(deck => deck.Id).Take(MaximumCustomDecks);
    }

    private static bool GrantCoreUnlocks(LocalProfileData profile)
    {
        bool changed = profile.CoreScheduleVersion < LocalCoreProgression.ScheduleVersion;
        profile.CoreScheduleVersion = Math.Max(profile.CoreScheduleVersion, LocalCoreProgression.ScheduleVersion);
        foreach (var card in LocalCoreProgression.Owned(XpMap(profile), false))
        {
            var grant = profile.CoreGrants.SingleOrDefault(entry => entry.Id == card.Key);
            if (grant == null) { profile.CoreGrants.Add(new LocalCardCount { Id = card.Key, Count = card.Value }); changed = true; }
            else if (grant.Count < card.Value) { grant.Count = card.Value; changed = true; }
        }
        return changed;
    }

    private static string ResolveDeckHero(string key, LocalProfileData profile)
    {
        LocalDeck loaner = LocalDecks.Resolve(
            LocalDecks.All.Concat(LocalDecks.TestLoaners).Concat(LocalDecks.PlayerStarterDecks), key);
        if (loaner != null) return loaner.Hero;
        if (key == "custom") return profile.CustomDecks.Count == 1 ? profile.CustomDecks[0].Hero : null;
        if (key != null && key.StartsWith("custom:", StringComparison.Ordinal)
            && long.TryParse(key.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            return profile.CustomDecks.SingleOrDefault(deck => deck.Id == id)?.Hero;
        return null;
    }

    private static void UpgradeProgression(LocalProfileData profile)
    {
        profile.Version = 9;
        profile.ClassXp = EmptyXp();
        foreach (LocalMatchRecord match in profile.Matches)
        {
            match.DeckHero = ResolveDeckHero(match.Deck, profile);
            if (match.DeckHero != null)
            {
                LocalClassXp progress = profile.ClassXp.Single(entry => entry.Hero == match.DeckHero);
                progress.Xp = checked(progress.Xp + LocalProfile.MatchClassXp(match));
            }
        }
        if (profile.Pending != null) profile.Pending.DeckHero = ResolveDeckHero(profile.Pending.Deck, profile);
        GrantCoreUnlocks(profile);
    }

    internal string ProgressionSummary(LocalMatchRecord match)
    {
        if (match == null || !data.Matches.Any(saved => saved.Id == match.Id)) return "";
        if (match.DeckHero == null) return "\n\nClass XP +0 (historical class unavailable)";
        int gained = LocalProfile.MatchClassXp(match);
        var after = XpMap(data);
        // The pending presentation is the latest settlement; acknowledgements never change XP.
        var before = new Dictionary<string, int>(after, StringComparer.Ordinal);
        before[match.DeckHero] -= gained;
        int oldLevel = LocalCoreProgression.Level(before[match.DeckHero]);
        int newLevel = LocalCoreProgression.Level(after[match.DeckHero]);
        string text = "\n\n" + LocalCoreProgression.ClassName(match.DeckHero) + " XP +" + gained;
        if (newLevel > oldLevel) text += "\nLevel up: " + oldLevel + " to " + newLevel;
        var oldCards = LocalCoreProgression.Owned(before);
        string[] unlocked = LocalCoreProgression.Owned(after).Keys.Where(id => !oldCards.ContainsKey(id))
            .Select(id => LocalCoreProgression.ById[id])
            .Select(card => card.Name + " x" + card.Copies).ToArray();
        if (unlocked.Length != 0) text += "\nCore unlocked: " + string.Join(", ", unlocked);
        if (match.ClassRewardGrants?.Count > 0) text += "\nClass rewards: " + string.Join(", ", match.ClassRewardGrants.OrderBy(row => row.Level).Select(row =>
            row.Dust > 0 ? row.Dust + " dust" : new[] { "", "Golden ", "Diamond ", "Signature " }[Math.Max(0, Math.Min(3, row.Premium))]
            + (LocalClassRewards.Rewards.FirstOrDefault(reward => reward.Hero == row.Hero && reward.CardId == row.CardId)?.Name ?? SummaryCardName(row.CardId))
            + (row.Copies > 1 ? " x" + row.Copies : "")));
        if (match.PortraitGrants?.Count > 0) text += "\nPortraits unlocked: " + string.Join(", ", match.PortraitGrants.Select(row =>
            (row.Kind == "golden" ? "Golden " : "Honored ") + SummaryCardName(row.CardId)));
        return text;
    }

    private static string SummaryCardName(string id) => id == null ? "" : SabberStoneCore.Model.Cards.FromId(id)?.Name ?? id;

    private static void AddCurrentBasic(Dictionary<string, int> collection)
    {
        foreach (string id in LocalCardCatalog.NewBasicIds)
            if (!collection.ContainsKey(id)) collection.Add(id, 2);
    }

    private static List<LocalCustomDeck> MigratedDecks(LocalCustomDeck deck) => deck == null
        ? new List<LocalCustomDeck>()
        : new List<LocalCustomDeck> { new LocalCustomDeck {
            Id = LegacyCustomDeckId, SortOrder = LegacyCustomDeckId,
            Name = deck.Name, Hero = deck.Hero, Cards = deck.Cards.ToList()
        } };

    private LocalProfileData Snapshot() => new LocalProfileData {
        Version = CurrentVersion, GrindTier = data.GrindTier,
        MasteryGrants = data.MasteryGrants.Select(LocalCardMastery.Clone).ToList(),
        ClassRewardGrants = data.ClassRewardGrants.Select(Copy).ToList(),
        PortraitGrants = data.PortraitGrants.Select(Copy).ToList(),
        PremiumCollection = data.PremiumCollection.Select(Copy).ToList(),
        CoreScheduleVersion = data.CoreScheduleVersion,
        CoreGrants = data.CoreGrants.Select(Copy).ToList(),
        ClassXp = data.ClassXp.Select(entry => new LocalClassXp { Hero = entry.Hero, Xp = entry.Xp }).ToList(),
        Pending = Copy(data.Pending),
        Matches = data.Matches.Select(Copy).ToList(),
        GoldBalance = data.GoldBalance,
        UnopenedPacks = data.UnopenedPacks.Select(Copy).ToList(),
        PackCounters = data.PackCounters.Select(Copy).ToList(),
        DustBalance = data.DustBalance, CraftSequence = data.CraftSequence, LastCraft = Copy(data.LastCraft),
        LadderState = Copy(data.LadderState), LadderRuleVersion = data.LadderRuleVersion,
        CasualRating = data.CasualRating, PlayMode = data.PlayMode,
        LadderCalibration = data.LadderCalibration == null ? null : new LocalLadderCalibration {
            Version = data.LadderCalibration.Version, MatchCount = data.LadderCalibration.MatchCount
        },
        Achievements = Copy(data.Achievements), PendingNonMatchAchievementNoticeId = data.PendingNonMatchAchievementNoticeId,
        PendingAchievementToastMatchIds = AchievementToastIds(data).ToList(), PendingAchievementToastMatchIdsSpecified = true,
        CardTokens = Copy(data.CardTokens), DeckKits = data.DeckKits.Select(Copy).ToList(), Trophies = data.Trophies.Select(Copy).ToList(),
        LegendDeck = Copy(data.LegendDeck), MilestoneCatchUp = Copy(data.MilestoneCatchUp), Welcome = Copy(data.Welcome),
        HiddenLoaners = (data.HiddenLoaners ?? new List<string>()).ToList(),
        SeenMassPackTooltip = data.SeenMassPackTooltip,
        PurchaseSequence = data.PurchaseSequence, Purchases = data.Purchases.Select(Copy).ToList(),
        OwnedCoins = data.OwnedCoins.ToList(), FavoriteCoins = data.FavoriteCoins.ToList(),
        OwnedRecipes = data.OwnedRecipes.ToList(), OwnedCardBacks = data.OwnedCardBacks.ToList(),
        OwnedHeroSkins = data.OwnedHeroSkins.ToList(), FavoriteCardBacks = data.FavoriteCardBacks.ToList(),
        FavoriteHeroSkins = data.FavoriteHeroSkins.Select(Copy).ToList(),
        OwnedHubThemes = data.OwnedHubThemes.ToList(), SelectedHubTheme = data.SelectedHubTheme,
        OwnedBoards = data.OwnedBoards.ToList(), SelectedBoard = data.SelectedBoard,
        OwnedPets = data.OwnedPets.ToList(), SelectedPet = data.SelectedPet,
        PetProgress = data.PetProgress.Select(row => new LocalPetProgress { Pet = row.Pet, Xp = row.Xp }).ToList(),
        PackSeed = data.PackSeed,
        PackSequence = data.PackSequence,
        Collection = data.Collection.Select(Copy).ToList(),
        CustomDecks = data.CustomDecks.Select(Copy).ToList(),
        NextCustomDeckId = data.NextCustomDeckId,
        LastPack = Copy(data.LastPack),
        PackRevealPending = data.PackRevealPending,
        PackBatch = (data.PackBatch ?? new List<LocalPackResult>()).Select(Copy).ToList(),
        PendingRewardPresentationId = data.PendingRewardPresentationId,
        // Viewed cards queued by MarkSeen fold in here, ahead of whatever the caller changes next: exactly the
        // order one commit per viewed card would have produced.
        UnseenCards = (data.UnseenCards ?? new List<LocalCardSeen>()).Where(row => !pendingSeen.Contains(SeenKey(row.Id, row.Premium))).Select(Copy).ToList()
    };

    internal Action<string> CommitFailure;

    internal T CommitChange<T>(Func<LocalProfileData, T> change)
    {
        var next = Snapshot();
        T result = change(next);
        Commit(next);
        return result;
    }

    private void Commit(LocalProfileData next)
    {
        if (next.Version != CurrentVersion) throw new InvalidOperationException("Only V15 profiles can be committed.");
        ValidateV14(next, profileOptions.AllCosmetics, profileOptions.LegendRating, profileOptions.AllDecks);
        GrantCoreUnlocks(next);
        GrantClassRewards(next, null);
        // O(history) catch-up keeps changed thresholds/metas current; real local histories are small.
        LocalCardMastery.Grant(next, null);
        var repaired = RepairDeckPremiums(next, profileOptions.AllCosmetics);
        TrackSeen(data, next);
        TryCalibrateLadder(next);
        ValidateV14(next, profileOptions.AllCosmetics, profileOptions.LegendRating, profileOptions.AllDecks);
        string temporary = path + ".tmp";
        bool preserveTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                CommitFailure?.Invoke("write");
                Serializer.Serialize(stream, next);
                CommitFailure?.Invoke("flush");
                stream.Flush(true);
                // Migration adds fields per record; only the final serialized size fits the loader's budget.
                if (stream.Length > MaximumProfileBytes)
                    throw new InvalidOperationException("Local profile exceeds the size limit.");
            }
            CommitFailure?.Invoke("replace");
            try
            {
                if (File.Exists(path)) { CommitFaultHook?.Invoke("replace"); File.Replace(temporary, path, null); }
                else File.Move(temporary, path);
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(temporary))
                {
                    preserveTemporary = true;
                    try { File.Move(temporary, path); } catch { }
                }
                // Recovery may leave next on disk; data stays unchanged because the commit still throws.
                throw;
            }
            data = next;
            LogDeckRepairs(repaired);
            pendingSeen.Clear(); // Every Snapshot since the last queue already folded them in.
        }
        finally { if (!preserveTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureBackup(byte[] original, int version)
    {
        string backup = path + ".v" + version + ".bak";
        if (File.Exists(backup))
        {
            long backupLength = new FileInfo(backup).Length;
            if (backupLength > MaximumProfileBytes || backupLength != original.LongLength
                || !File.ReadAllBytes(backup).SequenceEqual(original))
                throw new InvalidOperationException("Existing V" + version + " backup does not match the profile.");
            return;
        }
        string temporary = backup + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(original, 0, original.Length);
                stream.Flush(true);
            }
            File.Move(temporary, backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateV1(LocalProfileData profile)
    {
        if (profile == null || profile.Version != 1 || profile.Matches == null || profile.Matches.Count > MaximumRecords
            || profile.GoldBalance != 0 || profile.LegacyUnopenedPacks != 0 || profile.PackSeed != 0 || profile.PackSequence != 0
            || profile.Collection == null || profile.Collection.Count != 0 || profile.CustomDeck != null || profile.LastPack != null
            || profile.PackRevealPending || profile.PendingRewardPresentationId != null)
            throw new InvalidOperationException("Unsupported local profile.");
        ValidateMatchList(profile, 1);
    }

    private static void ValidateV2(LocalProfileData profile)
    {
        ValidateCurrent(profile, 2, LocalEconomy.LegacyV2Catalog, LocalEconomy.LegacyV2StarterCollection, true);
    }

    private static void ValidateV3(LocalProfileData profile)
    {
        ValidateCurrent(profile, 3, LocalEconomy.LegacyV4Catalog, LocalEconomy.LegacyV4StarterCollection, false);
    }

    private static void ValidateV4(LocalProfileData profile)
    {
        ValidateCurrent(profile, 4, LocalEconomy.LegacyV4Catalog, LocalEconomy.LegacyV4StarterCollection, false);
        ValidateDeckLibrary(profile);
    }

    private static void ValidateV5(LocalProfileData profile)
    {
        ValidateCurrent(profile, 5, LocalCardCatalog.Current, LocalEconomy.StarterCollection, false);
        ValidateDeckLibrary(profile);
    }

    private static readonly string[] V6Heroes = { "HERO_08", "HERO_04", "HERO_09", "HERO_01" };

    private static readonly string[] V7Heroes = { "HERO_08", "HERO_04", "HERO_09", "HERO_01",
        "HERO_06", "HERO_05", "HERO_03", "HERO_02", "HERO_07", "HERO_10" };

    private static void ValidateV8(LocalProfileData profile, bool allCosmetics = false, double legendRating = 0, bool allDecks = false) {
        ValidateProgression(profile, profile != null && (profile.Version == 9 || profile.Version == 10 || profile.Version == 11) ? profile.Version : CurrentVersion, LocalCoreProgression.Heroes);
        ValidatePremium(profile);
        if (profile.Version >= 10) { ValidateV10(profile, allCosmetics, allDecks); ValidateLadder(profile, legendRating); }
        ValidateSeen(profile);
    }

    private static void ValidateV14(LocalProfileData profile, bool allCosmetics = false, double legendRating = 0, bool allDecks = false)
    {
        GrindTiers.Find(profile?.GrindTier);
        ValidateV8(profile, allCosmetics, legendRating, allDecks);
        ValidateAchievements(profile);
        if (profile.HiddenLoaners == null || profile.HiddenLoaners.Any(key => !IsStarterLoanerShape(key))
            || profile.HiddenLoaners.Distinct(StringComparer.Ordinal).Count() != profile.HiddenLoaners.Count)
            throw new InvalidOperationException("Invalid hidden starter decks.");
    }

    private static void ValidateProgression(LocalProfileData profile, int version, string[] heroes)
    {
        if (profile == null || profile.ClassXp == null || profile.ClassXp.Count != heroes.Length
            || profile.ClassXp.Any(entry => entry == null || !heroes.Contains(entry.Hero) || entry.Xp < 0)
            || profile.ClassXp.Select(entry => entry.Hero).Distinct().Count() != heroes.Length)
            throw new InvalidOperationException("Invalid class XP.");
        if (profile.CoreScheduleVersion < 0 || profile.CoreGrants == null
            || profile.CoreGrants.Any(card => card == null || card.Premium != 0 || !LocalCardCatalog.IsCore(card.Id) || card.Count < 1 || card.Count > 2)
            || profile.CoreGrants.Select(card => card.Id).Distinct(StringComparer.Ordinal).Count() != profile.CoreGrants.Count)
            throw new InvalidOperationException("Invalid Core grant ledger.");
        // Pack ownership and receipts remain Classic-only; Core grants are monotonic.
        ValidateCurrent(profile, version, LocalCardCatalog.Current, LocalEconomy.StarterCollection, false);
        ValidateDeckLibrary(profile);
        if (version == 7 && profile.CustomDecks.Any(deck => !V7Heroes.Contains(deck.Hero)))
            throw new InvalidOperationException("Invalid V7 draft hero.");
    }

    private static void ValidateDeckLibrary(LocalProfileData profile)
    {
        if (profile.CustomDeck != null || profile.CustomDecks == null || profile.CustomDecks.Count > MaximumRecords
            || profile.NextCustomDeckId < FirstAllocatedCustomDeckId || profile.NextCustomDeckId == long.MaxValue)
            throw new InvalidOperationException("Invalid local deck library.");
        var ids = new HashSet<long>();
        foreach (LocalCustomDeck saved in profile.CustomDecks)
        {
            if (saved == null || !NativeCustomId(saved.Id) || !ids.Add(saved.Id) || saved.SortOrder < 0
                || saved.Id >= profile.NextCustomDeckId
                || (saved.SourceRecipeId != null && !profile.OwnedRecipes.Contains(saved.SourceRecipeId))
                || saved.Coin <= 0
                || saved.CardBack < 0 || saved.CardBack.HasValue && saved.RandomCardBack
                || saved.HeroSkin != null && !ValidName(saved.HeroSkin, 64)
                || saved.PetFamily != null && !LocalShop.Default.KnownPet(saved.PetFamily)
                || saved.PetForm != null && (saved.PetFamily == null || LocalShop.Default.PetFamily(saved.PetForm) != saved.PetFamily))
                throw new InvalidOperationException("Invalid saved local draft identity.");
            try {
                ValidateDeckEntries(saved.CardEntries, profile.Version);
                foreach (var board in saved.Sideboards) ValidateDeckEntries(board?.CardEntries, profile.Version);
                if (saved.Cards == null || saved.Cards.Count > LocalDecks.DraftLimit(saved.Cards))
                    throw new InvalidOperationException("Local draft exceeds its deck-size limit.");
                if (profile.Version >= 10 && NeedsExpandedDeckCatalog(saved.Cards))
                    LocalShop.ValidateDeck(saved.Name, saved.Hero, saved.Cards.Where(id => !LocalCardCatalog.IsCore(id)), saved.Sideboards, false, true, standardOnly: false);
                else LocalDecks.ValidateStoredDraft(saved.Name, saved.Hero, saved.Cards);
                new LocalDeck(saved.Name, saved.Hero, saved.Cards.ToArray()).WithSideboards(saved.Sideboards, allowInactive: true);
                if (profile.Version < 10) ValidateSideboardOwnership(saved.Sideboards, OwnedCollection(profile));
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { throw new InvalidOperationException("Invalid saved local draft.", error); }
            if (profile.Version < 10) ValidateOwnership(saved.Cards.Where(id => !LocalCardCatalog.IsCore(id)), profile.Collection);
        }
    }

    private static void ValidateCurrent(LocalProfileData profile, int version,
        IEnumerable<SabberStoneCore.Model.Card> knownCards, IReadOnlyDictionary<string, int> starter, bool legacyCustom)
    {
        if (profile == null || profile.Version != version || profile.Matches == null || profile.Matches.Count > MaximumRecords
            || (version < 10 && profile.UnopenedPacks.Count != 0)
            || profile.GoldBalance < 0 || profile.LegacyUnopenedPacks < 0 || profile.LegacyUnopenedPacks > 1000000
            || profile.PackSeed < 0 || profile.PackSequence < 0 || profile.Collection == null)
            throw new InvalidOperationException("Unsupported local profile.");
        if (version < 11 && (profile.PremiumCollection.Count != 0 || profile.ClassRewardGrants.Count != 0 || profile.PortraitGrants.Count != 0))
            throw new InvalidOperationException("Premium ownership requires V11.");
        if (version < 5 && profile.PendingRewardPresentationId != null)
            throw new InvalidOperationException("Unsupported local profile.");
        var catalog = version >= 10 ? LedgerCardIndex : knownCards.ToDictionary(card => card.Id, StringComparer.Ordinal);
        var collection = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in profile.Collection)
        {
            if (entry == null || !catalog.TryGetValue(entry.Id ?? "", out var card) || entry.Count < (version >= 10 ? 0 : 1)
                || (version < 10 && entry.Count > LocalEconomy.PlayableCap(card)) || collection.ContainsKey(entry.Id))
                throw new InvalidOperationException("Invalid local collection.");
            collection.Add(entry.Id, entry.Count);
        }
        if (version < 10 && starter.Any(pair => !collection.TryGetValue(pair.Key, out int count) || count < pair.Value))
            throw new InvalidOperationException("Invalid local starter collection.");
        if (profile.CustomDeck != null)
        {
            LocalDeck deck;
            try { deck = legacyCustom
                ? LocalDecks.LegacyV2Custom(profile.CustomDeck.Name, profile.CustomDeck.Hero, profile.CustomDeck.Cards)
                : LocalDecks.Custom(profile.CustomDeck.Name, profile.CustomDeck.Hero, profile.CustomDeck.Cards); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { throw new InvalidOperationException("Invalid saved local deck.", error); }
            if (deck.CardIds.GroupBy(id => id).Any(group => !collection.TryGetValue(group.Key, out int count) || group.Count() > count))
                throw new InvalidOperationException("Saved deck exceeds the local collection.");
        }
        ValidatePack(profile.LastPack, catalog, version);
        if (profile.PackRevealPending && (version < 3 || profile.LastPack == null))
            throw new InvalidOperationException("Invalid pending native pack receipt.");
        // A mass-opening batch exists only while its reveal is pending.
        if (profile.PackBatch == null || (profile.PackBatch.Count != 0 && !profile.PackRevealPending))
            throw new InvalidOperationException("Invalid mass pack receipt.");
        foreach (var pack in profile.PackBatch) ValidatePack(pack, catalog, version);
        if (profile.PackRevealPending) ValidatePendingPack(profile, collection);
        ValidateMatchList(profile, version);
        if (version >= 5 && profile.PendingRewardPresentationId != null)
        {
            if (string.IsNullOrWhiteSpace(profile.PendingRewardPresentationId)
                || profile.Pending != null
                || profile.Matches.Count(match => match.Id == profile.PendingRewardPresentationId
                    && match.RewardVersion >= 2 && match.Result != "Abandoned") != 1)
                throw new InvalidOperationException("Invalid pending reward presentation.");
        }
    }

    private static void ValidatePendingPack(LocalProfileData profile, Dictionary<string, int> collection)
    {
        if (profile.PackBatch.Count != 0) { ValidatePendingBatch(profile); return; }
        if (profile.Version >= 10 && profile.LastPack.PackType != FairPackRules.Legacy) {
            ValidateFairPending(profile); return;
        }
        if (profile.PackSequence < 1 || profile.LegacyUnopenedPacks == 1000000)
            throw new InvalidOperationException("Invalid pending native pack provenance.");
        var before = CollectionMap(collection);
        foreach (var card in profile.LastPack.Cards.Where(card => card.DuplicateGold == 0))
        {
            if (!before.TryGetValue(card.Id, out int count))
                throw new InvalidOperationException("Invalid pending native pack collection delta.");
            if (count == 1) before.Remove(card.Id);
            else before[card.Id] = count - 1;
        }
        int poolVersion = profile.LastPack.PoolVersion == 0 ? 1 : profile.LastPack.PoolVersion;
        var expected = LocalEconomy.GeneratePack(before, profile.PackSeed, profile.PackSequence - 1, poolVersion);
        bool sameReceipt = expected.Cards.Select(card => card.Id + ":" + card.DuplicateGold)
            .SequenceEqual(profile.LastPack.Cards.Select(card => card.Id + ":" + card.DuplicateGold), StringComparer.Ordinal);
        bool sameCollection = before.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(collection.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        if (!sameReceipt || !sameCollection || (profile.Version < 10 && profile.GoldBalance < expected.Cards.Sum(card => card.DuplicateGold)))
            throw new InvalidOperationException("Invalid pending native pack provenance.");
    }

    private static void ValidateMatchList(LocalProfileData profile, int profileVersion)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in profile.Matches)
            ValidateMatch(match, false, profileVersion, ids);
        if (profile.Pending != null) ValidateMatch(profile.Pending, true, profileVersion, ids);
        // Rival gold is paid at most once per NPC. The rival rule itself is not re-derived here (its constants may change).
        if (profile.Matches.Where(match => match.RivalGold != 0).GroupBy(match => match.NpcId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Duplicate rivalry reward.");
    }

    private static void ValidateMatch(LocalMatchRecord match, bool pending, int profileVersion, HashSet<string> ids)
    {
        GrindTiers.Find(match?.GrindTier);
        if (match == null || !Guid.TryParse(match.Id, out _) || !ids.Add(match.Id) || !ValidName(match.Deck, 64)
            || (match.FirstPlayer != 1 && match.FirstPlayer != 2) || !ValidName(match.Opponent, 64) || match.Seed < 0 || !ValidDate(match.StartedUtc)
            || (match.SettledUtc != null && (pending || !ValidDate(match.SettledUtc, true))))
            throw new InvalidOperationException("Invalid local match record.");
        if (match.CosmeticCoin <= 0 || match.CosmeticCoin >= 10000000
            || match.CosmeticCoinCardId != null && (!match.CosmeticCoin.HasValue || match.CosmeticCoin == 1
                || !Regex.IsMatch(match.CosmeticCoinCardId, "^[A-Za-z0-9_]{1,64}$")))
            throw new InvalidOperationException("Invalid match coin snapshot.");
        if (!ValidNpcFields(match.NpcId, match.NpcDeckId, match.NpcRating)
            || (match.NpcConceded == true && (pending || match.Result != "Won" || match.Conceded)) || (pending && match.NpcConceded != null))
            throw new InvalidOperationException("Invalid NPC encounter record.");
        if (match.RivalGold != 0 && (match.RivalGold != LocalNpcRecord.RivalSettleGold || pending || match.Result != "Won"
            || string.IsNullOrEmpty(match.NpcId)))
            throw new InvalidOperationException("Invalid rivalry reward.");
        if (match.DeckHero != null && !(profileVersion <= 6 ? V6Heroes : profileVersion == 7 ? V7Heroes : LocalCoreProgression.Heroes).Contains(match.DeckHero))
            throw new InvalidOperationException("Invalid match class.");
        if (profileVersion == 1)
        {
            if (match.RewardVersion != 0 || match.BonusGold != 0 || match.PackReward || match.RewardFacts != null)
                throw new InvalidOperationException("Invalid V1 reward fields.");
            if (pending)
            {
                if (!string.IsNullOrEmpty(match.Result) || match.Turns != 0 || match.Gold != 0 || match.Conceded)
                    throw new InvalidOperationException("Invalid pending match.");
                return;
            }
            ValidateTerminal(match);
            if (match.Gold != AwardV1(match.Result, match.Turns, match.Conceded))
                throw new InvalidOperationException("Invalid V1 award.");
            return;
        }
        // Rolled rewards exist only in profiles supporting them; V1/V2 keep their exact historical checks.
        bool rolled = LocalEconomy.IsRolled(match.RewardVersion) && profileVersion >= RollRewardProfileVersion;
        if (profileVersion == CurrentVersion && !rolled)
            throw new InvalidOperationException("Current profiles require V4, V5 or V6 match rewards.");
        if (match.RewardVersion != 1 && match.RewardVersion != 2 && !rolled)
            throw new InvalidOperationException("Invalid reward version.");
        LocalEconomy.ValidateFacts(match.RewardFacts);
        if (pending)
        {
            if ((match.RewardVersion != 2 && !rolled) || !string.IsNullOrEmpty(match.Result) || match.Turns != 0 || match.Gold != 0
                || match.Conceded || match.BonusGold != 0 || match.PackReward || !Same(match.RewardFacts, DefaultFacts())
                || (rolled && match.DustReward != 0))
                throw new InvalidOperationException("Invalid pending match.");
            return;
        }
        ValidateTerminal(match);
        if (match.RewardVersion == 1)
        {
            if (match.Gold != AwardV1(match.Result, match.Turns, match.Conceded) || match.BonusGold != 0 || match.PackReward
                || !Same(match.RewardFacts, DefaultFacts()))
                throw new InvalidOperationException("Invalid historical reward.");
        }
        else
        {
            var reward = MatchReward(match, match.Result, match.Turns, match.Conceded, match.RewardFacts);
            if (match.Gold != reward.Gold || match.BonusGold != reward.BonusGold || match.PackReward != reward.PackReward
                || (rolled && match.DustReward != reward.Dust))
                throw new InvalidOperationException("Invalid local reward.");
        }
    }

    private const int RollRewardProfileVersion = 11;

    // Missing = playset minus lifetime acquisition (the fair-pack generator's notion of a complete pool), per set in
    // catalogue order. The draw is not re-validated on load: the collection it read is not part of the receipt.
    internal static string RewardPackSet(LocalProfileData next, LocalMatchRecord match)
    {
        var acquired = next.Collection.ToDictionary(card => card.Id, card => card.Acquired, StringComparer.Ordinal);
        var missing = FairPackRules.PackTypes.Where(type => type != FairPackRules.Standard && type != FairPackRules.Legacy)
            .Select(type => new KeyValuePair<string, int>(type, FairPackRules.Eligible(type, FairPackRules.RuleVersion)
                .Sum(card => Math.Max(0, Playset(card.Id) - (acquired.TryGetValue(card.Id, out int count) ? count : 0)))))
            .ToList();
        return LocalEconomy.RewardPackSet(LocalEconomy.RewardRollSeed(match.Id, match.Seed), missing, FairPackRules.NewestSet);
    }

    // Rolled rewards recompute their versioned base roll from persisted Id and Seed; achievement payouts have separate receipts.
    private static LocalReward MatchReward(LocalMatchRecord match, string result, int turns, bool conceded, LocalRewardFacts facts)
    {
        if (!LocalEconomy.IsRolled(match.RewardVersion)) return LocalEconomy.Reward(result, turns, conceded, facts);
        int rollSeed = LocalEconomy.RewardRollSeed(match.Id, match.Seed);
        var reward = match.RewardVersion == 4 ? LocalEconomy.RewardV4(result, turns, conceded, rollSeed)
            : match.RewardVersion == 5 ? LocalEconomy.RewardV5(result, turns, conceded, rollSeed)
            : LocalEconomy.RewardV6(result, turns, conceded, rollSeed);
        var tier = GrindTiers.Find(match.GrindTier);
        return new LocalReward { Gold = tier.Gold(reward.Gold), BonusGold = reward.BonusGold, PackReward = reward.PackReward, Dust = tier.Dust(reward.Dust) };
    }

    private static void ValidateTerminal(LocalMatchRecord match)
    {
        if ((match.Result != "Won" && match.Result != "Lost" && match.Result != "Tied" && match.Result != "Abandoned")
            || match.Turns < 0 || match.Turns > MaximumTurns || (match.Conceded && match.Result != "Lost"))
            throw new InvalidOperationException("Invalid settled match.");
    }

    private static void ValidatePack(LocalPackResult pack, IReadOnlyDictionary<string, SabberStoneCore.Model.Card> catalog, int profileVersion)
    {
        if (pack == null) return;
        if (profileVersion >= 10 && pack.PackType != FairPackRules.Legacy) {
            ValidateFairReceipt(pack); return;
        }
        int poolVersion = pack.PoolVersion;
        if (poolVersion == 0 && profileVersion <= 4) poolVersion = 1;
        IReadOnlyList<SabberStoneCore.Model.Card> pool;
        try { pool = LocalCardCatalog.PackPool(poolVersion); }
        catch (ArgumentOutOfRangeException error) { throw new InvalidOperationException("Invalid local pack pool version.", error); }
        var poolIds = new HashSet<string>(pool.Select(card => card.Id), StringComparer.Ordinal);
        if (pack.Cards == null || pack.Cards.Count != 5) throw new InvalidOperationException("Invalid local pack receipt.");
        foreach (var card in pack.Cards)
            if (card == null || !catalog.TryGetValue(card.Id ?? "", out var known)
                || !poolIds.Contains(card.Id) || known.Rarity == SabberStoneCore.Enums.Rarity.FREE
                || card.Premium != 0 || (card.DuplicateGold != 0 && card.DuplicateGold != 10))
                throw new InvalidOperationException("Invalid local pack card.");
    }

    // A pending mass opening: undo its packs last to first, each proven like a single pending fair pack against the state
    // the later packs leave behind (collection, counters, sequence).
    private static void ValidatePendingBatch(LocalProfileData profile)
    {
        var batch = profile.PackBatch;
        string type = profile.LastPack.PackType;
        if (profile.Version < 15 || batch.Count < 2 || batch.Count > MassPackLimit || type == FairPackRules.Legacy
            || batch.Any(pack => pack == null || pack.PackType != type || pack.Cards == null) || PackKey(batch[batch.Count - 1]) != PackKey(profile.LastPack)
            || profile.PackSequence < batch.Count)
            throw new InvalidOperationException("Invalid pending mass pack receipt.");
        var state = new LocalProfileData {
            PackSeed = profile.PackSeed, PackSequence = profile.PackSequence,
            Collection = profile.Collection.Select(Copy).ToList(), PremiumCollection = profile.PremiumCollection.Select(Copy).ToList(),
            PackCounters = profile.PackCounters.Select(Copy).ToList()
        };
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            state.LastPack = batch[i];
            var before = ValidateFairPending(state);
            state.Collection = before.Collection; state.PremiumCollection = before.PremiumCollection;
            state.PackCounters.Single(row => row.PackType == type).Counters = batch[i].CountersBefore.Copy();
            state.PackSequence--;
        }
    }
    private static string PackKey(LocalPackResult pack) => pack.PackType + "|" + pack.PackRuleVersion + "|" + pack.PoolVersion + "|"
        + string.Join(",", pack.Cards.Select(card => card.Key + ":" + card.DuplicateGold + ":" + card.AcquiredBefore)) + "|"
        + (pack.CountersBefore == null ? "" : pack.CountersBefore.PacksOpened + "," + pack.CountersBefore.PacksSinceLegendary + ","
            + pack.CountersBefore.PacksSinceEpic + "," + pack.CountersBefore.FirstLegendaryReceived);

    // Returns the collection as it stood before the pending pack.
    private static LocalProfileData ValidateFairPending(LocalProfileData profile)
    {
        var pack = profile.LastPack;
        if (profile.PackSequence < 1) throw new InvalidOperationException("Invalid pending pack sequence.");
        var before = new LocalProfileData { Collection = profile.Collection.Select(Copy).ToList(),
            PremiumCollection = profile.PremiumCollection.Select(Copy).ToList() };
        foreach (var group in pack.Cards.GroupBy(c => c.Key)) {
            var drawn = group.First();
            int acquiredBefore = drawn.AcquiredBefore;
            var card = (drawn.Premium == 0 ? before.Collection : before.PremiumCollection)
                .SingleOrDefault(row => row.Id == drawn.Id && row.Premium == drawn.Premium);
            if (card == null || card.Count < group.Count()
                || acquiredBefore < Math.Min(card.Count - group.Count(), 1)
                || !LocalEconomy.HistoricAcquired(card.Acquired, acquiredBefore + group.Count(), group.Count()))
                throw new InvalidOperationException("Invalid pending acquisition delta.");
            card.Count -= group.Count();
            card.Acquired = acquiredBefore;
        }
        var acquired = (pack.PackRuleVersion == 1 ? before.Collection : before.Collection.Concat(before.PremiumCollection))
            .GroupBy(card => card.Id).ToDictionary(group => group.Key, group => group.Sum(card => card.Acquired));
        var expected = FairPackRules.Generate(pack.PackType, pack.PackRuleVersion, (ulong)profile.PackSeed,
            profile.PackSequence - 1, pack.CountersBefore.Copy(), id => acquired.TryGetValue(id, out int count) ? count : 0);
        var current = profile.PackCounters.SingleOrDefault(p => p.PackType == pack.PackType);
        if (!expected.CardIds.SequenceEqual(pack.Cards.Select(c => c.Id)) || !expected.Premiums.SequenceEqual(pack.Cards.Select(c => c.Premium))
            || current == null || !SameCounters(current.Counters, expected.After))
            throw new InvalidOperationException("Invalid pending fair pack provenance.");
        return before;
    }

    private static LocalProfileData MigrateV9(LocalProfileData old) {
        var next = Clone(old);
        next.Version = 10;
        next.UnopenedPacks = new List<LocalPackCount>();
        if (old.LegacyUnopenedPacks > 0) AddPacks(next, FairPackRules.Legacy, old.LegacyUnopenedPacks);
        next.LegacyUnopenedPacks = 0;
        next.PackCounters = new[] { FairPackRules.Legacy }.Concat(FairPackRules.PackTypes)
            .Select(type => new LocalPackProgress { PackType = type }).ToList();
        foreach (var card in next.Collection) card.Acquired = Math.Min(card.Count, Playset(card.Id));
        foreach (var match in next.Matches) match.Packs = match.PackReward
            ? new List<LocalPackCount> { new LocalPackCount { PackType = FairPackRules.Legacy, Count = 1 } } : new List<LocalPackCount>();
        if (next.LastPack != null) {
            next.LastPack.PackType = FairPackRules.Legacy; next.LastPack.PackRuleVersion = 0;
            next.LastPack.CountersBefore = new FairPackCounters();
            foreach (var card in next.LastPack.Cards) card.AcquiredBefore = Math.Max(0, AcquiredCount(next, card.Id)
                - next.LastPack.Cards.Count(c => c.Id == card.Id && c.DuplicateGold == 0));
        }
        int stars = next.Matches.Aggregate(0, (value, match) => NextStars(value, match.Result));
        next.LadderState = new LocalLadderState { StarIndex = stars, BestStarIndex = stars };
        next.CardTokens = new LocalCardTokens(); next.DeckKits = new List<LocalDeckKit>(); next.Trophies = new List<LocalTrophy>();
        next.LegendDeck = null; next.MilestoneCatchUp = null;
        foreach (var match in next.Matches.Concat(next.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { next.Pending })) {
            match.DustReward = 0; match.Ladder = null; match.ClassXpBefore = null; match.ClassXpAfter = null;
            match.UnlockedCardIds = null; match.ProgressionRuleVersion = 0; match.TierRewards = null;
            match.ClassRewardGrants = null; match.PortraitGrants = null;
        }
        foreach (var deck in next.CustomDecks) { deck.SourceRecipeId = null; ApplyCosmetics(deck, new LocalDeckCosmetics()); }
        next.LadderRuleVersion = 0; next.DustBalance = 0; next.CraftSequence = 0; next.LastCraft = null;
        next.PurchaseSequence = 0; next.Purchases = new List<LocalPurchaseReceipt>(); next.OwnedRecipes = new List<string>();
        next.OwnedCoins = new List<int> { 1 }; next.FavoriteCoins = new List<int> { 1 };
        next.OwnedCardBacks = new List<int> { 0 }; next.OwnedHeroSkins = new List<string>(); next.FavoriteCardBacks = new List<int> { 0 };
        next.FavoriteHeroSkins = new List<LocalHeroFavorite>();
        return next;
    }

    private static bool ValidName(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool ValidNpcFields(string npcId, string npcDeckId, double? npcRating) =>
        (npcId == null || ValidName(npcId, 128)) && (npcDeckId == null || ValidName(npcDeckId, 64))
        && (!npcRating.HasValue || (!double.IsNaN(npcRating.Value) && !double.IsInfinity(npcRating.Value)));

    private static bool ValidDate(string value, bool utc = false) => value != null && value.Length <= 64
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
        && (!utc || date.Offset == TimeSpan.Zero);

    private static bool NativeCustomId(long id) => id == LegacyCustomDeckId || id >= FirstAllocatedCustomDeckId;

    private static void ValidateOwnership(IEnumerable<string> ids, IEnumerable<LocalCardCount> collectionEntries)
    {
        var deck = ids.ToArray();
        var collection = LocalDecks.WithDeckCompanions(CollectionMap(collectionEntries), deck);
        if (deck.GroupBy(id => id).Any(group => !collection.TryGetValue(group.Key, out int owned) || group.Count() > owned))
            throw new InvalidOperationException("The local collection does not own this deck.");
    }

    private static void ValidateSideboardOwnership(IEnumerable<LocalSideboard> sideboards, IReadOnlyDictionary<string, int> owned)
    {
        foreach (var row in sideboards ?? Enumerable.Empty<LocalSideboard>())
            if (row.Cards.GroupBy(id => id).Any(group => !owned.TryGetValue(group.Key, out int count) || count < group.Count()))
                throw new InvalidOperationException("Sideboard selection is not owned.");
    }

    private static LocalRewardFacts DefaultFacts() => new LocalRewardFacts();
    private static bool Same(LocalRewardFacts left, LocalRewardFacts right) => left != null && right != null
        && left.DistinctCardsPlayed == right.DistinctCardsPlayed
        && left.EnemyMinionsDefeated == right.EnemyMinionsDefeated
        && left.MinimumHealth == right.MinimumHealth;

    private static LocalRewardFacts Copy(LocalRewardFacts facts) => facts == null ? null : new LocalRewardFacts {
        DistinctCardsPlayed = facts.DistinctCardsPlayed,
        EnemyMinionsDefeated = facts.EnemyMinionsDefeated,
        MinimumHealth = facts.MinimumHealth
    };

    private static LocalCraftReceipt Copy(LocalCraftReceipt receipt) => receipt == null ? null : new LocalCraftReceipt {
        Sequence = receipt.Sequence, Op = receipt.Op, Cards = receipt.Cards?.Select(Copy).ToList(),
        DustDelta = receipt.DustDelta, TokenRarity = receipt.TokenRarity, TokenDelta = receipt.TokenDelta, Undoable = receipt.Undoable
    };
    private static LocalCraftCard Copy(LocalCraftCard card) => card == null ? null : new LocalCraftCard {
        CardId = card.CardId, CountDelta = card.CountDelta, AcquiredBefore = card.AcquiredBefore, Premium = card.Premium
    };
    private static LocalLadderState Copy(LocalLadderState state) => state == null ? null : new LocalLadderState {
        StarIndex = state.StarIndex, Multiplier = state.Multiplier, StreakWins = state.StreakWins, HiddenRating = state.HiddenRating,
        PlacementGames = state.PlacementGames, BestStarIndex = state.BestStarIndex, LegendReached = state.LegendReached,
        FloorsRewarded = state.FloorsRewarded?.ToList()
    };
    private static LocalCardTokens Copy(LocalCardTokens tokens) => tokens == null ? null : new LocalCardTokens {
        Common = tokens.Common, Rare = tokens.Rare, Epic = tokens.Epic, Legendary = tokens.Legendary
    };
    private static LocalWelcome Copy(LocalWelcome welcome) => welcome == null ? null : new LocalWelcome {
        Version = welcome.Version, PackType = welcome.PackType, Packs = welcome.Packs, Gold = welcome.Gold, Dust = welcome.Dust,
        KitId = welcome.KitId, KitGoldCap = welcome.KitGoldCap, Seen = welcome.Seen, Cards = welcome.Cards?.Select(Copy).ToList() };
    private static LocalOwnedCardGrant Copy(LocalOwnedCardGrant grant) => grant == null ? null : new LocalOwnedCardGrant {
        CardId = grant.CardId, Premium = grant.Premium, Count = grant.Count, OwnedBefore = grant.OwnedBefore };
    private static LocalDeckKit Copy(LocalDeckKit kit) => kit == null ? null : new LocalDeckKit {
        Id = kit.Id, MilestoneId = kit.MilestoneId, GoldCap = kit.GoldCap, RedeemedPurchaseSequence = kit.RedeemedPurchaseSequence
    };
    private static LocalTrophy Copy(LocalTrophy trophy) => trophy == null ? null : new LocalTrophy {
        Id = trophy.Id, MilestoneId = trophy.MilestoneId, MatchId = trophy.MatchId, Utc = trophy.Utc
    };
    private static LocalLegendDeck Copy(LocalLegendDeck deck) => deck == null ? null : new LocalLegendDeck {
        MatchId = deck.MatchId, DeckName = deck.DeckName, Hero = deck.Hero, Cards = deck.Cards?.ToList(),
        Sideboards = deck.Sideboards?.Select(Copy).ToList(), Legacy = deck.Legacy
    };
    private static LocalSideboard Copy(LocalSideboard sideboard) => sideboard == null ? null : new LocalSideboard {
        Source = sideboard.Source, CardEntries = sideboard.CardEntries?.Select(Copy).ToList()
    };
    private static LocalMilestoneCatchUp Copy(LocalMilestoneCatchUp catchUp) => catchUp == null ? null : new LocalMilestoneCatchUp {
        Id = catchUp.Id, TierRewards = catchUp.TierRewards?.Select(Copy).ToList(), Acknowledged = catchUp.Acknowledged
    };
    private static LocalTierReward Copy(LocalTierReward reward) => reward == null ? null : new LocalTierReward {
        MilestoneId = reward.MilestoneId, FirstTime = reward.FirstTime, OpponentNpcId = reward.OpponentNpcId,
        OpponentName = reward.OpponentName, Grants = reward.Grants?.Select(Copy).ToList()
    };
    private static LocalTierGrant Copy(LocalTierGrant grant) => grant == null ? null : new LocalTierGrant {
        Kind = grant.Kind, Ref = grant.Ref, Count = grant.Count
    };
    private static LocalPurchaseReceipt Copy(LocalPurchaseReceipt receipt) => receipt == null ? null : new LocalPurchaseReceipt {
        Sequence = receipt.Sequence, RequestId = receipt.RequestId, CatalogVersion = receipt.CatalogVersion,
        ProductId = receipt.ProductId, Kind = receipt.Kind, Quantity = receipt.Quantity, GoldPaid = receipt.GoldPaid,
        KitId = receipt.KitId, PriceBeforeKit = receipt.PriceBeforeKit, Granted = Copy(receipt.Granted)
    };
    private static LocalPurchaseGrants Copy(LocalPurchaseGrants grants) => grants == null ? null : new LocalPurchaseGrants {
        Packs = grants.Packs?.Select(Copy).ToList(), Cards = grants.Cards?.Select(Copy).ToList(),
        RecipeId = grants.RecipeId, DeckId = grants.DeckId, CardBack = grants.CardBack, Coin = grants.Coin, HeroSkin = grants.HeroSkin,
        HubTheme = grants.HubTheme, Board = grants.Board, Pet = grants.Pet
    };
    private static LocalPurchaseCard Copy(LocalPurchaseCard card) => card == null ? null : new LocalPurchaseCard {
        CardId = card.CardId, CountBefore = card.CountBefore, CountAfter = card.CountAfter, AcquiredBefore = card.AcquiredBefore
    };
    private static LocalHeroFavorite Copy(LocalHeroFavorite favorite) => favorite == null ? null : new LocalHeroFavorite {
        Class = favorite.Class, HeroCardId = favorite.HeroCardId
    };
    private static LocalMatchRecord Copy(LocalMatchRecord match) => match == null ? null : new LocalMatchRecord {
        Id = match.Id, Deck = match.Deck, DeckHero = match.DeckHero, Opponent = match.Opponent, Seed = match.Seed, FirstPlayer = match.FirstPlayer, StartedUtc = match.StartedUtc, SettledUtc = match.SettledUtc,
        Result = match.Result, Turns = match.Turns, Conceded = match.Conceded, Gold = match.Gold,
        RewardVersion = match.RewardVersion, GrindTier = match.GrindTier, BonusGold = match.BonusGold, PackReward = match.PackReward,
        DustReward = match.DustReward, Ladder = Copy(match.Ladder), ClassXpBefore = match.ClassXpBefore, ClassXpAfter = match.ClassXpAfter,
        UnlockedCardIds = match.UnlockedCardIds?.ToList(), ProgressionRuleVersion = match.ProgressionRuleVersion,
        MasteryGrants = match.MasteryGrants?.Select(LocalCardMastery.Clone).ToList(),
        ClassRewardGrants = match.ClassRewardGrants?.Select(Copy).ToList(), PortraitGrants = match.PortraitGrants?.Select(Copy).ToList(),
        TierRewards = match.TierRewards?.Select(Copy).ToList(), Packs = match.Packs?.Select(Copy).ToList(),
        RewardFacts = Copy(match.RewardFacts), Unstarted = match.Unstarted, DebugScenario = match.DebugScenario,
        CosmeticCoin = match.CosmeticCoin, CosmeticCoinCardId = match.CosmeticCoinCardId,
        Pet = match.Pet, PetXpBefore = match.PetXpBefore, PetXpAfter = match.PetXpAfter, PetRules = match.PetRules,
        PetSmall = match.PetSmall, PetMedium = match.PetMedium, PetLarge = match.PetLarge, PetPacingPercent = match.PetPacingPercent,
        NpcId = match.NpcId, NpcDeckId = match.NpcDeckId, NpcRating = match.NpcRating, NpcConceded = match.NpcConceded,
        RivalGold = match.RivalGold,
        AchievementContext = Copy(match.AchievementContext), AchievementEvidenceFingerprint = match.AchievementEvidenceFingerprint,
        AchievementGrants = match.AchievementGrants?.Select(Copy).ToList(), Mode = match.Mode,
        Casual = match.Casual == null ? null : new LocalCasualReceipt { RatingBefore = match.Casual.RatingBefore,
            RatingAfter = match.Casual.RatingAfter, OpponentRating = match.Casual.OpponentRating }
    };

    private static LocalDeckCard Copy(LocalDeckCard card) => card == null ? null : new LocalDeckCard { Id = card.Id, Premium = card.Premium };
    private static LocalClassRewardGrant Copy(LocalClassRewardGrant row) => row == null ? null : new LocalClassRewardGrant {
        Hero = row.Hero, Level = row.Level, CardId = row.CardId, Premium = row.Premium, Copies = row.Copies, ScheduleVersion = row.ScheduleVersion,
        ScheduledCardId = row.ScheduledCardId, Dust = row.Dust, GrantedUtc = row.GrantedUtc
    };
    private static LocalPortraitGrant Copy(LocalPortraitGrant row) => row == null ? null : new LocalPortraitGrant {
        Hero = row.Hero, Kind = row.Kind, CardId = row.CardId, GrantedUtc = row.GrantedUtc
    };
    private static LocalCardSeen Copy(LocalCardSeen entry) => new LocalCardSeen { Id = entry.Id, Premium = entry.Premium, Seen = entry.Seen };
    private static LocalCardCount Copy(LocalCardCount entry) => new LocalCardCount { Id = entry.Id, Premium = entry.Premium, Count = entry.Count, Acquired = entry.Acquired };
    private static LocalCustomDeck Copy(LocalCustomDeck deck) => deck == null ? null : new LocalCustomDeck {
        Id = deck.Id, SortOrder = deck.SortOrder, Name = deck.Name, Hero = deck.Hero, SourceRecipeId = deck.SourceRecipeId,
        Coin = deck.Coin, RandomCoinUseFavorite = deck.RandomCoinUseFavorite,
        CardBack = deck.CardBack, RandomCardBack = deck.RandomCardBack, HeroSkin = deck.HeroSkin, RandomHeroUseFavorite = deck.RandomHeroUseFavorite,
        PetFamily = deck.PetFamily, PetForm = deck.PetForm, RandomPetUseFavorite = deck.RandomPetUseFavorite,
        CardEntries = deck.CardEntries?.Select(Copy).ToList(),
        Sideboards = deck.Sideboards?.Select(row => new LocalSideboard { Source = row.Source, CardEntries = row.CardEntries?.Select(Copy).ToList() }).ToList()
    };
    private static LocalPackResult Copy(LocalPackResult pack) => pack == null ? null : new LocalPackResult {
        PoolVersion = pack.PoolVersion, PackType = pack.PackType, PackRuleVersion = pack.PackRuleVersion, CountersBefore = pack.CountersBefore?.Copy(),
        Cards = pack.Cards == null ? null : pack.Cards.Select(card => new LocalPackCard { Id = card.Id, Premium = card.Premium, DuplicateGold = card.DuplicateGold, AcquiredBefore = card.AcquiredBefore }).ToList()
    };
    private static LocalPackResult CopyLegacyPack(LocalPackResult pack)
    {
        LocalPackResult copy = Copy(pack);
        if (copy != null) copy.PoolVersion = 1;
        return copy;
    }
    private static Dictionary<string, int> CollectionMap(IEnumerable<LocalCardCount> collection) =>
        collection.ToDictionary(entry => entry.Id, entry => entry.Count, StringComparer.Ordinal);
    private static Dictionary<string, int> CollectionMap(IEnumerable<KeyValuePair<string, int>> collection) =>
        collection.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    private static List<LocalCardCount> CollectionList(IEnumerable<KeyValuePair<string, int>> collection) => collection
        .OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new LocalCardCount { Id = entry.Key, Count = entry.Value }).ToList();

    internal string SavedHash()
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
    }

    /// <summary>Length and write/creation times of the saved file; changes when a commit replaces it. Null when it cannot be read.</summary>
    internal string SavedFileIdentity(out DateTime lastWriteUtc)
    {
        lastWriteUtc = DateTime.MinValue;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            lastWriteUtc = info.LastWriteTimeUtc;
            return info.Length + "|" + lastWriteUtc.Ticks + "|" + info.CreationTimeUtc.Ticks;
        }
        catch (IOException) { return null; }
    }

    // Non-copying status queries (the 3-second status routine): History/Pending/CustomDecks/Collection deep-copy or rebuild their data per call.
    internal int MatchCount => data.Matches.Count;
    internal string PendingMatchId => data.Pending?.Id;
    internal void OwnedCollectionTotals(out int distinct, out int total)
    {
        var owned = OwnedCollection(data);
        distinct = owned.Count; total = owned.Values.Sum();
    }
    internal sealed class DeckSummary { internal long Id; internal string Name; internal int CardCount; internal int DeckSize; }
    internal IReadOnlyList<DeckSummary> VisibleDeckSummaries() => VisibleDrafts(data)
        .Select(deck => new DeckSummary { Id = deck.Id, Name = deck.Name, CardCount = deck.Cards.Count, DeckSize = LocalDecks.DeckSize(deck.Cards) }).ToArray();

    private void Backup(string reason)
    {
        if (backups) LocalProfileBackups.Automatic(path, reason, data.Version);
    }

    // --- Collection seen state ("NEW" markers and class-tab counts) ---
    private static string SeenKey(string id, int premium) => premium.ToString(CultureInfo.InvariantCulture) + ":" + id;

    // The versions the native collection shows: Normal ownership (collection and Core grants) and the
    // available premium copies, exactly as OwnedCards projects them. Cosmetic hero rows are always seen.
    private static Dictionary<string, int> SeenOwned(LocalProfileData profile)
    {
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in OwnedCollection(profile)) if (pair.Value > 0) owned[SeenKey(pair.Key, 0)] = pair.Value;
        foreach (var card in profile.PremiumCollection.Where(card => card != null && card.Count > 0 && LocalClassRewards.IsAvailable(card.Id)))
        {
            string key = SeenKey(card.Id, card.Premium);
            owned[key] = checked((owned.TryGetValue(key, out int count) ? count : 0) + card.Count);
        }
        return owned;
    }

    // Copies a commit adds start unseen; seen copies never exceed ownership, and fully seen versions keep no row.
    // previous == null is a load-time commit (migration, startup grants): nothing it holds becomes NEW.
    internal static void TrackSeen(LocalProfileData previous, LocalProfileData next)
    {
        var grants = next.SeenGrants;
        next.SeenGrants = null;
        var before = previous == null ? null : SeenOwned(previous);
        var rows = (next.UnseenCards ?? new List<LocalCardSeen>()).Where(row => row != null && row.Id != null)
            .GroupBy(row => SeenKey(row.Id, row.Premium), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(row => row.Seen), StringComparer.Ordinal);
        var unseen = new List<LocalCardSeen>();
        foreach (var pair in SeenOwned(next).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            long seen = rows.TryGetValue(pair.Key, out int row) ? row
                : before == null ? pair.Value : before.TryGetValue(pair.Key, out int owned) ? owned : 0;
            if (grants != null && grants.TryGetValue(pair.Key, out int granted)) seen += granted;
            if (seen >= pair.Value) continue;
            int split = pair.Key.IndexOf(':');
            unseen.Add(new LocalCardSeen {
                Premium = int.Parse(pair.Key.Substring(0, split), CultureInfo.InvariantCulture),
                Id = pair.Key.Substring(split + 1), Seen = (int)Math.Max(0, seen)
            });
        }
        next.UnseenCards = unseen;
    }

    // Structural checks only: a row for a version no longer owned (or already fully seen) reads as seen and is dropped
    // by the next commit, so a stale row never blocks a load.
    private static void ValidateSeen(LocalProfileData profile)
    {
        var rows = profile.UnseenCards ?? throw new InvalidOperationException("Invalid collection seen state.");
        if (rows.Count > MaximumRecords || rows.Any(row => row == null || string.IsNullOrEmpty(row.Id)
                || row.Premium < 0 || row.Premium > 3 || row.Seen < 0)
            || rows.Select(row => SeenKey(row.Id, row.Premium)).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            throw new InvalidOperationException("Invalid collection seen state.");
    }

    internal static void SeeOnAcquire(LocalProfileData next, string id, int premium, int copies)
    {
        if (copies <= 0 || id == null) return;
        if (next.SeenGrants == null) next.SeenGrants = new Dictionary<string, int>(StringComparer.Ordinal);
        string key = SeenKey(id, premium);
        next.SeenGrants[key] = (int)Math.Min(int.MaxValue, (long)(next.SeenGrants.TryGetValue(key, out int seen) ? seen : 0) + copies);
    }

    /// <summary>Copies of one version the player has seen in the Collection (native CardStack.NumSeen, clamped to the
    /// owned count by the caller); int.MaxValue when every copy is seen.</summary>
    public int SeenCopies(string id, int premium) =>
        pendingSeen.Contains(SeenKey(id, premium)) ? int.MaxValue
            : data.UnseenCards.FirstOrDefault(card => card.Id == id && card.Premium == premium)?.Seen ?? int.MaxValue;

    public IReadOnlyList<LocalCardSeen> UnseenCards =>
        data.UnseenCards.Where(row => !pendingSeen.Contains(SeenKey(row.Id, row.Premium))).Select(Copy).ToArray();

    // Viewed versions not yet on disk. Sweeping a page of NEW cards would otherwise run one full synchronous commit
    // per card; they apply in memory at once and reach disk in one commit (FlushSeen, or any other commit).
    private readonly HashSet<string> pendingSeen = new HashSet<string>(StringComparer.Ordinal);
    public int PendingSeenCount => pendingSeen.Count;

    /// <summary>The player viewed every copy of this version. Applies immediately; durable at the next commit or
    /// <see cref="FlushSeen"/>, so a crash before then only brings the NEW marker back.</summary>
    public bool MarkSeen(string id, int premium)
    {
        string key = SeenKey(id, premium);
        if (pendingSeen.Contains(key) || !data.UnseenCards.Any(card => card.Id == id && card.Premium == premium)) return false;
        pendingSeen.Add(key);
        return true;
    }

    /// <summary>Writes the queued viewed versions in one commit; false when none were pending.</summary>
    public bool FlushSeen()
    {
        if (pendingSeen.Count == 0) return false;
        return CommitChange(next => true);
    }

    internal static LocalProfileData ReadFile(string path)
    {
        if (new FileInfo(path).Length > MaximumProfileBytes)
            throw new InvalidOperationException("Local profile exceeds the size limit.");
        using (var reader = XmlReader.Create(path, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumProfileBytes }))
            return (LocalProfileData)Serializer.Deserialize(reader, new XmlDeserializationEvents {
                OnUnknownElement = (sender, e) => {
                    // XmlSerializer otherwise ignores a repeated singleton after reading its first value.
                    if (e.ObjectBeingDeserialized is LocalProfileData && e.Element.LocalName == "LadderCalibration")
                        throw new InvalidOperationException("Duplicate ladder calibration.");
                }
            });
    }

    internal static void ValidateFile(string path)
    {
        using (var profile = new LocalProfile(path, false, false)) { }
    }

    internal static void ValidateFile(string path, ProfileOptions options)
    {
        using (var profile = new LocalProfile(path, false, false, null, options)) { }
    }

    public void Dispose()
    {
        try { FlushSeen(); } catch { } // Best effort: a lost write only brings NEW markers back.
        lease.Dispose();
    }
}

internal static class LocalProfileBackups
{
    internal sealed class Entry
    {
        internal string Id;
        internal long Seq;
        internal DateTime CreatedUtc;
        internal string Reason;
        internal int ProfileVersion;
        internal long Bytes;
        internal string Sha256;
    }

    internal static Action<string> FaultHook = null;
    internal static Action<string> Log = null;
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;
    internal static Exception LastError { get; private set; }
    private static readonly Regex IdPattern = new Regex(@"\A[0-9]{8}T[0-9]{9}Z-(startup|match|abandon|pack|purchase|pre-restore)-[0-9a-f]{8}\z");
    private static readonly Regex HashPattern = new Regex(@"\A[0-9a-f]{64}\z");
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    private static string DirectoryFor(string profilePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath)), "backups");

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse backup/restore path rejected: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void ValidateId(string id)
    {
        DateTime timestamp;
        if (id == null || !IdPattern.IsMatch(id) || !DateTime.TryParseExact(id.Substring(0, 19),
            "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp))
            throw new InvalidOperationException("Invalid backup ID.");
    }

    internal static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static Entry ReadEntry(string directory, string id)
    {
        ValidateId(id);
        string manifest = Path.Combine(directory, id + ".manifest"), file = Path.Combine(directory, id + ".xml");
        RejectLink(manifest);
        RejectLink(file);
        if (!File.Exists(manifest) || !File.Exists(file) || new FileInfo(manifest).Length > 4096)
            throw new InvalidOperationException("Backup data or manifest is missing or invalid.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(manifest, Utf8))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0 || fields.ContainsKey(line.Substring(0, equals)))
                throw new InvalidOperationException("Invalid backup manifest.");
            fields.Add(line.Substring(0, equals), line.Substring(equals + 1));
        }
        string[] keys = { "format", "seq", "id", "created_utc", "reason", "profile_version", "bytes", "sha256" };
        DateTime created;
        int version;
        long length, seq;
        if (fields.Count != keys.Length || keys.Any(key => !fields.ContainsKey(key))
            || fields["format"] != "1" || fields["id"] != id
            || !long.TryParse(fields["seq"], NumberStyles.None, CultureInfo.InvariantCulture, out seq) || seq < 1
            || !DateTime.TryParseExact(fields["created_utc"], "o", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out created) || created.Kind != DateTimeKind.Utc
            || created.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) != id.Substring(0, 19)
            || id != id.Substring(0, 20) + fields["reason"] + "-" + id.Substring(id.Length - 8)
            || !int.TryParse(fields["profile_version"], NumberStyles.None, CultureInfo.InvariantCulture, out version)
            || !long.TryParse(fields["bytes"], NumberStyles.None, CultureInfo.InvariantCulture, out length)
            || !HashPattern.IsMatch(fields["sha256"]) || new FileInfo(file).Length != length
            || Hash(File.ReadAllBytes(file)) != fields["sha256"])
            throw new InvalidOperationException("Backup manifest, size or SHA-256 verification failed.");
        return new Entry { Id = id, Seq = seq, CreatedUtc = created, Reason = fields["reason"], ProfileVersion = version,
            Bytes = length, Sha256 = fields["sha256"] };
    }

    internal static IReadOnlyList<Entry> List(string profilePath)
    {
        string directory = DirectoryFor(profilePath);
        RejectLink(directory);
        var valid = new List<Entry>();
        if (!Directory.Exists(directory)) return valid;
        foreach (string manifest in Directory.GetFiles(directory, "*.manifest"))
        {
            try { valid.Add(ReadEntry(directory, Path.GetFileNameWithoutExtension(manifest))); }
            catch (Exception) { /* Invalid pairs are never restore candidates. */ }
        }
        return valid.OrderByDescending(entry => entry.Seq).ThenByDescending(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    internal static void Automatic(string profilePath, string reason, int version)
    {
        SaveHold.Require("backing up a save");
        LastError = null;
        try { Create(profilePath, File.ReadAllBytes(profilePath), reason, version); }
        catch (Exception error) { LastError = error; Report("Automatic backup failed: " + error.Message); }
    }

    private static void Report(string message)
    {
        try { Log?.Invoke(message); } catch { }
    }

    private static void DeleteFile(string path)
    {
        FaultHook?.Invoke("delete:" + path);
        File.Delete(path);
    }

    private static bool DeleteBestEffort(string path)
    {
        try { DeleteFile(path); return true; }
        catch (Exception error) { LastError = error; Report("Cleanup delete failed: " + path + ": " + error.Message); return false; }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            RejectLink(path);
            foreach (string file in Directory.GetFiles(path)) DeleteBestEffort(file);
            foreach (string directory in Directory.GetDirectories(path)) DeleteDirectoryBestEffort(directory);
            Directory.Delete(path);
        }
        catch (Exception error) { LastError = error; Report("Cleanup delete failed: " + path + ": " + error.Message); }
    }

    private static void WriteVerified(string path, byte[] bytes, bool createNew)
    {
        RejectLink(path);
        using (var stream = new FileStream(path, createNew ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        if (Hash(File.ReadAllBytes(path)) != Hash(bytes))
            throw new IOException("Backup/restore temporary file verification failed.");
    }

    internal static string Create(string profilePath, byte[] bytes, string reason, int version, ISet<string> protectedIds = null)
    {
        DateTime now = UtcNow().ToUniversalTime();
        string id = now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + reason + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        ValidateId(id);
        if (version < 0) throw new InvalidOperationException("Invalid backup profile version.");
        string directory = DirectoryFor(profilePath);
        RejectLink(directory);
        Directory.CreateDirectory(directory);
        var valid = List(profilePath);
        Cleanup(directory);
        string hash = Hash(bytes);
        if (reason != "pre-restore" && valid.Count != 0 && valid[0].Sha256 == hash) return valid[0].Id;
        long seq = valid.Count == 0 ? 0 : valid[0].Seq;
        foreach (string existing in Directory.GetFiles(directory, "*.manifest"))
        {
            try
            {
                RejectLink(existing);
                foreach (string line in File.ReadLines(existing, Utf8))
                    if (line.StartsWith("seq=", StringComparison.Ordinal)
                        && long.TryParse(line.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > seq)
                        seq = parsed;
            }
            catch (Exception) { /* Reserve readable sequences even when the pair cannot be verified. */ }
        }
        seq = checked(seq + 1);
        string file = Path.Combine(directory, id + ".xml"), manifest = Path.Combine(directory, id + ".manifest");
        WriteVerified(file + ".tmp", bytes, true);
        FaultHook?.Invoke("data-written");
        File.Move(file + ".tmp", file);
        FaultHook?.Invoke("data-renamed");
        byte[] metadata = Utf8.GetBytes("format=1\nseq=" + seq.ToString(CultureInfo.InvariantCulture) + "\nid=" + id + "\ncreated_utc=" + now.ToString("o", CultureInfo.InvariantCulture)
            + "\nreason=" + reason + "\nprofile_version=" + version.ToString(CultureInfo.InvariantCulture)
            + "\nbytes=" + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + "\nsha256=" + hash + "\n");
        WriteVerified(manifest + ".tmp", metadata, true);
        FaultHook?.Invoke("manifest-written");
        File.Move(manifest + ".tmp", manifest);
        Rotate(profilePath, id, protectedIds);
        return id;
    }

    private static void Cleanup(string directory)
    {
        foreach (string temporary in Directory.GetFiles(directory, "*.tmp")) DeleteBestEffort(temporary);
        foreach (string staging in Directory.GetDirectories(directory, "restore-staging-*")) DeleteDirectoryBestEffort(staging);
        var manifests = new HashSet<string>(Directory.GetFiles(directory, "*.manifest").Select(Path.GetFileNameWithoutExtension), StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(directory, "*.xml"))
            if (!manifests.Contains(Path.GetFileNameWithoutExtension(file))) DeleteBestEffort(file);
    }

    private static void Rotate(string profilePath, string written, ISet<string> protectedIds)
    {
        var valid = List(profilePath);
        var keep = new HashSet<string>(valid.Take(10).Select(entry => entry.Id), StringComparer.Ordinal) { written };
        if (protectedIds != null) keep.UnionWith(protectedIds);
        // Daily retention uses the seven most recent UTC days containing valid backups, without an age cutoff.
        foreach (var daily in valid.GroupBy(entry => entry.CreatedUtc.Date).OrderByDescending(group => group.Key).Take(7))
            keep.Add(daily.First().Id);
        foreach (var entry in valid.Where(entry => entry.Reason == "pre-restore").Take(3)) keep.Add(entry.Id);
        string directory = DirectoryFor(profilePath);
        foreach (var entry in valid.Where(entry => !keep.Contains(entry.Id)))
        {
            // Keep the manifest if data could not be removed, so cleanup cannot mistake it for an orphan.
            if (DeleteBestEffort(Path.Combine(directory, entry.Id + ".xml")))
                DeleteBestEffort(Path.Combine(directory, entry.Id + ".manifest"));
        }
        Cleanup(directory);
    }

    internal static string Restore(string profilePath, string backupId, string expectedHash = null, DateTime? requestedUtc = null,
        ProfileOptions options = null)
    {
        string before = null;
        bool replacing = false, replaced = false;
        try
        {
            profilePath = Path.GetFullPath(profilePath);
            options = options ?? ProfileCatalog.OptionsBeside(profilePath);
            string directory = DirectoryFor(profilePath);
            RejectLink(directory);
            RejectLink(profilePath);
            RejectLink(profilePath + ".lock");
            FileStream lease;
            try { lease = new FileStream(profilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("Close the game before restoring; the profile lock is unavailable.", error); }
            using (lease)
            {
                bool exists = File.Exists(profilePath);
                if (expectedHash != null && (!exists || Hash(File.ReadAllBytes(profilePath)) != expectedHash))
                    throw new InvalidOperationException("Live profile SHA-256 differs from the restore request.");
                if (expectedHash == null && exists && requestedUtc.HasValue && File.GetLastWriteTimeUtc(profilePath) > requestedUtc.Value)
                    throw new InvalidOperationException("Live profile changed after the restore request.");
                if (backupId == "latest")
                {
                    string hash = exists ? Hash(File.ReadAllBytes(profilePath)) : null;
                    backupId = List(profilePath).FirstOrDefault(candidate => candidate.Sha256 != hash)?.Id;
                    if (backupId == null) throw new InvalidOperationException("No different valid backup is available.");
                }
                Entry entry = ReadEntry(directory, backupId);
                byte[] original = File.ReadAllBytes(Path.Combine(directory, backupId + ".xml"));
                if (Hash(original) != entry.Sha256) throw new InvalidOperationException("Backup changed during verification.");
                LocalProfileData current = null;
                if (exists)
                {
                    try { current = LocalProfile.ReadFile(profilePath); }
                    catch (Exception error) when (error is InvalidOperationException || error is XmlException) { }
                }
                if (current != null && (current.Pending != null || current.PackRevealPending || current.PendingRewardPresentationId != null))
                    throw new InvalidOperationException("A match or transaction is pending; finish it first.");
                string staging = Path.Combine(directory, "restore-staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(staging);
                    string stagedProfile = Path.Combine(staging, "profile.xml");
                    WriteVerified(stagedProfile, original, true);
                    foreach (string sidecar in Directory.GetFileSystemEntries(Path.GetDirectoryName(profilePath), "profile.xml.v*.bak")
                        .Concat(Directory.GetFileSystemEntries(Path.GetDirectoryName(profilePath), "profile.xml.v*.bak.tmp")))
                    {
                        RejectLink(sidecar);
                        if ((File.GetAttributes(sidecar) & FileAttributes.Directory) != 0)
                            throw new InvalidOperationException("Migration backup must be a regular file.");
                        if (sidecar.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                            File.Copy(sidecar, Path.Combine(staging, Path.GetFileName(sidecar)));
                    }
                    LocalProfile.ValidateFile(stagedProfile, options);
                }
                finally { if (Directory.Exists(staging)) DeleteDirectoryBestEffort(staging); }
                before = exists ? Create(profilePath, File.ReadAllBytes(profilePath), "pre-restore", current == null ? 0 : Math.Max(0, current.Version),
                    new HashSet<string>(StringComparer.Ordinal) { backupId }) : null;
                string temporary = profilePath + ".restore.tmp";
                RejectLink(temporary);
                bool preserveTemporary = false;
                try
                {
                    WriteVerified(temporary, original, false);
                    FaultHook?.Invoke("restore-temp-written");
                    replacing = true;
                    try
                    {
                        if (exists) { FaultHook?.Invoke("replace"); File.Replace(temporary, profilePath, null); }
                        else File.Move(temporary, profilePath);
                    }
                    catch
                    {
                        if (!File.Exists(profilePath) && File.Exists(temporary))
                        {
                            preserveTemporary = true;
                            try { File.Move(temporary, profilePath); } catch { }
                        }
                        throw;
                    }
                    replaced = true;
                    FaultHook?.Invoke("restore-replaced");
                    if (Hash(File.ReadAllBytes(profilePath)) != entry.Sha256)
                        throw new IOException("Restored profile SHA-256 verification failed.");
                }
                finally { if (!preserveTemporary && File.Exists(temporary)) DeleteBestEffort(temporary); }
                string outcome = "OK Restored " + backupId + "; pre-restore backup: " + (before ?? "none (no previous profile)") + ".";
                Report(outcome);
                return outcome;
            }
        }
        catch (Exception error)
        {
            string outcome = replacing ? "ERROR: Profile " + (replaced ? "WAS replaced" : "may have been replaced")
                + "; pre-restore backup: " + (before ?? "none (no previous profile)") + ". " + error.Message
                : "REFUSED: " + error.Message;
            LastError = new InvalidOperationException(outcome, error);
            Report(outcome);
            throw LastError;
        }
    }

#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
    // Driven by the lab's tools/profile_backups.py. A player restores from the launcher, so an Installer release has no such file drop.
    internal static void ProcessRestoreRequest(string profilePath, ProfileOptions options = null)
    {
        SaveHold.Require("restoring a save");
        string temporary = null;
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(profilePath));
            string request = Path.Combine(directory, "restore-request.txt");
            if (!File.Exists(request)) return;
            LastError = null;
            string result = Path.Combine(directory, "restore-result.txt");
            temporary = result + ".tmp";
            string outcome;
            try
            {
                string[] lines = null;
                DateTime requestedUtc = File.GetLastWriteTimeUtc(request);
                Exception readError = null;
                try
                {
                    RejectLink(request);
                    if (new FileInfo(request).Length > 4096) throw new InvalidOperationException("Restore request exceeds the size limit.");
                    lines = File.ReadAllLines(request, Utf8).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToArray();
                }
                catch (Exception error) { readError = error; }
                // Consume before any restore work: a crash must never replay this request.
                try { DeleteFile(request); }
                catch (Exception error) { throw new InvalidOperationException("could not consume request: " + error.Message, error); }
                if (readError != null) throw readError;
                if (lines.Length == 0) throw new InvalidOperationException("Missing backup ID.");
                string expected = null;
                if (lines.Length > 1)
                {
                    if (lines.Length != 2 || !lines[1].StartsWith("expected_sha256=", StringComparison.Ordinal)
                        || !Regex.IsMatch(lines[1].Substring(16), @"\A[0-9a-fA-F]{64}\z"))
                        throw new InvalidOperationException("Invalid expected_sha256 request field.");
                    expected = lines[1].Substring(16).ToLowerInvariant();
                }
                outcome = Restore(profilePath, lines[0], expected, requestedUtc, options);
            }
            catch (Exception error)
            {
                LastError = error;
                outcome = error.Message.StartsWith("ERROR:", StringComparison.Ordinal) || error.Message.StartsWith("REFUSED:", StringComparison.Ordinal)
                    ? error.Message : "REFUSED: " + error.Message;
            }
            outcome = outcome.Replace('\r', ' ').Replace('\n', ' ');
            Report(outcome);
            RejectLink(result);
            WriteVerified(temporary, Utf8.GetBytes(outcome + "\n"), false);
            if (File.Exists(result)) File.Replace(temporary, result, null);
            else File.Move(temporary, result);
        }
        catch (Exception error) { LastError = error; Report("Restore request result failed: " + error.Message); }
        finally { if (temporary != null && File.Exists(temporary)) DeleteBestEffort(temporary); }
    }
#endif
}
