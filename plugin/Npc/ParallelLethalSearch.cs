using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Two isolated first-action partitions. Fixed caps and ordered reduction make scheduling order irrelevant.
// shortcut: caps are not donated between partitions; redistribute only with a deterministic shared scheduler.
internal sealed class ParallelLethalSearch
{
    private readonly LethalJob[] workers = new LethalJob[2];
    private readonly List<string>[] faults = { new List<string>(), new List<string>() };
    private readonly Action<string> report;
    private readonly Action charge;
    private readonly Action<int> beforeWorker;
    private Task[] running;
    private int cancelled, nodes, charged;
    private long deadline = long.MaxValue;
    internal void LimitMilliseconds(double milliseconds)
    {
        long limit = Stopwatch.GetTimestamp() + (long)(Math.Max(0, milliseconds) * Stopwatch.Frequency / 1000);
        long previous;
        do { previous = Interlocked.Read(ref deadline); if (limit >= previous) return; }
        while (Interlocked.CompareExchange(ref deadline, limit, previous) != previous);
    }
    internal int Nodes => Volatile.Read(ref nodes);
    internal int Deepest => workers.Max(worker => worker.Deepest);
    internal bool IsDone { get; private set; }
    internal LethalResult Result { get; private set; }

    internal ParallelLethalSearch(FairBotState state, int cap, Action charge = null, Action<int> beforeWorker = null)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (cap < 0) throw new ArgumentOutOfRangeException(nameof(cap));
        this.charge = charge; this.beforeWorker = beforeWorker; report = state.FaultReporter;
        var actions = LethalJob.OrderedActions(state);
        for (int i = 0; i < workers.Length; i++)
        {
            int partition = i;
            var copy = state.ForkForSearch(line => faults[partition].Add(line));
            workers[i] = new LethalJob(copy, cap / 2 + (i < cap % 2 ? 1 : 0),
                () => Interlocked.Increment(ref nodes), actions.Where((a, index) => index % 2 == partition).ToArray());
        }
        Result = new LethalResult(LethalOutcome.Incomplete, null, 0);
        IsDone = cap == 0;
    }

    // Pump only on the owner thread. Worker callbacks never touch pilot budgets, logs, recorder or Unity.
    internal void Poll()
    {
        if (IsDone) return;
        if (running == null)
        {
            running = workers.Select((worker, index) => Task.Run(() => {
                try
                {
                    beforeWorker?.Invoke(index);
                    while (!worker.IsDone && Volatile.Read(ref cancelled) == 0 && Stopwatch.GetTimestamp() < Interlocked.Read(ref deadline)) worker.StepNode();
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    faults[index].Add("lethal-worker-fault=" + error.GetType().Name);
                    Interlocked.Exchange(ref cancelled, 1);
                }
            })).ToArray();
            return;
        }
        // Partition 1 may finish first, but cannot overtake an earlier first-action partition.
        // Once partition 0 finds an acceptable sampled line, remaining work is cancelled at its next node.
        if (running[0].IsCompleted && workers[0].Result.Outcome == LethalOutcome.Found)
        {
            Interlocked.Exchange(ref cancelled, 1);
            Task.WaitAll(running); Collect();
            Finish(LethalOutcome.Found, workers[0].Result.Line);
        }
        else if (running.All(task => task.IsCompleted))
        {
            Task.WaitAll(running); Collect();
            var found = workers.FirstOrDefault(worker => worker.Result.Outcome == LethalOutcome.Found);
            Finish(found != null ? LethalOutcome.Found : workers.All(worker => worker.Result.Outcome == LethalOutcome.None)
                ? LethalOutcome.None : LethalOutcome.Incomplete, found?.Result.Line);
        }
    }

    internal void Stop()
    {
        if (IsDone) return;
        Interlocked.Exchange(ref cancelled, 1);
        // An engine expansion is atomic, as on the serial path; join before reporting its actual cost.
        if (running != null) Task.WaitAll(running);
        Collect();
        var found = workers.FirstOrDefault(worker => worker.Result.Outcome == LethalOutcome.Found);
        Finish(found == null ? LethalOutcome.Incomplete : LethalOutcome.Found, found?.Result.Line);
    }

    private void Collect()
    {
        while (charged < Nodes) { charged++; charge?.Invoke(); }
        for (int i = 0; i < faults.Length; i++)
        {
            foreach (var line in faults[i]) report?.Invoke(line);
            faults[i].Clear();
        }
        Result = new LethalResult(LethalOutcome.Incomplete, null, Nodes);
    }
    private void Finish(LethalOutcome outcome, IReadOnlyList<BotAction> line)
    { Result = new LethalResult(outcome, line, Nodes); IsDone = true; }
}
