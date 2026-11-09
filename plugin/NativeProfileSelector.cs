using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Blizzard.T5.Core.Utils;
using Blizzard.T5.Jobs;
using HarmonyLib;
using Hearthstone.Core;
using MiniJSON;
using UnityEngine;

internal static class NativeProfileSelector
{
    private static readonly FieldInfo OptionsButton = AccessTools.Field(typeof(GameMenu), "m_optionsButton");
    private static ManualLogSource log;
    private static OfflineLaunchContext launch;
    private static NativeProfileMenu popup;
    private static UIBButton menuButton;
    private static bool startupPending, startupRevealQueued, quitting, reconciled;
    private static string startupError, startupErrorHeader;

    internal static bool BlocksHome => startupPending || popup != null && popup.IsShown();
#if !SOLOSTONE_RELEASE
    internal static bool OwnsPicker => popup != null && popup.IsShown() && DeckExperience.ProfileSelectorOwnsInput;
#endif
    internal static string RootProfilePath => launch.RootProfilePath;
#if !SOLOSTONE_RELEASE
    internal static IEnumerable<KeyValuePair<PegUIElement, string>> Controls
    {
        get
        {
            if (menuButton != null) yield return new KeyValuePair<PegUIElement, string>(menuButton, "save.menu");
            if (popup != null) foreach (var row in popup.Controls) yield return row;
        }
    }
#endif
    internal static bool IsSelected(PegUIElement element) => popup != null && popup.IsSelected(element);
    internal static void Log(string text) => log?.LogInfo(text);
#if !SOLOSTONE_RELEASE
    internal static bool OwnsInput(PegUIElement element) => element != null && popup != null && popup.IsShown()
        && popup.Controls.Any(control => ReferenceEquals(control.Key, element));
#endif

#if !SOLOSTONE_RELEASE
    internal static string InputBlock(PegUIElement element, DialogBase dialog) => ProfileInputBlock.Reason(BlocksHome, dialog != null,
        dialog != null && element != null && element.transform.IsChildOf(dialog.transform), OwnsInput(element), popup != null && popup.ClickLocked);
#endif

    internal static void Initialize(Harmony harmony, ManualLogSource logger, OfflineLaunchContext context)
    {
        log = logger; launch = context;
        if (context.SnapshotId != null || context.LaneId != 0) return;
        NativeOptionsPace.Initialize(harmony, logger);
        harmony.Patch(AccessTools.Method(typeof(BnetBar), "HandleEscapeKey") ?? throw new MissingMethodException("BnetBar", "HandleEscapeKey"),
            prefix: new HarmonyMethod(typeof(NativeProfileSelector), nameof(BeforeEscape)));
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "Awake") ?? throw new MissingMethodException("GameMenu", "Awake"),
            postfix: new HarmonyMethod(typeof(NativeProfileSelector), nameof(AddMenuRow)));
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "GetButtons") ?? throw new MissingMethodException("GameMenu", "GetButtons"),
            postfix: new HarmonyMethod(typeof(NativeProfileSelector), nameof(ListMenuRow)));
    }

    // The save hold is released and the launch has its startup choice. Until here no picker opens, so nothing can pick, create or delete a save.
    internal static void SavesResolved()
    {
        startupPending = launch.ShowProfileSelector;
        // ResolveSaves already ran the picker's Reconcile (nothing bound yet) for picker-capable launches.
        reconciled = launch.Startup?.Reconciled == true;
        // A picker launch with something to tell the player (their last save would not open, or the last start never finished).
        if (startupPending && launch.Startup?.Notice != null) { startupError = launch.Startup.Notice; startupErrorHeader = launch.Startup.NoticeHeader; }
    }

    internal static void Update()
    {
        popup?.SampleTooltipTrace();
        popup?.SamplePaceCheck();
        if (popup != null && popup.IsShown()) return;
        if (!startupPending || quitting || DialogManager.Get() == null || UniversalInputManager.Get() == null
            || UniversalInputManager.Get().IsDialogActive() || !SceneMgr.IsInitialized() || SceneMgr.Get().IsTransitioning()) return;
        var splash = SplashScreen.Get();
        if (splash != null && splash.gameObject.activeInHierarchy)
        {
            if (!startupRevealQueued)
            {
                startupRevealQueued = true;
                var reveal = new JobDefinition("Offline.ProfileSelectorReady", StartupShellReady(), Array.Empty<IJobDependency>());
                Processor.QueueJob("Offline.HideSplashForProfileSelector", splash.Hide(reveal), Array.Empty<IJobDependency>());
            }
            return;
        }
        Open(true);
        StartupTiming.Milestone("save-picker");
        if (startupError != null) { string text = startupError, header = startupErrorHeader; startupError = startupErrorHeader = null; ShowNotice(text, header); }
    }

    // A relaunch target that cannot open falls back to the ordinary startup picker instead of quitting.
    internal static void RecoverStartup(string error)
    {
        startupPending = true;
        startupError = "The chosen save could not open. Choose another. " + error; startupErrorHeader = null;
    }

    // The last-opened save passed the startup checks but failed to open: the ordinary picker, with the notice on top.
    internal static void RecoverAutoOpen(string notice, string header)
    {
        startupPending = true;
        startupError = notice; startupErrorHeader = header;
    }

    internal static void TraceDepth(string text) => log?.LogInfo("SAVE_TOOLTIP_DEPTH " + text);

    internal static void TraceTooltip(string phase, PegUIElement row, TooltipZone zone)
    {
        try
        {
            var tip = zone == null ? null : zone.GetTooltipObject();
            string TransformFacts(GameObject value) => value == null ? "missing" : value.name + " active=" + value.activeSelf + "/" + value.activeInHierarchy
                + " layer=" + value.layer + " position=" + value.transform.position + " rotation=" + value.transform.eulerAngles
                + " scale=" + value.transform.localScale + " worldScale=" + value.transform.lossyScale;
            var camera = row == null ? null : CameraUtils.FindFirstByLayerMask(1 << row.gameObject.layer);
            string view = camera == null ? "missing" : camera.name + " enabled=" + camera.enabled + "/" + camera.gameObject.activeInHierarchy
                + " mask=" + camera.cullingMask + " depth=" + camera.depth + " clip=" + camera.nearClipPlane + "/" + camera.farClipPlane
                + " position=" + camera.transform.position + " rotation=" + camera.transform.eulerAngles;
            string screen = tip == null || camera == null ? "missing" : camera.WorldToScreenPoint(tip.transform.position)
                + " bounds=" + DeckLayout.ScreenRect(tip, camera);
            string renderers = tip == null ? "missing" : string.Join(";", tip.GetComponentsInChildren<Renderer>(true).Take(4).Select(renderer =>
                renderer.name + " active=" + renderer.gameObject.activeInHierarchy + " enabled=" + renderer.enabled + " visible=" + renderer.isVisible
                + " layer=" + renderer.gameObject.layer + " bounds=" + renderer.bounds + " order=" + renderer.sortingOrder
                + " shader=" + renderer.sharedMaterial?.shader?.name + " queue=" + renderer.sharedMaterial?.renderQueue));
            log?.LogInfo("SAVE_TOOLTIP phase=" + phase + " showing=" + (zone != null && zone.IsShowingTooltip())
                + " prefab=" + zone?.tooltipPrefab?.name + " dialogs=" + DeckExperience.GameDialogCount
                + " row=[" + TransformFacts(row == null ? null : row.gameObject) + "] tip=[" + TransformFacts(tip)
                + "] camera=[" + view + "] screen=[" + screen + "] renderers=[" + renderers + "]");
        }
        catch (Exception error) { log?.LogWarning("SAVE_TOOLTIP phase=" + phase + " diagnosticError=" + error); }
    }

    private static IEnumerator<IAsyncJobResult> StartupShellReady() { yield break; }

    internal static string SwitchBlocker()
    {
        var profile = LocalLadder.Profile;
        if (!SceneMgr.IsInitialized() || SceneMgr.Get().GetMode() != SceneMgr.Mode.HUB || SceneMgr.Get().IsTransitionNowOrPending()
            || Box.Get() == null || Box.Get().IsBusy()) return "Home is busy.";
        if (LocalLadder.Screen == "Playing" || LocalLadder.Screen == "Starting" || profile?.HasPendingMatch == true) return "Finish the current match first.";
        if (profile?.HasPendingRewardPresentation == true || NativeRewardUI.BlocksHome || NativeResultsUI.BlocksEscape)
            return "Finish the pending reward first.";
        if (profile?.PackRevealPending == true) return "Finish the pack reveal first.";
        if (NativeAchievements.Active) return "Close " + NativeAchievements.ButtonLabel + " first.";
        if (NativeCrafting.PresentationBusy) return "Finish the crafting transaction first.";
        if (NativeShop.PresentationBusy) return "Finish the shop transaction first.";
        return null;
    }

    private static bool BeforeEscape(ref bool __result)
    {
        if (popup == null || !popup.HandleEscape()) return true;
        __result = true; return false;
    }

    private static void AddMenuRow(GameMenu __instance)
    {
        try
        {
            menuButton = __instance.CreateMenuButton(NativeGameMenuTidy.ProfileRow, NativeGameMenuTidy.ProfileLabel, _ => {
                BnetBar.Get()?.HideGameMenu(); Open(false);
            });
        }
        catch (Exception error) { menuButton = null; log?.LogWarning("SAVE_SELECTOR menu row unavailable: " + error.Message); }
    }

    private static void ListMenuRow(GameMenu __instance, ref List<UIBButton> __result)
    {
        if (menuButton == null || __result == null || GameMenu.IsInGameMenu() || NativeAchievements.Active || __result.Contains(menuButton)
            || !menuButton.transform.IsChildOf(__instance.transform.root)) return;
        int at = __result.IndexOf(OptionsButton?.GetValue(__instance) as UIBButton);
        __result.Insert(at < 0 ? 0 : at, menuButton);
    }

    private static void Open(bool startup)
    {
        try
        {
            // Once per launch, only at the unbound startup picker: finish or clean up a crashed delete/create. Never throws.
            if (startup && !reconciled && LocalLadder.Profile == null) { reconciled = true; ProfileCatalog.Reconcile(launch.RootProfilePath, Log); }
            if (popup == null) popup = new GameObject("SoloStoneProfileSelector").AddComponent<NativeProfileMenu>();
            popup.Open(startup);
        }
        catch (Exception error) { log?.LogError("SAVE_SELECTOR unavailable: " + error); ShowError(error.Message); }
    }

    // Options > Pace: the Pace page on its own, with Options hidden behind it (native Sound Options pattern).
    internal static void OpenPace()
    {
        try
        {
            if (popup == null) popup = new GameObject("SoloStoneProfileSelector").AddComponent<NativeProfileMenu>();
            popup.OpenPace();
        }
        catch (Exception error) { log?.LogError("SAVE_SELECTOR pace unavailable: " + error); ReturnToOptions(); ShowError(error.Message); }
    }

    internal static void ReturnToOptions()
    {
        var options = OptionsMenu.Get();
        if (options != null && !options.IsShown()) options.Show();
    }

    // Manage, its deletes and Options > Pace all check this (ProfileMenuRules.InMatch).
    internal static bool InMatch
    {
        get
        {
            var scene = SceneMgr.IsInitialized() ? SceneMgr.Get() : null;
            return ProfileMenuRules.InMatch(scene != null, scene?.GetMode() == SceneMgr.Mode.GAMEPLAY, scene?.IsModeRequested(SceneMgr.Mode.GAMEPLAY) == true,
                LocalLadder.Screen, LocalLadder.Profile?.HasPendingMatch == true);
        }
    }

    // The bound LocalProfile's own path, never launch.ProfilePath (an unbound picker launch points that at the root).
    private static string OpenProfile => LocalLadder.Profile?.FilePath;
    internal static bool IsOpenSave(string id, string selected) => ProfileCatalog.IsOpenSave(launch.RootProfilePath, id, selected, OpenProfile);
    internal static bool ManageAllowed(bool atStartup) => ProfileMenuRules.ManageAllowed(atStartup, LocalLadder.Profile != null, InMatch);

    internal static void EnterCurrent()
    {
        startupPending = false; popup?.Hide();
    }

    internal static void SelectStartup(string id)
    {
        try
        {
            popup?.ShowLoading();
            ClientBridge.BindStartupProfile(id);
            EnterCurrent();
        }
        catch (Exception error) { popup?.Open(true); ShowError("Save could not finish opening. Recovery may have repaired it; retry or choose another. " + error.Message); }
    }

    internal static void Switch(string id)
    {
        string blocked = SwitchBlocker();
        if (blocked != null) { ShowError(blocked); return; }
        try
        {
            ProfileCatalog.RequestRelaunch(launch.RootProfilePath, launch.RunId, id);
            NativeCollectionStatus.FlushPendingSeen(true, true);
            quitting = true; popup?.ShowLoading(); Application.Quit(46);
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    internal static void Create(string name, ProfileOptions options)
    {
        if (!startupPending)
        {
            string blocked = SwitchBlocker();
            if (blocked != null) { ShowError(blocked); return; }
        }
        try
        {
            // Not recorded here: the save is recorded once it has opened (startup bind, or the relaunch's CompleteRelaunch).
            ProfileSlot slot = ProfileCatalog.Create(launch.RootProfilePath, name, options, false);
            if (startupPending)
            {
                popup?.ShowLoading();
                ClientBridge.BindStartupProfile(slot.Id);
                EnterCurrent();
                return;
            }
            ProfileCatalog.RequestRelaunch(launch.RootProfilePath, launch.RunId, slot.Id);
            NativeCollectionStatus.FlushPendingSeen(true, true);
            quitting = true; popup?.ShowLoading(); Application.Quit(46);
        }
        catch (Exception error) { if (startupPending) popup?.Open(true); ShowError("Save creation/opening did not finish. Retry or choose another. " + error.Message); }
    }

    // Every refused or failed attempt logs one PROFILE_DELETE line, same formatter as ProfileCatalog.Delete. Opening the confirm
    // logs nothing; confirming logs the catalog's own line.
    private static void RefuseDelete(string id, string name, string outcome, string message)
    {
        Log(ProfileCatalog.DeleteLogLine(id, name, null, outcome));
        ShowError(message);
    }

    // A tap on a manage-mode row: plain confirm, no hold. Unique popup so a double tap cannot stack two.
    internal static void ConfirmDelete(string id)
    {
        ProfileSummary summary;
        try
        {
            string blocked = ProfileCatalog.DeleteBlock(launch.RootProfilePath, id, startupPending, OpenProfile, InMatch);
            if (blocked != null) { RefuseDelete(id, null, blocked, NativeSaveDeleteText.Failure(blocked)); return; }
            summary = ProfileCatalog.Describe(launch.RootProfilePath, id);
        }
        catch (Exception error) { RefuseDelete(id, null, "failed:" + ProfileCatalog.Reason(error), error.Message); return; }
        // Locked or unreadable files could hide a pending match: no confirm is offered, retry later.
        if (summary.PendingUnknown) { RefuseDelete(id, summary.Name, "refused-unreadable", NativeSaveDeleteText.Failure("refused-unreadable")); return; }
        try
        {
            DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
                m_id = NativeSaveDeleteText.PopupId, m_headerText = NativeSaveDeleteText.Header, m_showAlertIcon = false,
                m_text = NativeSaveDeleteText.Body(summary.Name, summary.CreatedUtc, summary.Readable, summary.Sandbox, summary.Rank, summary.Gold,
                    summary.Gold >= ProfileCatalog.UnlimitedGold, summary.DeckCount),
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL, m_confirmText = NativeSaveDeleteText.ConfirmLabel,
                m_cancelText = NativeSaveDeleteText.CancelLabel, m_keyboardEscIsCancel = true,
                m_responseCallback = (response, data) => { if (response == AlertPopup.Response.CONFIRM) Delete(id); }
            });
        }
        catch (Exception error) { RefuseDelete(id, summary.Name, "failed:" + ProfileCatalog.Reason(error), error.Message); }
    }

    internal static void Delete(string id)
    {
        var result = ProfileCatalog.DeleteFromPicker(launch.RootProfilePath, id, Log, startupPending, OpenProfile, InMatch);
        if (result.SlotRemoved) popup?.AfterDelete(); else ShowError(NativeSaveDeleteText.Failure(result.Outcome));
    }

#if !SOLOSTONE_RELEASE
    // Read-only test-control view of the save list at the picker: slots, selection, and the .deleted folder names.
    internal static JsonNode CatalogState()
    {
        var node = new JsonNode {
            { "startupPending", startupPending }, { "bound", LocalLadder.Profile != null }, { "reconciled", reconciled },
            { "pickerShown", popup != null && popup.IsShown() }, { "manage", popup != null && popup.Managing }, { "inMatch", InMatch },
            { "paceOption", NativeOptionsPace.State() }, { "startup", StartupState() }
        };
        try
        {
            var slots = new JsonList();
            foreach (var row in ProfileCatalog.List(launch.RootProfilePath))
                slots.Add(new JsonNode { { "id", row.Id }, { "name", row.Name }, { "unavailable", row.Unavailable } });
            string selected = ProfileCatalog.SelectedId(launch.RootProfilePath);
            node["slots"] = slots; node["selectedId"] = selected;
            // What the delete rule treats as open, and that file's hash: a delete of another save must leave both alone.
            node["openId"] = ProfileCatalog.List(launch.RootProfilePath).FirstOrDefault(row => IsOpenSave(row.Id, selected))?.Id;
            node["openSha256"] = OpenProfile != null && System.IO.File.Exists(OpenProfile) ? LocalProfileBackups.Hash(System.IO.File.ReadAllBytes(OpenProfile)) : null;
            node["deleted"] = TestControlProtocol.List(ProfileCatalog.DeletedFolders(launch.RootProfilePath).Cast<object>());
        }
        catch (Exception error) { node["error"] = error.Message; }
        return node;
    }
#endif

    // `hslive startup-save`: how this launch chose its save. autoOpened: the last-opened save opened with no picker.
    // pickerShown: the startup picker is on screen right now. saveId: the save bound to this process (null while unbound).
    private static JsonNode StartupState()
    {
        try { return StartupStateUnchecked(); }
        catch (Exception error) { return new JsonNode { { "error", error.Message } }; }
    }

    private static JsonNode StartupStateUnchecked()
    {
        var startup = launch.Startup;
        bool bound = LocalLadder.Profile != null;
        string saveId = bound ? ProfileCatalog.List(launch.RootProfilePath).FirstOrDefault(row => IsOpenSave(row.Id, ProfileCatalog.SelectedId(launch.RootProfilePath)))?.Id : null;
        string mode = SaveHold.Held ? "held" : startup?.Mode ?? (launch.RelaunchTargetId != null ? "relaunch" : "fixed");
        return new JsonNode {
            { "mode", mode }, { "reason", SaveHold.Held ? "client-checks" : startup?.Reason ?? (launch.RelaunchTargetId != null ? "change-profile" : "fixed-profile") },
            { "autoOpened", startup != null && startup.IsAuto && startup.Failure == null && bound },
            { "pickerShown", popup != null && popup.IsShown() }, { "saveId", saveId }, { "targetId", startup?.TargetId ?? launch.RelaunchTargetId },
            { "request", startup?.Request }, { "failure", startup?.Failure }, { "notice", startup?.Notice }
        };
    }

    internal static void ShowError(string text, string header = null) => ShowAlert(NativeSaveDeleteText.ErrorPopupId, text, header);

    // Startup only: why the picker is up instead of a save. Its own popup id (save.notice.back), never the save-error popup.
    internal static void ShowNotice(string text, string header = null) => ShowAlert(NativeSaveDeleteText.NoticePopupId, text, header);

    private static void ShowAlert(string popupId, string text, string header)
    {
        DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
            m_id = popupId, m_headerText = header ?? "SAVE UNAVAILABLE", m_text = text,
            m_showAlertIcon = true, m_responseDisplay = AlertPopup.ResponseDisplay.CANCEL,
            m_cancelText = "BACK", m_keyboardEscIsCancel = true
        });
    }
}


// The save picker and create-save form, laid out by the native ButtonListMenu container. Planks are native menu
// buttons. The name and meta fields are the menu's own title plate (the name edited in place with the native text
// input, like the deck tray rename); with the retail Options parts (ProfileFormKit) the cheats header gets the
// dropdown arrow and the cheats are Options checkboxes, otherwise planks. Mouse/touch only: no controller focus.
internal sealed class NativeProfileMenu : ButtonListMenu
{
    private const float PairScale = .88f;
    // Owner 2026-10-28: the section is "Sandbox" on screen; ids (save.cheat.*) and code names keep cheat.
    internal const string CheatNote = "Sandbox options are locked in when the save is created.", CheatTitle = "Sandbox (optional)", CheatTooltipHeader = "Sandbox";
    private static readonly Color LabelColor = new Color(.96f, .87f, .66f), DeleteColor = new Color(.9f, .2f, .15f);
    private sealed class Slice { internal GameObject Object; internal bool Text, Both; internal Vector3 Padding; }

    private readonly List<UIBButton> rows = new List<UIBButton>();
    private readonly List<Slice> slices = new List<Slice>();
    private readonly List<GameObject> owned = new List<GameObject>();
    private readonly List<KeyValuePair<string, Func<Camera, Rect?>>> paceBounds = new List<KeyValuePair<string, Func<Camera, Rect?>>>();
    private readonly List<Mesh> paceMeshes = new List<Mesh>();
    private bool layoutLogged;
    private int paceCheckFrame = -1;
    private string lastPaceCheck;
    private readonly List<Action> beforeLayout = new List<Action>();
    private readonly List<Action> afterLayout = new List<Action>();
    private readonly List<GameObject> unsized = new List<GameObject>(), ignoring = new List<GameObject>();
    private readonly List<KeyValuePair<GameObject, GameObject>> fieldRows = new List<KeyValuePair<GameObject, GameObject>>();
    private GameObject fieldsContent;
    private Action fieldsLayout;
    private readonly Dictionary<PegUIElement, string> controls = new Dictionary<PegUIElement, string>();
    private readonly HashSet<PegUIElement> selectedControls = new HashSet<PegUIElement>();
    private bool startup, wizard, pacePage, paceFromOptions, ownsNativeDialog, loading, cheatsOpen, editing, manage;
    private int page;
    private string saveName, pendingName;
    private ProfileOptions options;
    private Component nameText;
    private Color? nameColor;
    private UIBButton unitPlank;
    private GameObject cheatList;
    private int cheatRows;
    private float cheatPitch;
    private float unitWorld, unitScale = 1, kitScale;
    private Quaternion plankFacing = Quaternion.identity;
    private Vector3 right = Vector3.right, up = Vector3.forward, toward = Vector3.up;
    private int tooltipTraceCount, tooltipTraceFrame;
    private PegUIElement tooltipTraceRow;
    private TooltipZone tooltipTraceZone;
    private readonly ClickLockout lockout = new ClickLockout();
    private readonly TextInputRetry nameRetry = new TextInputRetry();
    private Mesh fieldsMesh;

    private bool Locked() => lockout.Blocks(Time.realtimeSinceStartup);
    internal bool ClickLocked => Locked();

    protected override void Awake() { m_showAnimation = false; base.Awake(); Hide(); }
    protected override List<UIBButton> GetButtons() => rows;
#if !SOLOSTONE_RELEASE
    internal IEnumerable<KeyValuePair<PegUIElement, string>> Controls
    {
        get
        {
            foreach (var control in controls) yield return control;
            // A click beside the menu (OnBlockerRelease): the startup picker and the form ignore it, so only the others list it.
            if (IsShown() && !startup && !wizard && !loading && m_blocker != null)
                yield return new KeyValuePair<PegUIElement, string>(m_blocker, NativeSaveDeleteText.DismissId);
        }
    }
#endif
    internal bool IsSelected(PegUIElement element) => element != null && selectedControls.Contains(element);
    internal bool Managing => manage;

    internal void Open(bool fromStartup)
    {
        startup = fromStartup; wizard = pacePage = paceFromOptions = loading = manage = false; page = 0; Build(); Show();
        ProfileFormKit.Request(() => { if (this != null && IsShown() && !loading) Build(); }, true);
    }

    internal void OpenPace()
    {
        startup = wizard = loading = manage = false; pacePage = paceFromOptions = true; page = 0; Build(); Show();
        ProfileFormKit.Request(() => { if (this != null && IsShown() && !loading) Build(); }, true);
    }

    // True when the key was this menu's (ProfileMenuRules.Escape).
    internal bool HandleEscape()
    {
        var action = ProfileMenuRules.Escape(IsShown(), startup, DialogManager.Get()?.ShowingDialog() == true, editing, loading, wizard, pacePage);
        if (action == ProfileEscape.Pass) return false;
        if (action == ProfileEscape.LeavePace) LeavePace();
        else if (action == ProfileEscape.LeaveWizard) { wizard = false; Build(); }
        else if (action == ProfileEscape.Close) Hide();
        return true;
    }

    private void LeavePace()
    {
        bool toOptions = paceFromOptions;
        pacePage = paceFromOptions = false;
        Hide();
        if (toOptions) NativeProfileSelector.ReturnToOptions();
    }

    private void Build()
    {
        if (editing) FinishEdit(UniversalInputManager.Get()?.GetInputText());
        bool shown = IsShown(); if (shown) Hide();
        lockout.Arm(Time.realtimeSinceStartup);
        var allButtons = AccessTools.Field(typeof(ButtonListMenu), "m_allButtons")?.GetValue(this) as List<UIBButton>
            ?? throw new MissingFieldException("ButtonListMenu", "m_allButtons");
        // Native Show dereferences every cached button, including rows Unity destroyed after an earlier rebuild.
        allButtons.Clear();
        // Destroy is deferred; inactive rows cannot inflate the next same-frame layout bounds.
        foreach (var item in owned) if (item != null) { item.SetActive(false); UnityEngine.Object.Destroy(item); }
        paceBounds.Clear(); layoutLogged = false; paceCheckFrame = -1; lastPaceCheck = null;
        foreach (var mesh in paceMeshes) UnityEngine.Object.Destroy(mesh);
        paceMeshes.Clear();
        owned.Clear(); rows.Clear(); slices.Clear(); beforeLayout.Clear(); afterLayout.Clear(); unsized.Clear(); fieldRows.Clear(); fieldsContent = null; fieldsLayout = null; controls.Clear(); selectedControls.Clear();
        nameText = null; nameColor = null; unitPlank = null; cheatList = null; cheatRows = 0; cheatPitch = 0; unitWorld = 0; unitScale = 1; kitScale = 0;
        Header(wizard ? "Create save" : pacePage ? "Pace" : "Choose save");
        Axes();
        if (wizard) BuildWizard(ProfileFormKit.Ready); else if (pacePage) BuildPace(ProfileFormKit.Ready); else BuildSlots(ProfileFormKit.Ready);
        if (shown) Show();
    }

    private void BuildSlots(bool kit)
    {
        var slots = ProfileCatalog.List(NativeProfileSelector.RootProfilePath).ToArray();
        string selected = ProfileCatalog.SelectedId(NativeProfileSelector.RootProfilePath);
        page = ProfileMenuGeometry.ClampPage(page, slots.Length);
        foreach (var slot in slots.Skip(page * ProfileMenuGeometry.PageSize).Take(ProfileMenuGeometry.PageSize))
        {
            var captured = slot;
            string label = slot.Name + (slot.Unavailable == null ? "" : " (unavailable: " + slot.Unavailable + ")");
            if (manage) { ManageRow(captured, label, slot.Id == selected, NativeProfileSelector.IsOpenSave(slot.Id, selected)); continue; }
            UIBButton row = Plank("ProfileSlot-" + slot.Id, "save.slot:" + slot.Id, label, _ => {
                if (captured.Unavailable != null) return;
                if (startup) NativeProfileSelector.SelectStartup(captured.Id);
                else if (captured.Id == selected) NativeProfileSelector.EnterCurrent();
                else NativeProfileSelector.Switch(captured.Id);
            });
            if (slot.Id == selected) { selectedControls.Add(row); MarkCurrent(row, kit, label); }
            // A damaged save stays listed so its name is not silently lost, but it cannot be opened.
            if (slot.Unavailable != null) row.SetEnabled(false);
        }
        if (page > 0) Plank("ProfilePrevious", "save.prev", "Previous", _ => { page--; Build(); });
        if (ProfileMenuGeometry.HasNext(page, slots.Length)) Plank("ProfileNext", "save.next", "Next", _ => { page++; Build(); });
        // "+ New save" and "Manage" (or "Done") share a row; manage mode hides "+ New save".
        bool canAdd = !manage && slots.Length < ProfileCatalog.MaximumSlots, canManage = slots.Length > 0 && NativeProfileSelector.ManageAllowed(startup);
        if (slots.Length > 0 && (canAdd || canManage)) Divider();
        UIBButton add = canAdd ? Plank("ProfileAdd", "save.add", "+ New save", _ => StartWizard(slots), !canManage) : null;
        UIBButton toggle = canManage ? Plank("ProfileManage", NativeSaveDeleteText.ManageId, manage ? NativeSaveDeleteText.DoneLabel : NativeSaveDeleteText.ManageLabel,
            _ => { manage = !manage; Build(); }, add == null) : null;
        if (add != null && toggle != null) Pair("ProfileManageActions", add, toggle);
        if (!startup) Plank("ProfileCancel", "save.cancel", "Cancel", _ => Hide());
    }

    // A locked row's tap does nothing, and ProfileCatalog.Delete refuses the open save regardless.
    private void ManageRow(ProfileSlot slot, string label, bool selected, bool open)
    {
        var plan = ProfileMenuRules.ManageRow(slot.Id, label, slot.Id == ProfileCatalog.LegacyId, open);
        UIBButton row = Plank("ProfileSlot-" + slot.Id, plan.Id, plan.Label, _ => { if (!plan.Locked) NativeProfileSelector.ConfirmDelete(slot.Id); });
        if (selected) selectedControls.Add(row);
        if (plan.Locked) row.SetEnabled(false); else MarkDeletable(row);
    }

    // The red mark (the glyph itself is NativeSaveDeleteText.DeleteMark): the plank text takes the colour through the kit.
    private static void MarkDeletable(UIBButton row) => ProfileFormKit.SetColor(ProfileFormKit.Label(row), DeleteColor);

    // A delete just removed a row: re-page and rebuild once the confirm dialog has gone. Lockout covers the gap.
    internal void AfterDelete()
    {
        lockout.Arm(Time.realtimeSinceStartup);
        if (isActiveAndEnabled) StartCoroutine(RefreshAfterDelete()); else RefreshNow();
    }

    private IEnumerator RefreshAfterDelete()
    {
        yield return null;
        RefreshNow();
    }

    private void RefreshNow()
    {
        if (this == null || loading || wizard || pacePage) return;
        int count = ProfileCatalog.List(NativeProfileSelector.RootProfilePath).Count;
        page = ProfileMenuGeometry.ClampPage(page, count);
        if (count == 0) manage = false;
        Build();
    }

    private void StartWizard(ProfileSlot[] slots)
    {
        int number = 1; var names = new HashSet<string>(slots.Select(slot => slot.Name), StringComparer.OrdinalIgnoreCase);
        while (names.Contains("Save " + number)) number++;
        saveName = "Save " + number; options = new ProfileOptions { GrindTier = GrindTiers.Default.Id }; cheatsOpen = false; wizard = true; Build();
    }

    private void BuildWizard(bool kit)
    {
        // The action pair is made first: its plank is the size unit for every other row.
        var create = Plank("ProfileCreate", "save.create", "Create", _ => CreateSave(), false);
        var cancel = Plank("ProfileCreateCancel", "save.cancel", "Cancel", _ => { wizard = false; Build(); }, false);
        if (kit) KitScale();
        PegUIElement name = Plate("ProfileName", "Name", saveName, out nameText);
        if (name == null) { Label("ProfileNameLabel", "Save name", LabelColor, 1); var plank = Plank("ProfileName", "save.name", saveName, _ => EditName()); name = plank; nameText = ProfileFormKit.Label(plank); }
        else { controls[name] = "save.name"; name.AddEventListener(UIEventType.RELEASE, _ => { if (!Locked()) EditName(); }); }
        nameColor = ProfileFormKit.GetColor(nameText);
        PegUIElement meta = Plate("ProfileMeta", "Meta", ProfileCatalog.MetaLabel, out _);
        if (meta == null) meta = Plank("ProfileMeta", null, ProfileCatalog.MetaLabel, _ => { });
        controls[meta] = "save.meta.violet-hold";
        ConfigureMeta(meta);
        PaceRows(kit, "save.pace:", () => GrindTiers.Find(options.GrindTier), tier => options.GrindTier = tier.Id);
        // Sandbox keeps its own one-line pitch; Pace reserves explicit two-line slices.
        cheatList = null; cheatRows = 0; cheatPitch = 0;
        Divider();
        CheatsHeader(kit);
        if (cheatsOpen)
        {
            Toggle(kit, "ProfileAllCards", "save.cheat.all-cards", "All cards owned", () => options.AllCards, value => options.AllCards = value);
            Toggle(kit, "ProfileAllDecks", "save.cheat.all-decks", "All decks", () => options.AllDecks, value => options.AllDecks = value);
            Toggle(kit, "ProfileAllCosmetics", "save.cheat.all-cosmetics", "All cosmetics", () => options.AllCosmetics, value => options.AllCosmetics = value);
            Toggle(kit, "ProfileInfiniteGold", "save.cheat.infinite-gold", "Infinite gold", () => options.InfiniteGold, value => options.InfiniteGold = value);
            Toggle(kit, "ProfileInfiniteDust", "save.cheat.infinite-dust", "Infinite dust", () => options.InfiniteDust, value => options.InfiniteDust = value);
            Toggle(kit, "ProfileLegendOne", "save.cheat.legend-one", "Start at Legend #1", () => options.StartLegendOne, value => options.StartLegendOne = value);
        }
        Divider();
        Pair("ProfileActions", create, cancel);
    }

    private void BuildPace(bool kit)
    {
        var back = Plank("ProfilePaceBack", "save.pace.back", "Back", _ => LeavePace(), false);
        if (kit) KitScale();
        PaceRows(kit, "save.pace.set:", () => LocalLadder.Profile.GrindTier, tier => LocalLadder.Profile.SetGrindTier(tier.Id));
        PaceNote();
        slices.Add(new Slice { Object = back.gameObject });
    }

    private void PaceRows(bool kit, string prefix, Func<GrindTier> current, Action<GrindTier> select)
    {
        if (wizard) Label("ProfilePaceHeading", "Pace", LabelColor, .8f);
        // One native slice, like Sandbox: retail inter-slice spacing must not pad every compact pace row.
        var group = Wrapper("ProfilePaceRows");
        var body = SizedContent(group, out Mesh mesh);
        var content = new List<GameObject>();
        foreach (var tier in GrindTiers.Selectable)
        {
            var captured = tier;
            var row = PaceRow(kit, tier, prefix + tier.Id, () => current().Id == captured.Id, () => select(captured));
            row.transform.parent.SetParent(body.transform, true);
            content.Add(row);
        }
        beforeLayout.Add(() => StackPaceRows(content, group, mesh));
        afterLayout.Add(() => CenterPaceRows(content));
    }

    private void StackPaceRows(List<GameObject> content, GameObject group, Mesh mesh)
    {
        var heights = content.Select(item => ProfileFormKit.Span(item.transform.parent.gameObject, up, out float lo, out float hi) ? hi - lo : 0).ToArray();
        float height = heights.Sum(), cursor = Vector3.Dot(group.transform.position, up) + height / 2;
        for (int i = 0; i < content.Count; i++)
        {
            var row = content[i].transform.parent;
            row.position += up * (cursor - heights[i] / 2 - Vector3.Dot(row.position, up));
            cursor -= heights[i];
        }
        ProfileFormKit.Span(content[0].transform.parent.gameObject, right, out float x0, out float x1);
        SizeSlice(group, mesh, x1 - x0, height);
    }

    // Centre the drawn group once, preserving the common checkbox/text columns across all tiers.
    private void CenterPaceRows(List<GameObject> content)
    {
        float left = float.MaxValue, edge = float.MinValue;
        foreach (var item in content)
        {
            if (ProfileFormKit.Span(item, right, out float x0, out float x1)) { left = Math.Min(left, x0); edge = Math.Max(edge, x1); }
            foreach (Component text in item.GetComponentsInChildren(ProfileFormKit.TextType, false))
            {
                var bounds = ProfileFormKit.RenderedBounds(text);
                if (!bounds.HasValue) continue;
                left = Math.Min(left, Corners(bounds.Value).Min(point => Vector3.Dot(point, right)));
                edge = Math.Max(edge, Corners(bounds.Value).Max(point => Vector3.Dot(point, right)));
            }
        }
        if (left > edge || content.Count == 0) return;
        float center = content.Average(item => Vector3.Dot(item.transform.parent.position, right));
        foreach (var item in content) item.transform.position += right * (center - (left + edge) / 2);
    }

    private static string PaceHours(double hours) => hours.ToString(hours < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);

    // Both screens use the action pair's width, in menu units before Show resets its scale.
    private float FormWidth => (ProfileFormKit.Span(unitPlank.gameObject, right, out float x0, out float x1)
        ? (x1 - x0) / unitScale : unitWorld * 4) * PairScale * 2 + unitWorld * .2f;

    private GameObject PaceRow(bool kit, GrindTier tier, string semanticId, Func<bool> read, Action select)
    {
        var wrapper = Wrapper("ProfilePace-" + tier.Id);
        var content = SizedContent(wrapper, out Mesh mesh, false);
        PegUIElement control;
        Component glyph = null;
        if (kit)
        {
            var box = ProfileFormKit.CloneCheckBox();
            Adopt(box.gameObject, content); Size(box.gameObject, .5f);
            box.SetButtonText("");
            var oldLabel = ProfileFormKit.Label(box);
            if (oldLabel != null) oldLabel.gameObject.SetActive(false);
            box.SetChecked(read());
            box.AddEventListener(UIEventType.RELEASE, _ => {
                if (Locked()) { box.SetChecked(read()); return; }
                try { select(); } catch (Exception error) { box.SetChecked(read()); NativeProfileSelector.ShowError(error.Message); return; }
                Build();
            });
            control = box;
        }
        else
        {
            var plank = Plank("ProfilePaceFallback-" + tier.Id, null, "", _ => {
                try { select(); } catch (Exception error) { NativeProfileSelector.ShowError(error.Message); return; }
                Build();
            }, false);
            plank.transform.SetParent(content.transform, true);
            control = plank;
            glyph = Caption(content.transform, read() ? "☑" : "☐", LabelColor, .8f);
        }
        control.name = wrapper.name + "Control";
        controls[control] = semanticId;
        if (read()) selectedControls.Add(control);
        var name = Caption(content.transform, tier.Name, LabelColor, .8f);
        var stats = Caption(content.transform, "one meta deck ~" + PaceHours(tier.HoursPerMetaDeck)
            + " h · half collection ~" + tier.HoursToHalfCollection + " h", new Color(.76f, .72f, .63f), .6f);
        foreach (var text in new[] { glyph, name, stats }) ProfileFormKit.SingleLine(text);
        // Capture only menu-local dimensions. Per-layout world dimensions follow the current wrapper scale.
        float baseScale = wrapper.transform.lossyScale.x, localUnit = unitWorld / baseScale, localWidth = FormWidth / baseScale;
        var nameScale = name.transform.localScale;
        var statsScale = stats.transform.localScale;
        var controlScale = control.transform.localScale;
        beforeLayout.Add(() => {
            float unit = localUnit * wrapper.transform.lossyScale.x, width = localWidth * wrapper.transform.lossyScale.x;
            name.transform.localScale = nameScale; stats.transform.localScale = statsScale;
            control.transform.localScale = controlScale;
            float pitch = ArrangePaceRow(control, glyph, name, stats, wrapper.transform.position, unit, width, kit);
            SizeSlice(wrapper, mesh, width, pitch);
        });
        TrackPace(tier.Id + ".checkbox", camera => kit ? MeshScreen(control.gameObject, camera) : TextScreen(glyph, camera));
        TrackPace(tier.Id + ".name", camera => TextScreen(name, camera));
        TrackPace(tier.Id + ".stats", camera => TextScreen(stats, camera));
        ConfigurePaceTooltip(control, tier);
        return content;
    }

    // Explicit row cells: checkbox left, two caption clones right. Glyph bounds determine placement, never a
    // checkbox's hanging Options label or an UberText frame whose pivot/alignment may differ from its glyphs.
    private float ArrangePaceRow(PegUIElement control, Component glyph, Component name, Component stats,
        Vector3 origin, float unit, float width, bool kit)
    {
        float left = Vector3.Dot(origin, right) - width / 2 + unit * .35f, middle = Vector3.Dot(origin, up);
        float checkWidth = unit * .5f, textLeft = left + checkWidth + unit * .22f;
        float textWidth = width - unit * .7f - checkWidth - unit * .22f;
        PlacePaceText(name, textLeft, middle, textWidth, unit * .38f);
        PlacePaceText(stats, textLeft, middle, textWidth, unit * .28f);
        var nameBounds = ProfileFormKit.RenderedBounds(name);
        var statsBounds = ProfileFormKit.RenderedBounds(stats);
        float nameHeight = nameBounds.HasValue ? TextHeight(nameBounds.Value) : unit * .38f;
        float statsHeight = statsBounds.HasValue ? TextHeight(statsBounds.Value) : unit * .28f;
        float lineGap = nameHeight * .15f, blockHeight = nameHeight + lineGap + statsHeight;
        PlacePaceText(name, textLeft, middle + (statsHeight + lineGap) / 2, textWidth, unit * .38f);
        PlacePaceText(stats, textLeft, middle - (nameHeight + lineGap) / 2, textWidth, unit * .28f);
        if (kit)
        {
            if (ProfileFormKit.Span(control.gameObject, up, out float y0, out float y1) && y1 > y0
                && ProfileFormKit.Span(control.gameObject, right, out float x0, out float x1) && x1 > x0)
                control.transform.localScale *= Math.Min(checkWidth / (x1 - x0), Math.Min(unit * .5f, blockHeight) / (y1 - y0));
            ProfileFormKit.Center(control.gameObject, right, up, left + checkWidth / 2, middle);
        }
        else
        {
            ProfileFormKit.SizeOnPlane(control.gameObject, right, up, width, blockHeight);
            ProfileFormKit.Center(control.gameObject, right, up, Vector3.Dot(origin, right), middle);
            PlacePaceText(glyph, left, middle, checkWidth, blockHeight);
        }
        return blockHeight + nameHeight * .35f;
    }

    private float TextHeight(Bounds bounds)
    {
        return Corners(bounds).Max(p => Vector3.Dot(p, up)) - Corners(bounds).Min(p => Vector3.Dot(p, up));
    }

    private void PlacePaceText(Component text, float left, float middle, float width, float height)
    {
        if (text == null) return;
        var bounds = ProfileFormKit.RenderedBounds(text);
        if (!bounds.HasValue) return;
        float w = Corners(bounds.Value).Max(p => Vector3.Dot(p, right)) - Corners(bounds.Value).Min(p => Vector3.Dot(p, right));
        float h = Corners(bounds.Value).Max(p => Vector3.Dot(p, up)) - Corners(bounds.Value).Min(p => Vector3.Dot(p, up));
        float scale = Math.Min(1, Math.Min(width / Math.Max(.0001f, w), height / Math.Max(.0001f, h)));
        if (!(scale > 0) || float.IsInfinity(scale)) return;
        text.transform.localScale *= scale;
        bounds = ProfileFormKit.RenderedBounds(text);
        if (!bounds.HasValue) return;
        text.transform.position += right * (left - Corners(bounds.Value).Min(p => Vector3.Dot(p, right)))
            + up * (middle - (Corners(bounds.Value).Min(p => Vector3.Dot(p, up)) + Corners(bounds.Value).Max(p => Vector3.Dot(p, up))) / 2);
    }

    private GameObject SizedContent(GameObject wrapper, out Mesh mesh, bool slice = true)
    {
        var content = new GameObject(wrapper.name + "Content");
        content.transform.SetParent(wrapper.transform, false); content.layer = wrapper.layer;
        unsized.Add(content);
        mesh = new Mesh { name = wrapper.name + "Bounds" }; paceMeshes.Add(mesh);
        wrapper.AddComponent<MeshFilter>().sharedMesh = mesh;
        if (slice) slices.Add(new Slice { Object = wrapper });
        return content;
    }

    private void SizeSlice(GameObject wrapper, Mesh mesh, float width, float height)
    {
        var vertices = new List<Vector3>();
        foreach (float x in new[] { -width / 2, width / 2 })
            foreach (float y in new[] { -height / 2, height / 2 })
                foreach (float z in new[] { -height * .01f, height * .01f })
                    vertices.Add(wrapper.transform.InverseTransformPoint(wrapper.transform.position + right * x + up * y + toward * z));
        mesh.Clear(); mesh.vertices = vertices.ToArray(); mesh.RecalculateBounds();
    }

    private void PaceNote()
    {
        var wrapper = Wrapper("ProfilePaceInfo");
        var content = SizedContent(wrapper, out Mesh mesh);
        var first = Caption(content.transform, "Applies from your next match.", LabelColor, .7f);
        var second = Caption(content.transform, "Earned progress is kept.", LabelColor, .7f);
        ProfileFormKit.SingleLine(first); ProfileFormKit.SingleLine(second);
        float scale = wrapper.transform.lossyScale.x, localUnit = unitWorld / scale, localWidth = FormWidth / scale;
        var firstScale = first.transform.localScale; var secondScale = second.transform.localScale;
        beforeLayout.Add(() => {
            float unit = localUnit * wrapper.transform.lossyScale.x, width = localWidth * wrapper.transform.lossyScale.x;
            first.transform.localScale = firstScale; second.transform.localScale = secondScale;
            SizeSlice(wrapper, mesh, width, ArrangePaceNote(first, second, wrapper.transform.position, unit, width));
        });
        TrackPace("note.first", camera => TextScreen(first, camera));
        TrackPace("note.second", camera => TextScreen(second, camera));
    }

    private float ArrangePaceNote(Component first, Component second, Vector3 origin, float unit, float width)
    {
        float middle = Vector3.Dot(origin, up), center = Vector3.Dot(origin, right), left = center - width / 2;
        PlacePaceText(first, left, middle, width, unit * .32f);
        PlacePaceText(second, left, middle, width, unit * .32f);
        var firstBounds = ProfileFormKit.RenderedBounds(first);
        var secondBounds = ProfileFormKit.RenderedBounds(second);
        float firstHeight = firstBounds.HasValue ? TextHeight(firstBounds.Value) : unit * .32f;
        float secondHeight = secondBounds.HasValue ? TextHeight(secondBounds.Value) : unit * .32f;
        float gap = firstHeight * .25f;
        PlacePaceText(first, left, middle + (secondHeight + gap) / 2, width, unit * .32f);
        PlacePaceText(second, left, middle - (firstHeight + gap) / 2, width, unit * .32f);
        foreach (var text in new[] { first, second })
        {
            var drawn = ProfileFormKit.RenderedBounds(text);
            if (drawn.HasValue) text.transform.position += right * (center
                - (Corners(drawn.Value).Min(p => Vector3.Dot(p, right)) + Corners(drawn.Value).Max(p => Vector3.Dot(p, right))) / 2);
        }
        return firstHeight + secondHeight + gap + firstHeight * .35f;
    }

    private void TrackPace(string id, Func<Camera, Rect?> bounds) => paceBounds.Add(new KeyValuePair<string, Func<Camera, Rect?>>(id, bounds));
    private static Rect? TextScreen(Component text, Camera camera)
    {
        var bounds = ProfileFormKit.RenderedBounds(text, true);
        return bounds.HasValue ? ProfileFormKit.ScreenRect(bounds.Value, camera) : null;
    }
    private static Rect? MeshScreen(GameObject item, Camera camera)
    {
        if (item == null) return null;
        Rect rect = DeckLayout.ScreenRect(item, camera);
        return rect.width > 0 && rect.height > 0 ? (Rect?)rect : null;
    }

    // Action planks: their clickable face (collider), not the bolt/end-cap ornaments, which sit on the native frame by design.
    private static Rect? ColliderScreen(GameObject item, Camera camera)
    {
        var collider = item == null ? null : item.GetComponentInChildren<Collider>();
        if (collider == null) return null;
        Bounds b = collider.bounds;
        var points = new List<Vector3>();
        foreach (float x in new[] { b.min.x, b.max.x })
            foreach (float y in new[] { b.min.y, b.max.y })
                foreach (float z in new[] { b.min.z, b.max.z })
                    points.Add(camera.WorldToScreenPoint(new Vector3(x, y, z)));
        var rect = Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y));
        return rect.width > 0 && rect.height > 0 ? (Rect?)rect : null;
    }

    internal void SamplePaceCheck()
    {
        if (paceCheckFrame < 0 || Time.frameCount < paceCheckFrame || !IsShown()) return;
        paceCheckFrame = -1;
        if (!wizard && !pacePage) return;
        var camera = CameraUtils.FindFirstByLayerMask(1 << m_menu.gameObject.layer);
        if (camera == null) return;
        try { CheckPace(camera); }
        catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM paceCheck page=" + (wizard ? "wizard" : "pace")
            + " screen=" + Screen.width + "x" + Screen.height + " ok=false inner=unavailable issues=[diagnostic:" + error.Message + "]"); }
    }

    private void CheckPace(Camera camera)
    {
        if (!wizard && !pacePage) return;
        // NineSliceElement's middle is the drawn background face, excluding its decorative side slices/shadows.
        // Intersect it with the INNER edges of the four drawn border side renderers: their actual widths/heights
        // supply the frame inset, rather than an estimated plank fraction or the whole background hierarchy.
        Rect? face = MeshScreen(m_menu.m_background.m_middle.m_slice, camera);
        Rect? left = MeshScreen(m_menu.m_border.m_left.m_slice, camera), rightEdge = MeshScreen(m_menu.m_border.m_right.m_slice, camera);
        Rect? top = MeshScreen(m_menu.m_border.m_top.m_slice, camera), bottom = MeshScreen(m_menu.m_border.m_bottom.m_slice, camera);
        if (!face.HasValue || !left.HasValue || !rightEdge.HasValue || !top.HasValue || !bottom.HasValue)
            throw new InvalidOperationException("drawn dialog face/frame renderers missing");
        float x0 = Math.Max(face.Value.xMin, left.Value.xMax), x1 = Math.Min(face.Value.xMax, rightEdge.Value.xMin);
        float y0 = Math.Max(face.Value.yMin, bottom.Value.yMax), y1 = Math.Min(face.Value.yMax, top.Value.yMin);
        var inner = new DeckRect(x0, y0, x1 - x0, y1 - y0);
        var elements = new List<ProfilePaceGeometry.Element>();
        foreach (var entry in paceBounds)
        {
            Rect? rect = entry.Value(camera);
            elements.Add(new ProfilePaceGeometry.Element(entry.Key, rect.HasValue
                ? new DeckRect(rect.Value.x, rect.Value.y, rect.Value.width, rect.Value.height) : default));
        }
        var issues = ProfilePaceGeometry.Check(inner, elements);
        string result = "SAVE_FORM paceCheck page=" + (wizard ? "wizard" : "pace") + " screen=" + Screen.width + "x" + Screen.height
            + " ok=" + (issues.Count == 0 ? "true" : "false") + " inner=" + Rect.MinMaxRect(inner.X, inner.Y, inner.X + inner.Width, inner.Y + inner.Height)
            + " innerSource=background.middle+border.inner-edges elements=" + elements.Count
            + " issues=[" + string.Join(";", issues) + "]";
        if (result == lastPaceCheck) return;
        lastPaceCheck = result;
        NativeProfileSelector.Log(result);
    }

    private void ConfigurePaceTooltip(PegUIElement row, GrindTier tier)
    {
        try
        {
            var tooltip = ConfigureTooltip(row);
            var layer = AccessTools.Property(typeof(TooltipZone), "LayerOverride");
            row.AddEventListener(UIEventType.ROLLOVER, _ => {
                tooltip.ShowBoxTooltip(tier.Name, tier.Summary
                    + "\nDeck estimate: one specific shipped meta deck bought from the shop by a new player (median quote)."
                    + "\nIncludes recipe fee and locked Core copies; match gold/dust only, no one-time grants."
                    + "\nGrant-only card prerequisites excluded."
                    + "\n50% wins, 6 games/hour; dust valued at half its amount in gold."
                    + "\nChange later under Options > Pace; earned progress is kept.");
                layer?.SetValue(tooltip, Enum.ToObject(layer.PropertyType, row.gameObject.layer), null);
                FitTooltip(tooltip, row.gameObject.layer);
                tooltip.AnchorTooltipTo(row.gameObject, Anchor.TOP_RIGHT_XZ, Anchor.TOP_LEFT_XZ);
                TransformUtil.ConstrainToScreen(tooltip.GetTooltipObject(), row.gameObject.layer);
                RaiseTooltip(tooltip, row.gameObject.layer);
            });
            row.AddEventListener(UIEventType.ROLLOUT, _ => tooltip.HideTooltip());
        }
        catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM pace tooltip unavailable: " + error.Message); }
    }

    private void CreateSave()
    {
        if (editing) FinishEdit(UniversalInputManager.Get()?.GetInputText());
        NativeProfileSelector.Create(saveName, options);
    }

    // Hover-only: the one available meta is shown as a value; the tooltip says the others are not done yet.
    private void ConfigureMeta(PegUIElement meta)
    {
        selectedControls.Add(meta);
        meta.SetEnabled(true);
        meta.SetEnabled(UIEventType.PRESS, false); meta.SetEnabled(UIEventType.TAP, false); meta.SetEnabled(UIEventType.RELEASE, false);
        meta.SetEnabled(UIEventType.ROLLOVER, true); meta.SetEnabled(UIEventType.ROLLOUT, true);
        var tooltip = ConfigureTooltip(meta);
        var tooltipLayer = AccessTools.Property(typeof(TooltipZone), "LayerOverride");
        bool traceHover = false;
        meta.AddEventListener(UIEventType.ROLLOVER, _ => {
            traceHover = tooltipTraceCount++ < 4;
            if (traceHover) NativeProfileSelector.TraceTooltip("enter", meta, tooltip);
            tooltip.ShowBoxTooltip("Meta", ProfileCatalog.BacklogTooltip);
            // Match GameMenuSignUpButton's post-show remap without a compile-time GameLayer dependency.
            tooltipLayer.SetValue(tooltip, Enum.ToObject(tooltipLayer.PropertyType, meta.gameObject.layer), null);
            // Native social menu tooltips sit beside their row, not inside its rendered face.
            FitTooltip(tooltip, meta.gameObject.layer);
            tooltip.AnchorTooltipTo(meta.gameObject, Anchor.TOP_RIGHT_XZ, Anchor.TOP_LEFT_XZ);
            TransformUtil.ConstrainToScreen(tooltip.GetTooltipObject(), meta.gameObject.layer);
            // Owner 2026-10-28: it sat on the dialog's own depth plane, behind the dialog; bring it in front of the dialog.
            RaiseTooltip(tooltip, meta.gameObject.layer);
            if (traceHover) { NativeProfileSelector.TraceTooltip("shown", meta, tooltip); QueueTooltipTrace(meta, tooltip); }
        });
        meta.AddEventListener(UIEventType.ROLLOUT, _ => {
            tooltip.HideTooltip();
            if (traceHover) NativeProfileSelector.TraceTooltip("out", meta, tooltip);
            traceHover = false;
        });
    }

    private void QueueTooltipTrace(PegUIElement row, TooltipZone zone)
    {
        tooltipTraceRow = row; tooltipTraceZone = zone; tooltipTraceFrame = Time.frameCount;
    }

    internal void SampleTooltipTrace()
    {
        if (tooltipTraceRow == null || Time.frameCount <= tooltipTraceFrame) return;
        NativeProfileSelector.TraceTooltip("next-frame", tooltipTraceRow, tooltipTraceZone);
        tooltipTraceRow = null; tooltipTraceZone = null;
    }

    // The native tooltip spawns at its prefab size, not the fitted menu's: bring it to a readable screen height.
    private static void FitTooltip(TooltipZone zone, int layer)
    {
        var tip = zone.GetTooltipObject();
        var camera = CameraUtils.FindFirstByLayerMask(1 << layer);
        if (tip == null || camera == null) return;
        float height = DeckLayout.ScreenRect(tip, camera).height, want = Screen.height * .1f;
        if (height > 0 && (height < want * .7f || height > want * 1.5f)) tip.transform.localScale *= want / height;
    }

    // The native tooltip shares the dialog's layer and depth plane; lift it wholly in front of the dialog so nothing covers it.
    private void RaiseTooltip(TooltipZone zone, int layer)
    {
        var tip = zone.GetTooltipObject();
        var camera = CameraUtils.FindFirstByLayerMask(1 << layer);
        if (tip == null || camera == null) return;
        float lift = TooltipDepth.RaiseAbove(tip, m_menu.gameObject, camera);
        if (lift > 0) NativeProfileSelector.TraceDepth("meta tooltip lifted " + lift.ToString("F3"));
    }

    private static TooltipZone ConfigureTooltip(PegUIElement button)
    {
        var zone = button.GetComponent<TooltipZone>();
        if (zone == null || zone.tooltipPrefab == null)
        {
            var template = Box.Get()?.GetComponentsInChildren<TooltipZone>(true).FirstOrDefault(value => value.tooltipPrefab != null)
                ?? UnityEngine.Object.FindObjectsByType<TooltipZone>(FindObjectsSortMode.None).FirstOrDefault(value => value.tooltipPrefab != null);
            if (template == null) throw new InvalidOperationException("Native tooltip template is unavailable.");
            if (zone == null) zone = button.gameObject.AddComponent<TooltipZone>();
            zone.tooltipPrefab = template.tooltipPrefab;
            zone.m_tooltipDirection = template.m_tooltipDirection;
            zone.m_tooltipDirectionMobile = template.m_tooltipDirectionMobile;
        }
        zone.targetObject = button.gameObject;
        zone.tooltipDisplayLocation = button.transform;
        zone.touchTooltipLocation = button.transform;
        return zone;
    }

    // ---- Row builders -------------------------------------------------------------------------------------------

    private void Axes()
    {
        var camera = CameraUtils.FindFirstByLayerMask(1 << m_menu.gameObject.layer);
        if (camera != null) { right = camera.transform.right; up = camera.transform.up; toward = -camera.transform.forward; return; }
        var container = m_menu.m_buttonContainer.transform;   // HS menus lie in their XZ plane, seen from +Y.
        right = container.right; up = container.forward; toward = container.up;
    }

    private UIBButton Plank(string id, string semanticId, string label, UIEvent.Handler action, bool slice = true)
    {
        var row = CreateMenuButton(id, label, e => { if (!Locked()) action(e); }); rows.Add(row); owned.Add(row.gameObject);
        if (semanticId != null) controls[row] = semanticId;
        if (semanticId == "save.create" || semanticId == "save.cancel" || semanticId == "save.pace.back" || semanticId == "save.cheat.heading")
            TrackPace(semanticId, camera => ColliderScreen(row.gameObject, camera) ?? MeshScreen(row.gameObject, camera));
        if (slice) slices.Add(new Slice { Object = row.gameObject });
        if (unitPlank == null)
        {
            unitPlank = row;
            var text = ProfileFormKit.Label(row);
            if (text != null) plankFacing = text.transform.rotation;
            if (ProfileFormKit.Span(row.gameObject, up, out float min, out float max)) unitWorld = max - min;
        }
        return row;
    }

    private void Divider()
    {
        var template = m_menu.m_templateHorizontalDivider;
        if (template == null) return;
        var divider = (GameObject)GameUtils.Instantiate(template, m_menu.m_buttonContainer.gameObject);
        divider.transform.localRotation = template.transform.localRotation;
        owned.Add(divider); slices.Add(new Slice { Object = divider, Padding = m_menu.m_horizontalDividerMinPadding });
        TrackPace("divider." + slices.Count, camera => MeshScreen(divider, camera));
    }

    private GameObject Wrapper(string id)
    {
        var wrapper = new GameObject(id);
        wrapper.transform.SetParent(m_menu.m_buttonContainer.transform, false);
        wrapper.layer = m_menu.gameObject.layer;
        owned.Add(wrapper);
        return wrapper;
    }

    // Options part, cloned at its prefab-space pose, moved under a row wrapper and turned/scaled onto the menu.
    private void Adopt(GameObject part, GameObject wrapper)
    {
        part.transform.SetParent(wrapper.transform, true);
        part.transform.rotation = ProfileFormKit.Correction(plankFacing) * part.transform.rotation;
        part.transform.localPosition = Vector3.zero;
        if (kitScale > 0) part.transform.localScale *= kitScale;
        ProfileFormKit.SetLayer(part, wrapper.layer);
    }

    // First kit part sets the kit scale: its frame is this many plank heights tall; later parts keep Options proportions.
    private void Size(GameObject part, float units)
    {
        if (kitScale > 0 || unitWorld <= 0 || !ProfileFormKit.Span(part, up, out float min, out float max) || max <= min) return;
        kitScale = unitWorld * units / (max - min);
        part.transform.localScale *= kitScale;
    }

    // Kit parts keep Options proportions; one probe checkbox sets their size against the menu plank.
    private void KitScale()
    {
        var probeRow = Wrapper("ProfileKitProbe");
        probeRow.SetActive(false);
        var probe = ProfileFormKit.CloneCheckBox();
        Adopt(probe.gameObject, probeRow);
        if (probe.m_check != null) probe.m_check.SetActive(false);
        Size(probe.gameObject, .5f);
    }

    // Value field: the menu's own title plate (the native stretchable parchment strip behind "Create save"),
    // widened by its middle slice, with its title text as the value. No dropdown socket, readable size.
    private PegUIElement Plate(string id, string caption, string text, out Component label)
    {
        label = null;
        if (fieldsContent == null) Fields();
        var header = m_menu.m_header;
        var headerText = AccessTools.Field(typeof(ButtonListMenuDef), "m_headerText").GetValue(m_menu) as Component;
        if (header == null || headerText == null || unitPlank == null || unitWorld <= 0) return null;
        var wrapper = new GameObject(id);
        wrapper.transform.SetParent(fieldsContent.transform, false);
        wrapper.layer = m_menu.gameObject.layer;
        var plate = UnityEngine.Object.Instantiate(header.gameObject, wrapper.transform, true);
        plate.name = id + "Plate";
        plate.transform.localPosition = Vector3.zero;
        // Native slices count meshes on inactive branches too; hidden header art would pad the row (seen on Deck).
        var hidden = plate.GetComponentsInChildren<Transform>(true).Where(node => node != plate.transform && !node.gameObject.activeSelf
            && node.GetComponentsInChildren<MeshFilter>(true).Length > 0).ToList();
        foreach (var node in hidden) if (node != null) UnityEngine.Object.DestroyImmediate(node.gameObject);
        Transform middle = ProfileFormKit.Counterpart(header.transform, m_menu.m_headerMiddle != null ? m_menu.m_headerMiddle.transform : null, plate.transform);
        Transform textNode = ProfileFormKit.Counterpart(header.transform, headerText.transform, plate.transform);
        if (textNode == null)
        {
            var copy = UnityEngine.Object.Instantiate(headerText.gameObject, plate.transform, true);
            copy.transform.position = plate.transform.position + (headerText.transform.position - header.transform.position);
            textNode = copy.transform;
        }
        label = textNode.GetComponent(ProfileFormKit.TextType);
        if (label == null) { UnityEngine.Object.Destroy(wrapper); owned.Remove(wrapper); return null; }
        ProfileFormKit.SetLayer(plate, wrapper.layer);
        if (ProfileFormKit.Span(plate, up, out float h0, out float h1, false, Strip) && h1 > h0) plate.transform.localScale *= unitWorld * .8f / (h1 - h0);
        ProfileFormKit.SetText(label, text);
        // Caption sits centred just above its field in the same row; the row is padded to make room for it.
        var title = Caption(wrapper.transform, caption, LabelColor, .8f);
        var field = wrapper.AddComponent<BoxCollider>();
        var hit = wrapper.AddComponent<PegUIElement>();
        var textComponent = label;
        float plankWidth = ProfileFormKit.Span(unitPlank.gameObject, right, out float p0, out float p1) ? p1 - p0 : unitWorld * 4;
        // Stretch while active (native slice maths reads live bounds): middle slice to 1.35 plank widths overall.
        beforeLayout.Add(() => {
            if (middle != null && ProfileFormKit.Span(plate, right, out float t0, out float t1, false, Strip) && ProfileFormKit.Span(middle.gameObject, right, out float m0, out float m1)
                && m1 > m0)
            {
                float target = plankWidth * 1.35f, grown = (m1 - m0) + target - (t1 - t0);
                if (grown > 0)
                {
                    var scale = middle.localScale; scale.x *= grown / (m1 - m0); middle.localScale = scale;
                    plate.GetComponent<MultiSliceElement>()?.UpdateSlices();
                    ProfileFormKit.ScaleRect(textComponent, "Width", target / Math.Max(.0001f, t1 - t0));
                }
            }
            if (ProfileFormKit.Span(plate, right, out float x0, out float x1, false, Strip) && ProfileFormKit.Span(plate, up, out float y0, out float y1, false, Strip))
            {
                Vector3 depth = toward * Vector3.Dot(textComponent.transform.position, toward);
                textComponent.transform.position = right * ((x0 + x1) / 2) + up * ((y0 + y1) / 2) + depth;
                FitCollider(wrapper, field, x0, x1, y0, y1);
                if (title != null)
                    title.transform.position = right * ((x0 + x1) / 2) + up * ((y0 + y1) / 2 + (y1 - y0) * 1.05f) + depth;
            }
        });
        // The caption's rendered text quad counts in the row bounds, so the row needs only a little extra room.
        fieldRows.Add(new KeyValuePair<GameObject, GameObject>(wrapper, plate));
        return hit;
    }

    // Name and meta rows as one slice. Native slice bounds over the cloned title plate reserved about twice the
    // drawn row (Deck and 4K), so the visible rows are excluded from native measuring and laid out here from the
    // plate's drawn height; an invisible mesh (filter only, no renderer) gives the container the exact block size.
    private void Fields()
    {
        var fields = Wrapper("ProfileFields");
        fieldsContent = new GameObject("ProfileFieldsContent");
        fieldsContent.transform.SetParent(fields.transform, false);
        fieldsContent.layer = fields.layer;
        unsized.Add(fieldsContent);
        var sizer = fields.AddComponent<MeshFilter>();
        // One bounds mesh for the menu's lifetime (refilled per layout), not one leaked per rebuild.
        if (fieldsMesh == null) fieldsMesh = new Mesh { name = "SaveFieldsBounds" };
        sizer.sharedMesh = fieldsMesh;
        slices.Add(new Slice { Object = fields });
        fieldsLayout = () => {
            float top = Vector3.Dot(fields.transform.position, up), cursor = top, left = float.MaxValue, edge = float.MinValue;
            float depth = Vector3.Dot(fields.transform.position, toward);
            foreach (var pair in fieldRows)
            {
                GameObject row = pair.Key, plate = pair.Value;
                if (row == null || plate == null || !ProfileFormKit.Span(plate, up, out float y0, out float y1, false, Strip)
                    || !ProfileFormKit.Span(plate, right, out float x0, out float x1, false, Strip)) continue;
                float height = y1 - y0, captionRoom = height * 1.05f, gap = height * .35f;
                if (cursor < top) cursor -= gap;
                float centerY = cursor - captionRoom - height / 2;
                row.transform.position += up * (centerY - (y0 + y1) / 2) + right * (Vector3.Dot(fields.transform.position, right) - (x0 + x1) / 2);
                cursor = centerY - height / 2;
                left = Math.Min(left, Vector3.Dot(fields.transform.position, right) - (x1 - x0) / 2);
                edge = Math.Max(edge, Vector3.Dot(fields.transform.position, right) + (x1 - x0) / 2);
            }
            if (left > edge) return;
            // Bounds mesh in the wrapper's own space: the block from the first caption's top to the last plate's bottom.
            var corners = new List<Vector3>();
            foreach (float x in new[] { left, edge })
                foreach (float y in new[] { top, cursor })
                    foreach (float z in new[] { depth - unitWorld * .05f, depth + unitWorld * .05f })
                        corners.Add(fields.transform.InverseTransformPoint(right * x + up * y + toward * z));
            var mesh = sizer.sharedMesh;
            mesh.Clear();
            mesh.vertices = corners.ToArray();
            mesh.RecalculateBounds();
        };
    }

    // The title strip's own art (slimTitleHeader_L/Center/R); its pPlane shadow quads are larger than what reads as the field.
    private static bool Strip(MeshFilter filter) => filter.name.StartsWith("slimTitleHeader", StringComparison.Ordinal);

    private void FitCollider(GameObject wrapper, BoxCollider box, float x0, float x1, float y0, float y1)
    {
        float depth = Vector3.Dot(wrapper.transform.position, toward);
        Vector3 center = right * ((x0 + x1) / 2) + up * ((y0 + y1) / 2) + toward * depth;
        box.center = wrapper.transform.InverseTransformPoint(center);
        box.size = Abs(wrapper.transform.InverseTransformVector(right * (x1 - x0))) + Abs(wrapper.transform.InverseTransformVector(up * (y1 - y0)))
            + Abs(wrapper.transform.InverseTransformVector(toward * unitWorld * .2f));
    }

    private static Vector3 Abs(Vector3 value) => new Vector3(Math.Abs(value.x), Math.Abs(value.y), Math.Abs(value.z));

    // Caption in the menu plank's own font, recoloured for the dialog background.
    private Component Caption(Transform parent, string text, Color color, float scale)
    {
        var source = unitPlank == null ? null : ProfileFormKit.Label(unitPlank);
        if (source == null) return null;
        var copy = UnityEngine.Object.Instantiate(source.gameObject, parent, true);
        copy.transform.localPosition = Vector3.zero;
        copy.transform.localScale *= scale;
        ProfileFormKit.SetLayer(copy, parent.gameObject.layer);
        var label = copy.GetComponent(ProfileFormKit.TextType);
        ProfileFormKit.ScaleRect(label, "Width", 1.8f); ProfileFormKit.ScaleRect(label, "Height", .5f);
        ProfileFormKit.SetText(label, text); ProfileFormKit.SetColor(label, color);
        return label;
    }

    // Fallback-only caption row (text-rect sized).
    private Component Label(string id, string text, Color color, float scale)
    {
        var wrapper = Wrapper(id);
        var label = Caption(wrapper.transform, text, color, scale);
        if (label == null) return null;
        slices.Add(new Slice { Object = wrapper, Text = true });
        TrackPace(id, camera => TextScreen(label, camera));
        return label;
    }

    // Native slices centre a row on its meshes; rows with hanging text are re-centred on what is drawn, on the
    // centre of the action pair. Idempotent: the container resets slice positions on every layout.
    private void CenterRow(GameObject row, Func<KeyValuePair<float, float>> extent)
    {
        afterLayout.Add(() => {
            var span = extent();
            if (span.Key > span.Value) return;
            float middle = Vector3.Dot(row.transform.parent.position, right);
            if (unitPlank != null && ProfileFormKit.Span(unitPlank.transform.parent.gameObject, right, out float a0, out float a1)) middle = (a0 + a1) / 2;
            row.transform.position += right * (middle - (span.Key + span.Value) / 2);
        });
    }

    private void CheatsHeader(bool kit)
    {
        var source = ProfileFormKit.Label(unitPlank);
        if (source == null)
        {
            var plank = Plank("ProfileCheats", "save.cheat.heading", (cheatsOpen ? "▾ " : "▸ ") + CheatTitle, _ => { cheatsOpen = !cheatsOpen; Build(); });
            if (cheatsOpen) selectedControls.Add(plank);
            return;
        }
        var wrapper = Wrapper("ProfileCheats");
        GameObject arrow = kit ? ProfileFormKit.CloneArrow() : null;
        string title = CheatTitle;
        if (arrow == null) title = (cheatsOpen ? "▾ " : "▸ ") + title;
        var copy = UnityEngine.Object.Instantiate(source.gameObject, wrapper.transform, true);
        copy.transform.localPosition = Vector3.zero;
        ProfileFormKit.SetLayer(copy, wrapper.layer);
        var label = copy.GetComponent(ProfileFormKit.TextType);
        ProfileFormKit.ScaleRect(label, "Width", 1.4f);
        ProfileFormKit.SetText(label, title); ProfileFormKit.SetColor(label, LabelColor);
        if (arrow != null)
        {
            Adopt(arrow, wrapper);
            foreach (var collider in arrow.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var peg in arrow.GetComponentsInChildren<PegUIElement>(true)) peg.enabled = false;
            // The dropdown arrow points down (open); collapsed it turns a quarter toward the reading direction.
            if (!cheatsOpen)
            {
                float angle = Vector3.Dot(Quaternion.AngleAxis(90, toward) * -up, right) > 0 ? 90 : -90;
                arrow.transform.rotation = Quaternion.AngleAxis(angle, toward) * arrow.transform.rotation;
            }
        }
        var box = wrapper.AddComponent<BoxCollider>();
        var header = wrapper.AddComponent<PegUIElement>();
        header.AddEventListener(UIEventType.RELEASE, _ => { if (Locked()) return; cheatsOpen = !cheatsOpen; Build(); });
        TooltipZone note = null;
        try { note = ConfigureTooltip(header); } catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM cheat tooltip unavailable: " + error.Message); }
        var noteLayer = AccessTools.Property(typeof(TooltipZone), "LayerOverride");
        header.AddEventListener(UIEventType.ROLLOVER, _ => {
            ProfileFormKit.SetColor(label, Color.white);
            if (note == null) return;
            note.ShowBoxTooltip(CheatTooltipHeader, CheatNote);
            noteLayer?.SetValue(note, Enum.ToObject(noteLayer.PropertyType, header.gameObject.layer), null);
            FitTooltip(note, header.gameObject.layer);
            note.AnchorTooltipTo(header.gameObject, Anchor.TOP_RIGHT_XZ, Anchor.TOP_LEFT_XZ);
            TransformUtil.ConstrainToScreen(note.GetTooltipObject(), header.gameObject.layer);
        });
        header.AddEventListener(UIEventType.ROLLOUT, _ => { ProfileFormKit.SetColor(label, LabelColor); note?.HideTooltip(); });
        controls[header] = "save.cheat.heading";
        TrackPace("sandbox.heading", camera => MeshScreen(wrapper, camera));
        if (cheatsOpen) selectedControls.Add(header);
        slices.Add(new Slice { Object = wrapper, Text = arrow == null });
        if (arrow != null)
            CenterRow(wrapper, () => {
                float left = float.MaxValue, edge = float.MinValue;
                if (ProfileFormKit.Span(arrow, right, out float a0, out float a1)) { left = a0; edge = a1; }
                var glyphs = ProfileFormKit.GlyphBounds(label);
                if (glyphs.HasValue) edge = Math.Max(edge, Corners(glyphs.Value).Max(point => Vector3.Dot(point, right)));
                return new KeyValuePair<float, float>(left, edge);
            });
        // Arrow and hit area follow the rendered title, known only once the menu is active.
        beforeLayout.Add(() => {
            ProfileFormKit.UpdateNow(label);
            Bounds? glyphs = ProfileFormKit.GlyphBounds(label) ?? ProfileFormKit.RectBounds(label);
            if (!glyphs.HasValue) return;
            Bounds area = glyphs.Value;
            float left = Corners(area).Min(point => Vector3.Dot(point, right));
            float middle = Vector3.Dot(area.center, up);
            if (arrow != null && ProfileFormKit.Span(arrow, right, out float a0, out float a1))
            {
                float width = a1 - a0;
                ProfileFormKit.Center(arrow, right, up, left - width * .9f, middle);
                float depth = Vector3.Dot(label.transform.position, toward);
                if (ProfileFormKit.Span(arrow, toward, out float d0, out float d1)) arrow.transform.position += toward * (depth - (d0 + d1) / 2);
                foreach (var renderer in arrow.GetComponentsInChildren<Renderer>()) area.Encapsulate(renderer.bounds);
            }
            box.center = wrapper.transform.InverseTransformPoint(area.center);
            Vector3 size = wrapper.transform.InverseTransformVector(area.size);
            box.size = new Vector3(Math.Abs(size.x), Math.Abs(size.y), Math.Abs(size.z)) + Vector3.one * .01f;
        });
    }

    // Native LayoutMenuBackground sizes the board from mesh bounds only; caption slices are sized by their text rects,
    // so a form stack can run past the board's bottom. Grow the board downward until it covers every row.
    private void CoverContent(Camera camera)
    {
        var frame = DeckLayout.ScreenRect(m_menu.m_background.gameObject, camera);
        Rect? content = null;
        foreach (var slice in slices)
        {
            if (slice.Object == null) continue;
            Rect one = DeckLayout.ScreenRect(slice.Object, camera);
            if (one.width <= 0 && one.height <= 0) continue;
            content = content.HasValue ? Rect.MinMaxRect(Math.Min(content.Value.xMin, one.xMin), Math.Min(content.Value.yMin, one.yMin),
                Math.Max(content.Value.xMax, one.xMax), Math.Max(content.Value.yMax, one.yMax)) : one;
        }
        if (!content.HasValue || frame.height <= 0 || unitPlank == null) return;
        float unit = DeckLayout.ScreenRect(unitPlank.gameObject, camera).height / unitScale;
        float missing = frame.yMin - (content.Value.yMin - unit * .45f);   // screen y grows upward
        if (missing <= 0) return;
        Vector3 at = camera.WorldToScreenPoint(m_menu.m_background.transform.position);
        float worldPerPixel = (camera.ScreenToWorldPoint(at + Vector3.up) - camera.ScreenToWorldPoint(at)).magnitude;
        OrientedBounds bounds = TransformUtil.ComputeOrientedWorldBounds(m_menu.m_buttonContainer.gameObject, default, default, null, true, true, true);
        float width = bounds.Extents[0].magnitude * 2f, height = bounds.Extents[2].magnitude * 2f + missing * worldPerPixel;
        m_menu.m_background.SetSize(width, height);
        m_menu.m_border.SetSize(width, height);
        // Keep the board's top (and the header on it) where it was: only the bottom grows.
        float shift = frame.yMax - DeckLayout.ScreenRect(m_menu.m_background.gameObject, camera).yMax;
        Vector3 move = camera.ScreenToWorldPoint(at + Vector3.up * shift) - camera.ScreenToWorldPoint(at);
        m_menu.m_background.transform.position += move; m_menu.m_border.transform.position += move;
    }

    private static IEnumerable<Vector3> Corners(Bounds bounds)
    {
        for (int corner = 0; corner < 8; corner++)
            yield return new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x, (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
    }

    private PegUIElement Toggle(bool kit, string id, string semanticId, string label, Func<bool> read, Action<bool> write, bool rebuild = false)
    {
        if (!kit)
        {
            UIBButton row = null;
            row = Plank(id, semanticId, (read() ? "☑ " : "☐ ") + label, _ => {
                try { write(!read()); }
                catch (Exception error) { NativeProfileSelector.ShowError(error.Message); return; }
                if (rebuild) { Build(); return; }
                row.SetText((read() ? "☑ " : "☐ ") + label);
                if (read()) selectedControls.Add(row); else selectedControls.Remove(row);
            });
            TrackPace(semanticId, camera => MeshScreen(row.gameObject, camera));
            if (read()) selectedControls.Add(row);
            return row;
        }
        var box = ProfileFormKit.CloneCheckBox();
        box.name = id;
        // One group for all cheats at a fixed pitch: per-row slices took the tall Options label rect as row height.
        if (cheatList == null)
        {
            cheatList = Wrapper("ProfileCheatList");
            slices.Add(new Slice { Object = cheatList });
            var list = cheatList;
            // Centre the block on what is drawn (boxes plus label glyphs), not on the boxes alone.
            CenterRow(list, () => {
                if (!ProfileFormKit.Span(list, right, out float left, out float edge)) return new KeyValuePair<float, float>(1, 0);
                foreach (var row in list.GetComponentsInChildren<CheckBox>())
                {
                    var text = ProfileFormKit.Label(row);
                    ProfileFormKit.UpdateNow(text);
                    var glyphs = ProfileFormKit.GlyphBounds(text);
                    if (glyphs.HasValue) edge = Math.Max(edge, Corners(glyphs.Value).Max(point => Vector3.Dot(point, right)));
                }
                return new KeyValuePair<float, float>(left, edge);
            });
        }
        Adopt(box.gameObject, cheatList); Size(box.gameObject, .5f);
        if (cheatPitch <= 0)
        {
            bool checkShown = box.m_check != null && box.m_check.activeSelf;
            if (box.m_check != null) box.m_check.SetActive(false);
            if (ProfileFormKit.Span(box.gameObject, up, out float y0, out float y1)) cheatPitch = (y1 - y0) * 1.45f;
            if (box.m_check != null) box.m_check.SetActive(checkShown);
            if (cheatPitch <= 0) cheatPitch = unitWorld * .7f;
        }
        box.transform.position -= up * (cheatPitch * cheatRows++);
        box.SetButtonText(label);
        ProfileFormKit.SetColor(ProfileFormKit.Label(box), LabelColor);
        box.SetChecked(read());
        box.AddEventListener(UIEventType.RELEASE, _ => {
            // The native checkbox has already toggled itself: a locked-out click is undone, not ignored.
            if (Locked()) { box.SetChecked(read()); return; }
            try { write(box.IsChecked()); }
            catch (Exception error) { box.SetChecked(read()); NativeProfileSelector.ShowError(error.Message); return; }
            if (rebuild) { Build(); return; }
            if (box.IsChecked()) selectedControls.Add(box); else selectedControls.Remove(box);
        });
        controls[box] = semanticId;
        TrackPace(semanticId, camera => MeshScreen(box.gameObject, camera));
        if (read()) selectedControls.Add(box);
        return box;
    }

    // Primary/secondary action planks side by side, primary first, as in native two-button dialogs.
    private void Pair(string id, UIBButton primary, UIBButton secondary)
    {
        var wrapper = Wrapper(id);
        // Only a pair that sets the dialog's plank unit (the form's action pair) rescales it; under full-size rows it stays 1.
        if (ReferenceEquals(unitPlank, primary) || ReferenceEquals(unitPlank, secondary)) unitScale = PairScale;
        float center = Vector3.Dot(wrapper.transform.position, right), middle = Vector3.Dot(wrapper.transform.position, up);
        float[] widths = new float[2];
        var planks = new[] { primary, secondary };
        for (int index = 0; index < 2; index++)
        {
            planks[index].transform.SetParent(wrapper.transform, true);
            planks[index].transform.localScale *= PairScale;
            widths[index] = ProfileFormKit.Span(planks[index].gameObject, right, out float min, out float max) ? max - min : unitWorld * 3;
        }
        float gap = unitWorld * .2f;
        ProfileFormKit.Center(primary.gameObject, right, up, center - gap / 2 - widths[0] / 2, middle);
        ProfileFormKit.Center(secondary.gameObject, right, up, center + gap / 2 + widths[1] / 2, middle);
        slices.Add(new Slice { Object = wrapper });
    }

    // Current save: the native Options check glyph on the plank face, just left of its name (ProfileMenuGeometry.CheckCenter).
    private void MarkCurrent(UIBButton row, bool kit, string label)
    {
        // Everything is measured and decided on the unparented clone (it lives under the kit holder), so a clone that is
        // then discarded can never inflate this row's Span; it is parented only once it is known to fit.
        GameObject glyph = kit ? ProfileFormKit.CloneCheckGlyph() : null;
        var face = row.m_RootObject != null ? row.m_RootObject : row.gameObject;
        if (glyph == null || !ProfileFormKit.Span(row.gameObject, right, out float x0, out float x1)
            || !ProfileFormKit.Span(row.gameObject, up, out float y0, out float y1) || !ProfileFormKit.Span(row.gameObject, toward, out _, out float z1))
        {
            Discard(glyph);
            row.SetText(ProfileMenuGeometry.FallbackLabel(label));
            return;
        }
        glyph.transform.rotation = ProfileFormKit.Correction(plankFacing) * glyph.transform.rotation;
        ProfileFormKit.SetLayer(glyph, row.gameObject.layer);
        if (ProfileFormKit.Span(glyph, up, out float g0, out float g1) && g1 > g0)
            glyph.transform.localScale *= (y1 - y0) * ProfileMenuGeometry.CheckHeightShare / (g1 - g0);
        float width = ProfileFormKit.Span(glyph, right, out float w0, out float w1) ? w1 - w0 : (y1 - y0) * ProfileMenuGeometry.CheckHeightShare;
        // The name's rendered extent along the row axis; unmeasured names are estimated from their length.
        float? nameMin = null, nameMax = null;
        var text = ProfileFormKit.Label(row);
        ProfileFormKit.UpdateNow(text);
        var glyphs = ProfileFormKit.GlyphBounds(text);
        if (glyphs.HasValue)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            var b = glyphs.Value;
            for (int corner = 0; corner < 8; corner++)
            {
                float along = Vector3.Dot(new Vector3((corner & 1) == 0 ? b.min.x : b.max.x, (corner & 2) == 0 ? b.min.y : b.max.y,
                    (corner & 4) == 0 ? b.min.z : b.max.z), right);
                lo = Math.Min(lo, along); hi = Math.Max(hi, along);
            }
            nameMin = lo; nameMax = hi;
        }
        var plan = ProfileMenuGeometry.PlanCheck(x0, x1, nameMin, nameMax, width, label.Length);
        if (!plan.HasValue)
        {
            Discard(glyph);
            row.SetText(ProfileMenuGeometry.FallbackLabel(label));
            return;
        }
        glyph.transform.SetParent(face.transform, true);
        ProfileFormKit.Center(glyph, right, up, plan.Value.Center, (y0 + y1) / 2);
        if (ProfileFormKit.Span(glyph, toward, out float d0, out _)) glyph.transform.position += toward * (z1 - d0 + .001f);
        if (plan.Value.NameShift > 0 && text != null) text.transform.position += right * plan.Value.NameShift;
    }

    private static void Discard(GameObject glyph)
    {
        if (glyph == null) return;
        glyph.SetActive(false); // Destroy is deferred to end of frame
        UnityEngine.Object.Destroy(glyph);
    }

    // ---- Name editing: retail deck-rename pattern (hide the label, native input over its rect, no background) ----

    private void EditName()
    {
        var input = UniversalInputManager.Get();
        if (editing || loading || input == null || nameText == null) return;
        var camera = CameraUtils.FindFirstByLayerMask(1 << nameText.gameObject.layer);
        ProfileFormKit.UpdateNow(nameText);
        Bounds? frame = ProfileFormKit.RectBounds(nameText), glyphs = ProfileFormKit.GlyphBounds(nameText);
        Rect? frameRect = frame.HasValue ? ProfileFormKit.ScreenRect(frame.Value, camera) : null;
        Rect? glyphRect = glyphs.HasValue ? ProfileFormKit.ScreenRect(glyphs.Value, camera) : null;
        if (!frameRect.HasValue) return;
        DeckRect rect = ProfileMenuGeometry.NameInput(Deck(frameRect.Value), glyphRect.HasValue ? Deck(glyphRect.Value) : (DeckRect?)null,
            Screen.width, Screen.height);
        var parms = new UniversalInputManager.TextInputParams {
            m_owner = gameObject, m_text = saveName, m_maxCharacters = 32, m_showBackground = false,
            m_rect = new Rect(rect.X, rect.Y, rect.Width, rect.Height), m_color = nameColor ?? ProfileFormKit.GetColor(nameText),
            m_updatedCallback = value => pendingName = value,
            m_completedCallback = value => FinishEdit(value, false),
            m_canceledCallback = (_, __) => FinishEdit(null, false),
            m_unfocusedCallback = () => FinishEdit(pendingName)
        };
        TextInputStyle(parms, nameText);
        editing = true; pendingName = saveName;
        ProfileFormKit.SetText(nameText, "");
        input.UseTextInput(parms);
        if (input.IsTextInputActive()) { nameRetry.Reset(); return; }
        // Native input field not ready yet (UseTextInput returned without taking input): put the name back, try again soon.
        editing = false;
        ProfileFormKit.SetText(nameText, saveName);
        if (nameRetry.Again()) StartCoroutine(RetryEditName());
        else { nameRetry.Reset(); NativeProfileSelector.Log("SAVE_FORM name input unavailable"); }
    }

    private IEnumerator RetryEditName()
    {
        var label = nameText;
        yield return new WaitForSecondsRealtime(.1f);
        if (this != null && IsShown() && wizard && !loading && !editing && ReferenceEquals(label, nameText)) EditName();
        else nameRetry.Reset();
    }

    private static DeckRect Deck(Rect value) => new DeckRect(value.x, value.y, value.width, value.height);

    // Font and alignment fields use types outside the compile references; the label's own font keeps the look.
    private static void TextInputStyle(UniversalInputManager.TextInputParams parms, Component text)
    {
        object font = ProfileFormKit.Font(text);
        if (font != null) AccessTools.Field(typeof(UniversalInputManager.TextInputParams), "m_font")?.SetValue(parms, font);
        var alignment = AccessTools.Field(typeof(UniversalInputManager.TextInputParams), "m_alignment");
        Type anchor = alignment == null ? null : Nullable.GetUnderlyingType(alignment.FieldType);
        string mode = ProfileFormKit.Alignment(text) ?? "";
        if (anchor != null) alignment.SetValue(parms, Enum.ToObject(anchor, mode.Contains("Right") ? 5 : mode.Contains("Left") ? 3 : 4));
    }

    // From the input's own complete/cancel callbacks the native input is already closing (cancelInput false).
    private void FinishEdit(string value, bool cancelInput = true)
    {
        if (!editing) return;
        editing = false;
        string name = value?.Trim();
        // Name rules are enforced at Create (ProfileCatalog.Create); an emptied field keeps the previous name.
        if (!string.IsNullOrEmpty(name)) saveName = name.Length > 32 ? name.Substring(0, 32) : name;
        if (cancelInput) UniversalInputManager.Get()?.CancelTextInput(gameObject);
        ProfileFormKit.SetText(nameText, saveName);
    }

    // ---- Native menu plumbing -----------------------------------------------------------------------------------

    private void Header(string text)
    {
        object header = AccessTools.Field(typeof(ButtonListMenuDef), "m_headerText").GetValue(m_menu);
        AccessTools.Property(header.GetType(), "Text").SetValue(header, text, null);
    }

    internal void ShowLoading()
    {
        loading = true; Header("Loading save…");
        foreach (var row in rows) row.SetEnabled(false);
        foreach (var control in controls.Keys) if (control != null) control.SetEnabled(false);
    }

    // A stray click beside the form must not discard it; the startup picker has nowhere to go back to. Beside the Pace page it
    // means Back, so Options comes back too.
    protected override void OnBlockerRelease(UIEvent e)
    {
        if (startup || wizard || loading) return;
        if (pacePage) { LeavePace(); return; }
        base.OnBlockerRelease(e);
    }

    public override void Show(bool playSound = true)
    {
        if (ownsNativeDialog) return;
        int before = DeckExperience.GameDialogCount;
        try { base.Show(playSound); ownsNativeDialog = true; }
        catch
        {
            if (DeckExperience.GameDialogCount > before) { ownsNativeDialog = true; Hide(); }
            else if (gameObject != null) gameObject.SetActive(false);
            throw;
        }
    }
    public override void Hide()
    {
        if (editing) FinishEdit(UniversalInputManager.Get()?.GetInputText());
        if (!ownsNativeDialog) { if (gameObject != null) gameObject.SetActive(false); return; }
        try { base.Hide(); } finally { ownsNativeDialog = false; }
    }
    protected override void OnDestroy()
    {
        Hide();
        if (fieldsMesh != null) UnityEngine.Object.Destroy(fieldsMesh);
        foreach (var mesh in paceMeshes) UnityEngine.Object.Destroy(mesh);
        base.OnDestroy();
    }

    protected override void LayoutMenu()
    {
        foreach (var row in rows) if (row != null) row.gameObject.SetActive(true);
        var container = m_menu.m_buttonContainer;
        container.ClearSlices();
        foreach (var item in ignoring) container.m_ignore.Remove(item);
        ignoring.Clear();
        foreach (var item in unsized) if (item != null) { container.m_ignore.Add(item); ignoring.Add(item); }
        foreach (var slice in slices)
        {
            if (slice.Object == null) continue;
            slice.Object.SetActive(true);
            container.AddSlice(slice.Object, slice.Padding, slice.Both ? slice.Padding : Vector3.zero, false, true, false, slice.Text);
        }
        foreach (var hook in beforeLayout)
        {
            try { hook(); } catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM layout hook failed: " + error.Message); }
        }
        try { fieldsLayout?.Invoke(); } catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM fields layout failed: " + error.Message); }
        container.UpdateSlices();
        foreach (var hook in afterLayout)
        {
            try { hook(); } catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM after-layout hook failed: " + error.Message); }
        }
        LayoutMenuBackground();
        var camera = CameraUtils.FindFirstByLayerMask(1 << m_menu.gameObject.layer);
        if (camera == null) return;
        try { CoverContent(camera); } catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM cover failed: " + error.Message); }
        Rect bounds = DeckLayout.ScreenRect(m_menu.gameObject, camera);
        if (bounds.height <= 0 || bounds.width <= 0) return;
        float unit = unitPlank == null ? bounds.height / 4 : DeckLayout.ScreenRect(unitPlank.gameObject, camera).height / unitScale;
        ProfileMenuGeometry.Fit fit = ProfileMenuGeometry.Place(Screen.width, Screen.height, unit, bounds.width, bounds.height);
        m_menu.transform.localScale *= fit.Scale;
        bounds = DeckLayout.ScreenRect(m_menu.gameObject, camera);
        Vector3 anchor = camera.WorldToScreenPoint(m_menu.transform.position);
        var from = new Vector3(bounds.center.x, bounds.center.y, anchor.z);
        var to = new Vector3(fit.CenterX, fit.CenterY, anchor.z);
        m_menu.transform.position += camera.ScreenToWorldPoint(to) - camera.ScreenToWorldPoint(from);
        paceCheckFrame = Time.frameCount + 3; // Every layout postpones sampling until the last pass and text meshes settle.
        if (layoutLogged) return;
        layoutLogged = true;
        bounds = DeckLayout.ScreenRect(m_menu.gameObject, camera);
        NativeProfileSelector.Log("SAVE_FORM layout " + Screen.width + "x" + Screen.height + " wizard=" + wizard + " kit=" + ProfileFormKit.State
            + " scale=" + fit.Scale.ToString("F3") + " bounds=" + bounds);
    }
}
