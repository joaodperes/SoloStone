#if !SOLOSTONE_RELEASE
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MiniJSON;

// Read observations only: never refresh caches or invoke profile/native selection writes.
internal static class NativeCosmeticsState
{
    private static JsonList Values<T>(IEnumerable<T> rows) => TestControlProtocol.List(rows.Cast<object>());

    private static JsonNode Card(CollectionDeckSlot slot, CollectionManager manager, CollectionDeck deck)
    {
        var card = new JsonNode {
            { "cardId", slot.CardID }, { "count", slot.GetCount(TAG_PREMIUM.NORMAL) },
            { "ownedCount", manager.GetOwnedCount(slot.CardID, TAG_PREMIUM.NORMAL) }
        };
        // Retail Fabled companions are non-collectible bundle slots that never reach the collection (ownedCount 0);
        // CollectionDeck.AddFabledBundledCard gives them the primary slot's Owned flag instead.
        var definition = slot.GetEntityDef();
        if (definition != null && definition.HasTag(GAME_TAG.IS_FABLED_BUNDLE_CARD))
        {
            card["bundlePrimaryId"] = deck.FindPrimaryFabledCardSlot(definition)?.CardID;
            card["slotOwned"] = slot.Owned;
        }
        return card;
    }

    // Live 2026-10-30 (Companion Hunter "invalid cards in your sideboard"): which native check refuses each sideboard slot.
    internal static JsonNode SideboardValidation(SideboardDeck side)
    {
        var node = new JsonNode();
        try
        {
            var owner = DefLoader.Get()?.GetEntityDef(side.OwnerCardDbId);
            int subset = owner?.GetTag(GAME_TAG.ALLOWED_SIDEBOARD_SUBSET) ?? 0;
            var members = subset == 0 ? null : GameDbf.GetIndex().GetSubsetById(subset);
            node["subsetId"] = subset;
            node["subsetSize"] = members?.Count ?? -1;
            node["ignoresClassRule"] = owner != null && owner.HasTag(GAME_TAG.SIDEBOARD_IGNORES_CLASS_RULE);
            node["invalidCount"] = side.MainDeck.GetInvalidSideboardCardCount(null);
            var ruleset = side.GetRuleset();
            var rows = new JsonList();
            foreach (var slot in side.GetSlots())
            {
                var definition = slot.GetEntityDef();
                DeckRule broken = null;
                bool filtered = definition != null && ruleset != null && ruleset.Filter(definition, side, out broken);
                rows.Add(new JsonNode { { "cardId", slot.CardID }, { "owned", slot.Owned },
                    { "canAddAsOwned", side.CanCardBeAddedAsOwned(slot.CardID, TAG_PREMIUM.NORMAL) },
                    { "inSubset", members != null && members.Contains(slot.CardID) },
                    { "validSlot", side.IsValidSlot(slot, false, false, true, null) },
                    { "rulesetFilter", filtered }, { "brokenRule", broken?.Type.ToString() } });
            }
            node["slots"] = rows;
        }
        catch (Exception error) { node["error"] = error.GetType().Name + ": " + error.Message; }
        return node;
    }

    // Mirrors CollectionDeck.GetFabledBundledCardSlotStatus: a companion is owned through its own slot flag and its primary.
    internal static bool CardsOwned(IList<JsonNode> cards)
    {
        bool Copies(JsonNode card) => (int)card["ownedCount"] >= (int)card["count"];
        return cards.All(card => !card.TryGetValue("bundlePrimaryId", out object primary) ? Copies(card)
            : primary is string id && card["slotOwned"] is bool owned && owned
                && cards.Any(row => (string)row["cardId"] == id && !row.ContainsKey("bundlePrimaryId") && Copies(row)));
    }

    private static bool SameCards(IEnumerable<string> saved, IEnumerable<JsonNode> native)
    {
        var expected = saved.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var actual = native.GroupBy(c => (string)c["cardId"]).ToDictionary(g => g.Key, g => g.Sum(c => (int)c["count"]));
        return expected.Count == actual.Count && expected.All(c => actual.TryGetValue(c.Key, out int count) && count == c.Value);
    }

    // Live 2026-10-25: the in-match hero actor reported a null card id. Gameplay actors are bound with Actor.SetEntity
    // (pinned Card.UpdateActor); only collection and preview actors carry an EntityDef, so the entity comes first.
    internal static string ActorCardId(string entityCardId, string entityDefCardId) => entityCardId ?? entityDefCardId;
    // The snapshot skin is the ledger's favourite, the hero entity is that skin, and the drawn actor shows it.
    internal static bool HeroMatches(string expectedHero, string snapshotHero, string heroCardId, string actorCardId) =>
        snapshotHero != null && snapshotHero == expectedHero && heroCardId == snapshotHero && actorCardId == snapshotHero;

    internal static bool CardBackMatches(LocalProfile profile, LocalDeckCosmetics shown, int back) =>
        shown != null && shown.RandomCardBack ? profile.OwnedCardBacks.Contains(back)
            : shown?.CardBack is int own ? back == own : profile.FavoriteCardBacks.Contains(back);

    internal static JsonNode Read(LocalProfile profile)
    {
        NativeTestControl.AssertMainThread();
        if (profile == null) throw new InvalidOperationException("Local profile unavailable");
        var mismatches = new List<string>();
        var cache = NetCache.Get() ?? throw new InvalidOperationException("NetCache unavailable");
        var manager = CollectionManager.Get() ?? throw new InvalidOperationException("CollectionManager unavailable");
        var backs = cache.GetNetObject<NetCache.NetCacheCardBacks>();
        var favorites = cache.GetNetObject<NetCache.NetCacheFavoriteHeroes>();
        var coins = cache.GetNetObject<NetCache.NetCacheCoins>();
        if (backs == null || favorites == null || coins == null) throw new InvalidOperationException("Native cosmetic caches unavailable");
        if (!new HashSet<int>(profile.OwnedCardBacks).SetEquals(backs.CardBacks)) mismatches.Add("ownedCardBacks");
        if (!new HashSet<int>(backs.FavoriteCardBacks).SetEquals(profile.FavoriteCardBacks)) mismatches.Add("favoriteCardBacks");
        if (!new HashSet<int>(profile.OwnedCoins.Where(id => GameDbf.CosmeticCoin.GetRecord(id) != null)).SetEquals(coins.Coins)) mismatches.Add("ownedCoins");
        if (!new HashSet<int>(profile.FavoriteCoins.Where(coins.Coins.Contains)).SetEquals(coins.FavoriteCoins)) mismatches.Add("favoriteCoins");
        var ledgerFavorites = new JsonNode();
        var nativeFavorites = new JsonNode();
        foreach (string hero in LocalDecks.PlayerHeroes)
        {
            string expected = profile.FavoriteHero(hero);
            ledgerFavorites[hero] = expected;
            var values = favorites.FavoriteHeroes.Where(f => (int)f.Item1 == (int)LocalDecks.HeroClass(hero)).ToArray();
            nativeFavorites[hero] = values.Length == 1 ? values[0].Item2.Name : null;
            var expectedPremium = (TAG_PREMIUM)NativeUIProjection.FavoritePremium(profile, hero, expected);
            if (values.Length != 1 || values[0].Item2.Name != expected || values[0].Item2.Premium != expectedPremium)
                mismatches.Add("favoriteHero:" + hero);
        }
        var expectedHeroes = new HashSet<string>(LocalDecks.PlayerHeroes.Concat(profile.OwnedHeroSkins), StringComparer.Ordinal);
        var ownedHeroes = new HashSet<string>(manager.GetAllCards()
            .Where(c => c.IsHeroSkin || LocalDecks.PlayerHeroes.Contains(c.CardId)).Select(c => c.CardId)
            .Concat(expectedHeroes).Distinct(StringComparer.Ordinal).Where(id => manager.GetTotalOwnedCount(id) > 0), StringComparer.Ordinal);
        var heroes = new JsonList();
        foreach (string hero in expectedHeroes.Union(ownedHeroes).OrderBy(id => id, StringComparer.Ordinal))
        {
            bool owned = manager.IsCardInCollection(hero, TAG_PREMIUM.NORMAL);
            heroes.Add(new JsonNode { { "cardId", hero }, { "inCollection", owned } });
            if (expectedHeroes.Contains(hero) != ownedHeroes.Contains(hero) || !owned) mismatches.Add("ownedHeroSkin:" + hero);
        }
        var savedDecks = new JsonList();
        foreach (var deck in profile.CustomDecks.OrderBy(d => d.Id))
            savedDecks.Add(new JsonNode { { "id", deck.Id }, { "name", deck.Name }, { "hero", deck.Hero },
                { "coin", deck.Coin }, { "randomCoinUseFavorite", deck.RandomCoinUseFavorite }, { "cardBack", deck.CardBack }, { "randomCardBack", deck.RandomCardBack }, { "heroSkin", deck.HeroSkin },
                { "sourceRecipeId", deck.SourceRecipeId }, { "cards", Values(deck.Cards) },
                { "sideboards", TestControlProtocol.List(deck.Sideboards.Select(s => (object)new JsonNode {
                    { "ownerCardId", s.Source }, { "cards", Values(s.Cards) } })) } });
        var nativeDecks = new JsonList();
        var decks = manager.GetDecks() ?? throw new InvalidOperationException("Native deck list unavailable");
        foreach (var pair in decks.OrderBy(d => d.Key))
        {
            var saved = profile.CustomDecks.SingleOrDefault(d => d.Id == pair.Key);
            string baseHero = NativeUIProbe.NativeBaseHero(pair.Value);
            string resolvedMatchHero = profile.DeckHero(pair.Key, baseHero);
            string pickerHero = pair.Value.GetDisplayHeroCardID(false);
            var cards = pair.Value.GetSlots().Select(slot => Card(slot, manager, pair.Value)).ToArray();
            var sides = pair.Value.GetAllSideboards().OrderBy(row => row.Key, StringComparer.Ordinal)
                .ToDictionary(row => row.Key, row => row.Value.GetSlots().Select(slot => Card(slot, manager, pair.Value)).ToArray());
            bool allOwned = CardsOwned(cards) && sides.Values.All(CardsOwned);
            nativeDecks.Add(new JsonNode { { "id", pair.Key }, { "name", pair.Value.Name }, { "hero", NativeUIProbe.NativeBaseHero(pair.Value) },
                { "coin", pair.Value.CosmeticCoinID }, { "randomCoinUseFavorite", pair.Value.RandomCoinUseFavorite },
                { "heroCardId", pair.Value.HeroCardID }, { "heroOverridden", pair.Value.HeroOverridden }, { "cardBack", pair.Value.CardBackID },
                { "resolvedMatchHero", resolvedMatchHero }, { "pickerHeroCardId", pickerHero },
                { "sourceRecipeId", saved?.SourceRecipeId }, { "sourceRecipeProvenance", "ledger join by native deck id" },
                { "cards", Values(cards) }, { "sideboards", TestControlProtocol.List(sides.Select(row => (object)new JsonNode {
                    { "ownerCardId", row.Key }, { "cards", Values(row.Value) },
                    { "validation", SideboardValidation(pair.Value.GetAllSideboards()[row.Key]) } })) }, { "allCardsOwned", allOwned } });
            if (pickerHero != resolvedMatchHero) mismatches.Add("pickerMatchHero:" + pair.Key);
            if (saved != null)
            {
                if (saved.Name != pair.Value.Name) mismatches.Add("deckName:" + pair.Key);
                if (saved.Hero != NativeUIProbe.NativeBaseHero(pair.Value)) mismatches.Add("deckHero:" + pair.Key);
                var shown = profile.DeckCosmetics(saved.Id);
                if (pair.Value.HeroOverridden != (shown.HeroSkin != null) || shown.HeroSkin != null && pair.Value.HeroCardID != shown.HeroSkin)
                    mismatches.Add("deckHeroSkin:" + pair.Key);
                if (pair.Value.CosmeticCoinID != shown.Coin || pair.Value.RandomCoinUseFavorite != shown.RandomCoinUseFavorite) mismatches.Add("deckCoin:" + pair.Key);
                if (!shown.RandomCardBack && pair.Value.CardBackID != shown.CardBack) mismatches.Add("deckCardBack:" + pair.Key);
                if (!SameCards(saved.Cards, cards)) mismatches.Add("deckCards:" + pair.Key);
                if (saved.Sideboards.Count != sides.Count || saved.Sideboards.Any(side =>
                    !sides.TryGetValue(side.Source, out var actual) || !SameCards(side.Cards, actual)))
                    mismatches.Add("deckSideboards:" + pair.Key);
            }
        }
        foreach (var saved in profile.CustomDecks.Where(d => !decks.ContainsKey(d.Id))) mismatches.Add("deckMissing:" + saved.Id);
        JsonNode matchState = null;
        if (SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY)
        {
            var match = AccessTools.Field(typeof(ClientBridge), "match").GetValue(null) as NativeMatch;
            var player = GameState.Get()?.GetFriendlySidePlayer();
            var hero = player?.GetHero();
            var snapshotBack = match == null ? null : AccessTools.Field(typeof(NativeMatch), "cosmeticCardBack").GetValue(match);
            string snapshotHero = match == null ? null : AccessTools.Field(typeof(NativeMatch), "cosmeticHero").GetValue(match) as string;
            string baseHero = match == null ? null : AccessTools.Field(typeof(NativeMatch), "cosmeticBaseHero").GetValue(match) as string;
            var actor = hero?.GetCard()?.GetActor();
            string actorHero = ActorCardId(actor?.GetEntity()?.GetCardId(), actor?.GetEntityDef()?.GetCardId());
            matchState = new JsonNode { { "matchId", LocalLadder.MatchId }, { "baseHero", baseHero },
                { "cardBack", player?.GetCardBackId() }, { "heroCardId", hero?.GetCardId() }, { "actorHeroCardId", actorHero },
                { "snapshotCoin", match?.CosmeticCoin }, { "snapshotCoinCardId", match?.CosmeticCoinCardId },
                { "snapshotCardBack", snapshotBack }, { "snapshotHeroCardId", snapshotHero } };
            if (player == null || hero == null || snapshotBack == null || snapshotHero == null) mismatches.Add("matchCosmeticsUnavailable");
            else
            {
                // A personal deck's own back/skin wins over the favourite; a random back is any owned back.
                long? deckId = match.CosmeticDeckId;
                var shown = deckId.HasValue ? profile.DeckCosmetics(deckId.Value) : null;
                int back = Convert.ToInt32(snapshotBack);
                bool backOk = CardBackMatches(profile, shown, back);
                if (player.GetCardBackId() != back || !backOk) mismatches.Add("matchCardBack");
                matchState["deckId"] = deckId;
                string expectedHero = baseHero == null ? null : profile.DeckHero(deckId, baseHero);
                if (!HeroMatches(expectedHero, snapshotHero, hero.GetCardId(), actorHero)) mismatches.Add("matchHero");
            }
        }
        var pets = NativePetState.Read(profile);
        foreach (string mismatch in (JsonList)pets["mismatches"]) mismatches.Add("pet:" + mismatch);
        return new JsonNode {
            { "pets", pets },
            { "ledger", new JsonNode { { "ownedCardBacks", Values(profile.OwnedCardBacks.OrderBy(i => i)) },
                { "ownedCoins", Values(profile.OwnedCoins) }, { "favoriteCoins", Values(profile.FavoriteCoins) },
                { "favoriteCardBacks", Values(profile.FavoriteCardBacks) }, { "ownedHeroSkins", Values(profile.OwnedHeroSkins.OrderBy(i => i, StringComparer.Ordinal)) },
                { "favoriteHeroSkins", ledgerFavorites }, { "ownedRecipes", Values(profile.OwnedRecipes.OrderBy(i => i, StringComparer.Ordinal)) }, { "decks", savedDecks } } },
            { "native", new JsonNode { { "cardBacks", Values(backs.CardBacks.OrderBy(i => i)) },
                { "coins", Values(coins.Coins.OrderBy(i => i)) }, { "favoriteCoins", Values(coins.FavoriteCoins.OrderBy(i => i)) },
                { "favoriteCardBacks", Values(backs.FavoriteCardBacks.OrderBy(i => i)) }, { "favoriteHeroes", nativeFavorites },
                { "heroSkins", heroes }, { "decks", nativeDecks } } },
            { "match", matchState }, { "mismatches", Values(mismatches) },
            { "gold", profile.Gold }, { "receipts", TestControlProtocol.List(profile.Purchases.Select(r => (object)TestControlShop.Receipt(r, profile.Gold))) }
        };
    }
}
#endif
