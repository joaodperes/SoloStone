using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Hearthstone.DataModels;
using UnityEngine;

// Owner 2026-10-30: one tile per deck, and players compare both prices without opening its page. The Deck tile shows its
// ready-made price natively (show_price); the Recipe price is a copy of that tile's native gold price plate (coin and
// number) above it, reading "100 Recipe" (NativeShop.RecipeTileText decides when; NativeShopArtMath.RecipePlate places).
// The copy is made inactive, stripped of every script but its text and of colliders, and parented next to the native
// plate, so it scrolls, hovers and is reused with its slot. It follows every slot rebind, price refresh and ownership
// change, and hides with the native plate (owned tiles show the Owned plate instead). Text only: it takes no input.
// Cost: one walk of the shop's slots every second frame into a reused list; per slot a few reference compares. A copy
// is made, placed or relabelled only when its slot's product, prices, availability or the setting change.
// [Shop] RecipeTilePlate (applied per shop open): none, or the placement knobs. Any failure hides every copy and leaves
// the native tiles for the session.
internal static class NativeShopTilePrices
{
    private const string CopyName = "SoloStoneRecipePrice", RenderPrefix = "UberText_RenderObject";
    private const int RetryFrames = 30, MaxRetryFrames = 960;
    private static readonly Type TextType = GameTypes.ByName("UberText");
    private static readonly PropertyInfo TextValue = TextType == null ? null : AccessTools.Property(TextType, "Text");
    private static readonly MethodInfo TextUpdate = TextType == null ? null : AccessTools.Method(TextType, "UpdateNow", new[] { typeof(bool) });
    private static NativeShopArtMath.RecipePlate settings = NativeShopArtMath.ParseRecipePlate(null);
    private static int version;
    private static Action<string> log;
    private static bool inert, described, missingDescribed;
    private static GameObject holder;
    private static readonly List<ShopSlot> slots = new List<ShopSlot>();
    private static readonly Dictionary<ShopSlot, Entry> entries = new Dictionary<ShopSlot, Entry>();
    private sealed class Entry
    {
        internal ProductDataModel Shown, Recipe;
        internal PriceDataModel DeckPrice, RecipePrice;
        internal ProductAvailability DeckState, RecipeState;
        internal int Version = -1, RetryAt, RetryGap;
        internal string Text;
        internal Component NativeText;
        internal Transform Plate, CopyOf;
        internal Vector3 PlateAt, PlateScale;
        internal GameObject Copy;
        internal Component CopyText;
        internal bool Placed, Restyle, Relabel;
        internal object PopupRoot, PopupSet;
        internal ShopSection Section;
        internal int ArrangeTries;
    }

    internal static void Configure(string setting, Action<string> logger)
    {
        log = logger;
        var next = NativeShopArtMath.ParseRecipePlate(setting, logger);
        if (next.Key == settings.Key) return;
        settings = next; version++;
        logger?.Invoke("[NativeShop] RecipeTilePlate: " + next.Key);
    }

    // Shop open, every second frame (odd frames; tile art paints on even ones).
    internal static void Tick(Component shop)
    {
        if (inert || shop == null || Time.frameCount % 2 != 1) return;
        try
        {
            if (Time.frameCount % 121 == 0) Sweep();
            if (!settings.Enabled || TextType == null || TextValue == null) { if (entries.Count != 0) Clear(); return; }
            shop.GetComponentsInChildren(false, slots);
            for (int i = 0; i < slots.Count; i++) Update(slots[i]);
        }
        catch (Exception error) { inert = true; Clear(); log?.Invoke("[NativeShop] inert: recipe tile price: " + error.Message); }
        finally { slots.Clear(); }
    }

    private static void Update(ShopSlot slot)
    {
        var shown = slot.BrowserButtonDataModel?.DisplayProduct;
        var recipe = shown?.Variants != null && shown.Variants.Count == 2 ? shown.Variants[1] : null;
        entries.TryGetValue(slot, out var entry);
        if (recipe == null) { if (entry != null) Hide(entry); return; }
        if (entry == null) entries[slot] = entry = new Entry();
        var deckPrice = shown.Prices != null && shown.Prices.Count > 0 ? shown.Prices[0] : null;
        var recipePrice = recipe.Prices != null && recipe.Prices.Count > 0 ? recipe.Prices[0] : null;
        bool rebound = !ReferenceEquals(entry.Shown, shown);
        if (rebound || !ReferenceEquals(entry.Recipe, recipe) || !ReferenceEquals(entry.DeckPrice, deckPrice) || !ReferenceEquals(entry.RecipePrice, recipePrice)
            || entry.DeckState != shown.Availability || entry.RecipeState != recipe.Availability || entry.Version != version)
        {
            // A slot rebound to another deck (scroll reuse) finds its native plate again: the tile's widgets may differ.
            if (rebound || entry.Version != version) { entry.NativeText = null; entry.Plate = null; entry.RetryAt = 0; entry.RetryGap = RetryFrames; }
            // The copy keeps the look it was made with (its visual scripts are gone): a locked or freed tile gets a new one.
            if (entry.DeckState != shown.Availability || entry.RecipeState != recipe.Availability) entry.Restyle = true;
            entry.Shown = shown; entry.Recipe = recipe; entry.DeckPrice = deckPrice; entry.RecipePrice = recipePrice;
            entry.DeckState = shown.Availability; entry.RecipeState = recipe.Availability; entry.Version = version;
            entry.Text = NativeShop.RecipeTileText(shown, settings);
            entry.Placed = false;
            entry.Relabel = true;
        }
        if (entry.Text == null) { Hide(entry); return; }
        if (entry.Plate == null || entry.NativeText == null)
        {
            // The tile's price widget loads after the product binds: look again, backing off (RetryFrames doubling up to
            // MaxRetryFrames) so a tile whose price never matches costs almost nothing.
            if (Time.frameCount < entry.RetryAt) { Hide(entry); return; }
            if (!Find(slot, entry, deckPrice)) { Retry(entry); Hide(entry); return; }
            entry.Placed = false;
        }
        if (!entry.Plate.gameObject.activeInHierarchy) { Hide(entry); return; }
        if (entry.Restyle || entry.Copy == null || entry.CopyOf != entry.Plate || entry.Copy.transform.parent != entry.Plate.parent)
        {
            if (Time.frameCount < entry.RetryAt) { Hide(entry); return; }
            entry.Restyle = false; entry.Placed = false;
            if (!MakeCopy(entry)) { Retry(entry); Hide(entry); return; }
        }
        // Placed again when the plate moves or its scale changes (open and scroll animations); hidden until it has bounds.
        if ((!entry.Placed || entry.Plate.localPosition != entry.PlateAt || entry.Plate.lossyScale != entry.PlateScale) && !Place(entry)) { Hide(entry); return; }
        if (!entry.Copy.activeSelf) entry.Copy.SetActive(true);
        // Live 2026-10-30: text set on the inactive copy never drew. As with the working UberText clones
        // (ProfileFormKit), the label is set once the copy is active and the text mesh is rebuilt at once.
        if (entry.Relabel)
        {
            entry.Relabel = false;
            Mirror(entry.NativeText, entry.CopyText);
            Raise(entry);
            TextValue.SetValue(entry.CopyText, entry.Text, null);
            TextUpdate?.Invoke(entry.CopyText, new object[] { true });
            entry.ArrangeTries = ArrangeLimit;
        }
        // The section gets its popup root only once its slots have loaded (and a pooled section may get another): one
        // field read per tick re-registers the copy whenever the root it was registered with is not the current one.
        Popup(slot, entry);
        // Until the coin and text can be measured (mesh not built, renderer off this frame), try again, a bounded number of times.
        if (entry.ArrangeTries > 0 && Arrange(entry)) entry.ArrangeTries = 0; else if (entry.ArrangeTries > 0) entry.ArrangeTries--;
    }
    private const int ArrangeLimit = 60;

    private static void Retry(Entry entry)
    {
        entry.RetryGap = Mathf.Clamp(entry.RetryGap * 2, RetryFrames, MaxRetryFrames);
        entry.RetryAt = Time.frameCount + entry.RetryGap;
    }

    // The native price text on a Deck tile: the active text reading the Deck's price (exact, else the same digits).
    private static bool Find(ShopSlot slot, Entry entry, PriceDataModel deckPrice)
    {
        if (deckPrice == null) return false;
        string exact = (deckPrice.DisplayText ?? "").Trim(), digits = Digits(exact);
        if (digits.Length == 0) digits = Digits(((long)deckPrice.Amount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Component match = null;
        var texts = slot.GetComponentsInChildren(TextType, false);
        foreach (var text in texts)
        {
            if (InCopy(text.transform)) continue;
            string value = (TextValue.GetValue(text, null) as string ?? "").Trim();
            if (value.Length != 0 && value == exact) { match = text; break; }
            if (match == null && digits.Length != 0 && digits != "0" && Digits(value) == digits) match = text;
        }
        if (match == null)
        {
            if (!missingDescribed && texts.Length != 0) { missingDescribed = true; log?.Invoke("[NativeShop] recipe tile price: no native price text '" + exact + "' on " + slot.name + "; texts: " + Texts(slot, texts)); }
            return false;
        }
        var plate = match.transform;
        for (int i = 0; i < settings.Lift && plate.parent != null && plate.parent != slot.transform; i++) plate = plate.parent;
        entry.NativeText = match; entry.Plate = plate;
        if (!described) { described = true; log?.Invoke("[NativeShop] recipe tile price: " + Describe(slot, match.transform, plate)); }
        return true;
    }

    // An inactive copy of the native plate (no Awake runs on it) loses every script but the price text, what lies under it
    // and the widget layout (WidgetTransform), every collider, rigidbody and particle system (no clicks, no data binding
    // rewriting it, no glints), and the generated render objects of the other texts (each still draws its old text).
    // Then the copy joins the native plate's parent; once active it is registered for popup rendering (Popup).
    // A failed copy is retried for this tile only.
    private static bool MakeCopy(Entry entry)
    {
        Retire(entry);
        if (holder == null) { holder = new GameObject("SoloStoneRecipePriceHolder"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder); }
        var source = entry.Plate;
        var copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
        copy.name = CopyName;
        try
        {
            var text = Counterpart(source, entry.NativeText.transform, copy.transform)?.GetComponent(TextType);
            if (text == null) { Failed("price text missing from the copy"); return false; }
            for (int pass = 0; pass < 3; pass++) // Components another one requires go on a later pass.
            {
                int removed = 0;
                foreach (var component in copy.GetComponentsInChildren<Component>(true))
                {
                    // WidgetTransform is layout data only (no lifecycle). UberText reads it at Awake: with it, the text takes the
                    // widget's rect and lies in the shop's XZ plane; without it (live 2026-10-30), its own XY plane, edge-on.
                    if (component == null || component is Transform || component == text || component is Hearthstone.UI.WidgetTransform
                        || Under(component.transform, text.transform)) continue;
                    bool particles = component.GetType().Name.StartsWith("ParticleSystem", StringComparison.Ordinal);
                    if (!particles && (component is Renderer || component is MeshFilter)) continue;
                    if (!particles && !(component is Behaviour) && !(component is Collider) && !(component is Rigidbody)) continue;
                    try { UnityEngine.Object.DestroyImmediate(component); removed++; } catch (Exception) { }
                }
                if (removed == 0) break;
            }
            foreach (var node in copy.GetComponentsInChildren<Transform>(true))
                if (node != null && !Under(node, text.transform) && node.name.StartsWith(RenderPrefix, StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(node.gameObject);
            copy.SetActive(false);
            copy.transform.SetParent(source.parent, false);
            copy.transform.localRotation = source.localRotation;
            copy.transform.localScale = source.localScale;
            entry.Copy = copy; entry.CopyText = text; entry.CopyOf = source; entry.Relabel = true; copy = null;
            return true;
        }
        catch (Exception error) { Failed((error.InnerException ?? error).Message); return false; }
        finally { if (copy != null) UnityEngine.Object.DestroyImmediate(copy); }
    }

    // The native text's look is set at run time by its widget's visual states (stripped from the copy), so the copy takes
    // the native text's current style rather than the prefab's serialized one.
    private static readonly string[] Style = { "m_TextColor", "m_OutlineColor", "m_Outline", "m_OutlineSize", "m_Font", "m_FontSize",
        "m_CharacterSize", "m_Width", "m_Height", "m_ResizeToFit", "m_Alignment", "m_Anchor", "m_RenderQueue", "m_AmbientLightBlend" };
    private static void Mirror(Component native, Component copy)
    {
        if (native == null || copy == null) return;
        foreach (string name in Style)
        {
            var field = AccessTools.Field(TextType, name);
            if (field != null) field.SetValue(copy, field.GetValue(native));
        }
    }

    // Live 2026-10-30: the copied text built its mesh (same font, colour, material, queue 3000 as the native one) yet drew
    // nothing over the copied frame: transparent draws of equal queue at the same height have no fixed order. The copy's
    // text goes one queue above every other renderer in the copy (queue knob: fixed increment instead).
    private static readonly FieldInfo TextQueue = TextType == null ? null : AccessTools.Field(TextType, "m_RenderQueue");
    private static void Raise(Entry entry)
    {
        if (TextQueue == null) return;
        int increment = settings.Queue;
        if (increment < 0)
        {
            int top = 3000;
            foreach (var renderer in entry.Copy.GetComponentsInChildren<Renderer>(true))
                if (!Under(renderer.transform, entry.CopyText.transform) && renderer.sharedMaterial != null) top = Math.Max(top, renderer.sharedMaterial.renderQueue);
            increment = top - 3000 + 1;
        }
        TextQueue.SetValue(entry.CopyText, increment);
    }

    // Live 2026-10-30: the copy's frame drew but its text never did, whatever its queue. Shop sections are popup-rendered:
    // ShopSection hands its IPopupRoot to everything under it once (ApplyPopupRendering over its transform, when ready),
    // which registers each UberText's text mesh with the popup renderer; a copy made later is never registered. So the
    // copy, once active and labelled, gets the same call on its own transform, and is cleaned up before it goes.
    private static readonly FieldInfo SectionPopupRoot = AccessTools.Field(typeof(ShopSection), "m_popupRoot");
    private static void Popup(ShopSlot slot, Entry entry)
    {
        if (SectionPopupRoot == null) return;
        if (entry.Section == null) entry.Section = slot.GetComponentInParent<ShopSection>();
        var root = entry.Section == null ? null : SectionPopupRoot.GetValue(entry.Section);
        if (ReferenceEquals(root, entry.PopupRoot)) return;
        Unregister(entry);
        if (root == null) return; // Not popup-rendered yet: checked again next tick.
        try
        {
            var apply = PopupMethod(root, "ApplyPopupRendering", 5);
            // A failed root is remembered (no set: nothing to clean up) and not retried until the section's root changes.
            if (apply == null) { entry.PopupRoot = root; Once(ref popupLogged, "no ApplyPopupRendering on the section's popup root"); return; }
            var set = Activator.CreateInstance(apply.GetParameters()[1].ParameterType);
            apply.Invoke(root, new object[] { entry.Copy.transform, set, false, 0, true });
            entry.PopupRoot = root; entry.PopupSet = set;
        }
        catch (Exception error) { entry.PopupRoot = root; entry.PopupSet = null; Once(ref popupLogged, "popup rendering: " + (error.InnerException ?? error).Message); }
    }
    private static void Once(ref bool logged, string message)
    {
        if (logged) return;
        logged = true;
        log?.Invoke("[NativeShop] recipe tile price: " + message);
    }
    private static bool popupLogged;
    private static MethodInfo PopupMethod(object root, string name, int parameters)
    {
        foreach (var type in root.GetType().GetInterfaces())
            if (type.Name == "IPopupRoot")
                foreach (var method in type.GetMethods())
                    if (method.Name == name && method.GetParameters().Length == parameters) return method;
        return null;
    }
    // Unregisters the copy from the popup renderer, then destroys it.
    private static void Retire(Entry entry)
    {
        Unregister(entry);
        entry.Section = null;
        if (entry.Copy != null) UnityEngine.Object.Destroy(entry.Copy);
        entry.Copy = null; entry.CopyText = null;
    }
    private static bool cleanupLogged;
    private static void Unregister(Entry entry)
    {
        // A section destroyed with its root (native ShopSection leaves the same) may throw here: logged once.
        if (entry.PopupRoot != null && entry.PopupSet != null && !(entry.PopupRoot is UnityEngine.Object dead && dead == null))
        {
            try { PopupMethod(entry.PopupRoot, "CleanupPopupRendering", 1)?.Invoke(entry.PopupRoot, new[] { entry.PopupSet }); }
            catch (Exception error) { Once(ref cleanupLogged, "popup cleanup: " + (error.InnerException ?? error).Message); }
        }
        entry.PopupRoot = null; entry.PopupSet = null;
    }

    // Live 2026-10-30: the native price's layout scripts (stripped) put the coin left of the amount; "100 Recipe" is longer
    // than any amount, so the copy lays out coin, the native coin-to-text gap and its text itself, centred on its frame.
    private static bool arrangeLogged;
    private static bool Arrange(Entry entry)
    {
        try { return ArrangeNow(entry); }
        catch (Exception error) { Once(ref arrangeLogged, "layout: " + error.Message); return true; } // Not retried.
    }
    private static bool ArrangeNow(Entry entry)
    {
        var native = entry.NativeText.transform;
        var nativeCoin = native.parent == null ? null : native.parent.Find("CurrencyIcon");
        var coin = entry.CopyText.transform.parent == null ? null : entry.CopyText.transform.parent.Find("CurrencyIcon");
        if (coin != null && !coin.IsChildOf(entry.Copy.transform)) return true; // lift=0: the coin is native; never moved.
        var camera = CameraUtils.FindFirstByLayerMask(1 << entry.Plate.gameObject.layer);
        Vector3 right = camera != null ? camera.transform.right : Vector3.right;
        if (nativeCoin == null || coin == null || !Span(nativeCoin, right, false, out float nativeCoinMin, out float nativeCoinMax)
            || !Span(native, right, true, out float nativeTextMin, out _) || !Span(coin, right, false, out float coinMin, out float coinMax)
            || !Span(entry.CopyText.transform, right, true, out float textMin, out float textMax)) return false;
        float gap = Mathf.Max(0f, nativeTextMin - nativeCoinMax) * settings.Scale, coinWidth = coinMax - coinMin, total = coinWidth + gap + (textMax - textMin);
        float centre = FrameCentre(entry.Copy.transform, right, (coinMin + textMax) / 2f);
        float left = centre - total / 2f;
        coin.position += right * (left - coinMin);
        entry.CopyText.transform.position += right * (left + coinWidth + gap - textMin);
        return true;
    }

    // Extent along axis of the active renderers under root: text render objects only (text) or everything else.
    private static bool Span(Transform root, Vector3 axis, bool text, out float min, out float max)
    {
        min = float.MaxValue; max = float.MinValue;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || renderer.name.StartsWith(RenderPrefix, StringComparison.Ordinal) != text) continue;
            var b = renderer.bounds;
            float centre = Vector3.Dot(b.center, axis), half = Extent(b.extents, axis);
            min = Mathf.Min(min, centre - half); max = Mathf.Max(max, centre + half);
        }
        return min <= max;
    }

    private static float FrameCentre(Transform copy, Vector3 axis, float fallback)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var renderer in copy.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || !renderer.name.StartsWith("Shop_Texture_PriceTag", StringComparison.Ordinal)) continue;
            var b = renderer.bounds;
            float centre = Vector3.Dot(b.center, axis), half = Extent(b.extents, axis);
            min = Mathf.Min(min, centre - half); max = Mathf.Max(max, centre + half);
        }
        return min <= max ? (min + max) / 2f : fallback;
    }

    private static bool copyFailureLogged;
    private static void Failed(string reason)
    {
        if (copyFailureLogged) return;
        copyFailureLogged = true;
        log?.Invoke("[NativeShop] recipe tile price: copy failed (tile retried): " + reason);
    }

    // Offset from the native plate's pivot in its own on-screen widths and heights (camera right / up), scaled about that pivot.
    private static bool Place(Entry entry)
    {
        var plate = entry.Plate;
        entry.PlateAt = plate.localPosition; entry.PlateScale = plate.lossyScale;
        if (!Bounds(plate, out var bounds) || bounds.size.sqrMagnitude < 1e-8f) { entry.Placed = false; return false; }
        var copy = entry.Copy.transform;
        copy.localRotation = plate.localRotation;
        copy.localScale = plate.localScale * settings.Scale;
        var camera = CameraUtils.FindFirstByLayerMask(1 << plate.gameObject.layer);
        Vector3 right = camera != null ? camera.transform.right : Vector3.right, up = camera != null ? camera.transform.up : Vector3.forward;
        copy.position = plate.position + right * (settings.X * Extent(bounds.size, right)) + up * (settings.Y * Extent(bounds.size, up));
        entry.Placed = true;
        return true;
    }

    private static float Extent(Vector3 size, Vector3 axis) =>
        Mathf.Abs(size.x * axis.x) + Mathf.Abs(size.y * axis.y) + Mathf.Abs(size.z * axis.z);

    private static bool Bounds(Transform plate, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        foreach (var renderer in plate.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled) continue;
            if (any) bounds.Encapsulate(renderer.bounds); else { bounds = renderer.bounds; any = true; }
        }
        return any;
    }

    private static void Hide(Entry entry)
    {
        if (entry.Copy != null && entry.Copy.activeSelf) entry.Copy.SetActive(false);
    }

    // Slots and plates destroyed with their section: forget them (a copy goes with its parent).
    private static void Sweep()
    {
        List<ShopSlot> gone = null;
        foreach (var pair in entries)
            if (pair.Key == null) (gone ?? (gone = new List<ShopSlot>())).Add(pair.Key);
        if (gone == null) return;
        foreach (var slot in gone)
        {
            Retire(entries[slot]);
            entries.Remove(slot);
        }
    }

    private static void Clear()
    {
        foreach (var entry in entries.Values) Retire(entry);
        entries.Clear();
    }

    // Strictly below root.
    private static bool Under(Transform node, Transform root) => node != null && node != root && node.IsChildOf(root);

    private static bool InCopy(Transform node)
    {
        for (var at = node; at != null; at = at.parent) if (at.name == CopyName) return true;
        return false;
    }

    // By sibling index, not name (Instantiate keeps child order; a plate may hold same-named texts).
    private static Transform Counterpart(Transform sourceRoot, Transform part, Transform cloneRoot)
    {
        var path = new List<int>();
        for (var at = part; at != sourceRoot; at = at.parent) { if (at == null) return null; path.Insert(0, at.GetSiblingIndex()); }
        var node = cloneRoot;
        foreach (int index in path) { if (index >= node.childCount) return null; node = node.GetChild(index); }
        return node;
    }

    private static string Digits(string text)
    {
        var digits = new StringBuilder();
        foreach (char c in text ?? "") if (c >= '0' && c <= '9') digits.Append(c);
        return digits.ToString();
    }

    // ---- One-shot live diagnostics (the tile hierarchy is not in code; the knobs are tuned from these lines) ----
    private static string Texts(ShopSlot slot, Component[] texts)
    {
        var rows = new List<string>();
        foreach (var text in texts) rows.Add(Path(slot.transform, text.transform) + "='" + TextValue.GetValue(text, null) + "'");
        return string.Join("; ", rows);
    }

    private static string Describe(ShopSlot slot, Transform text, Transform plate)
    {
        var line = new StringBuilder("text " + Path(slot.transform, text) + ", plate " + Path(slot.transform, plate) + " (lift " + settings.Lift + ")");
        for (var at = text; at != null && at != slot.transform.parent; at = at.parent)
        {
            line.Append(" | ").Append(at.name).Append(" [");
            foreach (var component in at.GetComponents<Component>()) if (component != null) line.Append(component.GetType().Name).Append(' ');
            line.Append("] children:");
            for (int i = 0; i < at.childCount && i < 12; i++) line.Append(' ').Append(at.GetChild(i).name).Append(at.GetChild(i).gameObject.activeSelf ? "" : "(off)");
        }
        if (Bounds(plate, out var bounds)) line.Append(" | plate bounds ").Append(bounds.size.x.ToString("F3")).Append("x").Append(bounds.size.y.ToString("F3")).Append("x").Append(bounds.size.z.ToString("F3"));
        return line.ToString();
    }

    // Live text diagnostics: the UberText fields that decide what draws, and its render object.
    private static readonly string[] Dumped = { "m_Text", "m_TextColor", "m_Font", "m_FontSize", "m_CharacterSize", "m_Width", "m_Height",
        "m_Hidden", "m_updated", "m_RenderToTexture", "m_GameStringLookup", "m_UseEditorText", "m_TextSet", "m_widgetTransform", "m_CurrentLayer", "m_RenderQueue" };
#if !SOLOSTONE_RELEASE
    private static string Dump(Component text)
    {
        if (text == null) return null;
        var line = new StringBuilder("active=" + text.gameObject.activeInHierarchy + " enabled=" + ((Behaviour)text).enabled + " layer=" + text.gameObject.layer
            + " scale=" + Vec(text.transform.lossyScale) + " pos=" + Vec(text.transform.position));
        foreach (string name in Dumped)
        {
            var field = AccessTools.Field(TextType, name);
            object value = null;
            try { value = field?.GetValue(text); } catch (Exception) { }
            line.Append(' ').Append(name).Append('=').Append(field == null ? "?" : value is UnityEngine.Object o ? (o == null ? "null" : o.name) : value?.ToString() ?? "null");
        }
        try
        {
            var rendering = AccessTools.Field(TextType, "m_UberTextRendering")?.GetValue(text);
            var go = rendering == null ? null : AccessTools.Field(rendering.GetType(), "m_textMeshGameObject")?.GetValue(rendering) as GameObject;
            if (go == null) line.Append(" render=none");
            else
            {
                var renderer = go.GetComponent<Renderer>();
                var mesh = go.GetComponent("TextMesh");
                line.Append(" render=").Append(go.name).Append(" parent=").Append(go.transform.parent == null ? "null" : go.transform.parent.name)
                    .Append(" active=").Append(go.activeInHierarchy).Append(" layer=").Append(go.layer).Append(" pos=").Append(Vec(go.transform.position))
                    .Append(" scale=").Append(Vec(go.transform.lossyScale)).Append(" rot=").Append(Vec(go.transform.eulerAngles));
                if (renderer != null)
                    line.Append(" renderer=").Append(renderer.enabled).Append(" visible=").Append(renderer.isVisible).Append(" bounds=").Append(Vec(renderer.bounds.size))
                        .Append(" mat=").Append(renderer.sharedMaterial == null ? "null" : renderer.sharedMaterial.name + "/" + renderer.sharedMaterial.shader?.name + "/q" + renderer.sharedMaterial.renderQueue);
                if (mesh != null) line.Append(" mesh='").Append(AccessTools.Property(mesh.GetType(), "text")?.GetValue(mesh, null)).Append('\'');
            }
        }
        catch (Exception error) { line.Append(" renderError=").Append(error.Message); }
        return line.ToString();
    }
#endif
    private static string Renderers(Transform root)
    {
        var line = new StringBuilder();
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            line.Append(Path(root, renderer.transform)).Append('[').Append(renderer.enabled && renderer.gameObject.activeInHierarchy ? "on" : "off")
                .Append(" q").Append(renderer.sharedMaterial == null ? -1 : renderer.sharedMaterial.renderQueue).Append(" so").Append(renderer.sortingOrder)
                .Append(" y").Append(renderer.bounds.center.y.ToString("F2")).Append(' ').Append(renderer.sharedMaterial == null ? "null" : renderer.sharedMaterial.shader?.name).Append("] ");
        return line.ToString().Trim();
    }
    private static string Vec(Vector3 v) => v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2");

    private static string Children(Transform node)
    {
        var line = new StringBuilder();
        foreach (var renderer in node.GetComponentsInChildren<Renderer>(true))
            line.Append(Path(node, renderer.transform)).Append(renderer.enabled && renderer.gameObject.activeInHierarchy ? "" : "(off)").Append(' ');
        return line.ToString().Trim();
    }

    private static string Path(Transform root, Transform node)
    {
        var names = new List<string>();
        for (var at = node; at != null && at != root; at = at.parent) names.Insert(0, at.name);
        return string.Join("/", names);
    }

#if !SOLOSTONE_RELEASE
    // test-control `ui all=1`: each Deck tile's recipe plate (shown copies first).
    internal static MiniJSON.JsonNode Diagnostics()
    {
        var rows = new List<object>();
        foreach (var pair in entries)
        {
            if (pair.Key == null || pair.Value.Shown == null) continue;
            var entry = pair.Value;
            rows.Add(new MiniJSON.JsonNode { { "product", entry.Shown.PmtId }, { "text", entry.Text },
                { "shown", entry.Copy != null && entry.Copy.activeInHierarchy },
                { "plate", entry.Plate == null ? null : Path(pair.Key.transform, entry.Plate) },
                { "plateShown", entry.Plate != null && entry.Plate.gameObject.activeInHierarchy },
                { "copyText", entry.CopyText == null ? null : TextValue.GetValue(entry.CopyText, null) as string },
                { "nativeDump", Dump(entry.NativeText) }, { "copyDump", Dump(entry.CopyText) },
                { "copyTextChildren", entry.CopyText == null ? null : Children(entry.CopyText.transform) },
                { "plateRenderers", entry.Plate == null ? null : Renderers(entry.Plate) }, { "copyRenderers", entry.Copy == null ? null : Renderers(entry.Copy.transform) } });
        }
        return new MiniJSON.JsonNode { { "setting", settings.Key }, { "inert", inert }, { "tiles", TestControlProtocol.List(rows) } };
    }
#endif
}
