using System;
using System.Collections.Generic;
using System.Linq;

// One complete native relationship per offer: native sellable-deck lookup requires a unique deck.
internal static class NativeShopDecks
{
    internal const int FirstId = 1800000000, LastId = 1820000000;
    // An offered Recipe lives on its Deck's page; without an offered Deck it keeps its own tile.
    internal static bool PageVariant(LocalShopProduct product) => product?.Kind == "Recipe"
        && NativeShopPurchase.Products.Any(p => p.Kind == "Deck" && p.RecipeId == product.RecipeId);
    internal static int Id(LocalShopProduct product) => checked(FirstId + product.NativeId + (product.Kind == "Deck" ? 10000 : 0));
    internal sealed class Row
    {
        internal string Table;
        internal int Id;
        internal Dictionary<string, object> Values;
    }
    internal static List<Row> Records(LocalShopProduct product, DeckRecipe recipe, Func<string, int> cardId)
    {
        if ((product.Kind != "Recipe" && product.Kind != "Deck") || product.NativeId < 1 || product.NativeId > LocalShop.MaxDeckNativeId || product.RecipeId != recipe.Id)
            throw new InvalidOperationException("Invalid synthetic shop deck.");
        int id = Id(product), firstCard = checked(FirstId + (id - FirstId) * 1000);
        var cards = recipe.Cards.SelectMany(c => Enumerable.Repeat(c.Id, c.Count)).ToArray();
        if (cards.Length == 0 || cards.Length >= 500) throw new InvalidOperationException("Invalid synthetic deck size.");
        var rows = new List<Row>();
        Action<string, int, Dictionary<string, object>> add = (table, rowId, values) => {
            if (rowId < FirstId || rowId >= LastId) throw new InvalidOperationException("Synthetic DBF id outside reserved range.");
            rows.Add(new Row { Table = table, Id = rowId, Values = values });
        };
        int hero = cardId(recipe.Hero), heroClass = (int)LocalDecks.HeroClass(recipe.Hero);
        string description = product.Kind == "Recipe" ? "Recipe only — cards not included; build it in the deck builder"
            : "Recipe plus missing cards, Core included; saved as a ready-to-play deck";
        // ALT_DESCRIPTION names what is sold and the deck's label. The native page's header line is the selected
        // variant name, not this field, so Details leads with the deck name and label.
        add("Deck", id, new Dictionary<string, object> {
            ["NAME"] = DisplayName(product, recipe), ["DESCRIPTION"] = description, ["ALT_DESCRIPTION"] = Header(product, recipe),
            ["TOP_CARD_ID"] = firstCard, ["PRECON_CLASS"] = heroClass });
        for (int i = 0; i < cards.Length; i++)
            add("DeckCard", firstCard + i, new Dictionary<string, object> {
                ["CARD_ID"] = cardId(cards[i]), ["DECK_ID"] = id, ["NEXT_CARD"] = i + 1 == cards.Length ? 0 : firstCard + i + 1 });
        int sideboardIndex = 500;
        foreach (var board in recipe.Sideboards)
        {
            int owner = Array.IndexOf(cards, board.Owner);
            if (owner < 0) throw new InvalidOperationException("Synthetic sideboard owner missing.");
            foreach (var card in board.Cards.SelectMany(c => Enumerable.Repeat(c.Id, c.Count)))
            {
                if (sideboardIndex >= 1000) throw new InvalidOperationException("Synthetic sideboard too large.");
                add("SideboardCard", firstCard + sideboardIndex++, new Dictionary<string, object> {
                    ["DECK_CARD_ID"] = firstCard + owner, ["SIDEBOARD_CARD_ID"] = cardId(card) });
            }
        }
        add("DeckTemplate", id, new Dictionary<string, object> {
            ["CLASS_ID"] = heroClass, ["HERO_CARD_ID"] = hero, ["DECK_ID"] = id,
            ["SORT_ORDER"] = product.NativeId, ["DISPLAY_CARD_ID"] = hero, ["DISPLAY_TEXTURE"] = "",
            ["FORMAT_TYPE"] = 2, ["SOURCE_TYPE"] = 0, ["IS_FREE_REWARD"] = false, ["IS_STARTER_DECK"] = false });
        add("SellableDeck", id, new Dictionary<string, object> { ["DECK_TEMPLATE_ID"] = id, ["GOLDEN_NAME"] = DisplayName(product, recipe) });
        return rows;
    }
    // Live 2026-10-25: recipe and ready-made deck tiles both showed the deck name. The tile banner wraps at about
    // 16 characters, so the suffix takes its own short line; the ready-made deck keeps the deck name.
    internal static string DisplayName(LocalShopProduct product, DeckRecipe recipe) =>
        product.Kind == "Recipe" ? recipe.Name + " (Recipe)" : recipe.Name;
    // "Ready-made deck — Fan favourite, Medium, Midrange" is the longest (49 characters).
    internal static string Header(LocalShopProduct product, DeckRecipe recipe) =>
        (product.Kind == "Recipe" ? "Recipe" : "Ready-made deck") + " — " + RecipeLibrary.Label(recipe);
    // Shop order within a class (owner 2026-10-29): Meta before Fan favourite, then name.
    internal static IEnumerable<DeckRecipe> ShopOrder(IEnumerable<DeckRecipe> recipes) =>
        recipes.OrderBy(recipe => recipe.Tag == "meta" ? 0 : 1).ThenBy(recipe => recipe.Name, StringComparer.OrdinalIgnoreCase);

    // Owner 2026-10-25: the native SellableDeck card list cannot mark owned copies, so the deck page's text panel (the
    // product description) says it, from the same MissingCopies the deck quote uses. After the deck name and label, the owned summary comes first so
    // the small panel always shows it, then how the deck plays (recipe previews run 88-144 characters), then missing
    // names only as space allows within DetailsLimit.
    internal const int DetailsLimit = 300;
    internal static string Details(LocalShopProduct product, DeckRecipe recipe, LocalProfile profile, int price) =>
        Details(product, recipe, LocalShop.Default.Library.MissingCopies(recipe, RecipeLibrary.OwnedCount(profile.OwnedAnyVersionCollection)), price);
    // price: the ledger (pre-kit) price. kitGoldCap: the deck kit this purchase would offer (LocalProfile.DeckKitFor), 0
    // for none (#57): the page then quotes the after-kit amount, as the native price does, and names the full price.
    internal static string Details(LocalShopProduct product, DeckRecipe recipe, RecipeMissingCopies missing, int price, int kitGoldCap = 0)
    {
        string blocker = product.Kind == "Deck" ? LocalShop.DeckPurchaseBlocker(missing) : null;
        // The deck name and label still lead the panel; the refusal replaces the price summary.
        if (blocker != null) return recipe.Name + " — " + RecipeLibrary.Label(recipe) + "\n" + blocker;
        string kitLine = product.Kind == "Deck" ? KitLine(price, kitGoldCap) : "";
        if (kitLine.Length != 0)
        {
            // The long kit line goes in when the summary and preview leave room; otherwise the brief one; and when even
            // that is too long (the name and label take room too), the preview sentence goes.
            int due = Math.Max(0, price - kitGoldCap);
            string full = Details(product, recipe, missing, kitLine, due);
            if (full.Length <= DetailsLimit) return full;
            string brief = Details(product, recipe, missing, KitLine(price, kitGoldCap, true), due);
            return brief.Length <= DetailsLimit ? brief : Details(product, recipe, missing, KitLine(price, kitGoldCap, true), due, false);
        }
        string text = Details(product, recipe, missing, "", price);
        return text.Length <= DetailsLimit ? text : Details(product, recipe, missing, "", price, false);
    }
    private static string Details(LocalShopProduct product, DeckRecipe recipe, RecipeMissingCopies missing, string kitLine, int price, bool withPreview = true)
    {
        string preview = kitLine + (!withPreview || string.IsNullOrWhiteSpace(recipe.Preview) ? "" : "\n" + recipe.Preview.Trim());
        int owned = missing.Needed - missing.Total;
        // The native header shows the selected variant's name, so the panel identifies the deck and its label.
        string label = recipe.Name + " — " + RecipeLibrary.Label(recipe) + "\n";
        if (product.Kind == "Recipe")
            return label + "Recipe only — cards not included. You own " + owned + " of " + missing.Needed + " cards." + preview;
        if (missing.Total == 0)
            return label + "You own all " + missing.Needed + " cards." + (price > 0 ? " Nothing missing — " + price + " gold." : "") + preview;
        var groups = missing.Purchasable.Where(row => row.Value > 0).OrderBy(row => RarityOrder(row.Key))
            .Select(row => RarityName(row.Key) + " " + row.Value).ToList();
        // Decision 2026-10-25: a ready-made deck also sells Core copies not yet unlocked by class level.
        int core = missing.LockedCore.Sum(LocalShop.DeckGrant), locked = missing.LockedCore.Sum(card => card.LockedCore) - core;
        if (core > 0) groups.Add("Core " + core);
        if (locked > 0) groups.Add("locked Core " + locked);
        string text = label + "You own " + owned + " of " + missing.Needed + " cards. Missing: " + missing.Total
            + " (" + string.Join(", ", groups) + ") — " + price + " gold." + preview;
        var names = missing.Cards.OrderByDescending(card => RarityOrder(card.Rarity)).ThenBy(card => card.Id, StringComparer.Ordinal)
            .Select(card => SabberStoneCore.Model.Cards.FromId(card.Id).Name + (card.Missing > 1 ? " x" + card.Missing : "")).ToArray();
        string line = "";
        int shown = 0;
        for (; shown < names.Length; shown++)
        {
            string next = (shown == 0 ? "\nMissing cards: " : "; ") + names[shown]; // Names may contain commas.
            string more = shown + 1 < names.Length ? "; +" + (names.Length - shown - 1) + " more" : "";
            if (text.Length + line.Length + next.Length + more.Length > DetailsLimit) break;
            line += next;
        }
        return text + line + (shown > 0 && shown < names.Length ? "; +" + (names.Length - shown) + " more" : "");
    }
    // Owner 2026-10-28: the quoted price already counts the kit; the page says so and names the full price.
    internal static string KitLine(int price, int kitGoldCap, bool brief = false) => price <= 0 || kitGoldCap <= 0 ? ""
        : brief ? " Deck kit applied. Full price: " + Gold(price) + "."
        : " Deck kit applied (up to " + Gold(kitGoldCap) + " gold). Full price: " + Gold(price) + " gold.";
    private static string Gold(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    private static int RarityOrder(SabberStoneCore.Enums.Rarity rarity) => rarity == SabberStoneCore.Enums.Rarity.FREE ? -1
        : rarity == SabberStoneCore.Enums.Rarity.COMMON ? 0
        : rarity == SabberStoneCore.Enums.Rarity.RARE ? 1 : rarity == SabberStoneCore.Enums.Rarity.EPIC ? 2
        : rarity == SabberStoneCore.Enums.Rarity.LEGENDARY ? 3 : -2;
    private static string RarityName(SabberStoneCore.Enums.Rarity rarity) => rarity == SabberStoneCore.Enums.Rarity.FREE ? "Free"
        : rarity == SabberStoneCore.Enums.Rarity.COMMON ? "Common"
        : rarity == SabberStoneCore.Enums.Rarity.RARE ? "Rare" : rarity == SabberStoneCore.Enums.Rarity.EPIC ? "Epic"
        : rarity == SabberStoneCore.Enums.Rarity.LEGENDARY ? "Legendary" : "Other";

    // Validate every table before inserting anything. Existing reserved rows must be ours, never retail.
    internal static void Ensure(IEnumerable<Row> rows, Func<string, IEnumerable<int>> ids,
        ISet<string> inserted, Action<Row> add)
    {
        var planned = rows.ToArray();
        if (planned.GroupBy(r => r.Table + ":" + r.Id).Any(g => g.Count() != 1))
            throw new InvalidOperationException("Duplicate synthetic shop record.");
        var existing = planned.Select(r => r.Table).Distinct().ToDictionary(table => table, table => {
            var known = ids(table);
            return known as HashSet<int> ?? new HashSet<int>(known);
        });
        foreach (var table in existing)
            if (table.Value.Any(id => id >= FirstId && !inserted.Contains(table.Key + ":" + id)))
                throw new InvalidOperationException("Pinned DBF max overlaps local range: " + table.Key + " max=" + table.Value.Max());
        foreach (var row in planned)
        {
            string key = row.Table + ":" + row.Id;
            if (existing[row.Table].Contains(row.Id)) continue;
            add(row);
            inserted.Add(key);
        }
    }
    // One offer's failure (missing card, bad recipe) excludes only that offer; it is not retried every frame.
    internal static void EnsureEach(IEnumerable<LocalShopProduct> products, Func<LocalShopProduct, List<Row>> records,
        Func<string, IEnumerable<int>> ids, ISet<string> inserted, ISet<string> failed, Action<Row> add, Action<string> excluded)
    {
        foreach (var product in products.Where(p => !failed.Contains(p.Id)))
        {
            try { Ensure(records(product), ids, inserted, add); }
            catch (Exception error)
            {
                failed.Add(product.Id);
                excluded?.Invoke("excluded " + product.Id + ": synthetic deck records: " + (error.InnerException ?? error).Message);
            }
        }
    }
#if !NATIVE_SHOP_POLICY_TEST
    private static readonly HashSet<string> inserted = new HashSet<string>(), failed = new HashSet<string>();
    internal static bool Missing(LocalShopProduct product) =>
        (product.Kind == "Recipe" || product.Kind == "Deck") && GameDbf.SellableDeck.GetRecord(Id(product)) == null;
    // Idempotent; also restores rows after a GameDbf reload drops them.
    internal static void Inject(Action<string> excluded)
    {
        var pending = LocalShop.Default.Products.Where(p => !p.verify && !LocalShop.Default.Withheld(p) && Missing(p) && !failed.Contains(p.Id)).ToArray();
        if (pending.Length == 0) return;
        // Offers own disjoint reserved ids, so one id snapshot per table serves every offer.
        var snapshot = new Dictionary<string, HashSet<int>>();
        Func<string, IEnumerable<int>> ids = table => snapshot.TryGetValue(table, out var known) ? known
            : snapshot[table] = new HashSet<int>(RecordsIn(table).Select(r => r.ID));
        EnsureEach(pending, product => {
            var rows = Records(product, LocalShop.Default.Recipe(product.RecipeId), NativeCardId);
            foreach (var row in rows.Where(r => r.Table == "DeckTemplate"))
            {
                // Borrow a native portrait material; DisplayCardId still selects this recipe's hero.
                var art = GameDbf.DeckTemplate.GetRecords().Where(r => r.ID < FirstId && !string.IsNullOrEmpty(r.DisplayTexture))
                    .OrderByDescending(r => r.ClassId == (int)row.Values["CLASS_ID"]).FirstOrDefault()
                    ?? throw new InvalidOperationException("Native deck portrait material unavailable.");
                row.Values["DISPLAY_TEXTURE"] = art.DisplayTexture;
            }
            return rows;
        }, ids, inserted, failed, Add, excluded);
    }
    private static void Add(Row row)
    {
        switch (row.Table)
        {
            case "Deck": Add(GameDbf.Deck, row); break;
            case "DeckTemplate": Add(GameDbf.DeckTemplate, row); break;
            case "DeckCard": Add(GameDbf.DeckCard, row); break;
            case "SideboardCard": Add(GameDbf.SideboardCard, row); break;
            case "SellableDeck": Add(GameDbf.SellableDeck, row); break;
            default: throw new InvalidOperationException("Unknown shop DBF table.");
        }
    }
    private static void Add<T>(Dbf<T> table, Row row) where T : DbfRecord, new()
    {
        var record = table.CreateNewRecord();
        record.SetID(row.Id);
        foreach (var pair in row.Values)
        {
            object value = pair.Value;
            if (record.GetVarType(pair.Key) == typeof(DbfLocValue))
            {
                var text = new DbfLocValue(); text.SetString((string)value); value = text;
            }
            if (record.GetVarType(pair.Key) == null) throw new InvalidOperationException("Unknown shop DBF field: " + pair.Key);
            record.SetVar(pair.Key, value);
        }
        table.AddRecord(record); // Maintains the ID dictionary and GameDbfIndex's DeckCard listeners.
    }
    private static int NativeCardId(string card)
    {
        int id = GameUtils.TranslateCardIdToDbId(card);
        if (id <= 0 || GameDbf.Card.GetRecord(id) == null) throw new InvalidOperationException("Missing native recipe card: " + card);
        return id;
    }
    private static IEnumerable<DbfRecord> RecordsIn(string name)
    {
        switch (name)
        {
            case "Deck": return GameDbf.Deck.GetRecords();
            case "DeckTemplate": return GameDbf.DeckTemplate.GetRecords();
            case "DeckCard": return GameDbf.DeckCard.GetRecords();
            case "SideboardCard": return GameDbf.SideboardCard.GetRecords();
            case "SellableDeck": return GameDbf.SellableDeck.GetRecords();
            default: throw new InvalidOperationException("Unknown shop DBF table.");
        }
    }
#endif
}
