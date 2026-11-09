using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if ENGINE_SOURCE
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;
using EngineZone = SabberStoneCore.Enums.Zone;
#endif

internal sealed class LocalAchievementDefinition
{
    internal string Id { get; }
    internal string Name { get; }
    internal string Condition { get; }
    internal LocalAchievementReward Reward { get; }

    internal LocalAchievementDefinition(string id, string name, string condition, LocalAchievementReward reward)
    {
        Id = id; Name = name; Condition = condition; Reward = reward;
    }

    internal LocalAchievementDefinition Copy() => new LocalAchievementDefinition(Id, Name, Condition, Reward.Copy());
}

internal sealed class LocalAchievementReward
{
    internal int Gold { get; }
    internal int Dust { get; }
    internal int StandardPacks { get; }
    internal int CardBackId { get; }
    internal string HeroSkinId { get; }
    internal string LinkedMilestoneId { get; }
    internal int GrantKindCount => (Gold != 0 ? 1 : 0) + (Dust != 0 ? 1 : 0) + (StandardPacks != 0 ? 1 : 0)
        + (CardBackId != 0 ? 1 : 0) + (HeroSkinId != null ? 1 : 0) + (LinkedMilestoneId != null ? 1 : 0);

    internal LocalAchievementReward(int gold = 0, int dust = 0, int standardPacks = 0, int cardBackId = 0,
        string heroSkinId = null, string linkedMilestoneId = null)
    {
        Gold = gold; Dust = dust; StandardPacks = standardPacks; CardBackId = cardBackId;
        HeroSkinId = heroSkinId; LinkedMilestoneId = linkedMilestoneId;
    }

    internal LocalAchievementReward Copy() => new LocalAchievementReward(Gold, Dust, StandardPacks, CardBackId,
        HeroSkinId, LinkedMilestoneId);
}

internal static class LocalAchievementCatalog
{
    internal const int Version = 1;
    private static readonly LocalAchievementDefinition[] Definitions = {
        A("calculated", "Calculated.", "Win with exactly 1 Health and 0 Armor after the final resolution.", cardBackId: 519),
        A("out-of-cards-not-ideas", "Out of Cards, Not Ideas", "Win while both your deck and hand contain 0 cards after the final resolution.", cardBackId: 516),
        A("seven-card-pickup", "Seven-Card Pickup", "One spell you play destroys all seven enemy minions that were on board when it began resolving; none may leave through bounce, transform, control change, or silence-only removal.", gold: 200),
        A("thanks-ill-take-it-from-here", "Thanks, I’ll Take It From Here", "A minion that first entered play under opposing control, then changed to your control, attacks and deals direct lethal to the opposing hero. It must be that same entity, not a generated copy.", cardBackId: 166),
        A("mutually-assured-destruction", "Mutually Assured Destruction", "A single resolution destroys both heroes and ends the match in a natural draw. Turn-limit draws do not count.", cardBackId: 452),
        A("aggressively-average", "Aggressively Average", "Defeat a queued Diamond/Legend world NPC with a legal personal deck and sideboards containing only Free/Common cards.", standardPacks: 3),
        A("call-an-ambulance", "Call an Ambulance…", "Begin your turn at 1–5 Health and 0 Armor against an enemy at 25+ Health, then win during that turn.", gold: 200),
        A("fine-ill-do-it-myself", "Fine, I’ll Do It Myself", "Deal lethal with your hero’s attack in a match where you played no minion cards. Summons from non-minion cards are allowed.", dust: 150),
        A("absolutely-unnecessary", "Absolutely Unnecessary", "Win with a single damage event dealing at least 30 damage beyond the enemy hero’s remaining Health plus Armor. Prevented damage and destruction effects do not count.", gold: 200),
        A("death-knight-grave-expenditure", "Death Knight — Grave Expenditure", "Spend at least 30 Corpses in one match, then win.", heroSkinId: "HERO_11n"),
        A("demon-hunter-face-value", "Demon Hunter — Face Value", "Your hero attacks on five different turns and delivers the final lethal attack.", heroSkinId: "HERO_10bm"),
        A("druid-ahead-of-the-curve", "Druid — Ahead of the Curve", "Reach at least 10 maximum Mana while the opponent has no more than 5 maximum Mana, then win. Temporary spendable Mana does not count.", heroSkinId: "HERO_06k"),
        A("hunter-whos-a-good-boy", "Hunter — Who’s a Good Boy?", "A Beast from your starting deck survives an opposing turn on your board, then delivers lethal with at least 20 Attack.", heroSkinId: "HERO_05ao"),
        A("mage-final-exam", "Mage — Final Exam", "Cast ten spells in one turn, then win during that turn. Automatic recasts and effects that cast cards for you do not count as your plays.", heroSkinId: "HERO_08f"),
        A("paladin-entry-level-position", "Paladin — Entry-Level Position", "A Silver Hand Recruit delivers lethal with at least 15 Attack. It must still be a Recruit, not a transformed minion.", heroSkinId: "HERO_04am_Uther"),
        A("priest-we-can-rebuild-you", "Priest — We Can Rebuild You", "Fall to 5 or less Health, subsequently restore at least 25 actual Health to your hero, and win at full Health. Armor and overhealing do not count.", heroSkinId: "HERO_09bc"),
        A("rogue-combo-meal", "Rogue — Combo Meal", "Play and activate four Combo cards in one turn, then win. Unactivated Combo text does not count.", heroSkinId: "HERO_03i"),
        A("shaman-the-whole-band", "Shaman — The Whole Band", "Control seven Totems at once, including each of the four basic Hero Power Totems, then win.", heroSkinId: "HERO_02k"),
        A("warlock-damage-is-a-resource", "Warlock — Damage Is a Resource", "Take at least 30 actual damage to your hero from your own cards or Hero Power during a match, then win at 10+ Health. Health payments are not damage.", heroSkinId: "HERO_07e"),
        A("warrior-good-stats-for-the-cost", "Warrior — Good Stats for the Cost", "A Petrified Ogre from your starting deck awakens, survives a complete opposing turn while awake on your board, then delivers the match-winning lethal attack.", heroSkinId: "HERO_01ap_Boulderfist_h"),
        A("no-copies-no-problem", "No Copies, No Problem", "Defeat a queued Diamond/Legend world NPC with no repeated base card identity anywhere in your starting main deck or sideboards. Cosmetic versions and equivalent reprints are the same identity.", dust: 200),
        A("creature-feature", "Creature Feature", "Defeat a queued Diamond/Legend world NPC with a legal personal starting deck and sideboards containing only minion cards.", standardPacks: 2),
        A("words-not-violence", "Words, Not Violence", "Defeat a queued Diamond/Legend world NPC with no minion cards in your personal starting deck or sideboards. Summons from other card types are allowed.", dust: 200),
        A("no-famous-people", "No Famous People", "Beat a queued Legend world NPC with no Legendary cards in your personal starting deck or sideboards.", standardPacks: 2),
        A("home-team", "Home Team", "Defeat a queued Diamond/Legend world NPC with no Neutral cards in your personal starting deck or sideboards.", gold: 150),
        A("neutral-ground", "Neutral Ground", "Defeat a queued Diamond/Legend world NPC with only Neutral cards in your personal starting deck and sideboards. Your hero and starting Hero Power are exempt.", standardPacks: 2),
        A("small-ball", "Small Ball", "Defeat a queued Diamond/Legend world NPC with every card in your personal starting deck and sideboards costing at most 3 at base cost.", dust: 200),
        A("heavy-reading", "Heavy Reading", "Defeat a queued Diamond/Legend world NPC with at least 15 cards in your starting main deck costing 7+ at base cost.", dust: 200),
        A("one-expansion-stand", "One Expansion Stand", "Defeat a queued Diamond/Legend world NPC with at least 20 main-deck cards from one expansion; every other main-deck/sideboard card must be from that same expansion or Core.", standardPacks: 2),
        A("perfect-curve", "Perfect Curve", "Defeat a queued Diamond/Legend world NPC with at least two starting main-deck cards at each base Mana cost from 1 through 7.", gold: 200),
        A("respectfully-no", "Respectfully, No.", "Counter an opposing spell with a base cost of at least 8, then win. Redirecting, preventing damage, or removing its target does not count as countering it.", gold: 150),
        A("from-downtown", "From Downtown", "Begin your turn with no friendly minions and an opposing hero at 30+ Health plus Armor, then win during that turn.", gold: 200),
        A("armored-division", "Armored Division", "Gain 50 Armor in a match and win with at least 25 Armor remaining.", dust: 200),
        A("untouchable", "Untouchable", "Win without your hero ever losing Health after the match began, against a Diamond/Legend NPC. Armor loss is allowed; player Health costs invalidate it.", dust: 200),
        A("one-of-everything", "One of Everything", "Simultaneously control at least six minions that can represent six different minion types, then win. Each minion supplies at most one distinct type, including dual-type/All minions.", dust: 200),
        A("family-reunion", "Family Reunion", "Simultaneously control seven minions with different base card identities sharing one minion type, then win. Seven identical tokens do not count.", dust: 200),
        A("topdecking-is-a-skill", "Topdecking Is a Skill", "Begin your turn with an empty hand; the normal turn-start draw is a spell you immediately play as your first action, and that spell deals direct lethal. No additional draws or intervening player actions.", standardPacks: 2),
        A("hostile-takeover", "Hostile Takeover", "Simultaneously control three minion entities that first entered play under opposing control and subsequently changed to your control, then win. Generated copies do not count.", standardPacks: 2),
        A("no-time-to-explain", "No Time to Explain", "Win before your fifth turn begins against a queued Diamond/Legend NPC. A genuine lethal or NPC concession qualifies; the normal short-match currency rule stays independent.", gold: 150),
        A("running-on-fumes", "Running on Fumes", "Take at least 10 actual fatigue damage to your hero during a match and win. Prevented damage and damage absorbed entirely by Armor do not count.", dust: 200),
        A("population-boom", "Population Boom", "Summon 15 friendly minions in one turn, then win. Each entry into play counts, including resummons; transform and control change do not.", standardPacks: 2),
        A("acceptable-losses", "Acceptable Losses", "Your own effects destroy at least seven friendly minions during one turn, then you win. Combat deaths from attacking and enemy-caused deaths do not count.", dust: 200),
        A("no-touching", "No Touching", "One spell you play Freezes at least five enemy minions that were not already Frozen, then you win during that turn.", dust: 200),
        A("chain-reaction", "Chain Reaction", "Resolve eight friendly Deathrattles from at least four different minion entities in one turn, then win. Each Deathrattle activation counts once; suppressed Deathrattles do not count.", gold: 150),
        A("power-trip", "Power Trip", "Deal at least 20 damage to the opposing hero with your Hero Power during a match, then win. Hero attacks, spawned minions, and delayed independent enchantments do not count.", dust: 200),
        A("pocket-sized-problem", "Pocket-Sized Problem", "An attacking minion with exactly 1 Attack deals exact lethal to the opposing hero. Triggered damage or another effect finishing the opponent does not qualify.", gold: 150),
        A("not-today", "Not Today", "A friendly minion that returned via Reborn survives the following opposing turn, then delivers a lethal attack. A different copy, resurrection, or transformation does not count.", standardPacks: 2),
        A("special-delivery", "Special Delivery", "Your card effects cause the opponent to attempt at least three draws into a full hand in one turn, burning those cards, then you win. Enemy-initiated draws do not count.", dust: 200),
        A("borrowed-time", "Borrowed Time", "Win during an actual extra turn granted to you by a card effect. Ordinary skipped opponent actions or an opponent concession before the extra turn starts do not count.", gold: 200),
        A("return-to-sender", "Return to Sender", "A spell that previously belonged to the opposing hand or deck and was actually transferred to you deals direct lethal. Created copies and stolen-minion attacks do not qualify.", gold: 150),
        A("a-hundred-good-games", "A Hundred Good Games", "Win 100 eligible world matches over your career. Losses never reset progress.", standardPacks: 3),
        A("class-act", "Class Act", "Win ten eligible world matches with each of the eleven starting hero classes. Later hero transformations do not change class credit.", standardPacks: 5),
        A("well-read", "Well Read", "Play 250 different collectible base card identities across eligible matches. Equivalent reprints and premium versions count once; tokens and automatic casts do not count.", dust: 400),
        A("the-board-is-a-workplace-hazard", "The Board Is a Workplace Hazard", "Defeat 1,000 enemy minions across eligible matches. Credit only deaths caused by your attacks/effects; one death occurrence counts once, and revived minions can die again. Enemy sacrifices, hand burns, bounces, and control changes do not count.", cardBackId: 138),
        A("the-complete-works", "The Complete Works", "Own a playable set of every collectible card in one supported non-Core expansion. Combine owned premium/normal copies up to each card’s playable limit; exclude tokens and temporarily loaned cards.", cardBackId: 189),
        A("world-tour", "World Tour", "Defeat 25 different named world NPC identities. Names changing and rematches do not add distinct opponents.", standardPacks: 3),
        A("made-to-measure", "Made to Measure", "Beat a queued Diamond/Legend NPC using a personal deck containing at least ten different card identities you previously crafted and still own. The deck must use those identities; copying an imported recipe does not bypass ownership checks.", dust: 400),
        A("welcome-to-legend", "Welcome to Legend", "Reach Legend for the first time.", linkedMilestoneId: "legend"),
        A("among-the-hundred", "Among the Hundred", "Finish a settled ranked match at Legend rank 100 or better for the first time.", linkedMilestoneId: "legend:top-100"),
        A("there-can-be-only-one", "There Can Be Only One", "Finish a settled ranked match at Legend rank 1 for the first time.", linkedMilestoneId: "legend:1"),
        A("first-signature-mastery", "Signed and Sealed", "Unlock your first Signature card through card mastery."),
        A("first-diamond-mastery", "Diamond Standard", "Unlock your first Diamond card through card mastery."),
        A("fifty-golden-masteries", "Golden Library", "Unlock 50 Golden cards through card mastery."),
        A("all-premium-deck", "All That Glitters", "Win an eligible match of at least four turns with a personal deck whose every card is premium.")
    };

    internal static IReadOnlyList<LocalAchievementDefinition> All => Definitions.Select(x => x.Copy()).ToArray();

    internal static LocalAchievementDefinition Get(string id)
    {
        var definition = Definitions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        return definition?.Copy();
    }

    private static LocalAchievementDefinition A(string id, string name, string condition, int gold = 0, int dust = 0,
        int standardPacks = 0, int cardBackId = 0, string heroSkinId = null, string linkedMilestoneId = null)
        => new LocalAchievementDefinition(id, name, condition,
            new LocalAchievementReward(gold, dust, standardPacks, cardBackId, heroSkinId, linkedMilestoneId));
}

public sealed class LocalAchievementState
{
    public int CatalogVersion;
    public List<LocalAchievementUnlock> Unlocks = new List<LocalAchievementUnlock>();
    public List<LocalAchievementGrantReceipt> Grants = new List<LocalAchievementGrantReceipt>();
    public LocalAchievementCareer Career = new LocalAchievementCareer();
}

public sealed class LocalAchievementUnlock
{
    public string Id;
    public int CatalogVersion;
    public string UnlockedUtc;
    public string MatchId;
    public string Provenance;
    public LocalAchievementWitnessRecord Witness;
}

public sealed class LocalAchievementWitnessRecord
{
    public string Kind;
    public long Sequence;
    public long ResolutionId;
    public long CastId;
    public long AttackId;
    public int EntityId;
    public long Incarnation;
    public int Turn;
    public long Value;
    public string Identity;
    public List<int> EntityIds = new List<int>();
    public List<string> BaseCardIds = new List<string>();
}

public sealed class LocalAchievementGrantReceipt
{
    public string AchievementId;
    public int CatalogVersion;
    public int Gold;
    public int Dust;
    public int StandardPacks;
    public int CardBackId;
    public string HeroSkinId;
    public string LinkedMilestoneId;

    internal static LocalAchievementGrantReceipt FromCatalog(string achievementId)
    {
        var definition = LocalAchievementCatalog.Get(achievementId)
            ?? throw new ArgumentException("Unknown achievement id.", nameof(achievementId));
        return new LocalAchievementGrantReceipt {
            AchievementId = definition.Id,
            CatalogVersion = LocalAchievementCatalog.Version,
            Gold = definition.Reward.Gold,
            Dust = definition.Reward.Dust,
            StandardPacks = definition.Reward.StandardPacks,
            CardBackId = definition.Reward.CardBackId,
            HeroSkinId = definition.Reward.HeroSkinId,
            LinkedMilestoneId = definition.Reward.LinkedMilestoneId
        };
    }
}

public sealed class LocalAchievementCareer
{
    public int EligibleWins;
    public List<LocalAchievementClassWins> ClassWins = new List<LocalAchievementClassWins>();
    public List<string> PlayedCollectibleBaseCardIds = new List<string>();
    public long CreditedEnemyMinionDeaths;
    public List<string> DefeatedNpcIds = new List<string>();
    public List<string> CraftedBaseCardIds = new List<string>();
    public string CompletedExpansion;
    public int CompletedExpansionCatalogVersion;
}

public sealed class LocalAchievementClassWins
{
    public string Hero;
    public int Wins;
}

public sealed class LocalAchievementPendingContext
{
    public string MatchId;
    public bool EligibleWorldMatch;
    public bool PersonalDeck;
    public string StartingHero;
    public string StartingHeroClass;
    public List<LocalAchievementDeckCard> MainDeck = new List<LocalAchievementDeckCard>();
    public List<LocalAchievementDeckCard> Sideboards = new List<LocalAchievementDeckCard>();
    public List<string> CraftedAndOwnedBaseCardIds = new List<string>();
    public string QueuedNpcId;
    public double QueuedNpcRating;
    public string QueuedNpcBand;
}

public sealed class LocalAchievementDeckCard
{
    public string BaseCardId;
    public int Copies = 1;
    public string CardType;
    public string CardClass;
    public int BaseCost;
    public string Rarity;
    public string CardSet;
    public int Premium;
    public bool ShouldSerializePremium() => Premium != 0;
}

internal static class LocalAchievementContextBinding
{
    internal static string Create(LocalAchievementPendingContext pending)
    {
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        var text = new StringBuilder();
        Add(text, pending.MatchId); Add(text, pending.EligibleWorldMatch ? "1" : "0");
        Add(text, pending.PersonalDeck ? "1" : "0"); Add(text, pending.StartingHero); Add(text, pending.StartingHeroClass);
        Add(text, pending.QueuedNpcId); Add(text, pending.QueuedNpcRating.ToString("R", CultureInfo.InvariantCulture));
        Add(text, pending.QueuedNpcBand);
        Cards(text, pending.MainDeck); Cards(text, pending.Sideboards);
        foreach (var id in (pending.CraftedAndOwnedBaseCardIds ?? new List<string>()).OrderBy(x => x, StringComparer.Ordinal)) Add(text, id);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    private static void Cards(StringBuilder text, IEnumerable<LocalAchievementDeckCard> cards)
    {
        var rows = (cards ?? Enumerable.Empty<LocalAchievementDeckCard>()).ToArray();
        Add(text, rows.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var card in rows)
        {
            if (card == null) { Add(text, null); continue; }
            Add(text, card.BaseCardId); Add(text, card.Copies.ToString(CultureInfo.InvariantCulture)); Add(text, card.CardType);
            Add(text, card.CardClass); Add(text, card.BaseCost.ToString(CultureInfo.InvariantCulture)); Add(text, card.Rarity); Add(text, card.CardSet);
            if (card.Premium != 0) { Add(text, "premium"); Add(text, card.Premium.ToString(CultureInfo.InvariantCulture)); }
        }
    }

    private static void Add(StringBuilder text, string value)
    {
        value = value ?? string.Empty;
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}

public sealed class LocalAchievementMatchEvidence
{
    internal string ContextBinding { get; }
    internal string MatchId { get; }
    internal string Result { get; }
    internal string StartingHero { get; }
    internal string OpponentNpcId { get; }
    internal string OpponentBand { get; }
    internal bool PlayerConceded { get; }
    internal bool OpponentConceded { get; }
    internal bool NaturalDraw { get; }
    internal bool Available { get; }
    internal string Fingerprint { get; }
    internal string SourceFingerprint { get; }
    internal int HumanControllerId { get; }
    internal int OpponentControllerId { get; }
    internal int HumanHeroId { get; }
    internal int OpponentHeroId { get; }
    // Live engine evidence failed validation: the match settles normally but earns no achievements or career progress.
    internal bool EvidenceInvalid => InvalidReason != null;
    internal string InvalidReason { get; private set; }
#if ENGINE_SOURCE
    internal IReadOnlyList<HistoryFact> Facts { get; }
#endif

    internal LocalAchievementMatchEvidence(string matchId, string result, string startingHero, string opponentNpcId,
        string opponentBand, bool playerConceded, bool opponentConceded, bool naturalDraw)
        : this(matchId, result, startingHero, opponentNpcId, opponentBand, playerConceded, opponentConceded,
            naturalDraw, null, null, false, 0, 0, 0, 0
#if ENGINE_SOURCE
            , Array.Empty<HistoryFact>()
#endif
            ) { }

    internal LocalAchievementMatchEvidence(string matchId, string result, string startingHero, string opponentNpcId,
        string opponentBand, bool playerConceded, bool opponentConceded, bool naturalDraw, bool available,
        int humanControllerId, int opponentControllerId, int humanHeroId, int opponentHeroId
#if ENGINE_SOURCE
        , IEnumerable<HistoryFact> facts
#endif
        ) : this(matchId, result, startingHero, opponentNpcId, opponentBand, playerConceded, opponentConceded,
            naturalDraw, null, null, available, humanControllerId, opponentControllerId, humanHeroId, opponentHeroId
#if ENGINE_SOURCE
            , facts
#endif
            ) { }

    internal LocalAchievementMatchEvidence(string matchId, string result, string startingHero, string opponentNpcId,
        string opponentBand, bool playerConceded, bool opponentConceded, bool naturalDraw, string contextBinding,
        string sourceFingerprint, bool available,
        int humanControllerId, int opponentControllerId, int humanHeroId, int opponentHeroId
#if ENGINE_SOURCE
        , IEnumerable<HistoryFact> facts
#endif
        )
    {
        ContextBinding = contextBinding; MatchId = matchId; Result = result; StartingHero = startingHero; OpponentNpcId = opponentNpcId;
        OpponentBand = opponentBand; PlayerConceded = playerConceded; OpponentConceded = opponentConceded;
        NaturalDraw = naturalDraw; Available = available; HumanControllerId = humanControllerId;
        OpponentControllerId = opponentControllerId; HumanHeroId = humanHeroId; OpponentHeroId = opponentHeroId;
        SourceFingerprint = sourceFingerprint;
        Fingerprint = available ? LocalAchievementFingerprint.Create(contextBinding, sourceFingerprint, result, startingHero,
            opponentNpcId, opponentBand, playerConceded, opponentConceded, naturalDraw) : null;
#if ENGINE_SOURCE
        Facts = Array.AsReadOnly((facts ?? Enumerable.Empty<HistoryFact>()).ToArray());
#endif
    }

    internal LocalAchievementMatchEvidence WithConcessions(bool playerConceded, bool opponentConceded) =>
        new LocalAchievementMatchEvidence(MatchId, Result, StartingHero, OpponentNpcId, OpponentBand,
            playerConceded, opponentConceded, NaturalDraw, ContextBinding, SourceFingerprint, Available, HumanControllerId, OpponentControllerId,
            HumanHeroId, OpponentHeroId
#if ENGINE_SOURCE
            , Facts
#endif
            ) { InvalidReason = InvalidReason };

    internal LocalAchievementMatchEvidence Invalidated(string reason)
    {
        var copy = WithConcessions(PlayerConceded, OpponentConceded);
        copy.InvalidReason = string.IsNullOrEmpty(reason) ? "invalid achievement evidence" : reason;
        return copy;
    }
}

internal static class LocalAchievementFingerprint
{
    internal static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", "");
    }

    internal static string Create(string context, string source, string result, string hero, string npc, string band,
        bool playerConceded, bool opponentConceded, bool naturalDraw)
    {
        var text = new StringBuilder();
        foreach (string value in new[] { context, source, result, hero, npc, band,
            playerConceded ? "1" : "0", opponentConceded ? "1" : "0", naturalDraw ? "1" : "0" })
        {
            string item = value ?? string.Empty;
            text.Append(item.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item).Append(';');
        }
        return Hash(text.ToString());
    }
}

internal abstract class LocalAchievementWitness
{
    internal long Sequence { get; }
    protected LocalAchievementWitness(long sequence) { Sequence = sequence; }
}

internal sealed class LocalAchievementTerminalWitness : LocalAchievementWitness
{
    internal LocalAchievementTerminalWitness(long sequence) : base(sequence) { }
}

internal sealed class LocalAchievementResolutionWitness : LocalAchievementWitness
{
    internal long ResolutionId { get; }
    internal long CastId { get; }
    internal IReadOnlyList<int> EntityIds { get; }
    internal LocalAchievementResolutionWitness(long sequence, long resolutionId, long castId, IEnumerable<int> entities)
        : base(sequence) { ResolutionId = resolutionId; CastId = castId; EntityIds = Array.AsReadOnly(entities.Distinct().ToArray()); }
}

internal sealed class LocalAchievementAttackWitness : LocalAchievementWitness
{
    internal long AttackId { get; }
    internal int EntityId { get; }
    internal long Incarnation { get; }
    internal LocalAchievementAttackWitness(long sequence, long attackId, int entityId, long incarnation)
        : base(sequence) { AttackId = attackId; EntityId = entityId; Incarnation = incarnation; }
}

internal sealed class LocalAchievementTurnWitness : LocalAchievementWitness
{
    internal int Turn { get; }
    internal LocalAchievementTurnWitness(long sequence, int turn) : base(sequence) { Turn = turn; }
}

internal sealed class LocalAchievementEntityWitness : LocalAchievementWitness
{
    internal int EntityId { get; }
    internal long Incarnation { get; }
    internal LocalAchievementEntityWitness(long sequence, int entityId, long incarnation)
        : base(sequence) { EntityId = entityId; Incarnation = incarnation; }
}

internal sealed class LocalAchievementDeckWitness : LocalAchievementWitness
{
    internal IReadOnlyList<string> BaseCardIds { get; }
    internal LocalAchievementDeckWitness(IEnumerable<string> ids) : base(0)
        { BaseCardIds = Array.AsReadOnly(ids.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()); }
}

internal sealed class LocalAchievementCareerWitness : LocalAchievementWitness
{
    internal long Value { get; }
    internal string Identity { get; }
    internal LocalAchievementCareerWitness(long value, string identity = null) : base(0) { Value = value; Identity = identity; }
}

internal sealed class LocalAchievementThresholdWitness : LocalAchievementWitness
{
    internal long Value { get; }
    internal LocalAchievementThresholdWitness(long sequence, long value) : base(sequence) { Value = value; }
}

internal sealed class LocalAchievementCandidate
{
    internal string Id { get; }
    internal LocalAchievementWitness Witness { get; }
    internal LocalAchievementCandidate(string id, LocalAchievementWitness witness)
    { Id = id; Witness = witness ?? throw new ArgumentNullException(nameof(witness)); }
}

internal sealed class LocalAchievementCareerDelta
{
    internal int EligibleWins { get; }
    internal string WinningHeroClass { get; }
    internal IReadOnlyList<string> PlayedCollectibleBaseCardIds { get; }
    internal long CreditedEnemyMinionDeaths { get; }
    internal string DefeatedNpcId { get; }
    internal LocalAchievementCareerDelta(int wins, string heroClass, IEnumerable<string> played, long deaths, string npc)
    {
        EligibleWins = wins; WinningHeroClass = heroClass;
        PlayedCollectibleBaseCardIds = Array.AsReadOnly((played ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray());
        CreditedEnemyMinionDeaths = deaths; DefeatedNpcId = npc;
    }
    internal static readonly LocalAchievementCareerDelta Empty = new LocalAchievementCareerDelta(0, null, null, 0, null);
}

internal sealed class LocalAchievementEvaluation
{
    internal IReadOnlyList<LocalAchievementCandidate> Earned { get; }
    internal LocalAchievementCareerDelta CareerDelta { get; }
    internal LocalAchievementEvaluation(IEnumerable<LocalAchievementCandidate> earned, LocalAchievementCareerDelta delta)
    { Earned = Array.AsReadOnly(earned.ToArray()); CareerDelta = delta; }
}

internal sealed class LocalAchievementLedgerContext
{
    internal string CompletedExpansion { get; }
    internal int CompletedExpansionCatalogVersion { get; }
    internal IReadOnlyCollection<string> MilestoneIds { get; }
    internal int GoldenMasteries, SignatureMasteries, DiamondMasteries, Turns;
    internal LocalAchievementLedgerContext(string expansion = null, int catalogVersion = 0, IEnumerable<string> milestones = null, IEnumerable<LocalMasteryGrant> mastery = null, int turns = 0)
    {
        Turns = turns;
        var grants = (mastery ?? Enumerable.Empty<LocalMasteryGrant>()).Where(row => row.Copies > 0).ToArray();
        GoldenMasteries = grants.Count(row => row.Premium == 1); SignatureMasteries = grants.Count(row => row.Premium == 3);
        DiamondMasteries = grants.Count(row => row.Premium == 2);
        CompletedExpansion = expansion; CompletedExpansionCatalogVersion = catalogVersion;
        MilestoneIds = Array.AsReadOnly((milestones ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray());
    }
    internal static readonly LocalAchievementLedgerContext Empty = new LocalAchievementLedgerContext();
}

internal static class LocalAchievementEvaluator
{
    internal static LocalAchievementEvaluation Evaluate(LocalAchievementPendingContext pending,
        LocalAchievementMatchEvidence evidence, LocalAchievementCareer prior, IReadOnlyCollection<string> alreadyUnlocked) =>
        Evaluate(pending, evidence, prior, alreadyUnlocked, LocalAchievementLedgerContext.Empty);

    internal static LocalAchievementEvaluation Evaluate(LocalAchievementPendingContext pending,
        LocalAchievementMatchEvidence evidence, LocalAchievementCareer prior, IReadOnlyCollection<string> alreadyUnlocked,
        LocalAchievementLedgerContext ledger)
    {
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        if (evidence == null) throw new ArgumentNullException(nameof(evidence));
        if (prior == null) throw new ArgumentNullException(nameof(prior));
        if (alreadyUnlocked == null) throw new ArgumentNullException(nameof(alreadyUnlocked));
        ledger = ledger ?? LocalAchievementLedgerContext.Empty;
        if (!string.Equals(pending.MatchId, evidence.MatchId, StringComparison.Ordinal)
            || !string.Equals(pending.StartingHero, evidence.StartingHero, StringComparison.Ordinal)
            || !string.Equals(pending.QueuedNpcId, evidence.OpponentNpcId, StringComparison.Ordinal)
            || !string.Equals(pending.QueuedNpcBand, evidence.OpponentBand, StringComparison.Ordinal))
            throw new InvalidOperationException("Achievement evidence does not match pending context.");
        if (evidence.Available && !string.Equals(evidence.ContextBinding, LocalAchievementContextBinding.Create(pending), StringComparison.Ordinal))
            throw new InvalidOperationException("Achievement evidence is not bound to the frozen pending context.");
        if (!pending.EligibleWorldMatch || !evidence.Available || evidence.EvidenceInvalid || evidence.PlayerConceded
            || (evidence.Result != "Won" && evidence.Result != "Lost" && evidence.Result != "Tied"))
            return new LocalAchievementEvaluation(Array.Empty<LocalAchievementCandidate>(), LocalAchievementCareerDelta.Empty);
#if ENGINE_SOURCE
        var context = new EvaluationContext(pending, evidence, prior, ledger);
        var unlocked = new HashSet<string>(alreadyUnlocked, StringComparer.Ordinal);
        var earned = new List<LocalAchievementCandidate>();
        foreach (var definition in LocalAchievementCatalog.All)
        {
            if (unlocked.Contains(definition.Id)) continue;
            var witness = context.Witness(definition.Id);
            if (witness != null) earned.Add(new LocalAchievementCandidate(definition.Id, witness));
        }
        return new LocalAchievementEvaluation(earned, context.Delta);
#else
        return new LocalAchievementEvaluation(Array.Empty<LocalAchievementCandidate>(), LocalAchievementCareerDelta.Empty);
#endif
    }

#if ENGINE_SOURCE
    private sealed class EvaluationContext
    {
        private readonly LocalAchievementPendingContext pending;
        private readonly LocalAchievementMatchEvidence evidence;
        private readonly LocalAchievementCareer prior;
        private readonly LocalAchievementLedgerContext ledger;
        private readonly HistoryFact[] facts;
        private readonly MatchTerminalFact terminal;
        private readonly BoardTimeline board;
        private readonly Dictionary<long, DamageResolvedHistoryFact> damageBySequence;
        private readonly HashSet<string> afterPlayed;
        private readonly Dictionary<string, int> afterClassWins;
        private readonly HashSet<string> afterNpcs;
        internal LocalAchievementCareerDelta Delta { get; }
        private bool Won => evidence.Result == "Won";
        private int Human => evidence.HumanControllerId;
        private int Opponent => evidence.OpponentControllerId;

        internal EvaluationContext(LocalAchievementPendingContext pending, LocalAchievementMatchEvidence evidence,
            LocalAchievementCareer prior, LocalAchievementLedgerContext ledger)
        {
            this.pending = pending; this.evidence = evidence; this.prior = prior; this.ledger = ledger;
            facts = evidence.Facts.ToArray();
            terminal = facts.OfType<MatchTerminalFact>().Single();
            board = new BoardTimeline(facts, Human);
            damageBySequence = facts.OfType<DamageResolvedHistoryFact>().ToDictionary(x => x.Sequence);
            var played = facts.OfType<CardPlayedFact>().Where(x => x.ControllerId == Human && x.DefinitionKnown)
                .Select(x => x.Definition.Materialize()).Where(x => x.Collectible).Select(x => LocalCardCatalog.CopyGroup(x.Id));
            long creditedDeaths = facts.OfType<MinionDiedFact>().LongCount(CreditedEnemyDeath);
            int wins = Won ? 1 : 0;
            string heroClass = wins == 0 ? null : pending.StartingHeroClass;
            string npc = wins == 0 ? null : pending.QueuedNpcId;
            Delta = new LocalAchievementCareerDelta(wins, heroClass, played, creditedDeaths, npc);
            afterPlayed = new HashSet<string>(prior.PlayedCollectibleBaseCardIds ?? new List<string>(), StringComparer.Ordinal);
            afterPlayed.UnionWith(Delta.PlayedCollectibleBaseCardIds);
            afterClassWins = (prior.ClassWins ?? new List<LocalAchievementClassWins>()).Where(x => x != null && x.Hero != null)
                .GroupBy(x => x.Hero, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(y => y.Wins), StringComparer.Ordinal);
            if (heroClass != null) afterClassWins[heroClass] = afterClassWins.TryGetValue(heroClass, out int count) ? count + 1 : 1;
            afterNpcs = new HashSet<string>(prior.DefeatedNpcIds ?? new List<string>(), StringComparer.Ordinal);
            if (npc != null) afterNpcs.Add(npc);
        }

        internal LocalAchievementWitness Witness(string id)
        {
            bool win = Won;
            switch (id)
            {
                case "first-signature-mastery": return ledger.SignatureMasteries >= 1 ? new LocalAchievementCareerWitness(ledger.SignatureMasteries) : null;
                case "first-diamond-mastery": return ledger.DiamondMasteries >= 1 ? new LocalAchievementCareerWitness(ledger.DiamondMasteries) : null;
                case "fifty-golden-masteries": return ledger.GoldenMasteries >= 50 ? new LocalAchievementCareerWitness(ledger.GoldenMasteries) : null;
                case "all-premium-deck": return win && ledger.Turns >= 4 && pending.PersonalDeck && MainCards().Any()
                    && Cards().All(card => card.Premium >= 1 && card.Premium <= 3) ? DeckWitness() : null;
                case "calculated": return win && HumanTerminal().Health == 1 && HumanTerminal().Armor == 0 ? TerminalWitness() : null;
                case "out-of-cards-not-ideas": return win && HumanTerminal().Hand == 0 && HumanTerminal().Deck == 0 ? TerminalWitness() : null;
                case "seven-card-pickup": return win ? SpellClear() : null;
                case "thanks-ill-take-it-from-here": return win ? StolenLethal() : null;
                case "mutually-assured-destruction": return MutualDestruction();
                case "aggressively-average": return win && DeckGate() && Cards().All(x => x.Rarity == "FREE" || x.Rarity == "COMMON") ? DeckWitness() : null;
                case "call-an-ambulance": return win ? TurnStartWin(x => x.HeroHealth >= 1 && x.HeroHealth <= 5 && x.HeroArmor == 0 && x.OpposingHeroHealth >= 25) : null;
                case "fine-ill-do-it-myself": return win && !facts.OfType<CardPlayedFact>().Any(x => x.ControllerId == Human && x.CardType == CardType.MINION) ? HeroAttackLethal() : null;
                case "absolutely-unnecessary": return win ? Overkill() : null;
                case "death-knight-grave-expenditure": return ClassWin("DEATHKNIGHT") && facts.OfType<CorpseSpentFact>().Where(x => x.ControllerId == Human).Sum(x => x.Amount) >= 30 ? Threshold(facts.OfType<CorpseSpentFact>().Last(x => x.ControllerId == Human).Sequence, 30) : null;
                case "demon-hunter-face-value": return ClassWin("DEMONHUNTER") ? HeroFiveTurnLethal() : null;
                case "druid-ahead-of-the-curve": return ClassWin("DRUID") ? ManaLead() : null;
                case "hunter-whos-a-good-boy": return ClassWin("HUNTER") ? StartingMinionSurvivalLethal(x => x.IsRace(Race.BEAST), 20, null, false) : null;
                case "mage-final-exam": return ClassWin("MAGE") ? TenSpellTurn() : null;
                case "paladin-entry-level-position": return ClassWin("PALADIN") ? RecruitLethal() : null;
                case "priest-we-can-rebuild-you": return ClassWin("PRIEST") ? PriestRecovery() : null;
                case "rogue-combo-meal": return ClassWin("ROGUE") ? FourCombos() : null;
                case "shaman-the-whole-band": return ClassWin("SHAMAN") ? SevenTotems() : null;
                case "warlock-damage-is-a-resource": return ClassWin("WARLOCK") && HumanTerminal().Health >= 10 ? SelfDamage() : null;
                case "warrior-good-stats-for-the-cost": return ClassWin("WARRIOR") ? OgreLethal() : null;
                case "no-copies-no-problem": return win && DeckGate() && Cards().GroupBy(x => LocalCardCatalog.CopyGroup(x.BaseCardId), StringComparer.Ordinal).All(x => x.Sum(y => y.Copies) == 1) ? DeckWitness() : null;
                case "creature-feature": return win && DeckGate() && Cards().All(x => x.CardType == "MINION") ? DeckWitness() : null;
                case "words-not-violence": return win && DeckGate() && Cards().All(x => x.CardType != "MINION") ? DeckWitness() : null;
                case "no-famous-people": return win && DeckGate(true) && Cards().All(x => x.Rarity != "LEGENDARY") ? DeckWitness() : null;
                case "home-team": return win && DeckGate() && Cards().All(x => x.CardClass != "NEUTRAL") ? DeckWitness() : null;
                case "neutral-ground": return win && DeckGate() && Cards().All(x => x.CardClass == "NEUTRAL") ? DeckWitness() : null;
                case "small-ball": return win && DeckGate() && Cards().All(x => x.BaseCost <= 3) ? DeckWitness() : null;
                case "heavy-reading": return win && DeckGate() && MainCards().Where(x => x.BaseCost >= 7).Sum(x => x.Copies) >= 15 ? DeckWitness() : null;
                case "one-expansion-stand": return win && DeckGate() ? OneExpansion() : null;
                case "perfect-curve": return win && DeckGate() && Enumerable.Range(1, 7).All(cost => MainCards().Where(x => x.BaseCost == cost).Sum(x => x.Copies) >= 2) ? DeckWitness() : null;
                case "respectfully-no": return win ? CounterLargeSpell() : null;
                case "from-downtown": return win ? TurnStartWin(x => x.FriendlyMinionCount == 0 && x.OpposingHeroHealth + x.OpposingHeroArmor >= 30) : null;
                case "armored-division": return win && facts.OfType<ArmorGainedFact>().Where(x => x.ControllerId == Human).Sum(x => x.Amount) >= 50 && HumanTerminal().Armor >= 25 ? Threshold(terminal.Sequence, HumanTerminal().Armor) : null;
                case "untouchable": return win && NpcGate() && !facts.OfType<HeroHealthChangedFact>().Any(x => x.ControllerId == Human && x.NewHealth < x.OldHealth) ? TerminalWitness() : null;
                case "one-of-everything": return win ? SixTypes() : null;
                case "family-reunion": return win ? SevenFamily() : null;
                case "topdecking-is-a-skill": return win ? TopdeckLethal() : null;
                case "hostile-takeover": return win ? ThreeStolen() : null;
                case "no-time-to-explain": return win && NpcGate() && facts.OfType<TurnStartedFact>().Count(x => x.ControllerId == Human) < 5 ? TerminalWitness() : null;
                case "running-on-fumes": return win && facts.OfType<DamageResolvedHistoryFact>().Where(x => x.VictimControllerId == Human && x.Cause == HistoryDamageCause.Fatigue).Sum(x => x.HealthDamage) >= 10 ? Threshold(terminal.Sequence, 10) : null;
                case "population-boom": return win ? PerTurnCount<MinionSummonedFact>(x => x.ControllerId == Human, 15) : null;
                case "acceptable-losses": return win ? FriendlyEffectDeaths() : null;
                case "no-touching": return win ? FreezeFive() : null;
                case "chain-reaction": return win ? DeathrattleEight() : null;
                case "power-trip": return win ? HeroPowerDamage() : null;
                case "pocket-sized-problem": return win ? OneAttackExactLethal() : null;
                case "not-today": return win ? RebornLethal() : null;
                case "special-delivery": return win ? BurnThree() : null;
                case "borrowed-time": return win ? ExtraTurnWin() : null;
                case "return-to-sender": return win ? StolenSpellLethal() : null;
                case "a-hundred-good-games": return prior.EligibleWins + Delta.EligibleWins >= 100 ? new LocalAchievementCareerWitness(prior.EligibleWins + Delta.EligibleWins) : null;
                case "class-act": return SupportedClasses.All(x => afterClassWins.TryGetValue(x, out int count) && count >= 10) ? new LocalAchievementCareerWitness(afterClassWins.Values.Sum()) : null;
                case "well-read": return afterPlayed.Count >= 250 ? new LocalAchievementCareerWitness(afterPlayed.Count) : null;
                case "the-board-is-a-workplace-hazard": return prior.CreditedEnemyMinionDeaths + Delta.CreditedEnemyMinionDeaths >= 1000 ? new LocalAchievementCareerWitness(prior.CreditedEnemyMinionDeaths + Delta.CreditedEnemyMinionDeaths) : null;
                case "the-complete-works": return !string.IsNullOrEmpty(ledger.CompletedExpansion) && ledger.CompletedExpansionCatalogVersion > 0 ? new LocalAchievementCareerWitness(ledger.CompletedExpansionCatalogVersion, ledger.CompletedExpansion) : null;
                case "world-tour": return afterNpcs.Count >= 25 ? new LocalAchievementCareerWitness(afterNpcs.Count) : null;
                case "made-to-measure": return win && DeckGate() && CraftedDeckCount() >= 10 ? new LocalAchievementCareerWitness(CraftedDeckCount()) : null;
                case "welcome-to-legend": return Milestone("legend");
                case "among-the-hundred": return Milestone("legend:top-100");
                case "there-can-be-only-one": return Milestone("legend:1");
                default: throw new InvalidOperationException("Unknown achievement id: " + id);
            }
        }

        private static readonly string[] SupportedClasses = { "DEATHKNIGHT", "DEMONHUNTER", "DRUID", "HUNTER", "MAGE", "PALADIN", "PRIEST", "ROGUE", "SHAMAN", "WARLOCK", "WARRIOR" };
        private (int Health, int Armor, int Hand, int Deck) HumanTerminal() => terminal.Player1Id == Human
            ? (terminal.Player1Health, terminal.Player1Armor, terminal.Player1Hand, terminal.Player1Deck)
            : (terminal.Player2Health, terminal.Player2Armor, terminal.Player2Hand, terminal.Player2Deck);
        private int OpponentTerminalHealth() => terminal.Player1Id == Opponent ? terminal.Player1Health : terminal.Player2Health;
        private int HumanMaxHealth() => terminal.Player1Id == Human ? terminal.Player1MaxHealth : terminal.Player2MaxHealth;
        private LocalAchievementTerminalWitness TerminalWitness() => new LocalAchievementTerminalWitness(terminal.Sequence);
        private LocalAchievementThresholdWitness Threshold(long sequence, long value) => new LocalAchievementThresholdWitness(sequence, value);
        private bool ClassWin(string heroClass) => Won && pending.StartingHeroClass == heroClass;
        private bool NpcGate(bool legendOnly = false) => !string.IsNullOrEmpty(pending.QueuedNpcId)
            && (pending.QueuedNpcBand == "Legend" || !legendOnly && pending.QueuedNpcBand == "Diamond");
        private bool DeckGate(bool legendOnly = false) => pending.PersonalDeck && NpcGate(legendOnly) && Cards().Any()
            && Cards().All(x => x != null && x.Copies > 0 && !string.IsNullOrEmpty(x.BaseCardId));
        private IEnumerable<LocalAchievementDeckCard> Cards() => (pending.MainDeck ?? new List<LocalAchievementDeckCard>()).Concat(pending.Sideboards ?? new List<LocalAchievementDeckCard>());
        private IEnumerable<LocalAchievementDeckCard> MainCards() => pending.MainDeck ?? new List<LocalAchievementDeckCard>();
        private LocalAchievementDeckWitness DeckWitness() => new LocalAchievementDeckWitness(Cards().Select(x => LocalCardCatalog.CopyGroup(x.BaseCardId)));
        private HeroFatalFact OpponentFatal() => facts.OfType<HeroFatalFact>().LastOrDefault(x => x.HeroId == evidence.OpponentHeroId);
        private DamageResolvedHistoryFact FatalDamage(HeroFatalFact fatal) => fatal != null && fatal.FatalDamageSequence != 0 && damageBySequence.TryGetValue(fatal.FatalDamageSequence, out var damage) ? damage : null;
        private CombatStartedFact FatalCombat(HeroFatalFact fatal)
        {
            var damage = FatalDamage(fatal);
            return damage?.AttackId > 0 ? facts.OfType<CombatStartedFact>().SingleOrDefault(x => x.AttackId == damage.AttackId) : null;
        }
        private bool CreditedEnemyDeath(MinionDiedFact death)
        {
            if (death.ControllerId != Opponent || death.SourceControllerId != Human || death.SourceId == 0
                || death.Cause == HistoryDeathCause.None) return false;
            if (death.FatalDamageSequence == 0) return death.Cause == HistoryDeathCause.Destroy;
            if (!damageBySequence.TryGetValue(death.FatalDamageSequence, out var damage)) return false;
            if (damage.Cause != HistoryDamageCause.Combat) return true;
            return damage.AttackId > 0 && facts.OfType<CombatStartedFact>().Any(x =>
                x.AttackId == damage.AttackId && x.ControllerId == Human);
        }
        private LocalAchievementWitness SpellClear()
        {
            var bodies = facts.OfType<SpellBodyStartedFact>().Where(x => x.ControllerId == Human).ToArray();
            foreach (var body in bodies)
            {
                var cast = facts.OfType<SpellCastFact>().FirstOrDefault(x => x.CastId == body.CastId && x.ControllerId == Human
                    && x.CasterKind == HistoryCastKind.Player && x.Outcome == HistoryCastOutcome.Started);
                if (cast == null) continue;
                long nextBody = bodies.Where(x => x.CastId == body.CastId && x.Sequence > body.Sequence)
                    .Select(x => x.Sequence).DefaultIfEmpty(long.MaxValue).Min();
                var start = board.Before(body.Sequence).Where(x => x.ControllerId == Opponent).ToArray();
                if (start.Length != 7) continue;
                var ids = new HashSet<(int, long)>(start.Select(x => (x.EntityId, x.Incarnation)));
                var deathGroup = facts.OfType<MinionDiedFact>().Where(x => x.CastId == body.CastId && x.SourceId == body.SpellEntityId
                        && x.ResolutionId > 0
                        && x.Sequence > body.Sequence && x.Sequence < nextBody && ids.Contains((x.EntityId, x.Incarnation)))
                    .GroupBy(x => x.ResolutionId).FirstOrDefault(x => x.Select(y => (y.EntityId, y.Incarnation)).Distinct().Count() == 7);
                if (deathGroup == null) continue;
                long end = deathGroup.Max(x => x.Sequence);
                if (facts.OfType<MinionLeftPlayFact>().Any(x => x.Sequence > body.Sequence && x.Sequence <= end && ids.Contains((x.EntityId, x.Incarnation)))
                    || facts.OfType<ControlChangedFact>().Any(x => x.Sequence > body.Sequence && x.Sequence <= end && ids.Any(id => id.Item1 == x.EntityId))
                    || facts.OfType<MinionFormChangedFact>().Any(x => x.Sequence > body.Sequence && x.Sequence <= end && ids.Any(id => id.Item1 == x.OldEntityId))) continue;
                return new LocalAchievementResolutionWitness(end, deathGroup.Key, body.CastId, start.Select(x => x.EntityId));
            }
            return null;
        }

        private LocalAchievementWitness StolenLethal()
        {
            var fatal = OpponentFatal(); var combat = FatalCombat(fatal);
            if (combat == null || combat.AttackerType != CardType.MINION || combat.ControllerId != Human) return null;
            var actor = board.Before(combat.Sequence).SingleOrDefault(x => x.EntityId == combat.AttackerId && x.Incarnation == combat.AttackerIncarnation);
            return actor != null && actor.FirstControllerId == Opponent && actor.WasControlledByHuman
                ? new LocalAchievementAttackWitness(combat.Sequence, combat.AttackId, actor.EntityId, actor.Incarnation) : null;
        }

        private LocalAchievementWitness MutualDestruction()
        {
            if (evidence.Result != "Tied" || !evidence.NaturalDraw || HumanTerminal().Health > 0 || OpponentTerminalHealth() > 0) return null;
            var fatals = facts.OfType<HeroFatalFact>().Where(x => x.ResolutionId > 0).ToArray();
            var humanFatal = fatals.LastOrDefault(x => x.HeroId == evidence.HumanHeroId);
            var opponentFatal = fatals.LastOrDefault(x => x.HeroId == evidence.OpponentHeroId);
            return humanFatal != null && opponentFatal != null && humanFatal.ResolutionId == opponentFatal.ResolutionId
                ? new LocalAchievementResolutionWitness(Math.Max(humanFatal.Sequence, opponentFatal.Sequence), humanFatal.ResolutionId, 0,
                    new[] { evidence.HumanHeroId, evidence.OpponentHeroId }) : null;
        }

        private LocalAchievementWitness TurnStartWin(Func<TurnStartedFact, bool> predicate)
        {
            var turn = facts.OfType<TurnStartedFact>().LastOrDefault(x => x.ControllerId == Human && x.Turn == terminal.Turn && predicate(x));
            return turn == null ? null : new LocalAchievementTurnWitness(turn.Sequence, turn.Turn);
        }

        private LocalAchievementWitness HeroAttackLethal()
        {
            var fatal = OpponentFatal(); var combat = FatalCombat(fatal);
            return combat != null && combat.ControllerId == Human && combat.AttackerType == CardType.HERO
                ? new LocalAchievementAttackWitness(combat.Sequence, combat.AttackId, combat.AttackerId, 0) : null;
        }

        private LocalAchievementWitness Overkill()
        {
            var fatal = OpponentFatal(); var damage = FatalDamage(fatal);
            return damage != null && damage.SourceControllerId == Human
                && damage.Amount - damage.PreHitHealth - damage.ArmorDamage >= 30
                ? new LocalAchievementResolutionWitness(damage.Sequence, damage.ResolutionId, damage.CastId, new[] { damage.SourceId, damage.VictimId }) : null;
        }

        private LocalAchievementWitness HeroFiveTurnLethal()
        {
            var fatal = OpponentFatal(); var lethal = FatalCombat(fatal);
            if (lethal == null || lethal.AttackerType != CardType.HERO || lethal.ControllerId != Human) return null;
            int turns = facts.OfType<CombatStartedFact>().Where(x => x.ControllerId == Human && x.AttackerType == CardType.HERO).Select(x => x.Turn).Distinct().Count();
            return turns >= 5 ? new LocalAchievementAttackWitness(lethal.Sequence, lethal.AttackId, lethal.AttackerId, 0) : null;
        }

        private LocalAchievementWitness ManaLead()
        {
            int humanMana = 0, opposingMana = 0;
            foreach (var fact in facts)
            {
                if (fact is TurnStartedFact turn)
                {
                    if (turn.ControllerId == Human) { humanMana = turn.BaseMana; opposingMana = turn.OpposingBaseMana; }
                    else { opposingMana = turn.BaseMana; humanMana = turn.OpposingBaseMana; }
                }
                else if (fact is MaxManaChangedFact mana)
                {
                    if (mana.ControllerId == Human) humanMana = mana.NewMaximum; else if (mana.ControllerId == Opponent) opposingMana = mana.NewMaximum;
                }
                if (humanMana >= 10 && opposingMana <= 5) return Threshold(fact.Sequence, humanMana);
            }
            return null;
        }

        private LocalAchievementWitness StartingMinionSurvivalLethal(Func<EngineCard, bool> cardTest, int attack, string exactId, bool latestAwakening)
        {
            var fatal = OpponentFatal(); var combat = FatalCombat(fatal);
            if (combat == null || combat.ControllerId != Human || combat.AttackerType != CardType.MINION || combat.AttackDamage < attack) return null;
            var actor = board.Before(combat.Sequence).SingleOrDefault(x => x.EntityId == combat.AttackerId && x.Incarnation == combat.AttackerIncarnation);
            if (actor == null || !actor.StartedInDeck || actor.FormChanged || !cardTest(actor.Card) || exactId != null && actor.Card.Id != exactId) return null;
            long after = actor.EntrySequence;
            if (latestAwakening)
            {
                var awake = facts.OfType<MinionAwakenedFact>().LastOrDefault(x => x.EntityId == actor.EntityId && x.Incarnation == actor.Incarnation && x.Sequence < combat.Sequence);
                if (awake == null) return null;
                after = awake.Sequence;
            }
            return SurvivedOpposingTurn(actor.EntityId, actor.Incarnation, after, combat.Sequence)
                ? new LocalAchievementAttackWitness(combat.Sequence, combat.AttackId, actor.EntityId, actor.Incarnation) : null;
        }

        private bool SurvivedOpposingTurn(int entity, long incarnation, long after, long before)
        {
            foreach (var opposingStart in facts.OfType<TurnStartedFact>().Where(x => x.ControllerId == Opponent && x.Sequence > after && x.Sequence < before))
            {
                var atStart = board.Before(opposingStart.Sequence).Any(x => x.EntityId == entity && x.Incarnation == incarnation && x.ControllerId == Human);
                var humanStart = facts.OfType<TurnStartedFact>().FirstOrDefault(x => x.ControllerId == Human && x.Sequence > opposingStart.Sequence && x.Sequence < before);
                if (atStart && humanStart != null && board.Before(humanStart.Sequence).Any(x => x.EntityId == entity && x.Incarnation == incarnation && x.ControllerId == Human)) return true;
            }
            return false;
        }

        private LocalAchievementWitness TenSpellTurn()
        {
            var actions = facts.OfType<PlayerActionFact>().Where(x => x.ControllerId == Human && x.Kind == HistoryPlayerActionKind.PlayCard && x.Turn == terminal.Turn).ToArray();
            var plays = facts.OfType<CardPlayedFact>().Where(x => x.ControllerId == Human && x.CardType == CardType.SPELL && x.Turn == terminal.Turn).ToDictionary(x => x.PlayId);
            var accepted = actions.Where(x => x.RelatedPlayId != 0 && plays.ContainsKey(x.RelatedPlayId)).Select(x => x.RelatedPlayId).Distinct().ToArray();
            return accepted.Length >= 10 ? new LocalAchievementTurnWitness(actions.Last().Sequence, terminal.Turn) : null;
        }

        private LocalAchievementWitness RecruitLethal()
        {
            var fatal = OpponentFatal(); var combat = FatalCombat(fatal);
            if (combat == null || combat.ControllerId != Human || combat.AttackerType != CardType.MINION || combat.AttackDamage < 15) return null;
            var actor = board.Before(combat.Sequence).SingleOrDefault(x => x.EntityId == combat.AttackerId && x.Incarnation == combat.AttackerIncarnation);
            return actor != null && !actor.FormChanged && IsRecruit(actor.Card)
                ? new LocalAchievementAttackWitness(combat.Sequence, combat.AttackId, actor.EntityId, actor.Incarnation) : null;
        }

        private static bool IsRecruit(EngineCard card) => card != null && (card.Id == "CS2_101t" || card[(GameTag)3444] == 1);

        private LocalAchievementWitness PriestRecovery()
        {
            var changes = facts.OfType<HeroHealthChangedFact>().Where(x => x.ControllerId == Human).ToArray();
            foreach (var low in changes.Where(x => x.NewHealth <= 5))
            {
                int healed = 0;
                foreach (var change in changes.Where(x => x.Sequence > low.Sequence && x.Reason == HistoryHealthChangeReason.Heal && x.NewHealth > x.OldHealth))
                {
                    healed += change.NewHealth - change.OldHealth;
                    if (healed >= 25 && HumanTerminal().Health == HumanMaxHealth()) return Threshold(change.Sequence, healed);
                }
            }
            return null;
        }

        private LocalAchievementWitness FourCombos()
        {
            var group = facts.OfType<ComboActivatedFact>().Where(x => x.ControllerId == Human).GroupBy(x => x.Turn).FirstOrDefault(x => x.Select(y => y.PlayId).Distinct().Count() >= 4);
            return group == null ? null : new LocalAchievementTurnWitness(group.Max(x => x.Sequence), group.Key);
        }

        private LocalAchievementWitness SevenTotems()
        {
            var basics = new HashSet<string>(new[] { "CS2_050", "CS2_051", "CS2_052", "NEW1_009" }, StringComparer.Ordinal);
            var snapshot = board.Snapshots.FirstOrDefault(x => {
                var own = x.Actors.Where(a => a.ControllerId == Human && a.Card.IsRace(Race.TOTEM)).ToArray();
                return own.Length == 7 && basics.IsSubsetOf(own.Select(a => a.Card.Id));
            });
            return snapshot == null ? null : new LocalAchievementResolutionWitness(snapshot.Sequence, 0, 0, snapshot.Actors.Where(x => x.ControllerId == Human).Select(x => x.EntityId));
        }

        private LocalAchievementWitness SelfDamage()
        {
            var damage = facts.OfType<DamageResolvedHistoryFact>().Where(x => x.VictimId == evidence.HumanHeroId && x.SourceControllerId == Human
                && x.SourceId != 0 && (x.Cause == HistoryDamageCause.SpellOrEffect || x.Cause == HistoryDamageCause.HeroPower)).ToArray();
            return damage.Sum(x => x.Amount) >= 30 ? new LocalAchievementResolutionWitness(damage.Last().Sequence, damage.Last().ResolutionId, damage.Last().CastId, damage.Select(x => x.SourceId)) : null;
        }

        private LocalAchievementWitness OgreLethal() => StartingMinionSurvivalLethal(_ => true, 0, "TLC_253", true);

        private LocalAchievementWitness OneExpansion()
        {
            foreach (var group in MainCards().Where(x => x.CardSet != "CORE").GroupBy(x => x.CardSet, StringComparer.Ordinal))
                if (group.Sum(x => x.Copies) >= 20 && Cards().All(x => x.CardSet == group.Key || x.CardSet == "CORE")) return DeckWitness();
            return null;
        }

        private LocalAchievementWitness CounterLargeSpell()
        {
            var counter = facts.OfType<SpellCounteredFact>().FirstOrDefault(x => x.CasterControllerId == Opponent && x.CounterControllerId == Human && x.BaseCost >= 8);
            return counter == null ? null : new LocalAchievementResolutionWitness(counter.Sequence, counter.ResolutionId, counter.CastId, new[] { counter.SpellEntityId, counter.CounterSourceId });
        }

        private LocalAchievementWitness SixTypes()
        {
            var races = Enum.GetValues(typeof(Race)).Cast<Race>().Where(x => x != Race.INVALID && x != Race.ALL).ToArray();
            foreach (var snapshot in board.Snapshots)
            {
                var actors = snapshot.Actors.Where(x => x.ControllerId == Human).ToArray();
                if (CanAssign(actors, races, 0, new HashSet<Race>(), 6)) return new LocalAchievementResolutionWitness(snapshot.Sequence, 0, 0, actors.Select(x => x.EntityId));
            }
            return null;
        }

        private static bool CanAssign(BoardActor[] actors, Race[] races, int index, HashSet<Race> used, int target)
        {
            if (used.Count >= target) return true;
            if (index >= actors.Length || actors.Length - index < target - used.Count) return false;
            if (CanAssign(actors, races, index + 1, used, target)) return true;
            foreach (var race in races.Where(x => !used.Contains(x) && actors[index].Card.IsRace(x)))
            { used.Add(race); if (CanAssign(actors, races, index + 1, used, target)) return true; used.Remove(race); }
            return false;
        }

        private LocalAchievementWitness SevenFamily()
        {
            var races = Enum.GetValues(typeof(Race)).Cast<Race>().Where(x => x != Race.INVALID && x != Race.ALL);
            foreach (var snapshot in board.Snapshots)
            {
                var own = snapshot.Actors.Where(x => x.ControllerId == Human).ToArray();
                foreach (var race in races)
                {
                    var family = own.Where(x => x.Card.IsRace(race)).GroupBy(x => LocalCardCatalog.CopyGroup(x.Card.Id), StringComparer.Ordinal).Select(x => x.First()).ToArray();
                    if (family.Length >= 7) return new LocalAchievementResolutionWitness(snapshot.Sequence, 0, 0, family.Take(7).Select(x => x.EntityId));
                }
            }
            return null;
        }

        private LocalAchievementWitness TopdeckLethal()
        {
            var fatal = OpponentFatal(); var damage = FatalDamage(fatal);
            if (damage == null || damage.SourceControllerId != Human || damage.SourceKind != CardType.SPELL || damage.CastId == 0) return null;
            var play = facts.OfType<CardPlayedFact>().LastOrDefault(x => x.EntityId == damage.SourceId && x.ControllerId == Human && x.CardType == CardType.SPELL && x.Sequence < damage.Sequence);
            if (play == null) return null;
            var turn = facts.OfType<TurnStartedFact>().LastOrDefault(x => x.ControllerId == Human && x.Turn == play.Turn && x.Sequence < play.Sequence);
            var draw = facts.OfType<HandEnteredFact>().SingleOrDefault(x => x.ControllerId == Human && x.EntityId == play.EntityId && x.EntryKind == HistoryHandEntryKind.TurnStartDraw && x.Sequence > turn?.Sequence && x.Sequence < play.Sequence);
            if (turn == null || turn.HandCount != 0 || draw == null) return null;
            if (facts.OfType<HandEnteredFact>().Any(x => x.ControllerId == Human && x.Sequence > draw.Sequence && x.Sequence < play.Sequence
                && (x.EntryKind == HistoryHandEntryKind.Draw || x.EntryKind == HistoryHandEntryKind.TurnStartDraw))) return null;
            var first = facts.OfType<PlayerActionFact>().FirstOrDefault(x => x.ControllerId == Human && x.Turn == play.Turn && x.Sequence > draw.Sequence);
            return first != null && first.Kind == HistoryPlayerActionKind.PlayCard && first.RelatedPlayId == play.PlayId
                ? new LocalAchievementResolutionWitness(damage.Sequence, damage.ResolutionId, damage.CastId, new[] { play.EntityId }) : null;
        }

        private LocalAchievementWitness ThreeStolen()
        {
            var snapshot = board.Snapshots.FirstOrDefault(x => x.Actors.Count(a => a.ControllerId == Human && a.FirstControllerId == Opponent && a.WasControlledByHuman) >= 3);
            return snapshot == null ? null : new LocalAchievementResolutionWitness(snapshot.Sequence, 0, 0,
                snapshot.Actors.Where(a => a.ControllerId == Human && a.FirstControllerId == Opponent && a.WasControlledByHuman).Take(3).Select(a => a.EntityId));
        }

        private LocalAchievementWitness PerTurnCount<T>(Func<T, bool> predicate, int count) where T : HistoryFact
        {
            var group = facts.OfType<T>().Where(predicate).GroupBy(x => x.Turn).FirstOrDefault(x => x.Count() >= count);
            return group == null ? null : new LocalAchievementTurnWitness(group.Max(x => x.Sequence), group.Key);
        }

        private LocalAchievementWitness FriendlyEffectDeaths()
        {
            var eligible = facts.OfType<MinionDiedFact>().Where(x => x.ControllerId == Human && x.SourceControllerId == Human && x.SourceId != 0).Where(x => {
                if (x.FatalDamageSequence == 0) return x.Cause == HistoryDeathCause.Destroy;
                return damageBySequence.TryGetValue(x.FatalDamageSequence, out var damage) && damage.Cause != HistoryDamageCause.Combat;
            });
            var group = eligible.GroupBy(x => x.Turn).FirstOrDefault(x => x.Count() >= 7);
            return group == null ? null : new LocalAchievementTurnWitness(group.Max(x => x.Sequence), group.Key);
        }

        private LocalAchievementWitness FreezeFive()
        {
            var bodies = facts.OfType<SpellBodyStartedFact>().Where(x => x.ControllerId == Human).ToArray();
            foreach (var body in bodies)
            {
                var cast = facts.OfType<SpellCastFact>().FirstOrDefault(x => x.CastId == body.CastId && x.ControllerId == Human
                    && x.CasterKind == HistoryCastKind.Player && x.Outcome == HistoryCastOutcome.Started);
                if (cast == null) continue;
                long nextBody = bodies.Where(x => x.CastId == body.CastId && x.Sequence > body.Sequence)
                    .Select(x => x.Sequence).DefaultIfEmpty(long.MaxValue).Min();
                var frozen = facts.OfType<KeywordAppliedFact>().Where(x => x.CastId == body.CastId && x.SourceId == body.SpellEntityId
                        && x.ResolutionId > 0
                        && x.Sequence > body.Sequence && x.Sequence < nextBody && x.Keyword == HistoryKeyword.Frozen
                        && x.ChangedFromFalse && x.ControllerId == Opponent)
                    .GroupBy(x => x.ResolutionId).FirstOrDefault(x => x.Select(y => y.TargetId).Distinct().Count() >= 5);
                if (frozen != null && body.Turn == terminal.Turn) return new LocalAchievementResolutionWitness(
                    frozen.Max(x => x.Sequence), frozen.Key, body.CastId, frozen.Select(x => x.TargetId).Distinct());
            }
            return null;
        }

        private LocalAchievementWitness DeathrattleEight()
        {
            var group = facts.OfType<DeathrattleResolvedFact>().Where(x => x.ControllerId == Human).GroupBy(x => x.Turn)
                .FirstOrDefault(x => x.Count() >= 8 && x.Select(y => y.EntityId).Distinct().Count() >= 4);
            return group == null ? null : new LocalAchievementTurnWitness(group.Max(x => x.Sequence), group.Key);
        }

        private LocalAchievementWitness HeroPowerDamage()
        {
            var damage = facts.OfType<DamageResolvedHistoryFact>().Where(x => x.SourceControllerId == Human && x.VictimId == evidence.OpponentHeroId && x.Cause == HistoryDamageCause.HeroPower).ToArray();
            return damage.Sum(x => x.Amount) >= 20 ? new LocalAchievementResolutionWitness(damage.Last().Sequence, damage.Last().ResolutionId, 0, damage.Select(x => x.SourceId)) : null;
        }

        private LocalAchievementWitness OneAttackExactLethal()
        {
            var fatal = OpponentFatal(); var damage = FatalDamage(fatal); var combat = FatalCombat(fatal);
            return combat != null && damage != null && combat.ControllerId == Human && combat.AttackerType == CardType.MINION
                && combat.AttackDamage == 1 && damage.Amount == combat.TargetPreHitHealth + combat.TargetPreHitArmor
                ? new LocalAchievementAttackWitness(combat.Sequence, combat.AttackId, combat.AttackerId, combat.AttackerIncarnation) : null;
        }

        private LocalAchievementWitness RebornLethal()
        {
            var fatal = OpponentFatal(); var combat = FatalCombat(fatal);
            if (combat == null || combat.ControllerId != Human || combat.AttackerType != CardType.MINION) return null;
            var reborn = facts.OfType<MinionRebornFact>().LastOrDefault(x => x.ReturnedEntityId == combat.AttackerId && x.ReturnedIncarnation == combat.AttackerIncarnation && x.ControllerId == Human);
            if (reborn == null || facts.OfType<MinionFormChangedFact>().Any(x => x.Sequence > reborn.Sequence && x.Sequence < combat.Sequence && x.OldEntityId == reborn.ReturnedEntityId)) return null;
            return SurvivedOpposingTurn(reborn.ReturnedEntityId, reborn.ReturnedIncarnation, reborn.Sequence, combat.Sequence)
                ? new LocalAchievementAttackWitness(combat.Sequence, combat.AttackId, combat.AttackerId, combat.AttackerIncarnation) : null;
        }

        private LocalAchievementWitness BurnThree()
        {
            var group = facts.OfType<CardBurnedFact>().Where(x => x.ControllerId == Opponent && x.SourceControllerId == Human && x.SourceId != 0 && x.Reason == HistoryBurnReason.FullHand)
                .GroupBy(x => x.Turn).FirstOrDefault(x => x.Count() >= 3);
            return group == null ? null : new LocalAchievementTurnWitness(group.Max(x => x.Sequence), group.Key);
        }

        private LocalAchievementWitness ExtraTurnWin()
        {
            var turn = facts.OfType<ExtraTurnStartedFact>().LastOrDefault(x => x.ControllerId == Human && x.Turn == terminal.Turn);
            return turn == null ? null : new LocalAchievementTurnWitness(turn.Sequence, turn.Turn);
        }

        private LocalAchievementWitness StolenSpellLethal()
        {
            var fatal = OpponentFatal(); var damage = FatalDamage(fatal);
            if (damage == null || damage.SourceControllerId != Human || damage.SourceKind != CardType.SPELL || damage.CastId == 0) return null;
            var transfer = facts.OfType<CardTransferredFact>().LastOrDefault(x => x.EntityId == damage.SourceId && x.ControllerId == Human
                && x.FromControllerId == Opponent && (x.FromZone == EngineZone.HAND || x.FromZone == EngineZone.DECK) && x.CardType == CardType.SPELL);
            return transfer == null ? null : new LocalAchievementResolutionWitness(damage.Sequence, damage.ResolutionId, damage.CastId, new[] { damage.SourceId });
        }

        private int CraftedDeckCount()
        {
            var crafted = new HashSet<string>(pending.CraftedAndOwnedBaseCardIds ?? new List<string>(), StringComparer.Ordinal);
            return Cards().Select(x => LocalCardCatalog.CopyGroup(x.BaseCardId)).Where(crafted.Contains).Distinct(StringComparer.Ordinal).Count();
        }

        private LocalAchievementWitness Milestone(string id) => ledger.MilestoneIds.Contains(id)
            ? new LocalAchievementCareerWitness(1, id) : null;
    }

    private sealed class BoardActor
    {
        internal int EntityId, ControllerId, FirstControllerId;
        internal long Incarnation, EntrySequence;
        internal EngineCard Card;
        internal bool StartedInDeck, WasControlledByHuman, FormChanged;
        internal BoardActor Copy() => (BoardActor)MemberwiseClone();
    }

    private sealed class BoardSnapshot
    {
        internal long Sequence { get; }
        internal IReadOnlyList<BoardActor> Actors { get; }
        internal BoardSnapshot(long sequence, IEnumerable<BoardActor> actors)
        { Sequence = sequence; Actors = Array.AsReadOnly(actors.Select(x => x.Copy()).ToArray()); }
    }

    private sealed class BoardTimeline
    {
        internal IReadOnlyList<BoardSnapshot> Snapshots { get; }
        internal BoardTimeline(IEnumerable<HistoryFact> facts, int humanControllerId)
        {
            var actors = new Dictionary<int, BoardActor>(); var snapshots = new List<BoardSnapshot> { new BoardSnapshot(0, actors.Values) };
            foreach (var fact in facts)
            {
                bool changed = true;
                if (fact is MinionSummonedFact summon)
                    actors[summon.EntityId] = new BoardActor { EntityId = summon.EntityId, Incarnation = summon.Incarnation,
                        ControllerId = summon.ControllerId, FirstControllerId = summon.ControllerId, EntrySequence = summon.Sequence,
                        Card = summon.Definition.Materialize(), StartedInDeck = summon.OriginKnown && summon.StartedInDeck };
                else if (fact is ControlChangedFact control && actors.TryGetValue(control.EntityId, out var controlled))
                { controlled.ControllerId = control.ToControllerId; controlled.Incarnation = control.ToIncarnation; controlled.WasControlledByHuman |= control.ToControllerId == humanControllerId; }
                else if (fact is MinionFormChangedFact form && actors.TryGetValue(form.OldEntityId, out var transformed))
                {
                    actors.Remove(form.OldEntityId);
                    if (form.OldEntityId == form.NewEntityId)
                    { transformed.Incarnation = form.NewIncarnation; transformed.Card = form.NewDefinition.Materialize(); transformed.FormChanged = true; actors[form.NewEntityId] = transformed; }
                    else actors[form.NewEntityId] = new BoardActor { EntityId = form.NewEntityId, Incarnation = form.NewIncarnation,
                        ControllerId = fact.ControllerId, FirstControllerId = fact.ControllerId, EntrySequence = fact.Sequence,
                        Card = form.NewDefinition.Materialize(), FormChanged = true };
                }
                else if (fact is MinionDiedFact died) actors.Remove(died.EntityId);
                else if (fact is MinionLeftPlayFact left) actors.Remove(left.EntityId);
                else changed = false;
                if (changed) snapshots.Add(new BoardSnapshot(fact.Sequence, actors.Values));
            }
            Snapshots = Array.AsReadOnly(snapshots.ToArray());
        }
        internal IReadOnlyList<BoardActor> Before(long sequence) => Snapshots.Last(x => x.Sequence < sequence).Actors;
    }
#endif
}
