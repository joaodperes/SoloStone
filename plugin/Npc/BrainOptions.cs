using System;
using System.Collections.Generic;
using System.Linq;

// AI-leap research knobs (2026-10-25, docs/roadmap/2026-10-25-npc-ai-leap-research.md). A profile's Budget.Brain
// (skills JSON `budget.brain`) changes how the golden planner VALUES positions and samples the opponent's reply;
// it never changes what the planner may see (FairBotState) or the node budget. Null = the shipping golden brain,
// and every default below reproduces it exactly (all scales 1, so each folded constant multiplies by exactly 1.0).
public enum ReplyPrior
{
    Class,     // shipping: fair class population frequencies (CardFrequencies)
    Inferred   // fair Bayesian deck inference: population decks of the class reweighted by the opponent's public plays
}

public sealed class BrainOptions
{
    // Multipliers on DeckPlanEvaluator terms; unknown names are rejected at load time.
    public static readonly IReadOnlyList<string> ScaleNames = Array.AsReadOnly(new[] {
        "ownLife", "enemyLife", "lowLife", "ownBoard", "enemyBoard", "boardAttack", "boardHealth", "boardCost", "minionBase",
        "taunt", "divineShield", "frozen", "deathrattle", "hand", "unknownCard", "discount", "weapon", "aura", "location",
        "threat", "holds", "aggression" });
    private readonly Dictionary<string, double> scales;
    public double? ClearRisk { get; }
    public bool ClearRiskByLife { get; }
    public ReplyPrior ReplyPrior { get; }
    // Inferred prior: weight of the class-population fallback mixed into the posterior (unlisted decks, tech cards).
    public double InferenceFloor { get; }
    // Inferred prior: research-only hold-out. These population lists are dropped from the inference deck set, so
    // self-play on them cannot overstate inference against a human whose exact list is not in the population.
    public IReadOnlyList<string> ExcludedDecks { get; }
    public LearnedValue Value { get; }
    public double ValueBlend { get; }
    // Evaluator shapes from the 2018-2020 HS AI Competition winners (docs: npc-ai-leap research, bot-code study).
    // HandFlat: hand cards are worth HandFlat each (first four) and HandTail (cards 5-7), 0 after, with no cost term.
    public double? HandFlat { get; }
    public double HandTail { get; }
    // SqrtLife: hero life scored as 2*sqrt(15*life) (slope 1 at 15 life, steeper when low) instead of linear + low-life penalty.
    public bool SqrtLife { get; }
    // ClearBonus: per turn (capped at 10) while the enemy board is empty.
    public double ClearBonus { get; }
    // Prune: TurnBeam expands one action per equivalence class (identical copies, attackers, targets), own and reply beams.
    public bool Prune { get; }
    // Per-deck overlay: scales multiplied onto the tier scales when the evaluator's plan is for that deck id.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> DeckScales { get; }
    public BrainOptions(IDictionary<string, double> scales = null, double? clearRisk = null, bool clearRiskByLife = false,
        ReplyPrior replyPrior = ReplyPrior.Class, double inferenceFloor = .15, IEnumerable<string> excludedDecks = null,
        LearnedValue value = null, double valueBlend = 1, double? handFlat = null, double handTail = 0, bool sqrtLife = false,
        double clearBonus = 0, bool prune = false, IDictionary<string, IDictionary<string, double>> deckScales = null)
    {
        Prune = prune;
        var decks = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var deck in deckScales ?? new Dictionary<string, IDictionary<string, double>>()) {
            foreach (var pair in deck.Value)
                if (!ScaleNames.Contains(pair.Key) || double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
                    throw new ArgumentException("Bad deck scale " + deck.Key + "." + pair.Key);
            decks[deck.Key] = new Dictionary<string, double>(deck.Value, StringComparer.Ordinal);
        }
        DeckScales = decks;
        if (handFlat.HasValue && !(handFlat.Value >= 0) || !(handTail >= 0) || !(clearBonus >= 0)) throw new ArgumentException("hand/clear values must be >= 0.");
        HandFlat = handFlat; HandTail = handTail; SqrtLife = sqrtLife; ClearBonus = clearBonus;
        this.scales = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in scales ?? new Dictionary<string, double>()) {
            if (!ScaleNames.Contains(pair.Key)) throw new ArgumentException("Unknown brain scale: " + pair.Key);
            if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value)) throw new ArgumentException("Bad brain scale: " + pair.Key);
            this.scales[pair.Key] = pair.Value;
        }
        if (clearRisk.HasValue && !(clearRisk.Value >= 0)) throw new ArgumentException("clearRisk must be >= 0.");
        if (!(inferenceFloor > 0 && inferenceFloor <= 1)) throw new ArgumentException("inferenceFloor must be in (0, 1].");
        ClearRisk = clearRisk; ClearRiskByLife = clearRiskByLife; ReplyPrior = replyPrior; InferenceFloor = inferenceFloor;
        ExcludedDecks = GoldenData.Copy((excludedDecks ?? Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal));
        Value = value; ValueBlend = valueBlend;
    }
    // Copy with these multipliers applied on top of the current scales (personality styles); every other field kept.
    public BrainOptions WithScales(IReadOnlyDictionary<string, double> extra)
    {
        var merged = new Dictionary<string, double>(scales, StringComparer.Ordinal);
        foreach (var pair in extra) merged[pair.Key] = Scale(pair.Key) * pair.Value;
        return new BrainOptions(merged, ClearRisk, ClearRiskByLife, ReplyPrior, InferenceFloor, ExcludedDecks, Value, ValueBlend,
            HandFlat, HandTail, SqrtLife, ClearBonus, Prune,
            DeckScales.ToDictionary(d => d.Key, d => (IDictionary<string, double>)d.Value.ToDictionary(p => p.Key, p => p.Value)));
    }
    // Copy with this reply prior; every other field kept (the experimental Options switch, NpcWorldRuntime.Begin).
    public BrainOptions WithReplyPrior(ReplyPrior prior) =>
        new BrainOptions(new Dictionary<string, double>(scales, StringComparer.Ordinal), ClearRisk, ClearRiskByLife, prior, InferenceFloor, ExcludedDecks, Value, ValueBlend,
            HandFlat, HandTail, SqrtLife, ClearBonus, Prune,
            DeckScales.ToDictionary(d => d.Key, d => (IDictionary<string, double>)d.Value.ToDictionary(p => p.Key, p => p.Value)));
    public double Scale(string name)
    {
        double value;
        return scales.TryGetValue(name, out value) ? value : 1;
    }
    public double Scale(string name, string deckId)
    {
        IReadOnlyDictionary<string, double> deck; double extra;
        return deckId != null && DeckScales.TryGetValue(deckId, out deck) && deck.TryGetValue(name, out extra) ? Scale(name) * extra : Scale(name);
    }
    public IReadOnlyDictionary<string, double> Scales => scales;
}

// Folded evaluator constants (one allocation per evaluator, no dictionary lookups in the hot path).
internal sealed class BrainScales
{
    internal static readonly BrainScales Identity = new BrainScales(null);
    internal readonly double OwnLife, EnemyLife, LowLife, OwnBoard, EnemyBoard, BoardAttack, BoardHealth, BoardCost, MinionBase,
        Taunt, DivineShield, Frozen, Deathrattle, Hand, UnknownCard, Discount, Weapon, Aura, Location, Threat, Holds, Aggression;
    internal BrainScales(BrainOptions brain, string deckId = null)
    {
        Func<string, double> s = name => brain == null ? 1 : brain.Scale(name, deckId);
        OwnLife = s("ownLife"); EnemyLife = s("enemyLife"); LowLife = s("lowLife"); OwnBoard = s("ownBoard"); EnemyBoard = s("enemyBoard");
        BoardAttack = s("boardAttack"); BoardHealth = s("boardHealth"); BoardCost = s("boardCost"); MinionBase = s("minionBase");
        Taunt = s("taunt"); DivineShield = s("divineShield"); Frozen = s("frozen"); Deathrattle = s("deathrattle"); Hand = s("hand");
        UnknownCard = s("unknownCard"); Discount = s("discount"); Weapon = s("weapon"); Aura = s("aura"); Location = s("location");
        Threat = s("threat"); Holds = s("holds"); Aggression = s("aggression");
    }
}
