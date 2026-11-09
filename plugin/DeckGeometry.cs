using System;
using System.Collections.Generic;
using System.Linq;

// Handheld UI geometry (pure, no Unity). The controller navigation that used to live beside it was removed on
// 2026-10-28; see docs/design/deck-native-controls-archive.md.

// The existing authenticated diagnostic flag opens one bounded window, never continuous logging.
internal sealed class DeckInputTraceWindow
{
    private bool enabled;
    private double until;
    private int remaining;
    internal bool Active { get; private set; }
    internal void Update(bool value, double now)
    {
        if (value && !enabled) { until = now + 20; remaining = 1024; }
        enabled = value;
        Active = enabled && now < until && remaining > 0;
    }
    internal bool Take(double now)
    {
        Update(enabled, now);
        if (!Active) return false;
        remaining--; Active = remaining > 0;
        return true;
    }
}

// Pure modal context policy: is exactly one native button-list dialog (the save picker) on screen?
internal struct DeckModalContext
{
    private readonly int gameDialogs;
    private readonly object currentDialog;
    private readonly bool loading, system, notification;
    internal DeckModalContext(int gameDialogs, object currentDialog, bool loading, bool system, bool notification)
    {
        this.gameDialogs = gameDialogs; this.currentDialog = currentDialog;
        this.loading = loading; this.system = system; this.notification = notification;
    }
    internal bool OwnsSingleButtonList => gameDialogs == 1 && currentDialog == null && !loading && !system && !notification;
    internal bool Matches(DeckModalContext other) => gameDialogs == other.gameDialogs
        && ReferenceEquals(currentDialog, other.currentDialog) && loading == other.loading && system == other.system && notification == other.notification;
}

// Screen coordinates have their origin at bottom left, like Unity's projection APIs.
internal struct DeckRect
{
    internal float X, Y, Width, Height;
    internal DeckRect(float x, float y, float width, float height) { X = x; Y = y; Width = width; Height = height; }
}
internal static class DeckMulliganGeometry
{
    internal static DeckRect[] Cards(float width, float height, int count)
    {
        if (count != 3 && count != 4) return new DeckRect[0];
        float cardWidth = Math.Min(230, width * .18f), cardHeight = Math.Min(350, height * .46f);
        float gap = width * .035f, total = cardWidth * count + gap * (count - 1);
        var cards = new DeckRect[count];
        for (int i = 0; i < count; i++) cards[i] = new DeckRect((width - total) / 2 + i * (cardWidth + gap), height * .32f, cardWidth, cardHeight);
        return cards;
    }
    internal static DeckRect Confirm(float width, float height) => new DeckRect(width * .39f, height * .13f, width * .22f, height * .15f);
}

// Options > Pace plank, under the left column's last row (Signature Card Appearance). y grows upward. Measured live at
// 1280x720 (tests/deck-ui): that dropdown ends 68 px above the frame, the plank is 37 px, so the plank is centred in the band
// with 15.5 px either side. The gap is a share of the plank's height, so the layout is the same at every menu scale.
internal static class OptionsPaceLayout
{
    internal const float GapShare = .42f;
    internal static DeckRect Place(DeckRect anchor, float width, float height) =>
        new DeckRect(anchor.X + (anchor.Width - width) / 2, anchor.Y - height * GapShare - height, width, height);
    internal static bool Overlap(DeckRect a, DeckRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}

// How far a plate's art reaches past its hit body (screen pixels, per side; Vertical is per top and bottom edge).
internal struct FrameMargin
{
    internal float Left, Right, Vertical;
    internal FrameMargin(float left, float right, float vertical) { Left = left; Right = right; Vertical = vertical; }
}

// Deck picker "Guide" button (NativeDeckPickerGuide). y grows upward. Back's own plate size, right-aligned with the picker's Back button and
// stacked above it in Back-height steps (the REMATCH button's slot is the first step). It is placed by its ART, not by its hit rect: the frame
// reaches FrameMargin past the body (about 36 px at 1080p), so a plate that clears Play by body can still sit on Play's artwork. Play is a round
// button: its rect's inscribed ellipse is the obstacle for the art; My Collection and REMATCH are checked by body. The first step that is clear
// is used; none means no button. Decided from the live rects every frame and only answered while Play's rect is known.
internal static class DeckGuideLayout
{
    internal const int MaxSlots = 4;
    internal const float GapShare = .25f, MarginShare = .05f;
    internal static DeckRect? Place(DeckRect back, FrameMargin frame, DeckRect? play, IEnumerable<DeckRect> others, out int slot)
    {
        slot = 0;
        if (back.Width <= 0 || back.Height <= 0 || !play.HasValue || play.Value.Width <= 0 || play.Value.Height <= 0) return null;
        float margin = back.Height * MarginShare;
        var obstacles = others.Where(rect => rect.Width > 0 && rect.Height > 0).ToArray();
        DeckRect ring = play.Value;
        float cx = ring.X + ring.Width / 2, cy = ring.Y + ring.Height / 2, reach = Math.Min(ring.Width, ring.Height) / 2 + margin;
        for (int step = 1; step <= MaxSlots; step++)
        {
            var plate = new DeckRect(back.X, back.Y + step * back.Height * (1 + GapShare), back.Width, back.Height);
            // The art's nearest point to Play's centre must be outside the ring.
            float nearX = Math.Max(plate.X - frame.Left, Math.Min(cx, plate.X + plate.Width + frame.Right));
            float nearY = Math.Max(plate.Y - frame.Vertical, Math.Min(cy, plate.Y + plate.Height + frame.Vertical));
            if ((nearX - cx) * (nearX - cx) + (nearY - cy) * (nearY - cy) < reach * reach) continue;
            var padded = new DeckRect(plate.X - margin, plate.Y - margin, plate.Width + 2 * margin, plate.Height + 2 * margin);
            if (obstacles.Any(rect => OptionsPaceLayout.Overlap(padded, rect))) continue;
            slot = step;
            return plate;
        }
        return null;
    }
}
