using System;
using System.Collections.Generic;
using System.Reflection;
using Hearthstone.UI;
using UnityEngine;

internal static class NativeHomeUI
{
    // UberText lives in a client assembly absent from our compile references.
    private static readonly FieldInfo TextMesh = typeof(StoreButton).GetField("m_storeText");
    private static readonly FieldInfo ClosedTextMesh = typeof(StoreButton).GetField("m_storeClosedText");
    private static readonly PropertyInfo Text = TextMesh.FieldType.GetProperty("Text");
    private static readonly HashSet<string> hiddenLabelPaths = new HashSet<string>();
    private static readonly HashSet<string> hiddenSlotPaths = new HashSet<string>();
    private static readonly Dictionary<Type, PropertyInfo> textTypes = new Dictionary<Type, PropertyInfo> {
        { TextMesh.FieldType, Text }
    };
    private static readonly string[] journalKeys = { "GLUE_BOX_JOURNAL", "GLUE_JOURNAL", "GLOBAL_JOURNAL", "GLUE_QUEST_LOG", "GLUE_QUESTLOG" };
    private static float nextJournalScan, summaryAt;
    private static bool journalSlotReported;
    private static bool summaryPending;

    internal static void BeginHub()
    {
        hiddenLabelPaths.Clear();
        hiddenSlotPaths.Clear();
        shopLabelPaths.Clear();
        nextJournalScan = 0;
        summaryAt = Time.realtimeSinceStartup + 2;
        summaryPending = true;
    }

    internal static void UpdateJournal(Box box, Action<string> report, Func<string, bool> entryActive = null)
    {
        if (box == null || Time.realtimeSinceStartup < nextJournalScan) return;
        // Catch localization/widget refreshes without walking the hierarchy every frame.
        nextJournalScan = Time.realtimeSinceStartup + 0.5f;
        if (summaryPending && Time.realtimeSinceStartup >= summaryAt && report != null)
        {
            // One concise summary per HUB load; no per-label text dump.
            summaryPending = false;
            int count = 0;
            foreach (var label in DrawerText(box))
                if (label.gameObject.activeInHierarchy) count++;
            report("NATIVE_MENU journal labels=" + hiddenLabelPaths.Count + " slots=" + hiddenSlotPaths.Count
                + " activeTexts=" + count
                + " widget=" + (box.m_journalButtonWidget != null)
                + " template=" + ((box.m_journalButtonWidget as WidgetInstance)?.Widget != null));
        }
        HideJournalLabels(box, report);
        LabelShop(box, report);
        ApplyModePolicy(box, report, entryActive);
        LayoutDrawer(box, report); // re-apply if native code re-lays out the drawer
    }

    internal static void HideJournalLabels(Box box, Action<string> report)
    {
        if (box == null) return;
        // The drawer Journal slot is SoloStone's Feats entry (NativeAchievements) unless Feats retired itself.
        if (NativeAchievements.HomeJournalReady) { LabelFeats(box, report); return; }
        var widget = box.m_journalButtonWidget;
        // WidgetInstance.Hide forwards to its asynchronously spawned template; disabling the host alone does not.
        if (widget != null && widget.IsInitialized) widget.Hide();
        HideJournalControl(box, widget, report);
        HideJournalControl(box, (widget as WidgetInstance)?.Widget, report);
        HideJournalControl(box, box.GetOldJournalButton(), report);
        HideJournalControl(box, box.m_QuestLogButton, report);
        object storeText = box.m_StoreButton == null ? null : TextMesh.GetValue(box.m_StoreButton);
        object closedStoreText = box.m_StoreButton == null ? null : ClosedTextMesh.GetValue(box.m_StoreButton);
        var journalValues = JournalValues();
        foreach (Component label in DrawerText(box))
        {
            if (label == null || ReferenceEquals(label, storeText) || ReferenceEquals(label, closedStoreText)) continue;
            if (journalValues.Contains(LabelText(label)) || IsJournalName(label.transform.name) || IsJournalName(label.transform.parent?.name))
                HideJournalObject(box, label.gameObject, true, report);
        }
    }

    // Native Journal labels read "Journal"/"Quest Log"; the slot opens Feats, so it says so.
    private static void LabelFeats(Box box, Action<string> report)
    {
        // Offline the retail drawer logic hides the Journal widget (no journal data); show it whenever the drawer is up.
        var widget = box.m_journalButtonWidget;
        var template = (widget as WidgetInstance)?.Widget as WidgetTemplate;
        if (widget != null && template != null && template.IsDesiredHidden && box.m_StoreButton != null
            && box.m_StoreButton.gameObject.activeInHierarchy)
        {
            widget.Show();
            widget.TriggerEvent("ENABLE_INTERACTION");
            report?.Invoke("NATIVE_MENU journal widget shown as Feats");
        }
        var journalValues = JournalValues();
        foreach (Component label in DrawerText(box))
        {
            if (label == null || !journalValues.Contains(LabelText(label))) continue;
            foreach (var type in textTypes)
                if (type.Key.IsInstanceOfType(label) && type.Value != null && type.Value.CanWrite)
                {
                    type.Value.SetValue(label, NativeAchievements.ButtonLabel, null);
                    if (hiddenLabelPaths.Add(Path(label.transform))) report?.Invoke("NATIVE_MENU journal label -> Feats " + Path(label.transform));
                }
        }
    }

    // The drawer label under the Store button (GLUE_BOX_STORE, "Store") reads the native button/tooltip word, "Shop".
    private static readonly HashSet<string> shopLabelPaths = new HashSet<string>();
    private static void LabelShop(Box box, Action<string> report)
    {
        string store = GameStrings.Get("GLUE_BOX_STORE"), shop = GameStrings.Get("GLUE_STORE_OPEN_BUTTON_TEXT");
        if (string.IsNullOrEmpty(shop) || shop == "GLUE_STORE_OPEN_BUTTON_TEXT") shop = "Shop";
        // The visible caption is the ButtonName beside the button (as NativeShop finds it). Its UberText holds the raw key
        // and localizes at render, so it is matched by place; its generated render child is left to UberText.
        var caption = box.m_StoreButton == null ? null : box.m_StoreButton.transform.parent?.Find("ButtonName");
        foreach (Component label in DrawerText(box))
        {
            if (label == null || label.transform.name.StartsWith("UberText_RenderObject", StringComparison.Ordinal)) continue;
            string text = LabelText(label);
            if (label.transform != caption && text != "Store" && (string.IsNullOrEmpty(store) || text != store)) continue;
            if (text == shop) continue;
            foreach (var type in textTypes)
                if (type.Key.IsInstanceOfType(label) && type.Value != null && type.Value.CanWrite)
                {
                    type.Value.SetValue(label, shop, null);
                    if (shopLabelPaths.Add(Path(label.transform)))
                        report?.Invoke("NATIVE_MENU store label '" + text + "' -> " + shop + " " + Path(label.transform));
                }
        }
    }

    // Retail's Shop tooltip promises Runestones and real money; the local shop takes gold only.
    internal const string StoreTooltipBody = "Buy items with gold.";
    internal static void StoreTooltip(StoreButton button)
    {
        var zone = button == null ? null : button.GetComponent<TooltipZone>();
        if (zone == null || !zone.IsShowingTooltip()) return;
        zone.HideTooltip();
        zone.ShowBoxTooltip(GameStrings.Get("GLUE_TOOLTIP_BUTTON_STORE_HEADLINE"), StoreTooltipBody);
    }

    private static HashSet<string> JournalValues()
    {
        var values = new HashSet<string>(journalKeys) { "Journal", "Quest Log" };
        foreach (string key in journalKeys)
        {
            string localized = GameStrings.Get(key);
            if (!string.IsNullOrEmpty(localized)) values.Add(localized);
        }
        return values;
    }

    private static bool IsJournalName(string name) => name != null &&
        (name.IndexOf("Journal", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("QuestLog", StringComparison.OrdinalIgnoreCase) >= 0);

    private static void HideJournalControl(Box box, Component control, Action<string> report)
    {
        if (control == null) return;
        HideJournalObject(box, control.gameObject, false, report);
        var slot = control.transform.parent;
        // Prefer the owned slot, but never disable a shared drawer containing retained controls.
        if (slot != null && IsJournalName(slot.name)) HideJournalObject(box, slot.gameObject, false, report);
        var label = slot?.Find("ButtonName");
        if (label != null) HideJournalObject(box, label.gameObject, true, report);
    }

    private static void HideJournalObject(Box box, GameObject target, bool label, Action<string> report)
    {
        if (target == null || box.transform.IsChildOf(target.transform)) return;
        foreach (var keep in new Component[] { box.m_StoreButton, box.m_OpenPacksButton, box.m_CollectionButton, box.m_Drawer })
        {
            if (keep == null) continue;
            if (keep.transform.IsChildOf(target.transform)) return;
            var name = keep.transform.parent?.Find("ButtonName");
            if (name != null && name.IsChildOf(target.transform)) return;
        }
        if (target.activeSelf) target.SetActive(false);
        string path = Path(target.transform);
        if ((label ? hiddenLabelPaths : hiddenSlotPaths).Add(path))
            report?.Invoke("NATIVE_MENU journal " + (label ? "label" : "slot") + " hidden " + path);
    }

    private static string Path(Transform transform)
    {
        string path = transform.name;
        for (var parent = transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
        return path;
    }

    private static IEnumerable<Component> DrawerText(Box box)
    {
        // TMP and TextMesh are optional client assemblies, so never add a hard dependency on either.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            foreach (string name in new[] { "TMPro.TMP_Text", "UnityEngine.TextMesh" })
            {
                var type = assembly.GetType(name, false);
                if (type != null && !textTypes.ContainsKey(type)) textTypes[type] = type.GetProperty("text");
            }
        var seen = new HashSet<Component>();
        foreach (var root in new Component[] { box.m_Drawer, (box.m_journalButtonWidget as WidgetInstance)?.Widget,
            box.m_journalButtonWidget, box.GetOldJournalButton(), box })
        {
            if (root == null) continue;
            foreach (var type in textTypes)
                if (type.Value != null)
                    foreach (Component component in root.GetComponentsInChildren(type.Key, true))
                        if (component != null && seen.Add(component)) yield return component;
        }
    }

    private static string LabelText(Component label)
    {
        foreach (var type in textTypes)
            if (type.Key.IsInstanceOfType(label)) return type.Value?.GetValue(label, null) as string ?? "";
        return "";
    }

    // The disabled fourth-mode face says Fan-made modes (community mods, modder toolkit); it stays inert like Arena.
    // Only the native face text changes, so font, plate and size stay native; the tooltip headline reads the same text.
    internal const string FanModesLabel = "Fan-made modes";
    internal const string ArenaEntry = "home.arena", FanModesEntry = "home.battlegrounds";

    // One switch for the Fan-made modes relabel and the Arena/Fan-made modes slot swap: each follows its hidden-entry policy, so an
    // enabled (implemented) Arena or Battlegrounds is neither force-relabelled nor swapped. active: entry id -> policy applies.
    internal static void ApplyModePolicy(Box box, Action<string> report, Func<string, bool> active = null)
    {
        if (box == null) return;
        bool campaigns = active == null || active(FanModesEntry), arena = active == null || active(ArenaEntry);
        if (campaigns) LabelFanModes(box, report); // Box.SetupButtonText rewrites the face on every HUB load
        if (campaigns && arena) SwapModeSlots(box, report); // native re-layout restores the authored slots
    }

    private static void LabelFanModes(Box box, Action<string> report)
    {
        var button = box?.m_BattleGroundsButton;
        if (button == null || button.GetText() == FanModesLabel) return;
        button.SetText(FanModesLabel);
        report?.Invoke("NATIVE_MENU battlegrounds label -> " + FanModesLabel);
        FitFanModesText(box, button, report);
    }

    // The bottom row's plate has a recessed lower lip, so the longer caption read low against it (Deck screenshot,
    // 2026-10-28). Lift it a few pixels toward the Hearthstone row and give it side margin; both are re-derived from
    // the authored text pose each time, so repeated HUB loads never accumulate.
    private const float FanModesLift = 0.07f, FanModesWidth = 0.9f;
    private sealed class AuthoredText { internal Vector3 Local; internal float Width; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Component, AuthoredText> authoredTexts =
        new System.Runtime.CompilerServices.ConditionalWeakTable<Component, AuthoredText>();
    private static void FitFanModesText(Box box, BoxMenuButton button, Action<string> report)
    {
        var text = typeof(BoxMenuButton).GetField("m_TextMesh").GetValue(button) as Component;
        var play = box.m_PlayButton;
        if (text == null || play == null) return;
        var width = text.GetType().GetProperty("Width");
        var authored = authoredTexts.GetValue(text, key => new AuthoredText { Local = key.transform.localPosition,
            Width = width != null ? (float)width.GetValue(key, null) : 0 });
        // One row's height in world units, measured on the live box; "up" is toward the Hearthstone row.
        Vector3 up = play.transform.position - button.transform.position;
        if (up.sqrMagnitude < 1e-6f) return;
        float rows = Mathf.Max(1, Mathf.Round(up.magnitude / Mathf.Max(1e-3f, RowHeight(box))));
        Vector3 lift = button.transform.InverseTransformVector(up / rows * FanModesLift);
        text.transform.localPosition = authored.Local + lift;
        if (width != null && width.CanWrite && authored.Width > 0) width.SetValue(text, authored.Width * FanModesWidth, null);
        report?.Invoke("NATIVE_MENU fan-made modes text lifted " + lift + " width " + authored.Width * FanModesWidth);
    }

    private static float RowHeight(Box box) => box.m_ArenaButton == null || box.m_BattleGroundsButton == null ? 1
        : (box.m_ArenaButton.transform.position - box.m_BattleGroundsButton.transform.position).magnitude;

    // Owner order: Hearthstone / Arena / Fan-made modes. The prefab authors it (the Battlegrounds button) above Arena;
    // swap the two whole buttons so each keeps its identity, handlers, tooltip and collider, and the faces match their
    // click areas. The disk sockets are shaped per row, so the plates also trade meshes (once, when both exist).
    // State lives per Box instance (a reloaded HUB builds a new Box); positions are re-derived from the current ones
    // each pass, so no transient intro position is ever remembered as a slot.
    private sealed class ModeSlots { internal Vector3 Arena, Campaigns; internal bool Placed, MeshesTraded, MeshNoted; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Box, ModeSlots> modeSlots =
        new System.Runtime.CompilerServices.ConditionalWeakTable<Box, ModeSlots>();

    private static void SwapModeSlots(Box box, Action<string> report)
    {
        var arena = box?.m_ArenaButton;
        var campaigns = box?.m_BattleGroundsButton;
        if (arena == null || campaigns == null) return;
        var state = modeSlots.GetOrCreateValue(box);
        Vector3 arenaNow = arena.transform.localPosition, campaignsNow = campaigns.transform.localPosition;
        if (!state.Placed || arenaNow != state.Arena || campaignsNow != state.Campaigns)
        {
            // Native layout (re)wrote both slots: the arena button takes the campaigns slot and vice versa.
            arena.transform.localPosition = campaignsNow;
            campaigns.transform.localPosition = arenaNow;
            state.Arena = campaignsNow; state.Campaigns = arenaNow;
            if (!state.Placed) report?.Invoke("NATIVE_MENU mode slots swapped: Arena above Fan-made modes");
            state.Placed = true;
        }
        if (state.MeshesTraded) return;
        var arenaFilter = arena.m_ButtonMeshRenderer?.GetComponent<MeshFilter>();
        var campaignsFilter = campaigns.m_ButtonMeshRenderer?.GetComponent<MeshFilter>();
        if (arenaFilter == null || campaignsFilter == null || arenaFilter == campaignsFilter)
        {
            if (!state.MeshNoted) report?.Invoke("NATIVE_MENU mode plates not ready to trade meshes; retrying");
            state.MeshNoted = true;
            return;
        }
        var tmp = arenaFilter.sharedMesh;
        arenaFilter.sharedMesh = campaignsFilter.sharedMesh;
        campaignsFilter.sharedMesh = tmp;
        state.MeshesTraded = true;
        report?.Invoke("NATIVE_MENU mode plates traded meshes");
    }

    internal static void DisableMode(Box box, BoxMenuButton button)
    {
        if (box == null || button == null) return;
        // Owner-approved look: the fourth slot is always the native decorative plate (boot shows it while net data is
        // pending). Box.ShowGameModesButton swaps in the Modes face after a match; swap it back. Only a layout without
        // the plate keeps a disabled Modes face.
        if (button == box.m_GameModesButton && box.m_EmptyFourthButton != null)
        {
            if (button.gameObject.activeSelf) button.gameObject.SetActive(false);
            if (!box.m_EmptyFourthButton.activeSelf) box.m_EmptyFourthButton.SetActive(true);
        }
        else button.gameObject.SetActive(true);
        box.SetBoxButtonInteractions(false, button, true);
        button.SetEnabled(UIEventType.PRESS, false);
        button.SetEnabled(UIEventType.RELEASE, false);
        button.SetEnabled(UIEventType.ROLLOVER, true);
        button.SetEnabled(UIEventType.ROLLOUT, true);
    }

    private static bool IsUnavailableMode(Box box, PegUIElement button) => box != null && button != null
        && (button == box.m_ArenaButton || button == box.m_BattleGroundsButton || button == box.m_GameModesButton);

    internal static bool ShowModeTooltip(Box box, PegUIElement button)
    {
        if (!IsUnavailableMode(box, button)) return true;
        if (box.IsInStateWithButtons())
            button.GetComponent<TooltipZone>()?.ShowBoxTooltip(((BoxMenuButton)button).GetText(), "Not done yet! In the backlog");
        return false;
    }

    internal static bool HideModeTooltip(Box box, PegUIElement button)
    {
        if (!IsUnavailableMode(box, button)) return true;
        button.GetComponent<TooltipZone>()?.HideTooltip();
        return false;
    }

    internal static void RefreshDrawer(Box box, int unopenedPacks, Action<string> report = null)
    {
        if (box == null) return;
        // ScreenBox readiness never arrives offline; its UpdateUIState otherwise writes -1.
        box.m_OpenPacksButton?.SetPackCount(unopenedPacks);
        LayoutDrawer(box, report);
    }

    // The prefab authors the slot positions (no runtime layout). With Feats the four slots sit at native spacing
    // (Store, Feats, Packs, Collection); without it Store takes the hidden Journal slot. The row then centres on the
    // drawer mesh, which moves with the buttons.
    private static void LayoutDrawer(Box box, Action<string> report)
    {
        var store = box?.m_StoreButton?.transform.parent;
        var packs = box?.m_OpenPacksButton?.transform.parent;
        var collection = box?.m_CollectionButton?.transform.parent;
        if (store == null || packs == null || collection == null || store == packs || store == collection || packs == collection) return;
        // Live: the Journal slot is BoxButtonParent/Box_Button_Journal (it carries the label), while the widget itself
        // lives at RootObject/JournalButton, parked off screen at scale 0 until retail journal data arrives (never offline).
        Transform journal = NativeAchievements.HomeJournalReady ? store.parent?.Find(JournalSlotName) : null;
        if (journal == store || journal == packs || journal == collection) journal = null;
        if (NativeAchievements.HomeJournalReady && journal == null && !journalSlotReported)
        {
            // One-shot live evidence for the drawer hierarchy when the Journal slot cannot be resolved.
            journalSlotReported = true;
            report?.Invoke("NATIVE_MENU feats slot unresolved store=" + Path(store) + " widget="
                + (box.m_journalButtonWidget == null ? "null" : Path(box.m_journalButtonWidget.transform) + " active=" + box.m_journalButtonWidget.gameObject.activeInHierarchy)
                + " questLog=" + (box.m_QuestLogButton == null ? "null" : Path(box.m_QuestLogButton.transform) + " active=" + box.m_QuestLogButton.gameObject.activeInHierarchy)
                + " old=" + (box.GetOldJournalButton() == null ? "null" : Path(box.GetOldJournalButton().transform))
                + " siblings=" + string.Join(",", Children(store.parent)));
        }
        // Each slot carries its sibling label, badge and hitbox.
        float step = collection.position.x - packs.position.x;
        if (journal != null) { MoveX(journal, packs.position.x - step); MoveX(store, packs.position.x - 2 * step); }
        else MoveX(store, packs.position.x - step);
        var mesh = box.m_Drawer?.GetDrawerMesh();
        if (mesh == null || mesh.bounds.size.x <= 0) { PlaceJournalWidget(box, journal, store); return; }
        float middle = journal != null ? packs.position.x - step / 2 : packs.position.x;
        float centre = mesh.bounds.center.x, shift = centre - middle;
        if (Math.Abs(shift) > Math.Abs(step) * 0.001f)
        {
            foreach (var slot in new[] { store, journal, packs, collection })
                if (slot != null) MoveX(slot, slot.position.x + shift);
            report?.Invoke("NATIVE_MENU drawer centred x=" + centre + " shift=" + shift + " spacing=" + step + " feats=" + (journal != null));
        }
        PlaceJournalWidget(box, journal, store);
    }

    internal const string JournalSlotName = "Box_Button_Journal";
    // Live lane 2 at unit scale the quill read smaller than its Store/Packs/Collection neighbours.
    internal const float FeatsIconScale = 1.25f;

    // Put the Journal widget in its drawer slot at the Store icon's offset within its own slot, at unit scale.
    private static void PlaceJournalWidget(Box box, Transform journal, Transform store)
    {
        var widget = box.m_journalButtonWidget?.transform;
        var storeIcon = box.m_StoreButton?.transform;
        if (journal == null || widget == null || storeIcon == null) return;
        Vector3 target = journal.position + (storeIcon.position - store.position);
        Vector3 scale = Vector3.one * FeatsIconScale;
        bool moved = widget.position != target || widget.localScale != scale;
        if (widget.position != target) widget.position = target;
        if (widget.localScale != scale) widget.localScale = scale;
        // Retail drawer refreshes send DISABLE_INTERACTION while journal data is missing; re-enable after each (re)placement.
        if (moved) box.m_journalButtonWidget.TriggerEvent("ENABLE_INTERACTION");
    }

    private static IEnumerable<string> Children(Transform parent)
    {
        if (parent == null) yield break;
        foreach (Transform child in parent) yield return child.name + (child.gameObject.activeSelf ? "" : "(off)");
    }

    private static void MoveX(Transform slot, float x)
    {
        var position = slot.position;
        position.x = x;
        slot.position = position;
    }
}
