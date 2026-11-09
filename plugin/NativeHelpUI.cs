using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// SoloStone Help (owner 2026-10-25): a native button above the bottom bar's menu (gear) on the HUB, where retail puts
// its own extra bar control (BnetBar.UpdateLayout: Skip Tutorial, TOP_RIGHT of the bar). There is no Help row in the
// native Game Menu (owner 2026-10-28: Help lives only on the HUB). Feats opens from the HUB drawer (NativeAchievements).
// Help opens one native AlertPopup (NativeHelpText). The HUB button is the Game Menu's own button template
// (ButtonListMenuDef.m_templateButton, the UberText plank Options/Quit use). Every path fails closed: a failure hides
// the HUB button for the session and nothing throws into Update or a native callback.
internal static class NativeHelpUI
{
    // ButtonListMenu.m_menuDefPrefab of the pinned client (native-bindings checks the literal).
    internal const string MenuDefPrefab = "ButtonListMenuDef.prefab:1ab57b5c429373a4b8b4e0c0c706ca3e";
    // Screen-relative size and gap; tuned live at 1280x720 and 4K.
    internal const float HeightShare = 0.05f, GapShare = 0.012f;
    private static readonly FieldInfo ButtonText = AccessTools.Field(typeof(UIBButton), "m_ButtonText");
    private static readonly FieldInfo Clock = AccessTools.Field(typeof(BnetBar), "m_currentTime");
    private static ManualLogSource log;
    // Launch lane (OfflineProbe sets it); -1 until known, which keeps the browser closed.
    internal static int Lane = -1;
    private static UIBButton homeButton;
    private static Camera camera;
    private static bool disabled;

    internal static UIBButton HomeButton => homeButton != null && homeButton.gameObject.activeInHierarchy ? homeButton : null;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        // The Help popup is one AlertPopup: widen its text and grow the whole popup to the screen (owner 2026-10-28: the
        // text was tiny with plenty of room around it). Both hooks only touch the Help popup, by its id.
        MethodInfo layout = AccessTools.Method(typeof(AlertPopup), "UpdateLayout") ?? throw new MissingMethodException("AlertPopup", "UpdateLayout");
        harmony.Patch(layout, prefix: new HarmonyMethod(typeof(NativeHelpUI), nameof(WidenHelpText)),
            postfix: new HarmonyMethod(typeof(NativeHelpUI), nameof(FitHelpPopup)));
    }

    // The Help text is wrapped at several widths and the popup is scaled to the screen at the width that allows the largest
    // font (owner 2026-10-28: the text was tiny with plenty of room around it). Both hooks only touch the Help popup, by id.
    internal static readonly float[] HelpWidenings = { 1f, 1.15f, 1.3f, 1.5f, 1.7f, 2f };
    private static readonly FieldInfo AlertText = AccessTools.Field(typeof(AlertPopup), "m_alertText");
    private static readonly FieldInfo OriginalScale = AccessTools.Field(typeof(DialogBase), "m_originalScale");
    private static readonly MethodInfo AlertLayout = AccessTools.Method(typeof(AlertPopup), "UpdateLayout");
    private static AlertPopup widened, fitted;
    private static float helpBaseWidth;
    private static bool fitting;

    // The #57 welcome popup is the same kind of text popup and gets the same fit.
    private static bool IsHelp(AlertPopup popup) => popup != null
        && (popup.GetInfo()?.m_id == NativeHelpText.PopupId || popup.GetInfo()?.m_id == NativeWelcomeText.PopupId);

    // Prefix: remember the native text width before the first layout.
    private static void WidenHelpText(AlertPopup __instance)
    {
        try
        {
            if (fitting || !IsHelp(__instance) || ReferenceEquals(widened, __instance)) return;
            widened = __instance;
            object text = AlertText?.GetValue(__instance);
            PropertyInfo width = text == null ? null : AccessTools.Property(text.GetType(), "Width");
            helpBaseWidth = width == null ? 0 : (float)width.GetValue(text, null);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_HELP width: " + error.Message); }
    }

    private static bool SetHelpWidth(AlertPopup popup, float factor)
    {
        object text = AlertText?.GetValue(popup);
        if (text == null || helpBaseWidth <= 0) return false;
        Type type = text.GetType();
        AccessTools.Property(type, "Width")?.SetValue(text, helpBaseWidth * factor, null);
        AccessTools.Method(type, "UpdateNow", new[] { typeof(bool) })?.Invoke(text, new object[] { false });
        AlertLayout.Invoke(popup, null);
        return true;
    }

    private static Rect HelpRect(AlertPopup popup, Camera view)
    {
        Rect rect = DeckLayout.ScreenRect(popup.m_body.gameObject, view);
        foreach (GameObject part in new[] { popup.m_header.m_container.gameObject, popup.m_buttonContainer.gameObject })
        {
            Rect one = DeckLayout.ScreenRect(part, view);
            if (one.width <= 0) continue;
            rect = rect.width <= 0 ? one : Rect.MinMaxRect(Mathf.Min(rect.xMin, one.xMin), Mathf.Min(rect.yMin, one.yMin),
                Mathf.Max(rect.xMax, one.xMax), Mathf.Max(rect.yMax, one.yMax));
        }
        return rect;
    }

    private static void FitHelpPopup(AlertPopup __instance)
    {
        if (fitting || !IsHelp(__instance) || ReferenceEquals(fitted, __instance)) return;
        fitted = __instance;
        fitting = true;
        try
        {
            Camera view = CameraUtils.FindFirstByLayerMask(1 << __instance.gameObject.layer);
            if (view == null || helpBaseWidth <= 0) return;
            HandheldPolicy.HelpFit(DeckExperience.LayoutEnabled, out float fitShare, out float fitMax);
            float best = 0, bestScale = 0; Rect bestRect = Rect.zero;
            foreach (float factor in HelpWidenings)
            {
                if (!SetHelpWidth(__instance, factor)) return;
                Rect rect = HelpRect(__instance, view);
                float scale = HandheldPolicy.FitScale(rect.width, rect.height, Screen.width, Screen.height, fitShare, fitMax);
                if (rect.width > 0 && scale > bestScale) { bestScale = scale; best = factor; bestRect = rect; }
            }
            if (best <= 0) { SetHelpWidth(__instance, 1f); return; }
            SetHelpWidth(__instance, best);
            if (bestScale <= 1.01f) return;
            // The show animation scales to m_originalScale, so the factor must land there as well as on the transform.
            if (OriginalScale != null) OriginalScale.SetValue(__instance, (Vector3)OriginalScale.GetValue(__instance) * bestScale);
            __instance.transform.localScale *= bestScale;
            log?.LogInfo("NATIVE_HELP popup width x" + best.ToString("F2") + " scaled x" + bestScale.ToString("F2") + " rect=" + bestRect + " screen=" + Screen.width + "x" + Screen.height);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_HELP fit: " + error.Message); }
        finally { fitting = false; }
    }

    internal static bool Show()
    {
        try
        {
            DialogManager dialogs = DialogManager.Get();
            if (dialogs == null) return false;
            return dialogs.ShowUniquePopup(new AlertPopup.PopupInfo {
                m_id = NativeHelpText.PopupId, m_headerText = NativeHelpText.Header, m_text = NativeHelpText.Body,
                m_showAlertIcon = false, m_iconSet = AlertPopup.PopupInfo.IconSet.None,
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_confirmText = NativeHelpText.DiscordLabel, m_cancelText = NativeHelpText.CloseLabel, m_keyboardEscIsCancel = true,
                m_responseCallback = (response, data) => { if (response == AlertPopup.Response.CONFIRM) OpenDiscord(); }
            });
        }
        catch (Exception error) { log?.LogWarning("NATIVE_HELP popup unavailable: " + error.Message); return false; }
    }

    // The system browser is a separate process; the invite also stays readable in the popup if this does nothing.
    private static void OpenDiscord()
    {
        try
        {
            if (!NativeHelpText.OpensBrowser(Lane)) { log?.LogInfo("NATIVE_HELP dry-run OpenURL " + NativeHelpText.DiscordUrl + " lane=" + Lane); return; }
            Application.OpenURL(NativeHelpText.DiscordUrl);
            log?.LogInfo("NATIVE_HELP OpenURL " + NativeHelpText.DiscordUrl + " lane=" + Lane);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_HELP browser unavailable: " + error.Message); }
    }

    // Called every frame by the probe's Update: never throws; the first failure retires the HUB button for the session.
    internal static void Update(bool atHome)
    {
        if (!disabled)
        {
            try { TickHelp(atHome); }
            catch (Exception error)
            {
                disabled = true;
                try { if (homeButton != null) UnityEngine.Object.Destroy(homeButton.gameObject); } catch { }
                homeButton = null;
                log?.LogError("NATIVE_HELP home button disabled after failure: " + error);
            }
        }
    }

    private static bool HomeReady(bool atHome, out BnetBar bar, out BnetBarMenuButton gear)
    {
        bar = atHome ? BnetBar.Get() : null;
        gear = bar != null ? bar.m_menuButton : null;
        Box box = Box.Get();
        Shop shop = Shop.Get();
        return gear != null && gear.gameObject.activeInHierarchy && box != null && box.IsInStateWithButtons()
            && !SceneMgr.Get().IsTransitioning() && UniversalInputManager.Get()?.IsDialogActive() != true
            && !(shop != null && shop.IsOpen()) && !NativeRewardUI.BlocksHome && !NativeAchievements.Active
            && !NativeProfileSelector.BlocksHome;
    }

    private static void TickHelp(bool atHome)
    {
        bool want = HomeReady(atHome, out BnetBar bar, out BnetBarMenuButton gear);
        if (!want)
        {
            if (homeButton != null && homeButton.gameObject.activeSelf) homeButton.gameObject.SetActive(false);
            return;
        }
        if (homeButton == null) homeButton = Create(bar, gear, "SoloStoneHelpButton", NativeHelpText.ButtonLabel, Show);
        if (!homeButton.gameObject.activeSelf) homeButton.gameObject.SetActive(true);
        Place(bar, gear, homeButton);
    }

    private static UIBButton Create(BnetBar bar, BnetBarMenuButton gear, string name, string label, Func<bool> release)
    {
        GameObject def = null;
        UIBButton clone = null;
        try
        {
            def = GameUtils.InstantiateGameObject(MenuDefPrefab) as GameObject;
            UIBButton template = def != null ? def.GetComponent<ButtonListMenuDef>()?.m_templateButton : null;
            if (template == null) throw new InvalidOperationException("native menu button template unavailable");
            clone = UnityEngine.Object.Instantiate(template);
            clone.name = name;
            clone.ClearEventListeners();
            clone.SetText(label);
            int layer = gear.gameObject.layer;
            foreach (Transform part in clone.GetComponentsInChildren<Transform>(true)) part.gameObject.layer = layer;
            camera = CameraUtils.FindFirstByLayerMask(1 << layer) ?? throw new InvalidOperationException("bottom bar camera unavailable");
            // Face the bar camera: turn the button so its label lies in the plane of the bar's own UberText (the clock).
            var textComponent = ButtonText?.GetValue(clone) as Component;
            var clock = Clock?.GetValue(bar) as Component;
            Quaternion facing = clock != null ? clock.transform.rotation : gear.transform.rotation;
            Quaternion labelInButton = textComponent != null ? Quaternion.Inverse(clone.transform.rotation) * textComponent.transform.rotation : Quaternion.identity;
            clone.transform.rotation = facing * Quaternion.Inverse(labelInButton);
            // Start on the gear's depth plane (inside the bar camera's clip range); Place then aligns it on screen.
            clone.transform.position = gear.transform.position;
            clone.gameObject.SetActive(true);
            clone.SetEnabled(true);
            clone.AddEventListener(UIEventType.RELEASE, _ => release());
            log?.LogInfo(name + " home button created layer=" + layer);
            return clone;
        }
        catch
        {
            if (clone != null) UnityEngine.Object.Destroy(clone.gameObject);
            throw;
        }
        finally { if (def != null) UnityEngine.Object.Destroy(def); }
    }

    // Right-aligned with the gear, just above the bar: clear of the gold counter (left of the gear), the drawer and the box.
    private static void Place(BnetBar bar, BnetBarMenuButton gear, UIBButton buttonObject)
    {
        if (camera == null) camera = CameraUtils.FindFirstByLayerMask(1 << gear.gameObject.layer)
            ?? throw new InvalidOperationException("bottom bar camera unavailable");
        // Own button: renderer bounds (current this frame; collider bounds can lag a physics sync). Gear: its hit plate.
        Rect button = ScreenRect(buttonObject.gameObject, false), menu = ScreenRect(gear.gameObject, true);
        Renderer barMesh = bar.GetComponent<Renderer>();
        float barTop = barMesh != null ? ScreenRect(barMesh.bounds).yMax : menu.yMax;
        if (button.height <= 0 || menu.width <= 0) return;
        float height = Screen.height * HeightShare;
        float scale = height / button.height;
        if (Mathf.Abs(scale - 1) > 0.02f)
        {
            buttonObject.transform.localScale *= scale;
            button = ScreenRect(buttonObject.gameObject, false);
        }
        float targetX = menu.xMax - button.width * 0.5f;
        var target = new Vector2(targetX, Mathf.Max(barTop, menu.yMax) + Screen.height * GapShare + button.height * 0.5f);
        Vector3 from = camera.WorldToScreenPoint(Center(buttonObject.gameObject));
        // Behind the gear's depth plane, where the native bar tooltips (gear, gold counter) sit, so they draw over this button
        // (owner 2026-10-28: they rendered behind it): the button's near face is held just beyond that plane.
        float gearDepth = camera.WorldToScreenPoint(gear.transform.position).z, sink = 0;
        if (TooltipDepth.Span(buttonObject.gameObject, null, camera, out float near, out float far))
        {
            float half = (far - near) * .5f;
            sink = HandheldPolicy.SinkBehind(gearDepth - half, gearDepth, Mathf.Max(.01f, half * .5f));
        }
        Vector3 to = new Vector3(target.x, target.y, gearDepth + sink);
        if ((to - from).sqrMagnitude > 0.25f)
            buttonObject.transform.position += camera.ScreenToWorldPoint(to) - camera.ScreenToWorldPoint(from);
    }

    // Hit plates when asked (renderers include glow FX), else (or if none) renderers.
    private static List<Bounds> Plates(GameObject root, bool colliders)
    {
        var plates = colliders ? root.GetComponentsInChildren<Collider>(false).Where(collider => collider.enabled)
            .Select(collider => collider.bounds).ToList() : new List<Bounds>();
        return plates.Count > 0 ? plates
            : root.GetComponentsInChildren<Renderer>(false).Where(renderer => renderer.enabled).Select(renderer => renderer.bounds).ToList();
    }

    private static Vector3 Center(GameObject root)
    {
        var plates = Plates(root, false);
        if (plates.Count == 0) return root.transform.position;
        Bounds all = plates[0];
        foreach (Bounds plate in plates) all.Encapsulate(plate);
        return all.center;
    }

    private static Rect ScreenRect(GameObject root, bool colliders)
    {
        Rect rect = Rect.zero; bool any = false;
        foreach (Bounds plate in Plates(root, colliders))
        {
            Rect one = ScreenRect(plate);
            rect = any ? Rect.MinMaxRect(Mathf.Min(rect.xMin, one.xMin), Mathf.Min(rect.yMin, one.yMin),
                Mathf.Max(rect.xMax, one.xMax), Mathf.Max(rect.yMax, one.yMax)) : one;
            any = true;
        }
        return rect;
    }

    private static Rect ScreenRect(Bounds bounds)
    {
        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = camera.WorldToScreenPoint(new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z));
            left = Mathf.Min(left, point.x); right = Mathf.Max(right, point.x);
            bottom = Mathf.Min(bottom, point.y); top = Mathf.Max(top, point.y);
        }
        return Rect.MinMaxRect(left, bottom, right, top);
    }
}
