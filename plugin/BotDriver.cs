using EngineGame = SabberStoneCore.Model.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

public enum SearchStop { Terminal, Nodes, Clock, Depth, Exhausted, Forced }

public sealed class PilotDecision
{
    public BotAction Action;
    public double Value;
    public int Nodes, Depth;
    public SearchStop Stop;
    public IReadOnlyList<KeyValuePair<BotAction, double>> Candidates = new KeyValuePair<BotAction, double>[0];
    public static PilotDecision From(BotDecision decision) => new PilotDecision {
        Action = decision.Action, Value = decision.Value, Nodes = decision.Nodes, Depth = decision.Depth,
        Stop = decision.Stop, Candidates = decision.Candidates };
}

public sealed class DriverTrace
{
    public readonly string Kind, FallbackReason;
    public readonly PilotDecision PilotDecision;
    public DriverTrace(string kind, PilotDecision decision, string fallbackReason = null)
    { Kind = kind; PilotDecision = decision; FallbackReason = fallbackReason; }
    public static DriverTrace Forced(string kind, PlayerTask task) => new DriverTrace(kind,
        new PilotDecision { Action = BotAction.From(task), Stop = SearchStop.Forced });
}

public sealed class MulliganCard
{
    public readonly int EntityId, Cost;
    public readonly string CardId;
    public MulliganCard(int entityId, string cardId, int cost) { EntityId = entityId; CardId = cardId; Cost = cost; }
}

public sealed class MulliganView
{
    public readonly IReadOnlyList<MulliganCard> Offered;
    public readonly bool Coin;
    public readonly IReadOnlyList<string> OwnDeck;
    public readonly CardClass OpponentClass;
    public MulliganView(IEnumerable<MulliganCard> offered, bool coin, IEnumerable<string> ownDeck, CardClass opponentClass)
    { Offered = Array.AsReadOnly(offered.ToArray()); Coin = coin; OwnDeck = Array.AsReadOnly(ownDeck.ToArray()); OpponentClass = opponentClass; }
}

public abstract class MulliganPolicy
{
    public abstract IReadOnlyList<int> Keep(MulliganView view);
    public static readonly MulliganPolicy ReplaceAll = new Basic(false), KeepAll = new Basic(true);
    private sealed class Basic : MulliganPolicy
    {
        private readonly bool keep;
        internal Basic(bool keep) { this.keep = keep; }
        public override IReadOnlyList<int> Keep(MulliganView view) => keep
            ? view.Offered.Where(c => c.CardId != "GAME_005").Select(c => c.EntityId).ToArray() : new int[0];
    }
}

public sealed class RewindWeapon
{
    public readonly string CardId;
    public readonly int EntityId, Attack, Durability;
    internal RewindWeapon(SabberStoneCore.Model.Entities.Weapon weapon)
    { CardId = weapon.Card.Id; EntityId = weapon.Id; Attack = weapon.AttackDamage; Durability = weapon.Durability; }
}

public sealed class RewindView
{
    public readonly string SourceCardId;
    public readonly RewindWeapon OwnWeapon, OpponentWeapon;
    public readonly int KeepOptionId, RewindOptionId;
#if ENGINE_SOURCE
    internal RewindView(EngineGame game, SabberStoneCore.Model.Entities.Controller owner)
    {
        SourceCardId = game.Rewind.SourceCardId;
        OwnWeapon = owner.Hero.Weapon == null ? null : new RewindWeapon(owner.Hero.Weapon);
        var opponent = owner.PlayerId == game.Player1.PlayerId ? game.Player2 : game.Player1;
        OpponentWeapon = opponent.Hero.Weapon == null ? null : new RewindWeapon(opponent.Hero.Weapon);
        KeepOptionId = game.Rewind.KeepEntityId; RewindOptionId = game.Rewind.RewindEntityId;
    }
#endif
}

// Factories are host adapters; the search callback and Rewind policy receive value-only fair inputs.
public sealed class DriverOptions
{
    public Func<EngineGame, int, int, DeckStyle, FairBotState> FairViewFactory;
    public Func<RewindView, bool> Rewind;
}

public static class BotDriver
{
    internal static BotAction BoundedFallback(FairBotState state, IEnumerable<BotAction> legal, Func<bool> spend)
        => BoundedFallback(state, legal, spend, out bool _);
    internal static BotAction BoundedFallback(FairBotState state, IEnumerable<BotAction> legal, Func<bool> spend, out bool complete)
        => BoundedFallback(state, legal, spend, out complete, out _);
    // complete: every one-ply candidate was evaluated (false: the result may be the unevaluated END_TURN default).
    internal static BotAction BoundedFallback(FairBotState state, IEnumerable<BotAction> legal, Func<bool> spend, out bool complete,
        out IReadOnlyList<KeyValuePair<BotAction, double>> compared)
    {
        var job = new BoundedFallbackJob(state, legal);
        while (!job.IsDone && spend()) job.StepNode();
        complete = job.IsDone; compared = job.Compared;
        return job.Result;
    }
    // Only the live host checks the deadline; the shared driver never reads a clock.
    public static (PlayerTask Task, DriverTrace Trace) Timeout(PlayerTask endTurn)
    {
        if (endTurn.PlayerTaskType != PlayerTaskType.END_TURN) throw new ArgumentException("Timeout requires END_TURN.", nameof(endTurn));
        return (endTurn, DriverTrace.Forced("timeout", endTurn));
    }
    public static IReadOnlyList<int> Mulligan(MulliganView view, MulliganPolicy policy)
    {
        var keeps = policy.Keep(view).ToArray();
        var offered = new HashSet<int>(view.Offered.Where(c => c.CardId != "GAME_005").Select(c => c.EntityId));
        if (keeps.Distinct().Count() != keeps.Length || keeps.Any(id => !offered.Contains(id)))
            throw new InvalidOperationException("Mulligan policy returned an unoffered or duplicate card.");
        return Array.AsReadOnly(keeps);
    }

    // NativeMatch uses the same forced paths before starting its legacy turn/search clocks.
    internal static PlayerTask Forced(EngineGame game, List<PlayerTask> tasks, out DriverTrace trace, DriverOptions options = null)
    {
        trace = null;
#if ENGINE_SOURCE
        var keep = LocalBot.RewindKeep(tasks);
        if (keep != null)
        {
            var task = keep;
            if (options?.Rewind != null)
            {
                var view = new RewindView(game, keep.Controller);
                int selected = options.Rewind(view) ? view.KeepOptionId : view.RewindOptionId;
                task = tasks.OfType<ChooseTask>().Single(t => t.Choices.Count == 1 && t.Choices[0] == selected);
            }
            trace = DriverTrace.Forced("rewind", task); return task;
        }
#endif
        return null;
    }

    public static (PlayerTask Task, DriverTrace Trace) Decide(EngineGame game, int botId, DeckStyle style,
        Func<FairBotState, PilotDecision> search, ref int decisionIndex, DriverOptions options = null,
        List<PlayerTask> tasks = null, Func<PlayerTask, bool> nativeActionEnabled = null)
    {
        var bot = game.Player1.PlayerId == botId ? game.Player1 : game.Player2;
        if (bot.PlayerId != botId) throw new ArgumentOutOfRangeException(nameof(botId));
        bool headless = tasks == null;
        if (tasks == null)
        {
#if ENGINE_SOURCE
            // Options warms mutable engine caches; headless callers must not mutate the authoritative game.
            var copy = game.CloneExact();
            tasks = (copy.Player1.PlayerId == botId ? copy.Player1 : copy.Player2).Options();
#else
            tasks = bot.Options();
#endif
        }
        // Exact-clone options have the current decision token; bind only the chosen task to authority,
        // without warming authoritative option caches or returning a task owned by the disposable clone.
        PlayerTask ForGame(PlayerTask task)
        {
            if (!headless || task.Game == game) return task;
            task.Game = game;
            task.Controller = task.Controller.PlayerId == game.Player1.PlayerId ? game.Player1 : game.Player2;
            if (task.Source != null) task.Source = game.IdEntityDic[task.Source.Id];
#if ENGINE_SOURCE
            if (task.ActionTarget != null) task.ActionTarget = game.IdEntityDic[task.ActionTarget.Id];
#else
            if (task.Target != null) task.Target = (SabberStoneCore.Model.Entities.ICharacter)game.IdEntityDic[task.Target.Id];
#endif
            return task;
        }
        if (nativeActionEnabled != null) tasks = tasks.Where(nativeActionEnabled).ToList();
        FairBotState state = null;
        try
        {
            var forced = Forced(game, tasks, out var trace, options);
            if (forced != null) return (ForGame(forced), trace);
            int seed = unchecked(997 * game.Turn + ++decisionIndex * 31);
            state = options?.FairViewFactory == null ? FairBotState.Create(game, botId, seed, style)
                : options.FairViewFactory(game, botId, seed, style);
            var decision = search(state);
            var task = decision.Action.Resolve(tasks);
            return (ForGame(task), new DriverTrace("search", decision));
        }
        catch (Exception error)
        {
            var original = error is SimulationFailureException wrapped ? wrapped.InnerException : error;
            string reason = original.GetType().Name + ":" + original.Message;
            PlayerTask task = null; int fallbackAttempts = 0;
            IReadOnlyList<KeyValuePair<BotAction, double>> compared = new KeyValuePair<BotAction, double>[0];
            if (state != null) {
                try { task = BoundedFallback(state, tasks.Select(BotAction.From), () => { if (fallbackAttempts >= 16) return false; fallbackAttempts++; return true; }, out bool _, out compared).Resolve(tasks); }
                catch (Exception fallbackError) when (!(fallbackError is OutOfMemoryException)) { state.FaultReporter?.Invoke("fallback-evaluation-fault=" + fallbackError.GetType().Name); }
            }
#if ENGINE_SOURCE
            // First-valid descriptor selection is an emergency policy, never a search restriction.
            if (task == null) {
                var answer = game.GetSelectorAnswer();
                if (answer != null) task = ChooseTask.Pick(game.ControllerById(answer.ControllerId), answer.SelectedOptionIds[0]);
            }
#endif
            task = task ?? tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.END_TURN) ?? LocalBot.Choose(tasks);
            return (ForGame(task), new DriverTrace("fallback", new PilotDecision { Action = BotAction.From(task), Stop = SearchStop.Forced, Nodes = fallbackAttempts, Candidates = compared }, reason));
        }
    }
}

// Same bounded one-ply policy as emergency fallback, prepared before expensive lethal search.
internal sealed class BoundedFallbackJob : INodeJob
{
    private readonly FairBotState state;
    private readonly BotAction[] actions;
    private int index;
    private double value;
    private readonly List<KeyValuePair<BotAction, double>> compared = new List<KeyValuePair<BotAction, double>>();
    // What this policy scored so far, for the decision log.
    internal IReadOnlyList<KeyValuePair<BotAction, double>> Compared => compared;
    internal BotAction Result { get; private set; }
    internal int Nodes => index;
    public bool IsDone => index == actions.Length;
    private readonly bool zeroDoomed;
    internal BoundedFallbackJob(FairBotState state, IEnumerable<BotAction> legal, bool zeroDoomed = false)
    {
        this.state = state; this.zeroDoomed = zeroDoomed;
        var allowed = new HashSet<BotAction>(legal);
        var ordered = state.OrderedActions.Where(allowed.Contains).GroupBy(a => a.WithoutPosition).Select(g => g.First()).ToArray();
        Result = ordered.FirstOrDefault(a => a.Type == PlayerTaskType.END_TURN);
        value = Result == null ? double.NegativeInfinity : state.FallbackValue(zeroDoomed);
        if (Result != null) compared.Add(new KeyValuePair<BotAction, double>(Result, value));
        if (Result == null) Result = ordered.First();
        actions = ordered.Where(a => a.Type != PlayerTaskType.END_TURN).Take(16).ToArray();
    }
    public void StepNode()
    {
        if (IsDone) return;
        var action = actions[index++];
        try
        {
            double score = state.Apply(action).FallbackValue(zeroDoomed);
            compared.Add(new KeyValuePair<BotAction, double>(action, score));
            if (score > value) { Result = action; value = score; }
        }
        catch (Exception error) when (FairHistory.IsPrivate(error)) { }
        catch (SimulationFailureException error) { state.ReportFault(error); }
    }
}
