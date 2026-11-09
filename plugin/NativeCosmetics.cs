using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

internal static partial class NativeUIProbe
{
    private static void InitializeCosmetics(Harmony harmony)
    {
        Hook(harmony, typeof(CollectionDeck), "GetDisplayHeroCardID", nameof(LocalDeckDisplayHero), new[] { typeof(bool) });
        Hook(harmony, typeof(Network), "SetFavoritePet", nameof(SelectLocalPet), new[] { typeof(int), typeof(bool?), typeof(bool?) });
        Hook(harmony, typeof(Network), "SetFavoritePetVariant", nameof(SelectLocalPetVariant), new[] { typeof(int), typeof(bool?), typeof(bool?) });
        NativeMatch.NativePetVariant = card => PetVariant(card).ID;
        NativeMatch.CosmeticsProfile = () => IsolatedLaunch ? Profile : null;
        // Decide one atomic local toggle from the ledger rather than the native cache.
        Hook(harmony, typeof(CardBackManager), "HandleFavoriteToggle", nameof(ToggleLocalCardBack), new[] { typeof(int) });
        Hook(harmony, typeof(CosmeticCoinManager), "RequestSetFavoriteCosmeticCoin", nameof(SelectLocalCoin), new[] { typeof(int), typeof(bool) });
        Hook(harmony, typeof(Network), "SetFavoriteCardBack", nameof(SelectLocalCardBack), new[] { typeof(int), typeof(bool) });
        Hook(harmony, typeof(Network), "SetFavoriteHero", nameof(SelectLocalHero),
            new[] { typeof(TAG_CLASS), typeof(NetCache.CardDefinition), typeof(bool) });
    }

    private static bool LocalDeckDisplayHero(CollectionDeck __instance, ref string __result)
    {
        if (!active || Profile == null || __instance == null
            || !(NativeUIProjection.IsPersonalId(__instance.ID) || NativeUIProjection.IsLoanerId(__instance.ID))) return true;
        string hero = NativeBaseHero(__instance);
        if (!Heroes.Contains(hero)) return true;
        // Read the native override so an unsaved editor preview still reflects the player's current edit.
        __result = Profile.ResolveHero(hero, __instance.HeroOverridden ? __instance.HeroCardID : null);
        return false;
    }

    internal static PetVariantDbfRecord PetVariant(string cardId) {
        int card = GameUtils.TranslateCardIdToDbId(cardId);
        var record = GameDbf.PetVariant?.GetRecords().SingleOrDefault(row => row.CardId == card);
        if (card <= 0 || record == null || !record.Enabled || GameDbf.Pet?.GetRecord(record.PetId)?.Enabled != true)
            throw new InvalidOperationException("Pet form unavailable in the pinned client: " + cardId);
        return record;
    }
    internal static bool PetEntryValid(LocalShopProduct product) {
        if (product.Pet == null) return true;
        var parent = GameDbf.Pet?.GetRecord(product.NativeId);
        if (parent == null || !parent.Enabled) return false;
        var levels = GameDbf.PetLevel.GetRecords().Where(row => row.PetId == parent.ID).OrderBy(row => row.Level).ToArray();
        var forms = LocalShop.Default.FormsForPet(product.Pet);
        var thresholds = LocalShop.Default.ThresholdsForPet(product.Pet);
        if (levels.Length != forms.Count || levels.Length == 0) return false;
        int total = 0;
        for (int i = 0; i < levels.Length; i++) {
            if (levels[i].Level != i + 1 || total != thresholds[i]) return false;
            var variant = PetVariant(forms[i]);
            if (variant.PetId != parent.ID || levels[i].PetVariantUnlocked != variant.ID) return false;
            if (i < levels.Length - 1) {
                if (levels[i].NextXpNeeded <= 0) return false;
                total = checked(total + levels[i].NextXpNeeded);
            }
        }
        return true;
    }
    private static NetCache.NetCachePets CosmeticPets() {
        var result = new NetCache.NetCachePets();
        if (Profile == null) return result;
        string selected = Profile.EffectiveSelectedPet;
        foreach (string pet in Profile.OwnedPets) {
            int id = LocalShop.Default.PetNativeId(pet);
            var info = new NetCache.PetInfo { PetId = id, CurrentLevel = Profile.PetLevel(pet), CurrentXp = Profile.PetLevelXp(pet),
                IsHsFavorite = LocalShop.Default.PetFamily(selected) == pet, IsBgFavorite = false };
            result.Pets.Add(id, info);
            foreach (string form in LocalShop.Default.FormsForPet(pet).Where(Profile.CanUsePetForm)) {
                int variant = PetVariant(form).ID;
                info.VariantIDs.Add(variant);
                result.PetVariants.Add(variant, new NetCache.PetVariantInfo { PetVariantId = variant,
                    IsHsFavorite = form == selected, IsBgFavorite = false });
            }
        }
        return result;
    }
    // Set while a receipt's silent publish has not completed: the next refresh of any kind then publishes without the XP event.
    internal static bool PendingSilentPets;
    // notifyXp=false publishes the ledger snapshot without the XP event: the end-of-game pet moment fires that event
    // itself from the settled receipt, so a later cache diff can never re-queue the bar or level-up scrolls.
    internal static void RefreshPets(bool notifyXp = true) {
        if (Profile == null) return;
        notifyXp &= !PendingSilentPets;
        var before = cache.TryGetValue(typeof(NetCache.NetCachePets), out object previous) ? (NetCache.NetCachePets)previous : null;
        var next = CosmeticPets();
        Publish(next);
        PendingSilentPets = false;
        var native = NetCache.Get();
        // OnPetModifications also writes the retail offline cache; publish the ledger snapshot and notify its XP event directly.
        var xpChanged = AccessTools.Field(typeof(NetCache), "PetsXpLevelChanged").GetValue(native) as NetCache.DelPetsXpLevelChanged;
        foreach (var pet in next.Pets.Values)
            if (notifyXp && before != null && before.Pets.TryGetValue(pet.PetId, out var old)
                && (old.CurrentLevel != pet.CurrentLevel || old.CurrentXp != pet.CurrentXp))
                xpChanged?.Invoke(pet.PetId, old.CurrentLevel, pet.CurrentLevel, old.CurrentXp, pet.CurrentXp);
        var favoritePet = AccessTools.Field(typeof(NetCache), "FavoritePetChanged").GetValue(native) as NetCache.DelFavoritePetChangedListener;
        var favoriteForm = AccessTools.Field(typeof(NetCache), "FavoritePetVariantChanged").GetValue(native) as NetCache.DelFavoritePetVariantChangedListener;
        foreach (var pet in next.Pets.Values) {
            var old = before != null && before.Pets.TryGetValue(pet.PetId, out var value) ? value : null;
            if ((old?.IsHsFavorite ?? false) != pet.IsHsFavorite || (old?.IsBgFavorite ?? false) != pet.IsBgFavorite)
                favoritePet?.Invoke(pet.PetId, pet.IsHsFavorite, pet.IsBgFavorite);
        }
        foreach (var form in next.PetVariants.Values) {
            var old = before != null && before.PetVariants.TryGetValue(form.PetVariantId, out var value) ? value : null;
            if ((old?.IsHsFavorite ?? false) != form.IsHsFavorite || (old?.IsBgFavorite ?? false) != form.IsBgFavorite)
                favoriteForm?.Invoke(form.PetVariantId, form.IsHsFavorite, form.IsBgFavorite);
        }
    }
    // Fires exactly the native retail event for one settled pet XP move (RewardXpNotificationManager and PetsManager
    // subscribe to it). Returns false when the native event has no listeners (nothing would be queued).
    internal static bool NotifyPetXp(int petId, int previousLevel, int newLevel, int previousXp, int newXp) {
        var native = NetCache.Get();
        var xpChanged = native == null ? null : AccessTools.Field(typeof(NetCache), "PetsXpLevelChanged").GetValue(native) as NetCache.DelPetsXpLevelChanged;
        if (xpChanged == null) return false;
        xpChanged(petId, previousLevel, newLevel, previousXp, newXp);
        return true;
    }
    private static bool SelectLocalPet(int __0, bool? __1, bool? __2) {
        if (!IsolatedLaunch) return true;
        bool saved = false;
        try {
            if (!active || !ready || Profile == null) throw new InvalidOperationException("Local pets are not ready.");
            if (__1.HasValue) {
                string pet = Profile.OwnedPets.SingleOrDefault(id => LocalShop.Default.PetNativeId(id) == __0)
                    ?? throw new InvalidOperationException("Pet is not owned.");
                if (__1.Value) Profile.SelectPet(pet);
                else if (LocalShop.Default.PetFamily(Profile.EffectiveSelectedPet) == pet) Profile.SelectPet(null);
            }
            saved = true;
            RefreshPets();
        } catch (Exception error) { CosmeticSelectionFailed(error, saved); }
        return false;
    }
    private static bool SelectLocalPetVariant(int __0, bool? __1, bool? __2) {
        if (!IsolatedLaunch) return true;
        bool saved = false;
        try {
            if (!active || !ready || Profile == null) throw new InvalidOperationException("Local pets are not ready.");
            if (__1.HasValue) {
                var record = GameDbf.PetVariant.GetRecord(__0) ?? throw new InvalidOperationException("Unknown pet form.");
                string card = GameUtils.TranslateDbIdToCardId(record.CardId);
                if (__1.Value) Profile.SelectPet(card);
                else if (Profile.EffectiveSelectedPet == card) Profile.SelectPet(null);
            }
            saved = true;
            RefreshPets();
        } catch (Exception error) { CosmeticSelectionFailed(error, saved); }
        return false;
    }

    private static NetCache.NetCacheCardBacks CosmeticCardBacks()
    {
        var backs = new NetCache.NetCacheCardBacks();
        backs.CardBacks.UnionWith(Profile.OwnedCardBacks);
        backs.FavoriteCardBacks.UnionWith(Profile.FavoriteCardBacks);
        return backs;
    }

    private static NetCache.NetCacheCoins CosmeticCoins() {
        var coins = new NetCache.NetCacheCoins();
        // Native random selection dereferences every owned DBF row without a null check.
        coins.Coins.UnionWith(Profile.OwnedCoins.Where(id => !string.IsNullOrEmpty(NativeMatch.NativeCoinCard(id))));
        coins.FavoriteCoins.UnionWith(Profile.FavoriteCoins.Where(coins.Coins.Contains));
        return coins;
    }

    private static void RefreshCoins() {
        var old = cache.TryGetValue(typeof(NetCache.NetCacheCoins), out object previous)
            ? ((NetCache.NetCacheCoins)previous).FavoriteCoins.ToArray() : new int[0];
        var coins = CosmeticCoins();
        Publish(coins);
        foreach (int coin in old.Except(coins.FavoriteCoins)) NetCache.Get().ProcessNewFavoriteCoin(coin, false);
        foreach (int coin in coins.FavoriteCoins.ToArray()) NetCache.Get().ProcessNewFavoriteCoin(coin, true);
    }

    private static bool SelectLocalCoin(int __0, bool __1) {
        if (!IsolatedLaunch) return true;
        bool saved = false;
        try {
            if (!active || !ready || Profile == null) throw new InvalidOperationException("Local cosmetics are not ready.");
            if (string.IsNullOrEmpty(NativeMatch.NativeCoinCard(__0))) throw new InvalidOperationException("Unknown cosmetic coin.");
            Profile.SetCoinFavorite(__0, __1);
            saved = true;
            RefreshCoins();
        } catch (Exception error) { CosmeticSelectionFailed(error, saved); }
        return false;
    }

    private static NetCache.NetCacheFavoriteHeroes CosmeticFavorites()
    {
        var favorites = new NetCache.NetCacheFavoriteHeroes();
        foreach (string hero in Heroes)
        {
            string skin = Profile.FavoriteHero(hero);
            favorites.FavoriteHeroes.Add(((TAG_CLASS)(int)LocalDecks.HeroClass(hero),
                new NetCache.CardDefinition { Name = skin, Premium = (TAG_PREMIUM)NativeUIProjection.FavoritePremium(Profile, hero, skin) }));
        }
        return favorites;
    }

    // Public so native checkout and the control command share the same post-commit refresh.
    public static void RefreshCosmetics()
    {
        RefreshPets();
        RefreshCoins();
        var manager = CollectionManager.Get() ?? throw new InvalidOperationException("Native collection unavailable during cosmetics refresh");
        var collection = (NetCache.NetCacheCollection)cache[typeof(NetCache.NetCacheCollection)];
        var heroes = NativeUIProjection.CosmeticHeroes(Profile).ToArray();
        var changes = new List<CollectionManager.CardModification>();
        // Every hero keeps one Normal copy; the golden vanilla hero (portrait grant) is a second, premium copy.
        var versions = heroes.Select(hero => (Hero: hero, Premium: TAG_PREMIUM.NORMAL, Count: 1))
            .Concat(heroes.Where(hero => Profile.OwnedCount(hero, 1) > 0).Select(hero => (Hero: hero, Premium: TAG_PREMIUM.GOLDEN, Count: Profile.OwnedCount(hero, 1))))
            .ToArray();
        foreach (var version in versions)
        {
            int count = manager.GetOwnedCount(version.Hero, version.Premium);
            if (count > version.Count) throw new InvalidOperationException("Native hero ownership exceeds ledger: " + version.Hero);
            if (count < version.Count) changes.Add(new CollectionManager.CardModification {
                modificationType = CollectionManager.CardModification.ModificationType.Add,
                cardID = version.Hero, premium = version.Premium, count = version.Count - count, seenBefore = true });
        }
        if (changes.Count != 0) manager.OnCardsModified(changes);
        // Hero insertion returns before the native collection cache notification.
        var cachedHeroes = new HashSet<(string, TAG_PREMIUM)>(collection.Stacks.Select(s => (s.Def.Name, s.Def.Premium)));
        foreach (var version in versions)
        {
            if (manager.GetOwnedCount(version.Hero, version.Premium) != version.Count) throw new InvalidOperationException("Native hero grant failed: " + version.Hero);
            if (cachedHeroes.Add((version.Hero, version.Premium)))
            {
                collection.Stacks.Add(new NetCache.CardStack {
                    Def = new NetCache.CardDefinition { Name = version.Hero, Premium = version.Premium }, Count = version.Count, NumSeen = version.Count });
                collection.TotalCardsOwned += version.Count;
            }
        }
        Publish(collection);
        var oldBacks = cache.TryGetValue(typeof(NetCache.NetCacheCardBacks), out object previousBacks)
            ? ((NetCache.NetCacheCardBacks)previousBacks).FavoriteCardBacks.ToArray() : new int[0];
        var oldHeroes = cache.TryGetValue(typeof(NetCache.NetCacheFavoriteHeroes), out object previousHeroes)
            ? ((NetCache.NetCacheFavoriteHeroes)previousHeroes).FavoriteHeroes.ToArray()
            : new (TAG_CLASS, NetCache.CardDefinition)[0];
        if (previousBacks is NetCache.NetCacheCardBacks ownership)
        {
            var added = Profile.OwnedCardBacks.Except(ownership.CardBacks).ToArray();
            if (added.Length != 0)
            {
                var backManager = CardBackManager.Get() ?? throw new InvalidOperationException("Native card-back manager unavailable");
                var modifications = new PegasusUtil.CardBackModifications();
                foreach (int id in added)
                {
                    backManager.SetCollectionCardBackOwned(id);
                    modifications.CardBackModifications_.Add(new PegasusUtil.CardBackModification { AssetCardBackId = id });
                }
                NetCache.Get().OnCardBackModifications(modifications);
            }
        }
        var backs = CosmeticCardBacks();
        var favorites = CosmeticFavorites();
        Publish(backs);
        Publish(favorites);
        // Use native notifications after replacing snapshots; no additive grant replay.
        foreach (int back in oldBacks.Where(id => !Profile.FavoriteCardBacks.Contains(id)))
            NetCache.Get().ProcessNewFavoriteCardBack(back, false);
        foreach (int back in Profile.FavoriteCardBacks) NetCache.Get().ProcessNewFavoriteCardBack(back, true);
        foreach (var old in oldHeroes.Where(old => !favorites.FavoriteHeroes.Any(f => f.Item1 == old.Item1
            && f.Item2.Name == old.Item2.Name && f.Item2.Premium == old.Item2.Premium)))
            manager.UpdateFavoriteHero(old.Item1, old.Item2.Name, old.Item2.Premium, false);
        foreach (var favorite in favorites.FavoriteHeroes)
            manager.UpdateFavoriteHero(favorite.Item1, favorite.Item2.Name, favorite.Item2.Premium, true);
    }

    private static bool ToggleLocalCardBack(int __0) => SelectLocalCardBack(__0, Profile == null || !Profile.FavoriteCardBacks.Contains(__0));

    private static bool SelectLocalCardBack(int __0, bool __1)
    {
        if (!IsolatedLaunch) return true;
        bool saved = false;
        try {
            if (!active || !ready || Profile == null) throw new InvalidOperationException("Local cosmetics are not ready.");
            NativeUIProjection.SelectCardBack(Profile, __0, __1);
            saved = true;
            RefreshCosmetics();
        } catch (Exception error) { CosmeticSelectionFailed(error, saved); }
        return false;
    }

    private static bool SelectLocalHero(TAG_CLASS __0, NetCache.CardDefinition __1, bool __2)
    {
        if (!IsolatedLaunch) return true;
        bool saved = false;
        try {
            if (!active || !ready || Profile == null) throw new InvalidOperationException("Local cosmetics are not ready.");
            string heroClass = Heroes.SingleOrDefault(h => (int)LocalDecks.HeroClass(h) == (int)__0);
            if (__1 == null || __1.Premium != TAG_PREMIUM.NORMAL && __1.Premium != TAG_PREMIUM.GOLDEN)
                throw new InvalidOperationException("Only owned Normal or Golden hero versions can be selected.");
            NativeUIProjection.SelectHeroVersion(Profile, heroClass, __1.Name, __2, (int)__1.Premium);
            saved = true;
            RefreshCosmetics();
        } catch (Exception error) { CosmeticSelectionFailed(error, saved); }
        return false;
    }

    private static void CosmeticSelectionFailed(Exception error, bool saved)
    {
        log.LogWarning("Local cosmetics selection: " + error.Message);
        try { if (active && ready) RefreshCosmetics(); }
        catch (Exception refreshError) { log.LogWarning("Cosmetics refresh: " + refreshError.Message); }
        DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
            m_headerText = saved ? "Cosmetic selection saved" : "Cosmetic selection failed",
            m_text = saved ? "Selection saved; native display could not refresh. Reopen Collection. " + error.Message : error.Message,
            m_responseDisplay = AlertPopup.ResponseDisplay.OK });
    }
}

#if !SOLOSTONE_RELEASE
// Read-only pet evidence: compare the durable ledger, native collection cache and the actual in-match model.
internal static class NativePetState
{
    internal static JsonNode Read(LocalProfile profile)
    {
        var mismatches = new JsonList(); var native = new JsonList(); var progress = new JsonList();
        var pets = NetCache.Get()?.GetNetObject<NetCache.NetCachePets>();
        if (pets == null) throw new InvalidOperationException("Native pet cache unavailable");
        var expected = new HashSet<int>(profile.OwnedPets.Select(LocalShop.Default.PetNativeId));
        if (!expected.SetEquals(pets.Pets.Keys)) mismatches.Add("ownedPets");
        var expectedVariants = new HashSet<int>();
        foreach (string pet in profile.OwnedPets) {
            int id = LocalShop.Default.PetNativeId(pet);
            var forms = LocalShop.Default.FormsForPet(pet).Where(profile.CanUsePetForm).Select(NativeUIProbe.PetVariant).ToArray();
            expectedVariants.UnionWith(forms.Select(form => form.ID));
            if (!pets.Pets.TryGetValue(id, out var info)) continue;
            if (info.CurrentLevel != profile.PetLevel(pet) || info.CurrentXp != profile.PetLevelXp(pet)
                || !info.VariantIDs.SetEquals(forms.Select(form => form.ID))) mismatches.Add("progress:" + pet);
            if (info.IsHsFavorite != (LocalShop.Default.PetFamily(profile.EffectiveSelectedPet) == pet)) mismatches.Add("favorite:" + pet);
        }
        if (!expectedVariants.SetEquals(pets.PetVariants.Keys)) mismatches.Add("unlockedForms");
        foreach (var row in pets.Pets.Values.OrderBy(row => row.PetId))
            native.Add(new JsonNode { { "petId", row.PetId }, { "level", row.CurrentLevel }, { "xp", row.CurrentXp },
                { "favorite", row.IsHsFavorite }, { "variants", TestControlProtocol.List(row.VariantIDs.OrderBy(id => id).Cast<object>()) } });
        var variants = new JsonList();
        foreach (var form in pets.PetVariants.Values.OrderBy(row => row.PetVariantId)) {
            var record = GameDbf.PetVariant.GetRecord(form.PetVariantId);
            string card = record == null ? null : GameUtils.TranslateDbIdToCardId(record.CardId);
            if (card == null || form.IsHsFavorite != (card == profile.EffectiveSelectedPet)) mismatches.Add("favoriteForm:" + form.PetVariantId);
            variants.Add(new JsonNode { { "variantId", form.PetVariantId }, { "cardId", card }, { "favorite", form.IsHsFavorite } });
        }
        foreach (var row in profile.PetProgress.OrderBy(row => row.Pet, StringComparer.Ordinal))
            progress.Add(new JsonNode { { "pet", row.Pet }, { "xp", row.Xp } });
        JsonNode match = null;
        if (SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY) {
            var running = AccessTools.Field(typeof(ClientBridge), "match").GetValue(null) as NativeMatch;
            string snapshot = running == null ? null : AccessTools.Field(typeof(NativeMatch), "cosmeticPet").GetValue(running) as string;
            var entity = GameState.Get()?.GetFriendlySidePlayer()?.GetPet();
            var actor = entity?.GetCard()?.GetActor();
            var controller = actor?.GetComponentInChildren<PetControllerGame>();
            var model = controller?.PetObject;
            bool rendered = model != null && model.activeInHierarchy && model.GetComponentsInChildren<Renderer>(true)
                .Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy);
            match = new JsonNode { { "snapshotPet", snapshot }, { "cardId", entity?.GetCardId() },
                { "variantId", controller?.PetVariantId }, { "model", model?.name }, { "rendered", rendered } };
            if (snapshot == null ? entity != null : entity?.GetCardId() != snapshot || controller == null
                || controller.PetVariantId != NativeUIProbe.PetVariant(snapshot).ID || !rendered)
                mismatches.Add("matchPet");
        }
        return new JsonNode {
            { "ledger", new JsonNode { { "ownedPets", TestControlProtocol.List(profile.PetProgress.Select(row => row.Pet).OrderBy(pet => pet, StringComparer.Ordinal).Cast<object>()) },
                { "selectedPet", profile.SelectedPet }, { "effectiveSelectedPet", profile.EffectiveSelectedPet }, { "progress", progress } } },
            { "native", native }, { "variants", variants }, { "match", match }, { "mismatches", mismatches }
        };
    }
}
#endif
