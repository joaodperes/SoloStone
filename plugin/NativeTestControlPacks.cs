#if !SOLOSTONE_RELEASE
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

internal sealed partial class NativeTestControl
{
    private void HandleOpenPacks(ControlEnvelope envelope)
    {
        AssertMainThread();
        var request = envelope.Request;
        var operation = ledger.Accept(request, out bool added);
        if (!added) { OperationReply(envelope.Connection, request, operation); return; }
        if (fault != null) Reject(operation, "Mutations frozen: " + fault);
        else if (ControlBusy().Count != 0) Reject(operation, "Another mutation unresolved");
        else if ((DateTime.UtcNow - request.ReceivedUtc).TotalSeconds > 10) Reject(operation, "Expired before execution");
        else if (!Enabled(out string enabledToken) || token != enabledToken)
        { Reject(operation, "Control flag changed"); Stop("Control flag changed"); }
        else
        {
            try
            {
                int count = request.Integer("count");
                var scene = SceneMgr.Get();
                var pack = scene?.GetMode() == SceneMgr.Mode.PACKOPENING ? PackOpening.Get() : null;
                var profile = LocalLadder.Profile;
                var director = NativePackTestUi.Field(pack, "m_director") as PackOpeningDirector;
                var stacks = NativePackTestUi.Field(pack, "m_unopenedPacks") as IEnumerable<KeyValuePair<int, UnopenedPack>>;
                var candidates = (stacks ?? Enumerable.Empty<KeyValuePair<int, UnopenedPack>>())
                    .Select(row => row.Value).Where(stack => stack != null && stack.gameObject.activeInHierarchy)
                    .OrderBy(stack => stack.GetBoosterId()).ToArray();
                bool MassAllowed(UnopenedPack stack) => pack.MassPackOpeningEnabled()
                    && count <= pack.MassPackOpeningPackLimit(stack.GetBoosterId())
                    && (bool)AccessTools.Method(typeof(PackOpening), "IsMassPackOpenable").Invoke(pack, new object[] { stack });
                int Available(UnopenedPack stack) => Math.Min(stack.GetCount(), profile?.PackInventory
                    .FirstOrDefault(row => row.PackType == NativeUIProjection.PackTypeForBooster(stack.GetBoosterId()))?.Count ?? 0);
                var target = candidates.FirstOrDefault(stack => Available(stack) >= count && (count == 1 || MassAllowed(stack)))
                    ?? candidates.OrderByDescending(Available).FirstOrDefault();
                string blocker = TestControlPacks.Blocker(count, pack != null, profile?.PackRevealPending == true,
                    NativeUIProbe.Active && profile != null && NativeResultsUI.CanStart && pack?.IsReady() == true
                        && scene.IsTransitioning() == false && director != null && !director.IsPlaying()
                        && NativePackTestUi.Field(pack, "m_draggedPack") == null
                        && NativePackTestUi.Field(pack, "m_centerPack") == null
                        && UniversalInputManager.Get()?.IsDialogActive() == false
                        && GameMenu.Get()?.GameMenuIsShown() != true && OptionsMenu.Get()?.IsShown() != true,
                    target == null ? 0 : Available(target), count == 1 || target != null && MassAllowed(target));
                if (blocker != null) throw new InvalidOperationException(blocker);
                var open = AccessTools.Method(typeof(PackOpening), "OpenBooster", new[] { typeof(UnopenedPack), typeof(int) })
                    ?? throw new InvalidOperationException("Native OpenBooster adapter unavailable");
                operation.Detail["count"] = count;
                operation.Detail["boosterId"] = target.GetBoosterId();
                operation.Detail["inputKind"] = "semantic-pack-drop";
                operation.Detail["nativeInvocationStarted"] = true;
                // DropPack calls this exact entry point with the held stack's count. Harmony commits and routes it.
                open.Invoke(pack, new object[] { target, count });
                if (!profile.PackRevealPending || profile.PendingPacks.Count != count)
                    throw new InvalidOperationException("Native opening did not commit the requested reveal; inspect profile before retry");
                operation.Detail["packSequence"] = profile.PackSequence;
                operation.Detail["packRevealPending"] = true;
                // Settled means native entry accepted, not animation complete. wait-ui observes the reveal.
                operation.Phase = "settled";
                uiSampleFrame = -1;
            }
            catch (Exception error)
            {
                if (operation.Detail.ContainsKey("nativeInvocationStarted"))
                {
                    fault = error.GetBaseException().Message;
                    operation.Phase = "fault"; operation.Detail["error"] = fault;
                }
                else Reject(operation, error.GetBaseException().Message);
            }
        }
        OperationReply(envelope.Connection, request, operation);
    }
}
#endif
