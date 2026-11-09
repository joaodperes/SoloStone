// #57 hidden starter decks (owner 2026-10-28): copy and test-control ids. Pure text, so test-control checks it on the Mac.
internal static class NativeStarterText
{
    // Native tray delete of a Core starter loaner hides it for this save; the native confirm says so.
    internal const string HideHeader = "Hide Starter Deck";
    internal const string HideBody = "Hide this starter deck? Its cards and your class progress stay. You can restore starter decks in Options.";
    // Native Options button (a rare fallback), shown only while this save hides at least one starter.
    internal const string OptionsObject = "SoloStoneRestoreStarters", OptionsLabel = "Restore Starter Decks", OptionsId = "options.restore-starters";
    internal const string RestorePopupId = "LOCAL_SOLOSTONE_RESTORE_STARTERS";
    internal const string RestoreHeader = "Restore Starter Decks"; // Title case like the hide confirm: the native header plate squeezes capitals.
    internal const string RestoreBody = "Show every hidden starter deck again? They return to your deck list the next time SoloStone starts.";
    internal const string RestoreLabel = "RESTORE", CancelLabel = "CANCEL";
    internal const string ConfirmId = "starters.restore", CancelId = "starters.cancel";
    internal static string DeleteButtonId(long deckId) => "deck:" + deckId + ".delete";

    internal static string DialogButtonId(string popupId, string button) =>
        popupId != RestorePopupId ? null : button == "confirm" ? ConfirmId : button == "cancel" ? CancelId : null;
}
