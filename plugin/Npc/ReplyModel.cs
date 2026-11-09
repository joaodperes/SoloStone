using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

public static class ReplyModel
{
    internal const int BeamWidth = 4;
    internal static int VerificationAllowance(int witnessLength) => 4 * witnessLength;
    internal static int WitnessLimit(int replyNodes, bool ownSecrets = false) =>
        Math.Min(Math.Max(1, replyNodes), TurnBeam.MaxLineActions) + (ownSecrets ? 2 : 1) * (TurnBeam.SettlementLimit + 1);
    // Search/settlement reservation, before the shared verification pool.
    internal static int SearchAllowance(int samples, int replyNodes, bool ownSecrets = false) =>
        samples * (replyNodes + BeamWidth * (TurnBeam.SettlementLimit + 1) + 3
            + (ownSecrets ? WitnessLimit(replyNodes, true) : 0)) + 3;
    // Honest worst-case job bound; ordinary samples need no verification.
    internal static int NodeAllowance(int samples, int replyNodes, bool ownSecrets = false) =>
        SearchAllowance(samples, replyNodes, ownSecrets) + samples * VerificationAllowance(WitnessLimit(replyNodes, ownSecrets));
    public static ReplyJob Job(FairBotState leaf, NpcProfile profile, int seed, DeckPlanEvaluator evaluator,
        IEnumerable<string> publiclyPlayed = null, int replyNodes = -1, int samples = -1) => new ReplyJob(leaf, profile, seed, evaluator, publiclyPlayed, replyNodes, samples);
    internal static double Blend(IEnumerable<KeyValuePair<double, double>> outcomes, double beta)
    {
        var sorted = outcomes.OrderBy(p => p.Key).ToArray(); double mass = sorted.Sum(p => p.Value);
        if (mass <= 0) throw new InvalidOperationException("Reply weights must be positive.");
        double mean = sorted.Sum(p => p.Key * p.Value) / mass, left = mass * .25, worst = 0;
        foreach (var p in sorted) { double take = Math.Min(left, p.Value); worst += take * p.Key; left -= take; if (left <= 0) break; }
        return (1 - beta) * mean + beta * worst / (mass * .25);
    }
}
public sealed class ReplyJob : INodeJob
{
    private FairBotState leaf;
    private FairFacts facts;
    private int settledChoices;
    private readonly NpcProfile profile;
    private readonly DeckPlanEvaluator evaluator;
    private readonly int seed, replyNodes, samples;
    private readonly double uncertainValue;
    private readonly string[] played;
    private FairBotState ended;
    private FairBotState sampleRoot, actualRoot, actualReply;
    private IReadOnlyList<BotAction> actualLine;
    private int actualIndex, actualSettlements;
    private bool actualEnded;
    private readonly List<BotAction> actualWitness = new List<BotAction>();
    private TurnBeam beam;
    private LethalJob verification;
    private double sampleValue;
    private int index;
    private readonly List<KeyValuePair<double, double>> outcomes = new List<KeyValuePair<double, double>>();
    private readonly List<KeyValuePair<double, double>> survivals = new List<KeyValuePair<double, double>>();
    private readonly List<KeyValuePair<double, double>> unknowns = new List<KeyValuePair<double, double>>();
    internal IReadOnlyList<KeyValuePair<double, double>> Outcomes => GoldenData.Copy(outcomes);
    internal IReadOnlyList<KeyValuePair<double, double>> Untested => GoldenData.Copy(unknowns);
    internal Action<FairBotState, IReadOnlyList<BotAction>, double> ObserveSample { get; set; }
    public bool IsDone { get; private set; }
    public int Nodes { get; private set; }
    public double Value { get; private set; }
    // Conservative lower bound: verified losses and untested holdouts both withhold survival credit.
    internal double? Survival { get; private set; }
    private void Finish()
    {
        if (outcomes.Sum(o => o.Value) > 0) {
            double beta = GoldenAblation.MeanReply ? 0 : profile.Personality.ReplyBlendBeta;
            Value = ReplyModel.Blend(outcomes, beta);
            if (survivals.Any(o => o.Key < 1 && o.Value > 0))
                Survival = ReplyModel.Blend(survivals, beta);
        } else if (Value <= -999000) Survival = 0;
        // Reply settlement has no verified root-to-end witness; sampled wins cannot certify own lethal.
        if (Value >= 999000) Value = uncertainValue;
        IsDone = true;
    }
    public IReadOnlyList<HandSample> Samples { get; private set; } = new HandSample[0];
    internal ReplyJob(FairBotState leaf, NpcProfile profile, int seed, DeckPlanEvaluator evaluator, IEnumerable<string> publiclyPlayed, int replyNodes, int samples = -1)
    {
        this.samples = samples < 0 ? profile.Budget.ReplyK : samples;
        this.leaf = leaf; facts = leaf.Facts; this.profile = profile; this.seed = seed; this.evaluator = evaluator;
        this.replyNodes = replyNodes < 0 ? profile.Budget.ReplyR : replyNodes;
        played = (publiclyPlayed ?? Enumerable.Empty<string>()).ToArray(); Value = leaf.Evaluate(evaluator);
        uncertainValue = Math.Max(-998000, Math.Min(998000, Value));
    }
    public void StepNode()
    {
        if (IsDone) return; Nodes++;
        if (ended == null) {
            if (Math.Abs(Value) >= 999000) { Finish(); return; }
            // A capped own line can stop inside a pending choice (Discover, Reborn picks): answer it with the
            // engine-ordered pick, one node each, as TurnBeam settles reply leaves; never END_TURN over it.
            if (leaf.HasPendingChoice) {
                if (settledChoices++ < TurnBeam.SettlementLimit && leaf.CanExpand) {
                    try { leaf = leaf.Apply(leaf.OrderedActions.First()); facts = leaf.Facts; Value = leaf.Evaluate(evaluator); }
                    catch (Exception error) when (FairHistory.IsPrivate(error)) { Finish(); }
                    catch (SimulationFailureException error) { leaf.ReportFault(error); Finish(); }
                } else Finish();
                return;
            }
            try { ended = leaf.ForReplyEndTurn(); }
            catch (Exception error) when (FairHistory.IsPrivate(error)) {
                Finish(); return;
            }
            Value = GoldenAblation.LegacyEval ? ended.Evaluate() : ended.Evaluate(evaluator);
            if (Math.Abs(Value) >= 999000 || ended.Facts.CurrentPlayerId == facts.Own.PlayerId) { Finish(); return; }
            return;
        }
        if (Samples.Count == 0) {
            var f = ended.Facts;
            var brain = profile.Budget.Brain;
            Samples = brain != null && brain.ReplyPrior == ReplyPrior.Inferred
                ? CardFrequencies.Sample(DeckInference.Table(ended.ReplyClass, played, brain), f.Opponent.Hand.Count(c => c.Unknown),
                    samples, f.Opponent.Mana, seed, played)
                : CardFrequencies.Sample(ended.ReplyClass, f.Opponent.Hand.Count(c => c.Unknown),
                    samples, f.Opponent.Mana, seed, played);
            return;
        }
        if (index >= Samples.Count) {
            Finish(); return;
        }
        try {
            if (actualLine != null) {
                if (!actualEnded && actualIndex < actualLine.Count && actualReply.CanExpand
                    && actualReply.Actions.Contains(actualLine[actualIndex])) {
                    var action = actualLine[actualIndex++];
                    actualReply = actualReply.Apply(action); actualWitness.Add(action);
                    actualEnded = action.Type == PlayerTaskType.END_TURN;
                    return;
                }
                // A different Secret can invalidate the policy tail. Settle the real prefix before scoring it.
                if (!actualReply.IsTerminal && actualReply.Facts.CurrentPlayerId == actualRoot.Facts.Own.PlayerId
                    && !actualEnded) {
                    if (actualReply.HasPendingChoice) {
                        if (actualSettlements++ >= TurnBeam.SettlementLimit || !actualReply.CanExpand) {
                            CompleteSample(uncertainValue, 0); return;
                        }
                        var choice = actualReply.OrderedActions.First();
                        actualReply = actualReply.Apply(choice); actualWitness.Add(choice);
                    } else {
                        var end = actualReply.ReplyEndTurnAction;
                        actualReply = actualReply.ForReplyEndTurn(); actualWitness.Add(end); actualEnded = true;
                    }
                    return;
                }
                double actualScore = GoldenAblation.LegacyEval ? -actualReply.Evaluate()
                    : evaluator.ReplyValue(actualReply.Facts, facts, actualReply.ForReplyHand(facts));
                var line = actualWitness.ToArray(); actualLine = null;
                sampleRoot = actualRoot;
                ScoreSample(actualScore, line, actualReply);
                return;
            }
            if (verification != null) {
                verification.StepNode();
                if (verification.IsDone) CompleteSample(sampleValue, verification.VerifiedReplyWinShare, verification.UntestedReplyShare);
                return;
            }
            if (beam == null) {
                var determinized = ended.Determinize(Samples[index].Cards, unchecked(seed + 7919 * (index + 1)));
                bool keepDeck = profile.Fixes.On("own-deck-value");
                actualRoot = ended.HasOwnSecrets ? determinized.ForReply(keepDeckOrder: keepDeck) : null;
                sampleRoot = determinized.ForReply(sampleSecrets: ended.HasOwnSecrets, keepDeckOrder: keepDeck);
                beam = GoldenAblation.LegacyEval ? new TurnBeam(sampleRoot, s => s.Evaluate(), replyNodes, ReplyModel.BeamWidth, true)
                    : new TurnBeam(sampleRoot, s => -evaluator.ReplyValue(s.Facts, facts, s.ForReplyHand(facts)), replyNodes, ReplyModel.BeamWidth, true,
                        prune: profile.Budget.Brain != null && profile.Budget.Brain.Prune);
                beam.SettleFallback = profile.Fixes.On("settle-fallback");
                return;
            }
            beam.StepNode();
            if (beam.IsDone) {
                var best = beam.Leaves.Where(n => !double.IsNegativeInfinity(n.Value))
                    .OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).FirstOrDefault();
                if (best == null && beam.Leaves.Count > 0) throw new InvalidOperationException("No settled reply leaf.");
                double score = best == null ? Value : -best.Value;
                if (actualRoot != null && best != null && best.Actions.Count > 0) {
                    // Policy searches a belief; only the real, owner-known Secret determines the scored outcome.
                    actualReply = actualRoot; actualLine = best.Actions; actualIndex = 0; actualSettlements = 0; actualEnded = false; actualWitness.Clear();
                } else ScoreSample(score, best?.Actions, best?.State);

            }
        }
        catch (Exception error) when (!(error is OutOfMemoryException)) {
            if (!FairHistory.IsPrivate(error)) leaf.FaultReporter?.Invoke("reply-sample-fault index=" + index + " error=" + error);
            if (verification != null) { CompleteSample(sampleValue, verification.VerifiedReplyWinShare, verification.UntestedReplyShare); return; }
            // A failing artificial sample contributes no mass; surviving weights normalize in Blend.
            index++; beam = null; verification = null; sampleRoot = null; actualRoot = null; actualReply = null; actualLine = null;
        }
    }
    private void ScoreSample(double score, IReadOnlyList<BotAction> line, FairBotState state)
    {
        ObserveSample?.Invoke(sampleRoot, line, score);
        // Terminal sentinels select the reply policy, never its continuous utility. Verified losses
        // contribute only to Survival; independent holdouts measure random danger without the optimized roll.
        sampleValue = Math.Abs(score) >= 999000
            ? state != null && !GoldenAblation.LegacyEval
                ? evaluator.ReplyValue(state.Facts, facts, state.ForReplyHand(facts), terminal: false)
                : uncertainValue
            : score;
        if (score <= -999000 && line != null && line.Count > 0) {
            // Reserved upstream, independently of how much of R the beam consumed. Each replay action is charged.
            verification = LethalJob.Verify(sampleRoot, line, ReplyModel.VerificationAllowance(line.Count), measureReplies: true);
        } else CompleteSample(sampleValue, 0, score <= -999000 ? 1 : 0);
    }
    private void CompleteSample(double score, double verifiedLoss, double untested = 0)
    {
        double weight = Samples[index].Weight;
        outcomes.Add(new KeyValuePair<double, double>(score, weight));
        // Unknown holdouts are not evidence of survival. Keep their mass separate and rank its lower bound.
        survivals.Add(new KeyValuePair<double, double>(Math.Max(0, 1 - verifiedLoss - untested), weight));
        unknowns.Add(new KeyValuePair<double, double>(untested, weight));
        index++; beam = null; verification = null; sampleRoot = null; actualRoot = null; actualReply = null; actualLine = null;
    }
}
