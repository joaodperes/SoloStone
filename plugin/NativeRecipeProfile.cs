using System;
using System.Linq;

public sealed partial class LocalProfile
{
    // The two-argument shop API allocates; the native picker already owns a persisted empty draft.
    public LocalCustomDeck SaveDeckFromRecipe(string recipeId, LocalCustomDeck deck, long existingDraftId)
    {
        if (!data.OwnedRecipes.Contains(recipeId)) throw new InvalidOperationException("Recipe is not owned.");
        if (deck == null) throw new ArgumentNullException(nameof(deck));
        var prior = GetDraft(existingDraftId);
        if (prior == null || deck.Id != existingDraftId || prior.Hero != deck.Hero)
            throw new InvalidOperationException("Recipe target is not the current local draft.");
        var canonical = NativeRecipeTemplates.PrepareChoice(NativeRecipeLibrary.Current,
            NativeRecipeLibrary.Current.Recipes.Single(recipe => recipe.Id == recipeId), prior, data.OwnedRecipes, _ => 0);
        if (!SameRecipeContents(canonical, deck)) throw new InvalidOperationException("Recipe contents changed before save.");
        if (prior.SourceRecipeId == recipeId && SameRecipeContents(prior, deck)) return prior;
        if (prior.Cards.Count != 0 || prior.Sideboards.Count != 0 || prior.SourceRecipeId != null)
            throw new InvalidOperationException("Recipe can only fill an empty new draft.");
        var next = Snapshot();
        var saved = next.CustomDecks.Single(row => row.Id == existingDraftId);
        saved.Name = deck.Name; saved.CardEntries = deck.CardEntries.Select(row => new LocalDeckCard { Id = row.Id, Premium = row.Premium }).ToList();
        saved.Sideboards = deck.Sideboards.Select(Clone).ToList(); saved.SourceRecipeId = recipeId;
        Commit(next);
        return GetDraft(existingDraftId);
    }

    private static bool SameRecipeContents(LocalCustomDeck left, LocalCustomDeck right) =>
        left.Name == right.Name && left.Hero == right.Hero
        && left.Cards.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(right.Cards.OrderBy(id => id, StringComparer.Ordinal))
        && RecipeSideboards(left) == RecipeSideboards(right);
    private static string RecipeSideboards(LocalCustomDeck deck) => string.Join(";", deck.Sideboards
        .OrderBy(board => board.Source, StringComparer.Ordinal)
        .Select(board => board.Source + ":" + string.Join(",", board.Cards.OrderBy(id => id, StringComparer.Ordinal))));
}
