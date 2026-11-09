using System;
using System.Collections.Generic;
using System.Linq;
using MiniJSON;
using UnityEngine;

// Native button chrome kit. One place that builds, resizes and judges the plate of a custom button cloned from a native UIBButton.
//
// A native Back / My Collection style button is two 3-slice strips (a metal frame and a gold plate) on one root: left cap, centre, right cap.
// The caps keep a uniform scale; only the centre stretches. Scaling the root (what Deck Guide and the Recipe guide used to do) stretches the
// caps and, with a label box widened by guesswork, lets the letters run onto the gold. Fit() instead widens the centre slices and moves the
// caps, grows the hit colliders by the same amount, and sizes the label's text box to the cream window (ButtonChromeRules), so UberText
// ResizeToFit shrinks the letters to fit rather than the plate to the letters.
//
// Audit()/State() are read-only: the test-control `buttons` command reports every active UIBButton's art, body and label rects plus the rule
// issues, so an agent can prove a button is right live (hslive buttons --check).
internal static class NativeButtonKit
{
    private static readonly System.Reflection.FieldInfo ButtonText = HarmonyLib.AccessTools.Field(typeof(UIBButton), "m_ButtonText");

    internal static Component LabelOf(UIBButton button) => ButtonText?.GetValue(button) as Component;

    // Marks a button this kit has fitted (also names it custom for the audit). Never read for behaviour.
    internal sealed class Fitted : MonoBehaviour
    {
        internal string Result = "";
        internal float WidthScale = 1f;
        internal float BaseAspect;   // worst raw cap aspect of the native source at the first Fit: the audit judges a clone against it, not against 1
        internal float BaseBody;   // hit width at the first Fit: the unit WidthScale is relative to, so Fit can be called again with another scale
    }

    // ---- slices -------------------------------------------------------------------------------------------------------------------------

    internal sealed class Strip
    {
        internal Transform Left, Center, Right;
        internal float Width;   // world extent along the reading axis
        internal bool Shown;    // drawn now: a hover glow strip is a third 3-slice strip that is hidden until the pointer is over the button
    }

    internal sealed class Chrome
    {
        internal Strip Frame, Plate;   // Frame = the widest shown strip, Plate = the gold strip inside it (null when the button has one strip only)
        internal readonly List<Strip> All = new List<Strip>();   // every strip, shown or not: all of them follow a width change
    }

    private static bool ShownUnder(Transform node, Transform root)
    {
        for (var at = node; at != null && at != root; at = at.parent) if (!at.gameObject.activeSelf) return false;
        return true;
    }

    // The 3-slice strips of a button: groups of exactly three non-text mesh children under one parent, ordered along `right`.
    internal static Chrome Find(UIBButton button, Vector3 right)
    {
        var groups = new Dictionary<Transform, List<MeshFilter>>();
        foreach (MeshFilter filter in button.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || ProfileFormKit.InText(filter.transform) || filter.transform.parent == null) continue;
            if (!groups.TryGetValue(filter.transform.parent, out var list)) groups[filter.transform.parent] = list = new List<MeshFilter>();
            list.Add(filter);
        }
        var strips = new List<Strip>();
        foreach (var group in groups.Values)
        {
            if (group.Count != 3) continue;
            var ordered = group.OrderBy(filter => Vector3.Dot(filter.transform.TransformPoint(filter.sharedMesh.bounds.center), right)).ToList();
            var strip = new Strip { Left = ordered[0].transform, Center = ordered[1].transform, Right = ordered[2].transform };
            float min = float.MaxValue, max = float.MinValue;
            foreach (var filter in ordered)
                if (SpanOf(filter, right, out float a, out float b)) { min = Math.Min(min, a); max = Math.Max(max, b); }
            strip.Width = max - min;
            strip.Shown = ordered.All(filter => ShownUnder(filter.transform, button.transform) && (filter.GetComponent<Renderer>()?.enabled ?? false));
            strips.Add(strip);
        }
        if (strips.Count == 0) return null;
        var shown = strips.Where(strip => strip.Shown).OrderByDescending(strip => strip.Width).ToList();
        if (shown.Count == 0) return null;
        var chrome = new Chrome { Frame = shown[0], Plate = shown.Count > 1 ? shown[1] : null };
        // Hidden strips (the hover glow, also one whose renderer is disabled) follow every width change, so they are never left narrow. Only
        // a strip about as wide as the plate or wider counts: a stray triple of small decoration meshes is not part of the plate.
        float reference = (chrome.Plate ?? chrome.Frame).Width;
        foreach (Strip strip in strips)
            if (strip.Shown || strip.Width >= reference * .9f) chrome.All.Add(strip);
        return chrome;
    }

    private static bool SpanOf(MeshFilter filter, Vector3 axis, out float min, out float max)
    {
        min = float.MaxValue; max = float.MinValue;
        if (filter == null || filter.sharedMesh == null) return false;
        Bounds local = filter.sharedMesh.bounds;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? local.min.x : local.max.x, (corner & 2) == 0 ? local.min.y : local.max.y,
                (corner & 4) == 0 ? local.min.z : local.max.z);
            float value = Vector3.Dot(filter.transform.TransformPoint(point), axis);
            min = Math.Min(min, value); max = Math.Max(max, value);
        }
        return true;
    }

    // The cap aspect furthest from 1 over every cap of every strip, read along the axes that run along the screen.
    internal static float RawCapAspect(Chrome chrome, Camera view)
    {
        float aspect = 1f;
        foreach (Strip strip in chrome.All)
            foreach (Transform cap in new[] { strip.Left, strip.Right })
            {
                Vector3 s = cap.lossyScale;
                float ratio = ButtonChromeRules.CapAspect(new[] { s.x, s.y, s.z }, Local(cap, view.transform.right), Local(cap, view.transform.up));
                if (Math.Abs(ratio - 1f) > Math.Abs(aspect - 1f)) aspect = ratio;
            }
        return aspect;
    }

    private static float? SpanWidth(Strip strip, Vector3 axis)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (Transform part in new[] { strip.Left, strip.Center, strip.Right })
            if (SpanOf(part, axis, out float a, out float b)) { min = Math.Min(min, a); max = Math.Max(max, b); }
        return max > min ? max - min : (float?)null;
    }

    private static bool SpanOf(Transform part, Vector3 axis, out float min, out float max) => SpanOf(part.GetComponent<MeshFilter>(), axis, out min, out max);

    // Local axis (0..2) of a transform that points most along `axis`.
    private static int AxisAlong(Transform node, Vector3 axis) => ButtonChromeRules.Dominant(Local(node, axis));

    private static float[] Local(Transform node, Vector3 axis)
    {
        Vector3 local = node.InverseTransformDirection(axis);
        return new[] { local.x, local.y, local.z };
    }

    // Widen (or narrow, delta < 0) one strip by `delta` world units along `right`, keeping its centre: caps move out, centre stretches.
    // The centre slice must have a real width and keep one after the change: a zero-width centre would scale by Infinity.
    private static bool CanGrow(Strip strip, Vector3 right, float delta) =>
        SpanOf(strip.Center, right, out float c0, out float c1) && c1 - c0 > 1e-4f && c1 - c0 + delta > 1e-4f;

    private static bool Grow(Strip strip, Vector3 right, float delta)
    {
        if (!CanGrow(strip, right, delta)) return false;
        SpanOf(strip.Center, right, out float c0, out float c1);
        float factor = (c1 - c0 + delta) / (c1 - c0), middle = (c0 + c1) / 2;
        strip.Left.position -= right * (delta / 2);
        strip.Right.position += right * (delta / 2);
        int axis = AxisAlong(strip.Center, right);
        Vector3 scale = strip.Center.localScale;
        scale[axis] *= factor;
        strip.Center.localScale = scale;
        if (SpanOf(strip.Center, right, out float n0, out float n1)) strip.Center.position += right * (middle - (n0 + n1) / 2);
        return true;
    }

    private static bool Extent(BoxCollider box, Vector3 right, out float min, out float max)
    {
        min = float.MaxValue; max = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = box.center + Vector3.Scale(box.size, new Vector3((corner & 1) == 0 ? -.5f : .5f, (corner & 2) == 0 ? -.5f : .5f, (corner & 4) == 0 ? -.5f : .5f));
            float value = Vector3.Dot(box.transform.TransformPoint(point), right);
            min = Math.Min(min, value); max = Math.Max(max, value);
        }
        return max > min;
    }

    private static void GrowColliders(UIBButton button, Vector3 right, float delta)
    {
        foreach (BoxCollider box in button.GetComponentsInChildren<BoxCollider>(true))
        {
            if (!Extent(box, right, out float min, out float max)) continue;
            Vector3 size = box.size;
            size[AxisAlong(box.transform, right)] *= (max - min + delta) / (max - min);
            box.size = size;
        }
    }

    // Hit width of the button along `right`: the union of its box colliders (what a click can land on).
    private static float BodyWidth(UIBButton button, Vector3 right)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (BoxCollider box in button.GetComponentsInChildren<BoxCollider>(true))
            if (Extent(box, right, out float min, out float max)) { lo = Math.Min(lo, min); hi = Math.Max(hi, max); }
        return hi > lo ? hi - lo : 0f;
    }

    // ---- fit ----------------------------------------------------------------------------------------------------------------------------

    internal sealed class FitResult
    {
        internal bool Sliced, Widened;
        internal float Delta;      // world units added to the art width
        internal string Detail = "";
    }

    // Make the clone's plate `widthScale` times as wide as its hit body was at the first Fit (>= 0.5; 1 = native width) without distorting the end
    // caps, then size the label's text box to the cream window. Idempotent: calling it again with another scale sets that width, so a layout can
    // re-fit a button per slot. Call after the layer is set (the camera decides the reading axis); needs no position and works on an inactive
    // clone. All-or-nothing: if any strip cannot take the change nothing moves and Widened stays false. A button without 3-slice strips keeps
    // its plate and only gets its label bounded to the body: CHECK result.Sliced (FitOrThrow does).
    internal static FitResult Fit(UIBButton button, float widthScale = 1f)
    {
        var result = new FitResult();
        Camera view = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
        if (view == null) { result.Detail = "no camera"; return result; }
        Vector3 right = view.transform.right;
        Chrome chrome = Find(button, right);
        result.Sliced = chrome != null && chrome.Plate != null;
        var marker = button.GetComponent<Fitted>() ?? button.gameObject.AddComponent<Fitted>();
        float body = BodyWidth(button, right);
        if (marker.BaseAspect <= 0 && chrome != null) marker.BaseAspect = RawCapAspect(chrome, view);
        if (marker.BaseBody <= 0) marker.BaseBody = body > 0 ? body : (result.Sliced ? chrome.Plate.Width : 0);
        if (result.Sliced && widthScale >= .5f && marker.BaseBody > 0)
        {
            float delta = marker.BaseBody * widthScale - (body > 0 ? body : marker.BaseBody * marker.WidthScale);
            if (Math.Abs(delta) <= 1e-4f) result.Widened = widthScale > 1.0001f;
            else if (chrome.All.All(strip => CanGrow(strip, right, delta)))
            {
                foreach (Strip strip in chrome.All) Grow(strip, right, delta);
                GrowColliders(button, right, delta);
                result.Widened = widthScale > 1.0001f; result.Delta = delta;
            }
            else result.Detail = "a centre slice cannot take the change";
        }
        FitLabel(button, right, chrome);
        marker.WidthScale = result.Detail.Length == 0 ? widthScale : marker.WidthScale;
        marker.Result = result.Sliced ? (result.Widened ? "widened" : "native") : "unsliced";
        return result;
    }

    // Fit, failing closed: a clone that is not a 3-slice plate, or whose widening was refused, is not a button we can vouch for. Callers destroy
    // the clone (their Create already does on a throw) and show nothing.
    internal static FitResult FitOrThrow(UIBButton button, float widthScale = 1f)
    {
        FitResult fit = Fit(button, widthScale);
        if (!fit.Sliced) throw new InvalidOperationException(button.name + " is not a 3-slice plate (" + fit.Detail + ")");
        if (fit.Detail.Length != 0) throw new InvalidOperationException(button.name + " fit refused: " + fit.Detail);
        if (widthScale > 1.0001f && !fit.Widened) throw new InvalidOperationException(button.name + " was not widened");
        return fit;
    }

    // Distance from the hit body's edge to the art's edge (screen pixels) of a plate: what a layout must keep clear on top of the hit rect (the frame
    // art reaches about 36 px past the body on each side at 1080p). Null until the plate has an on-screen body and art.
    internal static FrameMargin? FrameMarginOf(UIBButton button)
    {
        Camera view = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
        if (view == null) return null;
        ButtonChromeRules.Facts facts = Measure(button, view, null);
        if (!facts.Art.HasValue || !facts.Body.HasValue) return null;
        DeckRect art = facts.Art.Value, body = facts.Body.Value;
        var margin = new FrameMargin(body.X - art.X, art.X + art.Width - (body.X + body.Width), (art.Height - body.Height) / 2);
        return margin.Left >= 0 && margin.Right >= 0 && margin.Vertical >= 0 ? margin : (FrameMargin?)null;
    }

    // Text box = LabelFillShare of the cream window (of the body for a button without slices): UberText ResizeToFit then keeps every letter
    // inside it. The box is resized to exactly that, wider or narrower than the clone's own.
    private static void FitLabel(UIBButton button, Vector3 right, Chrome chrome)
    {
        Component text = LabelOf(button);
        if (text == null) return;
        float room;
        if (chrome != null && chrome.Plate != null)
        {
            if (!SpanOf(chrome.Plate.Left, right, out float l0, out float l1) || !SpanOf(chrome.Plate.Right, right, out float r0, out float r1)) return;
            float plate = r1 - l0, inset = ButtonChromeRules.CreamInsetShare * ((l1 - l0) + (r1 - r0));
            room = (plate - inset) * ButtonChromeRules.LabelFillShare;
        }
        else
        {
            float body = BodyWidth(button, right);
            if (body <= 0) return;
            room = body * .85f;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            Bounds? box = ProfileFormKit.RectBounds(text);
            if (!box.HasValue) return;
            float current = (Mathf.Abs(right.x) * box.Value.size.x + Mathf.Abs(right.y) * box.Value.size.y + Mathf.Abs(right.z) * box.Value.size.z);
            if (current <= 0 || Math.Abs(current - room) < room * .01f) return;
            if (!ProfileFormKit.ScaleRect(text, "Width", room / current)) return;
            ProfileFormKit.UpdateNow(text);
        }
    }

    // ---- audit --------------------------------------------------------------------------------------------------------------------------

    private static Rect Union(Rect? a, Rect b) => a == null ? b : Rect.MinMaxRect(Mathf.Min(a.Value.xMin, b.xMin), Mathf.Min(a.Value.yMin, b.yMin),
        Mathf.Max(a.Value.xMax, b.xMax), Mathf.Max(a.Value.yMax, b.yMax));

    private static float Round(float value) => Mathf.Round(value * 10) / 10;
    private static JsonList Rect4(DeckRect? r) => r == null ? null
        : TestControlProtocol.List(new object[] { Round(r.Value.X), Round(r.Value.Y), Round(r.Value.Width), Round(r.Value.Height) });
    private static DeckRect? Deck(Rect? r) => r == null ? (DeckRect?)null : new DeckRect(r.Value.x, r.Value.y, r.Value.width, r.Value.height);

    internal static string PathOf(Transform root, Transform part)
    {
        var names = new List<string>();
        for (var at = part; at != null && at != root; at = at.parent) names.Insert(0, at.name);
        return names.Count == 0 ? "." : string.Join("/", names);
    }

    private static Rect PieceRect(Transform piece, Camera view) => DeckLayout.Project(piece.GetComponent<Renderer>().bounds, view);

    // Everything the rules need, in screen pixels, plus the per-mesh parts for the dump.
    internal static ButtonChromeRules.Facts Measure(UIBButton button, Camera view, List<JsonNode> meshes)
    {
        var facts = new ButtonChromeRules.Facts();
        Transform root = button.transform;
        Rect? art = null, body = null;
        foreach (Renderer renderer in button.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || ProfileFormKit.InText(renderer.transform)) continue;
            Rect rect = DeckLayout.Project(renderer.bounds, view);
            if (rect.width <= 0 && rect.height <= 0) continue;
            art = Union(art, rect);
            if (meshes == null) continue;
            var node = new JsonNode { { "path", PathOf(root, renderer.transform) }, { "rect", Rect4(Deck(rect)) } };
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                Bounds local = filter.sharedMesh.bounds;
                node["mesh"] = filter.sharedMesh.name;
                node["meshSize"] = TestControlProtocol.List(new object[] { local.size.x, local.size.y, local.size.z });
                Vector3 s = renderer.transform.lossyScale;
                node["lossyScale"] = TestControlProtocol.List(new object[] { s.x, s.y, s.z });
                Vector3 p = renderer.transform.localPosition;
                node["localPosition"] = TestControlProtocol.List(new object[] { p.x, p.y, p.z });
            }
            Material material = renderer.sharedMaterial;
            if (material != null) node["material"] = material.name;
            meshes.Add(node);
        }
        foreach (Collider collider in button.GetComponentsInChildren<Collider>(false))
            if (collider.enabled) body = Union(body, DeckLayout.ColliderRect(collider, view));
        facts.Art = Deck(art); facts.Body = Deck(body);
        Component text = LabelOf(button);
        if (text != null)
        {
            Bounds? drawn = ProfileFormKit.RenderedBounds(text, true);
            if (drawn.HasValue) facts.Label = Deck(ProfileFormKit.ScreenRect(drawn.Value, view));
        }
        Chrome chrome = Find(button, view.transform.right);
        if (chrome == null) return facts;
        // Native art is not uniform in every axis (live 2026-11-05: Back and My Collection read 0.757 raw), so a stretch is the raw aspect relative to the
        // native source's, recorded at the first Fit. Without that baseline (a native button, or one this kit did not fit) the raw value is reported
        // but not judged.
        float baseline = button.GetComponent<Fitted>()?.BaseAspect ?? 0f;
        facts.RawCapAspect = RawCapAspect(chrome, view);
        facts.CapAspect = ButtonChromeRules.RelativeAspect(facts.RawCapAspect, baseline);
        if (chrome.Plate != null)
        {
            float glow = float.MaxValue;
            foreach (Strip strip in chrome.All)
                if (!strip.Shown && SpanWidth(strip, view.transform.right) is float width && width > 0) glow = Math.Min(glow, width / chrome.Plate.Width);
            if (glow < float.MaxValue) facts.GlowToPlate = glow;
        }
        if (chrome.Plate != null)
        {
            Rect left = PieceRect(chrome.Plate.Left, view), centre = PieceRect(chrome.Plate.Center, view), rightCap = PieceRect(chrome.Plate.Right, view);
            facts.Plate = Deck(Union(Union(left, centre), rightCap)); facts.CapLeft = left.width; facts.CapRight = rightCap.width;
        }
        return facts;
    }

#if !SOLOSTONE_RELEASE
    // Test control `buttons`: every active UIBButton, native and ours, with art, body, label and plate rects and the rule issues.
    // dump adds each mesh part. custom = this kit fitted it, or its name says it is ours.
    internal static JsonNode State(bool dump)
    {
        var node = new JsonNode { { "screen", TestControlProtocol.List(new object[] { Screen.width, Screen.height }) } };
        var list = new JsonList();
        // Clickability and ids come from the same snapshot `ui` serves (PegUI.FindHitElement at the element's own samples), keyed by the element.
        var listed = new Dictionary<object, UiElement>();
        try { foreach (UiElement element in NativeUiTree.Read().Elements) if (element.Handle != null) listed[element.Handle] = element; }
        catch (Exception error) { node["uiError"] = error.Message; }   // the verdict fails on it: without the snapshot nothing can be called clickable
        foreach (UIBButton button in UnityEngine.Object.FindObjectsByType<UIBButton>(FindObjectsSortMode.None))
        {
            if (button == null || !button.gameObject.activeInHierarchy) continue;
            var marker = button.GetComponent<Fitted>();
            bool custom = marker != null || button.name.StartsWith("SoloStone", StringComparison.Ordinal) || button.name.StartsWith("Local", StringComparison.Ordinal);
            var one = new JsonNode { { "name", button.name }, { "custom", custom }, { "fitted", marker == null ? null : marker.Result } };
            try
            {
                Camera view = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
                if (view == null) { one["error"] = "no camera"; list.Add(one); continue; }
                var meshes = dump ? new List<JsonNode>() : null;
                var facts = Measure(button, view, meshes);
                Component text = LabelOf(button);
                string label = text == null ? null : ProfileFormKit.TextOf(text);
                one["label"] = label;
                one["art"] = Rect4(facts.Art); one["body"] = Rect4(facts.Body); one["text"] = Rect4(facts.Label); one["plate"] = Rect4(facts.Plate);
                one["sliced"] = facts.Sliced;
                if (listed.TryGetValue(button, out UiElement entry))
                {
                    one["id"] = entry.Id;
                    one["clickable"] = !entry.OnScreen ? "off screen" : entry.Hit ? "yes" : entry.HitBy ?? "no";
                    one["enabled"] = entry.Enabled;
                }
                else one["clickable"] = "unlisted";
                if (facts.Sliced)
                {
                    one["cream"] = Rect4(ButtonChromeRules.Cream(facts.Plate.Value, facts.CapLeft.Value, facts.CapRight.Value));
                    one["capAspect"] = Round(facts.CapAspect * 100) / 100;   // relative to the native source; 1 = not stretched
                    one["capAspectRaw"] = Round(facts.RawCapAspect * 100) / 100;
                }
                var issues = new JsonList();
                foreach (string issue in ButtonChromeRules.Judge(facts, !string.IsNullOrEmpty(label))) issues.Add(issue);
                one["issues"] = issues;
                if (dump)
                {
                    var parts = new JsonList(); foreach (var mesh in meshes) parts.Add(mesh); one["parts"] = parts;
                    var kinds = new JsonList();
                    foreach (Component component in button.GetComponentsInChildren<Component>(true))
                        if (component != null && !(component is Transform) && !(component is Renderer) && !(component is MeshFilter))
                            kinds.Add(PathOf(button.transform, component.transform) + ":" + component.GetType().Name);
                    one["components"] = kinds;
                }
            }
            catch (Exception error) { one["error"] = error.Message; }
            list.Add(one);
        }
        node["buttons"] = list;
        return node;
    }
#endif
}
