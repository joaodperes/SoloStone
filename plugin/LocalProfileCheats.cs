using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public sealed partial class LocalProfile
{
    private const int DiamondQualityTag = 1932, SignatureQualityTag = 2589;
    // "All cards owned" = every card The Complete Works counts (the Standard collection, pack exclusions included) plus
    // the supported/deckbuilding cards. Hero-type collection cards such as CATA_190h count; base hero portraits do not.
    private static readonly Lazy<HashSet<string>> collectionCardIds = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(LocalShop.StandardCollectionCards.Where(card => card != null).Select(card => card.Id), StringComparer.Ordinal));
    private static readonly Lazy<EngineCard[]> supportedCheatCards = new Lazy<EngineCard[]>(() => LocalCardCatalog.Supported
        .Concat(LocalDecks.LegalDeckbuildingCards)
        .Concat(FairPackRules.PackTypes.SelectMany(type => FairPackRules.Eligible(type, FairPackRules.RuleVersion)))
        .Concat(LocalShop.StandardCollectionCards)
        .Where(card => card != null && card.Collectible && card.Implemented && LocalCardCatalog.InActiveMeta(card)
            && (card.Type != CardType.HERO || collectionCardIds.Value.Contains(card.Id) && !LocalCoreProgression.Heroes.Contains(card.Id)))
        .GroupBy(card => card.Id, StringComparer.Ordinal).Select(group => group.First())
        .OrderBy(card => card.Id, StringComparer.Ordinal).ToArray());
    private static readonly Lazy<LocalCardCount[]> supportedCheatPremium = new Lazy<LocalCardCount[]>(() => {
        var rows = new List<LocalCardCount>();
        foreach (var card in supportedCheatCards.Value)
        {
            int count = Playset(card.Id);
            rows.Add(new LocalCardCount { Id = card.Id, Premium = 1, Count = count, Acquired = count });
            if (card[(GameTag)DiamondQualityTag] == 1)
                rows.Add(new LocalCardCount { Id = card.Id, Premium = 2, Count = count, Acquired = count });
            if (card[(GameTag)SignatureQualityTag] == 1)
                rows.Add(new LocalCardCount { Id = card.Id, Premium = 3, Count = count, Acquired = count });
        }
        foreach (string hero in LocalCoreProgression.Heroes)
            rows.Add(new LocalCardCount { Id = hero, Premium = 1, Count = 1, Acquired = 1 });
        return rows.ToArray();
    });
    private static readonly Lazy<Dictionary<string, int>> supportedCheatPremiumCounts =
        new Lazy<Dictionary<string, int>>(() => supportedCheatPremium.Value.ToDictionary(
            row => PremiumKey(row.Id, row.Premium), row => row.Count, StringComparer.Ordinal));

    internal static IReadOnlyList<EngineCard> SupportedCheatCards => supportedCheatCards.Value;
    internal static IReadOnlyList<LocalCardCount> SupportedCheatPremium => supportedCheatPremium.Value;
    private static string PremiumKey(string id, int premium) => premium + ":" + id;
    internal static int CheatPremiumCount(string id, int premium) => id != null
        && supportedCheatPremiumCounts.Value.TryGetValue(PremiumKey(id, premium), out int count) ? count : 0;

    internal bool InfiniteGold => profileOptions.InfiniteGold;
    internal bool InfiniteDust => profileOptions.InfiniteDust;
    internal bool StartsLegendOne => profileOptions.StartLegendOne;

    // Test-control only: prepare XP, then let the live match's normal settlement grant the next class reward.
    internal LocalClassRewardGrant PreparePremiumUnlockForTest(string matchId)
    {
        LocalClassRewardGrant reward = null;
        CommitChange(next => {
            var match = next.Pending;
            if (match == null || match.Id != matchId || match.DebugScenario || CasualRules.IsCasual(match.Mode)
                || !LocalCoreProgression.Heroes.Contains(match.DeckHero) || next.PackRevealPending)
                throw new InvalidOperationException("A live Ranked class match is required.");
            var xp = next.ClassXp.Single(row => row.Hero == match.DeckHero);
            int level = LocalCoreProgression.Level(xp.Xp);
            var target = LocalClassRewards.ActiveRewards.Where(row => row.Hero == match.DeckHero && row.Level > level
                && !next.ClassRewardGrants.Any(grant => grant.Hero == row.Hero && grant.Level == row.Level))
                .OrderBy(row => row.Level).FirstOrDefault();
            if (target == null) throw new InvalidOperationException("No future class premium reward remains.");
            reward = LocalClassRewards.Resolve(next, target.Hero, target.Level);
            if (reward.Copies <= 0 || reward.Dust > 0) throw new InvalidOperationException("Next class reward grants dust, not a premium card.");
            xp.Xp = Math.Max(xp.Xp, LocalCoreProgression.LevelThresholds[target.Level - 1] - LocalCoreProgression.Xp("Won", false));
            return true;
        });
        return reward;
    }

    internal void PersistNewProfile(ProfileOptions options)
    {
        if (FileExists()) throw new InvalidOperationException("Creation cheats apply only to a new save.");
        var next = Snapshot();
        next.GrindTier = options.GrindTier ?? GrindTiers.Default.Id;
        // #57: every new save, cheats included, gets the welcome gift in this same creation commit.
        ApplyWelcome(next);
        if (options.AllCards)
        {
            foreach (var card in supportedCheatCards.Value)
            {
                int count = Playset(card.Id);
                if (LocalCardCatalog.IsCore(card.Id))
                {
                    var grant = next.CoreGrants.SingleOrDefault(row => row.Id == card.Id);
                    if (grant == null) next.CoreGrants.Add(new LocalCardCount { Id = card.Id, Count = count });
                    else grant.Count = Math.Max(grant.Count, count);
                    continue;
                }
                var owned = next.Collection.SingleOrDefault(row => row.Id == card.Id);
                if (owned == null) next.Collection.Add(new LocalCardCount { Id = card.Id, Count = count, Acquired = count });
                else { owned.Count = Math.Max(owned.Count, count); owned.Acquired = Math.Max(owned.Acquired, count); }
            }
            next.CoreScheduleVersion = LocalCoreProgression.ScheduleVersion;
        }
        // Sandbox All decks after All cards, so it only grants copies still missing (never past a playset).
        if (options.AllDecks) GrantAllDecks(next, options.AllCosmetics, MaximumCustomDecks);
        if (options.StartLegendOne)
        {
            next.LadderRuleVersion = 0;
            next.LadderState = null;
            next.LadderCalibration = null;
            GrantLegendOneCreation(next);
        }
        GrantCreationAchievements(next, options);
        Commit(next);
    }

    private bool FileExists() => System.IO.File.Exists(path);

    // Sandbox "All decks" (owner 2026-10-28): every offered ready-made deck, exactly as buying it would grant (GrantDeck),
    // in shop catalogue order up to the personal deck limit. Each grant gets a zero-gold Deck receipt, so recipe and deck
    // ownership keep their strict receipt provenance (ValidatePurchases accepts these only on All decks saves).
    internal static int GrantAllDecks(LocalProfileData next, bool allCosmetics, int deckLimit)
    {
        if (next.Purchases.Count != 0) throw new InvalidOperationException("All decks applies only to a new save.");
        int granted = 0;
        foreach (var product in LocalShop.Default.Products.Where(product => product.Kind == "Deck" && !product.verify && !LocalShop.Default.Withheld(product)))
        {
            if (next.CustomDecks.Count >= Math.Min(deckLimit, MaximumCustomDecks)) break;
            var receipt = new LocalPurchaseReceipt { Sequence = checked(next.PurchaseSequence + 1),
                RequestId = SandboxDeckRequestPrefix + product.Id, CatalogVersion = LocalShop.Default.Version,
                ProductId = product.Id, Kind = product.Kind, Quantity = 1, GoldPaid = 0, PriceBeforeKit = 0 };
            GrantDeck(next, product, receipt, allCosmetics);
            next.PurchaseSequence = receipt.Sequence;
            next.Purchases.Add(receipt);
            granted++;
        }
        return granted;
    }

    // All welcome assets and their receipt land in the same creation commit.
    private static void ApplyWelcome(LocalProfileData next)
    {
        if (next.Welcome != null || next.DeckKits.Any(kit => kit.Id == LocalWelcomeGift.KitId))
            throw new InvalidOperationException("The welcome gift was already granted.");
        next.Welcome = new LocalWelcome { Version = LocalWelcomeGift.Version, PackType = LocalWelcomeGift.PackType,
            Packs = LocalWelcomeGift.Packs, Gold = LocalWelcomeGift.Gold, Dust = LocalWelcomeGift.Dust,
            KitId = LocalWelcomeGift.KitId, KitGoldCap = LocalWelcomeGift.KitGoldCap };
        AddPacks(next, LocalWelcomeGift.PackType, LocalWelcomeGift.Packs);
        next.GoldBalance = checked(next.GoldBalance + LocalWelcomeGift.Gold);
        next.DustBalance = checked(next.DustBalance + LocalWelcomeGift.Dust);
        next.DeckKits.Add(new LocalDeckKit { Id = LocalWelcomeGift.KitId, MilestoneId = LocalWelcomeGift.KitMilestoneId,
            GoldCap = LocalWelcomeGift.KitGoldCap });
        foreach (var definition in LocalWelcomeGift.Cards)
        {
            var collection = definition.Premium == 0 ? next.Collection : next.PremiumCollection;
            var card = collection.SingleOrDefault(row => row.Id == definition.CardId && row.Premium == definition.Premium);
            int owned = card?.Count ?? 0, cap = Playset(definition.CardId);
            int count = KnownLedgerCard(definition.CardId) ? Math.Min(definition.Count, Math.Max(0, cap - owned)) : 0;
            next.Welcome.Cards.Add(new LocalOwnedCardGrant { CardId = definition.CardId, Premium = definition.Premium,
                Count = count, OwnedBefore = owned });
            if (count == 0) continue;
            if (card == null) { card = new LocalCardCount { Id = definition.CardId, Premium = definition.Premium }; collection.Add(card); }
            card.Count = checked(owned + count);
            card.Acquired = Math.Min(cap, checked(card.Acquired + count));
        }
    }

    private static void ValidateWelcomeCards(LocalWelcome welcome)
    {
        if (welcome == null) return;
        var table = LocalWelcomeGift.Table(welcome.Version);
        if (!LocalWelcomeGift.IsKnown(welcome) || welcome.Cards == null || welcome.Cards.Any(row => row == null)
            || welcome.Cards.Count != table.Count)
            throw new InvalidOperationException("Invalid welcome card grants.");
        foreach (var definition in table)
        {
            var rows = welcome.Cards.Where(row => row.CardId == definition.CardId && row.Premium == definition.Premium).ToArray();
            if (rows.Length != 1 || rows[0].OwnedBefore < 0 || rows[0].Count < 0 || rows[0].Count > definition.Count)
                throw new InvalidOperationException("Invalid welcome card grants.");
            // These V2 rows shipped before catalog admission; their zero receipts must survive that later admission.
            bool skippedBeforeAdmission = welcome.Version == 2 && (definition.CardId == "BE_036"
                || definition.CardId == "BE_EVENT_100" || definition.CardId == "BE_EVENT_101" || definition.CardId == "BE_EVENT_102");
            // Receipts outlive rarity corrections and retired DB entries; bound the playset of their day.
            if (!(rows[0].Count == 0 && (!KnownLedgerCard(definition.CardId) || skippedBeforeAdmission))
                && Cards.All.Any(card => card.Id == definition.CardId) && !Enumerable.Range(1, LocalEconomy.MaximumPlayset).Any(cap =>
                rows[0].Count == Math.Min(definition.Count, Math.Max(0, cap - rows[0].OwnedBefore))))
                throw new InvalidOperationException("Invalid welcome card grants.");
        }
    }

    // #57 first launch: the production open path (SelectedProfile.OpenStartup) calls this right after opening. It grants
    // only when this instance was built from a missing profile.xml and nothing has been written yet, so the gift lands in
    // the save's first commit: a crash leaves either no save (the next launch creates it again, once) or gift + receipt.
    // Loading an existing file, a migration and plain `new LocalProfile(path)` never grant.
    internal bool GrantWelcomeIfNew()
    {
        WelcomeSkipped = null;
        if (!bornMissing || FileExists() || data.Welcome != null) return false;
        // A missing profile.xml beside leftovers of an earlier save (an interrupted replace or restore, migration
        // sidecars, backups) is not a new player: fail closed, no gift and no commit (Commit would overwrite the .tmp).
        WelcomeSkipped = EarlierSaveTrace(path);
        if (WelcomeSkipped != null) return false;
        var next = Snapshot();
        ApplyWelcome(next);
        Commit(next);
        return true;
    }

    // Why the last GrantWelcomeIfNew on a missing file granted nothing (for the startup log), or null.
    internal string WelcomeSkipped { get; private set; }

    internal static string EarlierSaveTrace(string profilePath)
    {
        string directory = System.IO.Path.GetDirectoryName(profilePath), name = System.IO.Path.GetFileName(profilePath);
        foreach (string suffix in new[] { ".tmp", ".restore.tmp" })
            if (System.IO.File.Exists(profilePath + suffix)) return "leftover " + name + suffix;
        string sidecar = System.IO.Directory.GetFileSystemEntries(directory, name + ".v*.bak*").FirstOrDefault();
        if (sidecar != null) return "leftover " + System.IO.Path.GetFileName(sidecar);
        string backups = System.IO.Path.Combine(directory, "backups");
        if (System.IO.Directory.Exists(backups) && System.IO.Directory.EnumerateFileSystemEntries(backups).Any()) return "non-empty backups directory";
        return null;
    }

    public LocalWelcome Welcome => Copy(data.Welcome);
    public bool WelcomePopupPending => data.Welcome != null && !data.Welcome.Seen;

    // OK on the welcome popup. Idempotent; losing this commit can only show the popup again, never re-grant.
    public bool MarkWelcomeSeen()
    {
        if (data.Welcome == null) throw new InvalidOperationException("This save has no welcome gift.");
        if (data.Welcome.Seen) return false;
        var next = Snapshot();
        next.Welcome.Seen = true;
        Commit(next);
        return true;
    }
}
