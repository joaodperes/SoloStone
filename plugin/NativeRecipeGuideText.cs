using System.Collections.Generic;
using System.Linq;

internal static class NativeRecipeGuideText
{
    // The guide a saved deck carries: its SourceRecipeId (set by a shop ready-made deck purchase or by building from an owned recipe, kept
    // through renames and card edits) resolved in the offered library. Null for starters, loaners, hand-built decks and recipes the library
    // no longer offers; nothing is matched by card list.
    internal static DeckRecipe ForSavedDeck(IEnumerable<DeckRecipe> recipes, LocalCustomDeck deck) =>
        deck?.SourceRecipeId == null ? null : recipes.FirstOrDefault(recipe => recipe.Id == deck.SourceRecipeId);

    // Two pages only (owner 2026-11-05): the deck's overall strategy, then what to keep in the opening hand.
    internal static readonly string[] Headings = { "STRATEGY", "MULLIGAN" };
    internal static string[] Sections(DeckRecipe recipe) => new[] { recipe.Guide.Strategy, recipe.Guide.Mulligan };

    // The cards a page shows as native card pictures: page 0 the key cards, page 1 the keep list (ids validated against the deck).
    internal static IReadOnlyList<string> Cards(DeckRecipe recipe, int page) => page == 0 ? recipe.Guide.StrategyCards : recipe.Guide.KeepCards;

    // The card page's description wraps here (the game's text does not): lines of at most WrapColumns characters. The layout under it was measured
    // for the header line plus MaxWrappedLines lines (tests/recipe-picker checks every page fits).
    internal const int WrapColumns = 56, MaxWrappedLines = 4;
    internal static List<string> Wrap(string text)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > WrapColumns) { lines.Add(line); line = word; }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    // One popup per page: header "<deck>: <heading>", body "(n/2)" then the section. The same strings the dialog shows and the live test reads.
    internal static string PageHeader(DeckRecipe recipe, int page) => recipe.Name + ": " + Headings[page];
    internal static string PageBody(DeckRecipe recipe, int page) => "(" + (page + 1) + "/" + Sections(recipe).Length + ")\n\n" + Sections(recipe)[page];
}
