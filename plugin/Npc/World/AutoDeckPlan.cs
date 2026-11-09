using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

// WP1. Derives a complete DeckPlan for ANY admitted deck from card data alone, so GoldenPilot plays every library deck.
// Hand-written DeckPlans (DeckPlans.DragonWarrior etc.) stay authoritative: Overlay keeps every non-empty hand field.
// Only reads the bot's own decklist + static card definitions: fairness-neutral by construction.
public static class AutoDeckPlan
{
    public const string Prefix = "auto:";
    public static string PlanIdFor(string deckId) => Prefix + deckId;
    public static bool IsAutoPlanId(string planId) => planId != null && planId.StartsWith(Prefix, StringComparison.Ordinal);
    public static string DeckIdFor(string planId) => IsAutoPlanId(planId) ? planId.Substring(Prefix.Length) : null;

    // Class-only priors, identical to DeckPlans.DefaultMatchups (private there): never an inferred opponent list.
    private static readonly IReadOnlyList<MatchupClasses> Matchups = Array.AsReadOnly(new[] {
        new MatchupClasses(Matchup.Aggro, CardClass.HUNTER, (CardClass)14, CardClass.SHAMAN),
        new MatchupClasses(Matchup.Midrange, CardClass.WARRIOR, CardClass.PALADIN, CardClass.ROGUE, CardClass.DEATHKNIGHT),
        new MatchupClasses(Matchup.Control, CardClass.MAGE, CardClass.PRIEST, CardClass.WARLOCK, CardClass.DRUID)
    });

    // Full derivation: CardRoleClassifier roles, curve, archetype -> aggression curve + evaluator weights,
    // mulligan keep rules (cheap on-curve cards, archetype-specific keeps vs Matchup), removal thresholds by damage/cost,
    // conservative holds (Finisher/Payoff held until ThreatOrLethal), no PlanHints (those are deck-specific, hand-written).
    public static DeckPlan Derive(NpcDeckEntry deck)
    {
        if (deck == null) throw new ArgumentNullException(nameof(deck));
        var archetype = deck.Tags.Archetype;
        var cards = CardRoleClassifier.ClassifyDeck(deck);
        var counts = deck.Cards.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(c => c.Count), StringComparer.Ordinal);
        int Copies(ClassifiedCard c) { int n; return counts.TryGetValue(c.CardId, out n) ? Math.Min(2, n) : 1; }
        bool Minion(ClassifiedCard c) => Cards.FromId(c.CardId).Type == CardType.MINION;

        var mulligan = new List<MulliganRule>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        void Keep(MulliganRule rule) { if (kept.Add(rule.CardId + "|" + rule.Matchups + "|" + rule.Condition)) mulligan.Add(rule); }
        // Quests and deck-defining starters are kept in every matchup (what any human pilot does).
        foreach (var c in cards.Where(c => (c.Auto & AutoRole.Quest) != 0)) Keep(new MulliganRule(c.CardId));
        // Cheap Development always; aggro keeps both copies of everything at <= 2.
        foreach (var c in cards.Where(c => c.Cost <= 2 && (c.Roles & CardRole.Development) != 0))
            Keep(new MulliganRule(c.CardId, maxCopies: archetype == DeckArchetype.Aggro || c.Cost <= 1 ? Copies(c) : 1));
        // A 3-drop under EarlyCurve (needs one kept 1-2 drop); aggro/tempo keep it unconditionally vs Control.
        foreach (var c in cards.Where(c => c.Cost == 3 && Minion(c)))
        {
            Keep(new MulliganRule(c.CardId, condition: MulliganCondition.EarlyCurve, minimumEarlyPlays: 1));
            if (archetype == DeckArchetype.Aggro || archetype == DeckArchetype.Tempo) Keep(new MulliganRule(c.CardId, Matchup.Control));
        }
        // Cheap Removal vs Aggro.
        foreach (var c in cards.Where(c => c.Cost <= 3 && (c.Roles & CardRole.Removal) != 0 && !Minion(c)))
            Keep(new MulliganRule(c.CardId, Matchup.Aggro));
        // One Refill vs Control: the cheapest draw card only (rules cannot express "any one of").
        var refill = cards.Where(c => c.Cost <= 3 && (c.Roles & CardRole.Refill) != 0 && (c.Auto & AutoRole.Quest) == 0)
            .OrderBy(c => c.Cost).ThenBy(c => c.CardId, StringComparer.Ordinal).FirstOrDefault();
        if (refill != null) Keep(new MulliganRule(refill.CardId, Matchup.Control | (archetype == DeckArchetype.Control || archetype == DeckArchetype.Combo ? Matchup.Midrange : 0)));
        // A 4-drop minion only for slower decks vs slower opponents, with two early plays already kept. Never above 4.
        if (archetype != DeckArchetype.Aggro)
            foreach (var c in cards.Where(c => c.Cost == 4 && Minion(c)))
                Keep(new MulliganRule(c.CardId, Matchup.Midrange | Matchup.Control, MulliganCondition.EarlyCurve, minimumEarlyPlays: 2));

        var roles = cards.Where(c => c.Roles != 0).Select(c => new CardRoleEntry(c.CardId, c.Roles)).ToArray();

        var holds = new List<HoldRule>();
        var thresholds = new List<RemovalThreshold>();
        // Finishers wait for a threat or lethal; LethalSolver always overrides a hold.
        var finishers = cards.Where(c => (c.Roles & CardRole.Finisher) != 0 && (c.Auto & AutoRole.Burn) != 0).Select(c => c.CardId).ToArray();
        if (finishers.Length > 0 && archetype != DeckArchetype.Aggro) holds.Add(new HoldRule(HoldCondition.ThreatOrLethal, 2, finishers));
        // One-shot AoE spells wait for a worthwhile board; weapons/minions also develop.
        var aoe = cards.Where(c => (c.Auto & AutoRole.ImmediateClear) != 0).Select(c => c.CardId).ToArray();
        if (aoe.Length > 0) holds.Add(new HoldRule(HoldCondition.AoeWideBoard, 2, aoe));
        foreach (var c in cards.Where(c => (c.Auto & (AutoRole.Removal | AutoRole.Aoe)) == AutoRole.Removal && c.Cost >= 3 && !Minion(c)))
        {
            // Hard removal: target attack + health >= cost * 2, split evenly between the two minima.
            int each = Math.Max(1, c.Cost - 1);
            thresholds.Add(new RemovalThreshold(c.CardId, each, each));
            holds.Add(new HoldRule(HoldCondition.DamagedTargetWithExcess, 1.5, new[] { c.CardId }));
        }

        var weights = WeightsFor(archetype);
        if (cards.Any(c => (c.Roles & CardRole.Aura) != 0))
            weights = new EvaluatorWeights(weights.OwnLife, weights.EnemyLife, weights.BoardAttack, weights.BoardHealth, weights.Hand,
                weights.DivineShield, weights.HeldDragon, weights.WarptoothSetup, 3, weights.BoardSpace, weights.BattlecrySetup, weights.Herald);
        return new DeckPlan(PlanIdFor(deck.Id), deck.Id, Matchups, mulligan, roles, holds, thresholds, AggressionFor(archetype),
            new PlanHint[0], new DiscoverPreference[0], new RewindPreference[0], weights);
    }

    // Hand-written fields win when non-empty; derived fields fill the rest (e.g. Roles for cards the hand plan omits).
    public static DeckPlan Overlay(DeckPlan derived, DeckPlan handWritten)
    {
        if (derived == null) throw new ArgumentNullException(nameof(derived));
        if (handWritten == null) return derived;
        IEnumerable<T> Pick<T>(IReadOnlyList<T> hand, IReadOnlyList<T> auto) => hand.Count != 0 ? hand : auto;
        var handRoles = new HashSet<string>(handWritten.CardRoles.Select(r => r.CardId), StringComparer.Ordinal);
        // Roles merge per card: the hand plan's entry wins; derived entries fill cards it omits.
        var roles = handWritten.CardRoles.Count == 0 ? derived.CardRoles
            : handWritten.CardRoles.Concat(derived.CardRoles.Where(r => !handRoles.Contains(r.CardId))).ToArray();
        return new DeckPlan(handWritten.Id, handWritten.DeckId, Pick(handWritten.MatchupClasses, derived.MatchupClasses),
            Pick(handWritten.MulliganRules, derived.MulliganRules), roles, Pick(handWritten.Holds, derived.Holds),
            Pick(handWritten.RemovalThresholds, derived.RemovalThresholds), Pick(handWritten.AggressionCurve, derived.AggressionCurve),
            handWritten.Hints, Pick(handWritten.DiscoverPreferences, derived.DiscoverPreferences),
            Pick(handWritten.RewindPreferences, derived.RewindPreferences), handWritten.Weights ?? derived.Weights);
    }

    // Archetype priors used by Derive (data, so calibration can tune them without code changes later).
    // Order matters: the evaluator applies the LAST matching point (turn, own life, opponent life).
    public static IReadOnlyList<AggressionPoint> AggressionFor(DeckArchetype archetype)
    {
        const int Any = int.MaxValue;
        switch (archetype)
        {
            case DeckArchetype.Aggro: return Points(new AggressionPoint(1, Any, Any, 1.4), new AggressionPoint(1, Any, 15, 2.0),
                new AggressionPoint(1, 8, Any, .8), new AggressionPoint(1, 8, 10, 2.0));
            case DeckArchetype.Tempo: return Points(new AggressionPoint(1, Any, Any, 1.0), new AggressionPoint(6, Any, Any, 1.4),
                new AggressionPoint(1, Any, 12, 1.65), new AggressionPoint(1, 10, Any, .6), new AggressionPoint(1, 10, 8, 1.8));
            case DeckArchetype.Midrange: return Points(new AggressionPoint(1, Any, Any, .8), new AggressionPoint(7, Any, Any, 1.2),
                new AggressionPoint(1, Any, 12, 1.5), new AggressionPoint(1, 10, Any, .5), new AggressionPoint(1, 10, 8, 1.8));
            case DeckArchetype.Control: return Points(new AggressionPoint(1, Any, Any, .5), new AggressionPoint(8, Any, Any, 1.0),
                new AggressionPoint(1, Any, 10, 1.3), new AggressionPoint(1, 12, Any, .3), new AggressionPoint(1, 12, 6, 1.6));
            case DeckArchetype.Combo: return Points(new AggressionPoint(1, Any, Any, .6), new AggressionPoint(1, Any, 12, 1.6),
                new AggressionPoint(1, 10, Any, .4), new AggressionPoint(1, 10, 8, 1.8));
            default: throw new ArgumentOutOfRangeException(nameof(archetype));
        }
    }
    private static IReadOnlyList<AggressionPoint> Points(params AggressionPoint[] points) => Array.AsReadOnly(points);

    // Starts from the golden plans (Dragon Warrior tempo, Zee aggro-tempo, Pure Paladin value), deck-specific
    // terms (HeldDragon, WarptoothSetup, BattlecrySetup, Herald) zeroed: they only mean something with hand-written hints.
    public static EvaluatorWeights WeightsFor(DeckArchetype archetype)
    {
        switch (archetype)
        {
            case DeckArchetype.Aggro: return new EvaluatorWeights(.6, 1.5, 2.8, 1.0, 1.4, 1.5, 0, 0, 0, 2, 0, 0);
            case DeckArchetype.Tempo: return new EvaluatorWeights(.8, 1.1, 2.5, 1.05, 1.7, 1.5, 0, 0, 0, 1.5, 0, 0);
            case DeckArchetype.Midrange: return new EvaluatorWeights(.9, 1.0, 2.4, 1.2, 1.6, 2, 0, 0, 0, 1.5, 0, 0);
            case DeckArchetype.Control: return new EvaluatorWeights(1.1, .7, 2.2, 1.3, 1.4, 2.5, 0, 0, 0, 1, 0, 0);
            case DeckArchetype.Combo: return new EvaluatorWeights(1.0, .8, 2.2, 1.2, 1.8, 1.5, 0, 0, 0, 1, 0, 0);
            default: throw new ArgumentOutOfRangeException(nameof(archetype));
        }
    }
}

// WP1. Static card-text/tag classification into the existing CardRole flags (+ AutoRole for roles CardRole lacks).
[Flags]
public enum AutoRole { None = 0, Removal = 1, Aoe = 2, Taunt = 4, Burn = 8, Draw = 16, Finisher = 32, Tempo = 64, Heal = 128, Secret = 256, Quest = 512, ImmediateClear = 1024 }

public sealed class ClassifiedCard
{
    public string CardId { get; }
    public int Cost { get; }
    public AutoRole Auto { get; }
    public CardRole Roles { get; }
    public ClassifiedCard(string cardId, int cost, AutoRole auto, CardRole roles)
    { CardId = cardId; Cost = cost; Auto = auto; Roles = roles; }
}

public static class CardRoleClassifier
{
    private static readonly Regex Markup = new Regex(@"<[^>]*>|\[x\]|[$#@]", RegexOptions.Compiled);
    private static readonly Regex Damage = new Regex(@"deal (\d+) damage", RegexOptions.Compiled);
    private static readonly Regex AllTargets = new Regex(@"(to|damage to) all (other |enemy |enemies|minions|characters)|to all enemies|all minions|all enemy minions|destroy all|each enemy|to all enemy", RegexOptions.Compiled);

    // Uses SabberStoneCore Cards.FromId (tags: TAUNT, RUSH, CHARGE, SECRET, spell damage text, draw, AoE targeting).
    public static ClassifiedCard Classify(string cardId)
    {
        var card = Cards.FromId(cardId) ?? throw new ArgumentException("Unknown card: " + cardId);
        string text = Markup.Replace(card.Text ?? "", "").Replace('\n', ' ').ToLowerInvariant();
        bool minion = card.Type == CardType.MINION, spell = card.Type == CardType.SPELL;
        var auto = AutoRole.None;
        int damage = 0;
        foreach (Match m in Damage.Matches(text)) damage = Math.Max(damage, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        bool aoe = AllTargets.IsMatch(text) && (damage > 0 || text.Contains("destroy"));
        bool targeted = card.RequiresTarget || card.RequiresTargetIfAvailable || text.Contains("a minion") || text.Contains("an enemy");
        if (aoe) auto |= AutoRole.Aoe;
        // Broad AoE roles include granted triggers and optional development. Only immediate clears justify a hold.
        if (aoe && spell && !card.IsSecret && !Regex.IsMatch(text,
            @"\b(deathrattle|after|whenever|when|next)\b|choose one|at the (start|end)")) auto |= AutoRole.ImmediateClear;
        if (!aoe && (damage > 0 && targeted || text.Contains("destroy a") || text.Contains("destroy an") || text.Contains("silence")
            || text.Contains("freeze a") || text.Contains("transform a") || text.Contains("return an enemy")))
            auto |= AutoRole.Removal;
        // Burn: spell (or battlecry) damage that can reach the enemy hero.
        if (damage >= 2 && (text.Contains("enemy hero") || text.Contains("your opponent") || text.Contains("to all enemies")
            || spell && card.RequiresTarget && !text.Contains("minion"))) auto |= AutoRole.Burn;
        if (card.Taunt || minion && text.Contains("taunt")) auto |= AutoRole.Taunt;
        if (Regex.IsMatch(text, @"\bdraw\b|\bdraws\b|\bdraw (a|\d|two|three)")) auto |= AutoRole.Draw;
        if (text.Contains("restore") || Regex.IsMatch(text, @"gain \d+ armor") || card.LifeSteal) auto |= AutoRole.Heal;
        if (minion && (card.Rush || card.Charge || text.Contains("rush") || text.Contains("charge"))) auto |= AutoRole.Tempo;
        if (card.IsSecret) auto |= AutoRole.Secret;
        // Reward text describes future effects, not removal/draw/hold instructions for casting the quest.
        if (QuestValue.IsQuest(card.Id)) return new ClassifiedCard(card.Id, card.Cost, AutoRole.Quest, CardRole.Quest);
        bool reach = (auto & AutoRole.Burn) != 0 && damage >= 4 || minion && card.Charge && card.ATK >= 4;
        bool bomb = minion && card.Cost >= 7 && card.ATK + card.Health >= 14;
        if (reach || bomb) auto |= AutoRole.Finisher;

        var roles = (CardRole)0;
        if (minion && card.Cost <= 3) roles |= CardRole.Development;
        if ((auto & (AutoRole.Removal | AutoRole.Aoe)) != 0) roles |= CardRole.Removal;
        if (minion && (card.Rush || card.Charge)) roles |= CardRole.Rush;
        if ((auto & AutoRole.Draw) != 0) roles |= CardRole.Refill;
        if ((auto & AutoRole.Finisher) != 0) roles |= CardRole.Finisher;
        if (card.DivineShield || text.Contains("divine shield")) roles |= CardRole.Shield;
        if (Regex.IsMatch(text, @"\baura\b")) roles |= CardRole.Aura;
        if (text.Contains("discover") || Regex.IsMatch(text, @"add .* to your hand|get a copy|get (a|two|three) ")) roles |= CardRole.Generator;
        if (minion && card.IsRace(Race.DRAGON)) roles |= CardRole.Dragon;
        if (Regex.IsMatch(text, @"\bherald\b")) roles |= CardRole.Herald;
        return new ClassifiedCard(card.Id, card.Cost, auto, roles);
    }

    public static IReadOnlyList<ClassifiedCard> ClassifyDeck(NpcDeckEntry deck) =>
        Array.AsReadOnly(deck.Cards.Select(c => c.Id).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
            .Select(Classify).ToArray());
}
