using System.Collections.Generic;
using System.Globalization;

// #57 SoloStone welcome (NativeWelcomeUI): popup copy, reward scroll copy/items and test-control ids. Pure text (no Unity,
// no ledger), so test-control checks it on the Mac. Amounts are passed in from the saved welcome receipt,
// never typed here.
internal static class NativeWelcomeText
{
    internal const string PopupId = "LOCAL_SOLOSTONE_WELCOME";
    internal const string OkId = "welcome.ok";
    internal const string Header = "WELCOME TO SOLOSTONE", OkLabel = "OK";
    internal const string SignOff = "Have fun. Live life!";

    // Same budget as Help: fits the native popup at 1280x720 without scrolling (test-control checks it). The gift itself is
    // presented next by the native reward scroll, so the popup only introduces SoloStone and says the gift follows.
    internal const string Body =
        "Hearthstone offline: the Violet Hold meta against NPC opponents. Climb from Bronze to Legend; wins earn gold and dust for packs and crafting. " +
        "No timers, streaks or expiring offers. Ever.\n\n" +
        "Every class has a starter deck. Win to level it up and unlock more Core cards.\n\n" +
        "Your welcome gift comes next.\n\n" +
        SignOff;

    // Native reward scroll ("You've received"), shown after OK. Its dismiss control gets a reviewed id.
    internal const string ScrollDoneId = "welcome.scroll.done";
    internal const string ScrollTitle = "Welcome gift";
    internal static string ScrollDescription(int kitGoldCap, string cards = null) =>
        "Deck kit: any ready-made deck up to " + Amount(kitGoldCap) + " gold." + (string.IsNullOrEmpty(cards) ? "" : "\n\nCards:\n" + cards);

    internal enum ScrollItemKind { StandardPacks, Gold, Dust }

    // One scroll (retail fits up to 4 items): packs, gold, dust. Cards and the deck kit use the description.
    internal static IReadOnlyList<KeyValuePair<ScrollItemKind, int>> ScrollItems(int packs, int gold, int dust) => new[] {
        new KeyValuePair<ScrollItemKind, int>(ScrollItemKind.StandardPacks, packs),
        new KeyValuePair<ScrollItemKind, int>(ScrollItemKind.Gold, gold),
        new KeyValuePair<ScrollItemKind, int>(ScrollItemKind.Dust, dust) };

    internal static string Amount(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    // The popup's single OK button; every other popup keeps its own ids (null here).
    internal static string DialogButtonId(string popupId, string button) =>
        popupId == PopupId && button == "okay" ? OkId : null;
}

internal enum NativeWelcomePhase { Idle, Alert, AwaitScroll, Loading, Shown, Done }

// #57 welcome flow (pure; NativeWelcomeUI drives it): AlertPopup -> OK -> (a later frame, no dialog showing) native reward
// scroll -> dismiss commits Seen. Nothing here grants: the gift was committed when the save was created.
// - Seen only on a dismiss of the shown scroll that is not the fatal-error hide (RewardScroll.OnFatalError also hides).
// - The scroll is unavailable (load failure or not ready in time): Seen after the popup alone, so nobody is stuck.
// - The scroll vanished without a hide (scene change destroy): nothing is committed; both show again next launch.
// - One attempt per hub session (no phase goes back to Idle).
internal sealed class NativeWelcomeFlow
{
    // Focused seconds from OK until the scroll must be ready (the popup's hide animation plus a prefab load).
    internal const float ScrollTimeoutSeconds = 20f;
    private int okFrame;
    private float waited;

    internal NativeWelcomePhase Phase { get; private set; }
    // The scroll owns the HUB (BlocksHome, toasts, catch-up, selector, shop/Feats/Help openings) from OK until it is gone.
    internal bool ScrollActive => Phase == NativeWelcomePhase.AwaitScroll || Phase == NativeWelcomePhase.Loading || Phase == NativeWelcomePhase.Shown;
    internal bool CanShowAlert => Phase == NativeWelcomePhase.Idle;

    internal void AlertShown() { if (Phase == NativeWelcomePhase.Idle) Phase = NativeWelcomePhase.Alert; }

    internal void AlertAcknowledged(int frame)
    {
        if (Phase != NativeWelcomePhase.Alert) return;
        Phase = NativeWelcomePhase.AwaitScroll; okFrame = frame; waited = 0f;
    }

    // Open the scroll on a later frame than OK, once the popup's dialog is gone and the HUB is stable.
    internal bool ReadyToOpenScroll(int frame, bool dialogShowing, bool hubStable) =>
        Phase == NativeWelcomePhase.AwaitScroll && frame > okFrame && !dialogShowing && hubStable;

    internal void ScrollRequested() { if (Phase == NativeWelcomePhase.AwaitScroll) Phase = NativeWelcomePhase.Loading; }

    // False for a ready callback that is no longer awaited (timed out or failed): the caller leaves it alone.
    internal bool ScrollReady()
    {
        if (Phase != NativeWelcomePhase.Loading) return false;
        Phase = NativeWelcomePhase.Shown;
        return true;
    }

    // True: commit Seen. A fatal-error hide or a hide of a scroll that was never shown commits nothing.
    internal bool ScrollHidden(bool fatalError)
    {
        if (Phase != NativeWelcomePhase.Shown) return false;
        Phase = NativeWelcomePhase.Done;
        return !fatalError;
    }

    // Focused time while waiting for the scroll; true once it timed out: fall back to Seen after the popup alone.
    // Time before the request counts only while nothing else holds the HUB (another dialog may legitimately stay up for
    // a long time; #57 review): only an unanswered load times out.
    internal bool Tick(float focusedSeconds, bool dialogShowing = false)
    {
        if (Phase != NativeWelcomePhase.AwaitScroll && Phase != NativeWelcomePhase.Loading) return false;
        if (Phase == NativeWelcomePhase.AwaitScroll && dialogShowing) return false;
        waited += focusedSeconds > 0f ? focusedSeconds : 0f;
        if (waited <= ScrollTimeoutSeconds) return false;
        Phase = NativeWelcomePhase.Done;
        return true;
    }

    // The scroll could not be created or shown: true means fall back to Seen after the popup alone.
    internal bool ScrollFailed()
    {
        if (Phase != NativeWelcomePhase.AwaitScroll && Phase != NativeWelcomePhase.Loading && Phase != NativeWelcomePhase.Shown) return false;
        bool fallback = Phase != NativeWelcomePhase.Shown;
        Phase = NativeWelcomePhase.Done;
        return fallback;
    }

    // The shown scroll was destroyed without a hide (scene change): release the HUB, commit nothing.
    internal void ScrollLost() { if (ScrollActive) Phase = NativeWelcomePhase.Done; }

    // Anything that ends the attempt for this session without a scroll (popup refused, adapter failure).
    internal void Abandon() { Phase = NativeWelcomePhase.Done; }
}
