using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Owner 2026-10-25: each local shop category shows its own icon. The native tab widget only knows the icons in its
// ShopMainTabs atlas (NativeShopTabs.Icon), so a category can borrow any sprite or texture the client already has loaded;
// nothing is downloaded or shipped. Applied per frame while the shop is open (the tab's SpriteSwitcher re-applies its own
// sprite on every rebind). Public Unity API only; a missing asset leaves the native icon.
internal static class NativeShopTabIcons
{
    // tab id -> "sprite:<name>" or "tex:<name>[@x,y,w,h]" (a loaded native asset). The [Shop] TabIcons cfg overrides these;
    // "list" logs the loaded sprite and texture names once per shop open, to pick one.
    // Live 2026-10-29 (Deck): loaded non-atlas assets (Shop_Multipack_Icon, CollectionDeck_Silhouette) lose the round
    // tab frame and read as placeholders, so every category uses its framed atlas glyph (NativeShopTabs.Icon).
    internal static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly string[] IconRenderers = { "BaseIcon", "HighlightedIcon", "SelectedIcon" };
    private static Dictionary<string, string> wanted;
    private static readonly Dictionary<string, Sprite> resolved = new Dictionary<string, Sprite>(StringComparer.Ordinal);
    private static readonly HashSet<string> missing = new HashSet<string>(StringComparer.Ordinal);
    private static Action<string> log;
    private static bool failed, listing, listed;

    internal static void Configure(string setting, Action<string> logger)
    {
        log = logger;
        resolved.Clear(); missing.Clear(); failed = false;
        wanted = new Dictionary<string, string>(Defaults, StringComparer.Ordinal);
        listing = string.Equals((setting ?? "").Trim(), "list", StringComparison.OrdinalIgnoreCase); listed = false;
        if (listing) return;
        if (string.Equals((setting ?? "").Trim(), "none", StringComparison.OrdinalIgnoreCase)) { wanted.Clear(); return; }
        foreach (var pair in (setting ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = pair.IndexOf('=');
            if (split <= 0) continue;
            string id = pair.Substring(0, split).Trim(), value = pair.Substring(split + 1).Trim();
            if (value.Length == 0) wanted.Remove(id); else wanted[id] = value;
            if (Array.IndexOf(NativeShopTabs.Ids, id) < 0) log?.Invoke("[NativeShop] TabIcons: no category '" + id + "' (" + string.Join(", ", NativeShopTabs.Ids) + "); ignored");
        }
    }

    internal static void Tick(Component shop)
    {
        // Every few frames: a native rebind re-applies the atlas sprite at most a few frames before this swaps it back.
        if (listing && !listed && shop != null) { listed = true; List(); }
        if (failed || wanted == null || wanted.Count == 0 || shop == null || Time.frameCount % 4 != 0) return;
        try
        {
            foreach (var tab in shop.GetComponentsInChildren<ShopTab>(true))
            {
                string id = tab.CurrentData?.Id;
                if (id == null || !wanted.TryGetValue(id, out string asset)) continue;
                foreach (var renderer in tab.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (Array.IndexOf(IconRenderers, renderer.gameObject.name) < 0 || renderer.sprite == null) continue;
                    var sprite = Resolve(asset, renderer.sprite);
                    if (sprite != null && renderer.sprite != sprite) renderer.sprite = sprite;
                }
            }
        }
        catch (Exception error) { failed = true; log?.Invoke("[NativeShop] inert: tab icons: " + error.Message); }
    }

    // Diagnostic: every loaded sprite (with its texture) and texture, so a cfg value can name one.
    private static void List()
    {
        try
        {
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>().Where(s => s != null).OrderBy(s => s.name))
                log?.Invoke("[NativeShop] loaded sprite: " + sprite.name + " texture=" + (sprite.texture == null ? "" : sprite.texture.name));
            foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => t != null).OrderBy(t => t.name))
                log?.Invoke("[NativeShop] loaded texture: " + texture.name + " " + texture.width + "x" + texture.height);
        }
        catch (Exception error) { log?.Invoke("[NativeShop] loaded asset list failed: " + error.Message); }
    }

    // GPU textures are not CPU-readable: blit the sub-rect into a temporary render target, read it back and clear the
    // pixels outside the inscribed circle (soft one-pixel edge).
    private static Texture2D RoundCopy(Texture2D texture, float[] box)
    {
        const int size = 128;
        var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(texture, target, new Vector2(box[2], box[3]), new Vector2(box[0], box[1]));
            RenderTexture.active = target;
            var copy = new Texture2D(size, size, TextureFormat.ARGB32, false);
            copy.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            var pixels = copy.GetPixels32();
            float centre = (size - 1) / 2f, radius = size / 2f - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float distance = Mathf.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));
                    var pixel = pixels[y * size + x];
                    pixel.a = (byte)(pixel.a * Mathf.Clamp01(radius - distance + 0.5f));
                    pixels[y * size + x] = pixel;
                }
            copy.SetPixels32(pixels);
            copy.Apply(false, true);
            copy.name = texture.name + "_SoloStoneTabIcon";
            return copy;
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
    }

    // Same on-screen size as the native icon it replaces.
    private static Sprite Resolve(string asset, Sprite native)
    {
        string key = asset + "@" + native.bounds.size.x.ToString("R");
        if (resolved.TryGetValue(key, out var cached) && cached != null) return cached;
        if (missing.Contains(key)) return null;
        Sprite sprite = null;
        if (asset.StartsWith("sprite:", StringComparison.Ordinal))
        {
            var source = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == asset.Substring(7));
            if (source != null && native.bounds.size.x > 0)
                sprite = Sprite.Create(source.texture, source.rect, new Vector2(0.5f, 0.5f), source.rect.width / native.bounds.size.x);
        }
        else if (asset.StartsWith("tex:", StringComparison.Ordinal))
        {
            // tex:<name> or tex:<name>@x,y,w,h with a normalized sub-rect of a texture sheet.
            string[] parts = asset.Substring(4).Split('@');
            var texture = Resources.FindObjectsOfTypeAll<Texture2D>().FirstOrDefault(t => t.name == parts[0]);
            var box = parts.Length == 2 ? parts[1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
                : new[] { 0f, 0f, 1f, 1f };
            if (texture != null && native.bounds.size.x > 0 && box.Length == 4)
            {
                // Round like the native tab icons: the texture's own corners (e.g. the hub Store coin's white backing) are cut.
                var round = RoundCopy(texture, box);
                sprite = Sprite.Create(round, new Rect(0, 0, round.width, round.height), new Vector2(0.5f, 0.5f), round.width / native.bounds.size.x);
            }
        }
        if (sprite == null) { missing.Add(key); log?.Invoke("[NativeShop] tab icon asset not loaded: " + asset); return null; }
        resolved[key] = sprite;
        return sprite;
    }
}
