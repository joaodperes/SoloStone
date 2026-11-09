using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Native form parts for the save screens, cloned from the retail Options menu prefab: the Fullscreen checkbox
// (toggles, check glyph, label style) and the Resolution dropdown's arrow (the cheats header). Loaded raw (never
// instantiated as an OptionsMenu, so no Options side effects) into an inactive holder, then cloned per row.
// Every failure leaves Ready false and the screens fall back to native menu planks; a failed load is retried
// once per picker open (ProfileFormRetry).
internal static class ProfileFormKit
{
    private const string OptionsPrefab = "OptionsMenu.prefab:a6e5621068fd7c8429475b3e1a1aa991";
    internal static readonly Type TextType = GameTypes.ByName("UberText");
    private static readonly PropertyInfo TextValue = TextType == null ? null : AccessTools.Property(TextType, "Text");
    private static readonly PropertyInfo TextColorValue = TextType == null ? null : AccessTools.Property(TextType, "TextColor");
    private static readonly PropertyInfo TextAlignment = TextType == null ? null : AccessTools.Property(TextType, "Alignment");
    private static readonly MethodInfo TextRect = TextType == null ? null : AccessTools.Method(TextType, "GetBounds", Type.EmptyTypes);
    private static readonly MethodInfo TextGlyphs = TextType == null ? null : AccessTools.Method(TextType, "GetTextBounds", Type.EmptyTypes);
    private static readonly MethodInfo TextFont = TextType == null ? null : AccessTools.Method(TextType, "GetLocalizedFont", Type.EmptyTypes);
    private static readonly MethodInfo TextUpdate = TextType == null ? null : AccessTools.Method(TextType, "UpdateNow", new[] { typeof(bool) });
    private static readonly FieldInfo CheckLabel = AccessTools.Field(typeof(CheckBox), "m_uberText");
    private static readonly FieldInfo PlankText = AccessTools.Field(typeof(UIBButton), "m_ButtonText");

    private static GameObject holder, checkTemplate, dropTemplate;
    private static string arrowPath;
    private static readonly ProfileFormRetry retry = new ProfileFormRetry();
    internal static string State { get; private set; } = "idle";
    internal static bool Ready => retry.Loaded && checkTemplate != null && dropTemplate != null && TextType != null;

    // Async raw load; ready() runs on the main thread once the templates exist (or never, on failure).
    // newOpen: a fresh picker open, which may retry one earlier failure.
    internal static void Request(Action ready, bool newOpen)
    {
        if (!retry.Start(newOpen)) return;
        State = "loading";
        try
        {
            NativeRewardUI.LoadRaw(OptionsPrefab, raw => {
                try
                {
                    var options = raw == null ? null : raw.GetComponent<OptionsMenu>();
                    if (options == null || options.m_fullScreenCheckbox == null || options.m_graphicsRes == null)
                        throw new InvalidOperationException("Options prefab parts missing raw=" + (raw != null) + " menu=" + (options != null));
                    if (holder == null)
                    {
                        holder = new GameObject("SoloStoneSaveFormKit");
                        holder.SetActive(false);
                        UnityEngine.Object.DontDestroyOnLoad(holder);
                    }
                    checkTemplate = Template(options.m_fullScreenCheckbox.gameObject, "SaveFormCheckBox");
                    dropTemplate = Template(options.m_graphicsRes.gameObject, "SaveFormDropdown");
                    // A live DropdownControl would wire its own open/close listeners at Start: rows use its arrow only,
                    // found again in each clone by its path under the template root.
                    var control = dropTemplate.GetComponent<DropdownControl>()
                        ?? throw new InvalidOperationException("Dropdown control missing");
                    arrowPath = Path(dropTemplate.transform, control.m_button);
                    UnityEngine.Object.DestroyImmediate(control);
                    retry.Succeeded(); State = "ready";
                    NativeProfileSelector.Log("SAVE_FORM kit ready");
                }
                catch (Exception error) { Fail("unavailable", error); return; }
                // The menu rebuild is not part of the load: if it throws, the good templates stay Ready.
                try { ready?.Invoke(); }
                catch (Exception error) { NativeProfileSelector.Log("SAVE_FORM rebuild after kit load failed: " + error.Message); }
            });
        }
        catch (Exception error) { Fail("request failed", error); }
    }

    // A partial load must not leave templates that make Ready true.
    private static void Fail(string what, Exception error)
    {
        retry.Failed(); State = "failed: " + error.Message;
        foreach (var template in new[] { checkTemplate, dropTemplate }) if (template != null) UnityEngine.Object.Destroy(template);
        checkTemplate = dropTemplate = null; arrowPath = null;
        NativeProfileSelector.Log("SAVE_FORM kit " + what + ": " + error.Message);
    }

    // Holder sits at the origin, so the template keeps the part's prefab-space rotation and world scale.
    private static GameObject Template(GameObject part, string name)
    {
        var copy = UnityEngine.Object.Instantiate(part, holder.transform);
        copy.name = name;
        copy.transform.localPosition = Vector3.zero;
        copy.transform.rotation = part.transform.rotation;
        copy.transform.localScale = part.transform.lossyScale;
        copy.SetActive(true);
        return copy;
    }

    // Clones stay under the inactive holder (no Awake/Start) until the caller re-parents them into the menu.
    internal static CheckBox CloneCheckBox() => UnityEngine.Object.Instantiate(checkTemplate, holder.transform).GetComponent<CheckBox>();
    internal static GameObject CloneCheckGlyph()
    {
        var check = checkTemplate.GetComponent<CheckBox>().m_check;
        if (check == null) return null;
        var copy = UnityEngine.Object.Instantiate(check, holder.transform);
        copy.transform.rotation = check.transform.rotation; copy.transform.localScale = check.transform.lossyScale;
        copy.SetActive(true);
        return copy;
    }
    internal static GameObject CloneArrow()
    {
        var arrow = Find(dropTemplate, arrowPath)?.GetComponent<UIBButton>();
        if (arrow == null) return null;
        var copy = UnityEngine.Object.Instantiate(arrow.gameObject, holder.transform);
        copy.transform.rotation = arrow.transform.rotation; copy.transform.localScale = arrow.transform.lossyScale;
        copy.SetActive(true);
        return copy;
    }

    // The copy of part (under sourceRoot) inside cloneRoot, by relative path; null when part is outside sourceRoot.
    internal static Transform Counterpart(Transform sourceRoot, Transform part, Transform cloneRoot)
    {
        if (part == null || !part.IsChildOf(sourceRoot)) return null;
        string path = Path(sourceRoot, part);
        return path == null ? null : path.Length == 0 ? cloneRoot : cloneRoot.Find(path);
    }

    private static Transform Find(GameObject root, string path) => path == null ? null : path.Length == 0 ? root.transform : root.transform.Find(path);
    private static string Path(Transform root, Component part)
    {
        if (part == null || !part.transform.IsChildOf(root)) return null;
        var names = new List<string>();
        for (var at = part.transform; at != root; at = at.parent) names.Insert(0, at.name);
        return string.Join("/", names);
    }

    internal static bool InText(Transform node)
    {
        if (TextType == null) return false;
        for (var at = node; at != null; at = at.parent) if (at.GetComponent(TextType) != null) return true;
        return false;
    }

    internal static Component Label(CheckBox box) => CheckLabel?.GetValue(box) as Component;
    internal static Component Label(UIBButton plank) => PlankText?.GetValue(plank) as Component;

    internal static string TextOf(Component text) => text == null ? null : TextValue?.GetValue(text, null) as string;
    internal static void SetText(Component text, string value) { if (text != null) TextValue?.SetValue(text, value, null); }
    internal static void SetColor(Component text, Color value) { if (text != null && TextColorValue != null && TextColorValue.CanWrite) TextColorValue.SetValue(text, value, null); }
    internal static Color? GetColor(Component text) => text != null && TextColorValue != null ? TextColorValue.GetValue(text, null) as Color? : null;
    internal static object Font(Component text) => text == null || TextFont == null ? null : TextFont.Invoke(text, null);
    internal static string Alignment(Component text) => text == null || TextAlignment == null ? null : TextAlignment.GetValue(text, null)?.ToString();
    // Resize an UberText layout rect (Width/Height); returns false when the property is unavailable.
    internal static bool ScaleRect(Component text, string property, float factor)
    {
        var size = text == null || TextType == null ? null : AccessTools.Property(TextType, property);
        if (size == null || !size.CanWrite || size.PropertyType != typeof(float)) return false;
        size.SetValue(text, (float)size.GetValue(text, null) * factor, null);
        return true;
    }
    internal static void SingleLine(Component text)
    {
        if (text == null || TextType == null) return;
        AccessTools.Property(TextType, "WordWrap")?.SetValue(text, false, null);
        AccessTools.Property(TextType, "ResizeToFit")?.SetValue(text, false, null);
    }

    // Plank fallback keeps its native hit target while fitting the same two-line cell as the checkbox row.
    internal static void SizeOnPlane(GameObject item, Vector3 right, Vector3 up, float width, float height)
    {
        foreach (var axis in new[] { right, up })
        {
            if (!Span(item, axis, out float min, out float max) || max <= min) continue;
            Vector3 local = item.transform.InverseTransformDirection(axis);
            int component = Math.Abs(local.x) >= Math.Abs(local.y) && Math.Abs(local.x) >= Math.Abs(local.z) ? 0
                : Math.Abs(local.y) >= Math.Abs(local.z) ? 1 : 2;
            var scale = item.transform.localScale;
            scale[component] *= (axis == right ? width : height) / (max - min);
            item.transform.localScale = scale;
        }
    }

    internal static void UpdateNow(Component text) { if (text != null) TextUpdate?.Invoke(text, new object[] { true }); }
    // UberText's cached glyph bounds can still be empty after UpdateNow; the generated renderer is world-space.
    // drawnOnly: only enabled, active renderers count (the paceCheck diagnostic must not pass text nobody sees).
    internal static Bounds? RenderedBounds(Component text, bool drawnOnly = false)
    {
        if (text == null) return null;
        UpdateNow(text);
        Bounds? drawn = null;
        foreach (var renderer in text.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer.enabled || !ActiveUnder(renderer.transform, text.transform) || renderer.bounds.size.sqrMagnitude <= 0) continue;
            if (!drawn.HasValue) drawn = renderer.bounds;
            else { var bounds = drawn.Value; bounds.Encapsulate(renderer.bounds); drawn = bounds; }
        }
        return drawnOnly ? drawn : drawn ?? GlyphBounds(text) ?? RectBounds(text);
    }
    internal static Bounds? RectBounds(Component text) => text == null || TextRect == null ? null : TextRect.Invoke(text, null) as Bounds?;
    internal static Bounds? GlyphBounds(Component text)
    {
        if (text == null || TextGlyphs == null) return null;
        var value = TextGlyphs.Invoke(text, null) as Bounds?;
        return value.HasValue && value.Value.size.sqrMagnitude > 0 ? value : null;
    }

    // Mesh extent of root along a world axis, ignoring text meshes and inactive branches. Works while hidden.
    internal static bool Span(GameObject root, Vector3 axis, out float min, out float max, bool includeText = false, Func<MeshFilter, bool> keep = null)
    {
        min = float.MaxValue; max = float.MinValue;
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || !ActiveUnder(filter.transform, root.transform)) continue;
            if (!includeText && InText(filter.transform)) continue;
            if (keep != null && !keep(filter)) continue;
            var renderer = filter.GetComponent<Renderer>();
            if (renderer != null && !renderer.enabled) continue;
            Bounds local = filter.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3((corner & 1) == 0 ? local.min.x : local.max.x, (corner & 2) == 0 ? local.min.y : local.max.y,
                    (corner & 4) == 0 ? local.min.z : local.max.z);
                float value = Vector3.Dot(filter.transform.TransformPoint(point), axis);
                min = Math.Min(min, value); max = Math.Max(max, value);
            }
        }
        return min <= max;
    }

    private static bool ActiveUnder(Transform node, Transform root)
    {
        for (var at = node; at != null; at = at.parent)
        {
            if (at == root) return true;
            if (!at.gameObject.activeSelf) return false;
        }
        return true;
    }

    // Rotation taking an Options part (cloned at its prefab-space rotation) to the menu: the Options checkbox label
    // turned onto the menu plank label. Both are UberText authored the same way, so glyphs, frames and arrows follow.
    internal static Quaternion Correction(Quaternion plankText)
    {
        var label = Label(checkTemplate.GetComponent<CheckBox>());
        return plankText * Quaternion.Inverse(label != null ? label.transform.rotation : checkTemplate.transform.rotation);
    }

    // Move part (by its mesh centre) to a world point on the screen plane, depth unchanged.
    internal static void Center(GameObject part, Vector3 right, Vector3 up, float x, float y)
    {
        if (!Span(part, right, out float x0, out float x1) || !Span(part, up, out float y0, out float y1)) return;
        part.transform.position += right * (x - (x0 + x1) / 2) + up * (y - (y0 + y1) / 2);
    }

    internal static void SetLayer(GameObject root, int layer)
    {
        foreach (var node in root.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = layer;
    }

    // Retail rename (DeckTrayDeckListContent.RenameCurrentlyEditingDeck) puts the input over the label it hides.
    internal static Rect? ScreenRect(Bounds bounds, Camera camera)
    {
        if (camera == null) return null;
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x, (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
            Vector3 screen = camera.WorldToScreenPoint(point);
            x0 = Math.Min(x0, screen.x); x1 = Math.Max(x1, screen.x); y0 = Math.Min(y0, screen.y); y1 = Math.Max(y1, screen.y);
        }
        return Rect.MinMaxRect(x0, y0, x1, y1);
    }
}
