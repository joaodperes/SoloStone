using System;

// Options > FPS Counter: test-control id, copy, the averaging window and where the overlays sit. Pure (no Unity types), so
// test-control checks it on the Mac.
internal static class NativeFpsCounterText
{
    internal const string Id = "options.fps-counter", ObjectName = "SoloStoneFpsCounterCheckbox", Label = "FPS Counter";
    internal static string Overlay(bool ready, int fps) => ready ? fps + " FPS" : "-- FPS";
}

// The always-on brand label (NativeBrandLabel): tells the player this is not retail. Fixed copy, no setting.
internal static class BrandLabelText
{
    internal const string Text = "SoloStone Offline Mod";
}

// Mean frame rate over a window of real time: frames / seconds, so a stall inside the window shows as the average it is. Feed it
// Time.unscaledDeltaTime once per frame; pause and slow motion never touch it.
internal sealed class FpsAverager
{
    internal const float Window = .5f;
    private int frames;
    private float seconds;
    internal bool Ready { get; private set; }
    internal int Fps { get; private set; }

    internal void Add(float unscaledDelta)
    {
        if (!(unscaledDelta > 0f) || float.IsInfinity(unscaledDelta)) return;
        frames++; seconds += unscaledDelta;
        if (seconds < Window) return;
        Fps = (int)(frames / seconds + .5f); Ready = true;
        frames = 0; seconds = 0;
    }

    internal void Reset() { frames = 0; seconds = 0; Fps = 0; Ready = false; }
}

// Top-right corner, y grows downward (GUI space). Top-left is the native opponent name plate, the bottom corners hold the
// achievement toast (bar social bone) and the player's mana, deck and hero power; the old match debug box already lived
// top-right without covering any control. Only the opposing pet's board corner (cosmetic) can share it. Size and margin are
// shares of the screen height, so 1280x800 (Deck) and 1920x1080 scale alike.
// Two labels share the corner: the brand label (always on) at the top, the FPS label (when the setting is on) directly below it,
// both right-aligned to the same edge. With FPS off the brand label sits alone at the top.
internal struct FpsRect { internal float X, Y, Width, Height; }
internal static class FpsCounterLayout
{
    internal const float FontShare = 1f / 40f, MarginShare = .012f;
    internal const int MinFont = 14;
    // The brand text is longer than "NN FPS", so its font is a little smaller than the FPS one.
    internal const float BrandFontShare = 1f / 44f, GapShare = .002f;
    internal const int MinBrandFont = 12;
    internal static int FontSize(int screenHeight) => Math.Max(MinFont, (int)Math.Round(screenHeight * FontShare));
    internal static int BrandFontSize(int screenHeight) => Math.Max(MinBrandFont, (int)Math.Round(screenHeight * BrandFontShare));
    // The brand label: the top-right corner, where the FPS label alone used to sit.
    internal static FpsRect PlaceBrand(int screenWidth, int screenHeight, float textWidth, float textHeight)
    {
        float margin = screenHeight * MarginShare;
        return new FpsRect { X = screenWidth - margin - textWidth, Y = margin, Width = textWidth, Height = textHeight };
    }
    // The FPS label: right edge on the brand label's, top just under its bottom (a hair of gap). `brand` empty (not drawn this
    // frame): the corner itself, so a failed brand label never leaves FPS off screen.
    internal static FpsRect PlaceFps(int screenWidth, int screenHeight, FpsRect brand, float textWidth, float textHeight)
    {
        float margin = screenHeight * MarginShare;
        float top = brand.Width > 0 && brand.Height > 0 ? brand.Y + brand.Height + Math.Max(1f, screenHeight * GapShare) : margin;
        return new FpsRect { X = screenWidth - margin - textWidth, Y = top, Width = textWidth, Height = textHeight };
    }
    internal static bool Overlap(FpsRect a, FpsRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    // `lower` is directly under `upper`: no overlap, a gap of at most a few pixels, same right edge.
    internal static bool DirectlyBelow(FpsRect upper, FpsRect lower, int screenHeight) =>
        !Overlap(upper, lower) && lower.Y >= upper.Y + upper.Height - .01f && lower.Y - (upper.Y + upper.Height) <= Math.Max(2f, screenHeight * .01f)
        && Math.Abs((upper.X + upper.Width) - (lower.X + lower.Width)) < .5f;
    // The test-control rect convention: normalized 1280x720, top-left origin (UiElement.X/Y/W/H).
    internal static float[] Normalized(FpsRect rect, int screenWidth, int screenHeight) => new[] {
        rect.X * 1280f / screenWidth, rect.Y * 720f / screenHeight, rect.Width * 1280f / screenWidth, rect.Height * 720f / screenHeight };
    internal static bool OnScreen(FpsRect rect, int screenWidth, int screenHeight) =>
        rect.Width > 0 && rect.Height > 0 && rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= screenWidth && rect.Y + rect.Height <= screenHeight;
}
