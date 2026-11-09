using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

internal static class NativeRewardUI
{
    private sealed class LoadAttempt { internal string MatchId; internal int Generation; internal Action<GameObject> Callback; }
    private const string RewardPrefab = "RewardBoxes.prefab:f136fead3d6a148c6801f1e3bd2e8267";
    private static ManualLogSource log;
    private static RewardBoxesDisplay display;
    private static string matchId = "", prefabState = "idle", ackState = "idle", actorFacts = "none";
    private static string fallbackReason = "", ackRetryReason = "", summaryBody = "", summaryHeader = "", noItemsText = "";
    private static string playedDeck, placementFacts = "hub";
    private static int generation, createdMask, openedMask, narrativeSuppressions, detailsAttempt;
    private static float loadBegan, revealBegan, allOpenedAt = -1, doneReadyAt, nextWatch;
    private static int reachableMask;
    // GAMEPLAY only: the camera the boxes were framed for, and the one this presentation registered for PegUI input.
    private static Camera placedCamera, inputAdded;
    private static double placedScale = 1;
    private static readonly FieldInfo PackageInstancesField = AccessTools.Field(typeof(RewardBoxesDisplay), "m_rewardPackageInstances"),
        RewardObjectsField = AccessTools.Field(typeof(RewardBoxesDisplay), "m_RewardObjects"),
        CurrentDialogField = AccessTools.Field(typeof(DialogManager), "m_currentDialog"),
        DialogQueueField = AccessTools.Field(typeof(DialogManager), "m_dialogRequests");
    private static LocalRewardPhase phase;
    private static List<RewardData> rewards = new List<RewardData>();
    private static bool fallbackPopup, savePopup, detailsPopup;
    // #28: the no-reward card shows REMATCH (answering the popup's otherwise unused OK) when the rematch gate allows one.
    private static bool cardRematch;
    // Clones of the native Done button: same PegUI release path and look, owned by this presentation only.
    private static NormalButton detailedButton, rematchButton;
    // The no-reward card's own controls: REMATCH (red, when offered) beside CONTINUE and DETAILS (grey) under the card.
    private static UIBButton cardButton, cardDetails;
    private static bool cardStyled, cardFailed, cardConceded, cardDetailsFailed;
    private static float cardStyledAt;
    private static readonly FieldInfo ShowAnimStateField = AccessTools.Field(typeof(DialogBase), "m_showAnimState");

    internal static bool Active => phase != LocalRewardPhase.Idle;
    internal static RewardBoxesDisplay ControllerOwner => display;
    internal static string ControllerContext => matchId + ":" + generation + ":" + phase + ":" + createdMask + ":" + openedMask;
    internal static bool ControllerDoneReady => phase == LocalRewardPhase.DoneReady && !detailsPopup;
    internal static bool ControllerPackageReady(int index) => index >= 0 && index < rewards.Count && phase == LocalRewardPhase.Revealing
        && (createdMask & (1 << index)) != 0 && (openedMask & (1 << index)) == 0;

    internal static bool ControllerPackageReady(int index, RewardPackage package) => ControllerPackageReady(index) && package != null
        && PackageInstancesField?.GetValue(display) is List<GameObject> boxes && index < boxes.Count && boxes[index] != null
        && (package.gameObject == boxes[index] || package.transform.IsChildOf(boxes[index].transform));
    // A visible toast and its one-frame stable-HUB continuation own input just like reward boxes/popups; so does the #57
    // welcome reward scroll (from the popup's OK until the scroll is gone).
    internal static bool BlocksHome => NativeAchievementToast.Active || NativeResultsUI.BlocksEscape
        || display != null || OwnPopupUp() || NativeWelcomeUI.ScrollActive;

    // While a reward presentation is up only its own controls take input: the boxes, Done, DETAILED RESULTS and the
    // presentation's dialogs. The HUB Box buttons are gated (NativeUIProbe) and the ui tree reports them covered.
    internal static bool OwnsInput(PegUIElement element)
    {
        if (element == null) return false;
        if (ReferenceEquals(element, detailedButton) || ReferenceEquals(element, rematchButton)) return true;
        if (NativeWelcomeUI.OwnsInput(element)) return true;
        if (display != null && element.transform.IsChildOf(display.transform)) return true;
        return element.GetComponentInParent<DialogBase>() != null;
    }
    internal static PegUIElement DetailedButton => detailedButton != null && detailedButton.gameObject.activeInHierarchy ? detailedButton
        : cardDetails != null && cardDetails.gameObject.activeInHierarchy ? cardDetails : (PegUIElement)null;
    internal static PegUIElement RematchButton => rematchButton != null && rematchButton.gameObject.activeInHierarchy ? rematchButton
        : cardButton != null && cardButton.gameObject.activeInHierarchy ? cardButton : (PegUIElement)null;
    // HUB after a restart, otherwise the finished GAMEPLAY scene right after rank and class XP.
    private static bool PresentContext => NativeUIProbe.RewardContextReady || NativeResultsUI.PresentsInGameplay;

    internal static string Diagnostics => "match=" + matchId + ";generation=" + generation + ";phase=" + phase
        + ";items=" + string.Join(",", rewards.Select(Describe)) + ";prefab=" + prefabState
        + ";actors=" + actorFacts + ";createdMask=" + createdMask + ";openedMask=" + openedMask
        + ";done=" + (phase == LocalRewardPhase.DoneReady) + ";ack=" + ackState
        + ";fallback=" + fallbackReason + ";narrativeSuppressed=" + narrativeSuppressions
        + ";placement=" + placementFacts + ";detailedButton=" + (detailedButton != null) + ";rematchButton=" + (rematchButton != null)
        + ";cardRematch=" + cardRematch + ";details=" + detailsPopup + ";gameplay=" + NativeResultsUI.PresentsInGameplay;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        NativeButtonTint.Log = logger;
        NativeUIProbe.InitializeDeckCopyRules(harmony);
        harmony.Patch(Required(typeof(RewardBoxesDisplay), "CreateRewardInstance",
                new[] { typeof(RewardData), typeof(int), typeof(Vector3), typeof(bool) }),
            postfix: new HarmonyMethod(typeof(NativeRewardUI), nameof(ObserveRewardActor)));
        harmony.Patch(Required(typeof(RewardBoxesDisplay), "OpenReward", new[] { typeof(int), typeof(Vector3) }),
            postfix: new HarmonyMethod(typeof(NativeRewardUI), nameof(ObserveRewardOpened)));
        harmony.Patch(Required(typeof(RewardBoxesDisplay), "OnDoneButtonShown",
                new[] { typeof(Spell), typeof(object) }),
            postfix: new HarmonyMethod(typeof(NativeRewardUI), nameof(ObserveRewardDoneReady)));
        harmony.Patch(Required(typeof(RewardBoxesDisplay), "OnNavigateBack"),
            prefix: new HarmonyMethod(typeof(NativeRewardUI), nameof(BeforeRewardNavigateBack)));
        harmony.Patch(Required(typeof(RewardBoxesDisplay), "OnDestroy"),
            prefix: new HarmonyMethod(typeof(NativeRewardUI), nameof(BeforeRewardDestroy)));
        harmony.Patch(Required(typeof(BnetBar), "HandleEscapeKey"),
            prefix: new HarmonyMethod(typeof(NativeRewardUI), nameof(BeforeRewardEscape)));
        harmony.Patch(Required(typeof(NarrativeManager), "OnArenaRewardsShown"),
            prefix: new HarmonyMethod(typeof(NativeRewardUI), nameof(SuppressOwnedArenaNarrative)));
        harmony.Patch(Required(typeof(PopupDisplayManager), "CanShowRankedIntroForNewPlayer"),
            prefix: new HarmonyMethod(typeof(NativeRewardUI), nameof(SuppressLocalRankedIntro)));
        harmony.Patch(Required(typeof(DialogManager), "AddToQueue", new[] { typeof(DialogManager.DialogRequest) }),
            postfix: new HarmonyMethod(typeof(NativeRewardUI), nameof(ObserveRankedIntro)));
    }

    private static MethodInfo Required(Type type, string name, Type[] args = null) =>
        (args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args))
        ?? throw new MissingMethodException(type.FullName, name);

    internal static void Update()
    {
        GuardEndGameInput();
        string settlementError = LocalLadder.RewardSaveError;
        if (settlementError != null)
        {
            if (NativeUIProbe.RewardRetryContextReady) ShowSaveRetry(settlementError);
            return;
        }
        if (!PresentContext) return;
        LocalMatchRecord pending = NativeUIProbe.RewardProfile?.PendingRewardPresentation;
        if (phase != LocalRewardPhase.Idle && LocalRewardSafety.ResetToIdle(phase, NativeUIProbe.RewardContextReady, pending != null, BlocksHome))
        {
            log?.LogInfo("NATIVE_REWARD_IDLE_RESET phase=" + phase + ";match=" + matchId);
            ResetFlow();
            return;
        }
        if (phase == LocalRewardPhase.LoadingRewardBoxes && Time.realtimeSinceStartup - loadBegan > 45f)
            Fallback("Native reward asset load timed out.");
        if (phase == LocalRewardPhase.Revealing || phase == LocalRewardPhase.DoneReady) Watch();
        if (phase == LocalRewardPhase.Summary) { ShowNoItemsPopup(); StyleNoItemsCard(); return; }
        if (phase == LocalRewardPhase.NativeFallback && !fallbackPopup) { ShowFallbackPopup(); return; }
        if (phase == LocalRewardPhase.SaveError && !savePopup && ackRetryReason.Length != 0)
        { ShowAckRetry(matchId, ackRetryReason); return; }
        if (pending == null) return;
        if (phase == LocalRewardPhase.Idle && NativeResultsUI.BoxesReady && !fallbackPopup && !savePopup)
            BeginRewards(pending);
    }

    private static bool BeforeRewardEscape(ref bool __result)
    {
        if (NativeAchievementToast.Active || NativeResultsUI.BlocksEscape) { __result = true; return false; }
        // Deck Guide pages: with no dialog up between two pages Escape must not open the Game Menu over the picker.
        if (NativeDeckPickerGuide.Open) { __result = true; return false; }
        // #57 Escape over the welcome reward scroll dismisses it (its own Hide); while it loads Escape does nothing.
        if (NativeWelcomeUI.DismissOnEscape()) { __result = true; return false; }
        // Escape over Feats closes Feats (like a click outside) instead of opening the Game Menu over the Journal.
        if (NativeAchievements.CloseOnEscape()) { __result = true; return false; }
        bool exactOwner = display != null && ReferenceEquals(RewardBoxesDisplay.Get(), display);
        LocalRewardEscapeAction action = LocalRewardPresentation.PostMatchEscape(LocalRewardPresentation.RewardEscape(
            exactOwner, phase, openedMask, rewards.Count), detailsPopup, NativeResultsUI.PresentsInGameplay);
        if (action == LocalRewardEscapeAction.Passthrough) return true;
        __result = true;
        if (action == LocalRewardEscapeAction.NavigateBack) Navigation.GoBack();
        return false;
    }

    private static void BeginRewards(LocalMatchRecord match)
    {
        LocalRewardSummary summary = LocalRewardPresentation.Summary(match);
        summaryBody = NativeResultsUI.Receipt.SummaryText(summary);
        summaryHeader = summary.Header;
        noItemsText = LocalRewardPresentation.NoItemsText(summary);
        playedDeck = match.Deck;
        cardConceded = match.Conceded;
        matchId = match.Id;
        fallbackReason = "";
        rewards = RewardItems(summary, match.RewardVersion);
        if (rewards.Count == 0)
        {
            generation++;
            phase = LocalRewardPhase.Summary;
            ShowNoItemsPopup();
        }
        else BeginAnimation(match.Id);
    }

    // Nothing to open: one compact card; the full breakdown only behind DETAILED RESULTS. Both continue to the picker.
    private static void ShowNoItemsPopup()
    {
        string expectedMatch = matchId; int expectedGeneration = generation;
        if (!CurrentReceipt(expectedMatch)) return;
        NativeResultsUI.PresentNoItems(continued => {
            cardRematch = RematchOffered(expectedMatch);
            var info = new AlertPopup.PopupInfo {
                m_id = LocalRewardPresentation.NoItemsPopupPrefix + expectedMatch + "_" + expectedGeneration,
                m_headerText = summaryHeader, m_text = noItemsText, m_showAlertIcon = false,
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_confirmText = "CONTINUE", m_cancelText = LocalRewardPresentation.DetailsCardLabel, m_keyboardEscIsCancel = false,
                m_responseCallback = (response, data) => {
                    if (matchId != expectedMatch || generation != expectedGeneration
                        || phase != LocalRewardPhase.Summary || !CurrentReceipt(expectedMatch)) return;
                    if (response == AlertPopup.Response.CANCEL) { if (!ShowDetails(expectedMatch, continued)) continued(); }
                    else if (response == AlertPopup.Response.CONFIRM) continued();
                    // OK only comes from the card's REMATCH; anything else still continues instead of leaving a hidden popup.
                    else if (response == AlertPopup.Response.OK) { if (cardRematch) RematchAndContinue(expectedMatch, continued); else continued(); }
                }
            };
            try { return DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(info); }
            catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_NO_ITEMS_POPUP " + error.Message); return false; }
        }, () => AcknowledgeAndRoute(expectedMatch));
    }

    // continued == null: opened from the reward boxes, BACK returns to them. Otherwise CONTINUE leaves for the picker.
    private static bool ShowDetails(string expectedMatch, Action continued)
    {
        if (detailsPopup) return true;
        int expectedGeneration = generation, attempt = ++detailsAttempt;
        var info = new AlertPopup.PopupInfo {
            m_id = LocalRewardPresentation.DetailsPopupPrefix + expectedMatch + "_" + expectedGeneration,
            m_headerText = LocalRewardPresentation.DetailedResultsLabel, m_text = summaryBody, m_showAlertIcon = false,
            m_responseDisplay = continued == null ? AlertPopup.ResponseDisplay.OK : AlertPopup.ResponseDisplay.CONFIRM,
            m_okText = "BACK", m_confirmText = "CONTINUE", m_keyboardEscIsCancel = false,
            m_responseCallback = (response, data) => {
                if (attempt != detailsAttempt) return;
                detailsPopup = false;
                if (matchId != expectedMatch || generation != expectedGeneration || !CurrentReceipt(expectedMatch)) return;
                if (continued != null) { if (phase == LocalRewardPhase.Summary) continued(); }
                else if (phase == LocalRewardPhase.DoneReady)
                {
                    doneReadyAt = Time.realtimeSinceStartup;
                    SetDoneInput(true);
                }
            }
        };
        try { detailsPopup = DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(info); }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_DETAILS_POPUP " + error.Message); detailsPopup = false; }
        return detailsPopup;
    }

    private static void OpenDetails(string expectedMatch, int expectedGeneration)
    {
        if (detailsPopup || matchId != expectedMatch || generation != expectedGeneration || phase != LocalRewardPhase.DoneReady
            || display == null || !CurrentReceipt(expectedMatch)) return;
        SetDoneInput(false);
        if (!ShowDetails(expectedMatch, null)) SetDoneInput(true);
    }

    // While the breakdown is open the boxes take no clicks; Done stays the only way out once it closes.
    private static void SetDoneInput(bool enabled)
    {
        try
        {
            if (display?.m_DoneButton != null) RenderUtils.EnableColliders(display.m_DoneButton.gameObject, enabled);
            if (detailedButton != null) RenderUtils.EnableColliders(detailedButton.gameObject, enabled);
            if (rematchButton != null) RenderUtils.EnableColliders(rematchButton.gameObject, enabled);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_DETAILS_INPUT " + error.Message); }
    }

    private static void AttachDetailedButton(RewardBoxesDisplay owner, int column, int count)
    {
        if (owner?.m_DoneButton == null || detailedButton != null) return;
        string expectedMatch = matchId; int expectedGeneration = generation;
        // The breakdown is a convenience: Done keeps working without it.
        detailedButton = CloneDone(owner.m_DoneButton, "LocalDetailedResultsButton", LocalRewardPresentation.BoxesDetailsLabel,
            (camera, source) => UnderDone(camera, source, column, count), () => OpenDetails(expectedMatch, expectedGeneration),
            "NATIVE_RESULTS_DETAILED_BUTTON");
        NativeButtonTint.Apply(detailedButton, NativeButtonTint.Tint.Grey);
    }

    // #28: REMATCH under Done, left in the secondary row, when the rematch gate allows one (the caller asks; beside Done it
    // sat between the reward boxes: live 2026-10-28). It is Done plus a queued rematch: the same fade, receipt
    // acknowledgement and route to the picker, where NativeRematchUI starts it.
    private static void AttachRematchButton(RewardBoxesDisplay owner)
    {
        if (owner?.m_DoneButton == null || rematchButton != null) return;
        string expectedMatch = matchId; int expectedGeneration = generation;
        rematchButton = CloneDone(owner.m_DoneButton, "LocalRematchButton", NativeRematchUI.Label,
            (camera, source) => UnderDone(camera, source, 0, 2), () => RematchFromBoxes(expectedMatch, expectedGeneration),
            "NATIVE_RESULTS_REMATCH_BUTTON");
        NativeButtonTint.Apply(rematchButton, NativeButtonTint.Tint.Red);
    }

    // Null (and logged) on failure: these extras never block Done.
    private static NormalButton CloneDone(NormalButton source, string name, string label, Func<Camera, NormalButton, Vector3> position,
        Action release, string failure)
    {
        NormalButton clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
            clone.name = name;
            clone.ClearEventListeners();
            clone.SetText(label);
            FitLabel(clone);
            Camera camera = (placedCamera != null ? placedCamera : CameraUtils.FindFirstByLayerMask(1 << source.gameObject.layer))
                ?? throw new InvalidOperationException("Native reward button camera unavailable.");
            clone.transform.position = position(camera, source);
            clone.gameObject.SetActive(true);
            // Same native show path as RewardBoxesDisplay.AllDone: Spell BIRTH on the visual child.
            // SpellBase lives in Blizzard.T5.Game, absent from the compile references: reach Spell through Component.
            Component spell = clone.m_button != null ? clone.m_button.GetComponent(typeof(Spell)) : null;
            MethodInfo activate = spell == null ? null : spell.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "ActivateState" && method.GetParameters().Length == 1);
            if (activate != null) activate.Invoke(spell, new[] { Enum.ToObject(activate.GetParameters()[0].ParameterType, 1) });
            RenderUtils.EnableColliders(clone.gameObject, true);
            clone.SetEnabled(true);
            clone.AddEventListener(UIEventType.RELEASE, _ => release());
            return clone;
        }
        catch (Exception error)
        {
            log?.LogWarning(failure + " " + error.Message);
            if (clone != null) UnityEngine.Object.Destroy(clone.gameObject);
            return null;
        }
    }

    private static bool RematchOffered(string expectedMatch)
    {
        try
        {
            LocalProfile profile = NativeUIProbe.RewardProfile;
            bool offered = NativeRematchUI.Available && profile != null && profile.LastMatchId == expectedMatch && playedDeck != null
                && playedDeck == profile.LastMatchDeck && LocalLadder.RematchOffer(playedDeck) != null;
            log?.LogInfo("NATIVE_RESULTS_REMATCH offered=" + offered + ";match=" + expectedMatch
                + (offered ? "" : ";refusal=" + (LocalLadder.RematchRefusal ?? "not the last match")));
            return offered;
        }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_REMATCH " + error.Message); return false; }
    }

    // Done's own press (RewardBoxesDisplay.OnDoneButtonPressed): native fade and back handler, then CompleteAfterFade.
    private static void RematchFromBoxes(string expectedMatch, int expectedGeneration)
    {
        if (detailsPopup || matchId != expectedMatch || generation != expectedGeneration || phase != LocalRewardPhase.DoneReady
            || display == null || display.m_DoneButton == null || !CurrentReceipt(expectedMatch)) return;
        if (!RematchOffered(expectedMatch)) { RetireRematchButton(); return; }
        NativeRematchUI.Request(expectedMatch);
        try { display.m_DoneButton.TriggerRelease(); }
        catch { NativeRematchUI.Cancel("Done release failed"); throw; }
    }

    // The card's OK slot: CONTINUE plus a queued rematch. A gate that closed meanwhile still continues to the picker.
    private static void RematchAndContinue(string expectedMatch, Action continued)
    {
        if (RematchOffered(expectedMatch)) NativeRematchUI.Request(expectedMatch);
        continued();
    }

    // The column-th of count secondary buttons in one row centred under Done, in screen space (camera-independent axes);
    // a column beside Done when the row would leave the screen. Measured on Done at its native size, before it is enlarged
    // (colliders lag a frame behind a scale change). Height comes from Done's hit plate only: its renderers include glow FX.
    private static Vector3 UnderDone(Camera camera, NormalButton source, int column, int count)
    {
        Vector3 center = Plate(camera, source, out float width, out float height);
        Vector3 target = center;
        target.y -= height * LocalRewardPresentation.UnderDoneHeights;
        target.x += width * LocalRewardPresentation.UnderDoneColumn(column, count);
        if (target.y - height * 0.5f < Screen.height * 0.02f)
        {
            target = center;
            target.x += width * (LocalRewardPresentation.DoneScale * 0.5f + 0.6f);
            target.y -= height * 1.2f * (column - (count - 1) * 0.5f);
        }
        return camera.ScreenToWorldPoint(target);
    }

    // Down in screen space by LocalRewardPresentation.DoneDropHeights native plate heights (camera-independent axes).
    private static void DropDone(NormalButton done, Camera camera)
    {
        try
        {
            camera = camera != null ? camera : CameraUtils.FindFirstByLayerMask(1 << done.gameObject.layer);
            if (camera == null) return;
            Vector3 center = Plate(camera, done, out float _, out float height);
            done.transform.position = camera.ScreenToWorldPoint(center - new Vector3(0, height * LocalRewardPresentation.DoneDropHeights, 0));
        }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_DONE_DROP " + error.Message); }
    }

    // Done's screen centre and hit-plate size.
    private static Vector3 Plate(Camera camera, Component source, out float width, out float height)
    {
        Vector3 center = camera.WorldToScreenPoint(source.transform.position);
        var plates = source.GetComponentsInChildren<Collider>(true).Select(collider => collider.bounds).ToList();
        width = Screen.width * 0.12f; height = Screen.height * 0.06f;
        if (plates.Count > 0)
        {
            float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
            foreach (Bounds plate in plates)
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = camera.WorldToScreenPoint(new Vector3((corner & 1) == 0 ? plate.min.x : plate.max.x,
                        (corner & 2) == 0 ? plate.min.y : plate.max.y, (corner & 4) == 0 ? plate.min.z : plate.max.z));
                    left = Mathf.Min(left, point.x); right = Mathf.Max(right, point.x);
                    bottom = Mathf.Min(bottom, point.y); top = Mathf.Max(top, point.y);
                }
            width = Mathf.Clamp(right - left, Screen.width * 0.03f, Screen.width * 0.3f);
            height = Mathf.Clamp(top - bottom, Screen.height * 0.02f, Screen.height * 0.12f);
        }
        return center;
    }

    private static readonly Type UberTextType = GameTypes.ByName("UberText");

    // Done's own text component, shrunk to the plate: the long label must not hang off the cloned button.
    private static void FitLabel(NormalButton clone)
    {
        if (UberTextType != null)
            foreach (Component text in clone.GetComponentsInChildren(UberTextType, true))
            {
                PropertyInfo fit = AccessTools.Property(UberTextType, "ResizeToFit");
                if (fit != null && fit.CanWrite) fit.SetValue(text, true);
                PropertyInfo wrap = AccessTools.Property(UberTextType, "WordWrap");
                if (wrap != null && wrap.CanWrite) wrap.SetValue(text, false);
                // UberText.UpdateNow(bool updateIfInactive): invoking it with no arguments threw a parameter-count
                // mismatch and dropped the whole button (live 2026-10-25).
                AccessTools.Method(UberTextType, "UpdateNow", new[] { typeof(bool) })?.Invoke(text, new object[] { false });
            }
        // Legacy TextMesh label (UnityEngine.TextRenderingModule is not a compile reference): reached by reflection.
        var mesh = AccessTools.Field(typeof(NormalButton), "m_buttonText")?.GetValue(clone) as Component;
        PropertyInfo size = mesh == null ? null : AccessTools.Property(mesh.GetType(), "characterSize");
        Collider plate = clone.GetComponentsInChildren<Collider>(true).FirstOrDefault();
        Renderer rendered = mesh == null ? null : mesh.GetComponent<Renderer>();
        if (size != null && plate != null && rendered != null)
        {
            float textWidth = rendered.bounds.size.x, plateWidth = plate.bounds.size.x;
            if (textWidth > plateWidth * 0.85f && textWidth > 0)
                size.SetValue(mesh, (float)size.GetValue(mesh) * plateWidth * 0.85f / textWidth);
        }
    }

    // Retires both Done clones.
    private static void RetireDetailedButton()
    {
        NormalButton old = detailedButton;
        detailedButton = null;
        if (old != null) UnityEngine.Object.Destroy(old.gameObject);
        RetireRematchButton();
    }

    private static void RetireRematchButton()
    {
        NormalButton old = rematchButton;
        rematchButton = null;
        if (old != null) UnityEngine.Object.Destroy(old.gameObject);
    }

    internal static bool ShowTierFallback(LocalResultsReceipt receipt, Action continued)
    {
        var info = new AlertPopup.PopupInfo {
            m_id = "LOCAL_TIER_REWARD_FALLBACK_" + receipt.MatchId,
            m_headerText = "REWARDS SAVED",
            m_text = LocalRewardPresentation.TierRewardText(receipt.TierRewards) + "\n\nYour rewards are already in your profile.",
            m_showAlertIcon = false, m_responseDisplay = AlertPopup.ResponseDisplay.CANCEL,
            m_cancelText = "CONTINUE", m_keyboardEscIsCancel = false,
            m_responseCallback = (response, data) => { if (response == AlertPopup.Response.CANCEL) continued(); }
        };
        return DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(info);
    }

    private static List<RewardData> RewardItems(LocalRewardSummary summary, int rewardVersion)
    {
        var result = new List<RewardData>();
        foreach (LocalRewardItem item in summary.Items)
            result.Add(item.Kind == LocalRewardKind.Gold ? (RewardData)new GoldRewardData(item.Amount)
                : item.Kind == LocalRewardKind.Dust ? new ArcaneDustRewardData(item.Amount)
                : new BoosterPackRewardData(LocalRewardSafety.BoosterId(rewardVersion, item.PackType,
                    FairPackRules.NativeBoosterId, BoosterActorAvailable), item.Amount));
        return result;
    }

    private static bool BoosterActorAvailable(int boosterId)
    {
        var booster = GameDbf.Booster.GetRecord(boosterId);
        object loader = NativeUIProbe.RewardAssetLoader ?? throw new InvalidOperationException("Native asset loader is unavailable.");
        MethodInfo available = loader.GetType().GetMethods().Single(method => method.Name == "IsRuntimeAssetVariantAvailable"
            && method.GetParameters().Length == 2);
        ParameterInfo[] parameters = available.GetParameters();
        return booster != null && !string.IsNullOrEmpty(booster.ArenaPrefab)
            && (bool)available.Invoke(loader, new[] { AssetReference(parameters[0].ParameterType, booster.ArenaPrefab),
                Enum.ToObject(parameters[1].ParameterType, 0) });
    }

    private static void BeginAnimation(string expectedMatchId)
    {
        generation++;
        int requestedGeneration = generation;
        matchId = expectedMatchId;
        phase = LocalRewardPhase.LoadingRewardBoxes;
        prefabState = "loading-raw"; placementFacts = "hub"; placedCamera = null; placedScale = 1;
        actorFacts = "none";
        createdMask = openedMask = 0;
        loadBegan = Time.realtimeSinceStartup;
        try
        {
            object loader = NativeUIProbe.RewardAssetLoader ?? throw new InvalidOperationException("Native asset loader is unavailable.");
            MethodInfo load = loader.GetType().GetMethods().Single(method => method.Name == "LoadGameObject"
                && method.GetParameters().Length == 5);
            ParameterInfo[] parameters = load.GetParameters();
            object reference = AssetReference(parameters[0].ParameterType, RewardPrefab);
            Delegate callback = LoadCallback(parameters[1].ParameterType);
            bool accepted = (bool)load.Invoke(loader, new object[] {
                reference, callback, new LoadAttempt { MatchId = expectedMatchId, Generation = requestedGeneration }, false, true });
            if (!accepted && phase == LocalRewardPhase.LoadingRewardBoxes && generation == requestedGeneration)
                Fallback("Native reward asset request was rejected.");
        }
        catch (Exception error) { Fallback("Native reward asset load failed: " + error.Message); }
    }

    private static void Loaded(string expectedMatchId, int expectedGeneration, GameObject raw)
    {
        try
        {
            LocalMatchRecord pending = NativeUIProbe.RewardProfile?.PendingRewardPresentation;
            bool mayInstantiate = LocalRewardPresentation.CanInstantiate(expectedMatchId, expectedGeneration,
                matchId, generation, phase, pending?.Id, PresentContext,
                RewardBoxesDisplay.Get() != null, raw != null);
            if (!mayInstantiate) return;
            prefabState = "raw-loaded";
            GameObject instance = UnityEngine.Object.Instantiate(raw);
            RewardBoxesDisplay candidate = instance?.GetComponent<RewardBoxesDisplay>();
            try { Preflight(candidate); }
            catch { if (instance != null) UnityEngine.Object.Destroy(instance); throw; }
            display = candidate;
            candidate.SetRewards(rewards);
            candidate.m_playBoxFlyoutSound = false;
            MethodInfo setLayer = candidate.GetType().GetMethod("SetLayer", BindingFlags.Instance | BindingFlags.Public);
            Type layerType = setLayer.GetParameters()[0].ParameterType;
            setLayer.Invoke(candidate, new[] { Enum.ToObject(layerType, 19) });
            // The prefab is authored for the HUB box camera; in GAMEPLAY it must be framed for the end-game camera and
            // proven on screen, else the saved-rewards popup takes over (rewards are already saved).
            if (NativeResultsUI.PresentsInGameplay)
            {
                string offscreen = PlaceForCamera(candidate);
                if (offscreen != null) { Fallback("placement: " + offscreen); return; }
            }
            candidate.UseDarkeningClickCatcher(true);
            candidate.RegisterDoneCallback(() => CompleteAfterFade(expectedMatchId, expectedGeneration, candidate));
            phase = LocalRewardPhase.Revealing;
            revealBegan = Time.realtimeSinceStartup; allOpenedAt = -1; reachableMask = 0; nextWatch = 0; coveredBy = "";
            prefabState = "instantiated-owned";
            candidate.AnimateRewards();
            if (placedCamera != null) ScaleWorldDrift(candidate);
            int complete = (1 << rewards.Count) - 1;
            if (createdMask != complete) throw new InvalidOperationException("Native reward actors were not created exactly.");
        }
        catch (Exception error) { Fallback("Native reward animation could not start: " + error.Message); }
    }

    private static LocalVec V(Vector3 v) => new LocalVec(v.x, v.y, v.z);
    private static Vector3 U(LocalVec v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

    // Rigid move + uniform scale of the whole display before AnimateRewards (boxes fly to their bones' world
    // positions then, and reward actors attach under m_Root keeping local transforms). Math: LocalRewardPlacement.
    // Returns null when box targets, start bones and Done project on screen; else why not (the caller falls back).
    private static string PlaceForCamera(RewardBoxesDisplay candidate)
    {
        Camera camera = RewardCamera(out string candidates)
            ?? throw new InvalidOperationException("End-game reward camera unavailable (" + candidates + ").");
        Transform box = candidate.GetPackageData(1)?.FirstOrDefault()?.m_TargetBone
            ?? throw new InvalidOperationException("Native reward prefab has no single-box layout.");
        Transform view = camera.transform;
        float near = camera.nearClipPlane, far = camera.farClipPlane;
        // Depth of the native end-game screen, which this camera already frames; else a safe slice of the frustum.
        EndGameScreen end = EndGameScreen.Get();
        float depth = end != null ? Vector3.Dot(end.transform.position - view.position, view.forward) : 0;
        if (!(depth > near * 1.5f) || !(depth < far * 0.9f)) depth = near + (far - near) * 0.1f;
        double half = camera.orthographic ? camera.orthographicSize : depth * Math.Tan(camera.fieldOfView * Math.PI / 360);
        Transform root = candidate.transform;
        LocalRewardPlacement placement = LocalRewardPlacement.Fit(new[] { V(box.position) }, V(candidate.m_DoneButton.transform.position),
            V(root.position), V(view.position), V(view.right), V(view.up), V(view.forward), depth, half);
        root.position = U(placement.Position);
        root.rotation = new Quaternion((float)placement.Qx, (float)placement.Qy, (float)placement.Qz, (float)placement.Qw) * root.rotation;
        root.localScale = root.localScale * (float)placement.Scale;
        placedScale = placement.Scale;
        placedCamera = camera;
        EnsureInputCamera(camera);
        string offscreen = PlacedOffScreen(candidate, camera);
        placementFacts = "camera:" + camera.name + ":mask=0x" + camera.cullingMask.ToString("X") + ":depth=" + depth.ToString("G4")
            + ":scale=" + placement.Scale.ToString("G4") + ":viewport=" + (offscreen ?? "ok") + ":cameras=" + candidates;
        if (offscreen == null)
            log?.LogInfo("NATIVE_RESULTS_BOXES_PLACED placement=" + placementFacts + " ortho=" + camera.orthographic + " position=" + root.position);
        return offscreen;
    }

    // The package mesh (and reward actors) bob via native floatyObj/floatyObj2, which add a WORLD-space vector in HUB
    // units every frame and so ignore the display's placement scale: on the board camera the box drifted far off its
    // glow and collider (Deck/PC live 2026-10-29). Scale their magnitude with the layout, after AnimateRewards cloned
    // the packages and created the reward actors (before their first Update).
    private static void ScaleWorldDrift(RewardBoxesDisplay owner)
    {
        foreach (floatyObj2 drift in owner.GetComponentsInChildren<floatyObj2>(true))
            drift.magnitude = LocalRewardPlacement.WorldDrift(drift.magnitude, placedScale);
        foreach (floatyObj drift in owner.GetComponentsInChildren<floatyObj>(true))
            drift.magnitude = LocalRewardPlacement.WorldDrift(drift.magnitude, placedScale);
    }

    // The end-game layer's own camera: the most dedicated enabled layer-19 camera (a board camera with an Everything
    // mask loses), not merely the first match. The candidates go to the placement diagnostic.
    private static Camera RewardCamera(out string candidates)
    {
        Camera[] cameras = Camera.allCameras;
        candidates = string.Join(",", cameras.Where(camera => camera != null && (camera.cullingMask & 1 << 19) != 0)
            .Select(camera => camera.name + "/0x" + camera.cullingMask.ToString("X") + "/" + camera.depth.ToString("G3")).ToArray());
        int pick = LocalRewardSafety.PickCamera(cameras.Select(camera => new LocalCameraFacts {
            CullingMask = camera != null ? camera.cullingMask : 0, Depth = camera != null ? camera.depth : 0,
            Enabled = camera != null && camera.isActiveAndEnabled, RendersToTexture = camera != null && camera.targetTexture != null }).ToList(), 19);
        return pick >= 0 ? cameras[pick] : CameraUtils.FindFirstByLayerMask(1 << 19);
    }

    // Box clicks raycast through PegUI's input cameras: the framing camera must be one (NativeResultsUI registers the
    // first layer-19 match only). Balanced by ReleaseInputCamera.
    private static void EnsureInputCamera(Camera camera)
    {
        PegUI peg = PegUI.Get();
        if (peg == null || camera == null || ReferenceEquals(camera, inputAdded)) return;
        ReleaseInputCamera();
        var registered = AccessTools.Field(typeof(PegUI), "m_UICams")?.GetValue(peg) as System.Collections.IList;
        if (registered == null || registered.Contains(camera)) return;
        peg.AddInputCamera(camera);
        inputAdded = camera;
    }

    private static void ReleaseInputCamera()
    {
        Camera added = inputAdded;
        inputAdded = null;
        try { if (added != null && PegUI.Get() != null) PegUI.Get().RemoveInputCamera(added); }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_INPUT_CAMERA " + error.Message); }
    }

    private static LocalViewportPoint Point(Camera camera, string name, Vector3 world, bool loose = false)
    {
        Vector3 viewport = camera.WorldToViewportPoint(world);
        return new LocalViewportPoint(name, viewport.x, viewport.y, viewport.z, loose);
    }

    // Before AnimateRewards: every box target, its start bone and where Done will appear (AllDone moves it to the
    // targets' centre when there is more than one box).
    private static string PlacedOffScreen(RewardBoxesDisplay owner, Camera camera)
    {
        List<RewardBoxesDisplay.RewardPackageData> layout = owner.GetPackageData(rewards.Count);
        var points = new List<LocalViewportPoint>();
        Vector3 centre = Vector3.zero;
        for (int index = 0; index < layout.Count; index++)
        {
            points.Add(Point(camera, "box" + index, layout[index].m_TargetBone.position));
            points.Add(Point(camera, "start" + index, layout[index].m_StartBone.position, true));
            centre += layout[index].m_TargetBone.position;
        }
        points.Add(Point(camera, "done", layout.Count > 1 ? centre / layout.Count : owner.m_DoneButton.transform.position));
        return LocalRewardSafety.OffScreen(points, camera.nearClipPlane);
    }

    // Done shown: the revealed rewards and Done where the native display actually put them.
    private static string RevealedOffScreen(RewardBoxesDisplay owner, Camera camera)
    {
        var points = new List<LocalViewportPoint> { Point(camera, "done", owner.m_DoneButton.transform.position) };
        if (RewardObjectsField?.GetValue(owner) is GameObject[] objects)
            for (int index = 0; index < objects.Length; index++)
                if (objects[index] != null) points.Add(Point(camera, "reward" + index, objects[index].transform.position));
        return LocalRewardSafety.OffScreen(points, camera.nearClipPlane);
    }

    // Same rule as a real click and the test-control ui tree: live, on screen, and PegUI's own pick lands on it.
    private static bool Clickable(GameObject target, string name)
    {
        List<Collider> plates = target != null && target.activeInHierarchy
            ? target.GetComponentsInChildren<Collider>(false).Where(collider => collider.enabled).ToList() : new List<Collider>();
        if (plates.Count == 0) return LocalRewardSafety.Clickable(false, null, null);
        Camera camera = placedCamera;
        string offscreen = camera == null ? null
            : LocalRewardSafety.OffScreen(new[] { Point(camera, name, target.transform.position) }, camera.nearClipPlane);
        if (offscreen != null) return LocalRewardSafety.Clickable(true, offscreen, null);
        List<LocalHitProbe> probes = Probe(target, plates, camera ?? CameraUtils.FindFirstByLayerMask(1 << target.layer), out string cover);
        bool clickable = LocalRewardSafety.Clickable(true, null, probes);
        if (!clickable) coveredBy = name + ":" + (cover ?? "nothing");
        return clickable;
    }

    private static readonly FieldInfo MousePositionField = AccessTools.Field(typeof(UniversalInputManager), "m_mousePosition");
    private static string coveredBy = "";

    // PegUI.FindHitElement at each hit plate's centre, the mouse position restored after (as NativeUiTree.HitTest).
    private static List<LocalHitProbe> Probe(GameObject target, List<Collider> plates, Camera camera, out string cover)
    {
        cover = null;
        var probes = new List<LocalHitProbe>();
        PegUI peg = PegUI.Get(); UniversalInputManager input = UniversalInputManager.Get();
        if (peg == null || input == null || camera == null || MousePositionField == null || input.IsTouchMode()) return probes;
        object mouse = MousePositionField.GetValue(input);
        try
        {
            foreach (Collider plate in plates)
            {
                Vector3 screen = camera.WorldToScreenPoint(plate.bounds.center);
                if (!(screen.z > 0)) { probes.Add(LocalHitProbe.Nothing); continue; }
                MousePositionField.SetValue(input, new Vector3(screen.x, screen.y, 0));
                PegUIElement hit = peg.FindHitElement();
                if (hit != null && hit.transform.IsChildOf(target.transform)) { probes.Add(LocalHitProbe.Target); break; }
                probes.Add(hit == null ? LocalHitProbe.Nothing : LocalHitProbe.Covered);
                if (cover == null && hit != null) cover = hit.GetType().Name + ":" + hit.gameObject.name;
            }
        }
        finally { MousePositionField.SetValue(input, mouse); }
        return probes;
    }

    // EndGameScreen.Show re-activates its full-screen "Click to continue" hitbox (rank uses it). While this presentation
    // owns the finished GAMEPLAY screen that hitbox would win PegUI's pick over the boxes and Done for real mouse and
    // test control alike (live 2026-10-25), so it is switched off and its prompt hidden. The hitbox comes back once the
    // presentation ends; the prompt stays hidden (nothing continues through it after the rewards).
    private static EndGameScreen guardedScreen;
    private static bool restoreHitbox;
    private static readonly FieldInfo ContinueTextField = AccessTools.Field(typeof(EndGameScreen), "m_continueText");

    private static void GuardEndGameInput()
    {
        try
        {
            EndGameScreen end = EndGameScreen.Get();
            bool own = phase != LocalRewardPhase.Idle && end != null && NativeResultsUI.OwnsScreen(end);
            if (!own)
            {
                EndGameScreen guarded = guardedScreen;
                bool restore = restoreHitbox;
                guardedScreen = null; restoreHitbox = false;
                if (restore && guarded != null && guarded.m_hitbox != null) guarded.m_hitbox.gameObject.SetActive(true);
                return;
            }
            if (!ReferenceEquals(guardedScreen, end)) { guardedScreen = end; restoreHitbox = false; }
            if (end.m_hitbox != null && end.m_hitbox.gameObject.activeSelf)
            {
                end.m_hitbox.gameObject.SetActive(false);
                restoreHitbox = true;
            }
            if (ContinueTextField?.GetValue(end) is Component prompt && prompt != null && prompt.gameObject.activeSelf)
                prompt.gameObject.SetActive(false);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_ENDGAME_INPUT " + error.Message); }
    }

    // The compact card's DETAILED RESULTS is not a cancel: its red X is hidden (CONTINUE keeps the confirm check).
    private static void StyleNoItemsCard()
    {
        try
        {
            DialogManager dialogs = DialogManager.Get();
            if (dialogs == null || !(CurrentDialogField?.GetValue(dialogs) is AlertPopup alert) || alert == null || alert.m_cancelButton == null) return;
            string id = alert.GetInfo()?.m_id;
            if (id == null || !id.StartsWith(LocalRewardPresentation.NoItemsPopupPrefix, StringComparison.Ordinal)) return;
            Transform cancel = alert.m_cancelButton.transform;
            foreach (GameObject icon in alert.m_buttonIconsSet1.Concat(alert.m_buttonIconsSet2))
                if (icon != null && icon.activeSelf && icon.transform.IsChildOf(cancel)) icon.SetActive(false);
            if (cardFailed || cardStyled && (cardConceded || cardDetails != null || cardDetailsFailed)) return;
            if (!cardStyled)
            {
                try { LayoutCard(alert); cardStyled = true; cardStyledAt = Time.realtimeSinceStartup; }
                catch (Exception error)
                {
                    // Once per card: it falls back to the native CONTINUE + DETAILS row (OK then just continues).
                    cardFailed = true; cardRematch = false;
                    RetireCardButtons(alert);
                    log?.LogWarning("NATIVE_RESULTS_CARD_LAYOUT " + error.Message);
                    return;
                }
            }
            // Placed once the card stopped scaling in (positions measured mid-punch put it behind the card: live); without a
            // readable animation state, after a short wait.
            string shown = ShowAnimStateField?.GetValue(alert)?.ToString();
            if (cardConceded || (shown != null && shown != "FINISHED" && Time.realtimeSinceStartup - cardStyledAt < 2f)) return;
            try { PlaceCardDetails(alert); }
            catch (Exception error)
            {
                // Only DETAILS falls back (the native one, back in the row); REMATCH stays.
                cardDetailsFailed = true;
                if (cardDetails != null) UnityEngine.Object.Destroy(cardDetails.gameObject);
                cardDetails = null;
                alert.m_cancelButton.gameObject.SetActive(true);
                alert.m_buttonContainer?.UpdateSlices();
                log?.LogWarning("NATIVE_RESULTS_CARD_DETAILS " + error.Message);
            }
        }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_CARD_STYLE " + error.Message); }
    }

    // Owner 2026-10-28: CONTINUE and a red REMATCH (a CONTINUE copy, check icon hidden, answering the popup's unused OK) share
    // the row; DETAILS moves under the card as a grey copy answering CANCEL, the native one staying hidden in the row (an
    // inactive slice is skipped). Two buttons keep the card's native two-button width. Popups are destroyed on hide, so
    // nothing here leaks into a later dialog.
    private static void LayoutCard(AlertPopup alert)
    {
        UIBButton confirm = alert.m_confirmButton, cancel = alert.m_cancelButton;
        MultiSliceElement row = alert.m_buttonContainer;
        if (confirm == null || cancel == null || row == null || !confirm.gameObject.activeSelf)
            throw new InvalidOperationException("Native card buttons unavailable.");
        int index = row.m_slices.FindIndex(slice => slice.m_slice == confirm.gameObject);
        if (index < 0) throw new InvalidOperationException("CONTINUE is not a slice of the card's button row.");
        if (cardRematch)
        {
            cardButton = UnityEngine.Object.Instantiate(confirm, confirm.transform.parent);
            cardButton.name = "LocalRematchCardButton";
            cardButton.ClearEventListeners();
            foreach (GameObject icon in alert.m_buttonIconsSet1.Concat(alert.m_buttonIconsSet2))
            {
                if (icon == null || !icon.transform.IsChildOf(confirm.transform) || icon.transform == confirm.transform) continue;
                Transform copy = cardButton.transform.Find(RelativePath(icon.transform, confirm.transform));
                if (copy != null) copy.gameObject.SetActive(false);
            }
            cardButton.SetText(LocalRewardPresentation.RematchLabel);
            NativeButtonTint.Apply(cardButton, NativeButtonTint.Tint.Red);
            cardButton.AddEventListener(UIEventType.RELEASE, _ => Answer(alert, AlertPopup.Response.OK));
            var source = row.m_slices[index];
            row.AddSlice(cardButton.gameObject, source.m_minLocalPadding, source.m_maxLocalPadding, source.m_reverse, source.m_useMeshRenderers,
                source.m_useWidgetTransform, source.m_useTextSize, source.m_includeAllChildren);
            var added = row.m_slices[row.m_slices.Count - 1];
            row.m_slices.RemoveAt(row.m_slices.Count - 1);
            row.m_slices.Insert(index + 1, added);
        }
        // After the player's own concede the card stays CONTINUE (+ REMATCH): no breakdown (owner 2026-10-28).
        alert.m_cancelButton.gameObject.SetActive(false);
        row.UpdateSlices();
        log?.LogInfo("NATIVE_RESULTS_CARD_LAYOUT rematch=" + (cardButton != null) + ";conceded=" + cardConceded);
    }

    // Grey DETAILS copy (answers CANCEL) centred under the row, one button height plus a gap below it. The X icon is already
    // hidden on the native DETAILS (StyleNoItemsCard), so the copy has none.
    private static void PlaceCardDetails(AlertPopup alert)
    {
        UIBButton confirm = alert.m_confirmButton, cancel = alert.m_cancelButton;
        Camera camera = CameraUtils.FindFirstByLayerMask(1 << confirm.gameObject.layer)
            ?? throw new InvalidOperationException("Card camera unavailable.");
        cardDetails = UnityEngine.Object.Instantiate(cancel, cancel.transform.parent);
        cardDetails.name = "LocalDetailsCardButton";
        cardDetails.ClearEventListeners();
        cardDetails.gameObject.SetActive(true);
        NativeButtonTint.Apply(cardDetails, NativeButtonTint.Tint.Grey);
        cardDetails.AddEventListener(UIEventType.RELEASE, _ => Answer(alert, AlertPopup.Response.CANCEL));
        Rect left = ScreenRect(camera, confirm), right = cardButton != null ? ScreenRect(camera, cardButton) : left;
        Vector3 target = new Vector3((left.center.x + right.center.x) * 0.5f, left.center.y - left.height * LocalRewardPresentation.CardDetailsGapHeights,
            camera.WorldToScreenPoint(confirm.transform.position).z);
        // Move by the screen offset between the copy's own visual centre and the target (its pivot need not be its centre).
        Rect own = ScreenRect(camera, cardDetails);
        Vector3 from = new Vector3(own.center.x, own.center.y, target.z);
        cardDetails.transform.position += camera.ScreenToWorldPoint(target) - camera.ScreenToWorldPoint(from);
        log?.LogInfo("NATIVE_RESULTS_CARD_DETAILS screen=" + ScreenRect(camera, cardDetails) + ";continue=" + left);
    }

    // On-screen rectangle of a button's enabled colliders.
    private static Rect ScreenRect(Camera camera, Component button)
    {
        bool any = false; float minX = 0, maxX = 0, minY = 0, maxY = 0;
        foreach (Collider collider in button.GetComponentsInChildren<Collider>(false))
        {
            if (!collider.enabled) continue;
            Bounds b = collider.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 p = camera.WorldToScreenPoint(new Vector3((corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y, (corner & 4) == 0 ? b.min.z : b.max.z));
                if (!any) { minX = maxX = p.x; minY = maxY = p.y; any = true; }
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
        }
        if (!any) throw new InvalidOperationException(button.name + " has no on-screen collider.");
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private static void RetireCardButtons(AlertPopup alert)
    {
        foreach (UIBButton old in new[] { cardButton, cardDetails })
        {
            if (old == null) continue;
            alert.m_buttonContainer?.m_slices.RemoveAll(slice => slice.m_slice == old.gameObject);
            UnityEngine.Object.Destroy(old.gameObject);
        }
        cardButton = cardDetails = null;
        if (alert.m_cancelButton != null) alert.m_cancelButton.gameObject.SetActive(true);
        alert.m_buttonContainer?.UpdateSlices();
    }

    private static readonly MethodInfo AlertButtonPress = AccessTools.Method(typeof(AlertPopup), "ButtonPress");

    // AlertPopup.ButtonPress: the popup's own once-only answer, callback and hide.
    private static void Answer(AlertPopup alert, AlertPopup.Response response)
    {
        try { if (alert != null) AlertButtonPress.Invoke(alert, new object[] { response }); }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_CARD_ANSWER " + response + " " + error.Message); }
    }

    private static string RelativePath(Transform child, Transform root)
    {
        string path = child.name;
        for (Transform at = child.parent; at != null && at != root; at = at.parent) path = at.name + "/" + path;
        return path;
    }

    // Reveal watchdog: boxes must become clickable on screen, Done must follow the last opened box, and stay clickable.
    private static void Watch()
    {
        float now = Time.realtimeSinceStartup;
        if (display == null || now < nextWatch) return;
        nextWatch = now + 0.25f;
        coveredBy = "";
        int complete = (1 << rewards.Count) - 1;
        bool doneClickable = false;
        try
        {
            if (phase == LocalRewardPhase.Revealing)
            {
                if (PackageInstancesField?.GetValue(display) is List<GameObject> boxes)
                    for (int index = 0; index < boxes.Count && index < rewards.Count; index++)
                        if ((reachableMask & 1 << index) == 0 && Clickable(boxes[index], "box" + index)) reachableMask |= 1 << index;
                if (openedMask == complete && allOpenedAt < 0) allOpenedAt = now;
            }
            else doneClickable = Clickable(display.m_DoneButton != null ? display.m_DoneButton.gameObject : null, "done");
        }
        catch (Exception error) { Fallback("watchdog: " + error.Message); return; }
        string reason = LocalRewardSafety.Watchdog(phase, now - revealBegan, ((reachableMask | openedMask) & complete) == complete,
            allOpenedAt < 0 ? -1 : now - allOpenedAt, now - doneReadyAt, doneClickable, detailsPopup);
        if (reason != null) Fallback("watchdog: " + reason + (coveredBy.Length == 0 ? "" : " (hit " + coveredBy + ")"));
    }

    private static void Preflight(RewardBoxesDisplay candidate)
    {
        if (rewards.OfType<ArcaneDustRewardData>().Any() && candidate?.m_RewardSet?.m_RewardDust == null)
            throw new InvalidOperationException("Native dust reward actor is unavailable.");
        if (candidate == null || RewardBoxesDisplay.Get() != candidate || candidate.m_Root == null
            || candidate.m_ClickCatcher == null || candidate.m_DoneButton == null || candidate.m_DoneButton.m_button == null
            || candidate.m_RewardSet == null
            || candidate.m_RewardSet.m_RewardGold == null || candidate.m_RewardSet.m_RewardPackage == null
            || candidate.m_RewardSet.m_RewardData == null || FullScreenFXMgr.Get() == null || NarrativeManager.Get() == null)
            throw new InvalidOperationException("Native reward prefab failed required component preflight.");
        List<RewardBoxesDisplay.RewardPackageData> layout = candidate.GetPackageData(rewards.Count);
        if (layout == null || layout.Count != rewards.Count
            || layout.Any(entry => entry == null || entry.m_StartBone == null || entry.m_TargetBone == null))
            throw new InvalidOperationException("Native reward prefab has no exact reward layout.");
        foreach (int boosterId in rewards.OfType<BoosterPackRewardData>().Select(pack => pack.Id).Distinct())
            if (!BoosterActorAvailable(boosterId))
                throw new InvalidOperationException("Native booster reward actor is unavailable.");
    }

    private static object AssetReference(Type type, string value)
    {
        MethodInfo conversion = type.GetMethod("op_Implicit", BindingFlags.Static | BindingFlags.Public,
            null, new[] { typeof(string) }, null);
        if (conversion == null || conversion.ReturnType != type)
            throw new MissingMethodException(type.FullName, "op_Implicit(String)");
        return conversion.Invoke(null, new object[] { value });
    }

    private static Delegate LoadCallback(Type callbackType)
    {
        MethodInfo invoke = callbackType.GetMethod("Invoke");
        ParameterExpression[] args = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        MethodInfo bridge = typeof(NativeRewardUI).GetMethod(nameof(LoadedBridge), BindingFlags.Static | BindingFlags.NonPublic);
        MethodCallExpression call = Expression.Call(bridge, Expression.Convert(args[1], typeof(GameObject)), Expression.Convert(args[2], typeof(object)));
        return Expression.Lambda(callbackType, call, args).Compile();
    }

    internal static void LoadRaw(string asset, Action<GameObject> callback)
    {
        object loader = NativeUIProbe.RewardAssetLoader ?? throw new InvalidOperationException("Native asset loader is unavailable.");
        MethodInfo load = loader.GetType().GetMethods().Single(method => method.Name == "LoadGameObject" && method.GetParameters().Length == 5);
        ParameterInfo[] parameters = load.GetParameters();
        bool accepted = (bool)load.Invoke(loader, new object[] { AssetReference(parameters[0].ParameterType, asset),
            LoadCallback(parameters[1].ParameterType), new LoadAttempt { Callback = callback }, false, true });
        if (!accepted) throw new InvalidOperationException("Native asset request rejected: " + asset);
    }

    private static void LoadedBridge(GameObject raw, object callbackData)
    {
        var attempt = callbackData as LoadAttempt;
        if (attempt?.Callback != null) attempt.Callback(raw);
        else if (attempt != null) Loaded(attempt.MatchId, attempt.Generation, raw);
    }

    private static void ObserveRewardActor(RewardBoxesDisplay __instance, RewardData __0, int __1, ref GameObject __result)
    {
        if (__instance != display || phase != LocalRewardPhase.Revealing) return;
        try
        {
            if (__1 < 0 || __1 >= rewards.Count || !ReferenceEquals(__0, rewards[__1]) || __result == null
                || !SameReward(rewards[__1], __0))
                throw new InvalidOperationException("Native reward actor did not match requested item " + __1 + ".");
            createdMask |= 1 << __1;
            actorFacts = actorFacts == "none" ? Actor(__1, __0, __result) : actorFacts + "," + Actor(__1, __0, __result);
        }
        catch (Exception error) { Fallback(error.Message); }
    }

    private static void ObserveRewardOpened(RewardBoxesDisplay __instance, int __0, ref GameObject[] ___m_RewardObjects)
    {
        if (__instance != display || (phase != LocalRewardPhase.Revealing && phase != LocalRewardPhase.DoneReady)) return;
        if (__0 >= 0 && __0 < rewards.Count && ___m_RewardObjects != null && __0 < ___m_RewardObjects.Length
            && ___m_RewardObjects[__0] != null && ___m_RewardObjects[__0].activeSelf
            && (createdMask & 1 << __0) != 0)
            openedMask |= 1 << __0;
    }

    private static void ObserveRewardDoneReady(RewardBoxesDisplay __instance)
    {
        if (__instance != display || phase != LocalRewardPhase.Revealing) return;
        int complete = (1 << rewards.Count) - 1;
        if (createdMask == complete && openedMask == complete)
        {
            phase = LocalRewardPhase.DoneReady;
            doneReadyAt = Time.realtimeSinceStartup;
            Camera camera = placedCamera;
            string offscreen = camera == null ? null : RevealedOffScreen(__instance, camera);
            if (offscreen != null) { placementFacts += ":revealed=" + offscreen; Fallback("revealed: " + offscreen); return; }
            // Owner 2026-10-28: Done much bigger as the main action. Grown around its centre its top edge crowded the reward
            // above it (live), so it first drops so that edge keeps the native gap plus a margin.
            DropDone(__instance.m_DoneButton, camera);
            // One row under Done: Rematch (when offered) then Details, both cloned and placed at Done's native size.
            bool rematch = RematchOffered(matchId);
            if (rematch) AttachRematchButton(__instance);
            AttachDetailedButton(__instance, rematchButton != null ? 1 : 0, rematchButton != null ? 2 : 1);
            __instance.m_DoneButton.transform.localScale *= LocalRewardPresentation.DoneScale;
        }
        else Fallback("Native Done appeared before every requested reward opened.");
    }

    private static bool BeforeRewardNavigateBack(RewardBoxesDisplay __instance, ref bool __result)
    {
        if (__instance != display) return true;
        if (phase == LocalRewardPhase.Closing) { __result = true; return false; }
        int complete = (1 << rewards.Count) - 1;
        if (phase != LocalRewardPhase.DoneReady || openedMask != complete) return true;
        phase = LocalRewardPhase.Closing;
        RetireDetailedButton();
        Required(typeof(RewardBoxesDisplay), "FadeFullscreenEffectsOut").Invoke(__instance, null);
        return true;
    }

    private static void BeforeRewardDestroy(RewardBoxesDisplay __instance)
    {
        if (__instance != display) return;
        bool unexpected = LocalRewardPresentation.UnexpectedDestroy(true, phase);
        display = null;
        if (unexpected) Fallback("Native reward display closed before completion.", false);
        else if (phase == LocalRewardPhase.Closing && CurrentReceipt(matchId))
        {
            generation++;
            phase = LocalRewardPhase.Idle;
        }
    }

    private static bool SuppressOwnedArenaNarrative(ref bool __result)
    {
        if (!LocalRewardPresentation.SuppressArenaNarrative(display != null, RewardBoxesDisplay.Get() == display, phase)) return true;
        narrativeSuppressions++;
        __result = false;
        return false;
    }

    private static bool SuppressLocalRankedIntro(ref bool __result)
    {
        bool suppress = LocalRewardPresentation.SuppressRankedIntro(NativeUIProbe.IsolatedLaunch, SceneMgr.IsInitialized(),
            SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.TOURNAMENT);
        if (!suppress) return true;
        __result = false;
        return false;
    }

    private static void ObserveRankedIntro(DialogManager.DialogRequest __0, ref bool __result)
    {
        if (!(__0?.m_info is RankedIntroPopup.RankedIntroPopupInfo)) return;
        log.LogInfo("NATIVE_RANKED_INTRO queued=" + __result + ";active=" + NativeUIProbe.Active
            + ";scene=" + (SceneMgr.IsInitialized() ? SceneMgr.Get().GetMode().ToString() : "none")
            + Environment.NewLine + Environment.StackTrace);
    }

    private static void CompleteAfterFade(string expectedMatchId, int expectedGeneration, RewardBoxesDisplay expectedDisplay)
    {
        int complete = (1 << rewards.Count) - 1;
        if (!LocalRewardPresentation.CanAcknowledge(expectedMatchId, expectedGeneration, matchId, generation,
            phase, expectedDisplay == display, openedMask, rewards.Count)) return;
        display = null;
        phase = LocalRewardPhase.Acknowledging;
        ReleaseInputCamera(); placedCamera = null;
        UnityEngine.Object.Destroy(expectedDisplay.gameObject);
        NativeResultsUI.BoxesCompleted();
        AcknowledgeAndRoute(expectedMatchId);
    }

    private static void AcknowledgeAndRoute(string expectedMatchId)
    {
        if (!NativeResultsUI.CanAcknowledge(expectedMatchId))
        {
            // Nothing is up any more: the HUB gate no longer depends on phase, and a HUB with no receipt resets it.
            log?.LogWarning("NATIVE_REWARD_ACK_SKIPPED match=" + expectedMatchId + ";phase=" + phase + ";results=" + NativeResultsUI.Diagnostics);
            return;
        }
        try
        {
            bool acknowledged = NativeUIProjection.AcknowledgeAndRefreshCore(NativeUIProbe.RewardProfile,
                expectedMatchId, NativeUIProbe.RefreshCoreProgression, out Exception refreshError);
            ackState = acknowledged ? "committed" : "already-committed";
            if (refreshError != null)
            {
                fallbackReason = "Core ownership refresh failed: " + refreshError.Message;
                log.LogError("NATIVE_CORE_REFRESH " + refreshError);
            }
        }
        catch (Exception error)
        {
            ackState = "failed:" + error.Message;
            ackRetryReason = error.Message;
            phase = LocalRewardPhase.SaveError;
            ShowAckRetry(expectedMatchId, error.Message);
            return;
        }
        NativeResultsUI.Acknowledged();
        phase = LocalRewardPhase.RouteChoice;
        FinishRoute(expectedMatchId);
    }

    // Retail chaining: acknowledged rewards land on the deck picker with the played deck selected; no route popup.
    private static void FinishRoute(string expectedMatch)
    {
        try { NativeUIProbe.RememberPlayedDeck(playedDeck); }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_PLAYED_DECK " + error.Message); }
        LocalLadder.RewardPresentationComplete();
        // A dropped request is retried by NativeUIProbe's finished-match route while GAMEPLAY remains.
        NativeUIProbe.Navigate(SceneMgr.Mode.TOURNAMENT);
        NativeResultsUI.RouteCompleted(expectedMatch);
        ResetFlow();
    }

    private static void Fallback(string reason, bool destroy = true)
    {
        if (phase == LocalRewardPhase.Idle || phase == LocalRewardPhase.RouteChoice || phase == LocalRewardPhase.Acknowledging) return;
        generation++;
        fallbackReason = reason;
        detailsPopup = false;
        RetireDetailedButton();
        RewardBoxesDisplay old = display;
        display = null;
        ReleaseInputCamera(); placedCamera = null;
        phase = LocalRewardPhase.NativeFallback;
        prefabState = "fallback:" + reason;
        if (old != null) ForgetNavigation(old);
        if (destroy && old != null) UnityEngine.Object.Destroy(old.gameObject);
        ShowFallbackPopup();
        log?.LogWarning("NATIVE_REWARD_FALLBACK fallback=" + reason + ";placement=" + placementFacts);
    }

    // Once Done showed, the display pushed its OnNavigateBack: a destroyed owner must not stay on the back stack.
    private static void ForgetNavigation(RewardBoxesDisplay owner)
    {
        try
        {
            Navigation.RemoveHandler((Navigation.NavigateBackHandler)Delegate.CreateDelegate(typeof(Navigation.NavigateBackHandler),
                owner, Required(typeof(RewardBoxesDisplay), "OnNavigateBack")));
        }
        catch (Exception error) { log?.LogWarning("NATIVE_REWARD_NAVIGATION " + error.Message); }
    }

    private static void ShowFallbackPopup()
    {
        if (fallbackPopup || !CurrentReceipt(matchId)) return;
        string expectedMatch = matchId; int expectedGeneration = generation;
        string exact = summaryBody + "\n\n"
            + "The reward animation could not load. Your rewards are already in your profile.";
        var info = new AlertPopup.PopupInfo {
            m_id = LocalRewardSafety.FallbackPopupPrefix + matchId + "_" + generation,
            m_headerText = "REWARDS SAVED", m_text = exact, m_showAlertIcon = false,
            m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
            m_confirmText = "TRY ANIMATION AGAIN", m_cancelText = "CONTINUE", m_keyboardEscIsCancel = false,
            m_responseCallback = (response, data) => {
                if (matchId != expectedMatch || generation != expectedGeneration || phase != LocalRewardPhase.NativeFallback) return;
                fallbackPopup = false;
                if (!CurrentReceipt(matchId)) return;
                if (response == AlertPopup.Response.CONFIRM) BeginAnimation(matchId);
                else { NativeResultsUI.BoxesCompleted(); AcknowledgeAndRoute(matchId); }
            }
        };
        fallbackPopup = DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(info);
    }

    private static void ShowAckRetry(string expectedMatchId, string reason)
    {
        if (savePopup) return;
        int expectedGeneration = generation;
        var info = new AlertPopup.PopupInfo {
            m_id = LocalRewardSafety.AckRetryPopupPrefix + expectedMatchId,
            m_headerText = "REWARD SAVE RETRY",
            m_text = "The reward receipt is still pending. Retry the save before continuing.\n\n" + reason,
            m_showAlertIcon = false, m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM,
            m_confirmText = "RETRY SAVE", m_keyboardEscIsCancel = false,
            m_responseCallback = (response, data) => {
                if (matchId != expectedMatchId || generation != expectedGeneration || phase != LocalRewardPhase.SaveError) return;
                savePopup = false; AcknowledgeAndRoute(expectedMatchId);
            }
        };
        savePopup = DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(info);
    }

    private static void ShowSaveRetry(string reason)
    {
        if (savePopup) return;
        var info = new AlertPopup.PopupInfo {
            m_id = LocalRewardSafety.SettleRetryPopupId,
            m_headerText = "RESULT NOT SAVED",
            m_text = reason + "\n\nRetry uses the same match and reward facts.",
            m_showAlertIcon = false, m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM,
            m_confirmText = "RETRY SAVE", m_keyboardEscIsCancel = false,
            m_responseCallback = (response, data) => { savePopup = false; LocalLadder.RetryRewardSave(); }
        };
        savePopup = DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(info);
    }

    private static bool CurrentReceipt(string expected) => NativeUIProbe.RewardProfile?.PendingRewardPresentation?.Id == expected;
    private static bool SameReward(RewardData expected, RewardData actual) =>
        expected is GoldRewardData eg && actual is GoldRewardData ag && eg.Amount == ag.Amount
        || expected is ArcaneDustRewardData ed && actual is ArcaneDustRewardData ad && ed.Amount == ad.Amount
        || expected is BoosterPackRewardData eb && actual is BoosterPackRewardData ab
            && LocalRewardSafety.SameBooster(eb.Id, eb.Count, ab.Id, ab.Count);
    private static string Describe(RewardData reward) => reward is GoldRewardData gold ? "GOLD:" + gold.Amount
        : reward is ArcaneDustRewardData dust ? "DUST:" + dust.Amount
        : reward is BoosterPackRewardData pack ? "BOOSTER:" + pack.Id + ":" + pack.Count : reward.RewardType.ToString();
    private static string Actor(int index, RewardData reward, GameObject actor) => index + ":" + Describe(reward)
        + ":instance=" + actor.GetInstanceID() + ":active=" + actor.activeSelf;

    // Ids of the current and queued AlertPopups; DialogManager keeps a request queued until its popup is current.
    private static List<string> DialogIds()
    {
        var ids = new List<string>();
        DialogManager dialogs = DialogManager.Get();
        if (dialogs == null) return ids;
        AlertPopup current = CurrentDialogField?.GetValue(dialogs) as AlertPopup;
        if (current != null) ids.Add(current.GetInfo()?.m_id);
        if (DialogQueueField?.GetValue(dialogs) is System.Collections.IEnumerable queue)
            foreach (object request in queue)
                if ((request as DialogManager.DialogRequest)?.m_info is AlertPopup.PopupInfo info) ids.Add(info.m_id);
        return ids;
    }

    // A read failure never gates: the HUB must stay usable.
    private static bool OwnPopupUp()
    {
        try { return LocalRewardSafety.BlocksHomeInput(false, DialogIds()); }
        catch (Exception) { return false; }
    }

    // fallbackReason and placementFacts survive the reset: test-control state keeps the last presentation's outcome.
    private static void ResetFlow()
    {
        RetireDetailedButton();
        ReleaseInputCamera(); placedCamera = null;
        display = null; matchId = ""; rewards.Clear(); createdMask = openedMask = reachableMask = 0;
        fallbackPopup = savePopup = detailsPopup = cardRematch = cardStyled = cardFailed = cardConceded = cardDetailsFailed = false;
        playedDeck = null;
        cardButton = cardDetails = null;
        prefabState = "idle"; actorFacts = "none";
        ackRetryReason = summaryBody = summaryHeader = noItemsText = ""; phase = LocalRewardPhase.Idle;
    }
}
