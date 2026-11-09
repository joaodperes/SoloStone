// Deck guide dialog and deck-picker button: ids and labels. Pure strings (no Unity, no profile), so test-control checks them on the Mac.
// The two-page guide popup (Strategy, Mulligan) is shared by the deck builder's "Recipe guide" and the deck picker's "Deck Guide" (NativeRecipeGuide).
internal static class NativeDeckGuideText
{
    internal const string PopupPrefix = "LOCAL_RECIPE_GUIDE_";
    internal const string PickerId = "picker.guide", NextId = "guide.next", CloseId = "guide.close";
    // Text, not a question mark: the picker already shows a native "?" (format help) beside My Collection.
    internal const string PickerLabel = "Guide";

    internal static string PopupId(string recipeId, int page) => PopupPrefix + recipeId + "_" + page;

    // Page 1 shows NEXT (confirm) and CLOSE (cancel); the last page shows CLOSE alone (also Escape). Other popups keep their ids (null here).
    internal static string DialogButtonId(string popupId, string button) =>
        popupId == null || !popupId.StartsWith(PopupPrefix, System.StringComparison.Ordinal) ? null
        : button == "confirm" ? NextId : button == "cancel" ? CloseId : null;

    internal static bool IsDialogButtonId(string id) => id == NextId || id == CloseId;
}
