using System;
using System.Collections.Generic;

internal static class ProfileMenuGeometry
{
    // One native menu plank is this share of the screen height; the whole dialog then shrinks to fit if needed.
    internal const float UnitShare = .068f, MaxWidthShare = .8f, MaxHeightShare = .9f;

    // Picker paging: six saves a page; after a delete the page falls back to the last one that still has rows.
    internal const int PageSize = 6;
    internal static int ClampPage(int page, int count) => Math.Max(0, Math.Min(page, Math.Max(0, (count - 1) / PageSize)));
    internal static bool HasNext(int page, int count) => (page + 1) * PageSize < count;

    internal struct Fit
    {
        internal float Scale, CenterX, CenterY;
        internal Fit(float scale, float centerX, float centerY) { Scale = scale; CenterX = centerX; CenterY = centerY; }
    }

    // unitPixels: current on-screen height of one native plank; content: current on-screen dialog size.
    internal static Fit Place(float width, float height, float unitPixels, float contentWidth, float contentHeight)
    {
        // Before fitting, the native menu renders well under one pixel tall: never clamp to whole pixels.
        const float tiny = 1e-4f;
        float scale = Math.Min(height * UnitShare / Math.Max(tiny, unitPixels),
            Math.Min(width * MaxWidthShare / Math.Max(tiny, contentWidth), height * MaxHeightShare / Math.Max(tiny, contentHeight)));
        return new Fit(scale, width / 2, height / 2);
    }

    // Current-save check on a plank. The plank's light face sits inside its dark rivet ends: measured on the native plank
    // it spans FaceLeft..FaceRight of the whole row's extent (resolution independent). The check sits at a fixed inset
    // inside the face's left edge (one pad past the border); a name that would touch it is nudged right, and only a name
    // that cannot be nudged inside the face (extreme length) gets no check: the caller then labels the row in text.
    internal const float FaceLeft = .15f, FaceRight = .85f, CheckHeightShare = .42f, CheckPadShare = .5f, NameGapShare = .6f,
        EstimatedCharShare = .045f;
    internal const int FallbackChars = 14;

    internal struct CheckPlan
    {
        internal float Center, NameShift;
        internal CheckPlan(float center, float nameShift) { Center = center; NameShift = nameShift; }
    }

    // rowMin/rowMax: the whole row's extent along the row axis; nameMin/nameMax: the rendered name's extent, when
    // measured (null: estimated from nameChars; the native glyph bounds are not laid out yet when the row is built).
    internal static CheckPlan? PlanCheck(float rowMin, float rowMax, float? nameMin, float? nameMax, float checkWidth, int nameChars)
    {
        float span = rowMax - rowMin, pad = checkWidth * CheckPadShare;
        float faceMin = rowMin + span * FaceLeft, faceMax = rowMin + span * FaceRight;
        float center = faceMin + pad + checkWidth / 2;
        if (center + checkWidth / 2 + pad > faceMax) return null;
        bool measured = nameMin.HasValue && nameMax.HasValue;
        float lo, hi;
        if (measured) { lo = nameMin.Value; hi = nameMax.Value; }
        else
        {
            float half = Math.Max(1, nameChars) * EstimatedCharShare * span / 2, mid = (rowMin + rowMax) / 2;
            lo = mid - half; hi = mid + half;
        }
        float need = center + checkWidth / 2 + pad * NameGapShare;
        float shift = Math.Max(0, need - lo);
        if (hi + shift > faceMax - pad * .5f) return null;
        return new CheckPlan(center, shift);
    }

    // Text marker for a current save when the check cannot be placed: always short enough for the plank face.
    internal static string FallbackLabel(string label)
    {
        label = label ?? "";
        int room = FallbackChars - 2;
        return "* " + (label.Length > room ? label.Substring(0, room - 2) + ".." : label);
    }

    // Native text input over a hidden label, in the GUI viewport fractions UniversalInputManager expects (top-left
    // origin). frame: the label's text rect in screen pixels (bottom-left origin); glyphs: its rendered glyphs, when known.
    // Width follows the frame; height follows the glyphs so the input font matches the label.
    internal static DeckRect NameInput(DeckRect frame, DeckRect? glyphs, float width, float height)
    {
        float lineHeight = glyphs.HasValue && glyphs.Value.Height > 0 ? glyphs.Value.Height * 1.35f : frame.Height * .9f;
        lineHeight = Math.Max(Math.Min(lineHeight, frame.Height * 1.2f), Math.Min(14f, height * .02f));
        float centerY = glyphs.HasValue && glyphs.Value.Height > 0 ? glyphs.Value.Y + glyphs.Value.Height / 2 : frame.Y + frame.Height / 2;
        float x = Math.Max(0, frame.X), right = Math.Min(width, frame.X + frame.Width);
        float top = Math.Max(0, Math.Min(height - lineHeight, height - (centerY + lineHeight / 2)));
        return new DeckRect(x / width, top / height, Math.Max(0, right - x) / width, lineHeight / height);
    }
}

// After the save list or form is rebuilt in place, the second click of a double-click (or the click that closed an
// in-game dialog) would land on whatever row now sits under the pointer, maybe a save slot that switches saves.
internal sealed class ClickLockout
{
    internal const float Seconds = .3f;
    private float until = float.MinValue;
    internal void Arm(float now) => until = now + Seconds;
    internal bool Blocks(float now) => now < until;
}

// Test-control hit truth while the picker (or the startup shell) is up. An alert above it, e.g. the delete confirm or the
// error popup, owns input: only its own buttons count and the planks under it are covered. A plank inside its click
// lockout ignores the click, so it reports blocked and press waits instead of firing into nothing. Null: not blocked.
internal static class ProfileInputBlock
{
    internal const string Lockout = "clickLockout";
    internal static string Reason(bool blocksHome, bool dialog, bool inDialog, bool owned, bool locked) =>
        !blocksHome ? null : dialog ? (inDialog ? null : "dialog") : !owned ? "profileSelector" : locked ? Lockout : null;
}

// The Options kit loads once; a failed load is retried at most once per picker open, never in a loop.
internal sealed class ProfileFormRetry
{
    private enum Phase { Idle, Loading, Loaded, Failed }
    private Phase phase;
    internal bool Loaded => phase == Phase.Loaded;
    internal bool Start(bool newOpen)
    {
        if (phase == Phase.Loading || phase == Phase.Loaded || phase == Phase.Failed && !newOpen) return false;
        phase = Phase.Loading;
        return true;
    }
    internal void Succeeded() => phase = Phase.Loaded;
    internal void Failed() => phase = Phase.Failed;
}

// The native text input ignores UseTextInput until its input field is ready; the name edit retries a few frames
// later instead of leaving the field blank and locked.
internal sealed class TextInputRetry
{
    internal const int MaxAttempts = 20;
    private int attempts;
    internal void Reset() => attempts = 0;
    // Called after a refused UseTextInput: true while another try is allowed.
    internal bool Again() => ++attempts < MaxAttempts;
}

// Screen-pixel rectangles only. Touching edges are allowed; missing/empty measurements fail closed.
internal static class ProfilePaceGeometry
{
    internal struct Element
    {
        internal string Id;
        internal DeckRect Rect;
        internal Element(string id, DeckRect rect) { Id = id; Rect = rect; }
    }

    internal static List<string> Check(DeckRect inner, IList<Element> elements)
    {
        var issues = new List<string>();
        if (!Valid(inner)) { issues.Add("inner:empty-or-invalid"); return issues; }
        for (int i = 0; i < elements.Count; i++)
        {
            var item = elements[i]; var rect = item.Rect;
            if (!Valid(rect)) { issues.Add(item.Id + ":empty-or-invalid"); continue; }
            if (rect.X < inner.X || rect.Y < inner.Y || rect.X + rect.Width > inner.X + inner.Width
                || rect.Y + rect.Height > inner.Y + inner.Height) issues.Add(item.Id + ":outside");
            for (int j = 0; j < i; j++)
            {
                var other = elements[j]; var b = other.Rect;
                if (Valid(b) && rect.X < b.X + b.Width && b.X < rect.X + rect.Width
                    && rect.Y < b.Y + b.Height && b.Y < rect.Y + rect.Height)
                    issues.Add(other.Id + "/" + item.Id + ":overlap");
            }
        }
        return issues;
    }

    private static bool Valid(DeckRect rect) => Finite(rect.X) && Finite(rect.Y) && Finite(rect.Width) && Finite(rect.Height)
        && rect.Width > 0 && rect.Height > 0;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
