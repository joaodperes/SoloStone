using System;
using System.Linq;

internal sealed class LocalTierRewardCatchUp
{
    private bool queued;
    internal void Update(LocalProfile profile, bool idle, Action<LocalResultsReceipt, Action> enqueue, Action<Exception> log)
    {
        if (queued || !idle || profile == null) return;
        var pending = profile.PendingMilestoneCatchUp;
        if (pending == null) return;
        queued = true;
        bool completed = false;
        try
        {
            enqueue(new LocalResultsReceipt { MatchId = pending.Id,
                TierRewards = pending.TierRewards.Select(LocalProfile.Clone).ToArray() }, () => {
                if (completed) return;
                completed = true;
                try { profile.AcknowledgeMilestoneCatchUp(pending.Id); }
                catch (Exception error) { log?.Invoke(error); }
            });
        }
        catch (Exception error) { log?.Invoke(error); }
    }
}
