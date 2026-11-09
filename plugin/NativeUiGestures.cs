#if !SOLOSTONE_RELEASE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Test-control pointer gestures beyond a click (#57 live checks, owner 2026-10-28). Main thread only.
// - Hover: native roll-over (PegUIElement.TriggerOver) of one listed element; it stays until the next hover or unhover
//   rolls it out, like a parked cursor. Hover-only controls (a deck box's delete button) then appear.
// - Tray drag: the native deck tray reorder path. Press then hold the deck box (PegUI HOLD -> OnHold ->
//   StartDragToReorder), let the native DeckTrayDeckListContent.UpdateDragToReorder move it toward the target slot,
//   then release (OnRelease -> StopDragToReorder -> SendChanges). UpdateDragToReorder reads the mouse button and
//   position; a transpiler routes exactly those two reads in that one method through this class, which answers with a
//   synthetic held pointer only while a test-control drag is in flight. Every other reader still sees the real mouse.
// - Gear: the live BnetBar's own menu button is the only "home.menu" (UiRules.GameMenuControlAllowed).
internal static class NativeUiGestures
{
    private static readonly MethodInfo NativeButton = AccessTools.Method(typeof(InputCollection), "GetMouseButton", new[] { typeof(int) });
    private static readonly MethodInfo NativePosition = AccessTools.Method(typeof(InputCollection), "GetMousePosition", Type.EmptyTypes);
    private static readonly FieldInfo StartPos = AccessTools.Field(typeof(DeckTrayDeckListContent), "m_traySectionStartPos");
    private static readonly FieldInfo Sections = AccessTools.Field(typeof(DeckTrayDeckListContent), "m_traySections");
    private static readonly PropertyInfo Dragging = AccessTools.Property(typeof(DeckTrayReorderableContent), "DraggingDeckBox");
    private static ManualLogSource log;
    private static PegUIElement hovered;
    private static DeckTrayDeckListContent dragContent;
    private static int dragIndex = -1;
    internal static bool DragAvailable { get; private set; }

    // The Escape key in the order a real key press takes (UniversalInputManager.UpdateInput): DialogManager first (its current dialog's own
    // Escape: AlertPopup with m_keyboardEscIsCancel -> GoBack), and only without a dialog the bottom bar's handler (Game Menu and friends).
    // The real key read (InputCollection.GetKeyUp(Escape)) inside those two dialog handlers is answered true for this one call
    // (transpiler below, nothing else sees it). Route = "dialog" or "bnetbar"; a dialog that does not handle Escape is "dialog" with false.
    internal static bool Escape() => Escape(out _);
    internal static bool Escape(out string route)
    {
        var dialog = DialogEscapeAvailable ? CurrentDialog?.GetValue(DialogManager.Get()) : null;
        if (dialog != null)
        {
            route = "dialog";
            escapeKey = true;
            try { return (bool)DialogKeyboard.Invoke(DialogManager.Get(), null); }
            finally { escapeKey = false; }
        }
        route = "bnetbar";
        return BnetBar.Get() != null
            && (bool)(AccessTools.Method(typeof(BnetBar), "HandleEscapeKey") ?? throw new MissingMethodException("BnetBar", "HandleEscapeKey")).Invoke(BnetBar.Get(), null);
    }
    private static readonly FieldInfo CurrentDialog = AccessTools.Field(typeof(DialogManager), "m_currentDialog");
    private static readonly MethodInfo DialogKeyboard = AccessTools.Method(typeof(DialogManager), "HandleKeyboardInput", Type.EmptyTypes);
    private static readonly MethodInfo NativeKeyUp = AccessTools.Method(typeof(InputCollection), "GetKeyUp", new[] { typeof(KeyCode) });
    private static bool escapeKey;
    internal static bool DialogEscapeAvailable { get; private set; }
    internal static bool KeyUp(KeyCode key) => escapeKey && key == KeyCode.Escape || InputCollection.GetKeyUp(key);

    // Exactly one key read per handler is routed; any other shape fails the patch (dialog-first Escape unavailable: ui-escape keeps the bar's handler).
    private static IEnumerable<CodeInstruction> RouteEscapeKey(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int reads = 0;
        foreach (var instruction in code)
            if (instruction.Calls(NativeKeyUp)) { instruction.operand = AccessTools.Method(typeof(NativeUiGestures), nameof(KeyUp)); reads++; }
        if (reads != 1) throw new InvalidOperationException("dialog Escape key reads changed: " + reads);
        return code;
    }

    // The native Concede row closes the Game Menu itself; the semantic command must too, or the menu stays up over Result.
    internal static void CloseMenusForConcede()
    {
        var bar = BnetBar.Get();
        if (GameMenu.Get()?.GameMenuIsShown() == true) bar?.HideGameMenu();
        if (OptionsMenu.Get()?.IsShown() == true) OptionsMenu.Get().Hide(false);
    }

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        NativeGameplayDrag.Initialize(harmony);
        UiRules.GameMenuControlAllowed = element => element.Handle is BnetBarMenuButton gear && gear != null
            && gear.gameObject.activeInHierarchy && ReferenceEquals(gear, BnetBar.Get()?.m_menuButton);
        try
        {
            if (NativeButton == null || NativePosition == null || StartPos == null || Sections == null || Dragging == null)
                throw new MissingMemberException("native tray drag binding missing");
            harmony.Patch(AccessTools.Method(typeof(DeckTrayDeckListContent), "UpdateDragToReorder", Type.EmptyTypes)
                ?? throw new MissingMethodException("DeckTrayDeckListContent", "UpdateDragToReorder"),
                transpiler: new HarmonyMethod(typeof(NativeUiGestures), nameof(RouteDragPointer)));
            DragAvailable = true;
        }
        catch (Exception error) { DragAvailable = false; log?.LogWarning("TEST_CONTROL drag route unavailable: " + error.Message); }
        try
        {
            if (CurrentDialog == null || DialogKeyboard == null || NativeKeyUp == null) throw new MissingMemberException("dialog Escape binding missing");
            harmony.Patch(DialogKeyboard, transpiler: new HarmonyMethod(typeof(NativeUiGestures), nameof(RouteEscapeKey)));
            harmony.Patch(AccessTools.Method(typeof(AlertPopup), "HandleKeyboardInput", Type.EmptyTypes) ?? throw new MissingMethodException("AlertPopup", "HandleKeyboardInput"),
                transpiler: new HarmonyMethod(typeof(NativeUiGestures), nameof(RouteEscapeKey)));
            DialogEscapeAvailable = true;
        }
        catch (Exception error) { DialogEscapeAvailable = false; log?.LogWarning("TEST_CONTROL dialog-first Escape unavailable: " + error.Message); }
    }

    // Exactly one button read and one position read are routed; any other shape fails the patch (drag unavailable).
    private static IEnumerable<CodeInstruction> RouteDragPointer(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int buttons = 0, positions = 0;
        foreach (var instruction in code)
        {
            if (instruction.Calls(NativeButton)) { instruction.operand = AccessTools.Method(typeof(NativeUiGestures), nameof(MouseButton)); buttons++; }
            else if (instruction.Calls(NativePosition)) { instruction.operand = AccessTools.Method(typeof(NativeUiGestures), nameof(MousePosition)); positions++; }
        }
        if (buttons != 1 || positions != 1) throw new InvalidOperationException("UpdateDragToReorder input reads changed: " + buttons + "/" + positions);
        return code;
    }

    internal static bool MouseButton(int button) => dragContent != null ? button == 0 : InputCollection.GetMouseButton(button);

    internal static Vector3 MousePosition()
    {
        if (dragContent == null) return InputCollection.GetMousePosition();
        try { return SlotPoint(dragContent, dragIndex); }
        catch (Exception error) { log?.LogWarning("TEST_CONTROL drag pointer: " + error.Message); EndDrag(); return InputCollection.GetMousePosition(); }
    }

    // The inverse of UpdateDragToReorder's slot math: the screen point whose ray meets the tray plane in the middle of
    // slot `index` (slot = floor(-(z - startZ) / sectionDepth)). Recomputed per read, so tray scrolling is followed.
    private static Vector3 SlotPoint(DeckTrayDeckListContent content, int index)
    {
        Camera camera = Camera.main;
        var start = StartPos.GetValue(content) as Transform;
        var sections = Sections.GetValue(content) as IList;
        if (camera == null || start == null || sections == null || sections.Count == 0) throw new InvalidOperationException("tray layout unavailable");
        float depth = TransformUtil.ComputeSetPointBounds((Component)sections[0], false).size.z;
        if (depth <= 1e-4f) throw new InvalidOperationException("tray section has no depth");
        Vector3 normal = -camera.transform.forward;
        Vector3 world = start.position + new Vector3(0f, 0f, -(index + .5f) * depth);
        world -= normal * Vector3.Dot(world - start.position, normal);
        return camera.WorldToScreenPoint(world);
    }

    internal static bool Hoverable(UiElement item) =>
        item?.Handle is PegUIElement element && element != null && element.IsEnabled() && element.IsEnabled(UIEventType.ROLLOVER);

    internal static void Hover(UiElement item)
    {
        NativeTestControl.AssertMainThread();
        var element = (PegUIElement)item.Handle;
        if (hovered != null && hovered != element && hovered.gameObject.activeInHierarchy) hovered.TriggerOut();
        hovered = element;
        element.TriggerOver();
    }

    // True when a still-live hovered element was rolled out.
    internal static bool Unhover()
    {
        NativeTestControl.AssertMainThread();
        var element = hovered; hovered = null;
        if (element == null || !element.gameObject.activeInHierarchy) return false;
        element.TriggerOut();
        return true;
    }

    internal static string HoveredName => hovered != null ? hovered.gameObject.name : null;

    private static readonly MethodInfo CoinTileDrag = AccessTools.Method(typeof(CollectionCardVisual), "OnDrag", Type.EmptyTypes);
    private static readonly MethodInfo CoinTrayEvent = AccessTools.Method(typeof(DeckTrayCosmeticCoinContent), "WidgetEventListener", new[] { typeof(string) });
    private static readonly MethodInfo CoinDrop = AccessTools.Method(typeof(CollectionInputMgr), "DropCard", new[] { typeof(bool) });
    private static readonly MethodInfo CanGrab = AccessTools.Method(typeof(CollectionInputMgr), "CanGrabItem", new[] { typeof(Actor) });
    private static readonly PropertyInfo OverDeck = AccessTools.Property(typeof(CollectionInputMgr), "MouseIsOverDeck");
    private static readonly FieldInfo CoinDeck = AccessTools.Field(typeof(DeckTrayCosmeticCoinContent), "m_currentDeck");
    private static readonly FieldInfo CoinActor = AccessTools.Field(typeof(DeckTrayCosmeticCoinContent), "m_cosmeticCoinActor");
    private static readonly FieldInfo CoinAnimation = AccessTools.Field(typeof(DeckTrayCosmeticCoinContent), "m_animData");

    internal static string CoinDragBlocker(UiElement from, UiElement to)
    {
        var manager = CollectionManager.Get(); var tray = CollectionDeckTray.Get(); var input = CollectionInputMgr.Get();
        var content = tray?.GetCosmeticCoinContent(); var deck = manager?.GetEditedDeck();
        if (!NativeUIProbe.IsolatedLaunch || LocalLadder.Profile == null) return "No local profile";
        if (manager == null || !manager.IsInEditMode() || deck == null || LocalLadder.Profile.GetDraft(deck.ID) == null)
            return "No editable local deck";
        if (content == null || !content.gameObject.activeInHierarchy || !tray.CanPickupCard()
            || manager.GetCollectibleDisplay()?.GetViewMode() != CollectionUtils.ViewMode.COINS) return "Coin tray not ready";
        if (CoinTileDrag == null || CoinTrayEvent == null || CoinDrop == null || OverDeck?.GetSetMethod(true) == null
            || CanGrab == null || CoinDeck == null || CoinActor == null || CoinAnimation == null) return "Native coin drag route unavailable";
        if (!ReferenceEquals(CoinDeck.GetValue(content), deck) || CoinAnimation.GetValue(content) != null) return "Coin tray busy";
        if (input == null || input.HasHeldCard() || input.IsDraggingScrollbar()) return "Native drag already in progress";
        bool assigning = UiRules.IsCoinTile(from.Id);
        var tile = (assigning ? from : to).Handle as CollectionCardVisual;
        var socket = (assigning ? to : from).Handle as PegUIElement;
        if (tile == null || socket == null || socket.GetComponentInParent<DeckTrayCosmeticCoinContent>() != content
            || tile.GetVisualType() != CollectionUtils.ViewMode.COINS) return "Coin drag needs native coin controls";
        int coin = CosmeticCoinManager.Get().GetCoinIdFromCoinCard(tile.CardId);
        if (!LocalLadder.Profile.OwnsCoin(coin) || !CosmeticCoinManager.Get().IsOwnedCoinCard(tile.CardId)
            || string.IsNullOrEmpty(NativeMatch.NativeCoinCard(coin))) return "Coin not owned or unavailable";
        if (!assigning && (!deck.CosmeticCoinID.HasValue || !LocalLadder.Profile.OwnsCoin(deck.CosmeticCoinID.Value)
            || string.IsNullOrEmpty(NativeMatch.NativeCoinCard(deck.CosmeticCoinID.Value)))) return "No owned coin in socket";
        Actor actor = assigning ? tile.GetCollectionCardActors()?.GetPreferredActor() : CoinActor.GetValue(content) as Actor;
        if (actor == null || !(bool)CanGrab.Invoke(input, new object[] { actor }) || assigning && !tile.CanPickUpCard()) return "Native coin pickup disabled";
        bool sourceOver = NativeUiTree.AtPoint(from, () => tray.MouseIsOver(Box.Get().GetCamera()));
        bool targetOver = NativeUiTree.AtPoint(to, () => tray.MouseIsOver(Box.Get().GetCamera()));
        if (sourceOver != !assigning || targetOver != assigning) return "Coin drag must cross the tray boundary";
        return null;
    }

    // Synchronous bounded gesture: native pickup, then native mouse-up delivery at the already hit-tested endpoint.
    // No save/cosmetic setter is called here; native drag handlers own the change and notifications.
    internal static bool CoinDrag(UiElement from, UiElement to)
    {
        NativeTestControl.AssertMainThread();
        var input = CollectionInputMgr.Get(); var tray = CollectionDeckTray.Get();
        bool assigning = UiRules.IsCoinTile(from.Id);
        NativeUiTree.AtPoint(from, () => {
            if (assigning) CoinTileDrag.Invoke(from.Handle, null);
            else CoinTrayEvent.Invoke(tray.GetCosmeticCoinContent(), new object[] { "CODE_TRAY_DRAG_START" });
            return true;
        });
        if (!input.HasHeldCard()) return false;
        NativeUiTree.AtPoint(to, () => {
            OverDeck.SetValue(input, tray.MouseIsOver(Box.Get().GetCamera()), null);
            CoinDrop.Invoke(input, new object[] { false });
            return true;
        });
        return !input.HasHeldCard();
    }

    // Native drag preconditions for two listed deck boxes; null when the drag may start.
    internal static string DragBlocker(UiElement from, UiElement to, out int targetIndex)
    {
        targetIndex = -1;
        if (UiRules.IsCoinDrag(from?.Id, to?.Id)) return CoinDragBlocker(from, to);
        if (!DragAvailable) return "Native drag route unavailable";
        var content = CollectionDeckTray.Get()?.GetDecksContent();
        if (content == null || !content.gameObject.activeInHierarchy) return "No native deck tray";
        if (!(from?.Handle is CollectionDeckBoxVisual source) || source == null || !(to?.Handle is CollectionDeckBoxVisual target) || target == null)
            return "Drag needs two native deck boxes";
        if (source.IsLocked()) return "Locked deck boxes do not reorder";
        if (!content.CanDragToReorderDecks) return "Native tray reorder disabled";
        if (Dragging.GetValue(content) != null) return "Native drag already in progress";
        targetIndex = target.GetPositionIndex();
        return targetIndex < 0 ? "Target deck box has no slot" : null;
    }

    // Mouse-down frame on the source box.
    internal static void DragPress(UiElement from)
    {
        NativeTestControl.AssertMainThread();
        var element = (PegUIElement)from.Handle;
        element.TriggerOver(); element.TriggerPress();
    }

    // Hold frame: the synthetic pointer goes live first, so the tray's own Update this frame sees the button held.
    internal static bool DragHold(UiElement from, int targetIndex)
    {
        NativeTestControl.AssertMainThread();
        var box = (CollectionDeckBoxVisual)from.Handle;
        dragContent = CollectionDeckTray.Get()?.GetDecksContent(); dragIndex = targetIndex;
        box.TriggerHold();
        if (dragContent != null && ReferenceEquals(Dragging.GetValue(dragContent), box)) return true;
        EndDrag();
        box.TriggerReleaseAll(false); box.TriggerOut();
        return false;
    }

    internal static int Slot(UiElement item) => item?.Handle is CollectionDeckBoxVisual box && box != null ? box.GetPositionIndex() : -1;

    // The source box's slot while the native drag still holds it; -1 once native stopped the drag.
    internal static int DragSlot(UiElement from)
    {
        var box = from.Handle as CollectionDeckBoxVisual;
        if (box == null || dragContent == null || !ReferenceEquals(Dragging.GetValue(dragContent), box)) return -1;
        return box.GetPositionIndex();
    }

    // Mouse-up over the dragged box after a hold: no tap (the native hold timer is long past), so the deck does not open.
    internal static void DragRelease(UiElement from)
    {
        NativeTestControl.AssertMainThread();
        EndDrag();
        var box = from.Handle as PegUIElement;
        if (box == null || !box.gameObject.activeInHierarchy) return;
        box.TriggerRelease(); box.TriggerReleaseAll(true); box.TriggerOut();
    }

    // Pointer back to the real mouse: the native tray stops the drag on its next Update (button up), as a real release.
    internal static void EndDrag() { dragContent = null; dragIndex = -1; }
}
#endif
