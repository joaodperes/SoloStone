using System;
using System.Collections.Generic;
using System.Linq;
using PegasusShared;

internal sealed class NativeRecipeTemplates
{
    private readonly Dictionary<string, CollectionManager.TemplateDeck> templates = new Dictionary<string, CollectionManager.TemplateDeck>(StringComparer.Ordinal);
    private readonly DeckRecipe[] recipes;

    internal NativeRecipeTemplates(IEnumerable<DeckRecipe> source)
    {
        recipes = source.OrderBy(recipe => recipe.Id, StringComparer.Ordinal).ToArray();
        foreach (var recipe in recipes) templates.Add(recipe.Id, Map(recipe));
        if (templates.Values.GroupBy(template => template.m_id).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Recipe template ID collision.");
    }

    internal List<CollectionManager.TemplateDeck> ForClass(IEnumerable<string> owned, FormatType format, TAG_CLASS heroClass)
    {
        if (format != FormatType.FT_WILD && format != FormatType.FT_STANDARD) return new List<CollectionManager.TemplateDeck>();
        var entitlements = new HashSet<string>(owned, StringComparer.Ordinal);
        return recipes.Where(recipe => entitlements.Contains(recipe.Id) && recipe.Class == heroClass.ToString())
            .Select(recipe => templates[recipe.Id]).ToList();
    }

    internal DeckRecipe Recipe(CollectionManager.TemplateDeck template) => template == null ? null
        : recipes.FirstOrDefault(recipe => ReferenceEquals(templates[recipe.Id], template));

    internal static bool[] ButtonVisibility(int recipes, int slots)
    {
        if (recipes < 0 || recipes > slots) throw new InvalidOperationException("Too many local recipes for the native picker.");
        return Enumerable.Range(0, slots).Select(index => index < recipes).ToArray();
    }

    internal static CollectionManager.TemplateDeck Map(DeckRecipe recipe)
    {
        // Negative IDs cannot resolve bundled DBF records; hash stays stable as library grows.
        uint hash = 2166136261;
        foreach (char character in recipe.Id) hash = unchecked((hash ^ character) * 16777619);
        int id = -1 - (int)(hash & 0x3fffffff);
        string[] cards = recipe.Cards.SelectMany(row => Enumerable.Repeat(row.Id, row.Count)).ToArray();
        RuneType[] runes = NativeDeckRunes.Order(recipe.Hero, cards);
        var result = new CollectionManager.TemplateDeck {
            m_id = id, m_deckTemplateId = id, m_class = (TAG_CLASS)Enum.Parse(typeof(TAG_CLASS), recipe.Class),
            m_title = recipe.Name, m_description = recipe.Preview, m_formatType = FormatType.FT_STANDARD,
            m_isStarterDeck = false, m_rune1 = runes[0], m_rune2 = runes[1], m_rune3 = runes[2]
        };
        foreach (var row in recipe.Cards) result.m_cardIds.Add(row.Id, row.Count);
        foreach (var board in recipe.Sideboards)
            result.m_sideboardCardIds.Add(board.Owner, board.Cards.SelectMany(row => Enumerable.Repeat(row.Id, row.Count)).ToList());
        return result;
    }

    internal static LocalCustomDeck PrepareChoice(RecipeLibrary library, DeckRecipe recipe, LocalCustomDeck draft,
        IEnumerable<string> owned, Func<string, int> ownedCount, Func<string, int, int> ownedVersion = null)
    {
        if (recipe == null || !library.Recipes.Contains(recipe) || !owned.Contains(recipe.Id, StringComparer.Ordinal))
            throw new InvalidOperationException("Recipe is not owned or no longer available.");
        if (draft == null || draft.Hero != recipe.Hero)
            throw new InvalidOperationException("Recipe does not match the current draft.");
        var built = library.BuildDeck(recipe, ownedCount).Deck;
        var result = new LocalCustomDeck {
            Id = draft.Id, SortOrder = draft.SortOrder, Name = built.Name, Hero = built.Hero,
            Cards = built.CardIds, SourceRecipeId = recipe.Id,
            Sideboards = built.Sideboards.Select(board => new LocalSideboard { Source = board.Key, Cards = board.Value.ToList() }).ToList()
        };
        LocalCrafting.AssignOwnedPremiums(result, ownedVersion ?? ((id, premium) => premium == 0 ? ownedCount(id) : 0), true);
        return result;
    }
}

internal static class NativeRecipeSafety
{
    internal static T OrDefault<T>(Func<T> action, T fallback, Action<Exception> log)
    {
        try { return action(); }
        catch (Exception error) { log(error); return fallback; }
    }

    internal static void ResetPreview(CollectionDeck deck) => deck.ClearSideboards();

    internal static void AssertContents(LocalCustomDeck saved, string name, string hero,
        IEnumerable<string> cards, IEnumerable<LocalSideboard> sideboards)
    {
        var actualCards = cards.ToArray();
        var actualBoards = sideboards.ToArray();
        if (saved.Name != name || saved.Hero != hero
            || !saved.Cards.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(actualCards.OrderBy(id => id, StringComparer.Ordinal))
            || Signature(saved.Sideboards) != Signature(actualBoards))
            throw new InvalidOperationException("Native recipe projection differs from committed contents: name=" + name
                + " hero=" + hero + " missing=" + string.Join(",", saved.Cards.Except(actualCards))
                + " extra=" + string.Join(",", actualCards.Except(saved.Cards))
                + " counts=" + actualCards.Length + "/" + saved.Cards.Count
                + " sideboards=" + Signature(actualBoards) + " expected=" + Signature(saved.Sideboards));
    }

    private static string Signature(IEnumerable<LocalSideboard> boards) => string.Join(";", boards
        .OrderBy(board => board.Source, StringComparer.Ordinal)
        .Select(board => board.Source + ":" + string.Join(",", board.Cards.OrderBy(id => id, StringComparer.Ordinal))));
}
