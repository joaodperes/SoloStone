using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

public sealed partial class DeckPlanEvaluator : IStateEvaluator
{
    public DeckPlan Plan { get; }
    // AI-leap research knobs (BrainOptions); null and the identity scales reproduce the shipping evaluator exactly.
    public BrainOptions Brain { get; }
    private readonly BrainScales k;
    // Opt-in mining telemetry; includes clamps in unselected reply-search nodes.
    internal bool TrackClamps { get; set; }
    internal int ClampHits { get; private set; }
    public DeckPlanEvaluator(DeckPlan plan, BrainOptions brain = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan)); Brain = brain;
        k = brain == null ? BrainScales.Identity : new BrainScales(brain, plan.DeckId);
        ownLifeBreaks = LifeBreaks(plan.AggressionCurve.Select(p => p.OwnLifeAtMost));
        enemyLifeBreaks = LifeBreaks(plan.AggressionCurve.Select(p => p.OpponentLifeAtMost));
    }
    private readonly long[] ownLifeBreaks, enemyLifeBreaks;
    private static long[] LifeBreaks(IEnumerable<int> thresholds) => thresholds.Where(n => n > 0).Select(n => (long)n)
        .Concat(new[] { long.MaxValue }).Distinct().OrderBy(n => n).ToArray();
    private EvaluatorWeights W => !GoldenAblation.LegacyWeights ? Plan.Weights : new EvaluatorWeights(1.1, 1, 2.2, 1.2, 1.8, 4,
        Plan.Weights.HeldDragon, Plan.Weights.WarptoothSetup, Plan.Weights.ActiveAura, Plan.Weights.BoardSpace, Plan.Weights.BattlecrySetup, Plan.Weights.Herald);
    public double Evaluate(FairFacts f) => Score(f.Own, f.Opponent, f.Turn,
        f.DistinctFriendlyCharactersDamagedThisTurn, f.OwnDeckKnowledge, f.Own.Hand, f.Own.Secrets, f);
    // These terms read facts changed even by an otherwise quiet turn boundary.
    internal bool BoundaryChangesValue(FairFacts f) =>
        (!GoldenAblation.LegacyWeights && Plan.AggressionCurve.Any(p => p.FromTurn == f.Turn + 1))
        || (W.WarptoothSetup > 0 && f.DistinctFriendlyCharactersDamagedThisTurn > 0
            && f.Own.Board.Count + f.Own.Locations.Count < 7
            && (f.Own.Hand.Any(c => c.CardId == "JAIL_421") || f.OwnDeckKnowledge.ContainsKey("JAIL_421")))
        || (Brain != null && (Brain.Value != null || Brain.ClearBonus > 0))
        || (Brain?.ClearRisk ?? GoldenAblation.ClearRisk) > 0
        || f.Own.Secrets.Count != 0 || f.Own.Quests.Count != 0 || f.Opponent.Quests.Count != 0
        || f.Own.OverloadOwed != 0 || f.Own.OverloadLocked != 0
        || f.Opponent.BaseMana < 10 || f.Opponent.OverloadOwed != 0 || f.Opponent.OverloadLocked != 0
        || f.Own.Board.Any(c => c.CardId == "TIME_005" && Tag(c, GameTag.NUM_TURNS_IN_PLAY) == 1)
        || OwnDeckValue && f.Own.DeckAdded.Count + f.Own.DeckUpgraded.Count + f.Opponent.DeckAdded.Count != 0;
    internal double FriendlyBoardCredit(FairCard c) => Math.Max(0, OwnBoard(c) * k.OwnBoard);
    internal double FriendlyAttackCredit(FairCard c, int expiringAttack) => k.OwnBoard *
        (expiringAttack * W.BoardAttack * k.BoardAttack
            - (c.Frozen ? (Math.Min(6, c.Attack) - Math.Min(6, (long)c.Attack - expiringAttack)) * k.Frozen : 0));
    // Inferred prior (BrainOptions): the opponent's starting class and public plays, fixed for one search; the planner
    // sets them, and the clear table is built once per evaluator (Score runs per node).
    private CardClass? inferredClass;
    private IReadOnlyList<string> inferredPlays;
    private IReadOnlyList<CardFrequency> inferredTable;
    internal void SetOpponentEvidence(CardClass startingClass, IEnumerable<string> plays)
    { inferredClass = startingClass; inferredPlays = GoldenData.Copy(plays); inferredTable = null; }
    internal double ReplyValue(FairFacts reply, FairFacts leaf, IReadOnlyList<FairCard> hand, bool terminal = true)
    {
        double value = Score(reply.Opponent, reply.Own, reply.Turn, 0, leaf.OwnDeckKnowledge, hand,
            leaf.Own.Secrets.Where(c => reply.Opponent.SecretEntityIds.Contains(c.EntityId)), reply, true, terminal);
        if (GoldenAblation.ReplyHandCost && Math.Abs(value) < 999000) value -= Hand(reply.Own.Hand);
        return value;
    }
    private double Score(FairPlayer own, FairPlayer enemy, int turn, int damaged, IReadOnlyDictionary<string, int> deck,
        IReadOnlyList<FairCard> hand, IEnumerable<FairCard> secrets, FairFacts facts, bool reversed = false, bool terminal = true)
    {
        if (terminal && (own.PlayState == PlayState.WON || enemy.PlayState == PlayState.LOST)) return 1000000;
        if (terminal && (own.PlayState == PlayState.LOST || enemy.PlayState == PlayState.WON)) return -1000000;
        if (own.PlayState == PlayState.TIED) return -100;
        var w = W; long life = (long)own.Hero.Health + own.Hero.Armor, otherLife = (long)enemy.Hero.Health + enemy.Hero.Armor;
        double ownDraws = DrawCapacity(own), enemyDraws = DrawCapacity(enemy);
        double value = (w.OwnLife * k.OwnLife) * LifeUtility(life, turn, true)
            - (w.EnemyLife * k.EnemyLife) * LifeUtility(otherLife, turn, false)
            + BoardTotal(own, enemy, ref ownDraws, ref enemyDraws, true) * k.OwnBoard
            - BoardTotal(enemy, own, ref enemyDraws, ref ownDraws, false) * k.EnemyBoard + Hand(hand)
            + Math.Min(8, hand.Where(c => !c.Unknown && c.Cost <= Math.Min(10, own.BaseMana + 1))
                .Sum(c => Math.Min(4, Math.Max(0, Cards.FromId(c.CardId).Cost - c.Cost)))) * k.Discount
            // Opponent resource value uses public capacity/debt only, never hidden hand demand.
            + (FutureMana(own, hand) - FutureMana(enemy, Array.Empty<FairCard>())) * k.Discount
            + Weapon(own.Weapon, enemy.Board, own.Board, w.EnemyLife * k.EnemyLife * Aggression(turn, int.MaxValue, int.MaxValue))
            - Weapon(enemy.Weapon, own.Board, enemy.Board, w.OwnLife * k.OwnLife)
            + secrets.Sum(Secret)
            + QuestAssets(own, hand, true) - QuestAssets(enemy, enemy.Hand, false)
            + (w.ActiveAura * k.Aura) * (own.Auras.Sum(c => (long)AuraTicks(c)) - enemy.Auras.Sum(c => (long)AuraTicks(c)))
            + (3 * k.Location) * (own.Locations.Sum(c => (long)c.Durability) - enemy.Locations.Sum(c => (long)c.Durability));
        if (RecurringValue) value += RecurringTerm(own, enemy, turn, life, otherLife);
        if (TriggerValue) value += TriggerTerm(own, enemy);
        if (OwnDeckValue) value += DeckTerm(own, true, turn, life, otherLife) - DeckTerm(enemy, false, turn, life, otherLife);
        if (ZeroDoomed && !reversed) value -= DoomedSwing(enemy, facts) * k.EnemyBoard;
        if (DeathHeld) value -= EnemyHeld(enemy);
        value -= Math.Min(200 * k.Threat, Math.Max(0, PublicThreatBound(facts, reversed) - life + 1) * (50.0 * k.Threat));
        foreach (var hold in GoldenAblation.NoHolds ? new HoldRule[0] : Plan.Holds) {
            var held = hand.Where(c => hold.CardIds.Contains(c.CardId)).ToArray();
            // Playing the last held Aura preserves Timeways coverage instead of destroying its setup value.
            bool activeAura = hold.Condition == HoldCondition.AuraForTimeways
                && own.Auras.Any(c => hold.CardIds.Contains(c.CardId) && AuraTicks(c) > 0);
            if (held.Length == 0 && !activeAura) continue;
            bool enabled = hold.EnabledBy.Count == 0 || hand.Any(c => hold.EnabledBy.Contains(c.CardId));
            if (hold.Condition == HoldCondition.DragonActivator && held.Any(dragon => hand.Any(c => c.EntityId != dragon.EntityId && hold.EnabledBy.Contains(c.CardId))))
                value += (hold.Bonus + w.HeldDragon) * k.Holds;
            if (hold.Condition == HoldCondition.AuraForTimeways && enabled) value += (hold.Bonus + w.ActiveAura) * k.Holds;
            if (hold.Condition == HoldCondition.RefillAfterClear && own.Board.Count >= 3) value += hold.Bonus * k.Holds;
            if (hold.Condition == HoldCondition.ThreatOrLethal && !enemy.Board.Any(c => c.Attack >= 5 || c.Health >= 5)) value += hold.Bonus * k.Holds;
            if (hold.Condition == HoldCondition.AoeWideBoard && enemy.Board.Count < 2 && enemy.Board.Sum(c => (long)c.Attack) < 6) value += hold.Bonus * k.Holds;
            if (hold.Condition == HoldCondition.DamagedTargetWithExcess) {
                var rule = Plan.RemovalThresholds.FirstOrDefault(r => held.Any(c => c.CardId == r.CardId));
                if (rule != null && !enemy.Board.Any(c => c.Attack >= rule.MinimumTargetAttack && c.Health >= rule.MinimumTargetHealth)) value += hold.Bonus * k.Holds;
            }
        }
        value -= EarlyTimethief(own);
        if (w.WarptoothSetup > 0 && own.Board.Count + own.Locations.Count < 7
            && (hand.Any(c => c.CardId == "JAIL_421") || deck.ContainsKey("JAIL_421")))
            value += w.WarptoothSetup * Math.Min(3, damaged) / 4;
        // Engine updates Zee's public progress display (plays since last doubling, 0..4) after every play, including non-Battlecries.
        if (own.HeroPower.CardId == "JAIL_800hp2" && Tag(own.HeroPower, GameTag.TAG_SCRIPT_DATA_NUM_1) == 4
            && hand.Any(c => c.Cost <= own.Mana && UsefulBattlecry(c, own, deck))) value += w.BattlecrySetup;
        if (own.Board.Count + own.Locations.Count >= 7 && hand.Any(c => Roles(c.CardId, CardRole.Rush | CardRole.Payoff))) value -= w.BoardSpace;
        value -= ClearExposure(own, enemy);
        if (Brain != null && Brain.ClearBonus > 0 && enemy.Board.Count == 0) value += Brain.ClearBonus * Math.Min(10, turn);
        if (Brain?.Value != null) value += Brain.ValueBlend * Brain.Value.Correction(own, enemy, turn, hand);
        if (TrackClamps && (value < -998000 || value > 998000)) ClampHits++;
        return Math.Max(-998000, Math.Min(998000, value));
    }
    // Overextension: expected board value that the opponent's best plausible area clear next turn would destroy,
    // excusing the `2 + enemy board` most valuable victims (the minions needed to contest anyway). The clear prior is
    // the fair class population (CardFrequencies) at the opponent's next-turn mana and hand count, plus any public
    // (known) card in its hand; never the hidden hand. Committing extra clear-vulnerable minions while already
    // ahead loses value; clear-proof bodies (health above the printed damage, Divine Shield) cost nothing.
    // Timethief Rafaam (TIME_005) wins only when Ready, and Score returns that terminal value first; one played this turn is
    // otherwise a 10/10 that threw the win condition away. A board penalty, unlike a hand bonus, never rewards drawing or
    // Discovering it. Only this turn's play counts (NUM_TURNS_IN_PLAY 0 at own leaves, 1 once the reply turn starts), so
    // an earlier slam is not a reason to destroy it.
    internal static double EarlyTimethief(FairPlayer own) =>
        GoldenAblation.FabledHold * own.Board.Count(c => c.CardId == "TIME_005" && Tag(c, GameTag.NUM_TURNS_IN_PLAY) <= 1);
    internal double ClearExposure(FairPlayer own, FairPlayer enemy)
    {
        double scale = Brain?.ClearRisk ?? GoldenAblation.ClearRisk;
        // Closing out a low opponent justifies commitment: the risk fades from 25 to 10 enemy life.
        if (GoldenAblation.ClearRiskByLife || Brain != null && Brain.ClearRiskByLife) scale *= Math.Max(0, Math.Min(1, ((long)enemy.Hero.Health + enemy.Hero.Armor - 10) / 15.0));
        int free = 2 + enemy.Board.Count;
        if (scale <= 0 || own.Board.Count <= free) return 0;
        CardClass heroClass;
        try { heroClass = Cards.FromId(enemy.Hero.CardId).Class; } catch (Exception) { return 0; }
        int mana = Math.Min(10, enemy.BaseMana + 1);
        var clears = new List<KeyValuePair<double, double>>();
        int unknown = Math.Min(10, enemy.Hand.Count(c => c.Unknown) + 1);
        Func<int, double> loss = damage => own.Board
            .Where(m => damage == int.MaxValue || !m.DivineShield && damage >= m.Health)
            .Select(OwnBoard).OrderByDescending(v => v).Skip(free).Sum();
        // Inferred prior (BrainOptions): clears weighted by the deck posterior from the opponent's public plays. Every
        // Score is from the bot's side (ReplyValue too: its `own` is the bot), so `enemy` is always the real opponent.
        bool inferred = Brain != null && Brain.ReplyPrior == ReplyPrior.Inferred && inferredClass.HasValue;
        if (inferred && inferredTable == null) inferredTable = DeckInference.Table(inferredClass.Value, inferredPlays, Brain);
        foreach (var clear in inferred ? CardFrequencies.Clears(inferredTable, mana)
            : CardFrequencies.Clears(heroClass, mana)) {
            double miss = 1;
            for (int i = 0; i < unknown; i++) miss *= Math.Max(0, (30 - clear.Value - i) / (30 - i));
            clears.Add(new KeyValuePair<double, double>(loss(clear.Key), 1 - miss));
        }
        foreach (var card in enemy.Hand.Where(c => !c.Unknown && c.Type == CardType.SPELL && c.Cost <= mana)) {
            if (!CardFrequency.IsAreaThreat((Cards.FromId(card.CardId).Text ?? "").Replace('_', ' ').ToLowerInvariant())) continue;
            int damage = CardFrequencies.ClearDamage(card.CardId);
            if (damage > 0) clears.Add(new KeyValuePair<double, double>(loss(damage), 1));
        }
        // The opponent casts its most damaging available clear: each clear counts only when no better one is held.
        double expected = 0, none = 1;
        foreach (var clear in clears.Where(c => c.Key > 0).OrderByDescending(c => c.Key)) {
            expected += none * clear.Value * clear.Key; none *= 1 - clear.Value;
        }
        return scale * expected;
    }
    // Forced-loss tiebreak: count material already fighting, not payouts the lethal reply can bypass.
    // Reply simulation already accounts for effects that actually prevent the loss.
    internal double Resistance(FairFacts f) => f.Own.Board.Sum(Priced) - f.Opponent.Board.Sum(Priced)
        + W.OwnLife * Life((long)f.Own.Hero.Health + f.Own.Hero.Armor);
    internal static int UnusedResources(FairBotState state)
    {
        var useful = state.OrderedActions.TakeWhile(a => a.Type != SabberStoneCore.Tasks.PlayerTasks.PlayerTaskType.END_TURN).ToList();
#if ENGINE_SOURCE
        useful.AddRange(state.Actions.Where(a => a.Type == SabberStoneCore.Tasks.PlayerTasks.PlayerTaskType.ACTIVATE_LOCATION));
#endif
        if (useful.Count == 0) return 0;
        return state.Facts.Own.Mana + useful.Where(a => a.Type == SabberStoneCore.Tasks.PlayerTasks.PlayerTaskType.HERO_POWER
#if ENGINE_SOURCE
            || a.Type == SabberStoneCore.Tasks.PlayerTasks.PlayerTaskType.ACTIVATE_LOCATION
#endif
            ).Select(a => a.Source).Distinct().Count() * 2;
    }
    internal bool Roles(string id, CardRole role) => Plan.CardRoles.Any(r => r.CardId == id && (r.Roles & role) != 0);
    internal bool UsefulBattlecry(FairCard card, FairPlayer own, IReadOnlyDictionary<string, int> deck)
    {
        if (card.Unknown || card.Type != CardType.MINION || Cards.FromId(card.CardId)[GameTag.BATTLECRY] == 0
            || !Roles(card.CardId, CardRole.Payoff | CardRole.Herald | CardRole.Refill)) return false;
        if (card.CardId == "DINO_411") return deck.Any(p => p.Value > 0
            && Cards.FromId(p.Key).Type == CardType.MINION && Cards.FromId(p.Key).ATK == 0);
        if (card.CardId == "JAIL_457") return own.Board.Count > 0;
        return true;
    }
    internal double Hand(IEnumerable<FairCard> cards)
    {
        if (Brain?.HandFlat == null)
            return cards.Sum(c => c.Unknown ? 5.5 * k.UnknownCard : HandCard(c)) * k.Hand;
        int count = cards.Count();
        return (Math.Min(4, count) * Brain.HandFlat.Value + Math.Max(0, Math.Min(7, count) - 4) * Brain.HandTail) * k.Hand;
    }
    private double HandCard(FairCard card)
    {
        double generic = 2 + Math.Min(8, Math.Max(card.Cost, Cards.FromId(card.CardId).Cost)) * W.Hand;
        if (DeathHeld && card.Held != null) return Math.Max(generic, HeldValue(card, true));
        // A held attack weapon promises the same charges as an equipped one, but still costs mana to equip.
        if (card.Type == CardType.WEAPON && card.Attack > 0)
            return Math.Min(generic, .75 * Weapon(card, Array.Empty<FairCard>(), Array.Empty<FairCard>(), W.EnemyLife * k.EnemyLife));
        return generic;
    }
    private static int AuraTicks(FairCard c) => Math.Max(0, Tag(c, GameTag.QUEST_PROGRESS_TOTAL) - Tag(c, GameTag.QUEST_PROGRESS));
    private double Board(FairCard c) => 2 * k.MinionBase + c.Attack * (W.BoardAttack * k.BoardAttack)
        + (HitPays(c) ? (double)c.Health + c.Damage : (DeathChip ? ChipShare(c) : 1) * c.Health) * (W.BoardHealth * k.BoardHealth)
        + c.Cost * (.6 * k.BoardCost) + (c.Taunt ? 2 * k.Taunt : 0) + (c.DivineShield ? W.DivineShield * k.DivineShield : 0)
        - (c.Frozen ? Math.Min(6, c.Attack) * k.Frozen : 0);
    private double OwnBoard(FairCard c) => Priced(c);
    internal static bool Deathrattle(FairCard c) => c.DeathValue.Any;
    private double BoardTotal(FairPlayer owner, FairPlayer enemy, ref double ownDraws, ref double enemyDraws, bool ownerIsBot)
    {
        double value = 0;
        for (int i = 0; i < owner.Board.Count; i++) value += Priced(owner.Board[i]);
        return value + (DeathMeasured ? MeasuredDeath(owner, enemy, ref ownDraws, ref enemyDraws, ownerIsBot)
            : DeathValue(owner, enemy, ref ownDraws, ref enemyDraws));
    }
    // Incremental pending value only. Once death resolves, the new board/hand replaces this term.
    internal double DeathValue(FairPlayer owner, FairPlayer enemy)
    {
        double ownDraws = DrawCapacity(owner), enemyDraws = DrawCapacity(enemy);
        return DeathMeasured ? MeasuredDeath(owner, enemy, ref ownDraws, ref enemyDraws, true)
            : DeathValue(owner, enemy, ref ownDraws, ref enemyDraws);
    }
    private static double DrawCapacity(FairPlayer recipient) => Math.Min(Math.Max(0, 10 - recipient.Hand.Count), recipient.DeckCount);
    private double DeathValue(FairPlayer owner, FairPlayer enemy, ref double ownDraws, ref double enemyDraws)
    {
        double value = 0;
        for (int i = 0; i < owner.Board.Count; i++) {
            var c = owner.Board[i];
            if ((!c.DeathValue.Any && !c.RebornValue.Any) || c.Untouchable) continue;
            int pressure = 0;
            if (!c.Immune && !c.Stealth && !(ZeroDoomed && c.Doomed)) // doomed: death is certain, no killability credit
                for (int j = 0; j < enemy.Board.Count; j++) {
                    var attacker = enemy.Board[j];
                    if (!attacker.CantAttack && !attacker.Untouchable && !attacker.Frozen)
                        pressure = Math.Max(pressure, attacker.Attack);
                }
            // Long-run retention still matters on an empty board; a publicly killable body pays sooner.
            double discount = .65 + .25 * Math.Min(1, pressure / (double)Math.Max(1, (long)c.Health + (c.DivineShield ? pressure : 0)));
            int slots = Math.Max(0, 8 - owner.Board.Count - owner.Locations.Count);
            value += discount * Payload(c.DeathValue, slots, ref ownDraws, ref enemyDraws)
                + c.DeathValue.Other;
            // Deathrattle summons resolve before Reborn and can occupy its slot.
            if (c.RebornValue.Any && c.DeathValue.Bodies < slots) {
                value += discount * Payload(c.RebornValue, 1, ref ownDraws, ref enemyDraws);
                // The fresh copy regains printed Deathrattle, never attachments or another Reborn.
                value += discount * (.65 * Payload(c.RebornDeathValue, Math.Max(1, slots - (int)c.DeathValue.Bodies),
                    ref ownDraws, ref enemyDraws) + c.RebornDeathValue.Other);
            }
        }
        return value * k.Deathrattle;
    }
    private double Payload(FairDeathValue d, int slots, ref double ownDraws, ref double enemyDraws)
    {
        // Each pending payout reserves public recipient capacity, including a Reborn continuation.
        double cards = Math.Min(d.Cards, ownDraws), opponentCards = Math.Min(d.OpponentCards, enemyDraws);
        ownDraws -= cards; enemyDraws -= opponentCards;
        double fit = d.Bodies > 0 ? Math.Min(1, slots / d.Bodies) : 0;
        return fit * (2 * k.MinionBase * d.Bodies + d.Attack * W.BoardAttack * k.BoardAttack
            + d.Health * W.BoardHealth * k.BoardHealth + .6 * k.BoardCost * d.Cost
            + 2 * k.Taunt * d.Taunt + W.DivineShield * k.DivineShield * d.Shield)
            + (cards - opponentCards) * 5.5 * k.Hand * k.UnknownCard;
    }
    private double Life(long life) => Brain != null && Brain.SqrtLife ? 2 * Math.Sqrt(15.0 * Math.Max(0, life))
        : life - Math.Max(0, 12 - life) * (2 * k.LowLife);
    private double Aggression(int turn, int ownLife, int enemyLife)
    {
        double face = 1;
        if (!GoldenAblation.LegacyWeights)
            foreach (var point in Plan.AggressionCurve)
                if (turn >= point.FromTurn && ownLife <= point.OwnLifeAtMost && enemyLife <= point.OpponentLifeAtMost)
                    face = 1 + (point.FaceWeight - 1) * k.Aggression;
        return Math.Max(.05, face);
    }
    // Integrate positive marginal values, never reprice HP already lost. Own-life overrides express
    // defensive intent as extra survival value; keeping axes separate prevents healing/damage cross-jumps.
    private double LifeUtility(long life, int turn, bool own)
    {
        double value = 0; long start = 0;
        double normal = Aggression(turn, int.MaxValue, int.MaxValue);
        foreach (long boundary in own ? ownLifeBreaks : enemyLifeBreaks) {
            long end = Math.Min(Math.Max(0, life), boundary);
            int threshold = (int)Math.Min(int.MaxValue, boundary);
            double marginal = own ? 1 + Math.Max(0, normal - Aggression(turn, threshold, int.MaxValue))
                : Aggression(turn, int.MaxValue, threshold);
            value += (Life(end) - Life(start)) * marginal;
            if (end >= life) break;
            start = end;
        }
        return value;
    }
    private static double FutureMana(FairPlayer own, IReadOnlyList<FairCard> hand)
    {
        // Capacity, not unspent mana: spending a crystal this turn never changes this term.
        // Price this owner's retained capacity and next refresh, independent of whose turn it is.
        int capacity = Math.Min(10, own.BaseMana);
        int locked = own.OverloadLocked;
        int nextDebt = own.OverloadOwed;
        int demand = hand.Where(c => !c.Unknown && c.Cost > 0).Select(c => Math.Min(10, c.Cost)).DefaultIfEmpty(0).Max();
        // Charge only inaccessible held plays: a playable card must not earn extra value merely for staying in hand.
        Func<int, double> access = mana => {
            int usable = Math.Max(0, mana);
            return 2 * Math.Sqrt(usable) - (demand <= usable ? 0 : .5 + (demand - usable) / (double)demand);
        };
        return access(capacity - locked) + .6 * access(Math.Min(10, capacity + 1) - nextDebt);
    }
    // Cheap public-board screen, not a proof against cards in hand or arbitrary triggers.
    internal static bool PublicBoardSurvives(FairFacts f) => f.Own.PlayState != PlayState.LOST
        && f.Opponent.PlayState != PlayState.WON
        && PublicThreatBound(f) < (long)f.Own.Hero.Health + f.Own.Hero.Armor;
    internal static long PublicThreatBound(FairFacts f, bool reversed = false)
    {
        var cached = reversed ? f.OpponentThreatBound : f.OwnThreatBound;
        if (cached.HasValue) return cached.Value;
        var own = reversed ? f.Opponent : f.Own;
        long bound = PublicFaceDamage(own, reversed ? f.Own : f.Opponent, f.CurrentPlayerId,
            (long)own.Hero.Health + own.Hero.Armor);
        if (reversed) f.OpponentThreatBound = bound; else f.OwnThreatBound = bound;
        return bound;
    }
    // survival-scoring coverage only. PublicThreatBound's below-life shortcut is inappropriate for
    // ranking defenses; use exact blockers plus the existing fair class-prior sampler and known hand.
    // shortcut: printed burn/Charge and current spell power omit conditional/dynamic combos; reply search
    // remains authoritative. Extend this allowance when engine evidence identifies missed coverage.
    private readonly Dictionary<(CardClass, int, int), IReadOnlyList<HandSample>> incomingHands = new Dictionary<(CardClass, int, int), IReadOnlyList<HandSample>>();
    private readonly Dictionary<string, int> incomingBurn = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, double> incomingAllowance = new Dictionary<string, double>(StringComparer.Ordinal);
    internal double PublicIncomingDamage(FairFacts f)
    {
        var enemy = f.Opponent;
        bool active = enemy.PlayerId == f.CurrentPlayerId;
        int mana = active ? enemy.Mana : Math.Max(0, Math.Min(10, enemy.BaseMana + 1) - enemy.OverloadOwed);
        int unknown = Math.Min(10, enemy.Hand.Count(c => c.Unknown) + (!active && enemy.DeckCount > 0 && enemy.Hand.Count < 10 ? 1 : 0));
        int spellPower = enemy.Board.Sum(c => Math.Max(0, Tag(c, GameTag.SPELLPOWER)));
        bool heroPower = !active || !enemy.HeroPower.Exhausted, boardSlot = enemy.Board.Count < 7;
        var known = enemy.Hand.Where(c => !c.Unknown).ToArray();
        // The allowance reads only these public inputs. One cached value per distinct input keeps per-line
        // margin work at board allocation plus O(hand) key building (see TurnBeam.SetAttackMargin).
        string allowanceKey = string.Join("|", (int)enemy.Class, mana, unknown, spellPower, boardSlot ? 1 : 0,
            heroPower ? enemy.HeroPower.CardId + ":" + enemy.HeroPower.Cost : "-",
            string.Join(",", known.Select(c => c.CardId + ":" + c.Cost + ":" + c.Attack)));
        if (!incomingAllowance.TryGetValue(allowanceKey, out double allowance)) {
            var key = (enemy.Class, unknown, mana);
            if (!incomingHands.TryGetValue(key, out var samples)) {
                var table = Brain != null && Brain.ReplyPrior == ReplyPrior.Inferred && inferredClass.HasValue
                    ? (inferredTable ?? (inferredTable = DeckInference.Table(inferredClass.Value, inferredPlays, Brain)))
                    : CardFrequencies.ForOrEmpty(enemy.Class);
                samples = table.Count == 0 ? new[] { new HandSample(new string[0], 1) }
                    : CardFrequencies.Sample(table, unknown, 4, mana, 0, inferredPlays);
                incomingHands[key] = samples;
            }
            int powerDamage = heroPower ? CardFrequencies.FaceDamage(enemy.HeroPower.CardId) : 0;
            foreach (var sample in samples) {
                var items = known.Select(c => new { c.CardId, c.Cost, c.Attack })
                    .Concat(sample.Cards.Select(id => new { CardId = id, Cost = Cards.FromId(id).Cost, Attack = Cards.FromId(id).ATK }));
                // 0/1 knapsack over next-turn mana: hero power once, each public/sampled card at most once.
                var best = new double[mana + 1];
                for (int m = Math.Max(0, enemy.HeroPower.Cost); m <= mana; m++) best[m] = powerDamage;
                foreach (var item in items) {
                    var card = Cards.FromId(item.CardId);
                    if (item.Cost > mana) continue;
                    if (!incomingBurn.TryGetValue(item.CardId, out int burn)) {
                        burn = CardFrequencies.FaceDamage(item.CardId);
                        incomingBurn[item.CardId] = burn;
                    }
                    double damage = burn + (burn > 0 && card.Type == CardType.SPELL ? spellPower : 0)
                        + (card.Charge && boardSlot ? Math.Max(0, item.Attack) * (card.Windfury ? 2 : 1) : 0);
                    int cost = Math.Max(0, item.Cost);
                    for (int m = mana; m >= cost; m--) best[m] = Math.Max(best[m], best[m - cost] + damage);
                }
                allowance += sample.Weight * best[mana];
            }
            incomingAllowance[allowanceKey] = allowance;
        }
        return PublicFaceDamage(f.Own, enemy, f.CurrentPlayerId) + allowance;
    }
    [ThreadStatic] private static ThreatSearch threatSearch;
    internal static long PublicFaceDamage(FairPlayer own, FairPlayer enemy, int currentPlayer, long lifeThreshold = 0)
    {
        bool active = enemy.PlayerId == currentPlayer;
        long face = 0; int count = 0;
        for (int i = 0; i < enemy.Board.Count; i++) {
            var c = enemy.Board[i]; int hits = ThreatCount(c, active);
            count += hits;
            if (!c.CantAttackHeroes && !(active && c.RushOnly)) face += (long)hits * c.Attack;
        }
        var hero = enemy.Hero;
        int attack = active ? hero.Attack : (enemy.Weapon?.Attack ?? 0);
        int heroHits = hero.Frozen || hero.CantAttack || active && hero.Exhausted || attack <= 0 ? 0
            : Math.Max(0, hero.AttackLimit - (active ? hero.AttacksThisTurn : 0));
        if (enemy.Weapon != null) heroHits = Math.Max(0, Math.Min(heroHits, enemy.Weapon.Durability));
        count += heroHits;
        if (!hero.CantAttackHeroes) face += (long)heroHits * attack;
        // Score's penalty is zero below life; the survival screen is true there too. An upper bound
        // below life therefore preserves BOTH outputs; exact callers omit the threshold.
        if (face == 0 || face < lifeThreshold || count > 63) return face;
        int blockers = 0;
        for (int i = 0; i < own.Board.Count; i++) if (IsBlocker(own.Board[i])) blockers++;
        if (blockers == 0) return face;
        var search = threatSearch ?? (threatSearch = new ThreatSearch());
        search.Reset(own.Board);
        for (int i = 0; i < enemy.Board.Count; i++) {
            var c = enemy.Board[i]; int hits = ThreatCount(c, active);
            for (int j = 0; j < hits; j++) search.Add(c.Attack, !c.CantAttackHeroes && !(active && c.RushOnly));
        }
        for (int i = 0; i < heroHits; i++) search.Add(attack, !hero.CantAttackHeroes);
        // Maximize remaining face attacks over blocker allocations. Ignoring retaliation/trigger deaths
        // overestimates threat, which is safe for the survival screen (including repeated Windfury hits).
        long maximum = search.Search(0, 0, search.Health[0], search.Shield[0], face);
        return search.Capped ? face : maximum;
    }
    private static bool IsBlocker(FairCard c) => c.Taunt && !c.Stealth && !c.Untouchable && !c.Immune;
    private sealed class ThreatSearch
    {
        private const int StateLimit = 2048, MemoSize = 8192, TransitionLimit = StateLimit * 63;
        private struct Entry
        {
            internal ulong Used;
            internal int Blocker, Generation;
            internal long Health, Value;
            internal bool Shield;
        }
        private readonly Entry[] memo = new Entry[MemoSize];
        private readonly ThreatAttack[] attacks = new ThreatAttack[63];
        internal readonly int[] Health = new int[7];
        internal readonly bool[] Shield = new bool[7];
        private int generation, attackCount, blockerCount, visits, transitions;
        internal bool Capped;
        internal void Reset(IReadOnlyList<FairCard> board)
        {
            if (generation == int.MaxValue) { Array.Clear(memo, 0, memo.Length); generation = 0; }
            generation++; attackCount = blockerCount = visits = transitions = 0; Capped = false;
            for (int i = 0; i < board.Count; i++) {
                var c = board[i]; if (!IsBlocker(c)) continue;
                Health[blockerCount] = c.Health; Shield[blockerCount++] = c.DivineShield;
            }
        }
        internal void Add(int damage, bool face)
        {
            // Preserve the old Face/Damage ordering and interchangeable-hit pruning without LINQ.
            int i = attackCount++;
            while (i > 0 && (attacks[i - 1].Face && !face || attacks[i - 1].Face == face && attacks[i - 1].Damage > damage)) {
                attacks[i] = attacks[i - 1]; i--;
            }
            attacks[i] = new ThreatAttack(damage, face);
        }
        private int Slot(ulong used, int blocker, long health, bool shield)
        {
            ulong hash = used ^ (used >> 32) ^ ((ulong)(uint)health * 2654435761UL)
                ^ ((ulong)(uint)blocker * 2246822519UL) ^ (shield ? 3266489917UL : 0);
            hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
            int slot = (int)(hash & (MemoSize - 1));
            while (memo[slot].Generation == generation && (memo[slot].Used != used || memo[slot].Blocker != blocker
                || memo[slot].Health != health || memo[slot].Shield != shield)) slot = (slot + 1) & (MemoSize - 1);
            return slot;
        }
        internal long Search(ulong used, int blocker, long health, bool shield, long remainingFace)
        {
            if (health <= 0) {
                if (++blocker == blockerCount) return remainingFace;
                health = Health[blocker]; shield = Shield[blocker];
            }
            if (remainingFace == 0) return 0;
            int slot = Slot(used, blocker, health, shield);
            if (memo[slot].Generation == generation) return memo[slot].Value;
            if (++visits > StateLimit) { Capped = true; return remainingFace; }
            long best = 0;
            for (int i = 0; i < attackCount; i++) {
                // Retain the old 2048-state result/fallback, while also explicitly bounding loop work
                // (charges, not minions). This ceiling cannot truncate a formerly uncapped search.
                if (++transitions > TransitionLimit) { Capped = true; return remainingFace; }
                if ((used & (1UL << i)) != 0) continue;
                // Identical unused hits are interchangeable, including Windfury charges.
                if (i > 0 && attacks[i].Damage == attacks[i-1].Damage && attacks[i].Face == attacks[i-1].Face
                    && (used & (1UL << (i-1))) == 0) continue;
                best = Math.Max(best, Search(used | (1UL << i), blocker,
                    shield ? health : health - attacks[i].Damage, false,
                    remainingFace - (attacks[i].Face ? attacks[i].Damage : 0)));
                if (Capped || best == remainingFace) break;
            }
            // Descendants may have occupied our original empty slot.
            slot = Slot(used, blocker, health, shield);
            memo[slot] = new Entry { Used = used, Blocker = blocker, Health = health, Shield = shield, Value = best, Generation = generation };
            return best;
        }
    }
    private static int ThreatCount(FairCard c, bool active)
    {
        if (c.CantAttack || c.Untouchable || c.Frozen || c.Attack <= 0) return 0;
        if (active && c.Exhausted) return 0;
        return Math.Max(0, c.AttackLimit - (active ? c.AttacksThisTurn : 0));
    }
    private readonly struct ThreatAttack
    {
        internal readonly int Damage;
        internal readonly bool Face;
        internal ThreatAttack(int damage, bool face) { Damage = damage; Face = face; }
    }
    private double Weapon(FairCard weapon, IReadOnlyList<FairCard> targets, IReadOnlyList<FairCard> friendly, double faceValue)
    {
        if (weapon == null || weapon.Durability <= 0) return 0;
        // Future charges are delayed and may be replaced. Retain extra value when a real trade is available.
        double trade = targets.Where(c => !c.Stealth && !c.Immune && !c.Untouchable)
            .Select(c => Math.Min(weapon.Attack, c.Health) * .4).DefaultIfEmpty(0).Max();
        // Axe pings both boards. Resolved pings are simulated; this is only the remaining-charge option value.
        double ping = weapon.CardId == "TLC_478" ? .4 * (targets.Count(c => !c.Immune && !c.Untouchable)
            - friendly.Count(c => !c.Immune && !c.Untouchable)) : 0;
        return Math.Max(0, weapon.Attack * Math.Min(.6, faceValue * .5) + trade + ping)
            * (2 * (1 - Math.Pow(.5, weapon.Durability))) * k.Weapon;
    }
    // shortcut: generic bounded retained-effect credit; add card-specific values when measured cases justify them.
    private double Secret(FairCard secret) => Math.Min(8, 2 + Math.Min(3, Math.Max(0, secret.Cost)) * W.Hand);
    internal static int Tag(FairCard c, GameTag tag) { int v; return c.Tags.TryGetValue(tag, out v) ? v : 0; }
    internal double DiscoverBonus(string id, string source)
    {
        foreach (var pref in Plan.DiscoverPreferences.Where(p => source == null || p.SourceCardId == source)) {
            int i = pref.PreferredCardIds.ToList().IndexOf(id);
            if (i >= 0) return (pref.PreferredCardIds.Count - i) * .001;
        }
        return 0;
    }
}

// Strongest value-only key available before H5's AiFingerprint lands: all exposed facts and sorted tags,
// plus legal options (covers pending choices). Not Describe(), which omits shields/costs/history damage.
// shortcut: unexposed enchantments are not hashed; a continuation is still re-checked for legality before reuse.
public static class GoldenPublicKey
{
    public static string For(FairBotState state) => For(state, true);
    internal static string For(FairBotState state, bool includeEffects)
    {
        var f = state.Facts; var b = new StringBuilder();
        b.Append(f.Turn).Append('|').Append(f.CurrentPlayerId).Append('|').Append(f.DistinctFriendlyCharactersDamagedThisTurn)
            .Append('|').Append(f.OpponentSecretCount);
        foreach (var p in new[] { f.Own, f.Opponent }) {
            b.Append('|').Append(p.PlayerId).Append(':').Append(p.Mana).Append(':').Append(p.BaseMana).Append(':').Append(p.DeckCount).Append(':').Append((int)p.PlayState)
                .Append(':').Append(p.OverloadOwed).Append(':').Append(p.OverloadLocked)
                .Append(':').Append(string.Join(",", p.SecretEntityIds));
            if (p.PendingQuestRewards.Count > 0) b.Append(":quest-rewards:").Append(string.Join(",", p.PendingQuestRewards));
            foreach (var reward in p.OpaqueQuestRewards.OrderBy(r => r.Key)) b.Append(":opaque-quest-reward:").Append(reward.Key).Append('=').Append(reward.Value);
            foreach (var zone in new[] { new[] { p.Hero, p.HeroPower, p.Weapon }, p.Board, p.Hand, p.Locations, p.Auras, p.Secrets }
                .Concat(p.Quests.Count == 0 && p.QuestEffects.Count == 0 ? new IReadOnlyList<FairCard>[0]
                    : new[] { p.Quests, p.QuestEffects })) {
                b.Append('/');
                foreach (var c in zone.Where(c => c != null).OrderBy(c => c.EntityId)) {
                    b.Append(c.EntityId).Append(':').Append(c.CardId).Append(':').Append(c.Position).Append(':').Append(c.Cost)
                        .Append(':').Append(c.Attack).Append(':').Append(c.Health).Append(':').Append(c.Armor).Append(':').Append(c.Durability)
                        .Append(':').Append(c.Taunt).Append(':').Append(c.DivineShield).Append(':').Append(c.Frozen).Append(':').Append(c.Exhausted)
                        .Append(':').Append(c.CantAttack).Append(':').Append(c.CantAttackHeroes).Append(':').Append(c.Untouchable)
                        .Append(':').Append(c.Stealth).Append(':').Append(c.Immune).Append(':').Append(c.RushOnly)
                        .Append(':').Append(c.AttackLimit).Append(':').Append(c.AttacksThisTurn);
                    if (c.DeathValue.Any || c.RebornValue.Any) {
                        b.Append(":death"); AppendDeath(b, c.DeathValue);
                        AppendDeath(b, c.RebornValue); AppendDeath(b, c.RebornDeathValue);
                    }
                    // Grants the old summary prices alike can differ in measured value; held minions change a hand card's value.
                    if (c.HasAttachedEffects && c.Death != null) b.Append(":dv").Append(c.Death.Key);
                    if (c.Held != null) b.Append(":held").Append(string.Join("+", c.Held));
                    if (c.GeneratedQuest) b.Append(":generated-quest");
                    foreach (var tag in c.Tags.OrderBy(t => (int)t.Key)) b.Append(',').Append((int)tag.Key).Append('=').Append(tag.Value);
                    b.Append(';');
                }
            }
        }
        foreach (var pair in f.OwnDeckKnowledge.OrderBy(p => p.Key, StringComparer.Ordinal)) b.Append('|').Append(pair.Key).Append('=').Append(pair.Value);
        if (includeEffects) b.Append("|effects:").Append(state.OwnVisibleEffectsKey());
        foreach (var action in state.Actions.OrderBy(a => a.ToString(), StringComparer.Ordinal)) b.Append('|').Append(action);
        return b.ToString();
    }
    private static void AppendDeath(StringBuilder b, FairDeathValue d) =>
        b.Append(':').Append(d.Bodies).Append(',').Append(d.Attack).Append(',').Append(d.Health)
            .Append(',').Append(d.Cost).Append(',').Append(d.Taunt).Append(',').Append(d.Shield)
            .Append(',').Append(d.Cards).Append(',').Append(d.OpponentCards).Append(',').Append(d.Other);
}
