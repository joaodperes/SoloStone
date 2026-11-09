using System;
using System.Linq;

internal static partial class NativeUIProbe
{
    internal static void RefreshShopPurchase(LocalPurchaseReceipt receipt)
    {
        NativeTestControl.AssertMainThread();
        PublishBoosters();
        if (receipt.Granted.Cards.Count != 0)
        {
            var manager = CollectionManager.Get() ?? throw new InvalidOperationException("Native collection unavailable during shop commit");
            AddNativeGrants(NativeUIProjection.ShopGrantCounts(Profile.Collection, receipt.Granted.Cards.Select(card => card.CardId),
                id => manager.GetOwnedCount(id, TAG_PREMIUM.NORMAL)));
        }
        if (receipt.Granted.DeckId.HasValue)
        {
            var saved = Profile.GetDraft(receipt.Granted.DeckId.Value);
            if (!decks.ContainsKey(saved.Id))
                decks.Add(saved.Id, LocalProfile.LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false));
            var manager = CollectionManager.Get() ?? throw new InvalidOperationException("Native collection unavailable during shop commit");
            // Retry a partial projection without recharging or adding the same saved deck twice.
            if (manager.GetDeck(saved.Id) == null || manager.GetBaseDeck(saved.Id) == null)
                Required(typeof(CollectionManager), "OnDeckCreated", new[] { typeof(NetCache.DeckHeader), typeof(int?) })
                    .Invoke(manager, new object[] { ExpectedHeader(saved.Id), null });
            foreach (var native in new[] { manager.GetDeck(saved.Id), manager.GetBaseDeck(saved.Id) }.Distinct())
            {
                if (native == null) throw new InvalidOperationException("Native purchased deck projection incomplete");
                ProjectMissingDraftCards(native, saved);
                ProjectSideboards(native, saved.Sideboards);
                native.MarkNetworkContentsLoaded();
                ValidateNative(native);
            }
            SynchronizeExpectedNativeRanks();
        }
        RefreshCosmetics();
    }
}
