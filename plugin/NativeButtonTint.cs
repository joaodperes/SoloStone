using System.Collections.Generic;
using UnityEngine;

// Recolours a cloned native button (#28 owner 2026-10-28: REMATCH red, DETAILS grey). The plates draw with the opaque
// Hero/Unlit/Unlit_Texture, which has no colour property. Live 2026-10-28: a multiply twin never showed, and Sprites/Default
// honours the atlas alpha (most of the plate vanished). So the plate texture is copied once per tint (GPU blit, read back,
// recoloured by luminance on the CPU) and the clone's plate renderers get a copy of their own material with that texture:
// same shader, queue and look. The native buttons keep their shared materials. Additive glows are tinted through a property
// block. Labels are left alone. Idempotent per button. Never throws (a failed tint leaves the native colours); copies whose
// source material or texture the client unloaded are destroyed on the next call.
internal static class NativeButtonTint
{
    internal enum Tint { Red, Grey }
    private sealed class Copy<T> where T : Object { internal Object Source; internal T Made; }
    private static readonly Dictionary<string, Copy<Material>> tinted = new Dictionary<string, Copy<Material>>();
    private static readonly Dictionary<string, Copy<Texture2D>> textures = new Dictionary<string, Copy<Texture2D>>();
    private static MaterialPropertyBlock block;
    internal static BepInEx.Logging.ManualLogSource Log;

    internal static void Apply(Component button, Tint tint)
    {
        if (button == null) return;
        try { Recolour(button, tint); }
        catch (System.Exception error) { Log?.LogWarning("NATIVE_BUTTON_TINT " + button.name + " failed: " + error.Message); }
    }

    private static void Recolour(Component button, Tint tint)
    {
        Prune(tinted); Prune(textures);
        if (block == null) block = new MaterialPropertyBlock();
        int plates = 0, glows = 0;
        foreach (MeshRenderer renderer in button.GetComponentsInChildren<MeshRenderer>(true))
        {
            Material material = renderer.sharedMaterial;
            if (material == null || ProfileFormKit.InText(renderer.transform)) continue;
            string shader = material.shader.name;
            if (shader.Contains("Additive") && (material.HasProperty("_Color") || material.HasProperty("_TintColor")))
            {
                string property = material.HasProperty("_TintColor") ? "_TintColor" : "_Color";
                Color source = material.GetColor(property);
                Color glow = tint == Tint.Red ? new Color(source.r, source.g * 0.3f, source.b * 0.25f, source.a)
                    : new Color(source.r * 0.5f, source.g * 0.5f, source.b * 0.5f, source.a);
                renderer.GetPropertyBlock(block);
                block.SetColor(property, glow);
                renderer.SetPropertyBlock(block);
                glows++;
            }
            else if (shader.Contains("Unlit_Texture") && material.mainTexture != null && !material.name.EndsWith(Suffix(tint)))
            {
                renderer.sharedMaterial = Tinted(material, tint);
                plates++;
            }
        }
        Log?.LogInfo("NATIVE_BUTTON_TINT " + button.name + " tint=" + tint + ";plates=" + plates + ";glows=" + glows);
    }

    private static string Suffix(Tint tint) => " (SoloStone " + tint + ")";

    private static Material Tinted(Material source, Tint tint)
    {
        string key = source.GetInstanceID() + "|" + tint;
        if (tinted.TryGetValue(key, out Copy<Material> cached) && cached.Made != null) return cached.Made;
        var material = new Material(source) { name = source.name + Suffix(tint), mainTexture = Recolored(source.mainTexture, tint) };
        tinted[key] = new Copy<Material> { Source = source, Made = material };
        return material;
    }

    // Red keeps the plate's shading (luminance) in a warm red; grey is the plate desaturated and slightly dimmed.
    private static Texture2D Recolored(Texture source, Tint tint)
    {
        string key = source.GetInstanceID() + "|" + tint;
        if (textures.TryGetValue(key, out Copy<Texture2D> cached) && cached.Made != null) return cached.Made;
        int width = source.width, height = source.height;
        RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        Texture2D copy = null;
        try
        {
            Graphics.Blit(source, target, Vector2.one, Vector2.zero);
            RenderTexture.active = target;
            copy = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = source.name + Suffix(tint) };
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        }
        catch
        {
            if (copy != null) Object.Destroy(copy);
            throw;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
        Color32[] pixels = copy.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 p = pixels[i];
            float lum = (0.3f * p.r + 0.59f * p.g + 0.11f * p.b) / 255f;
            pixels[i] = tint == Tint.Red
                ? new Color32(Byte(lum * 1.25f), Byte(lum * 0.4f), Byte(lum * 0.33f), p.a)
                : new Color32(Byte(lum * 0.95f), Byte(lum * 0.95f), Byte(lum * 0.95f), p.a);
        }
        copy.SetPixels32(pixels);
        copy.wrapMode = source.wrapMode; copy.filterMode = source.filterMode;
        copy.Apply(false, true);
        textures[key] = new Copy<Texture2D> { Source = source, Made = copy };
        return copy;
    }

    private static void Prune<T>(Dictionary<string, Copy<T>> cache) where T : Object
    {
        List<string> gone = null;
        foreach (var entry in cache)
            if (entry.Value.Source == null || entry.Value.Made == null) (gone ?? (gone = new List<string>())).Add(entry.Key);
        if (gone == null) return;
        foreach (string key in gone)
        {
            if (cache[key].Made != null) Object.Destroy(cache[key].Made);
            cache.Remove(key);
        }
    }

    private static byte Byte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
}
