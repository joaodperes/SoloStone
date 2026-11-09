#if !SOLOSTONE_RELEASE
using System;
using System.Reflection;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Opt-in pointer injection. Run the unmodified native Update path, including raycasts, grab/target and release.
internal static class NativeGameplayDrag
{
    private static readonly MethodInfo Update = AccessTools.DeclaredMethod(typeof(InputManager), "Update", Type.EmptyTypes);
    private static readonly FieldInfo Position = AccessTools.Field(typeof(UniversalInputManager), "m_mousePosition");
    private static readonly FieldInfo LeftButton = AccessTools.Field(typeof(InputManager), "m_leftMouseButtonIsDown");
    private static readonly FieldInfo MouseDownObject = AccessTools.Field(typeof(InputManager), "m_lastObjectMousedDown");
    private static readonly FieldInfo TouchDragging = AccessTools.Field(typeof(InputManager), "m_touchDraggingCard");
    private static readonly FieldInfo TargetingHeroPower = AccessTools.Field(typeof(InputManager), "m_targettingHeroPower");
    private static readonly MethodInfo SetDragging = AccessTools.DeclaredMethod(typeof(InputManager), "SetDragging", new[] { typeof(bool) });
    private static bool available, running, dispatch, down, up, releasePending, clickingChoice;
    private static InputManager gestureInput;
    private static UniversalInputManager gestureUniversal;
    private static GameState gestureGame;
    private static object gestureGameEntity;
    private static SceneMgr gestureScene;
    private static string gestureMatch;
    private static Vector3 pointer, from, to;
    private static float began;
    private static int startFrame;
    internal static bool Running => running;
    // Set whenever a cleanup (even one the driver ran itself and swallowed) left held input or targeting behind. The scheduler
    // reads it: a drag whose cleanup failed has an unknown input state and may never be rejected as "nothing happened".
    internal static string CleanupFailure { get; private set; }
    // What the client input still holds right now, read from the live managers rather than from the finished gesture.
    internal static string InputResidue()
    {
        var input = InputManager.Get(); var game = GameState.Get();
        if (input == null) return "no input manager";
        if (input.LeftMouseButtonDown) return "left button down";
        if (input.GetHeldCard() != null) return "held card";
        if (game != null && game.IsInTargetMode()) return "target mode";
        return null;
    }

    internal static void Initialize(Harmony harmony)
    {
        try
        {
            if (Update == null || Position == null || LeftButton == null || MouseDownObject == null
                || TouchDragging == null || TargetingHeroPower == null || SetDragging == null) return;
            harmony.Patch(Update, prefix: new HarmonyMethod(typeof(NativeGameplayDrag), nameof(AllowUpdate)));
            foreach (string name in new[] { "GetMouseButtonDown", "GetMouseButtonUp", "GetMouseButton" })
                harmony.Patch(AccessTools.Method(typeof(InputCollection), name, new[] { typeof(int) })
                    ?? throw new MissingMethodException(name), prefix: new HarmonyMethod(typeof(NativeGameplayDrag),
                    name == "GetMouseButtonDown" ? nameof(ButtonDown) : name == "GetMouseButtonUp" ? nameof(ButtonUp) : nameof(Button)));
            harmony.Patch(AccessTools.Method(typeof(InputCollection), "GetMousePosition", Type.EmptyTypes)
                ?? throw new MissingMethodException("GetMousePosition"), prefix: new HarmonyMethod(typeof(NativeGameplayDrag), nameof(MousePosition)));
            available = true;
        }
        catch { available = false; }
    }
    private static bool AllowUpdate() => !running || dispatch;
    private static bool ButtonDown(int __0, ref bool __result)
    { if (!dispatch) return true; __result = __0 == 0 && down; return false; }
    private static bool ButtonUp(int __0, ref bool __result)
    { if (!dispatch) return true; __result = __0 == 0 && up; return false; }
    private static bool Button(int __0, ref bool __result)
    { if (!dispatch) return true; __result = __0 == 0 && !up; return false; }
    private static bool MousePosition(ref Vector3 __result)
    { if (!dispatch) return true; __result = pointer; return false; }

    internal static Vector3 EntityPoint(int id)
    {
        var front = GameState.Get(); var entity = front?.GetEntity(id);
        if (entity == null) throw new InvalidOperationException("Unknown entity");
        bool hand = entity.GetTag(GAME_TAG.ZONE) == 3 && entity.GetControllerId() == front.GetFriendlySidePlayer()?.GetPlayerId();
        if (entity.GetTag(GAME_TAG.ZONE) != 1 && !hand) throw new InvalidOperationException("Entity not in play or friendly hand");
        var card = entity.GetCard(); var actor = card?.GetActor();
        if (actor == null || !card.gameObject.activeInHierarchy || !actor.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Entity has no visible actor");
        Collider collider = hand ? front.GetFriendlySidePlayer().GetHandZone().GetStandIn(card)?.standInCollider : actor.GetCollider();
        Vector2 point = NativePlaytest.Point(collider);
        return new Vector3(point.x * Screen.width / 1280f, (720f - point.y) * Screen.height / 720f, 0);
    }
    internal static Vector3 ChoicePoint(int id)
    {
        var game = GameState.Get(); var choice = game?.GetFriendlyEntityChoices();
        var entity = game?.GetEntity(id); var input = InputManager.Get(); var universal = UniversalInputManager.Get();
        if (!available || choice?.ChoiceType != CHOICE_TYPE.TARGET || choice.CountMin != 1 || choice.CountMax != 1
            || !game.IsInChoiceMode() || entity?.GetTag(GAME_TAG.ZONE) != 3
            || !game.CanTargetCardsInHand() || !game.IsChoice(entity)
            || input == null || !input.IsInputEnabled() || !input.PermitDecisionMakingInput()
            || universal == null || universal.IsDialogActive() || universal.IsTouchMode())
            throw new InvalidOperationException("Hand TARGET choice input blocked (requires mouse mode)");
        return EntityPoint(id);
    }
    private static Vector3 BoardPoint(int position)
    {
        var zone = GameState.Get()?.GetFriendlySidePlayer()?.GetBattlefieldZone();
        var plane = Board.Get()?.FindCollider("DragPlane");
        if (zone == null || plane == null || position > zone.GetCards().Count + 1)
            throw new InvalidOperationException("Board position unavailable");
        var world = plane.bounds.center;
        world.x = zone.transform.position.x - (zone.GetCards().Count + 1) * zone.GetSlotWidth() / 2
            + (position - .5f) * zone.GetSlotWidth();
        world.z = zone.transform.position.z;
        Vector3 pixel = Camera.main.WorldToScreenPoint(world);
        if (!CameraUtils.Raycast(Camera.main, pixel, 1 << plane.gameObject.layer, out RaycastHit hit) || hit.collider != plane)
            throw new InvalidOperationException("Board position covered");
        return pixel;
    }
    internal static void Begin(ControlRequest request, JsonNode detail)
    {
        if (!available || running) throw new InvalidOperationException("Native gameplay drag unavailable or busy");
        CleanupFailure = null;
        var input = InputManager.Get(); var universal = UniversalInputManager.Get();
        var game = GameState.Get(); var scene = SceneMgr.Get();
        if (game?.GetGameEntity() == null || scene?.GetMode() != SceneMgr.Mode.GAMEPLAY
            || request.Text("matchId") != ClientBridge.DiagnosticMatchId)
            throw new InvalidOperationException("Native gameplay context changed");
        if (input == null || universal == null || !input.IsInputEnabled() || !input.PermitDecisionMakingInput()
            || universal.IsDialogActive() || universal.IsTouchMode() || input.GetHeldCard() != null || (game.IsInTargetMode() && request.Command != "choice-click"))
            throw new InvalidOperationException("Native gameplay input blocked (requires mouse mode)");
        clickingChoice = request.Command == "choice-click";
        from = clickingChoice ? ChoicePoint(request.EntityIds("entityIds")[0]) : EntityPoint(request.Integer("entityId"));
        bool clickingEntity = request.Command == "entity-drag" && request.Has("click");
        to = clickingChoice || clickingEntity ? from : request.Has("target") ? EntityPoint(request.Integer("target")) : BoardPoint(request.Integer("position"));
        detail["from"] = TestControlProtocol.List(new object[] { from.x, from.y });
        detail["to"] = TestControlProtocol.List(new object[] { to.x, to.y });
        detail["screen"] = TestControlProtocol.List(new object[] { Screen.width, Screen.height });
        detail["inputKind"] = clickingChoice ? "native-choice-click" : clickingEntity ? "native-entity-click" : "native-gameplay-drag";
        detail["clickable"] = true; detail["clickAccepted"] = false;
        detail["dragAccepted"] = false; detail["released"] = false;
        began = Time.realtimeSinceStartup; startFrame = Time.frameCount;
        gestureInput = input; gestureUniversal = universal; gestureGame = game;
        gestureGameEntity = game.GetGameEntity(); gestureScene = scene; gestureMatch = ClientBridge.DiagnosticMatchId;
        releasePending = false; running = true;
        try
        {
            Frame(from, true, false);
            detail["pressed"] = input.LeftMouseButtonDown;
            if (!input.LeftMouseButtonDown) throw new InvalidOperationException("Native press refused");
        }
        catch { try { Cancel(); } catch { /* recorded in CleanupFailure */ } throw; }
    }
    internal static bool Tick(JsonNode detail)
    {
        if (!running) return true;
        try
        {
            RequireContext();
            if (releasePending) return true;
            if (Time.frameCount <= startFrame) return false;
            float elapsed = Time.realtimeSinceStartup - began;
            if (elapsed > 5) throw new InvalidOperationException("Native drag deadline");
            bool release = elapsed >= .65f;
            Frame(Vector3.Lerp(from, to, Mathf.Clamp01(elapsed / .45f)), false, release);
            detail["dragAccepted"] = (bool)detail["dragAccepted"] || gestureInput.GetHeldCard() != null || gestureGame.IsInTargetMode();
            if (release)
            {
                // Retain ownership until the scheduler reconciles sends and cancels any residual targeting.
                detail["released"] = !gestureInput.LeftMouseButtonDown;
                if (gestureInput.LeftMouseButtonDown) throw new InvalidOperationException("Native release refused");
                releasePending = true;
            }
            return release;
        }
        catch { try { Cancel(); } catch (Exception cleanup) { detail["cleanupError"] = cleanup.GetBaseException().Message; } throw; }
    }
    private static void RequireContext()
    {
        if (gestureInput == null || gestureUniversal == null || gestureGame == null || gestureScene == null
            || !ReferenceEquals(InputManager.Get(), gestureInput) || !ReferenceEquals(UniversalInputManager.Get(), gestureUniversal)
            || !ReferenceEquals(GameState.Get(), gestureGame) || !ReferenceEquals(gestureGame.GetGameEntity(), gestureGameEntity)
            || !ReferenceEquals(SceneMgr.Get(), gestureScene) || gestureScene.GetMode() != SceneMgr.Mode.GAMEPLAY
            || gestureMatch != ClientBridge.DiagnosticMatchId || !gestureInput.IsInputEnabled()
            || !gestureInput.PermitDecisionMakingInput() || gestureUniversal.IsDialogActive() || gestureUniversal.IsTouchMode())
            throw new InvalidOperationException("Native gameplay drag context changed or input blocked");
    }
    private static void Frame(Vector3 point, bool pressed, bool released)
    {
        RequireContext();
        var input = gestureUniversal;
        object old = Position.GetValue(input);
        pointer = point; down = pressed; up = released; dispatch = true;
        try { Position.SetValue(input, point); Update.Invoke(gestureInput, null); }
        finally { try { Position.SetValue(input, old); } finally { dispatch = false; } }
    }
    internal static void Cancel()
    {
        if (!running) return;
        Exception failure = null;
        try
        {
            if (gestureInput != null)
            {
                // Cleanup the original input only. Never dispatch synthetic input into a changed scene/dialog.
                try { if (!clickingChoice) gestureInput.CancelTargetMode(); } catch (Exception error) { failure = error; }
                try { if (!clickingChoice) gestureInput.ReturnHeldCardToHand(); } catch (Exception error) { failure = failure ?? error; }
                try { LeftButton.SetValue(gestureInput, false); } catch (Exception error) { failure = failure ?? error; }
                try { MouseDownObject.SetValue(gestureInput, null); } catch (Exception error) { failure = failure ?? error; }
                try { TouchDragging.SetValue(gestureInput, false); } catch (Exception error) { failure = failure ?? error; }
                try { TargetingHeroPower.SetValue(gestureInput, false); } catch (Exception error) { failure = failure ?? error; }
                try { SetDragging.Invoke(gestureInput, new object[] { false }); } catch (Exception error) { failure = failure ?? error; }
                try
                {
                    if (gestureInput.LeftMouseButtonDown || gestureInput.GetHeldCard() != null || (!clickingChoice && gestureGame?.IsInTargetMode() == true))
                        failure = failure ?? new InvalidOperationException("Native drag cleanup left held input or targeting");
                }
                catch (Exception error) { failure = failure ?? error; }
            }
        }
        finally
        {
            running = false; dispatch = false; down = up = releasePending = false;
            gestureInput = null; gestureUniversal = null; gestureGame = null; gestureGameEntity = null; gestureScene = null; gestureMatch = null;
        }
        if (failure != null)
        {
            CleanupFailure = failure.GetBaseException().Message;
            throw new InvalidOperationException("Native drag cleanup failed", failure);
        }
    }
}
#endif
