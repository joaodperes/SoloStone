#if !SOLOSTONE_RELEASE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using MiniJSON;
using UnityEngine;
using UnityEngine.SceneManagement;

// On-demand diagnostic only. Never activates, changes, or destroys a native object.
internal static class FireFxScene
{
    private const int MaxNodes = 12000, MaxRows = 64, MaxScenes = 8, MaxPending = 32768;
    private const long MaxMilliseconds = 40;
    private static string Cut(string text) => text == null ? "" : text.Length <= 96 ? text : text.Substring(0, 96);
    private sealed class Visit { internal Transform Node; internal int Child; internal bool Reported; }
    private static Stack<Visit> continuation;
    private static bool incomplete;
    private static int cursor;
    private static string filter;
    private static bool Relevant(Transform node)
    {
        for (int depth = 0; node != null && depth < 64; depth++, node = node.parent)
            if (string.IsNullOrEmpty(filter)
                ? node.gameObject.GetComponent("Spell") != null || node.gameObject.GetComponent("ParticleSystem") != null
                : node.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
    private static string Path(Transform node)
    {
        var parts = new List<string>();
        for (int i = 0; node != null && i < 8; i++, node = node.parent) parts.Add(Cut(node.name));
        parts.Reverse();
        return string.Join("/", parts);
    }
    private static string SpellState(Component component)
    {
        if (component == null) return "";
        try {
            var method = component.GetType().GetMethod("GetActiveState", Type.EmptyTypes);
            return method == null ? "unavailable" : Cut(Convert.ToString(method.Invoke(component, null), CultureInfo.InvariantCulture));
        } catch { return "unavailable"; }
    }
    private static string ParticlePlaying(Component component)
    {
        if (component == null) return "";
        try {
            var property = component.GetType().GetProperty("isPlaying");
            return property == null ? "unavailable" : Convert.ToString(property.GetValue(component, null), CultureInfo.InvariantCulture);
        } catch { return "unavailable"; }
    }
    internal static JsonNode Read(int selectedScene, int selectedRoot, string name = null, int after = 0)
    {
        NativeTestControl.AssertMainThread();
        var watch = Stopwatch.StartNew(); var rows = new JsonList();
        if (after != 0 && (after != cursor || continuation == null)) throw new InvalidOperationException("Scene cursor expired; restart scan");
        var pending = after == 0 ? new Stack<Visit>() : continuation;
        if (after == 0) filter = name;
        int visited = 0; var roots = new JsonList(); bool selectedFound = after != 0 || selectedScene < 0; bool truncated = after == 0 ? SceneManager.sceneCount > MaxScenes : incomplete;
        for (int i = 0; after == 0 && i < Math.Min(SceneManager.sceneCount, MaxScenes); i++)
        {
            if (selectedScene >= 0 && i != selectedScene) continue;
            var scene = SceneManager.GetSceneAt(i); if (!scene.isLoaded) continue;
            // This Unity call returns an array atomically; traversal below has explicit limits.
            var sceneRoots = scene.GetRootGameObjects();
            for (int n = 0; n < sceneRoots.Length; n++) {
                if (n >= 4096) { truncated = true; break; }
                var root = sceneRoots[n];
                if (roots.Count < 64) roots.Add(new JsonNode { { "scene", i }, { "root", n }, { "name", Cut(root.name) } });
                // Root labels are a preview; all roots still enter traversal.
                if (selectedRoot >= 0 && n != selectedRoot) continue;
                selectedFound = true;
                if (pending.Count == MaxPending) { truncated = true; break; }
                pending.Push(new Visit { Node = root.transform });
            }
        }
        while (pending.Count > 0 && visited < MaxNodes && rows.Count < MaxRows && watch.ElapsedMilliseconds < MaxMilliseconds)
        {
            var visit = pending.Pop(); var transform = visit.Node; if (transform == null) continue;
            var go = transform.gameObject; if (!go.activeInHierarchy) continue;
            if (!visit.Reported)
            {
                visited++; visit.Reported = true;
                if (Relevant(transform))
                {
                    var spellNode = transform; Component spell = null;
                    for (int depth = 0; spellNode != null && depth < 8; depth++, spellNode = spellNode.parent)
                        if ((spell = spellNode.gameObject.GetComponent("Spell")) != null) break;
                    var particle = go.GetComponent("ParticleSystem");
                    var p = transform.position;
                    rows.Add(new JsonNode { { "name", Cut(go.name) }, { "path", Path(transform) },
                        { "instance", go.GetInstanceID() }, { "x", p.x }, { "y", p.y }, { "z", p.z },
                        { "particle", particle != null }, { "particlePlaying", ParticlePlaying(particle) },
                        { "spell", spell == null ? "" : Cut(spell.gameObject.name) },
                        { "spellInstance", spell == null ? 0 : spell.gameObject.GetInstanceID() },
                        { "spellComponentId", spell == null ? 0 : spell.GetInstanceID() },
                        { "spellState", SpellState(spell) } });
                }
            }
            // Keep the parent's next child on the stack so a time/row budget never discards siblings.
            if (visit.Child < transform.childCount)
            {
                if (pending.Count + 2 > MaxPending) { truncated = true; continue; }
                var child = transform.GetChild(visit.Child++);
                pending.Push(visit);
                pending.Push(new Visit { Node = child });
            }
        }
        bool more = pending.Count != 0;
        continuation = more ? pending : null; incomplete = truncated;
        int next = more ? ++cursor : 0;
        if (!selectedFound && !truncated) throw new InvalidOperationException("Selected scene/root is unavailable");
        return new JsonNode { { "objects", rows }, { "roots", roots }, { "visited", visited }, { "truncated", truncated },
            { "nextCursor", next }, { "complete", !more && !truncated }, { "name", filter },
            { "elapsedMs", watch.ElapsedMilliseconds }, { "maxNodes", MaxNodes }, { "maxRows", MaxRows }, { "maxMs", MaxMilliseconds } };
    }
}
#endif
