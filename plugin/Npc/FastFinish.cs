using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Tasks.PlayerTasks;

// Search-free turn completion (live incident 2026-11-01, Biscuitzz turn 27): once the NPC's turn budget is exhausted and
// the clock-cut fallback would END_TURN without having evaluated its alternatives, finish the turn with obvious free
// actions instead of passing with ready attackers and unspent mana. One action per decision (the runner calls back after
// each action). A pure function of the fair view and its legal actions: no clock, stable tie-breaks on entity ids; the
// only simulation is the fair view's own deterministic one-ply Apply. Live only: NativeMatch.NpcFallback calls it for
// reason clock-cut/fast-finish; the harness and corpus never reach this path, and a search that finishes normally never
// does either.
public static class FastFinish
{
    public const string DriverKind = "fast-finish";
    // Runner reason for later decisions of a turn that already entered fast finish (search-free).
    public const string Reason = "fast-finish";
    public const string Attack = "attack", Mana = "mana", None = "none";

    public static bool Applies(string reason) => reason == "clock-cut" || reason == Reason;

    // Attacks first (visible lethal, favourable trades, face, then Taunt-forced), then obvious mana, else null (none:
    // END_TURN). A pending choice returns null with kind null.
    public static BotAction Choose(FairBotState state, IEnumerable<BotAction> legal, out string kind)
    {
        kind = None;
        if (state == null || legal == null) return null;
        var actions = legal.Where(a => a != null).Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal).ToArray();
        // A pending choice (Discover, Choose One, mulligan) is never answered here: kind null, the caller decides.
        if (actions.Any(a => a.Type == PlayerTaskType.CHOOSE)) { kind = null; return null; }
        var facts = state.Facts;
        var attack = ChooseAttack(facts, actions, state.FallbackUsesEvaluator);
        if (attack != null) { kind = Attack; return attack; }
        var mana = ChooseMana(state, facts, actions);
        if (mana != null) { kind = Mana; return mana; }
        return null;
    }

    // The host's search-free fallback (NativeMatch.NpcFallback after its forced/action-limit/deadline checks; the
    // harness hard-decision fixture drives the same code). Clock cut: the bounded one-ply fallback first; when it ran out
    // of time and would END_TURN unevaluated, fast finish instead. Fast-finish reason: fast finish only. kind: attack|mana|
    // none when fast finish decided, null otherwise. An evaluated END_TURN (every candidate scored) is respected.
    public static BotAction Fallback(FairBotState view, BotAction[] legal, string reason, Func<bool> spend, out string kind)
        => Fallback(view, legal, reason, spend, out kind, out _);
    internal static BotAction Fallback(FairBotState view, BotAction[] legal, string reason, Func<bool> spend, out string kind,
        out IReadOnlyList<KeyValuePair<BotAction, double>> compared)
    {
        kind = null; BotAction chosen = null; bool complete = false; compared = null;
        if (reason != Reason) chosen = BotDriver.BoundedFallback(view, legal, spend, out complete, out compared);
        if (Applies(reason) && (chosen == null || !complete && chosen.Type == PlayerTaskType.END_TURN))
        {
            var finish = Choose(view, legal, out kind);
            chosen = finish ?? chosen ?? legal.FirstOrDefault(a => a.Type == PlayerTaskType.END_TURN)
                ?? BotDriver.BoundedFallback(view, legal, () => false);
        }
        return chosen;
    }

    // Public value used to compare trades: stats now on board plus printed cost.
    internal static double Value(FairCard card) => 2.0 * card.Attack + card.Health + card.Cost;

    private static bool Tagged(FairCard card, GameTag tag) => card.Tags.TryGetValue(tag, out int value) && value != 0;
    private static int AttacksLeft(FairCard card) => Math.Max(1, card.AttackLimit - card.AttacksThisTurn);

    // Our attacker kills the defender (a Divine Shield absorbs the hit).
    internal static bool Kills(FairCard attacker, FairCard target) => !target.DivineShield && attacker.Attack > 0
        && (attacker.Attack >= target.Health || Tagged(attacker, GameTag.POISONOUS) && target.Type == CardType.MINION);
    // Our attacker dies to the defender's counterattack. A hero counts any hit that reaches its health + armour.
    internal static bool Dies(FairCard attacker, FairCard target)
    {
        if (attacker.Immune || target.Attack <= 0) return false;
        if (attacker.Type == CardType.HERO) return (long)target.Attack >= (long)attacker.Health + attacker.Armor;
        return !attacker.DivineShield && (target.Attack >= attacker.Health || Tagged(target, GameTag.POISONOUS));
    }
    // Favourable: kills and survives, or a minion kills a higher-value minion. Heroes never trade their life away.
    internal static bool Favourable(FairCard attacker, FairCard target) =>
        Kills(attacker, target) && (!Dies(attacker, target) || attacker.Type != CardType.HERO && Value(target) > Value(attacker));

    // shells: killing a harmless payoff body is no trade, it hands the owner the payoff (death-chip).
    private static BotAction ChooseAttack(FairFacts facts, BotAction[] actions, bool shells)
    {
        var own = new Dictionary<int, FairCard>();
        foreach (var card in facts.Own.Board.Concat(new[] { facts.Own.Hero })) if (card.EntityId != 0) own[card.EntityId] = card;
        var enemy = new Dictionary<int, FairCard>();
        foreach (var card in facts.Opponent.Board) if (card.EntityId != 0) enemy[card.EntityId] = card;
        int face = facts.Opponent.Hero.EntityId;
        var attacks = actions.Where(a => (a.Type == PlayerTaskType.MINION_ATTACK || a.Type == PlayerTaskType.HERO_ATTACK)
            && own.ContainsKey(a.Source) && own[a.Source].Attack > 0 && (a.Target == face || enemy.ContainsKey(a.Target))).ToArray();
        if (attacks.Length == 0) return null;
        FairCard Attacker(BotAction a) => own[a.Source];

        // 1. Visible lethal: the legal face swings together reach the opponent's health + armour.
        var faceSwings = attacks.Where(a => a.Target == face).ToArray();
        long faceDamage = faceSwings.GroupBy(a => a.Source).Sum(g => (long)Attacker(g.First()).Attack * AttacksLeft(Attacker(g.First())));
        if (faceSwings.Length > 0 && faceDamage >= (long)facts.Opponent.Hero.Health + facts.Opponent.Hero.Armor)
            return faceSwings.OrderByDescending(a => Attacker(a).Attack).ThenBy(a => a.Source).First();

        // 2. Favourable trades, highest-value victim first; survivors before even trades, cheapest attacker first.
        var trade = attacks.Where(a => a.Target != face && Favourable(Attacker(a), enemy[a.Target])
                && !(shells && DeckPlanEvaluator.ChipFree(enemy[a.Target])))
            .OrderByDescending(a => Value(enemy[a.Target])).ThenBy(a => Dies(Attacker(a), enemy[a.Target]) ? 1 : 0)
            .ThenBy(a => Value(Attacker(a))).ThenBy(a => a.Source).ThenBy(a => a.Target).FirstOrDefault();
        if (trade != null) return trade;

        // 3. Face with every attacker that can reach it (no Taunt in the way), biggest first.
        var hit = faceSwings.OrderByDescending(a => Attacker(a).Attack).ThenBy(a => a.Source).FirstOrDefault();
        if (hit != null) return hit;

        // 4. A Taunt is in the way: hit it when the attacker survives (chip) or the ready attackers together clear it.
        //    A hero never swings into a counterattack that kills it.
        var taunts = attacks.Where(a => a.Target != face && enemy[a.Target].Taunt
            && !(Attacker(a).Type == CardType.HERO && Dies(Attacker(a), enemy[a.Target]))).ToArray();
        bool Clears(int target)
        {
            var defender = enemy[target];
            var swings = taunts.Where(a => a.Target == target).GroupBy(a => a.Source).Select(g => Attacker(g.First())).ToArray();
            if (swings.Length == 0) return false;
            if (!defender.DivineShield && swings.Any(c => Tagged(c, GameTag.POISONOUS))) return true;
            var damage = swings.Select(c => (long)c.Attack * AttacksLeft(c)).OrderBy(x => x).ToArray();
            long total = damage.Sum();
            return defender.DivineShield ? damage.Length >= 2 && total - damage[0] >= defender.Health : total >= defender.Health;
        }
        return taunts.Where(a => !Dies(Attacker(a), enemy[a.Target]) || Clears(a.Target))
            .OrderBy(a => Dies(Attacker(a), enemy[a.Target]) ? 1 : 0).ThenByDescending(a => Attacker(a).Attack)
            .ThenBy(a => enemy[a.Target].Health).ThenBy(a => a.Source).ThenBy(a => a.Target).FirstOrDefault();
    }

    // Obvious mana: untargeted minions without Choose One/Discover or a carried card (rightmost slot), then an untargeted
    // or face hero power; highest cost first. Each must strictly improve the fair one-ply evaluation and keep every
    // friendly minion on board at its stats, so a public drawback or a board wipe in a battlecry skips it. Spells are
    // never cast here. Live 2026-11-01 (tater96): a carved Treant ("Battlecry: Cast {0}") cast Typhoon and shuffled away a
    // 24-attack board; the fair one-ply cannot always see a carried card's effect, so such minions are never obvious.
    private static BotAction ChooseMana(FairBotState state, FairFacts facts, BotAction[] actions)
    {
        var hand = new Dictionary<int, FairCard>();
        foreach (var card in facts.Own.Hand) if (card.EntityId != 0) hand[card.EntityId] = card;
        int face = facts.Opponent.Hero.EntityId;
        var minions = actions.Where(a => a.Type == PlayerTaskType.PLAY_CARD && a.Target == 0 && a.ChooseOne <= 0
                && hand.TryGetValue(a.Source, out var card) && card.Type == CardType.MINION && !NeedsChoice(card))
            .GroupBy(a => a.Source).Select(g => g.OrderByDescending(a => a.Position).First())
            .Select(a => (Action: a, Cost: hand[a.Source].Cost, Order: 0));
        var powers = actions.Where(a => a.Type == PlayerTaskType.HERO_POWER && (a.Target == 0 || a.Target == face))
            .Select(a => (Action: a, Cost: facts.Own.HeroPower.Cost, Order: 1));
        var ordered = minions.Concat(powers).OrderByDescending(c => c.Cost).ThenBy(c => c.Order)
            .ThenBy(c => c.Action.Source).ThenBy(c => c.Action.Target).Select(c => c.Action).ToArray();
        if (ordered.Length == 0) return null;
        double before = state.FallbackValue();
        foreach (var action in ordered)
        {
            try
            {
                var next = state.Apply(action);
                // A play that opens a choice (Discover, Adapt, ...) needs a decision this heuristic cannot make safely.
                if (!next.HasPendingChoice && KeepsBoard(facts, next.Facts) && next.FallbackValue() > before + 1e-9) return action;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { }
        }
        return null;
    }

    // Every friendly minion before the play is still on board, no weaker.
    internal static bool KeepsBoard(FairFacts before, FairFacts after)
    {
        var now = new Dictionary<int, FairCard>();
        foreach (var card in after.Own.Board) if (card.EntityId != 0) now[card.EntityId] = card;
        return before.Own.Board.All(c => now.TryGetValue(c.EntityId, out var kept) && kept.Attack >= c.Attack && kept.Health >= c.Health)
            && after.Own.Hero.Health + after.Own.Hero.Armor >= before.Own.Hero.Health + before.Own.Hero.Armor;
    }

    // Card.HistoryDisplay targets that name a carried card's identity (dbf) in the host's text.
    private static readonly GameTag[] CarriedCardTags = { GameTag.TAG_SCRIPT_DATA_NUM_1, GameTag.TAG_SCRIPT_DATA_NUM_2, (GameTag)2921 }; // 2921 = TAG_SCRIPT_DATA_NUM_6 (absent from the vendor engine DLL)
    internal static bool CarriesCard(FairCard card) =>
#if ENGINE_SOURCE
        // Entity-reference hosts (Treant, Pupil, Hammer, Egg) carry an entity id in tag 2, which no dbfId test can recognise.
        IsReferenceHost(card) ||
#endif
        CarriedCardTags.Any(tag => card.Tags.TryGetValue(tag, out int dbf) && dbf >= 1000
        && SabberStoneCore.Model.Cards.FromAssetId(dbf) != null);
#if ENGINE_SOURCE
    private static bool IsReferenceHost(FairCard card) =>
        card.CardId != null && SabberStoneCore.Actions.PayloadRules.ReferencesEntity(SabberStoneCore.Model.Cards.FromId(card.CardId), GameTag.TAG_SCRIPT_DATA_NUM_1)
        && card.Tags.TryGetValue(GameTag.TAG_SCRIPT_DATA_NUM_1, out int reference) && reference != 0;
#endif
    // Own hand card's printed definition (public to its owner): Choose One or Discover needs a choice; a carried card
    // (e.g. a carved Treant's spell) is an effect this heuristic does not judge.
    private static bool NeedsChoice(FairCard card)
    {
        if (Tagged(card, GameTag.CHOOSE_ONE) || Tagged(card, GameTag.DISCOVER) || CarriesCard(card)) return true;
        var definition = card.CardId == null ? null : SabberStoneCore.Model.Cards.FromId(card.CardId);
        return definition != null && (definition[GameTag.CHOOSE_ONE] != 0 || definition[GameTag.DISCOVER] != 0);
    }
}
