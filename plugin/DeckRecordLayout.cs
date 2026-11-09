using System;
using System.Collections.Generic;
using System.Linq;

// Deck picker record line (NativeDeckRecord). Pure, no Unity; y grows upward. The line is centred under the picker's hero name (the anchor),
// one text-height gap below it; when that spot would leave the screen or touch any other control or art, the same spot above the anchor is
// tried. No free spot means no line (null, slot 0): it fails closed rather than sit on a control. Everything is relative (shares of the
// text's own height, margins of the screen), so the choice is the same at 1920x1080 and 1280x800. Obstacles are padded by a share of the
// text height so the glyphs never touch another control's art.
internal static class DeckRecordLayout
{
    internal const float GapShare = .35f, PadShare = .25f, ScreenMarginShare = .02f;
    // Gaps tried in order on each side. The hero actor's art bounds are looser than its pixels (live 2026-11-05: its box reaches below the hero
    // name), so the nearest spot can be blocked while a spot a little further down is clear.
    internal static readonly float[] GapShares = { GapShare, 1f, 1.7f };
    internal const int Below = 1, Above = 2;

    internal static DeckRect? Place(DeckRect anchor, float width, float height, float screenWidth, float screenHeight,
        IEnumerable<DeckRect> obstacles, out int slot)
    {
        slot = 0;
        if (anchor.Width <= 0 || anchor.Height <= 0 || width <= 0 || height <= 0 || screenWidth <= 0 || screenHeight <= 0) return null;
        float pad = height * PadShare, x = anchor.X + (anchor.Width - width) / 2;
        float marginX = screenWidth * ScreenMarginShare, marginY = screenHeight * ScreenMarginShare;
        var blocking = (obstacles ?? Enumerable.Empty<DeckRect>()).Where(rect => rect.Width > 0 && rect.Height > 0).ToArray();
        foreach (int side in new[] { Below, Above })
            foreach (float share in GapShares)
            {
                float gap = height * share;
                var text = new DeckRect(x, side == Below ? anchor.Y - gap - height : anchor.Y + anchor.Height + gap, width, height);
                if (text.X < marginX || text.Y < marginY || text.X + text.Width > screenWidth - marginX || text.Y + text.Height > screenHeight - marginY) continue;
                var padded = new DeckRect(text.X - pad, text.Y - pad, text.Width + 2 * pad, text.Height + 2 * pad);
                if (blocking.Any(rect => OptionsPaceLayout.Overlap(padded, rect))) continue;
                slot = side;
                return text;
            }
        return null;
    }
}
