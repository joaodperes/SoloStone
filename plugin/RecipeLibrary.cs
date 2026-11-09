using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using MiniJSON;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public sealed class DeckRecipe
{
    public string Id, Name, Class, Hero, Archetype, Role, Difficulty, Preview;
    // Shop label (owner 2026-10-29): Tag "meta" (researched core list) or "fan" (fan favourite) and the play style.
    // Research names the data/research/meta-decks archetype this list comes from (null for lists kept from earlier research).
    public string Tag, Style, Research;
    public RecipeGuide Guide;
    public RecipeSource Source;
    public List<RecipeCard> Cards = new List<RecipeCard>();
    public List<RecipeSideboard> Sideboards = new List<RecipeSideboard>();
    public RecipePending Pending; // Null unless the published list needs work from another branch.
}

// A published list that needs a card or rule another branch adds (data/shop/recipes-v1.json "pending"). The list ships
// as published once the engine and pool accept it; until then it ships as Fallback, or not at all without one. Its
// errors must all be explained by Needs: anything else fails the library, so a pending list never hides a real defect.
public sealed class RecipePending
{
    public const string AzalinaRule = "rule:azalina-20-card", Azalina = "JAIL_430";
    public List<string> Needs = new List<string>(); // Card ids, or AzalinaRule (20-card Azalina decks).
    public string Branch;
    public RecipeFallback Fallback;
    public bool Waiting; // Set at load: true while the published list is not yet valid.
    // Card needs explain the pool/engine errors naming that card; AzalinaRule explains only deck-size errors (Validate's
    // own check, LocalDeck and LocalDecks.ValidateNativeSave wordings) of an exactly 20-card list holding Azalina.
    internal bool Explains(string error, DeckRecipe recipe) => Needs.Any(need => need == AzalinaRule
        ? recipe.Cards.Any(row => row.Id == Azalina) && recipe.Cards.Sum(row => row.Count) == 20
            && (error.EndsWith(": Invalid deck size.", StringComparison.Ordinal) || error.Contains("Local deck must contain")
                || error.Contains("-card limit."))
        : error.EndsWith(": " + need, StringComparison.Ordinal));
}

public sealed class RecipeFallback
{
    public string Changes;
    public List<RecipeCard> Cards = new List<RecipeCard>();
    public List<RecipeSideboard> Sideboards = new List<RecipeSideboard>();
}

// The two guide pages (Strategy, Mulligan): plain short text for a new player, limits in words (RecipeLibrary.Max*Words), and the
// cards each page shows as native card pictures (StrategyCards: key cards; KeepCards: the mulligan keep list). Ids must be in the deck.
public sealed class RecipeGuide
{
    public string Strategy, Mulligan;
    public List<string> StrategyCards = new List<string>(), KeepCards = new List<string>();
}
public sealed class RecipeSource { public string Site, Url, Accessed, Patch, DeckCode, Changes; }
public sealed class RecipeCard { public string Id; public int Count; }
public sealed class RecipeSideboard { public string Owner; public List<RecipeCard> Cards = new List<RecipeCard>(); }

public sealed class RecipeCardCopies
{
    public string Id { get; internal set; }
    public Rarity Rarity { get; internal set; }
    public int Needed { get; internal set; }
    public int Owned { get; internal set; }
    public int Missing => Math.Max(0, Needed - Owned);
    public int Purchasable => LocalCardCatalog.IsCore(Id) ? 0 : Missing;
    public int LockedCore => LocalCardCatalog.IsCore(Id) ? Missing : 0;
    // Empty hero means a combined class level; null means no known progression entry.
    public string UnlockHero => LocalCoreProgression.ById.TryGetValue(Id, out var unlock) ? unlock.Hero : null;
    public int? UnlockLevel => LocalCoreProgression.ById.TryGetValue(Id, out var unlock) ? (int?)unlock.Threshold : null;
}

public sealed class RecipeDeckBuild
{
    public LocalDeck Deck { get; internal set; }
    public IReadOnlyList<string> CardIds => Deck.CardIds.AsReadOnly();
    public IReadOnlyDictionary<string, string[]> Sideboards => Deck.Sideboards;
    public IReadOnlyList<RecipeCardCopies> Copies { get; internal set; }
    public bool Incomplete => Copies.Any(card => card.Missing != 0);
}

public sealed class RecipeMissingCopies
{
    public int Needed { get; internal set; } // All required copies, main deck plus owned-only sideboard selections.
    public int Total { get; internal set; }
    public IReadOnlyList<RecipeCardCopies> Cards { get; internal set; } // Copies with Missing > 0.
    public IReadOnlyDictionary<Rarity, int> Purchasable { get; internal set; }
    public IReadOnlyList<RecipeCardCopies> LockedCore { get; internal set; }
}

/// <summary>Headless recipe data and deck construction. No profile writes or copy reservations.</summary>
public sealed class RecipeLibrary
{
    public int Schema { get; private set; }
    public int LibraryVersion { get; private set; }
    public string ClientBuild { get; private set; }
    public IReadOnlyList<DeckRecipe> Recipes { get; private set; }
    // Pending lists without a fallback whose dependency has not landed: listed in the catalogue, never offered.
    public IReadOnlyList<DeckRecipe> Withheld { get; private set; } = Array.AsReadOnly(new DeckRecipe[0]);
    internal bool IsWithheld(string id) => id != null && Withheld.Any(recipe => recipe.Id == id);
    private HashSet<string> poolIds;
    private Dictionary<string, DeckRecipe> recipesById;
    internal bool HasRecipe(string id) => id != null && recipesById.ContainsKey(id);
    internal DeckRecipe FindRecipe(string id) => id != null && recipesById.TryGetValue(id, out var recipe) ? recipe : null;

    public static RecipeLibrary Load(string path) => LoadText(File.ReadAllText(path));

    public static RecipeLibrary LoadText(string text)
    {
        var root = Object(EmbeddedJson.Parse(text));
        var library = new RecipeLibrary {
            Schema = Number(root, "schema"), LibraryVersion = Number(root, "libraryVersion"), ClientBuild = Text(root, "clientBuild"),
            Recipes = Array.AsReadOnly(List(root, "recipes").Select(ParseRecipe).ToArray())
        };
        library.poolIds = PoolIds(library.ClientBuild);
        var errors = library.ResolvePending();
        errors.AddRange(library.Validate());
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        library.recipesById = library.Recipes.ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
        return library;
    }

    private static HashSet<string> PoolIds(string clientBuild)
    {
#if ENGINE_SOURCE
        var installed = OfflinePoolPolicy.Load();
        if (clientBuild != installed.ClientBuild)
            throw new InvalidDataException("Recipe pool policy/build mismatch.");
        return installedPoolIds.Value;
#else
        // The pinned legacy engine cannot validate the modern pool; never silently accept a recipe.
        throw new InvalidOperationException("Recipe validation requires the source engine (ENGINE_SOURCE).");
#endif
    }
#if ENGINE_SOURCE
    // Installed policy and active meta are immutable for this process; each library still checks ClientBuild.
    private static readonly Lazy<HashSet<string>> installedPoolIds = new Lazy<HashSet<string>>(() => {
        var ids = new HashSet<string>(OfflinePoolPolicy.Load().Members.Where(row => row.Scope == "Standard").Select(row => row.CardId), StringComparer.Ordinal);
        ids.UnionWith(SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds);
        ids.UnionWith(LocalDecks.LegalDeckbuildingCards.Select(card => card.Id));
        // Fabled companions are submitted deck cards, even though not collectible pool members.
        ids.UnionWith(ids.ToArray().SelectMany(SabberStoneCore.Actions.FabledRules.Bundle));
        ids.RemoveWhere(id => !LocalCardCatalog.InActiveMeta(Cards.FromId(id)));
        return ids;
    });
#endif

    // Pending lists: the published list when valid, else the fallback, else withheld. Errors not explained by Needs are returned.
    private List<string> ResolvePending()
    {
        var errors = new List<string>();
        var active = new List<DeckRecipe>();
        var withheld = new List<DeckRecipe>();
        foreach (var recipe in Recipes)
        {
            var pending = recipe?.Pending;
            if (pending == null) { active.Add(recipe); continue; }
            if (pending.Needs.Count == 0 || pending.Needs.Any(need => string.IsNullOrWhiteSpace(need))
                || pending.Needs.Distinct(StringComparer.Ordinal).Count() != pending.Needs.Count || string.IsNullOrWhiteSpace(pending.Branch))
            { errors.Add(recipe.Id + ": Invalid pending dependency."); continue; }
            var published = Validate(recipe);
            pending.Waiting = published.Count != 0;
            var unexplained = published.Where(error => !pending.Explains(error, recipe)).ToArray();
            if (unexplained.Length != 0) { errors.AddRange(unexplained.Select(error => error + " (pending list, not explained by its needs)")); continue; }
            if (!pending.Waiting) active.Add(recipe);
            else if (pending.Fallback == null) withheld.Add(recipe);
            else
            {
                recipe.Cards = pending.Fallback.Cards; recipe.Sideboards = pending.Fallback.Sideboards;
                recipe.Source.Changes = pending.Fallback.Changes;
                active.Add(recipe); // Validate() checks the fallback strictly.
            }
        }
        Recipes = Array.AsReadOnly(active.ToArray());
        Withheld = Array.AsReadOnly(withheld.ToArray());
        return errors;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Schema != 1) errors.Add("Unsupported recipe schema.");
        if (LibraryVersion < 1) errors.Add("Invalid libraryVersion.");
        if (Recipes.Count == 0) errors.Add("Recipe library is empty.");
        foreach (var group in Recipes.Concat(Withheld).GroupBy(recipe => recipe?.Id, StringComparer.Ordinal))
            if (group.Count() > 1) errors.Add("Duplicate recipe id: " + group.Key);
        foreach (var recipe in Recipes) errors.AddRange(Validate(recipe));
        return errors.AsReadOnly();
    }

    public IReadOnlyList<string> Validate(DeckRecipe recipe)
    {
        var errors = new List<string>();
        if (recipe == null) { errors.Add("Missing recipe."); return errors.AsReadOnly(); }
        Action<string> error = message => errors.Add((recipe.Id ?? "<recipe>") + ": " + message);
        Action<string, string, int> text = (value, field, limit) => {
            if (string.IsNullOrWhiteSpace(value) || value.Length > limit) error("Invalid " + field + ".");
        };
        foreach (var field in new[] { recipe.Id, recipe.Name, recipe.Class, recipe.Hero, recipe.Archetype })
            text(field, "recipe text", int.MaxValue);
        // Empty draft reaches LocalDecks.ValidDeckName without applying collection availability to recipe cards.
        try { LocalDecks.Draft(recipe.Name, "HERO_08", Array.Empty<string>()); }
        catch (ArgumentException ex) { error(ex.Message); }
        if (recipe.Role != "strong" && recipe.Role != "fun") error("Invalid role.");
        if (!new[] { "easy", "medium", "hard" }.Contains(recipe.Difficulty)) error("Invalid difficulty.");
        if (recipe.Tag != "meta" && recipe.Tag != "fan") error("Invalid tag.");
        if (!Styles.Contains(recipe.Style)) error("Invalid style.");
        if (recipe.Research != null && string.IsNullOrWhiteSpace(recipe.Research)) error("Invalid research archetype.");
        text(recipe.Preview, "preview", 200);
        text(recipe.Guide?.Strategy, "guide strategy", 400);
        text(recipe.Guide?.Mulligan, "guide mulligan", 400);
        foreach (var (field, shown) in new[] { ("strategyCards", recipe.Guide?.StrategyCards), ("keepCards", recipe.Guide?.KeepCards) })
        {
            if (shown == null || shown.Count < 1 || shown.Count > MaxGuideCards || shown.Distinct(StringComparer.Ordinal).Count() != shown.Count) error("Invalid guide " + field + ".");
            else
            {
                var held = new HashSet<string>((recipe.Cards ?? new List<RecipeCard>()).Concat((recipe.Sideboards ?? new List<RecipeSideboard>())
                    .Where(board => board?.Cards != null).SelectMany(board => board.Cards)).Where(row => row != null).Select(row => row.Id), StringComparer.Ordinal);
                foreach (string cardId in shown) if (cardId == null || !held.Contains(cardId)) error("Guide " + field + " card not in the deck: " + cardId);
            }
        }
        if (Words(recipe.Guide?.Strategy) > MaxStrategyWords) error("Guide strategy over " + MaxStrategyWords + " words.");
        if (Words(recipe.Guide?.Mulligan) > MaxMulliganWords) error("Guide mulligan over " + MaxMulliganWords + " words.");
        var source = recipe.Source;
        foreach (var field in new[] { source?.Site, source?.Url, source?.Accessed, source?.Patch, source?.DeckCode })
            text(field, "source", int.MaxValue);
        if (source?.Changes == null) error("Missing source changes.");
        if (!DateTime.TryParseExact(source?.Accessed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) error("Invalid source accessed date.");
        if (!Uri.TryCreate(source?.Url, UriKind.Absolute, out _)) error("Invalid source URL.");
        if (!LocalCoreProgression.Heroes.Contains(recipe.Hero)) error("Unsupported hero.");
        else if (LocalDecks.HeroClass(recipe.Hero).ToString() != recipe.Class) error("Class and hero disagree.");
        var rows = recipe.Cards;
        var boards = recipe.Sideboards;
        if (rows == null || boards == null || rows.Any(row => row == null || string.IsNullOrWhiteSpace(row.Id) || row.Count < 1 || row.Count > 40)
            || boards.Any(board => board == null || string.IsNullOrWhiteSpace(board.Owner) || board.Cards == null
                || board.Cards.Any(row => row == null || string.IsNullOrWhiteSpace(row.Id) || row.Count < 1 || row.Count > 40)))
        { error("Invalid card/sideboard records."); return errors.AsReadOnly(); }
        if (rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count
            || boards.Select(board => board.Owner).Distinct(StringComparer.Ordinal).Count() != boards.Count
            || boards.Any(board => board.Cards.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != board.Cards.Count))
            error("Duplicate card/sideboard records.");
        bool known = true;
        foreach (var row in rows.Concat(boards.SelectMany(board => board.Cards)))
        {
            var card = FindCard(row.Id);
            if (card == null || !card.Implemented || !poolIds.Contains(row.Id))
            { error("Unregistered card or outside offline Standard pool: " + row.Id); known = false; }
        }
        // Retail decklists (and their deck codes) hold collectible cards plus Fabled companions, which are non-collectible
        // IS_FABLED_BUNDLE_CARD slots; any other token is never a main-deck card. Sideboards may hold non-collectible modules.
        foreach (var row in rows)
        {
            var card = FindCard(row.Id);
            if (card != null && !card.Collectible
#if ENGINE_SOURCE
                && !SabberStoneCore.Actions.FabledRules.IsCompanion(row.Id)
#endif
                )
            { error("Not a collectible deck card: " + row.Id); known = false; }
        }
        // Class, rune and copy checks also run over the registered cards when another card is unavailable, so a pending
        // list's declared need (RecipePending) never hides an unrelated defect.
        var registered = Expand(rows).Where(id => FindCard(id) != null).ToArray();
        if (!LocalCardCatalog.WithinCopyLimits(registered)) error("Deck exceeds playable copy limit.");
        if (LocalCoreProgression.Heroes.Contains(recipe.Hero))
        {
            var heroClass = LocalDecks.HeroClass(recipe.Hero);
            var cards = registered.Select(Cards.FromId).ToArray();
            if (cards.Any(card => !LocalDecks.IsAllowedInDeck(card, heroClass))) error("Card does not belong to selected class.");
            try { LocalDecks.ValidateRunes(cards, heroClass); } catch (InvalidOperationException ex) { error(ex.Message); }
        }
        if (!known) return errors.AsReadOnly();
        var ids = Expand(rows).ToArray();
        if (ids.Length != LocalDecks.DeckSize(ids)) error("Invalid deck size.");
        try { LocalDecks.ValidateFabled(ids); } catch (InvalidOperationException ex) { error(ex.Message); }
        try { MakeDeck(recipe); }
        catch (InvalidOperationException ex) { error(ex.Message); }
        catch (ArgumentException ex) { error(ex.Message); }
        catch (KeyNotFoundException ex) { error(ex.Message); }
        return errors.AsReadOnly();
    }

    // Owner 2026-11-05: the guide was far too long. Two short pages, counted in whitespace-separated words.
    public const int MaxStrategyWords = 30, MaxMulliganWords = 25, MaxGuideCards = 5;
    private static int Words(string text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
    internal static readonly string[] Styles = { "aggro", "tempo", "midrange", "control", "combo" };
    // Shop label, e.g. "Meta, Hard, Combo" or "Fan favourite, Medium, Midrange".
    public static string Label(DeckRecipe recipe) => (recipe.Tag == "meta" ? "Meta" : "Fan favourite") + ", " + Capital(recipe.Difficulty) + ", " + Capital(recipe.Style);
    private static string Capital(string text) => string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);

    public RecipeDeckBuild BuildDeck(DeckRecipe recipe, Func<string, int> ownedCount)
    {
        if (ownedCount == null) throw new ArgumentNullException(nameof(ownedCount));
        var errors = Validate(recipe);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
        var needed = recipe.Cards.Concat(recipe.Sideboards.SelectMany(board => board.Cards))
            .GroupBy(row => row.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        // Every sideboard selection (Beatrix and Underbelly contraband) needs owned copies on top of the main deck's, as
        // LocalProfile.IsDeckComplete checks. Fabled companions are never sold or granted: owning the primary owns them.
        var exempt = new HashSet<string>(needed.Keys.Where(LocalDecks.IsFabledCompanion), StringComparer.Ordinal);
        var copies = needed.Select(row => new RecipeCardCopies {
            Id = row.Key, Rarity = Cards.FromId(row.Key).Rarity, Needed = row.Value,
            Owned = Math.Max(exempt.Contains(row.Key) ? row.Value : 0, ownedCount(row.Key))
        }).ToArray();
        return new RecipeDeckBuild { Deck = MakeDeck(recipe), Copies = Array.AsReadOnly(copies) };
    }

    public RecipeMissingCopies MissingCopies(DeckRecipe recipe, Func<string, int> ownedCount)
    {
        var copies = BuildDeck(recipe, ownedCount).Copies;
        var counts = Enum.GetValues(typeof(Rarity)).Cast<Rarity>().ToDictionary(rarity => rarity, rarity => 0);
        foreach (var card in copies) counts[card.Rarity] += card.Purchasable;
        return new RecipeMissingCopies { Needed = copies.Sum(card => card.Needed), Total = copies.Sum(card => card.Missing),
            Cards = Array.AsReadOnly(copies.Where(card => card.Missing > 0).ToArray()),
            Purchasable = new ReadOnlyDictionary<Rarity, int>(counts),
            LockedCore = Array.AsReadOnly(copies.Where(card => card.LockedCore > 0).ToArray()) };
    }

    /// <summary>Pass profile.Collection (includes Core grants); never reserves other decks.</summary>
    public static Func<string, int> OwnedCount(IReadOnlyDictionary<string, int> collection)
    {
        if (collection == null) throw new ArgumentNullException(nameof(collection));
        return id => {
            var card = FindCard(id) ?? throw new ArgumentException("Unknown collection card: " + id);
            return card.Rarity == Rarity.FREE ? LocalEconomy.PlayableCap(card)
                : collection.TryGetValue(id, out int count) ? Math.Max(0, count) : 0;
        };
    }

    private static LocalDeck MakeDeck(DeckRecipe recipe) => new LocalDeck(recipe.Name, recipe.Hero, Expand(recipe.Cards).ToArray())
        .WithSideboards(recipe.Sideboards.Select(board => new LocalSideboard { Source = board.Owner, Cards = Expand(board.Cards).ToList() }), true);
    private static IEnumerable<string> Expand(IEnumerable<RecipeCard> rows) => rows.SelectMany(row => Enumerable.Repeat(row.Id, row.Count));
    private static EngineCard FindCard(string id)
    {
        try { return Cards.FromId(id); } catch (KeyNotFoundException) { return null; }
    }
    private static JsonNode Object(object value) => value as JsonNode ?? throw new InvalidDataException("Expected JSON object.");
    private static string Text(JsonNode row, string key) => row.TryGetValue(key, out object value) && value is string text ? text : null;
    private static int Number(JsonNode row, string key)
    {
        if (!row.TryGetValue(key, out object value) || !(value is long || value is int)
            || Convert.ToInt64(value) < int.MinValue || Convert.ToInt64(value) > int.MaxValue)
            throw new InvalidDataException("Expected integer: " + key);
        return Convert.ToInt32(value);
    }
    private static JsonList List(JsonNode row, string key) => row.TryGetValue(key, out object value) && value is JsonList list
        ? list : throw new InvalidDataException("Expected array: " + key);
    private static List<string> GuideIds(JsonNode guide, string key) =>
        guide.TryGetValue(key, out object value) && value is JsonList list ? list.Select(item => item as string).ToList() : new List<string>();
    private static List<RecipeCard> ParseCards(JsonNode row) => List(row, "cards").Select(value => {
        var card = Object(value); return new RecipeCard { Id = Text(card, "id"), Count = Number(card, "count") };
    }).ToList();
    private static DeckRecipe ParseRecipe(object value)
    {
        var row = Object(value);
        var guide = row.TryGetValue("guide", out object guideValue) && guideValue is JsonNode g ? g : new JsonNode();
        var source = row.TryGetValue("source", out object sourceValue) && sourceValue is JsonNode s ? s : new JsonNode();
        return new DeckRecipe {
            Id = Text(row, "id"), Name = Text(row, "name"), Class = Text(row, "class"), Hero = Text(row, "hero"), Archetype = Text(row, "archetype"),
            Role = Text(row, "role"), Difficulty = Text(row, "difficulty"), Preview = Text(row, "preview"),
            Tag = Text(row, "tag"), Style = Text(row, "style"), Research = Text(row, "research"), Pending = ParsePending(row),
            Guide = new RecipeGuide { Strategy = Text(guide, "strategy"), Mulligan = Text(guide, "mulligan"),
                StrategyCards = GuideIds(guide, "strategyCards"), KeepCards = GuideIds(guide, "keepCards") },
            Source = new RecipeSource { Site = Text(source, "site"), Url = Text(source, "url"), Accessed = Text(source, "accessed"),
                Patch = Text(source, "patch"), DeckCode = Text(source, "deckCode"), Changes = Text(source, "changes") },
            Cards = ParseCards(row), Sideboards = ParseSideboards(row)
        };
    }
    private static List<RecipeSideboard> ParseSideboards(JsonNode row) => row.ContainsKey("sideboards") ? List(row, "sideboards").Select(boardValue => {
        var board = Object(boardValue); return new RecipeSideboard { Owner = Text(board, "owner"), Cards = ParseCards(board) };
    }).ToList() : new List<RecipeSideboard>();
    private static RecipePending ParsePending(JsonNode row)
    {
        if (!row.TryGetValue("pending", out object value)) return null;
        var pending = Object(value);
        var fallback = pending.TryGetValue("fallback", out object fallbackValue) ? Object(fallbackValue) : null;
        return new RecipePending {
            Needs = List(pending, "needs").Select(need => need as string).ToList(), Branch = Text(pending, "branch"),
            Fallback = fallback == null ? null : new RecipeFallback { Changes = Text(fallback, "changes"),
                Cards = ParseCards(fallback), Sideboards = ParseSideboards(fallback) }
        };
    }
}

// MiniJSON supplies the game-compatible object model but accepts malformed JSON syntax.
internal static class EmbeddedJson
{
    internal static object Read(Stream stream) {
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 1024, true))
            return Parse(reader.ReadToEnd());
    }
    internal static object Parse(string text) {
        new Syntax(text).Validate();
        return Json.Deserialize(text);
    }
    internal static JsonNode Object(object value) => value as JsonNode ?? throw new InvalidDataException("Expected JSON object.");
    internal static JsonList Array(object value) => value as JsonList ?? throw new InvalidDataException("Expected JSON array.");
    internal static string Text(JsonNode row, string key) {
        if (!row.TryGetValue(key, out object value) || value == null) return null;
        return value as string ?? throw new InvalidDataException("Expected string: " + key);
    }
    internal static int? Integer(JsonNode row, string key, bool nullable = false) {
        if (!row.TryGetValue(key, out object value) || (nullable && value == null)) return null;
        if (!(value is long number) || number < int.MinValue || number > int.MaxValue)
            throw new InvalidDataException("Expected integer: " + key);
        return (int)number;
    }
    internal static bool Boolean(JsonNode row, string key) {
        if (!row.TryGetValue(key, out object value)) return false;
        return value is bool flag ? flag : throw new InvalidDataException("Expected boolean: " + key);
    }

    private sealed class Syntax
    {
        private static readonly System.Text.RegularExpressions.Regex Token = new System.Text.RegularExpressions.Regex(
            @"\G[ \t\r\n]*(?:""(?:[^""\\\x00-\x1f]|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|true|false|null|[{}\[\]:,])",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private readonly string text;
        private int offset;
        internal Syntax(string text) { this.text = text ?? throw new InvalidDataException("Missing JSON."); }
        internal void Validate() {
            Value(0);
            while (offset < text.Length && " \t\r\n".IndexOf(text[offset]) >= 0) offset++;
            if (offset != text.Length) throw Invalid();
        }
        private string Next() {
            var match = Token.Match(text, offset);
            if (!match.Success) throw Invalid();
            offset += match.Length;
            return match.Value.TrimStart(' ', '\t', '\r', '\n');
        }
        private InvalidDataException Invalid() => new InvalidDataException("Malformed JSON at offset " + offset + ".");
        private void Value(int depth, string token = null) {
            if (depth > 64) throw Invalid();
            token = token ?? Next();
            if (token == "{" || token == "[") {
                bool obj = token == "{";
                string end = obj ? "}" : "]";
                var keys = new HashSet<string>(StringComparer.Ordinal);
                token = Next();
                if (token == end) return;
                while (true) {
                    if (obj) {
                        if (token[0] != '"' || !keys.Add((string)Json.Deserialize(token)) || Next() != ":") throw Invalid();
                        Value(depth + 1);
                    }
                    else Value(depth + 1, token);
                    token = Next();
                    if (token == end) return;
                    if (token != ",") throw Invalid();
                    token = Next();
                }
            }
            else if (token[0] != '"' && token != "true" && token != "false" && token != "null"
                && token[0] != '-' && (token[0] < '0' || token[0] > '9')) throw Invalid();
        }
    }
}
