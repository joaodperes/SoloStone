using System;
using System.Collections.Generic;

// Native button chrome rules (pure, no Unity). A native UIBButton plate is made of two 3-slice strips: a metal frame ("Bracket") and a gold
// plate ("Button"). In each strip the two end caps keep a uniform scale and only the centre slice stretches. The cream label window runs from
// inside one gold cap to inside the other: its width is the plate minus CreamInsetShare of the two caps (measured on retail Back and My
// Collection, 2026-11-05). A custom button is right when its art keeps those proportions and its label sits inside that window.
// Screen rects: y grows upward, pixels (the `buttons` test-control audit reports them; NativeButtonKit measures and widens).
internal static class ButtonChromeRules
{
    // Share of both gold caps' widths the cream window gives up (Back: plate 98 px, caps 41 px, cream 75 px at 1080p).
    internal const float CreamInsetShare = .56f;
    // The label may fill this much of the cream window (UberText ResizeToFit shrinks the letters, never the plate).
    internal const float LabelFillShare = .94f;
    // Pixel slack: projected rects of adjoining slices overlap by a pixel or two, glyph bounds are rounded.
    internal const float Tolerance = 2f;
    // An end cap whose x:y scale ratio leaves 1 +/- this is stretched (a root scale applied to a 3-slice button does exactly that).
    internal const float CapAspectTolerance = .03f;

    internal const string CapStretched = "cap-stretched", LabelOutsideCream = "label-outside-cream", LabelOutsideBody = "label-outside-body",
        BodyOutsideArt = "body-outside-art", LabelOutsideArt = "label-outside-art", NoLabel = "label-not-drawn", GlowNarrow = "glow-narrow";
    // The hover glow strip wraps the plate: a hidden strip narrower than this share of the plate leaves the plate's ends unlit (or lights a stub).
    internal const float GlowCoverShare = .98f;

    internal sealed class Facts
    {
        internal DeckRect? Art, Body, Label, Plate;
        // Widths of the plate strip's two end caps (the gold ends); null when the button has no 3-slice strips.
        internal float? CapLeft, CapRight;
        // Worst cap scale ratio relative to the native source's (1 = not stretched).
        internal float CapAspect = 1f;
        // The same measure before dividing by the native source's (informational: native art need not be uniform).
        internal float RawCapAspect = 1f;
        // Narrowest hidden (glow) strip's width over the plate's width, null when the button has none.
        internal float? GlowToPlate;
        internal bool Sliced => Plate.HasValue && CapLeft.HasValue && CapRight.HasValue;
    }

    // x:y scale ratio of an end cap, read along the axes that really run along the screen: `rightLocal` / `upLocal` are the reading and vertical
    // directions in the cap's own local axes (Transform.InverseTransformDirection), `scale` its lossy scale. A cap whose width runs along local z
    // is judged on z, not on a hard-coded x. 1 = uniform.
    internal static float CapAspect(float[] scale, float[] rightLocal, float[] upLocal)
    {
        int across = Dominant(rightLocal), up = Dominant(upLocal);
        if (across == up) up = (across + 1) % 3;   // degenerate (both directions on one axis): judge against any other axis
        float height = Math.Abs(scale[up]);
        return height < 1e-6f ? 1f : Math.Abs(scale[across]) / height;
    }

    // Raw cap aspect relative to the native source's: 1 = not stretched by us. No baseline (a native button) = not judged.
    internal static float RelativeAspect(float raw, float baseline) => baseline > 1e-6f ? raw / baseline : 1f;

    internal static int Dominant(float[] direction)
    {
        float x = Math.Abs(direction[0]), y = Math.Abs(direction[1]), z = Math.Abs(direction[2]);
        return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
    }

    internal static DeckRect Cream(DeckRect plate, float capLeft, float capRight)
    {
        float width = Math.Max(0, plate.Width - CreamInsetShare * (capLeft + capRight));
        return new DeckRect(plate.X + (plate.Width - width) / 2, plate.Y, width, plate.Height);
    }

    // Widest the drawn label may be inside the cream window.
    internal static float LabelRoom(DeckRect plate, float capLeft, float capRight) => Cream(plate, capLeft, capRight).Width * LabelFillShare;

    internal static bool Inside(DeckRect inner, DeckRect outer, float slack = Tolerance) =>
        inner.X >= outer.X - slack && inner.Y >= outer.Y - slack && inner.X + inner.Width <= outer.X + outer.Width + slack
        && inner.Y + inner.Height <= outer.Y + outer.Height + slack;

    // Same horizontal span only: the label's tall glyph box need not sit inside the window's height.
    internal static bool InsideX(DeckRect inner, DeckRect outer, float slack = Tolerance) =>
        inner.X >= outer.X - slack && inner.X + inner.Width <= outer.X + outer.Width + slack;

    // Issues for one button; empty = right. A button with a label that is not drawn (hidden text, empty string) is not judged on the label.
    internal static List<string> Judge(Facts facts, bool labelExpected)
    {
        var issues = new List<string>();
        if (facts.Sliced && Math.Abs(facts.CapAspect - 1f) > CapAspectTolerance) issues.Add(CapStretched);
        if (facts.Art.HasValue && facts.Body.HasValue && !Inside(facts.Body.Value, facts.Art.Value)) issues.Add(BodyOutsideArt);
        if (facts.GlowToPlate.HasValue && facts.GlowToPlate.Value < GlowCoverShare) issues.Add(GlowNarrow);
        if (!facts.Label.HasValue) { if (labelExpected) issues.Add(NoLabel); return issues; }
        DeckRect label = facts.Label.Value;
        if (facts.Art.HasValue && !Inside(label, facts.Art.Value)) issues.Add(LabelOutsideArt);
        if (facts.Sliced)
        {
            DeckRect cream = Cream(facts.Plate.Value, facts.CapLeft.Value, facts.CapRight.Value);
            if (!InsideX(label, cream)) issues.Add(LabelOutsideCream);
        }
        else if (facts.Body.HasValue && !InsideX(label, facts.Body.Value)) issues.Add(LabelOutsideBody);
        return issues;
    }
}
