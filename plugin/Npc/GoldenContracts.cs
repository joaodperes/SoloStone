using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;

// Frozen W1b boundary. Immutable record-shaped classes keep the plugin on C# 7.3/net48.
public sealed class NpcProfile
{
    public string Id { get; }
    public int Version { get; }
    public string DisplayName { get; }
    public string DeckId { get; }
    public string HeroSkin { get; }
    public string HeroSkinPower { get; }
    public int CardBack { get; }
    public Personality Personality { get; }
    public Budget Budget { get; }
    public string PlanId { get; }
    public AiFixes Fixes { get; }
    public NpcProfile(string id, int version, string displayName, string deckId, string heroSkin,
        int cardBack, Personality personality, Budget budget, string planId, string heroSkinPower = null, AiFixes fixes = null)
    { Id = id; Version = version; DisplayName = displayName; DeckId = deckId; HeroSkin = heroSkin;
      CardBack = cardBack; Personality = personality; Budget = budget; PlanId = planId; HeroSkinPower = heroSkinPower; Fixes = fixes ?? new AiFixes(); }
    public NpcProfile WithFixes(AiFixes fixes) => new NpcProfile(Id, Version, DisplayName, DeckId, HeroSkin,
        CardBack, Personality, Budget, PlanId, HeroSkinPower, fixes);
    public NpcProfile WithBudget(Budget budget) => new NpcProfile(Id, Version, DisplayName, DeckId, HeroSkin,
        CardBack, Personality, budget, PlanId, HeroSkinPower, Fixes);
}

public sealed class Personality
{
    // Seconds; pacing is separate from node budgets and capped at four seconds per turn by the runner.
    public double PreTurnMinSeconds { get; }
    public double PreTurnMaxSeconds { get; }
    public double ActionMinSeconds { get; }
    public double ActionMaxSeconds { get; }
    public EmoteProfile Emotes { get; }
    public double ReplyBlendBeta { get; }
    public double ConcessionWillingness { get; }
    public int ConcedeFromTurn { get; }
    public Personality(double preTurnMinSeconds, double preTurnMaxSeconds, double actionMinSeconds,
        double actionMaxSeconds, EmoteProfile emotes, double replyBlendBeta,
        double concessionWillingness, int concedeFromTurn)
    { PreTurnMinSeconds = preTurnMinSeconds; PreTurnMaxSeconds = preTurnMaxSeconds;
      ActionMinSeconds = actionMinSeconds; ActionMaxSeconds = actionMaxSeconds; Emotes = emotes;
      ReplyBlendBeta = replyBlendBeta; ConcessionWillingness = concessionWillingness; ConcedeFromTurn = concedeFromTurn; }
}

public enum NpcReaction { MatchStart, ReplyToHumanEmote, OwnBigTurn, HumanBigTurn, BotLethalFound, BotDoomed, Concession, HumanMisplay }

public sealed class EmoteRule
{
    public NpcReaction Trigger { get; }
    public string Emote { get; }
    public double Chance { get; }
    public int MaxUses { get; }
    public string HumanEmote { get; }
    public bool Mock { get; }
    public EmoteRule(NpcReaction trigger, string emote, double chance, int maxUses = 1, string humanEmote = null, bool mock = false)
    { Trigger = trigger; Emote = emote; Chance = chance; MaxUses = maxUses; HumanEmote = humanEmote; Mock = mock; }
}

public sealed class EmoteProfile
{
    public int Budget { get; }
    public double MinGapMilliseconds { get; }
    public int SwingThreshold { get; }
    public IReadOnlyList<EmoteRule> Rules { get; }
    // Rules for the same trigger are ordered alternatives; each chance applies if earlier alternatives declined.
    public EmoteProfile(int budget, double gapSeconds, params EmoteRule[] rules)
    { Budget = budget; MinGapMilliseconds = gapSeconds * 1000; SwingThreshold = 8; Rules = GoldenData.Copy(rules); }
}

// Lethal has a separate per-decision cap and a shared per-turn allocation.
public interface INpcLethalSearch
{
    INodeJob LethalSearch { get; }
}

public sealed class Budget
{
    public int TurnNodes { get; }
    public int PlanNodes { get; }
    public int LethalNodes { get; }
    public int TurnLethalNodes => 3 * LethalNodes;
    public int ReplyK { get; }
    public int ReplyL { get; }
    public int ReplyR { get; }
    // Own-turn beam width of TurnPlanner (frontier lines kept per depth).
    public int OwnBeamWidth { get; }
    // Elite+ (2026-10-25 research, docs/roadmap/2026-10-25-npc-elite-research.md). Defaults reproduce every older budget.
    // Dedupe: the own beam keeps one line per reached public state (transpositions of commuting actions waste width).
    public bool Dedupe { get; }
    // Live wall-clock search budget per NPC turn (NpcTurnRunner); 0 = the 5 s default. Node budgets stay the harness truth.
    public double TurnSearchSeconds { get; }
    // Live guard: no new search after this much wall time since the turn started (0 = none), so actions finish before the rope.
    public double TurnWallSeconds { get; }
    // AI-leap research (BrainOptions): how the planner values positions and samples replies. Null = shipping golden brain.
    public BrainOptions Brain { get; }
    public Budget(int turnNodes, int planNodes, int lethalNodes, int replyK, int replyL, int replyR, int ownBeamWidth = 6,
        bool dedupe = false, double turnSearchSeconds = 0, double turnWallSeconds = 0, BrainOptions brain = null)
    { TurnNodes = turnNodes; PlanNodes = planNodes; LethalNodes = lethalNodes;
      ReplyK = replyK; ReplyL = replyL; ReplyR = replyR; OwnBeamWidth = Math.Max(1, ownBeamWidth);
      Dedupe = dedupe; TurnSearchSeconds = Math.Max(0, turnSearchSeconds); TurnWallSeconds = Math.Max(0, turnWallSeconds); Brain = brain; }
    public Budget WithBrain(BrainOptions brain) => new Budget(TurnNodes, PlanNodes, LethalNodes, ReplyK, ReplyL, ReplyR, OwnBeamWidth,
        Dedupe, TurnSearchSeconds, TurnWallSeconds, brain);
}

[Flags] public enum Matchup { Aggro = 1, Midrange = 2, Control = 4, Any = 7 }
public enum MulliganCondition { Always, KeptDragon, EarlyCurve, EarlyCurveAndDragon, HasOneDrop, HasAura, CoinAndCheapMinions }
[Flags] public enum CardRole
{ Development = 1, Dragon = 2, Generator = 4, Removal = 8, Fire = 16, DamageEnabler = 32,
  Rush = 64, Payoff = 128, Refill = 256, Aura = 512, Shield = 1024, Herald = 2048, Finisher = 4096, Quest = 8192 }
// AoeWideBoard (NPC World WP1, appended): an AoE is worth holding until the enemy has >= 2 minions or >= 6 board attack.
public enum HoldCondition { DragonActivator, DamagedTargetWithExcess, ThreatOrLethal, AuraForTimeways, RefillAfterClear, FifthMinionBattlecry, AoeWideBoard }
public enum PlanHintKind
{ WarptoothDamage, FireBeforeVolcano, EarlyRushTrades, KindredDragon, AuraTiming, BoardSpace,
  ShatterHalves, ProtectDivineShields, FifthMinionBattlecry, MaievDormancy, DarkGift,
  TinyPalAmmo, DeathwingChoices, HeraldProgress, ElementalSequence }
public enum DiscoverPool { ClassDragons, WarriorMinions, ClassPirates, OwnDeckMinions, DarkGifts, TinyPalAmmo, DeathwingCataclysms, AnyWeapon }

public sealed class MatchupClasses
{
    public Matchup Matchup { get; }
    public IReadOnlyList<CardClass> Classes { get; }
    public MatchupClasses(Matchup matchup, params CardClass[] classes)
    { Matchup = matchup; Classes = GoldenData.Copy(classes); }
}

public sealed class MulliganRule
{
    public string CardId { get; }
    public Matchup Matchups { get; }
    public MulliganCondition Condition { get; }
    public int MaxCopies { get; }
    public int MinimumEarlyPlays { get; }
    public MulliganRule(string cardId, Matchup matchups = Matchup.Any,
        MulliganCondition condition = MulliganCondition.Always, int maxCopies = 1, int minimumEarlyPlays = 0)
    { CardId = cardId; Matchups = matchups; Condition = condition; MaxCopies = maxCopies; MinimumEarlyPlays = minimumEarlyPlays; }
}

public sealed class CardRoleEntry
{
    public string CardId { get; }
    public CardRole Roles { get; }
    public CardRoleEntry(string cardId, CardRole roles) { CardId = cardId; Roles = roles; }
}

public sealed class HoldRule
{
    public HoldCondition Condition { get; }
    public IReadOnlyList<string> CardIds { get; }
    public IReadOnlyList<string> EnabledBy { get; }
    public double Bonus { get; }
    public HoldRule(HoldCondition condition, double bonus, string[] cardIds, params string[] enabledBy)
    { Condition = condition; Bonus = bonus; CardIds = GoldenData.Copy(cardIds); EnabledBy = GoldenData.Copy(enabledBy); }
}

public sealed class RemovalThreshold
{
    public string CardId { get; }
    public int MinimumTargetAttack { get; }
    public int MinimumTargetHealth { get; }
    public int MinimumExcess { get; }
    public bool LethalOverrides { get; }
    public RemovalThreshold(string cardId, int minimumTargetAttack, int minimumTargetHealth, int minimumExcess = 0, bool lethalOverrides = true)
    { CardId = cardId; MinimumTargetAttack = minimumTargetAttack; MinimumTargetHealth = minimumTargetHealth;
      MinimumExcess = minimumExcess; LethalOverrides = lethalOverrides; }
}

public sealed class AggressionPoint
{
    // Last matching point wins; life is current public Health + Armor. Terminal results override every weight.
    public int FromTurn { get; }
    public int OwnLifeAtMost { get; }
    public int OpponentLifeAtMost { get; }
    public double FaceWeight { get; }
    public AggressionPoint(int fromTurn, int ownLifeAtMost, int opponentLifeAtMost, double faceWeight)
    { FromTurn = fromTurn; OwnLifeAtMost = ownLifeAtMost; OpponentLifeAtMost = opponentLifeAtMost; FaceWeight = faceWeight; }
}

public sealed class PlanHint
{
    public PlanHintKind Kind { get; }
    public IReadOnlyList<string> CardIds { get; }
    public int Threshold { get; }
    public double Bonus { get; }
    public string Guidance { get; }
    public PlanHint(PlanHintKind kind, int threshold, double bonus, string guidance, params string[] cardIds)
    { Kind = kind; Threshold = threshold; Bonus = bonus; Guidance = guidance; CardIds = GoldenData.Copy(cardIds); }
}

public sealed class DiscoverPreference
{
    public string SourceCardId { get; }
    public DiscoverPool Pool { get; }
    public IReadOnlyList<string> PreferredCardIds { get; }
    public string Guidance { get; }
    public DiscoverPreference(string sourceCardId, DiscoverPool pool, string guidance, params string[] preferredCardIds)
    { SourceCardId = sourceCardId; Pool = pool; Guidance = guidance; PreferredCardIds = GoldenData.Copy(preferredCardIds); }
}

public sealed class WeaponPreference
{
    public string CardId { get; }
    public double Value { get; }
    public WeaponPreference(string cardId, double value) { CardId = cardId; Value = value; }
}

public sealed class RewindPreference
{
    public string SourceCardId { get; }
    public double RewindBottomFraction { get; }
    public double OpponentAdvantageThreshold { get; }
    public IReadOnlyList<WeaponPreference> Weapons { get; }
    public RewindPreference(string sourceCardId, double rewindBottomFraction, double opponentAdvantageThreshold, params WeaponPreference[] weapons)
    { SourceCardId = sourceCardId; RewindBottomFraction = rewindBottomFraction;
      OpponentAdvantageThreshold = opponentAdvantageThreshold; Weapons = GoldenData.Copy(weapons); }
}

public sealed class EvaluatorWeights
{
    public double OwnLife { get; }
    public double EnemyLife { get; }
    public double BoardAttack { get; }
    public double BoardHealth { get; }
    public double Hand { get; }
    public double DivineShield { get; }
    public double HeldDragon { get; }
    public double WarptoothSetup { get; }
    public double ActiveAura { get; }
    public double BoardSpace { get; }
    public double BattlecrySetup { get; }
    public double Herald { get; }
    public EvaluatorWeights(double ownLife, double enemyLife, double boardAttack, double boardHealth, double hand,
        double divineShield, double heldDragon, double warptoothSetup, double activeAura, double boardSpace,
        double battlecrySetup, double herald)
    { OwnLife = ownLife; EnemyLife = enemyLife; BoardAttack = boardAttack; BoardHealth = boardHealth; Hand = hand;
      DivineShield = divineShield; HeldDragon = heldDragon; WarptoothSetup = warptoothSetup;
      ActiveAura = activeAura; BoardSpace = boardSpace; BattlecrySetup = battlecrySetup; Herald = herald; }
}

public sealed class DeckPlan
{
    public string Id { get; }
    public string DeckId { get; }
    public IReadOnlyList<MatchupClasses> MatchupClasses { get; }
    // Unlisted cards are replaced. KeptDragon means another Dragon retained by a satisfied keep rule, never an expensive discard.
    public IReadOnlyList<MulliganRule> MulliganRules { get; }
    public IReadOnlyList<CardRoleEntry> CardRoles { get; }
    public IReadOnlyList<HoldRule> Holds { get; }
    public IReadOnlyList<RemovalThreshold> RemovalThresholds { get; }
    public IReadOnlyList<AggressionPoint> AggressionCurve { get; }
    public IReadOnlyList<PlanHint> Hints { get; }
    public IReadOnlyList<DiscoverPreference> DiscoverPreferences { get; }
    public IReadOnlyList<RewindPreference> RewindPreferences { get; }
    public EvaluatorWeights Weights { get; }
    public DeckPlan(string id, string deckId, IEnumerable<MatchupClasses> matchupClasses,
        IEnumerable<MulliganRule> mulliganRules, IEnumerable<CardRoleEntry> cardRoles, IEnumerable<HoldRule> holds,
        IEnumerable<RemovalThreshold> removalThresholds, IEnumerable<AggressionPoint> aggressionCurve,
        IEnumerable<PlanHint> hints, IEnumerable<DiscoverPreference> discoverPreferences,
        IEnumerable<RewindPreference> rewindPreferences, EvaluatorWeights weights)
    { Id = id; DeckId = deckId; MatchupClasses = GoldenData.Copy(matchupClasses); MulliganRules = GoldenData.Copy(mulliganRules);
      CardRoles = GoldenData.Copy(cardRoles); Holds = GoldenData.Copy(holds); RemovalThresholds = GoldenData.Copy(removalThresholds);
      AggressionCurve = GoldenData.Copy(aggressionCurve); Hints = GoldenData.Copy(hints);
      DiscoverPreferences = GoldenData.Copy(discoverPreferences); RewindPreferences = GoldenData.Copy(rewindPreferences); Weights = weights; }
}

public sealed class GoldenContext
{
    public int Turn { get; }
    public int DecisionIndex { get; }
    public int ArtificialSeed { get; }
    public NpcProfile NpcProfile { get; }
    public GoldenPublicInputs PublicInputs { get; }
    public GoldenContext(int turn, int decisionIndex, int artificialSeed, NpcProfile npcProfile)
        : this(turn, decisionIndex, artificialSeed, npcProfile, null) { }
    public GoldenContext(int turn, int decisionIndex, int artificialSeed, NpcProfile npcProfile, GoldenPublicInputs publicInputs)
    { Turn = turn; DecisionIndex = decisionIndex; ArtificialSeed = artificialSeed; NpcProfile = npcProfile; PublicInputs = publicInputs; }
}

// Supplemental values absent from FairFacts; hosts may supply them without exposing an engine object.
public sealed class GoldenPublicInputs
{
    public IReadOnlyList<string> OpponentPlayed { get; }
    public string DiscoverSource { get; }
    public IReadOnlyList<KeyValuePair<int, string>> Offered { get; }
    public GoldenPublicInputs(IEnumerable<string> opponentPlayed = null, string discoverSource = null,
        IEnumerable<KeyValuePair<int, string>> offered = null)
    { OpponentPlayed = GoldenData.Copy(opponentPlayed); DiscoverSource = discoverSource; Offered = GoldenData.Copy(offered); }
}

// Keep the frozen W1b interface; GoldenPilot provides real BotDriver adapters alongside it.
// Adapters implement these value-only boundaries; no duplicate harness types or engine references enter mulligans.
public sealed class GoldenMulliganCard
{
    public int EntityId { get; }
    public string CardId { get; }
    public int Cost { get; }
    public GoldenMulliganCard(int entityId, string cardId, int cost) { EntityId = entityId; CardId = cardId; Cost = cost; }
}
public interface IGoldenMulliganView
{
    IReadOnlyList<GoldenMulliganCard> Offered { get; }
    bool HasCoin { get; }
    IReadOnlyList<string> OwnDeckIds { get; }
    CardClass OpponentClass { get; }
}
public interface IGoldenPilot
{
    GoldenDecision Decide(FairBotState view, GoldenContext ctx);
    IReadOnlyList<int> Mulligan(IGoldenMulliganView view, GoldenContext ctx);
}
public sealed class DecisionSearchInfo
{
    public int PlanNodeCap, ValidationNodeCap, ValidationNodes, UnscheduledNodes;
    public double SearchBudgetMilliseconds;
    public bool Interrupted, Reused, ValidationCompared, ValidationDeadline, ValidationFallback;
    public string Mistake = "none";
    // Live runner only (null/0 on the node-budget harness path, so harness records keep their shape).
    // FastFinish: attack|mana|none when the search-free completion chose this decision (FastFinish.cs).
    public string FastFinish;
    // Plan reuse (GoldenPilot.Reuse): why this decision searched instead of continuing its cached line, null when the
    // pilot never consulted a cache (fallbacks, forced). "reused" on a hit.
    public string Replan;
    // Why a decision that the search did not pick was taken: policy:stop:cause. FallbackReason stays for the runner's
    // control flow (a non-null reason re-enters the fallback); this one is only logged.
    public string FallbackWhy;
}

public sealed class GoldenDecision
{
    public DecisionSearchInfo SearchInfo { get; internal set; } = new DecisionSearchInfo();
    public BotAction Action { get; }
    public double Value { get; }
    public int Nodes { get; }
    public int Depth { get; }
    // W1a adapters preserve SearchStop names: Terminal, Nodes, Clock, Depth, Exhausted, Forced.
    public string Stop { get; }
    // DriverTrace.Kind: timeout, rewind, selector, search, fallback.
    public string DriverKind { get; }
    public string FallbackReason { get; }
    public IReadOnlyList<KeyValuePair<BotAction, double>> Candidates { get; }
    public PilotDecision ToPilotDecision() => new PilotDecision {
        Action = Action, Value = Value, Nodes = Nodes, Depth = Depth,
        Stop = (SearchStop)Enum.Parse(typeof(SearchStop), Stop), Candidates = Candidates };
    public GoldenDecision(BotAction action, double value, int nodes, int depth, string stop,
        string fallbackReason, IEnumerable<KeyValuePair<BotAction, double>> candidates, string driverKind = "search")
    { Action = action; Value = value; Nodes = nodes; Depth = depth; Stop = stop;
      DriverKind = driverKind; FallbackReason = fallbackReason; Candidates = GoldenData.Copy(candidates); }
}

public interface IMatchRecorder
{
    // False means callers must skip fingerprint/hash work entirely.
    bool Enabled { get; }
    void OnMulligan(int playerId, IReadOnlyList<int> keptEntityIds);
    // Nodes is total scheduler work (lethal + planner + replies), not just trace.Nodes.
    void OnDecision(GoldenContext context, GoldenDecision trace, string lethalResult, bool planReused,
        int nodes, double thinkMilliseconds, double maxSliceMilliseconds);
    void OnHumanAction(int turn, string botAction, int optionsIndex, IReadOnlyList<int> choices, bool conceded);
    void OnEmote(int playerId, string emote, int publicEventIndex);
    void OnConcession(int playerId, int turn);
    void OnResult(string result, int turns);
    void OnPostAction(long sequence, string replayFingerprintSha256);
}
public sealed class NullMatchRecorder : IMatchRecorder
{
    public static readonly NullMatchRecorder Instance = new NullMatchRecorder();
    private NullMatchRecorder() { }
    public bool Enabled => false;
    public void OnMulligan(int playerId, IReadOnlyList<int> keptEntityIds) { }
    public void OnDecision(GoldenContext context, GoldenDecision trace, string lethalResult, bool planReused,
        int nodes, double thinkMilliseconds, double maxSliceMilliseconds) { }
    public void OnHumanAction(int turn, string botAction, int optionsIndex, IReadOnlyList<int> choices, bool conceded) { }
    public void OnEmote(int playerId, string emote, int publicEventIndex) { }
    public void OnConcession(int playerId, int turn) { }
    public void OnResult(string result, int turns) { }
    public void OnPostAction(long sequence, string replayFingerprintSha256) { }
}

internal static class GoldenData
{
    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) =>
        Array.AsReadOnly((values ?? Enumerable.Empty<T>()).ToArray());
}
