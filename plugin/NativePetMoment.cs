using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets;
using BepInEx.Logging;
using HarmonyLib;
using Hearthstone.DataModels;
using Hearthstone.UI;
using Hearthstone.Progression;
using PegasusUtil;
using UnityEngine;

// Retail end-of-game pet moment (EndGameScreen.ShowPetXpGains / ShowPetRewards): the QuestXpReward PET bar, then one
// level-up scroll per level crossed. Presentation only: the XP is already durable in the settled match receipt.
// The native managers are driven directly because our results flow replaces EndGameScreen.ContinueEvents.
internal static class NativePetMoment
{
    private static LocalPetMomentStep step;
    private static ManualLogSource log;

    internal static bool Skippable => step?.Skippable == true;
    internal static bool BarStage => step?.BarStage == true;
    internal static string Diagnostics => step?.Diagnostics ?? "idle";

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        NativeResultsUI.PetStep = step = new LocalPetMomentStep(new NativePetHost(), () => Time.realtimeSinceStartup,
            message => log?.LogInfo(message), message => log?.LogWarning(message));
        // "Go to collection" would deep-link out of the finished GAMEPLAY scene while results are still presenting.
        // Patched on the scroll's data model factory: the 13-byte RewardScroll.IsGoToCollectionAllowed may be inlined.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(PetUtility), "CreateRewardScrollForPetLevel"),
            postfix: new HarmonyMethod(typeof(NativePetMoment), nameof(AfterPetScroll)));
    }

    internal static void Update() => step?.Update();

    private static void AfterPetScroll(RewardScrollDataModel __result)
    {
        if (__result != null && NativeResultsUI.ResultsPresenting) __result.AllowGoToCollection = false;
    }

    // Native buttons on the level-up scroll (hide/favourite/collection) get stable test-control ids from their object names.
    // The level-up scroll's own controls take input during the pet moment; the results overlay must not cover them.
    internal static bool OwnsScrollInput(PegUIElement element)
    {
        if (element == null || !NativeResultsUI.PetMomentActive) return false;
        var scroll = NativePetHost.FindScroll();
        if (scroll == null) return false;
        var root = scroll.GetComponentInParent<WidgetInstance>()?.gameObject ?? scroll.gameObject;
        return element.transform.IsChildOf(root.transform);
    }

    internal static IEnumerable<KeyValuePair<PegUIElement, string>> ScrollControls()
    {
        if (!NativeResultsUI.PetMomentActive) yield break;
        var scroll = NativePetHost.FindScroll();
        if (scroll == null) yield break;
        var root = scroll.GetComponentInParent<WidgetInstance>()?.gameObject ?? scroll.gameObject;
        var seen = new Dictionary<string, int>();
        foreach (var element in root.GetComponentsInChildren<PegUIElement>(false))
        {
            string name = element.gameObject.name;
            seen.TryGetValue(name, out int count); seen[name] = count + 1;
            yield return new KeyValuePair<PegUIElement, string>(element, "results.pet.scroll." + name + (count > 0 ? "#" + count : ""));
        }
    }
}

internal sealed class NativePetHost : ILocalPetNative
{
    private static readonly FieldInfo XpChanges = AccessTools.Field(typeof(RewardXpNotificationManager), "m_xpChanges");
    private float scrollCheckAt;
    private bool scrollVisible;

    public void ClearQueues()
    {
        var xp = RewardXpNotificationManager.Get();
        if (xp != null)
        {
            xp.SetTrackTypeFilter(RewardXpNotificationManager.PetFilteredTrackTypes);
            if (xp.IsShowingXpGains) xp.TerminateEarly();
            if (XpChanges?.GetValue(xp) is IList changes)
                for (int i = changes.Count - 1; i >= 0; i--)
                    if (changes[i] is RewardTrackXpChange change && change.RewardTrackType == (int)Global.RewardTrackType.PET) changes.RemoveAt(i);
        }
        PetsManager.Get()?.GetRewardPresenter()?.Clear();
    }

    public void Notify(LocalPetXpEvent move)
    {
        if (GameDbf.Pet?.GetRecord(move.PetId) == null)
            throw new InvalidOperationException("Pet record missing in the pinned client: " + move.PetId);
        if (RewardXpNotificationManager.Get() == null) throw new InvalidOperationException("RewardXpNotificationManager missing.");
        if (PetsManager.Get() == null) throw new InvalidOperationException("PetsManager missing.");
        if (!NativeUIProbe.NotifyPetXp(move.PetId, move.PreviousLevel, move.NewLevel, move.PreviousXp, move.NewXp)) throw new InvalidOperationException("Pet XP event has no listeners.");
    }

    public bool BeginBar(Action finished)
    {
        var xp = RewardXpNotificationManager.Get();
        xp.SetTrackTypeFilter(RewardXpNotificationManager.PetFilteredTrackTypes);
        if (!xp.HasXpGainsToShow()) return false;
        xp.InitEndOfGameFlow(null);
        xp.ShowRewardTrackXpGains(finished);
        return true;
    }

    public bool BarReady => RewardXpNotificationManager.Get()?.IsReady == true;

    public void TerminateBar()
    {
        var xp = RewardXpNotificationManager.Get();
        if (xp != null && xp.IsShowingXpGains) xp.TerminateEarly();
    }

    public bool ShowNextScroll(Action hidden) => PetsManager.Get()?.ShowNextReward(hidden) == true;

    // Throttled: the scene scan runs from the per-frame stall guard.
    public bool ScrollOnScreen
    {
        get
        {
            float now = Time.realtimeSinceStartup;
            if (now - scrollCheckAt < 0.25f) return scrollVisible;
            scrollCheckAt = now;
            try { scrollVisible = FindScroll() != null; } catch { scrollVisible = false; }
            return scrollVisible;
        }
    }

#pragma warning disable CS0618 // The pinned Unity predates FindAnyObjectByType.
    internal static RewardScroll FindScroll() => UnityEngine.Object.FindObjectOfType<RewardScroll>();
#pragma warning restore CS0618
}

