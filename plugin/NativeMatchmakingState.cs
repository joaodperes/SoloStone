using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Test-control `ui` report for the native Finding Opponent popup's Cancel plate. Read only.
internal static partial class NativeMatchmaking
{
#if !SOLOSTONE_RELEASE
    private static readonly FieldInfo OriginalRotation = AccessTools.Field(typeof(UIBButton), "m_RootObjectOriginalRotation");
#endif

#if !SOLOSTONE_RELEASE
    // reportIssue-style block: { active, shown, cancel: { activeSelf, enabled, faceDown, plateVisible, renderers: [...] } }.
    // A correct disabled plate: activeSelf, !enabled, faceDown, plateVisible, and renderers with a real material/texture.
    internal static JsonNode State()
    {
        var state = new JsonNode { { "active", active }, { "shown", popup != null && popup.IsShown() } };
        try
        {
            var button = popup?.m_cancelButton;
            if (button == null) { state["cancel"] = null; return state; }
            var root = button.m_RootObject != null ? button.m_RootObject.transform : button.transform;
            var rows = new List<object>();
            int visible = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                bool drawn = renderer.enabled && renderer.gameObject.activeInHierarchy;
                if (drawn && renderer.isVisible) visible++;
                if (rows.Count >= 40) continue;
                rows.Add(new JsonNode { { "path", Path(renderer.transform, root) }, { "type", renderer.GetType().Name },
                    { "enabled", renderer.enabled }, { "activeInHierarchy", renderer.gameObject.activeInHierarchy },
                    { "isVisible", renderer.isVisible }, { "materials", TestControlProtocol.List(Materials(renderer)) } });
            }
            state["cancel"] = new JsonNode {
                { "activeSelf", button.gameObject.activeSelf }, { "activeInHierarchy", button.gameObject.activeInHierarchy },
                { "enabled", button.IsEnabled() }, { "faceDown", FaceDown(button) },
                { "rootEuler", Vec(root.localEulerAngles) }, { "disabledRotation", Vec(button.m_DisabledRotation) },
                { "colliderEnabled", button.GetComponent<Collider>()?.enabled },
                { "plateVisible", visible > 0 }, { "visibleRenderers", visible }, { "renderers", TestControlProtocol.List(rows) } };
        }
        catch (Exception error) { state["error"] = error.GetType().Name + ": " + error.Message; }
        return state;
    }
#endif

    private static string Vec(Vector3 value) => value.x.ToString("0.#") + "," + value.y.ToString("0.#") + "," + value.z.ToString("0.#");

#if !SOLOSTONE_RELEASE
    // Retail's disabled pose: root rotation == original rotation + m_DisabledRotation (UIBButton.Flip). Null = unknown.
    private static bool? FaceDown(UIBButton button)
    {
        if (button.m_RootObject == null || OriginalRotation == null) return null;
        var original = OriginalRotation.GetValue(button) as Vector3?;
        if (original == null) return null;
        // Compare rotations, not Euler components: the same pose reads as 0,0,180 or 180,180,0.
        var target = Quaternion.Euler(original.Value + button.m_DisabledRotation);
        return Quaternion.Angle(button.m_RootObject.transform.localRotation, target) < 1f;
    }
#endif

    private static string Path(Transform node, Transform root)
    {
        var names = new List<string>();
        for (int depth = 0; node != null && depth < 12 && node != root; depth++, node = node.parent) names.Add(node.name);
        names.Reverse();
        return root.name + (names.Count == 0 ? "" : "/" + string.Join("/", names));
    }

#if !SOLOSTONE_RELEASE
    private static List<object> Materials(Renderer renderer)
    {
        var rows = new List<object>();
        foreach (var material in renderer.sharedMaterials)
        {
            if (material == null) { rows.Add(new JsonNode { { "name", null } }); continue; }
            var texture = material.HasProperty("_MainTex") ? material.mainTexture : null;
            rows.Add(new JsonNode { { "name", material.name }, { "shader", material.shader == null ? null : material.shader.name },
                { "texture", texture == null ? null : texture.name },
                { "color", material.HasProperty("_Color") ? material.color.ToString() : null } });
        }
        return rows;
    }
#endif
}
