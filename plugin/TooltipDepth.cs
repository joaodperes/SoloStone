using System.Collections.Generic;
using UnityEngine;

// Camera-depth layering for native tooltips (owner 2026-10-28): the tooltip prefab shares a layer, and so a depth plane, with the
// surfaces it points at, so mod-added surfaces (the Create-save dialog, the HUB Help button) can cover it. Depth = distance
// along the camera's forward axis; decisions are HandheldPolicy.LiftInFront / SinkBehind.
internal static class TooltipDepth
{
    // Depth span of the enabled renderers under `root`, ignoring those under `skip`.
    internal static bool Span(GameObject root, GameObject skip, Camera view, out float near, out float far)
    {
        near = float.MaxValue; far = float.MinValue;
        if (root == null || view == null) return false;
        Vector3 origin = view.transform.position, forward = view.transform.forward;
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || (skip != null && renderer.transform.IsChildOf(skip.transform))) continue;
            Bounds bounds = renderer.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                float depth = Vector3.Dot(new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z) - origin, forward);
                near = Mathf.Min(near, depth); far = Mathf.Max(far, depth);
            }
        }
        return near <= far;
    }

    // Move `tip` toward its camera until it is wholly in front of everything under `surface` (the tip itself excluded).
    // Returns the distance moved (0 when already in front or nothing could be measured).
    internal static float RaiseAbove(GameObject tip, GameObject surface, Camera view)
    {
        if (tip == null || !Span(surface, tip, view, out float surfaceNear, out _) || !Span(tip, null, view, out float tipNear, out float tipFar)) return 0;
        float lift = HandheldPolicy.LiftInFront(tipFar, surfaceNear, Mathf.Max(.01f, (tipFar - tipNear) * .25f));
        if (lift > 0) tip.transform.position -= view.transform.forward * lift;
        return lift;
    }
}
