using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

internal static class DeckLayout
{
    private static Type menuTextType;
    internal static Type MenuTextType => menuTextType ?? (menuTextType = GameTypes.ByName("UberText"));
    private static readonly FieldInfo Waiting = AccessTools.Field(typeof(MulliganManager), "m_waitingForUserInput");
    private static readonly List<Placement> placements = new List<Placement>();
    private static MulliganManager owner, departing;
    private static ManualLogSource log;
    private static bool failed;
    private static int width, height;
    private static Camera camera;
    private static Vector3 cameraPosition;
    private static Quaternion cameraRotation;
    private static float cameraSize, cameraFov;
    private sealed class Placement
    {
        internal Transform Root;
        internal Renderer Face;
        internal DeckRect Target;
        internal Vector3 Position, Scale, AppliedPosition, AppliedScale;
        internal Quaternion Rotation, AppliedRotation;
        internal Placement(GameObject root, DeckRect target, Renderer face = null)
        {
            Root = root.transform; Target = target; Face = face;
            Position = Root.localPosition; Scale = Root.localScale; Rotation = Root.localRotation;
        }
        internal bool Changed => Root == null || (Root.localPosition - AppliedPosition).sqrMagnitude > .000001f
            || (Root.localScale - AppliedScale).sqrMagnitude > .000001f || Quaternion.Angle(Root.localRotation, AppliedRotation) > .01f;
        internal void Restore()
        {
            if (Root == null) return;
            Root.localPosition = Position; Root.localScale = Scale; Root.localRotation = Rotation;
        }
        internal string Summary;
        internal void Apply()
        {
            var collider = Root.GetComponent<Collider>();
            Camera view = CameraUtils.FindFirstByLayerMask(1 << (Face != null ? Face.gameObject.layer : collider != null ? collider.gameObject.layer : Root.gameObject.layer));
            if (view == null) throw new InvalidOperationException("mulligan actor camera unavailable");
            Rect rect = Face != null ? FaceRect(Face, view) : collider != null ? ColliderRect(collider, view) : ScreenRect(Root.gameObject, view);
            if (rect.width < 1 || rect.height < 1) throw new InvalidOperationException("mulligan actor bounds unavailable");
            Root.localScale = Scale * Mathf.Min(Target.Width / rect.width, Target.Height / rect.height);
            rect = Face != null ? FaceRect(Face, view) : collider != null ? ColliderRect(collider, view) : ScreenRect(Root.gameObject, view);
            Vector3 current = view.WorldToScreenPoint(Root.position);
            var from = new Vector3(rect.center.x, rect.center.y, current.z);
            var to = new Vector3(Target.X + Target.Width / 2, Target.Y + Target.Height / 2, current.z);
            Root.position += view.ScreenToWorldPoint(to) - view.ScreenToWorldPoint(from);
            AppliedPosition = Root.localPosition; AppliedScale = Root.localScale; AppliedRotation = Root.localRotation;
            rect = Face != null ? FaceRect(Face, view) : collider != null ? ColliderRect(collider, view) : ScreenRect(Root.gameObject, view);
            Summary = Root.name + " camera=" + view.name + " rect=" + rect;
        }
    }
    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        foreach (Type type in new[] { typeof(OptionsMenu), typeof(SoundOptionsMenu), typeof(ButtonListMenu), typeof(AlertPopup) })
        {
            var hide = AccessTools.Method(type,"Hide");
            if (hide != null) harmony.Patch(hide,prefix:new HarmonyMethod(typeof(DeckLayout),nameof(RestoreMenus)));
        }
        // Restore before the native coroutine captures positions/scales for replacement and transition to hand.
        foreach (string method in new[] { "BeginDealNewCards", "SkipMulligan", "OnDestroy" })
            harmony.Patch(AccessTools.Method(typeof(MulliganManager), method), prefix: new HarmonyMethod(typeof(DeckLayout), nameof(BeforeNativeTransition)));
        harmony.Patch(AccessTools.Method(typeof(MulliganManager), "ForceMulliganActive"), prefix: new HarmonyMethod(typeof(DeckLayout), nameof(BeforeActiveChange)));
    }
    private static void BeforeNativeTransition(MulliganManager __instance)
    {
        try { if (owner == __instance) Restore(); departing = __instance; }
        catch (Exception error) { Fail(error); }
    }
    private static void BeforeActiveChange(MulliganManager __instance, bool __0) { if (!__0) BeforeNativeTransition(__instance); }
    internal static void Tick(bool enabled)
    {
        if (failed) return;
        try
        {
            if (!enabled) { if (placements.Count != 0) Restore(); return; }
            MulliganManager mulligan = MulliganManager.Get();
            if (mulligan == null || mulligan == departing || !mulligan.IsMulliganActive() || mulligan.IsMulliganIntroActive()
                || mulligan.IsCustomIntroActive() || Waiting == null || !(bool)Waiting.GetValue(mulligan))
            { if (placements.Count != 0) Restore(); return; }
            if (GameState.Get() == null || !GameState.Get().IsMulliganPhase()
                || GameState.Get().GetBooleanGameOption(GameEntityOption.MULLIGAN_USES_ALTERNATE_ACTORS))
            { if (placements.Count != 0) Restore(); return; }
            List<Card> cards = mulligan.GetStartingCards();
            if (cards == null || (cards.Count != 3 && cards.Count != 4)) { Restore(); return; }
            if (owner != mulligan)
            {
                Restore();
                // StartingCards is the native replaceable list: neither Coin nor opponent hand enters this row.
                foreach (Card card in cards)
                    if (card == null || card.GetActor() == null || !card.GetActor().transform.IsChildOf(card.transform)) return;
                camera = CameraUtils.FindFirstByLayerMask(1 << cards[0].GetActor().gameObject.layer);
                if (camera == null) return;
                owner = mulligan;
                DeckRect[] row = DeckMulliganGeometry.Cards(Screen.width, Screen.height, cards.Count);
                for (int i = 0; i < cards.Count; i++) placements.Add(new Placement(cards[i].gameObject, row[i], cards[i].GetActor().GetMeshRenderer()));
                GameObject confirm = mulligan.GetMulliganButton();
                if (confirm != null && confirm.activeInHierarchy) placements.Add(new Placement(confirm, DeckMulliganGeometry.Confirm(Screen.width, Screen.height)));
                if (mulligan.mulliganButtonWidget != null && mulligan.mulliganButtonWidget.gameObject.activeInHierarchy)
                    placements.Add(new Placement(mulligan.mulliganButtonWidget.gameObject, DeckMulliganGeometry.Confirm(Screen.width, Screen.height)));
                // Native banner already fits above the cards; its animated glow is not a sizing bound.
                Apply();
                log?.LogInfo("DECK mulligan " + Screen.width + "x" + Screen.height + ": " + string.Join("; ", placements.ConvertAll(p => p.Summary).ToArray()));
                return;
            }
            bool changed = width != Screen.width || height != Screen.height || cameraPosition != camera.transform.position
                || cameraRotation != camera.transform.rotation || cameraSize != camera.orthographicSize || cameraFov != camera.fieldOfView;
            foreach (Placement place in placements) changed |= place.Changed;
            if (changed)
            {
                // Rebuild targets after resize, always starting from the exact unmodified native transforms.
                if (width != Screen.width || height != Screen.height) { Restore(); Tick(enabled); return; }
                Apply();
            }
        }
        catch (Exception error) { Fail(error); }
    }
    private static void Apply()
    {
        foreach (Placement place in placements) place.Restore();
        foreach (Placement place in placements) place.Apply();
        width = Screen.width; height = Screen.height;
        cameraPosition = camera.transform.position; cameraRotation = camera.transform.rotation;
        cameraSize = camera.orthographicSize; cameraFov = camera.fieldOfView;
    }
    internal static void Restore()
    {
        foreach (Placement place in placements) place.Restore();
        placements.Clear(); owner = null; camera = null;
    }
    private static void Fail(Exception error)
    {
        failed = true;
        try { Restore(); } catch { }
        log?.LogWarning("DECK mulligan layout disabled for session: " + error.Message);
    }
    internal static Rect ScreenRect(GameObject root, Camera view)
    {
        Rect rect = Rect.zero;
        bool any = false;
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled) continue;
            Rect one = Project(renderer.bounds, view);
            rect = any ? Rect.MinMaxRect(Mathf.Min(rect.xMin, one.xMin), Mathf.Min(rect.yMin, one.yMin),
                Mathf.Max(rect.xMax, one.xMax), Mathf.Max(rect.yMax, one.yMax)) : one;
            any = true;
        }
        return rect;
    }
    internal static Rect FaceRect(Renderer face, Camera view)
    {
        var mesh = face.GetComponent<MeshFilter>()?.sharedMesh;
        return mesh == null ? Project(face.bounds, view) : Project(mesh.bounds, view, face.transform);
    }
    internal static Rect ColliderRect(Collider collider, Camera view)
    {
        if (collider is BoxCollider box) return Project(new Bounds(box.center, box.size), view, box.transform);
        return Project(collider.bounds, view);
    }
    internal static Rect Project(Bounds bounds, Camera view, Transform local = null)
    {
        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
            point = view.WorldToScreenPoint(local == null ? point : local.TransformPoint(point));
            left = Mathf.Min(left, point.x); right = Mathf.Max(right, point.x);
            bottom = Mathf.Min(bottom, point.y); top = Mathf.Max(top, point.y);
        }
        return Rect.MinMaxRect(left, bottom, right, top);
    }
    private sealed class MenuPlacement
    {
        internal Transform Text;
        internal string Key;
        internal Vector3 Scale, Applied;
    }
    private static readonly Dictionary<Transform,MenuPlacement> menuText = new Dictionary<Transform,MenuPlacement>();
    internal static void RestoreMenus()
    {
        foreach(var saved in menuText.Values) if(saved.Text!=null && saved.Text.localScale==saved.Applied) saved.Text.localScale=saved.Scale;
        menuText.Clear();
    }
    private static readonly FieldInfo CurrentDialog = AccessTools.Field(typeof(DialogManager), "m_currentDialog");
    private static float nextMenuScan;
    // Native menus enlarge their label text a little for the handheld screen. Polled a few times a second while a menu is
    // open (the menus animate in, so one measurement at Show would be taken at the wrong size); nothing runs otherwise.
    internal static void TickMenus(bool enabled)
    {
        if (!enabled) { if (menuText.Count != 0) RestoreMenus(); return; }
        float now = Time.realtimeSinceStartup;
        if (now < nextMenuScan) return;
        nextMenuScan = now + .3f;
        var options = OptionsMenu.Get(); var sound = SoundOptionsMenu.Get(); var game = GameMenu.Get();
        var dialogs = DialogManager.Get();
        Component root = options != null && options.IsShown() ? options
            : sound != null && sound.IsShown() ? sound
            : game != null && game.GameMenuIsShown() ? game
            : dialogs != null ? CurrentDialog?.GetValue(dialogs) as AlertPopup : null;
        if (root == null) { if (menuText.Count != 0) RestoreMenus(); return; }
        // Buttons only: dropdown and text-field labels sit in clipped boxes and must not grow.
        foreach (UIBButton element in root.GetComponentsInChildren<UIBButton>(false)) MenuText(element);
    }
    private static bool ElementRect(PegUIElement element, Camera view, out Rect rect)
    {
        rect = Rect.zero;
        bool any = false;
        foreach (Collider collider in element.GetComponents<Collider>())
        {
            if (!collider.enabled) continue;
            Rect one = ColliderRect(collider, view);
            rect = any ? Rect.MinMaxRect(Mathf.Min(rect.xMin, one.xMin), Mathf.Min(rect.yMin, one.yMin),
                Mathf.Max(rect.xMax, one.xMax), Mathf.Max(rect.yMax, one.yMax)) : one;
            any = true;
        }
        return any && rect.width > 0 && rect.height > 0;
    }
    private static void MenuText(PegUIElement row)
    {
        if(row==null || !row.gameObject.activeInHierarchy) return;
        var type=MenuTextType; if(type==null) return;
        var view=CameraUtils.FindFirstByLayerMask(1<<row.gameObject.layer); if(view==null) return;
        if(!ElementRect(row,view,out var bounds)) return;
        // The row's own size is part of the key: a row measured mid-animation is measured again once it settles.
        string key=row.GetInstanceID()+":"+Mathf.RoundToInt(bounds.height/2);
        foreach(Component label in row.GetComponentsInChildren(type,false))
        {
            Transform text=label.transform;
            if(menuText.TryGetValue(text,out var previous))
            {
                if(previous.Key==key && text.localScale==previous.Applied) continue;
                if(text.localScale==previous.Applied) text.localScale=previous.Scale;
                menuText.Remove(text);
            }
            Rect rect=ScreenRect(label.gameObject,view);
            if(rect.width<=0||rect.height<=0) continue;
            float scale=Mathf.Min(1.2f,22f/rect.height, bounds.width*.8f/rect.width,bounds.height*.85f/rect.height);
            if(scale<=1) continue;
            var saved=new MenuPlacement{Text=text,Key=key,Scale=text.localScale};
            text.localScale*=scale;saved.Applied=text.localScale;menuText[text]=saved;
        }
    }
}
