using System;
using System.Collections.Generic;

// Pure decisions behind the 2026-10-28 UI fixes, kept free of Unity types so tests/deck-ui runs them directly.
internal static class HandheldPolicy
{
    // The offline bottom bar (menu gear) is up where a currency frame is shown and, as in retail, during a match.
    internal static bool BarVisible(bool currencyScene, bool gameplay, bool transitioning) => !transitioning && (currencyScene || gameplay);

    // Help popup: the factor that scales a (width,height) pixel rect to `share` of the screen; never shrinks, never above max.
    internal static float FitScale(float width, float height, float screenWidth, float screenHeight, float share, float max)
    {
        if (width <= 0 || height <= 0 || screenWidth <= 0 || screenHeight <= 0) return 1;
        return Math.Max(1f, Math.Min(max, Math.Min(screenWidth * share / width, screenHeight * share / height)));
    }
    // The handheld gets the big read-at-arm's-length popup; a desktop monitor only a modest enlargement.
    internal static void HelpFit(bool handheld, out float share, out float max) { share = handheld ? .88f : .8f; max = handheld ? 3f : 1.5f; }

    // Depth layering inside one camera (depth = distance from it, growing away). A tooltip must sit wholly in front of the
    // surface it overlaps: the distance to move it TOWARD the camera so its far face clears the surface's near face by `margin`.
    internal static float LiftInFront(float tipFar, float surfaceNear, float margin) => Math.Max(0f, tipFar - surfaceNear + margin);
    // A control that must stay behind a plane (a tooltip's): the distance to move it AWAY from the camera so its near face
    // clears `planeDepth` by `margin`.
    internal static float SinkBehind(float nearDepth, float planeDepth, float margin) => Math.Max(0f, planeDepth + margin - nearDepth);

    // Native start-up (GraphicsManager.UpdateFramerateSettings) may lower a saved rate above 30; the rate to put back, or 0.
    internal static int RateToRestore(int saved, int current) => saved > 30 && current < saved ? saved : 0;

    // Game Menu rows: drop the Achievements row, relabel the save selector, and never leave two dividers (null) in a row.
    internal static int TidyRows<T>(List<T> rows, Func<T, string> nameOf, string drop, string relabelName, Action<T> relabel) where T : class
    {
        int removed = 0;
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            T row = rows[i];
            if (row == null) continue;
            string name = nameOf(row);
            if (name == drop) { rows.RemoveAt(i); removed++; }
            else if (name == relabelName) relabel(row);
        }
        for (int i = rows.Count - 1; i > 0; i--)
            if (rows[i] == null && rows[i - 1] == null) { rows.RemoveAt(i); removed++; }
        return removed;
    }
}
