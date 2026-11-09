using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using Hearthstone.UI;
using PegasusShared;
using UnityEngine;

internal static class NativeTierRewards
{
    private static readonly LocalTierRewardCatchUp catchUp = new LocalTierRewardCatchUp();
    private static NativeTierRewardStep step;
    private static ManualLogSource log;

    internal static void Initialize(ManualLogSource logger)
    {
        log = logger;
        NativeResultsUI.TierRewardStep = step = new NativeTierRewardStep(logger,
            () => NativeUIProbe.RewardContextReady || NativeUIProbe.GameplayRewardContextReady);
    }

    // Test control: the shown chest's native click control and whether that click is accepted now.
    internal static PegUIElement ClickTarget => step?.ClickTarget;
    internal static bool ClickReady => step?.ClickReady == true;

    internal static void Update()
    {
        step?.Update();
        catchUp.Update(NativeUIProbe.RewardProfile,
            NativeUIProbe.RewardContextReady && NativeResultsUI.CanStart && !NativeRewardUI.Active
                && !NativeAchievementToast.Active && !NativeWelcomeUI.ScrollActive
                && !NativeResultsUI.BlocksEscape && !ClientBridge.OwnsLocalPostMatch
                && NativeUIProbe.RewardProfile?.Pending == null
                && NativeUIProbe.RewardProfile?.PackRevealPending != true,
            NativeResultsUI.PresentTierRewardsOnly,
            error => log?.LogWarning("NATIVE_TIER_CATCHUP " + error));
    }
}

// Display only: grants were committed by settlement/catch-up before this step runs.
internal sealed class NativeTierRewardStep : ILocalTierRewardStep
{
    // RankMgr.cs:38, same prefab passed to WidgetInstance.Create by EndGameScreen.cs:1129.
    private const string RewardPrefab = "RankedRewardDisplay.prefab:f95c6e7ec80adde4aa6c2f6df24262ea";
    private readonly ManualLogSource log;
    private readonly Func<bool> sceneReady;
    private Action done;
    private LocalResultsReceipt receipt;
    private IReadOnlyList<LocalTierRewardPlan> plans;
    private TranslatedMedalInfo medal;
    private WidgetInstance widget;
    private RankedRewardDisplay display;
    private Camera inputCamera;
    private int index, segmentEnd, generation;
    private float began, loadBegan;
    private bool debugEffects, cameraAdded, fallback, popup, retried, awaitingScene;
    private string popupId;
    private DialogBase dialog;

    // sceneReady: a reward can present in the current scene (settled HUB or finished GAMEPLAY), not mid-transition.
    internal NativeTierRewardStep(ManualLogSource log = null, Func<bool> sceneReady = null)
    { this.log = log; this.sceneReady = sceneReady ?? (() => true); }

    public void Present(LocalResultsReceipt value, Action completed)
    {
        if (completed == null) throw new ArgumentNullException(nameof(completed));
        // #51 retires an attempt before starting the next one, so a still-active presentation is stale.
        if (done != null) Abandon("superseded by a new presentation");
        if (value.TierRewards.Count == 0) { completed(); return; }
        receipt = value; done = completed; generation++; index = 0;
        began = Time.realtimeSinceStartup; fallback = popup = retried = awaitingScene = false;
        try
        {
            plans = LocalTierRewardPlan.Build(value.TierRewards,
                id => LocalShop.Default.Recipes.FirstOrDefault(recipe => recipe.Id == id)?.Name,
                // GameDbf.cs:101; RewardUtils.cs:2060 does not validate card-back ids before conversion.
                id => GameDbf.CardBack != null && GameDbf.CardBack.GetRecord(id) != null,
                id => LocalDecks.All.Concat(LocalDecks.TestLoaners).Concat(LocalDecks.TestOpponents).FirstOrDefault(deck => deck.Id == id)?.Opponent
                    ?? NpcWorldRuntime.World?.Find(id)?.DisplayName);   // challengers are world NPC ids
            // #51 EnsureReceipt publishes the settled medal through NativeUIProbe.PublishResultsMedal.
            // Same current medal as EndGameScreen.cs:1614-1616; MedalInfoTranslator.cs:187.
            medal = RankMgr.Get().GetLocalPlayerMedalInfo().GetCurrentMedal(FormatType.FT_STANDARD);   // NativeUIProbe.LocalDeckFormat
            if (medal == null || !medal.IsValid()) throw new InvalidOperationException("After-match medal unavailable.");
            PresentNext();
        }
        catch (Exception error) { Failed(error); }
    }

    private void PresentNext()
    {
        if (index == plans.Count) { Finish(); return; }
        if (plans[index].Tiles.Count == 0) { ShowCaptionPopup(); return; }
        segmentEnd = index;
        var groups = new List<List<RewardData>>();
        // RankedRewardDisplay.cs:87-90 drops empty groups; caption-only milestones use a native popup between segments.
        while (segmentEnd < plans.Count && plans[segmentEnd].Tiles.Count > 0)
            groups.Add(plans[segmentEnd++].Tiles.Select(NativeTile).ToList());
        loadBegan = Time.realtimeSinceStartup;
        int token = generation;
        // EndGameScreen.cs:1129-1139 creates a widget and waits for its child, rather than loading a raw prefab.
        var expected = widget = WidgetInstance.Create(RewardPrefab);
        if (expected == null) throw new InvalidOperationException("Ranked reward widget missing.");
        expected.RegisterReadyListener(_ => {
            if (done == null || token != generation || widget != expected) return;
            try
            {
                display = expected.GetComponentInChildren<RankedRewardDisplay>();
                if (display == null) throw new InvalidOperationException("Ranked reward display missing.");
                // RankedRewardDisplay.cs:220 supplies its own hitbox, positioning and effects when no end-game screen exists.
                if (EndGameScreen.Get() == null)
                {
                    // EndGameScreen.Awake normally registers the popup camera; startup catch-up has no screen.
                    inputCamera = CameraUtils.FindFirstByLayerMask(1 << 19);
                    if (inputCamera == null || PegUI.Get() == null) throw new InvalidOperationException("Ranked reward input camera missing.");
                    cameraAdded = !((System.Collections.IList)AccessTools.Field(typeof(PegUI), "m_UICams").GetValue(PegUI.Get())).Contains(inputCamera);
                    if (cameraAdded) PegUI.Get().AddInputCamera(inputCamera);
                    display.m_debugClickCatcher.gameObject.layer = 19;
                    debugEffects = true;
                    AccessTools.Method(typeof(RankedRewardDisplay), "ActivateDebugEquivalentsOfEndGameScreen").Invoke(display, null);
                }
                // AnimationUtil sends OnShown to the display GameObject, not the outer widget instance.
                var caption = display.gameObject.AddComponent<NativeTierRewardCaption>();
                caption.Owner = this; caption.Display = display; caption.Log = log;
                // RankedRewardDisplay.cs:68-98 converts display data only; no MarkAsShown/notice/claim path.
                display.Initialize(medal, groups, () => {
                    if (done == null || token != generation || widget != expected) return;
                    log?.LogInfo("NATIVE_TIER_DISMISSED " + receipt.MatchId + " groups=" + index + ".." + segmentEnd);
                    try { CleanupWidget(); index = segmentEnd; PresentNext(); }
                    catch (Exception error) { Failed(error); }
                });
                display.Show();
            }
            catch (Exception error) { Failed(error); }
        });
    }

    private static RewardData NativeTile(LocalTierRewardTile tile)
    {
        // Constructors: BoosterPackRewardData.cs:16, CardBackRewardData.cs:14, RandomCardRewardData.cs:16.
        // RewardUtils.cs:2060-2099 maps these to card-back, random-card and booster data models.
        switch (tile.Kind)
        {
            case LocalTierRewardTileKind.Pack: return new BoosterPackRewardData(tile.Id, tile.Count);
            case LocalTierRewardTileKind.CardBack: return new CardBackRewardData(tile.Id);
            case LocalTierRewardTileKind.RandomCard:
                return new RandomCardRewardData((TAG_RARITY)Enum.Parse(typeof(TAG_RARITY), tile.Rarity), TAG_PREMIUM.NORMAL, tile.Count);
            default: throw new ArgumentOutOfRangeException(nameof(tile.Kind));
        }
    }

    private void ShowCaptionPopup()
    {
        int expectedIndex = index;
        var plan = plans[index];
        popupId = "LOCAL_NATIVE_TIER_" + receipt.MatchId + "_" + index;
        popup = ShowPopup(plan.Header, string.Join("\n", plan.Captions), () => {
            if (index != expectedIndex) return;
            index++; PresentNext();
        });
        if (!popup) throw new InvalidOperationException("Milestone caption popup unavailable.");
    }

    private bool ShowPopup(string header, string text, Action continued)
    {
        int token = generation;
        string expectedId = popupId;
        // DialogManager.cs:275/899: the process callback runs when the queued request is shown; false destroys it.
        // Retired requests are rejected there, so cleanup never has to remove queue entries (which can drop
        // unrelated dialogs) and only ever hides the dialog this step owns.
        return DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(new AlertPopup.PopupInfo {
            m_id = popupId, m_headerText = header, m_text = text,
            m_showAlertIcon = false, m_responseDisplay = AlertPopup.ResponseDisplay.CANCEL,
            m_cancelText = "CONTINUE", m_keyboardEscIsCancel = false,
            m_responseCallback = (response, data) => {
                if (response != AlertPopup.Response.CANCEL || done == null || token != generation || !popup || popupId != expectedId) return;
                popup = false; popupId = null;
                dialog = null;
                try { continued(); }
                catch (Exception error) { Failed(error); }
            }
        }, (shown, _) => {
            if (done == null || token != generation || popupId != expectedId) return false;
            dialog = shown;
            return true;
        });
    }

    internal void Update()
    {
        if (done == null) return;
        // While the chest or a caption popup waits on the player, #51 keeps resetting its clock; so do we.
        if (AwaitingPlayer) began = Time.realtimeSinceStartup;
        // #51 owns the 120 s timeout and has retired this attempt by now: clean up, never report success.
        if (Time.realtimeSinceStartup - began >= 120f) { Abandon("timed out"); return; }
        if (fallback)
        {
            if (popup) return;
            try
            {
                // done() is success in #51; failures need their own fallback before that callback.
                popupId = "LOCAL_NATIVE_TIER_FALLBACK_" + receipt.MatchId;
                popup = ShowPopup("REWARDS SAVED", LocalRewardPresentation.TierRewardText(receipt.TierRewards)
                    + "\n\nYour rewards are already in your profile.", Finish);
                if (!popup) Abandon("fallback popup unavailable");
            }
            catch (Exception error) { log?.LogWarning("NATIVE_TIER_FALLBACK " + error); Abandon("fallback popup failed"); }
            return;
        }
        if (awaitingScene)
        {
            if (!sceneReady()) return;
            awaitingScene = false;
            try { PresentNext(); }
            catch (Exception error) { Failed(error); }
            return;
        }
        if (widget != null && display == null && Time.realtimeSinceStartup - loadBegan > 45f)
            Failed(new TimeoutException("Ranked reward widget load timed out."));
        else if (!popup && widget == null)
        {
            // A scene change destroyed the chest (live: a restart shown during LOGIN->HUB). Show it again once the
            // scene settles; a second loss gets the REWARDS SAVED popup, never a silent wait on the attempt timeout.
            CleanupWidget();
            if (retried) { Failed(new InvalidOperationException("Ranked reward widget destroyed again outside the step.")); return; }
            retried = awaitingScene = true;
            log?.LogWarning("NATIVE_TIER_RETRY " + receipt.MatchId + " reward widget destroyed outside the step");
        }
    }

    // The control the shown chest listens to: the retail end-game hitbox, or the display's own catcher without that screen.
    internal PegUIElement ClickTarget => done == null || fallback || display == null ? null
        : debugEffects ? display.m_debugClickCatcher : EndGameScreen.Get()?.m_hitbox;
    // Caption popups are dialogs (dialog.cancel CONTINUE); the chest takes a click only while idle and shown.
    internal bool ClickReady => !popup && AwaitingPlayer && ClickTarget != null;

    // Shown and waiting for a click (chest or popup), as opposed to loading or stalled.
    public bool AwaitingPlayer => done != null && !fallback && (popup || (display != null && shown
        && !(bool)AccessTools.Field(typeof(RankedRewardDisplay), "m_isAnimating").GetValue(display)
        && !(bool)AccessTools.Field(typeof(RankedRewardDisplay), "m_isHidePending").GetValue(display)));
    private bool shown;
    internal void MarkShown()
    {
        if (done == null || display == null) return;
        shown = true;
        log?.LogInfo("NATIVE_TIER_SHOWN " + receipt.MatchId);
    }

    internal LocalTierRewardPlan CurrentPlan
    {
        get
        {
            if (done == null || display == null || fallback) return null;
            // RankedRewardDisplay.cs:145-158 decrements before binding the next milestone group.
            int remaining = (int)AccessTools.Field(typeof(RankedRewardDisplay), "m_numAnimationsRemaining").GetValue(display);
            return plans[Math.Max(index, Math.Min(segmentEnd - 1, segmentEnd - remaining))];
        }
    }

    internal void Failed(Exception error)
    {
        if (done == null) return;
        log?.LogWarning("NATIVE_TIER_REWARDS " + error);
        CleanupWidget(); fallback = true;
    }

    private void CleanupWidget()
    {
        try
        {
            if (debugEffects && display != null)
                (AccessTools.Field(typeof(RankedRewardDisplay), "m_screenEffectsHandle").GetValue(display) as ScreenEffectsHandle)?.StopEffect();
        }
        catch (Exception error) { log?.LogWarning("NATIVE_TIER_CLEANUP " + error); }
        finally
        {
            if (cameraAdded && inputCamera != null && PegUI.Get() != null) PegUI.Get().RemoveInputCamera(inputCamera);
            cameraAdded = false; inputCamera = null;
            if (widget != null) UnityEngine.Object.Destroy(widget.gameObject);
            widget = null; display = null; debugEffects = false; shown = false;
        }
    }

    // Success: the player saw the chest or the fallback popup.
    private void Finish()
    {
        var completed = done;
        if (completed == null) return;
        done = null; generation++;
        CleanupUi();
        completed();
    }

    // The attempt was retired by #51 (its timeout, a flow failure or a new receipt): no retry may outlive it.
    public void Retire() => Abandon("attempt retired");

    // Not success: drop done without calling it, so nothing is acknowledged unseen; #51's timeout/fallback owns it.
    private void Abandon(string reason)
    {
        if (done == null) return;
        log?.LogWarning("NATIVE_TIER_ABANDON " + reason);
        done = null; generation++;
        CleanupUi();
    }

    private void CleanupUi()
    {
        try
        {
            CleanupWidget();
            if (dialog != null) dialog.Hide();
        }
        catch (Exception error) { log?.LogWarning("NATIVE_TIER_CLEANUP " + error); }
        finally { dialog = null; popupId = null; popup = false; fallback = false; plans = null; receipt = null; }
    }
}

// RankedRewardDisplay.cs:12-26 has no title/caption field: the milestone name goes on the native blue ribbon instead.
internal sealed class NativeTierRewardCaption : MonoBehaviour
{
    internal NativeTierRewardStep Owner;
    internal RankedRewardDisplay Display;
    internal ManualLogSource Log;
    private static readonly Type UberText = GameTypes.ByName("UberText");
    private static readonly System.Reflection.PropertyInfo UberTextText = UberText == null ? null : AccessTools.Property(UberText, "Text");
    private static readonly System.Reflection.FieldInfo Animating = AccessTools.Field(typeof(RankedRewardDisplay), "m_isAnimating"),
        HidePending = AccessTools.Field(typeof(RankedRewardDisplay), "m_isHidePending");
    private readonly HashSet<Renderer> hidden = new HashSet<Renderer>();
    private Vector3 centreOffset, lastItemsPosition;
    private bool centred;
    private int centreLogs;

    private void OnShown() => Owner?.MarkShown();

    // The display binds a retail season chest in Chest mode (RankedRewardDisplay.cs:75).
    // Keep native widgets/FSMs active: disabling Chests can stall reveal and click animations.
    // Suppress rendering only; text bindings expose localization keys, not translated English.
    private void LateUpdate()
    {
        try
        {
            if (Display == null || !Display.isActiveAndEnabled) return;
            var root = Display.transform;
            Transform chests = root.Find("Chests"), items = root.Find("RewardItems"), banner = root.Find("GenericBlueRewardBanner");
            // Only the reward items and the blue ribbon are ours to show. The chest, its medal and any medal frame
            // outside them go; clicks hit colliders (m_debugClickCatcher / EndGameScreen hitbox), not renderers.
            foreach (var renderer in Display.GetComponentsInChildren<Renderer>(true))
            {
                var node = renderer.transform;
                if (Under(node, items) || Under(node, banner)) continue;
                if (renderer.GetType().Name == "ParticleSystemRenderer" && !Under(node, chests)) continue;
                Hide(renderer);
            }
            foreach (var medal in Display.GetComponentsInChildren<RankedMedal>(true))
                foreach (var renderer in medal.GetComponentsInChildren<Renderer>(true)) Hide(renderer);
            if (UberTextText == null) return;
            if (chests != null) foreach (Component text in chests.GetComponentsInChildren(UberText, true)) SetText(text, "");
            var plan = Owner?.CurrentPlan;
            if (banner != null && plan != null)
                foreach (Component text in banner.GetComponentsInChildren(UberText, true))
                    switch (text.gameObject.name)
                    {
                        case "HeadlineText": SetText(text, plan.Title + "!"); break;
                        case "SourceText": SetText(text, BannerLine(plan)); break;
                        case "DetailsText": SetText(text, ""); break;
                    }
            Centre(items, banner);
        }
        catch (Exception error) { Owner?.Failed(error); }
    }

    internal static string BannerLine(LocalTierRewardPlan plan)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(plan.Subtitle)) lines.Add(plan.Subtitle);
        lines.AddRange(plan.Captions);
        return lines.Count == 0 ? "Click to continue" : string.Join("\n", lines.ToArray());
    }

    // The retail x1/x2/... slots sit beside the season chest. With the chest hidden, shift the whole RewardItems
    // group so its visible bounds centre under the ribbon; slot spacing is untouched. Measured only while the native
    // display is idle (not mid click/death animation); an animation that rewrites the position gets the offset back.
    private void Centre(Transform items, Transform banner)
    {
        if (items == null || banner == null) return;
        if (centred && items.localPosition != lastItemsPosition) items.localPosition += centreOffset;
        if (!(bool)Animating.GetValue(Display) && !(bool)HidePending.GetValue(Display)
            && VisibleBounds(items, out var item) && VisibleBounds(banner, out var ribbon))
        {
            var root = Display.transform;
            float error = root.InverseTransformPoint(ribbon.center).x - root.InverseTransformPoint(item.center).x;
            float width = Math.Abs(root.InverseTransformVector(item.size).x);
            if (Math.Abs(error) > Math.Max(width * .01f, 1e-4f))
            {
                var shift = items.parent.InverseTransformVector(root.TransformVector(new Vector3(error, 0, 0)));
                items.localPosition += shift;
                centreOffset += shift; centred = true;
                if (centreLogs++ < 8) Log?.LogInfo("NATIVE_TIER_CENTRE " + Path(items) + " dx=" + error + " width=" + width);
            }
        }
        lastItemsPosition = items.localPosition;
    }

    // Visible meshes only: text meshes and particles move or resize with content and would skew the centre.
    private static bool VisibleBounds(Transform target, out Bounds bounds)
    {
        bounds = default(Bounds);
        bool any = false;
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy
                || renderer.GetType().Name == "ParticleSystemRenderer" || renderer.bounds.size == Vector3.zero
                || (UberText != null && renderer.GetComponentInParent(UberText) != null)) continue;
            if (any) bounds.Encapsulate(renderer.bounds); else bounds = renderer.bounds;
            any = true;
        }
        return any;
    }

    private static bool Under(Transform node, Transform root) => root != null && node.IsChildOf(root);

    private void Hide(Renderer renderer)
    {
        renderer.forceRenderingOff = true;
        if (hidden.Add(renderer)) Log?.LogInfo("NATIVE_TIER_HIDE " + Path(renderer.transform));
    }

    private void SetText(Component text, string value)
    {
        if ((string)UberTextText.GetValue(text, null) == value) return;
        UberTextText.SetValue(text, value, null);
        Log?.LogInfo("NATIVE_TIER_TEXT " + Path(text.transform) + " = " + value);
    }

    private static string Path(Transform node) => node.parent == null ? node.name : Path(node.parent) + "/" + node.name;
}
