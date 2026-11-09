using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

public sealed class BotBudget
{
    public readonly int Depth, Branch, Nodes, Milliseconds;
    public BotBudget(int depth, int branch, int nodes, int milliseconds)
    { Depth = depth; Branch = branch; Nodes = nodes; Milliseconds = milliseconds; }
    public static BotBudget ForStars(int stars)
    {
        int tier = Math.Min(5, Math.Max(0, stars) / 30);
        return new BotBudget(tier + 2, 6 + tier * 2, new[] {24,48,96,160,256,384}[tier], 8 + tier * 6);
    }
    // Same depth/branch/node limits with no wall-clock cutoff, so the search result does not depend on CPU load.
    public BotBudget WithoutClock() => new BotBudget(Depth, Branch, Nodes, int.MaxValue);
}
public sealed class BotDecision
{
    public BotAction Action;
    public double Value;
    public int Nodes, Depth;
    public long Milliseconds;
    public SearchStop Stop;
    public IReadOnlyList<KeyValuePair<BotAction, double>> Candidates = new KeyValuePair<BotAction, double>[0];
}
public static class BotSearch
{
    private sealed class Node
    {
        public FairBotState State;
        public BotAction Root;
        public double Value;
    }
    private static IEnumerable<BotAction> Branches(FairBotState state, int width)
    {
        var ordered = state.OrderedActions;
#if ENGINE_SOURCE
        if (state.HasLocationHand)
        {
            // At most eight insertion slots: retain locations inside the existing branch/node budget.
            var placements = ordered.GroupBy(a => state.IsLocation(a.Source) ? a.ToString() : a.WithoutPosition)
                .Select(g => g.First()).Take(width).ToList();
            return placements.Concat(ordered.Where(a => !placements.Contains(a))).Take(width);
        }
#endif
        var distinct = ordered.GroupBy(a => a.WithoutPosition).Select(g => g.First()).Take(width).ToList();
        return distinct.Concat(ordered.Where(a => !distinct.Contains(a))).Take(width);
    }
    public static BotDecision Choose(FairBotState state, BotBudget budget)
    {
        if (state.RewindKeep != null)
        {
            double value = state.Evaluate();
            return new BotDecision { Action = state.RewindKeep, Value = value, Stop = SearchStop.Forced,
                Candidates = Array.AsReadOnly(new[] { new KeyValuePair<BotAction, double>(state.RewindKeep, value) }) };
        }
        var clock = Stopwatch.StartNew();
        var legal = state.OrderedActions;
        if (legal.Length == 0) throw new InvalidOperationException("No fair legal action.");
        var fallback = legal[0];
        var result = new BotDecision { Action = fallback, Value = double.NegativeInfinity, Stop = SearchStop.Depth };
        var candidates = new Dictionary<BotAction, double>();
        foreach (var action in legal)
            if (!candidates.ContainsKey(action)) candidates.Add(action, double.NegativeInfinity);
        var frontier = new List<Node> { new Node { State = state } };
        for (int depth = 1; depth <= budget.Depth && result.Nodes < budget.Nodes; depth++)
        {
            var next = new List<Node>();
            foreach (var parent in frontier)
            {
                if (!parent.State.CanExpand) continue;
                foreach (var action in Branches(parent.State, budget.Branch))
                {
                    if (result.Nodes >= budget.Nodes) { result.Stop = SearchStop.Nodes; goto Done; }
                    if (clock.ElapsedMilliseconds >= budget.Milliseconds) { result.Stop = SearchStop.Clock; goto Done; }
                    result.Nodes++;
                    FairBotState child;
                    try { child = parent.State.Apply(action); }
                    catch (Exception error) when (FairHistory.IsPrivate(error))
                    {
                        // Hidden history cannot be scored; discard this branch's simulation clone.
                        continue;
                    }
                    result.Depth = depth;
                    double value = child.Evaluate() - depth * .01;
                    var root = parent.Root ?? action;
                    if (value > candidates[root]) candidates[root] = value;
                    if (value > result.Value) { result.Action = root; result.Value = value; }
                    if (value >= 999000) { result.Stop = SearchStop.Terminal; goto Done; }
                    next.Add(new Node { State = child, Root = root, Value = value });
                }
            }
            frontier = next.Where(n => n.State.CanExpand).OrderByDescending(n => n.Value).Take(budget.Branch).ToList();
            if (frontier.Count == 0) { result.Stop = SearchStop.Exhausted; break; }
        }
        if (result.Stop == SearchStop.Depth && result.Nodes >= budget.Nodes) result.Stop = SearchStop.Nodes;
        Done:
        if (legal.Length == 1 && result.Stop != SearchStop.Terminal && result.Stop != SearchStop.Clock && result.Stop != SearchStop.Nodes)
            result.Stop = SearchStop.Forced;
        result.Candidates = Array.AsReadOnly(candidates.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal).ToArray());
        result.Milliseconds = clock.ElapsedMilliseconds;
        return result;
    }
}
