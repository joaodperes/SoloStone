using System.Collections.Generic;

// Flip All (owner 2026-10-29): a native button on the pack-opening screen reveals a single pack's remaining face-down cards
// one after another. Each step is the native click path (PackOpeningCard.ForceReveal -> OnPress: sound, rarity FX,
// OnCardRevealed, native Done after the last). Retail has no single-pack reveal-all (Space reveals one random card with
// QuickOpen); many packs go through native mass opening, whose highlights screen has its own Reveal All. Nothing here
// touches the save: the pack was committed before the animation (BeginNativePack) and native Done still acknowledges it
// (CompleteNativePackReveal). Pure sequencing and text, so tests/test-control runs it on the Mac.
internal static class PackFlipText
{
    internal const string ButtonLabel = "Flip All", ButtonObject = "SoloStoneFlipAllButton", ButtonId = "pack.flip-all";
}

// One hidden card as the native director has it this frame.
internal struct PackFlipCard
{
    // Ready, reveal enabled and input enabled: a click on it would reveal it now. All five are Live once the pack has landed.
    internal bool Live;
    internal bool Revealed;
    // Screen x: a run flips left to right.
    internal float Order;
    internal PackFlipCard(bool live, bool revealed, float order) { Live = live; Revealed = revealed; Order = order; }
}

internal enum PackFlipStep { Idle, Wait, Reveal }

// Per committed reveal (owner = the native director our transaction armed; the client makes a new one per pack).
// Interrupting is free: a new owner, no owner (scene left, Done, quit) or no face-down card ends the run; the save never
// depends on it. A card that stays face down after its reveal stalls the run for this pack: the player clicks it.
internal sealed class PackFlipSequence
{
    internal const float StaggerSeconds = 0.35f;     // between flips: each card's own reveal sound and FX still read
    private object owner;
    private bool running, stalled;
    private float next;
    private readonly HashSet<int> attempted = new HashSet<int>();

    internal bool Running => running;
    internal bool Stalled => stalled;

    // Every card present, Live, and at least one still face down.
    internal static bool Landed(IReadOnlyList<PackFlipCard> cards)
    {
        if (cards == null || cards.Count == 0) return false;
        bool faceDown = false;
        foreach (var card in cards) { if (!card.Live) return false; faceDown |= !card.Revealed; }
        return faceDown;
    }

    // The leftmost face-down card (native index on ties), or -1.
    internal static int NextCard(IReadOnlyList<PackFlipCard> cards)
    {
        int best = -1;
        for (int i = 0; i < (cards?.Count ?? 0); i++)
            if (cards[i].Live && !cards[i].Revealed && (best < 0 || cards[i].Order < cards[best].Order)) best = i;
        return best;
    }

    // The button shows while this pack has landed with cards face down, no run is going, and nothing covers the screen.
    internal bool ButtonVisible(object director, IReadOnlyList<PackFlipCard> cards, bool paused) =>
        director != null && ReferenceEquals(director, owner) && !running && !stalled && !paused && Landed(cards);

    // The button's press: starts at once. False when there is nothing to flip.
    internal bool Start(object director, IReadOnlyList<PackFlipCard> cards, float now)
    {
        Own(director);
        if (director == null || running || stalled || !Landed(cards)) return false;
        running = true; next = now;
        return true;
    }

    // Once per frame. paused: a dialog or menu covers the screen (the run waits, then resumes).
    internal PackFlipStep Step(object director, IReadOnlyList<PackFlipCard> cards, float now, bool paused, out int index)
    {
        index = -1;
        Own(director);
        if (director == null || !Landed(cards)) { running = false; return PackFlipStep.Idle; }
        if (!running) return PackFlipStep.Idle;
        if (paused) { if (next < now + StaggerSeconds) next = now + StaggerSeconds; return PackFlipStep.Wait; }
        if (now < next) return PackFlipStep.Wait;
        index = NextCard(cards);
        if (index < 0) { running = false; return PackFlipStep.Idle; }
        if (!attempted.Add(index)) { running = false; stalled = true; index = -1; return PackFlipStep.Idle; }
        next = now + StaggerSeconds;
        return PackFlipStep.Reveal;
    }

    private void Own(object director)
    {
        if (ReferenceEquals(director, owner)) return;
        owner = director; running = false; stalled = false; attempted.Clear();
    }
}

// Pixel bounds are [left, bottom, right, top], in Unity's bottom-left screen coordinates.
internal static class PackFlipBounds
{
    internal static bool Clear(float[] button, IReadOnlyList<float[]> obstacles, float width, float height)
    {
        const float gap = 8f;
        if (!Valid(button) || button[0] < gap || button[1] < gap || button[2] > width - gap || button[3] > height - gap)
            return false;
        foreach (var other in obstacles)
            if (!Valid(other) || (button[0] < other[2] + gap && button[2] > other[0] - gap
                && button[1] < other[3] + gap && button[3] > other[1] - gap)) return false;
        return true;
    }
    private static bool Valid(float[] bounds)
    {
        if (bounds == null || bounds.Length != 4) return false;
        foreach (float value in bounds) if (float.IsNaN(value) || float.IsInfinity(value)) return false;
        return bounds[2] > bounds[0] && bounds[3] > bounds[1];
    }
}
