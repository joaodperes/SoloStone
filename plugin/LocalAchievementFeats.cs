using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Feats layout and point values for the native Journal "Feats" screens (owner 2026-10-28).
// Presentation only: unlock rules, rewards and the saved ledger live in LocalAchievements/LocalProfileAchievements.
// Owner-tweakable table: change a row's points (retail-like 5/10/25/50 scale), short tile text or placement here.
// Every catalog achievement must appear exactly once (GuardTests enforce it).
internal static class LocalAchievementFeatTable
{
    internal static readonly int[] PointScale = { 5, 10, 25, 50 };

    internal sealed class Category
    {
        internal string Id { get; }
        internal string Name { get; }
        // Retail ACHIEVEMENT_CATEGORY Icon code whose medallion art (and id) this category borrows.
        internal string RetailIcon { get; }
        internal IReadOnlyList<Subcategory> Subcategories { get; }
        internal Category(string id, string name, string retailIcon, params Subcategory[] subcategories)
        { Id = id; Name = name; RetailIcon = retailIcon; Subcategories = subcategories; }
    }

    internal sealed class Subcategory
    {
        internal string Id { get; }
        internal string Name { get; }
        // Retail ACHIEVEMENT_SUBCATEGORY icon code borrowed for the subcategory button (e.g. RANKED, BOH, ARENA).
        internal string RetailIcon { get; }
        internal IReadOnlyList<Row> Rows { get; }
        internal Subcategory(string id, string name, string retailIcon, params Row[] rows)
        { Id = id; Name = name; RetailIcon = retailIcon; Rows = rows; }
    }

    internal sealed class Row
    {
        internal string Id { get; }
        internal int Points { get; }
        // One line for the native tile; the inspection popup shows the full condition.
        internal string Summary { get; }
        internal Row(string id, int points, string summary) { Id = id; Points = points; Summary = summary; }
    }

    private static Row R(string id, int points, string summary) => new Row(id, points, summary);

    internal static readonly IReadOnlyList<Category> Categories = new[] {
        new Category("progression", "Progression", "general",
            new Subcategory("career", "Career", "WOLF",
                R("a-hundred-good-games", 25, "Win 100 eligible world matches."),
                R("class-act", 50, "Win 10 eligible matches with each of the 11 classes."),
                R("the-board-is-a-workplace-hazard", 25, "Defeat 1,000 enemy minions."),
                R("world-tour", 25, "Defeat 25 different named world NPCs.")),
            new Subcategory("ranked", "Ranked", "RANKED",
                R("welcome-to-legend", 25, "Reach Legend for the first time."),
                R("among-the-hundred", 50, "Finish a ranked match at Legend 100 or better."),
                R("there-can-be-only-one", 50, "Finish a ranked match at Legend 1."))),
        new Category("gameplay", "Gameplay", "gameplay",
            new Subcategory("signature", "Signature Feats", "GRYPHON",
                R("calculated", 25, "Win with exactly 1 Health and 0 Armor."),
                R("out-of-cards-not-ideas", 25, "Win with an empty deck and an empty hand."),
                R("seven-card-pickup", 25, "One spell destroys seven enemy minions."),
                R("thanks-ill-take-it-from-here", 50, "A stolen enemy minion deals the lethal attack."),
                R("mutually-assured-destruction", 50, "Destroy both heroes at once for a natural draw."),
                R("aggressively-average", 25, "Beat a Diamond/Legend NPC with only Free/Common cards."),
                R("call-an-ambulance", 25, "Start a turn at 1–5 Health against 25+ and win that turn."),
                R("fine-ill-do-it-myself", 10, "Win with a hero attack after playing no minion cards."),
                R("absolutely-unnecessary", 25, "Win with one hit of 30+ overkill damage.")),
            new Subcategory("match", "Match Feats", "HYDRA",
                R("respectfully-no", 10, "Counter an enemy spell costing 8+, then win."),
                R("from-downtown", 25, "Win from an empty board against 30+ Health in one turn."),
                R("armored-division", 10, "Gain 50 Armor and win with 25+ Armor left."),
                R("untouchable", 25, "Beat a Diamond/Legend NPC without losing Health."),
                R("one-of-everything", 10, "Control six minions of six different types, then win."),
                R("family-reunion", 10, "Control seven different minions of one type, then win."),
                R("topdecking-is-a-skill", 25, "Topdeck a spell into an empty hand and deal lethal with it."),
                R("hostile-takeover", 25, "Control three stolen enemy minions at once, then win."),
                R("no-time-to-explain", 10, "Beat a Diamond/Legend NPC before your fifth turn."),
                R("running-on-fumes", 10, "Take 10 fatigue damage and win."),
                R("population-boom", 25, "Summon 15 minions in one turn, then win."),
                R("acceptable-losses", 10, "Destroy seven of your own minions in one turn, then win."),
                R("no-touching", 10, "Freeze five enemy minions with one spell and win that turn."),
                R("chain-reaction", 25, "Trigger eight Deathrattles in one turn, then win."),
                R("power-trip", 10, "Deal 20 Hero Power damage to the enemy hero, then win."),
                R("pocket-sized-problem", 10, "A 1-Attack minion deals exact lethal."),
                R("not-today", 25, "A Reborn minion survives a turn, then attacks for lethal."),
                R("special-delivery", 25, "Burn three enemy cards in one turn, then win."),
                R("borrowed-time", 25, "Win during an extra turn."),
                R("return-to-sender", 25, "A spell taken from the opponent deals lethal."))),
        new Category("classes", "Classes", "adventures",
            new Subcategory("class-mastery", "Class Challenges", "BOH",
                R("death-knight-grave-expenditure", 25, "Spend 30 Corpses in one match, then win."),
                R("demon-hunter-face-value", 10, "Attack with your hero on five turns and deal lethal."),
                R("druid-ahead-of-the-curve", 25, "Reach 10 Mana while the enemy has 5 or less, then win."),
                R("hunter-whos-a-good-boy", 25, "A starting-deck Beast deals lethal with 20+ Attack."),
                R("mage-final-exam", 25, "Cast ten spells in one turn and win that turn."),
                R("paladin-entry-level-position", 50, "A Silver Hand Recruit deals lethal with 15+ Attack."),
                R("priest-we-can-rebuild-you", 25, "Drop to 5 Health, heal 25, win at full Health."),
                R("rogue-combo-meal", 25, "Activate four Combos in one turn, then win."),
                R("shaman-the-whole-band", 50, "Control seven Totems including all four basics, then win."),
                R("warlock-damage-is-a-resource", 25, "Take 30 self-damage, then win at 10+ Health."),
                R("warrior-good-stats-for-the-cost", 50, "An awakened Petrified Ogre deals the winning attack."))),
        new Category("deckbuilding", "Deckbuilding", "game_modes",
            new Subcategory("deck-challenges", "Deck Challenges", "ARENA",
                R("no-copies-no-problem", 10, "Beat a Diamond/Legend NPC with a singleton deck."),
                R("creature-feature", 10, "Beat a Diamond/Legend NPC with only minion cards."),
                R("words-not-violence", 10, "Beat a Diamond/Legend NPC with no minion cards."),
                R("no-famous-people", 25, "Beat a Legend NPC with no Legendary cards."),
                R("home-team", 10, "Beat a Diamond/Legend NPC with no Neutral cards."),
                R("neutral-ground", 10, "Beat a Diamond/Legend NPC with only Neutral cards."),
                R("small-ball", 10, "Beat a Diamond/Legend NPC with only 0–3 Cost cards."),
                R("heavy-reading", 10, "Beat a Diamond/Legend NPC with 15+ cards costing 7+."),
                R("one-expansion-stand", 10, "Beat a Diamond/Legend NPC with a one-expansion deck."),
                R("perfect-curve", 10, "Beat a Diamond/Legend NPC with two cards at each Cost 1–7."),
                R("made-to-measure", 25, "Beat a Diamond/Legend NPC with ten cards you crafted."))),
        new Category("collection", "Collection", "collection",
            new Subcategory("library", "Library", "CLASSIC",
                R("well-read", 25, "Play 250 different collectible cards."),
                R("the-complete-works", 50, "Own a full playable set of one expansion."),
                R("first-signature-mastery", 10, "Unlock your first Signature through card mastery."),
                R("first-diamond-mastery", 25, "Unlock your first Diamond through card mastery."),
                R("fifty-golden-masteries", 25, "Unlock 50 Golden cards through card mastery."),
                R("all-premium-deck", 25, "Win with a personal deck containing only premium cards.")))
    };
}

internal sealed class LocalFeat
{
    internal string Id, Name, Summary, Condition, RewardText, CategoryId, SubcategoryId;
    internal int NativeId, Points, Progress, Quota, SortOrder;
    internal bool Earned;
    internal long UnlockedUnixSeconds;
    internal LocalAchievementReward Reward;
    internal bool Generated;
    internal IReadOnlyList<LocalFeatCard> Cards = Array.Empty<LocalFeatCard>();
    internal int RewardPremium;
}

internal sealed class LocalFeatCard
{
    internal string CardId;
    internal int DbfId, Premium, Copies;
}

internal sealed class LocalFeatStats
{
    internal int Points, AvailablePoints, Completed, Total;
    internal int Percent => AvailablePoints <= 0 ? 0 : Points * 100 / AvailablePoints;
    internal static LocalFeatStats Of(IEnumerable<LocalFeat> feats)
    {
        var all = feats.Where(f => !f.Generated).ToArray();
        return new LocalFeatStats { Points = all.Where(f => f.Earned).Sum(f => f.Points), AvailablePoints = all.Sum(f => f.Points),
            Completed = all.Count(f => f.Earned), Total = all.Length };
    }
}

internal sealed class LocalFeatSubcategory
{
    internal string Id, Name, RetailIcon, SectionName;
    internal int NativeId, SectionNativeId;
    internal IReadOnlyList<LocalFeat> Feats;
    internal LocalFeatStats Stats;
}

internal sealed class LocalFeatCategory
{
    internal string Id, Name, RetailIcon;
    internal int NativeId;
    internal IReadOnlyList<LocalFeatSubcategory> Subcategories;
    internal LocalFeatStats Stats;
}

// Immutable, validated projection of one profile's achievements for the Feats screens. Never mutates profile state.
internal sealed class LocalAchievementFeats
{
    // Synthetic ids far above retail DBF ids; stable by table position, never saved.
    internal const int CategoryIdBase = 1_900_000_000, SubcategoryIdBase = 1_910_000_000,
        SectionIdBase = 1_920_000_000, AchievementIdBase = 1_930_000_000;
    internal static readonly IReadOnlyList<string> SupportedClasses = new[] {
        "DEATHKNIGHT", "DEMONHUNTER", "DRUID", "HUNTER", "MAGE", "PALADIN",
        "PRIEST", "ROGUE", "SHAMAN", "WARLOCK", "WARRIOR"
    };

    internal IReadOnlyList<LocalFeatCategory> Categories { get; }
    internal IReadOnlyList<LocalFeat> All { get; }
    internal LocalFeatStats Stats { get; }
    internal IReadOnlyList<LocalFeat> RecentlyCompleted => All.Where(f => f.Earned && !f.Generated)
        .OrderByDescending(f => f.UnlockedUnixSeconds).ThenBy(f => f.SortOrder).ToArray();

    internal const int LevelIdBase = 1_940_000_000, MasteryIdBase = 1_950_000_000,
        TrackSectionIdBase = 1_960_000_000, TrackSubcategoryIdBase = 1_970_000_000;
    internal static bool IsNativeAchievementId(int id) => id >= AchievementIdBase && id < AchievementIdBase + 100_000
        || id >= LevelIdBase && id < LevelIdBase + 1_300 || id >= MasteryIdBase && id < MasteryIdBase + 4_000_000;
    internal static bool IsNativeSectionId(int id) => id >= SectionIdBase && id < SectionIdBase + 100_000
        || id >= TrackSectionIdBase && id < TrackSectionIdBase + 24;
    internal LocalFeat Find(int nativeId) => All.FirstOrDefault(f => f.NativeId == nativeId);
    internal LocalFeat Find(string id) => All.FirstOrDefault(f => f.Id == id);

    private LocalAchievementFeats(LocalAchievementState state, LocalProfileData profile = null)
    {
        ValidateTable();
        Validate(state);
        var unlocks = state.Unlocks.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var catalog = LocalAchievementCatalog.All.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var all = new List<LocalFeat>();
        var categories = new List<LocalFeatCategory>();
        int subIndex = 0, order = 0;
        for (int c = 0; c < LocalAchievementFeatTable.Categories.Count; c++)
        {
            var category = LocalAchievementFeatTable.Categories[c];
            var subs = new List<LocalFeatSubcategory>();
            foreach (var sub in category.Subcategories)
            {
                var feats = new List<LocalFeat>();
                foreach (var row in sub.Rows)
                {
                    var definition = catalog[row.Id];
                    bool earned = unlocks.TryGetValue(row.Id, out LocalAchievementUnlock unlock);
                    int quota = Quota(row.Id);
                    var feat = new LocalFeat {
                        Id = row.Id, Name = definition.Name, Summary = row.Summary, Condition = definition.Condition,
                        Points = row.Points, Reward = definition.Reward, CategoryId = category.Id, SubcategoryId = sub.Id,
                        RewardText = definition.Reward.GrantKindCount == 0 ? row.Points + " Feats points"
                            : LocalAchievementPresentation.SavedRewardText(LocalAchievementGrantReceipt.FromCatalog(row.Id)),
                        NativeId = AchievementIdBase + CatalogIndex(row.Id), SortOrder = order++, Earned = earned, Quota = quota,
                        Progress = earned ? quota : Math.Max(0, Math.Min(quota, RawProgress(row.Id, state.Career))),
                        UnlockedUnixSeconds = earned ? UnixSeconds(unlock.UnlockedUtc) : 0
                    };
                    feats.Add(feat); all.Add(feat);
                }
                subs.Add(new LocalFeatSubcategory { Id = sub.Id, Name = sub.Name, RetailIcon = sub.RetailIcon, NativeId = SubcategoryIdBase + subIndex,
                    SectionNativeId = SectionIdBase + subIndex, Feats = feats, Stats = LocalFeatStats.Of(feats) });
                subIndex++;
            }
            categories.Add(new LocalFeatCategory { Id = category.Id, Name = category.Name, RetailIcon = category.RetailIcon,
                NativeId = CategoryIdBase + c, Subcategories = subs, Stats = LocalFeatStats.Of(subs.SelectMany(s => s.Feats)) });
        }
        if (profile != null) AddTracks(profile, categories, all);
        Categories = categories; All = all; Stats = LocalFeatStats.Of(all);
    }

    internal static LocalAchievementFeats Create(LocalAchievementState snapshot) =>
        new LocalAchievementFeats(snapshot ?? throw new InvalidOperationException("Achievement state is missing."));

    internal static LocalAchievementFeats Create(LocalAchievementState state, LocalProfileData profile) =>
        new LocalAchievementFeats(state, profile ?? throw new ArgumentNullException(nameof(profile)));

    private static SabberStoneCore.Model.Card Definition(string id)
    {
        try { return id == null ? null : SabberStoneCore.Model.Cards.FromId(id); }
        catch (KeyNotFoundException) { return null; }
    }
    private static LocalFeatCard Card(string id, int premium, int copies) => new LocalFeatCard {
        CardId = id, DbfId = Definition(id)?.AssetId ?? 0, Premium = premium, Copies = copies
    };
    private static string CardText(LocalFeatCard card) => (card.Premium == 0 ? "" : new[] { "", "Golden ", "Diamond ", "Signature " }[card.Premium])
        + (Definition(card.CardId)?.Name ?? LocalClassRewards.Rewards.FirstOrDefault(row => row.CardId == card.CardId)?.Name ?? card.CardId) + (card.Copies > 1 ? " x" + card.Copies : "");
    private static LocalFeat Track(int id, string key, string name, string condition, int progress, int quota, bool earned,
        IEnumerable<LocalFeatCard> cards = null, LocalAchievementReward reward = null, long date = 0)
    {
        var items = (cards ?? Enumerable.Empty<LocalFeatCard>()).ToArray();
        string text = items.Length > 0 ? string.Join(", ", items.Select(CardText))
            : reward?.Dust > 0 ? reward.Dust + " dust" : reward?.HeroSkinId ?? "Portrait";
        return new LocalFeat { NativeId = id, Id = key, Name = name, Summary = condition + "\n" + text, Condition = condition,
            Progress = Math.Max(0, Math.Min(Math.Max(0, quota), progress)), Quota = Math.Max(0, quota),
            Earned = earned, Generated = true, Cards = items, Reward = reward ?? new LocalAchievementReward(),
            RewardText = text, UnlockedUnixSeconds = date };
    }

    private static long GrantDate(LocalProfileData profile, Func<LocalMatchRecord, bool> granted) => profile.Matches.Where(granted)
        .Select(match => UnixSeconds(match.StartedUtc)).DefaultIfEmpty(0).Max();

    private static void AddTracks(LocalProfileData profile, List<LocalFeatCategory> categories, List<LocalFeat> all)
    {
        var xp = profile.ClassXp.ToDictionary(row => row.Hero, row => row.Xp, StringComparer.Ordinal);
        int combined = LocalCoreProgression.NeutralBaseCombinedLevel + LocalCoreProgression.Heroes.Sum(hero => LocalCoreProgression.Level(xp[hero]) - 1);
        for (int index = 0; index <= LocalCoreProgression.Heroes.Length; index++) {
            string hero = index < LocalCoreProgression.Heroes.Length ? LocalCoreProgression.Heroes[index] : "";
            string name = hero == "" ? "All classes" : LocalCoreProgression.ClassName(hero);
            var levels = new List<LocalFeat>();
            foreach (var group in LocalCoreProgression.Unlocks.Where(row => row.Hero == hero).GroupBy(row => hero == "" ? Math.Max(LocalCoreProgression.NeutralBaseCombinedLevel, row.Threshold) : row.Threshold)) {
                int level = group.Key;
                bool earned = group.All(card => profile.CoreGrants.Any(grant => grant.Id == card.Id && grant.Count >= card.Copies));
                levels.Add(Track(LevelIdBase + index * 100 + level, "level:" + index + ":" + level, "Level " + level,
                    hero == "" ? "Reach combined class level " + level + "." : "Reach " + name + " level " + level + ".",
                    hero == "" ? combined : xp[hero], hero == "" ? level : LocalCoreProgression.LevelThresholds[level - 1], earned,
                    group.Select(card => Card(card.Id, 0, card.Copies)), date: GrantDate(profile,
                        match => group.Any(card => match.UnlockedCardIds?.Contains(card.Id) == true))));
            }
            if (hero != "") {
                // Preview unclaimed levels in order, as settlement grants them, so two levels never show the same substitute.
                var claimed = profile.ClassRewardGrants;
                profile.ClassRewardGrants = new List<LocalClassRewardGrant>(claimed);
                foreach (var scheduled in LocalClassRewards.ActiveRewards.Where(row => row.Hero == hero).OrderBy(row => row.Level)) {
                    var grant = LocalClassRewards.Resolve(profile, hero, scheduled.Level);
                    bool earned = claimed.Any(row => row.Hero == hero && row.Level == scheduled.Level);
                    if (!earned) profile.ClassRewardGrants.Add(grant);
                    levels.Add(Track(LevelIdBase + index * 100 + scheduled.Level, "level:" + index + ":" + scheduled.Level,
                        "Level " + scheduled.Level, "Reach " + name + " level " + scheduled.Level + ".", xp[hero],
                        LocalCoreProgression.LevelThresholds[scheduled.Level - 1], earned,
                        grant.Dust == 0 ? new[] { Card(grant.CardId, grant.Premium, grant.Copies) } : null,
                        new LocalAchievementReward(dust: grant.Dust), Math.Max(UnixSeconds(grant.GrantedUtc), GrantDate(profile,
                            match => match.ClassRewardGrants?.Any(row => row.Hero == hero && row.Level == scheduled.Level) == true))));
                }
                profile.ClassRewardGrants = claimed;
                int wins = profile.Matches.Count(match => match.DeckHero == hero && LocalProfile.XpWin(match));
                foreach (string kind in new[] { "golden", "honored" }) {
                    var grant = profile.PortraitGrants.SingleOrDefault(row => row.Hero == hero && row.Kind == kind);
                    var portrait = LocalClassRewards.Portraits.Single(row => row.Hero == hero);
                    string card = grant?.CardId ?? (kind == "golden" ? hero : portrait.Honored);
                    var feat = Track(LevelIdBase + index * 100 + (kind == "golden" ? 61 : 62), "portrait:" + hero + ":" + kind,
                        (kind == "golden" ? "Golden hero" : "Honored portrait"), "Win Ranked matches as " + name + ".", wins,
                        kind == "golden" ? LocalClassRewards.GoldenPortraitWins : LocalClassRewards.HonoredPortraitWins,
                        grant != null, reward: new LocalAchievementReward(heroSkinId: card), date: Math.Max(UnixSeconds(grant?.GrantedUtc), GrantDate(profile,
                            match => match.PortraitGrants?.Any(row => row.Hero == hero && row.Kind == kind) == true)));
                    feat.RewardPremium = kind == "golden" ? 1 : 0;
                    feat.RewardText = (kind == "golden" ? "Golden " : "Honored ") + name + " portrait";
                    feat.Summary = feat.Condition + "\n" + feat.RewardText;
                    levels.Add(feat);
                }
            }
            AddTrack(categories.Single(row => row.Id == "classes"), all, index, "levels:" + index, name, "Level rewards", levels);
        }
        var counts = LocalCardMastery.PointCounts(profile.Matches);
        var cardClasses = LocalCoreProgression.Heroes.Select(hero => SabberStoneCore.Model.Cards.FromId(hero).Class).ToArray();
        // Include missing Core/starter and shop cards, including grant-only cards when they have tiers.
        var pool = LocalCardCatalog.Supported.Concat(LocalShop.StandardCollectionCards)
            .Where(card => card.Collectible && LocalCardCatalog.InActiveMeta(card))
            // Keep rows the player already progressed or earned after a meta switch.
            .Concat(counts.Keys.Concat(profile.MasteryGrants.Select(row => row.CardId)).Select(Definition)
                .Where(card => card != null && card.Collectible))
            .GroupBy(card => card.Id, StringComparer.Ordinal).Select(group => group.First())
            // Multiclass cards have no single printed Class; assign once so their native ids stay unique.
            .Select(card => new { Card = card, Class = card.Class == SabberStoneCore.Enums.CardClass.INVALID
                ? cardClasses.FirstOrDefault(heroClass => LocalDecks.IsAllowedInDeck(card, heroClass)) : card.Class })
            .Where(row => row.Class != SabberStoneCore.Enums.CardClass.INVALID)
            .ToLookup(row => row.Class.ToString(), row => row.Card, StringComparer.Ordinal);
        // Index legacy receipt dates once instead of scanning match history for every collectible card.
        var dates = profile.MasteryGrants.Select(row => new { row.CardId, Date = UnixSeconds(row.GrantedUtc) })
            .Concat(profile.ClassRewardGrants.Where(row => row.CardId != null)
                .Select(row => new { row.CardId, Date = UnixSeconds(row.GrantedUtc) }))
            .Concat(profile.Matches.SelectMany(match => (match.MasteryGrants ?? Enumerable.Empty<LocalMasteryGrant>())
                .Select(row => row.CardId).Concat((match.ClassRewardGrants ?? Enumerable.Empty<LocalClassRewardGrant>())
                    .Select(row => row.CardId)).Where(id => id != null).Distinct(StringComparer.Ordinal)
                .Select(id => new { CardId = id, Date = UnixSeconds(match.StartedUtc) })))
            .GroupBy(row => row.CardId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(row => row.Date), StringComparer.Ordinal);
        for (int index = 0; index <= LocalCoreProgression.Heroes.Length; index++) {
            string hero = index < LocalCoreProgression.Heroes.Length ? LocalCoreProgression.Heroes[index] : null;
            string cardClass = hero == null ? "NEUTRAL" : SabberStoneCore.Model.Cards.FromId(hero).Class.ToString();
            var mastery = new List<LocalFeat>();
            foreach (var card in pool[cardClass]) {
                string id = card.Id;
                counts.TryGetValue(id, out int points);
                var tiers = LocalCardMastery.Tiers(id, points, GrindTiers.Find(profile.GrindTier)).ToArray();
                if (tiers.Length == 0) continue;
                Func<LocalMasteryProgress, bool> done = tier => profile.MasteryGrants.Any(row => row.CardId == id && row.Premium == tier.Premium)
                    || LocalClassRewards.ProgressionAtLeast(profile, id, tier.Premium) >= LocalCrafting.Playset(id);
                var next = tiers.FirstOrDefault(tier => !done(tier));
                bool earned = next == null;
                next = next ?? tiers.Last();
                var feat = Track(checked(MasteryIdBase + card.AssetId * 4 + next.Premium), "mastery:" + id,
                    card.Name + " — " + new[] { "", "Golden", "Diamond", "Signature" }[next.Premium],
                    "Win with this card in your personal Ranked or Casual deck (at least four turns; no concessions).",
                    next.Wins, next.RequiredWins, earned, new[] { Card(id, next.Premium, LocalCrafting.Playset(id)) },
                    date: dates.TryGetValue(id, out long date) ? date : 0);
                feat.Summary += "\n" + next.Wins + " / " + next.RequiredWins + " wins";
                mastery.Add(feat);
            }
            AddTrack(categories.Single(row => row.Id == "collection"), all, 12 + index, "mastery:" + index,
                (hero == null ? "Neutral" : LocalCoreProgression.ClassName(hero)), "Card Mastery", mastery);
        }
        foreach (var feat in all.Where(row => !row.Generated && !row.Earned)) {
            int premium = feat.Id == "first-signature-mastery" ? 3 : feat.Id == "first-diamond-mastery" ? 2 : feat.Id == "fifty-golden-masteries" ? 1 : 0;
            if (premium != 0) feat.Progress = Math.Min(feat.Quota, profile.MasteryGrants.Count(row => row.Premium == premium && row.Copies > 0));
        }
        foreach (var category in categories) category.Stats = LocalFeatStats.Of(category.Subcategories.SelectMany(sub => sub.Feats));
    }

    private static void AddTrack(LocalFeatCategory category, List<LocalFeat> all, int index, string id, string name, string section, List<LocalFeat> feats)
    {
        feats = section == "Card Mastery"
            // Native sorts by status before SortOrder, so earned rows stay last like other tracks.
            ? feats.OrderBy(feat => feat.Earned).ThenByDescending(feat => feat.Progress > 0)
                .ThenBy(feat => Definition(feat.Cards[0].CardId).Cost)
                .ThenBy(feat => Definition(feat.Cards[0].CardId).Name, StringComparer.Ordinal).ThenBy(feat => feat.NativeId).ToList()
            : feats.OrderBy(feat => feat.Earned).ThenByDescending(feat => (double)feat.Progress / Math.Max(1, feat.Quota))
                .ThenBy(feat => feat.NativeId).ToList();
        for (int i = 0; i < feats.Count; i++) { feats[i].SortOrder = i; feats[i].CategoryId = category.Id; feats[i].SubcategoryId = id; }
        category.Subcategories = category.Subcategories.Concat(new[] { new LocalFeatSubcategory { Id = id, Name = name,
            RetailIcon = "CLASSIC", NativeId = TrackSubcategoryIdBase + index, SectionNativeId = TrackSectionIdBase + index,
            SectionName = section, Feats = feats, Stats = LocalFeatStats.Of(feats) } }).ToArray();
        all.AddRange(feats);
    }

    private static int CatalogIndex(string id)
    {
        var all = LocalAchievementCatalog.All;
        for (int i = 0; i < all.Count; i++) if (all[i].Id == id) return i;
        throw new InvalidOperationException("Feat table names an unknown achievement: " + id);
    }

    internal static void ValidateTable()
    {
        var rows = LocalAchievementFeatTable.Categories.SelectMany(c => c.Subcategories).SelectMany(s => s.Rows).ToArray();
        var catalog = LocalAchievementCatalog.All.Select(row => row.Id).ToArray();
        if (rows.Length != catalog.Length || !rows.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(catalog.OrderBy(id => id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Feat table must list every catalog achievement exactly once.");
        if (rows.Any(r => !LocalAchievementFeatTable.PointScale.Contains(r.Points) || string.IsNullOrWhiteSpace(r.Summary)))
            throw new InvalidOperationException("Feat rows need a point value from the scale and a summary.");
        if (LocalAchievementFeatTable.Categories.Any(c => c.Subcategories.Count == 0 || c.Subcategories.Any(s => s.Rows.Count == 0)))
            throw new InvalidOperationException("Feat categories and subcategories must not be empty.");
    }

    private static void Validate(LocalAchievementState state)
    {
        if (state.CatalogVersion != LocalAchievementCatalog.Version || state.Unlocks == null || state.Grants == null
            || state.Career == null || state.Unlocks.Count != state.Grants.Count)
            throw new InvalidOperationException("Feats refused invalid achievement state.");
        var known = new HashSet<string>(LocalAchievementCatalog.All.Select(row => row.Id), StringComparer.Ordinal);
        if (state.Unlocks.Any(row => row == null || !known.Contains(row.Id) || row.CatalogVersion != LocalAchievementCatalog.Version)
            || state.Unlocks.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != state.Unlocks.Count
            || state.Grants.Any(row => !ValidGrant(row, known))
            || state.Grants.Select(row => row.AchievementId).Distinct(StringComparer.Ordinal).Count() != state.Grants.Count
            || !state.Unlocks.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(state.Grants.Select(row => row.AchievementId).OrderBy(id => id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Feats refused invalid achievement ledger.");
    }

    private static bool ValidGrant(LocalAchievementGrantReceipt row, HashSet<string> known)
    {
        if (row == null || !known.Contains(row.AchievementId)) return false;
        var expected = LocalAchievementGrantReceipt.FromCatalog(row.AchievementId);
        return row.CatalogVersion == expected.CatalogVersion && row.Gold == expected.Gold && row.Dust == expected.Dust
            && row.StandardPacks == expected.StandardPacks && row.CardBackId == expected.CardBackId
            && row.HeroSkinId == expected.HeroSkinId && row.LinkedMilestoneId == expected.LinkedMilestoneId
            && (row.CardBackId == 0 || AchievementExclusiveCosmetics.ContainsCardBack(row.CardBackId))
            && (row.HeroSkinId == null || AchievementExclusiveCosmetics.ContainsHeroSkin(row.HeroSkinId));
    }

    internal static int Quota(string id)
    {
        switch (id)
        {
            case "a-hundred-good-games": return 100;
            case "class-act": return SupportedClasses.Count;
            case "well-read": return 250;
            case "the-board-is-a-workplace-hazard": return 1000;
            case "world-tour": return 25;
            case "fifty-golden-masteries": return 50;
            default: return 1;
        }
    }

    private static int RawProgress(string id, LocalAchievementCareer career)
    {
        switch (id)
        {
            case "a-hundred-good-games": return career.EligibleWins;
            case "class-act":
                var wins = (career.ClassWins ?? new List<LocalAchievementClassWins>()).Where(row => row != null && row.Hero != null)
                    .GroupBy(row => row.Hero, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Max(row => row.Wins), StringComparer.Ordinal);
                return SupportedClasses.Count(hero => wins.TryGetValue(hero, out int count) && count >= 10);
            case "well-read": return career.PlayedCollectibleBaseCardIds?.Count ?? 0;
            case "the-board-is-a-workplace-hazard": return (int)Math.Min(int.MaxValue, Math.Max(0, career.CreditedEnemyMinionDeaths));
            case "world-tour": return career.DefeatedNpcIds?.Count ?? 0;
            // made-to-measure is a per-match condition (ten crafted cards in the winning deck): a 0/1 bar, not an owned count.
            default: return 0;
        }
    }

    private static long UnixSeconds(string utc) =>
        DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when)
            ? when.ToUnixTimeSeconds() : 0;

    // Full text for the native inspection popup: condition, reward, progress and eligibility.
    internal string Detail(LocalFeat feat)
    {
        string progress = feat.Quota > 1 ? "\nProgress: " + feat.Progress + " / " + feat.Quota + (feat.Id.StartsWith("mastery:", StringComparison.Ordinal) ? " wins" : "") : "";
        return feat.Condition + progress + "\nReward: " + feat.RewardText + "\n"
            + (feat.Generated ? "Granted automatically through progression. No claim needed." : Eligibility(feat.Id));
    }

    internal static string Eligibility(string id)
    {
        if (id == "the-complete-works")
            return "Collection completion uses durable owned cards. Tokens and temporarily loaned cards do not count.";
        if (id == "welcome-to-legend" || id == "among-the-hundred" || id == "there-can-be-only-one")
            return "Linked ladder milestones use settled ranked results. Playtests do not count.";
        return "Eligible world matches count. Your concessions and playtests do not count.";
    }
}
