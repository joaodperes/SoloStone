using System.Globalization;

// Test-control policy only; the native adapter owns no pack transaction or reveal implementation.
internal static class TestControlPacks
{
    internal const int MaxCount = 40;
    internal const string Back = "packs.back", Done = "packs.done", Reveal = "packs.mass.reveal",
        Next = "packs.mass.next", SummaryDone = "packs.mass.done";

    internal static string CardId(int instance) => "packs.card:" + instance.ToString(CultureInfo.InvariantCulture);

    internal static string Blocker(int count, bool packScene, bool pending, bool ready, int available, bool massAllowed)
    {
        if (count < 1 || count > MaxCount) return "Pack count must be 1-40.";
        if (!packScene) return "open-packs requires PACKOPENING.";
        if (pending) return "A native pack reveal is pending.";
        if (!ready) return "Pack opening is busy or not ready.";
        if (available < count) return "Not enough packs in one native stack.";
        if (count >= 2 && !massAllowed) return "Native mass opening is unavailable for this stack.";
        return null;
    }
}
