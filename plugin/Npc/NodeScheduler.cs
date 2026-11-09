using System;
using System.Collections.Generic;

public enum NodeStepResult { Running, Done }

// StepNode performs at most one search expansion and retains its continuation.
// Jobs must not use clocks or the caller's slice size to choose their work.
public interface INodeJob
{
    bool IsDone { get; }
    void StepNode();
}

public sealed class NodeScheduler
{
    private readonly Queue<INodeJob> jobs = new Queue<INodeJob>();
    public int Budget { get; private set; }
    public int Nodes { get; private set; }
    public int RemainingNodes { get { return Budget - Nodes; } }
    public bool IsBudgetExhausted { get { return RemainingNodes == 0 && jobs.Count != 0; } }

    public NodeScheduler(int nodeBudget)
    {
        if (nodeBudget < 0) throw new ArgumentOutOfRangeException(nameof(nodeBudget));
        Budget = nodeBudget;
    }

    public void Enqueue(INodeJob job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (!job.IsDone) jobs.Enqueue(job);
    }

    public void DiscardJobs() { jobs.Clear(); }

    // Done means completion or cap hit; IsBudgetExhausted distinguishes them.
    public NodeStepResult Step(int maxNodes)
    {
        if (maxNodes < 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        int allowance = Math.Min(maxNodes, RemainingNodes);
        while (jobs.Count != 0)
        {
            var job = jobs.Peek();
            if (job.IsDone) { jobs.Dequeue(); continue; }
            if (allowance == 0) break;
            // Charge before calling the job, including a node that throws.
            Nodes++;
            allowance--;
            job.StepNode();
        }
        return jobs.Count == 0 || RemainingNodes == 0 ? NodeStepResult.Done : NodeStepResult.Running;
    }
}
