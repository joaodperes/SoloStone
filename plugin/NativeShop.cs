using System;
using System.Collections.Generic;
using System.Linq;
#if !NATIVE_SHOP_POLICY_TEST
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Blizzard.T5.Jobs;
using Blizzard.T5.Services;
using HarmonyLib;
using Hearthstone.Attribution;
using Hearthstone.CRM;
using Hearthstone.DataModels;
using Hearthstone.Store;
using HearthstoneTelemetry;
using Shared.Scripts.Game.Shop.Product;
using UnityEngine;
using ShopAttribute = Shared.Scripts.Game.Shop.Product.Attribute;
#endif

// #57 kit question copy and test-control ids (pure: test-control checks them against the denylist).
internal static class NativeShopKitText
{
    internal const string UseId = "shop.kit.use", FullId = "shop.kit.full";
    internal const string Header = "DECK KIT", UseLabel = "USE KIT", FullLabel = "PAY FULL";
    internal static string Body(string deck, int price, int kitGoldCap) =>
        (string.IsNullOrEmpty(deck) ? "" : deck + "\n") + "USE KIT: pay " + Gold(Math.Max(0, price - kitGoldCap))
        + " gold, the kit covers up to " + Gold(kitGoldCap) + ".\nPAY FULL: pay " + Gold(price) + " gold and keep the kit for another deck.";
    private static string Gold(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}

// PAY FULL is the kit question's cancel button, and Escape also answers CANCEL. A CANCEL means PAY FULL only together with
// a RELEASE on that button in the same frame (either order); a CANCEL with no such release (Escape, back) cancels once its
// frame is over. A press dragged off the button sends no RELEASE, so it can never arm a later Escape.
internal sealed class NativeShopKitRelease
{
    private int releasedFrame = -1, cancelFrame = -1;
    internal void Reset() { releasedFrame = -1; cancelFrame = -1; }
    internal bool Waiting => cancelFrame >= 0;
    // RELEASE on PAY FULL: true when it completes a CANCEL already received this frame.
    internal bool Released(int frame) { releasedFrame = frame; return cancelFrame == frame; }
    // CANCEL response: true = PAY FULL now (released this frame); false = wait for this frame's release.
    internal bool Cancel(int frame) { if (releasedFrame == frame) return true; cancelFrame = frame; return false; }
    // A CANCEL still waiting after its frame was not a PAY FULL press.
    internal bool Expired(int frame) => cancelFrame >= 0 && frame > cancelFrame;
}

internal sealed class NativeShopPurchase
{
    // Preserve the spike's STANDARD PMT id; expansion ids are stable across catalogue reordering.
    internal const long ProductId = 1900000001;
    internal const int MaxDeckNativeId = 128; // Equals LocalShop.MaxDeckNativeId (ShopTests pin it); test control links this file alone.
    // Reserved local PMT ranges; PmtId refuses anything else, and test control allows exactly these product ids.
    internal static bool IsLocalPmtId(long id) =>
        id > 1900000000L && id < 1901000000L // packs: Standard 1900000001, expansions 1900000000 + booster id
        || id > 1910000000L && id < 1920000000L // card backs: 1910000000 + card back id
        || id > 1920000000L && id < 1930000000L // hero skins: 1920000000 + hero card DBF id
        || id > 1930000000L && id <= 1930000000L + MaxDeckNativeId // recipes: 1930000000 + NativeId
        || id > 1940000000L && id <= 1940000000L + MaxDeckNativeId // ready-made decks: 1940000000 + NativeId
        || id > 1950000000L && id < 1950001000L // main-menu themes: 1950000000 + LocalHubCosmetics.ThemeNativeId
        || id > 1960000000L && id < 1960200000L // boards: 1960000000 + LocalHubCosmetics.BoardNativeId
        || id > 1970000000L && id < 1971000000L // pets: 1970000000 + pet id
        || id > 1980000001L && id < 1990000000L; // coins: 1980000000 + cosmetic coin id
    // Bumped whenever the offered set or native quotes change; per-frame identity caches key on it.
    internal static int CatalogueVersion = 0;
#if !NATIVE_SHOP_POLICY_TEST || NATIVE_SHOP_LEDGER_TEST
    private static LocalShop catalogue;
    private static LocalShopProduct[] offeredProducts;
    private static Dictionary<long, LocalShopProduct> productMap;
    internal static IReadOnlyList<LocalShopProduct> Products
    {
        get
        {
            if (!ReferenceEquals(catalogue, LocalShop.Default)) Seed(null, null);
            return offeredProducts;
        }
    }
    internal static void Seed(Func<LocalShopProduct, bool> valid, Action<string> excluded)
    {
        var map = new Dictionary<long, LocalShopProduct>();
        var offered = new List<LocalShopProduct>();
        foreach (var product in LocalShop.Default.Products.Where(p => !p.verify && !LocalShop.Default.Withheld(p)))
        {
            try {
                NativeItemId(product);
                long id = PmtId(product);
                if (valid != null && !valid(product)) throw new InvalidOperationException("native item unavailable");
                map.Add(id, product);
                offered.Add(product);
            }
            catch (Exception error) { excluded?.Invoke("excluded " + product.Id + ": " + error.Message); }
        }
        catalogue = LocalShop.Default;
        productMap = map;
        offeredProducts = offered.ToArray();
        CatalogueVersion++;
    }
    internal static long PmtId(LocalShopProduct product)
    {
        long id;
        switch (product.Kind)
        {
            case "Pack": id = product.PackType == FairPackRules.Standard
                ? ProductId : 1900000000L + FairPackRules.NativeBoosterId(product.PackType); break;
            case "CardBack": id = 1910000000L + NativeItemId(product); break;
            case "Coin": id = 1980000000L + NativeItemId(product); break;
            case "HeroSkin": id = 1920000000L + NativeItemId(product); break;
            case "Recipe": id = 1930000000L + product.NativeId; break;
            case "Deck": id = 1940000000L + product.NativeId; break;
            case "HubTheme": id = 1950000000L + NativeItemId(product); break;
            case "Board": id = 1960000000L + NativeItemId(product); break;
            case "Pet": id = 1970000000L + NativeItemId(product); break;
            default: throw new InvalidOperationException("Unknown local product kind.");
        }
        return IsLocalPmtId(id) ? id : throw new InvalidOperationException("Local product outside reserved PMT range: " + product.Id);
    }
    internal static int NativeItemId(LocalShopProduct product)
    {
        if (product.Kind == "Recipe" || product.Kind == "Deck") return NativeShopDecks.Id(product);
        if (product.Kind == "HubTheme") return LocalHubCosmetics.ThemeNativeId(product.HubTheme);
        if (product.Kind == "Board") return LocalHubCosmetics.BoardNativeId(product.Board.Value);
        if (product.Kind == "Pet") return product.Pet == null ? 999999 : product.NativeId;
        if (product.Kind == "Coin" && (!product.Coin.HasValue || product.Coin.Value <= 1))
            throw new InvalidOperationException("Default or unknown coin is not sold.");
        int id = product.Kind == "Coin" ? product.Coin.Value : product.Kind == "Pack" ? FairPackRules.NativeBoosterId(product.PackType)
            : product.Kind == "CardBack" ? product.CardBack.Value : GameUtils.TranslateCardIdToDbId(product.HeroSkin);
        if (id <= 0 || id >= 10000000) throw new InvalidOperationException("Native item outside reserved PMT range: " + product.Id);
        return id;
    }
    internal static bool TryProduct(long pmtId, out LocalShopProduct product)
    {
        _ = Products;
        return productMap.TryGetValue(pmtId, out product);
    }
    internal static LocalShopProduct Product(long pmtId) => TryProduct(pmtId, out var product) ? product
        : throw new InvalidOperationException("Unknown local shop product.");
    internal static int CollectionCardBackProductId(int cardBack) => cardBack > 0
        && TryProduct(1910000000L + cardBack, out var product) && product.Kind == "CardBack" && product.CardBack == cardBack
        ? checked((int)PmtId(product)) : 0;
    internal static int CollectionHeroSkinProductId(string heroSkin)
    {
        var product = Products.FirstOrDefault(p => p.Kind == "HeroSkin" && p.HeroSkin == heroSkin);
        return product == null ? 0 : checked((int)PmtId(product));
    }
    internal static bool AllowsClosedShop(string kind, string collectionCosmeticId, string productCosmeticId) =>
        (kind == "CardBack" || kind == "HeroSkin") && !string.IsNullOrEmpty(collectionCosmeticId)
        && collectionCosmeticId == productCosmeticId;
    internal static long NextTransactionId(LocalProfile profile, long previous, long ticks) =>
        Math.Max(ticks, Math.Max(checked(profile.HighestNumericPurchaseRequestId + 1), checked(previous + 1)));
    internal static string Blocker(LocalProfile profile, LocalShopProduct product)
    {
        // Mirrors TestControlShop.Blocker; per-product, so every query is non-copying.
        if (profile.HasPendingMatch || profile.HasPendingRewardPresentation) return "Match or rewards pending.";
        if (profile.PackRevealPending) return "A native pack reveal is pending.";
        if (product.Kind == "Deck") return profile.DeckPurchaseBlocker(product.RecipeId) ?? DeckCapacityBlocker(profile, product);
        return null;
    }
    // Tray-capacity seam: NativeShop installs NativeUIProbe.PersonalDeckBlocker (the personal cap plus room in the live
    // tray beside this session's loaners); without it (Mac tests) the personal cap alone. Non-copying: runs per product.
    internal static Func<LocalProfile, string> PersonalDeckBlockerSeam;
    internal static string PersonalDeckBlocker(LocalProfile profile) => PersonalDeckBlockerSeam != null ? PersonalDeckBlockerSeam(profile)
        : profile.CustomDeckCount >= LocalProfile.MaximumCustomDecks ? LocalProfile.PersonalDeckLimitMessage : null;
    internal static string DeckCapacityBlocker(LocalProfile profile, LocalShopProduct product)
    {
        if (product.Kind != "Deck") return null;
        string blocker = PersonalDeckBlocker(profile);
        return blocker != null && profile.DeckPurchaseNeedsSlot(product.RecipeId) ? blocker : null;
    }
    internal static bool Owned(LocalProfile profile, LocalShopProduct product, int price)
    {
        switch (product.Kind)
        {
            case "Recipe": return profile.OwnsRecipe(product.RecipeId);
            case "Deck": return profile.HasDeckReceipt(product.RecipeId) || price == 0 && profile.DeckPurchaseBlocker(product.RecipeId) == null;
            case "CardBack": return profile.OwnsCardBack(product.CardBack.Value);
            case "Coin": return profile.OwnsCoin(product.Coin.Value);
            case "HeroSkin": return profile.OwnsHeroSkin(product.HeroSkin);
            case "HubTheme": return profile.OwnsHubTheme(product.HubTheme);
            case "Board": return profile.OwnsBoard(product.Board.Value);
            case "Pet": return profile.OwnsPet(product.Pet);
            default: return false; // Packs remain repeatable even with unopened inventory.
        }
    }
    // Themes and boards are equipped by clicking their tile; the equipped one is named so on its tile.
    internal static bool IsSelectable(LocalShopProduct product) => product.Kind == "HubTheme" || product.Kind == "Board" || product.Kind == "Pet";
    internal static bool Equipped(LocalProfile profile, LocalShopProduct product) =>
        product.Kind == "HubTheme" ? profile.SelectedHubTheme == product.HubTheme
        : product.Kind == "Board" ? profile.SelectedBoard == product.Board.Value
        : product.Kind == "Pet" && (product.Pet == null ? profile.EffectiveSelectedPet == null : LocalShop.Default.PetFamily(profile.EffectiveSelectedPet) == product.Pet);
    // Owner 2026-10-28: the native Owned badge already marks owned tiles (a click equips them), so only the equipped tile
    // carries a second line; every other tile shows its name alone.
    internal static string TileName(LocalShopProduct product, bool owned, bool equipped) =>
        product.Description + (equipped ? "\nEquipped" : "");
    // price is the pre-kit quote; kitId (a ready-made deck only) makes the ledger redeem that kit in the same commit.
    internal LocalPurchaseReceipt Commit(LocalProfile profile, long transaction, long product, string currency, int quantity, long price, string kitId = null)
    {
        if (transaction <= 0 || currency != "XSH" || quantity != 1 || price < 0 || price > int.MaxValue)
            throw new InvalidOperationException("Invalid local gold purchase.");
        var local = Product(product);
        // The ledger checks expected price and funds atomically, and replays receipts before checking the new balance.
        return profile.Purchase(local.Id, quantity, (int)price, transaction.ToString(System.Globalization.CultureInfo.InvariantCulture), kitId);
    }
#endif
}

#if !NATIVE_SHOP_POLICY_TEST || NATIVE_SHOP_LEDGER_TEST
// #57 deck kits in the native Shop (owner 2026-10-28). Ordering a ready-made deck while an unredeemed kit exists asks
// first: USE KIT / PAY FULL, Escape cancels. The answer covers only the one order it re-places (that product at that
// pre-kit quote, within Window seconds): the order guard (Dispatch) binds it to that order, Buy takes it and the ledger
// commit redeems the kit (LocalProfile.Purchase kitId). An order that dies on the way leaves nothing for a later click.
internal sealed class NativeShopKitChoice
{
    internal const float Window = 30f;
    private long askedPmt, chosenPmt, orderPmt;
    private int askedPrice, askedCap, chosenPrice, chosenCap, orderPrice, orderAmount;
    private string askedKit, chosenKit, orderKit;
    private float chosenAt, orderAt;
    private bool chosen, ordered, orderDiscounted;

    internal bool Asking => askedKit != null;
    // The kit a ready-made deck order would offer, or null (other kinds, free quotes, no unredeemed kit).
    internal static LocalDeckKit Offer(LocalProfile profile, LocalShopProduct product, int price) =>
        product.Kind != "Deck" || price <= 0 || profile.DeckPurchaseBlocker(product.RecipeId) != null ? null : profile.DeckKitFor(price);
    internal static int GoldDue(int price, int kitGoldCap) => Math.Max(0, price - kitGoldCap);
    // #57 review: a kit badge means the kit can be used now: some offered ready-made deck takes it (a priced deck the kit
    // covers) and its personal deck fits or can be completed in place. All-cards saves (every deck free) show none.
    internal static bool KitUsable(LocalProfile profile, IEnumerable<LocalShopProduct> offered, string deckBlocker) =>
        profile != null && profile.HasUnredeemedDeckKit
        && offered.Any(product => product.Kind == "Deck" && (deckBlocker == null || !profile.DeckPurchaseNeedsSlot(product.RecipeId))
            && KitTakes(profile, product, profile.PurchasePrice(product.Id)));
    private static bool KitTakes(LocalProfile profile, LocalShopProduct product, int price) =>
        Offer(profile, product, price) != null && !NativeShopPurchase.Owned(profile, product, price);
    // Live 2026-10-28: the native deck page greys and refuses Buy below its displayed price (asset-side wallet binding).
    // So a ready-made deck with an unredeemed kit is quoted natively at its after-kit amount (tile, page, Prices[0]);
    // the ledger keeps the pre-kit price (expectedPrice) and gets the kit id at commit.
    internal static int NativeQuote(LocalProfile profile, LocalShopProduct product, int fullPrice)
    {
        var kit = Offer(profile, product, fullPrice);
        return kit == null ? fullPrice : GoldDue(fullPrice, kit.GoldCap);
    }
    internal void Ask(long pmt, int price, LocalDeckKit kit)
    {
        if (kit == null) throw new ArgumentNullException(nameof(kit));
        Cancel();
        askedPmt = pmt; askedPrice = price; askedKit = kit.Id; askedCap = kit.GoldCap;
    }
    // Returns the gold the re-placed order will charge (after the kit when it is used).
    internal int Answer(bool useKit, float now)
    {
        if (!Asking) throw new InvalidOperationException("No deck kit question is open.");
        chosen = true; chosenPmt = askedPmt; chosenPrice = askedPrice; chosenAt = now;
        chosenKit = useKit ? askedKit : null; chosenCap = useKit ? askedCap : 0;
        askedKit = null;
        return GoldDue(chosenPrice, chosenCap);
    }
    internal void Cancel() { askedKit = null; chosen = false; chosenKit = null; ordered = false; orderKit = null; }
    // An answer waits for its re-placed order (LocalPurchase skips the question for it).
    internal bool Answered(long pmt, int price, float now) =>
        chosen && chosenPmt == pmt && chosenPrice == price && now >= chosenAt && now - chosenAt < Window;
    // Gold this order charges: after the chosen kit, else the full quote.
    internal int Due(long pmt, int price, float now) => Answered(pmt, price, now) ? GoldDue(price, chosenCap) : Ordered(pmt, price, now) ? orderAmount : price;
    private bool Ordered(long pmt, int price, float now) => ordered && orderPmt == pmt && orderPrice == price && now >= orderAt && now - orderAt < Window;
    // Order guard: every candidate order passes here. price is the ledger (pre-kit) price, quote the native one. The
    // answered order must carry exactly the amount its answer charges; the answer then moves onto that order and is gone
    // for any later click. An unanswered order clears any earlier binding and is accepted only at an unkitted quote.
    internal bool Dispatch(long pmt, int price, int quote, double amount, float now)
    {
        if (!Answered(pmt, price, now)) { ordered = false; orderKit = null; return quote == price && amount == price; }
        int due = GoldDue(chosenPrice, chosenCap);
        chosen = false;
        if (amount != due) { chosenKit = null; return false; }
        ordered = true; orderPmt = pmt; orderPrice = price; orderKit = chosenKit; orderAt = now; orderAmount = due;
        orderDiscounted = chosenKit != null; chosenKit = null;
        return true;
    }
    // Buy consumes the bound order: the kit id to redeem, or null (paid in full or never asked). A discounted order
    // whose binding was lost or expired is refused: it is never charged in full.
    internal string Take(long pmt, int price, float now) => Take(pmt, price, now, out _);
    // bound: the order carried an answered kit question (USE KIT or PAY FULL).
    internal string Take(long pmt, int price, float now, out bool bound)
    {
        bound = Ordered(pmt, price, now);
        bool orphan = !bound && ordered && orderDiscounted && orderPmt == pmt;
        string kit = bound ? orderKit : null;
        chosen = false; chosenKit = null; ordered = false; orderKit = null;
        if (orphan) throw new InvalidOperationException("The deck kit order lost its answer; nothing was charged.");
        return kit;
    }
}
#endif

internal sealed class NativeShopConfirmation
{
    private readonly float deadline;
    private readonly Action<bool, object> respond;
    private readonly Action cleanup;
    private bool completed;
    internal NativeShopConfirmation(float deadline, Action<bool, object> respond, Action cleanup)
    { this.deadline = deadline; this.respond = respond; this.cleanup = cleanup; }
    internal void Tick(float now, bool active)
    { if (!active || now >= deadline) Complete(false, null); }
    internal void Complete(bool confirm, object data)
    {
        if (completed) return;
        completed = true; // Reserve before invoking native callbacks, including reentrant/late delivery.
        bool delivered = false;
        try { respond(confirm, data); delivered = true; }
        finally { if (!confirm || !delivered) cleanup(); }
    }
}

// Owner 2026-10-25: a fast second pack-tile click within this many unscaled seconds of the native purchase-complete
// window opening, or of its acknowledgement, does nothing (no order, transaction, debit or popup).
internal static class NativeShopClicks
{
    internal const float PackCooldownSeconds = 0.5f;
    internal static bool PackCoolingDown(float now, float purchaseWindowAt) => now - purchaseWindowAt < PackCooldownSeconds;
    // Pinned PegUI.IsDragAmountAboveDragTolerance, applied to this tile's own press position (dpi = PegUI's
    // m_dragToleranceDpiAdjustmentFactor). A zero axis tolerance never counts as a drag, as in PegUI.
    internal static bool BeyondDragTolerance(float dx, float dy, float toleranceX, float toleranceY, float dpi) =>
        toleranceX != 0f && Math.Abs(dx) > Math.Abs(toleranceX * dpi) || toleranceY != 0f && Math.Abs(dy) > Math.Abs(toleranceY * dpi);
}

// One UI read checks hundreds of elements in one frame; catalogue and page identity are the same for all of them,
// so they are computed once per frame, shop, page, selected variant and catalogue version. A failing check is
// remembered for that key too: every element fails closed and the refusal is reported once, not per element.
internal sealed class NativeShopControlCache
{
    private int frame = -1, version;
    private object shop, page, variant;
    private bool catalogue, localVariant, failed;
    internal bool Resolve(int frame, object shop, object page, object variant, int version,
        Func<bool> computeCatalogue, Func<bool> computeVariant, Action<Exception> failure, out bool localVariant)
    {
        if (frame != this.frame || version != this.version || !ReferenceEquals(shop, this.shop)
            || !ReferenceEquals(page, this.page) || !ReferenceEquals(variant, this.variant))
        {
            this.frame = frame; this.version = version; this.shop = shop; this.page = page; this.variant = variant;
            catalogue = this.localVariant = false; failed = true; // A throwing check leaves a failed, not half-built, entry.
            try { catalogue = computeCatalogue(); this.localVariant = variant != null && computeVariant(); failed = false; }
            catch (Exception error) { catalogue = this.localVariant = false; failure(error); }
        }
        localVariant = !failed && this.localVariant;
        return !failed && catalogue;
    }
}

// Shop open rebuilds every native quote, bundle and deck text (RefreshPrices) only when an input changed since the last
// rebuild that completed: the profile, its committed ledger (replaced by every commit), the shop instance, the native
// product service or the catalogue version (bumped by every seed, reseed and rebuild, so a rebuild after this one, or a
// reseed, always forces the next). A rebuild that throws midway never marks itself fresh.
internal sealed class NativeShopRefreshState
{
    private object profile, ledger, shop, service;
    private int version;
    private bool fresh;
    internal void Invalidate() { fresh = false; profile = ledger = shop = service = null; }
    internal void Complete(object profile, object ledger, object shop, object service, int version)
    { this.profile = profile; this.ledger = ledger; this.shop = shop; this.service = service; this.version = version; fresh = true; }
    internal bool IsFresh(object profile, object ledger, object shop, object service, int version) => fresh && version == this.version
        && ReferenceEquals(profile, this.profile) && ReferenceEquals(ledger, this.ledger) && ReferenceEquals(shop, this.shop)
        && ReferenceEquals(service, this.service);
}

// Test-control reveal of an off-screen local shop tile: when the press may scroll the shop first.
internal static class NativeShopReveal
{
    internal const int MaxAttempts = 3, SettleFrames = 20, StableFrames = 3;
    // Started: the native call began a scroll (an attempt). Pending: the browser is still loading or positioning
    // sections, so neither the tile position nor the scroll height is final; the press waits without spending an
    // attempt. InView: the native call found the tile already inside its scroll bounds. Refused: anything else.
    internal enum Outcome { Refused, Pending, InView, Started }
    // Fully inside the 1280x720 normalized screen; a partly clipped or unlaid-out tile needs a reveal.
    internal static bool FullyVisible(bool onScreen, float x, float y, float w, float h) =>
        onScreen && w > 0 && h > 0 && x >= 0 && y >= 0 && x + w <= 1280f && y + h <= 720f;
    // Only a tile that is not fully visible: a visible tile that is not hit is covered (page, purchase, dialog), and
    // scrolling the browser beneath the cover would be blind input. Live 2026-10-25: a first reveal during layout
    // overshot (the tile moved once sections settled), so a retry re-centres only after the last scroll had its settle
    // frames and the tile's rect has stopped changing (scroll tween and section layout done).
    internal static bool ShouldReveal(string id, bool onScreen, float x, float y, float w, float h, int stableFrames,
        int attempts, int frame, int lastFrame) =>
        id != null && id.StartsWith("shop.product.", StringComparison.Ordinal) && attempts < MaxAttempts
        && (attempts == 0 || frame - lastFrame >= SettleFrames && stableFrames >= StableFrames) && !FullyVisible(onScreen, x, y, w, h);
    // Pinned Shop.MoveToProductCoroutine centres a slot only once the browser is ready and not dirty.
    internal static bool LaidOut(bool browserReady, bool browserDirty) => browserReady && !browserDirty;
    // Live 2026-10-25 (lane 1): at idle on the Decks and Hero Skins tabs that state never arrived, so the press timed out
    // with no reveal. The native wait is unbounded (pinned ShopTabController.SendShopVisitTelemetryCoroutine gives up on
    // the same wait after 20 s). After LayoutWaitFrames pending frames the press reveals anyway, once the tile has a
    // real rect that has stayed still for StableFrames; the usual bounded attempts, settle frames and cover checks apply.
    internal const int LayoutWaitFrames = 30;
    internal static bool LayoutWaitOver(int pendingSince, int frame, int stableFrames, float w, float h) =>
        pendingSince >= 0 && frame - pendingSince >= LayoutWaitFrames && stableFrames >= StableFrames && w > 0 && h > 0;
}

// Pinned ProductSectionExtensions.GetLayoutMapping: rows = clamp(ceil(products / slots per row), 0, max_layout_count), and
// ShopSection.LoadSlotsCoroutine loads rows * slots-per-row tiles. Every tile past the products is a native "Reserved /
// Something new is coming!" teaser (ShopSection.SetReservedSlotDataModel). Local sections never show it (owner no-FOMO
// rule): the empty tile is disabled before positioning, so a short last row keeps native tile size and position.
internal static class NativeShopLayout
{
    // One tile per deck: standardsingle fits four decks per row; standard fits two wider tiles.
    internal const string DefaultDeckRowStyle = "standardsingle";
    internal static string DeckRowStyle = DefaultDeckRowStyle;
    internal static void ConfigureDeckRowStyle(string value, Action<string> log)
    {
        DeckRowStyle = value == "standard" || value == "standardsingle" ? value : DefaultDeckRowStyle;
        if (value != "standard" && value != "standardsingle")
            log?.Invoke("[NativeShop] invalid DeckRowStyle '" + value + "'; using " + DefaultDeckRowStyle);
    }
    // Native section labels only show through a divider (pinned ProductCatalog: DisplayDivder when divider_options are
    // set; retail expansion rows use header_plate). DeckSectionDividers patches the native iterator's between-section
    // guard so the first local deck section also gets its own divider. "none" = no dividers.
    internal const string DefaultDeckHeaders = "header_plate";
    internal static string DeckHeaders = DefaultDeckHeaders;
    // Native splits on "," without trimming: normalise to lower-case, trimmed, non-empty options.
    internal static void ConfigureDeckHeaders(string value)
    {
        var options = (value ?? "").Split(',').Select(option => option.Trim().ToLowerInvariant()).Where(option => option.Length != 0).ToArray();
        DeckHeaders = options.Length == 0 ? DefaultDeckHeaders : options.Contains("none") ? "none" : string.Join(",", options);
    }
    internal const int MaxRows = 3; // Pinned SectionAttributes: standard, standardsingle and the packs override.
    // Pinned layout maps: standardsingle "3x1" x4; standard and default "6x1" x2 (12 units wide).
    internal static int SlotsPerRow(string style) => style == "standardsingle" ? 4 : 2;
    internal static int Capacity(string style) => SlotsPerRow(style) * MaxRows;
    internal static int Rows(int products, int slotsPerRow, int maxRows) =>
        Math.Max(0, Math.Min(maxRows, (products + slotsPerRow - 1) / slotsPerRow));
    internal static int EmptySlots(int products, int slotsPerRow, int maxRows) =>
        Rows(products, slotsPerRow, maxRows) * slotsPerRow - Math.Min(products, Rows(products, slotsPerRow, maxRows) * slotsPerRow);
    // Sizes of the consecutive native sections one product list is split into (each within one section's capacity).
    internal static IEnumerable<int> Chunks(int products, string style)
    {
        for (int offset = 0; offset < products; offset += Capacity(style)) yield return Math.Min(Capacity(style), products - offset);
    }
    internal static bool HideEmptySlot(string sectionId, bool empty) =>
        empty && sectionId != null && sectionId.StartsWith("local-", StringComparison.Ordinal);
    // ShopTabController keeps a fixed row of sub-tab slots; slots past the active tab's sub-tabs only get HIDE_TAB,
    // which the shop prefab draws as empty grey buttons. Local shop: those slots are deactivated.
    internal static bool UnusedSubTab(int index, int used) => index >= used;
    // A positioned leading divider sits on the first section, at most one divider height above it; a divider left at
    // FetchDivider's staging z (its section failed to load) is far above and is not counted.
    internal static float LeadingDividerGap(float firstSectionTop, float dividerMin, float dividerMax) =>
        dividerMax > firstSectionTop && dividerMin >= firstSectionTop && dividerMin - firstSectionTop <= dividerMax - dividerMin
            ? dividerMax - firstSectionTop : 0f;
    // Live 2026-10-25: deactivating ShopSubTab.Widget left the grey plate drawn; the plate belongs to the bar's slot
    // container above the sub-tab widget. Each slot's root is its highest ancestor that holds no other slot and not the
    // controller, so the plate goes with its button. The climb must end under an ancestor shared with another slot or the
    // controller (never a scene root); otherwise that slot's root is null and the caller refuses.
    internal static List<T> ExclusiveRoots<T>(IList<T> slots, Func<T, T> parent, T stop) where T : class
    {
        var chains = slots.Select(slot => Chain(slot, parent)).ToList();
        var stopChain = new HashSet<T>(Chain(stop, parent));
        var holders = new Dictionary<T, int>(); // Node -> how many slot chains pass through it (per frame while open).
        foreach (var node in chains.SelectMany(chain => chain.Distinct()))
            holders[node] = holders.TryGetValue(node, out int count) ? count + 1 : 1;
        Func<T, bool> shared = node => stopChain.Contains(node) || holders.TryGetValue(node, out int count) && count > 1;
        var roots = new List<T>();
        for (int i = 0; i < chains.Count; i++)
        {
            T root = null;
            foreach (T node in chains[i]) { if (shared(node)) break; root = node; }
            T above = root == null ? null : parent(root);
            roots.Add(above != null && shared(above) ? root : null);
        }
        return roots;
    }
    internal static bool OnChain<T>(T node, T ancestor, Func<T, T> parent) where T : class => Chain(node, parent).Contains(ancestor);
    private static List<T> Chain<T>(T node, Func<T, T> parent) where T : class
    {
        var chain = new List<T>();
        for (int depth = 0; node != null && depth < 64; depth++, node = parent(node)) chain.Add(node);
        return chain;
    }
}

// Owner 2026-10-29: Cards (packs), Decks I/II (six/five class sub-tabs), Skins and Themes.
// Ids are stable test-control names (shop.tab.<id>). Icons are keys of the pinned ShopMainTabs switcher
// (FEATURED, HS, BG, OTHER, RS); NativeShopTabIcons may swap in a loaded sprite or texture over them.
internal static class NativeShopTabs
{
    internal const string Cards = "cards", Decks1 = "decks_1", Decks2 = "decks_2", Skins = "skins", Themes = "themes";
    internal static readonly string[] Ids = { Cards, Decks1, Decks2, Skins, Themes };
    internal static readonly (string Tab, string Id, string Hero, string Label)[] DeckClasses = {
        (Decks1, "deck_deathknight", "HERO_11", "Death Knight"),
        (Decks1, "deck_demonhunter", "HERO_10", "Demon Hunter"),
        (Decks1, "deck_druid", "HERO_06", "Druid"),
        (Decks1, "deck_hunter", "HERO_05", "Hunter"),
        (Decks1, "deck_mage", "HERO_08", "Mage"),
        (Decks1, "deck_paladin", "HERO_04", "Paladin"),
        (Decks2, "deck_priest", "HERO_09", "Priest"),
        (Decks2, "deck_rogue", "HERO_03", "Rogue"),
        (Decks2, "deck_shaman", "HERO_02", "Shaman"),
        (Decks2, "deck_warlock", "HERO_07", "Warlock"),
        (Decks2, "deck_warrior", "HERO_01", "Warrior")
    };
    internal static readonly string[] SubTabs = new[] { "packs" }.Concat(DeckClasses.Select(c => c.Id))
        .Concat(new[] { "hero_skins", "card_backs", "coin_list", "pet_list", "board_list", "theme_list" }).ToArray();
    internal static readonly HashSet<string> KitBadgeTabs = new HashSet<string> { Decks1, Decks2 }; // #57: category badges only
    // Five distinct framed atlas glyphs (live 2026-10-29: loaded non-atlas icons lose the frame). Cards: Hearthstone
    // swirl. Decks I: Battlegrounds glyph. Decks II: Runestones glyph. Skins: star (Featured). Themes: crossed weapons (Modes).
    internal static string Icon(string tab) => tab == Cards ? "hs" : tab == Decks1 ? "bg" : tab == Decks2 ? "rs" : tab == Skins ? "featured" : "other";
    // The category the shop opens on: the first one with products (BuildSections omits empty ones). deckHeroes: heroes of
    // the offered decks and recipes; without them a deck offer is assumed to fill Decks I.
    internal static string OpenTab(IEnumerable<string> kinds, IEnumerable<string> deckHeroes = null)
    {
        var present = new HashSet<string>(kinds ?? Enumerable.Empty<string>());
        if (present.Contains("Pack")) return Cards;
        if (present.Overlaps(new[] { "Deck", "Recipe" }))
            return deckHeroes == null || deckHeroes.Any(hero => DeckClasses.Any(c => c.Tab == Decks1 && c.Hero == hero)) ? Decks1 : Decks2;
        if (present.Overlaps(new[] { "HeroSkin", "CardBack", "Coin", "Pet" })) return Skins;
        return present.Overlaps(new[] { "Board", "HubTheme" }) ? Themes : Cards;
    }
}

// Binding parameter names use metadata spelling: "X", "X[]" (any depth) and one outermost by-ref "X&" / "X[]&".
// CLR has no by-ref of by-ref or array of by-ref, so "X&&" and "X&[]" are rejected.
// Shared by runtime registration and the offline binding checks so both resolve names identically.
internal static class NativeBindingTypeNames
{
    internal static T Resolve<T>(string name, Func<string, T> named, Func<T, T> byRef, Func<T, T> array)
    {
        if (string.IsNullOrEmpty(name)) throw new TypeLoadException("Empty binding type name.");
        bool isByRef = name.EndsWith("&", StringComparison.Ordinal);
        string element = isByRef ? name.Substring(0, name.Length - 1) : name;
        int rank = 0;
        while (element.EndsWith("[]", StringComparison.Ordinal)) { element = element.Substring(0, element.Length - 2); rank++; }
        if (element.Length == 0 || element.IndexOfAny(new[] { '&', '[', ']', '<', '>', '*', ',' }) >= 0)
            throw new TypeLoadException("Unsupported binding type name: " + name);
        T type = named(element);
        for (int i = 0; i < rank; i++) type = array(type);
        return isByRef ? byRef(type) : type;
    }
}

#if !NATIVE_SHOP_POLICY_TEST
internal static class NativeShop
{
    private const string PageId = "solostone-local";
    private const float Timeout = 30f;
    private static ManualLogSource log;
    private static ConfigEntry<bool> option;
    private static ConfigEntry<string> tabIcons, tileArt, cradleMode, deckRowStyle, deckHeaders, recipeTilePrice;
    private static bool installed, failed, building, ready, opening, successSent, resultShown, committed, seeded;
    private static float began, openBegan, purchaseBegan, purchaseWindowAt = float.NegativeInfinity;
    private static long sequence;
    private static string purchaseFailure;
    private static ProductDataService products;
    private static PurchaseAuthWindow auth;
    private static Transaction transaction;
    private static object collectionPurchase;
    private static Box openingBox;
    private static Box.State previousBoxState;
    private static AlertPopup.PopupInfo localConfirmation;
    private static NativeShopConfirmation confirmation;
    private static long purchasePrice;
    // #57: kit question popup (USE KIT / PAY FULL), whether its PAY FULL button was pressed, and the kit Buy took.
    private static readonly NativeShopKitChoice kits = new NativeShopKitChoice();
    private static AlertPopup.PopupInfo kitQuestion;
    private static AlertPopup kitHooked;
    private static readonly NativeShopKitRelease kitRelease = new NativeShopKitRelease();
    private static ProductDataModel kitProduct;
    private static string purchaseKit;
    private static string themeNoticeAfterPurchase;
    private static LocalPurchaseReceipt purchaseReceipt;
    private static readonly NativeShopPurchase purchases = new NativeShopPurchase();
    private static readonly HashSet<BuyProductEventArgs> requests = new HashSet<BuyProductEventArgs>();
    private static readonly HashSet<string> reasons = new HashSet<string>();
    private static LocalProfile Profile => NativeUIProbe.RewardProfile;
    private static bool Isolated => NativeUIProbe.IsolatedLaunch;
    private static bool Enabled => Isolated && installed && option?.Value == true && !failed;

    internal static bool HomeStoreReady => Enabled && ready;
    internal static bool Active => Isolated && Enabled && ready;
    internal static bool IsReady => HomeStoreReady;
    internal static bool IsActive => Active;
    internal static string ControllerAction(PegUIElement control)
    {
        var shown=control?.GetComponentInParent<ShopSlot>()?.BrowserButtonDataModel?.DisplayProduct;
        if(shown==null || !NativeShopPurchase.TryProduct(shown.PmtId,out var local)) return "View product";
        if(NativeShopPurchase.IsSelectable(local) && NativeShopPurchase.Owned(Profile,local,0)) return "Equip";
        return local.Kind=="Pack" || NativeShopPurchase.IsSelectable(local) ? "Buy with gold" : "View product details";
    }
    internal static object ControllerTransaction => transaction;
#if !SOLOSTONE_RELEASE
    internal static StorePurchaseAuth ControllerAuth => auth == null ? null : Read(auth,"m_purchaseAuth") as StorePurchaseAuth;
#endif
    internal static string ControllerContext => NativeShopPurchase.CatalogueVersion + ":" + transaction?.ID + ":" + resultShown + ":" + committed
        + ":" + (localConfirmation?.m_id ?? "") + ":" + (kitQuestion?.m_id ?? "") + ":" + opening;
    internal static bool PresentationBusy => opening || confirmation != null || auth != null || transaction != null || kitQuestion != null;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        if (!Isolated) return;
        InstallHooks(harmony);
        // Diagnostic bindings resolve on use and deny only that control; isolation bindings remain mandatory.
        foreach (var row in ReflectionBindings.Where(row => row[5] != "ui")
            .GroupBy(row => row[1] + "." + row[2]).Select(group => group.First()))
        {
            string key = row[1] + "." + row[2];
            try { _ = bindings[key].Value; }
            catch (Exception error) { Fail("binding " + key, error); }
        }
        try
        {
            // AddComponent runs Awake before BepInEx publishes PluginInfo.Instance.
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<OfflineProbe>()
                ?? throw new InvalidOperationException("OfflineProbe component unavailable during shop registration");
            option = plugin.Config.Bind("Shop", "NativeShop", Isolated, "Enable the isolated local-gold native shop spike. Never enables retail commerce.");
            // The shop switch above is a player setting and stays; the art and layout knobs below are dev knobs, left out of a public build (I2c).
#if SOLOSTONE_RELEASE
            NativeShopTabIcons.Configure("", message => log?.LogInfo(message));
            NativeShopTileArt.Configure("auto", message => log?.LogInfo(message));
            NativeShopTilePrices.Configure(NativeShopArtMath.DefaultRecipePlate, message => log?.LogInfo(message));
            NativeShopLayout.ConfigureDeckHeaders(NativeShopLayout.DefaultDeckHeaders);
            NativeShopLayout.ConfigureDeckRowStyle(NativeShopLayout.DefaultDeckRowStyle, message => log?.LogInfo(message));
#else
            tabIcons = plugin.Config.Bind("Shop", "TabIcons", "",
                "Shop tab icons: tabId=sprite:<loaded sprite> or tabId=tex:<loaded texture>, ';'-separated, over the built-in choices. none = native icons.");
            NativeShopTabIcons.Configure(tabIcons.Value, message => log?.LogInfo(message));
            tileArt = plugin.Config.Bind("Shop", "TileArt", "auto",
                "Board, theme and No pet tile art: auto = each board's frame and corners, drawn from the installed game and cached locally, and the Hearthstone swirl for the rest; neutral = the swirl everywhere; none = native ticket tiles.");
            NativeShopTileArt.Configure(tileArt.Value, message => log?.LogInfo(message));
            // Renamed from RecipeTilePrice (2026-10-30) so live-test overrides of the first layout don't mask the default.
            recipeTilePrice = plugin.Config.Bind("Shop", "RecipeTilePlate", NativeShopArtMath.DefaultRecipePlate,
                "Recipe price above each deck tile's ready-made price (a copy of its gold coin plate): none, or ';'-separated lift (levels from the price text up to the copied plate), x / y (offset in native plate widths / heights), scale and text ({0} = price). Applies at the next shop open.");
            NativeShopTilePrices.Configure(recipeTilePrice.Value, message => log?.LogInfo(message));
            cradleMode = plugin.Config.Bind("Shop", "SubTabCradle", "hide",
                "hide = no sub-tab cradle (its empty grey sockets) when a category has fewer sub-tabs than sockets; native = keep it.");
            deckRowStyle = plugin.Config.Bind("Shop", "DeckRowStyle", NativeShopLayout.DefaultDeckRowStyle,
                "Test knob: one tile per deck; standardsingle = four decks per row (default); standard = two decks per row (wider tiles). Applies when the shop catalogue is next built (restart the game).");
            deckHeaders = plugin.Config.Bind("Shop", "DeckHeaders", NativeShopLayout.DefaultDeckHeaders,
                "Test knob: native divider options above each Meta / Fan favourite deck group (header_plate, watermark, empty, comma-separated); none = no header. Applies at the next game start.");
            NativeShopLayout.ConfigureDeckHeaders(deckHeaders.Value);
            NativeShopLayout.ConfigureDeckRowStyle(deckRowStyle.Value, message => log?.LogInfo(message));
            deckRowStyle.SettingChanged += (_, __) => NativeShopLayout.ConfigureDeckRowStyle(deckRowStyle.Value, message => log?.LogInfo(message));
#endif
            NativeShopPurchase.PersonalDeckBlockerSeam = _ => NativeUIProbe.PersonalDeckBlocker();
            UiRules.LocalShopControlAllowed = element => {
                try { return AllowControl(element); }
                catch (Exception error) { Refuse("control identity: " + (error.InnerException ?? error).Message); return false; }
            };
            installed = !failed;
            if (!option.Value) Trace("disabled by Shop.NativeShop");
        }
        catch (Exception error) { Fail("registration", error); }
    }

    // kind, declaring type (nested types use +), member, parameter types (* = unique name), hook, phase.
    // Phase ui means optional diagnostic binding, resolved on use. Safety hooks stay first.
    // One contract drives runtime resolution and the Mac source/metadata checks.
    private static readonly string[][] ReflectionBindings =
    {
        new[] { "Method", "CardBackManager", "GetCollectionManagerCardBackPurchaseProductId", "System.Int32", "CollectionCardBackProduct", "" },
        new[] { "Method", "HeroSkinUtils", "GetCollectionManagerHeroSkinPurchaseProductData", "System.String", "CollectionHeroSkinProduct", "" },
        new[] { "Field", "CardBackInfoManager", "m_currentCardBackIdx", "*", "", "" },
        new[] { "Field", "CardBackInfoManager", "m_isStoreTransactionActive", "*", "", "" },
        new[] { "Method", "CardBackInfoManager", "OnFailedPurchaseAck", "Hearthstone.Store.ProductInfo;PaymentMethod", "", "" },
        new[] { "Field", "BaseHeroSkinInfoManager", "m_currentEntityDef", "*", "", "" },
        new[] { "Field", "BaseHeroSkinInfoManager", "m_isStoreTransactionActive", "*", "", "" },
        new[] { "Method", "BaseHeroSkinInfoManager", "OnFailedPurchaseAck", "Hearthstone.Store.ProductInfo;PaymentMethod", "", "" },
        new[] { "Field", "StoreManager", "OnSuccessfulPurchase", "*", "", "" },
        new[] { "Method", "ShopTabController", "BuildTabData", "Hearthstone.DataModels.ShopDataModel", "LocalTabs", "postfix" },
        // Every class: an offered Deck and Recipe share the Deck page through native variants.
        new[] { "Method", "Game.Shop.VariantUtils", "ResolveVariants", "*", "DeckVariants", "postfix" },
        new[] { "Method", "SellableDeckProductPage", "GetFirstVariantToDisplay", "*", "DeckFirstVariant", "postfix" },
        new[] { "Field", "SellableDeckProductPage", "m_firstVariant", "*", "", "" },
        // #57: native new-items badges (hub Store button, Decks categories) mean "an unredeemed deck kit".
        new[] { "Method", "ShopButtonStatus", "HasNewItems", "*", "StoreKitBadge", "" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "UpdateTabNotificationStates", "*", "KitTabBadges", "postfix" },
        new[] { "Method", "Hearthstone.Store.ProductInfo", "UpdatePrices", "Network+Bundle", "LocalPrice", "postfix" },
        new[] { "Field", "Hearthstone.Store.ProductInfo", "m_vcPrices", "*", "", "" },
        new[] { "Property", "Hearthstone.Store.ProductInfo", "Description", "*", "", "" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "TryGetProduct", "System.Int64;Hearthstone.Store.ProductInfo&", "", "" },
        new[] { "Method", "Blizzard.Commerce.CommerceWrapper", "get_Instance", "*", "BlockCommerce", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendShopBalanceAvailable", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendShopCardClick", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendShopProductDetailOpened", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendShopPurchaseEvent", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendShopStatus", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendShopVisit", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendBlizzardCheckoutInitializationResult", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendBlizzardCheckoutIsReady", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendBlizzardCheckoutPurchaseCancel", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendBlizzardCheckoutPurchaseCompletedFailure", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendBlizzardCheckoutPurchaseCompletedSuccess", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendBlizzardCheckoutPurchaseStart", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendPurchaseCancelClicked", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendPurchasePayNowClicked", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendLoadProducts", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendFlowPerformance", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendFlowPerformanceShop", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneTelemetry.TelemetryClient", "SendCosmeticsRenderingFallback", "*", "SkipIsolated", "" },
        new[] { "Method", "Hearthstone.Attribution.BlizzardAttributionManager", "SendEvent_FirstShopVisit", "*", "SkipIsolated", "" },
        new[] { "Method", "Hearthstone.Attribution.BlizzardAttributionManager", "SendEvent_Purchase", "*", "SkipIsolated", "" },
        new[] { "Method", "Hearthstone.Attribution.BlizzardAttributionManager", "SendEvent_VirtualCurrencyTransaction", "*", "SkipIsolated", "" },
        new[] { "Method", "Hearthstone.CRM.BlizzardCRMManager", "SendEvent_RealMoneyTransaction", "*", "SkipIsolated", "" },
        new[] { "Method", "Hearthstone.CRM.BlizzardCRMManager", "SendEvent_VirtualCurrencyTransaction", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneCheckout", "Initialize", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "Update", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneCheckout", "Shutdown", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneCheckout", "LoadPersonalizedShopData", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneCheckout", "ProcessDeeplink", "*", "SkipIsolated", "" },
        new[] { "Method", "HearthstoneCheckout", "ShowCheckout", "*", "BlockCheckout", "" },
        new[] { "Method", "HearthstoneCheckout", "PurchaseWithVirtualCurrency", "*", "BlockCheckout", "" },
        new[] { "Method", "HearthstoneCheckout", "CancelCurrentTransaction", "*", "BlockCheckout", "" },
        new[] { "Method", "HearthstoneCheckout", "RequestClose", "*", "BlockCheckout", "" },
        new[] { "Method", "HearthstoneCheckout", "LoadProducts", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "GetPersonalizedShopData", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "Job_CreateCSDK", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "Job_GenerateSSOToken", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "Job_InitializeCheckoutClient", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "Job_ShowCheckout", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "Job_PurchaseWithVirtualCurrency", "*", "CheckoutInitialize", "" },
        new[] { "Method", "HearthstoneCheckout", "GetVirtualCurrencyBalance", "*", "BlockBalance", "" },
        new[] { "Method", "HearthstoneCheckout", "ResumeCheckout", "*", "EmptyCoroutine", "" },
        new[] { "Method", "HearthstoneCheckout", "GetDependencies", "*", "CheckoutDependencies", "" },
        new[] { "Method", "HearthstoneCheckout", "IsAvailable", "*", "CheckoutUnavailable", "" },
        new[] { "Method", "Blizzard.GameService.SDK.Client.Integration.BattleNet", "GenerateAppWebCredentials", "*", "BlockCredentials", "" },
        new[] { "Method", "Network", "FlushVirtualCurrencyPending", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "ReportBlizzardCheckoutStatus", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "CancelBlizzardPurchase", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "ConfirmPurchase", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "MercenariesPlayerInfoRequest", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "MercenariesCollectionRequest", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "MercenariesTeamListRequest", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "MercenariesMythicTreasureScalarsRequest", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "RequestLuckyDrawBoxState", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "RequestLuckyDrawBoxStateInGracePeriod", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "SendGetBlackMarketPlayerStateRequest", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "SendGetBlackMarketItemListRequest", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "RequestAccountLicensesUpdate", "*", "SkipIsolated", "" },
        new[] { "Method", "Network", "AckNotice", "*", "SkipIsolated", "" },
        new[] { "Method", "CollectionManager", "StartInitialMercenaryLoadIfRequired", "*", "SkipIsolated", "" },
        new[] { "Method", "StoreManager", "Load", "*", "LoadStore", "" },
        new[] { "Method", "StoreManager", "StartStoreBuy", "*", "Buy", "" },
        new[] { "Method", "PurchaseManager", "Internal_PurchaseProductCoroutine", "*", "GuardPurchase", "" },
        new[] { "Method", "PurchaseManager", "PurchaseProduct", "Hearthstone.DataModels.ProductDataModel;Hearthstone.DataModels.PriceDataModel;PurchaseManager+PurchaseManagerOptions;System.Int32", "LocalPurchase", "" },
        new[] { "Method", "PurchaseManager", "Internal_PurchaseProduct", "*", "ClearFinishedPurchase", "postfix" },
        new[] { "Method", "ShopSlot", "OnRelease", "*", "BuyPackTile", "" },
        new[] { "Method", "Hearthstone.UI.Clickable", "OnRelease", "*", "NoteDragRelease", "" },
        new[] { "Field", "Hearthstone.UI.Clickable", "m_dragged", "*", "", "" },
        new[] { "Field", "Hearthstone.UI.Clickable", "m_pegUiElement", "*", "", "" },
        new[] { "Method", "Hearthstone.UI.Clickable", "OnClick", "*", "NoteTilePress", "" }, // PRESS listener: Clickables only.
        new[] { "Method", "Hearthstone.UI.Clickable", "OnReleaseAll", "*", "ClearTilePress", "postfix" },
        new[] { "Method", "Hearthstone.UI.Clickable", "OnRollOut", "*", "ClearTilePress", "postfix" },
        new[] { "Method", "Hearthstone.UI.Clickable", "OnDisable", "*", "ClearTilePress", "postfix" },
        new[] { "Method", "ShopSection", "SetReservedSlotDataModel", "*", "HideEmptySlot", "" },
        new[] { "Method", "ShopBrowser", "LoadSectionsCoroutine", "", "DeckSectionDividers", "iterator-transpiler" },
        new[] { "Method", "ShopTabController", "RebindSubTabData", "*", "ShowUsedSubTabs", "" },
        new[] { "Method", "ShopTabController", "RebindSubTabData", "*", "HideUnusedSubTabs", "postfix" },
        new[] { "Field", "ShopTabController", "m_subTabs", "*", "", "" },
        new[] { "Field", "ShopTabController", "m_tabs", "*", "", "" },
        new[] { "Field", "Shop", "m_shopTabController", "*", "", "ui" }, // Per-frame sub-tab sync and its diagnostic.
        new[] { "Field", "PegUI", "m_dragToleranceDpiAdjustmentFactor", "*", "", "" },
        new[] { "Method", "StoreManager", "IsOpen", "*", "StoreAvailable", "" },
        new[] { "Method", "StoreManager", "ShowStore", "*", "CanShowStore", "" },
        new[] { "Method", "StoreManager", "SendShopPurchaseEventTelemetry", "*", "SkipIsolated", "" },
        new[] { "Method", "ShopInitialization", "StartInitializing", "*", "Bootstrap", "" },
        new[] { "Method", "ShopInitialization", "InitializeShopCoroutine", "*", "EmptyCoroutine", "" },
        new[] { "Method", "StoreManager", "HandleShopAvailabilityChange", "*", "AvailabilityChanged", "" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "CreateDataModels", "*", "SeedCatalog", "" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "RefreshShopPageRequests", "*", "SkipIsolated", "" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "RefreshWebshopWidgetSectionCandidates", "*", "SkipIsolated", "" },
        new[] { "Method", "CatalogUtils", "CanUpdateProductStatus", "*", "CatalogReady", "" },
        new[] { "Method", "ProductCatalog", "UpdateProductStatus", "*", "UpdateAvailability", "" },
        new[] { "Method", "CurrencyManager", "Blizzard.T5.Services.IHasUpdate.Update", "*", "WalletUpdate", "" },
        new[] { "Method", "CurrencyManager+CurrencyCache", "TryRefresh", "*", "RefreshCurrency", "" },
        new[] { "Method", "NativeUIProbe", "Update", "*", "Tick", "postfix" },
        new[] { "Method", "Box", "EnableStoreButtonGO", "*", "StoreButtonVisibility", "" },
        new[] { "Method", "StoreButton", "UpdateState", "*", "StoreButtonState", "" },
        new[] { "Method", "StoreButton", "Load", "*", "", "" },
        new[] { "Method", "StoreButton", "OnStoreStatusChanged", "System.Boolean", "", "" },
        new[] { "Method", "StoreButton", "IsVisualClosed", "*", "", "" },
        new[] { "Method", "Box", "EnableButton", "PegUIElement", "", "" },
        new[] { "Method", "Box", "DisableButton", "PegUIElement", "", "" },
        new[] { "Method", "PegUIElement", "IsEnabled", "UIEventType", "", "" },
        new[] { "Method", "Box", "OnStoreButtonReleased", "*", "Open", "" },
        new[] { "Method", "Box", "OnStoreShown", "*", "HomeStoreShown", "" },
        new[] { "Method", "Box", "OnStoreHidden", "*", "HomeStoreHidden", "" },
        new[] { "Method", "ShopUtils", "TryGetMainVirtualCurrencyType", "*", "NoOtherCurrency", "" },
        new[] { "Method", "ShopUtils", "TryGetBoosterVirtualCurrencyType", "*", "NoOtherCurrency", "" },
        new[] { "Method", "ShopUtils", "IsProductGamemodesAvailable", "*", "LocalGamemode", "" },
        new[] { "Method", "StoreManager", "OnPurchaseResultAcknowledged", "*", "Acknowledge", "" },
        new[] { "Method", "StoreManager", "Job_ShowCompletePurchaseSuccessWhenReady", "*", "SuccessJob", "" },
        new[] { "Method", "ShopPopupSpawner", "CreatePurchaseConfirmPopup", "*", "Confirm", "" },
        new[] { "Method", "PurchaseAuthWindow", "CompletePurchaseSuccess", "*", "SuccessShown", "postfix" },
        new[] { "Method", "PurchaseAuthWindow", "CompletePurchaseFailure", "*", "FailureShown", "postfix" },
        new[] { "Method", "ProductCatalog", "Job_PopulateData", "*", "GuardCatalogJob", "postfix" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "CreateDataModels", "*", "NativeFailure", "finalizer" },
        new[] { "Method", "StoreManager", "ShowStore", "*", "NativeFailure", "finalizer" },
        new[] { "Method", "Shop", "Open", "*", "NativeFailure", "finalizer" },
        new[] { "Property", "ShopInitialization", "Status", "*", "", "" },
        new[] { "Property", "ShopInitialization", "StatusMessage", "*", "", "" },
        new[] { "Method", "StoreManager", "GetCurrentStore", "*", "", "" },
        new[] { "Method", "StoreManager", "LoadGeneralStore", "*", "", "" },
        new[] { "Method", "StoreManager", "FireStatusChangedEventIfNeeded", "*", "", "" },
        new[] { "Method", "StoreManager", "SetCanTapOutConfirmationUI", "*", "", "" },
        new[] { "Method", "StoreManager", "OnAuthExit", "*", "", "" },
        new[] { "Method", "StoreManager", "HandleCommercePurchaseFailure", "*", "", "" },
        new[] { "Method", "StoreManager", "HandlePurchaseSuccess", "*", "", "" },
        new[] { "Property", "StoreManager", "BattlePayAvailable", "*", "", "" },
        new[] { "Property", "StoreManager", "FeaturesReady", "*", "", "" },
        new[] { "Property", "StoreManager", "Status", "*", "", "" },
        new[] { "Field", "StoreManager", "m_pendingProductPurchaseArgs", "*", "", "" },
        new[] { "Field", "StoreManager", "m_activeTransaction", "*", "", "" },
        new[] { "Field", "StoreManager", "m_transactionIDsConclusivelyHandled", "*", "", "" },
        new[] { "Field", "StoreManager", "OnSuccessfulPurchaseAck", "*", "", "" },
        new[] { "Field", "StoreManager", "OnFailedPurchaseAck", "*", "", "" },
        new[] { "Field", "StoreManager", "m_waitingToShowStore", "*", "", "" },
        new[] { "Field", "PurchaseManager+PurchaseOrder", "Product", "*", "", "" },
        new[] { "Field", "PurchaseManager+PurchaseOrder", "Price", "*", "", "" },
        new[] { "Field", "PurchaseManager+PurchaseOrder", "Quantity", "*", "", "" },
        new[] { "Field", "PurchaseManager+PurchaseOrder", "HasFinished", "*", "", "" },
        new[] { "Method", "PurchaseManager", "CancelCurrentPurchase", "*", "", "" },
        new[] { "Field", "PurchaseManager", "m_currentPurchaseCoroutine", "*", "", "" },
        new[] { "Field", "PurchaseManager+PurchaseManagerOptions", "SuppressVcConfirmation", "*", "", "" },
        new[] { "Method", "AssetLoader", "Get", "*", "", "" },
        new[] { "Property", "Hearthstone.Store.ProductDataService", "ProductMap", "*", "", "" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "SetProduct", "Network+Bundle", "", "" },
        new[] { "Field", "Hearthstone.Store.ProductDataService", "m_hasSetPages", "*", "", "" },
        new[] { "Field", "CatalogNetworkPages", "m_pages", "*", "", "" },
        new[] { "Field", "CatalogNetworkPages", "m_placements", "*", "", "" },
        new[] { "Method", "CatalogNetworkPages", "GetOrCreatePage", "*", "", "" },
        new[] { "Field", "com.blizzard.commerce.Model.Placement", "placementId", "*", "", "" },
        new[] { "Field", "com.blizzard.commerce.Model.Placement", "page", "*", "", "" },
        new[] { "Field", "com.blizzard.commerce.Model.Page", "pageId", "*", "", "" },
        new[] { "Field", "ProductCatalog", "m_hasUpdatedProductStatusOnce", "*", "", "" },
        new[] { "Method", "CurrencyManager", "GetCurrencyCache", "*", "", "" },
        new[] { "Method", "CurrencyManager", "UpdateVirtualCurrencyWallet", "*", "", "" },
        new[] { "Field", "CurrencyManager", "m_walletDataModel", "*", "", "" },
        new[] { "Property", "Hearthstone.DataModels.WalletDataModel", "AllowRealMoneyPurchases", "*", "", "" },
        new[] { "Method", "CurrencyManager+CurrencyCache", "UpdateBalance", "*", "", "" },
        // Bottom-bar dust and gold frames (LocalCurrency): optional, so a changed client only loses those displays.
        new[] { "Method", "CurrencyManager+CurrencyCache", "IsCached", "", "", "ui" },
        new[] { "Method", "CurrencyManager", "UpdateWallet", "CurrencyType", "", "ui" },
        new[] { "Property", "CurrencyManager+CurrencyCache", "Type", "*", "", "" },
        new[] { "Field", "CurrencyManager+CurrencyCache", "m_status", "*", "", "" },
        new[] { "Field", "Box", "m_railroadManager", "*", "", "" },
        new[] { "Field", "Box", "m_previousState", "*", "", "" },
        new[] { "Method", "Box", "ChangeState", "*", "", "" },
        new[] { "Method", "BoxRailroadManager", "ShouldDisableButtonType", "*", "", "" },
        new[] { "Method", "NativeUIProbe", "PublishBoosters", "*", "", "" },
        new[] { "Method", "Blizzard.Commerce.ProductId", "CreateFrom", "System.Int64", "", "" },
        new[] { "Method", "Blizzard.Commerce.TransactionData", ".ctor", "Blizzard.Commerce.ProductId;System.String;System.UInt32;System.Boolean", "", "" },
        new[] { "Field", "ShopSlot", "m_clickable", "*", "", "" }, // Pack tile drag guard; also a UI-tree accessor.
        new[] { "Field", "Shop", "m_browserScroller", "*", "", "ui" },
        new[] { "Method", "ShopButtonStatus", "Get", "*", "", "ui" }, // #57 kit badge refresh
        new[] { "Field", "ShopButtonStatus", "m_shopButtonDataModel", "*", "", "ui" },
        new[] { "Method", "Hearthstone.Store.ProductDataService", "GetTabs", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Method", "Hearthstone.Store.ProductDataService", "TryGetSubTabs", "*", "", "ui" },
#endif
        new[] { "Field", "Shop", "m_browser", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "ShopBrowser", "m_sections", "*", "", "ui" },
#endif
        new[] { "Field", "ShopBrowser", "m_activeDividers", "*", "", "ui" },
        new[] { "Field", "ShopDivider", "m_scrollableItem", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "ShopBrowser", "m_loadingCoroutine", "*", "", "ui" },
        new[] { "Field", "ShopBrowser", "m_isLoadingSuppressed", "*", "", "ui" },
#endif
#if !SOLOSTONE_RELEASE
        new[] { "Method", "UIBScrollable", "CenterObjectInView", "*", "", "ui" },
#endif
#if !SOLOSTONE_RELEASE
        new[] { "Field", "DeckTray", "m_scrollbar", "*", "", "ui" },
        new[] { "Field", "DeckTrayDeckListContent", "m_newDeckButton", "*", "", "ui" },
#endif
        new[] { "Field", "DeckTemplatePicker", "m_pickerButtons", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "DeckTemplatePicker", "m_currentSelectedClass", "*", "", "ui" },
#endif
        new[] { "Method", "UIBScrollable", "GetScrollableItemsHeight", "", "BrowserScrollHeight", "postfix" },
        new[] { "Field", "UIBScrollable", "m_scrollableItems", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "UIBScrollable", "m_HeightMode", "*", "", "ui" },
        new[] { "Field", "UIBScrollable", "m_PolledScrollHeight", "*", "", "ui" },
        new[] { "Field", "UIBScrollable", "m_ScrollBottomPadding", "*", "", "ui" },
        new[] { "Field", "UIBScrollable", "m_ScrollBounds", "*", "", "ui" },
#endif
#if !SOLOSTONE_RELEASE
        new[] { "Field", "UIBScrollable", "m_ScrollEaseType", "*", "", "ui" },
#endif
        new[] { "Field", "ProxyButton", "m_target", "*", "", "ui" },
        new[] { "Field", "ShopTab", "m_clickable", "*", "", "ui" },
        new[] { "Field", "ShopSubTab", "m_button", "*", "", "ui" },
        new[] { "Field", "ProductPageController", "m_quantityPrompt", "*", "", "ui" },
        new[] { "Field", "StorePurchaseAuth", "m_transaction", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "PurchaseAuthWindow", "m_purchaseAuth", "*", "", "ui" },
#endif
        new[] { "Field", "UniversalInputManager", "m_mousePosition", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "Box", "OnPressDisabledButtons", "*", "", "ui" },
#endif
        new[] { "Field", "AbsDeckPickerTrayDisplay", "m_playButton", "*", "", "ui" },
        new[] { "Field", "AbsDeckPickerTrayDisplay", "m_heroName", "*", "", "ui" },
        new[] { "Field", "AbsDeckPickerTrayDisplay", "m_heroPowerContainer", "*", "", "ui" },
        new[] { "Field", "AbsDeckPickerTrayDisplay", "m_heroActor", "*", "", "ui" },
        new[] { "Field", "AbsDeckPickerTrayDisplay", "m_heroPowerActor", "*", "", "ui" },
        new[] { "Field", "DeckPickerTrayDisplay", "m_xpBar", "*", "", "ui" },
        new[] { "Field", "DeckPickerTrayDisplay", "m_leftArrow", "*", "", "ui" },
        new[] { "Field", "DeckPickerTrayDisplay", "m_rightArrow", "*", "", "ui" },
        new[] { "Field", "DeckPickerTrayDisplay", "m_switchFormatButton", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "RewardBoxesDisplay", "m_doneButtonFinishedShown", "*", "", "ui" },
#endif
        new[] { "Field", "DialogManager", "m_currentDialog", "*", "", "ui" },
        new[] { "Field", "GameMenu", "m_optionsButton", "*", "", "ui" },
        new[] { "Field", "GameMenu", "m_concedeButton", "*", "", "ui" },
        new[] { "Method", "iTween", "Count", "UnityEngine.GameObject", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Method", "UberText", "get_Text", "", "", "ui" },
        new[] { "Field", "BaseHeroSkinInfoManager", "m_desiredVisibilityState", "*", "", "ui" },
#endif
        new[] { "Field", "Hearthstone.UI.Clickable", "m_stateCollection", "*", "", "ui" },
#if !SOLOSTONE_RELEASE
        new[] { "Field", "DeckHelper", "m_replaceCardActor", "*", "", "ui" },
        new[] { "Field", "DeckHelper", "m_choiceActors", "*", "", "ui" },
#endif
        new[] { "Field", "ShopStatus", "Failed", "*", "", "" },
        new[] { "Field", "ShopStatus", "Initializing", "*", "", "" },
        new[] { "Field", "ShopStatus", "Ready", "*", "", "" },
        new[] { "Field", "StoreManager+TransactionStatus", "READY", "*", "", "" },
        new[] { "Field", "StoreManager+TransactionStatus", "WAIT_BLIZZARD_CHECKOUT", "*", "", "" },
    };
    private static readonly Dictionary<string, Lazy<Type>> bindingTypes = ReflectionBindings.Select(row => row[1])
        .Distinct().ToDictionary(name => name, name => new Lazy<Type>(() =>
            GameTypes.ByName(name) ?? throw new TypeLoadException(name)));
    // Lazy caches both successful resolution and exceptions; a broken binding is never looked up per frame.
    private static readonly Dictionary<string, Lazy<MemberInfo>> bindings = ReflectionBindings
        .GroupBy(row => row[1] + "." + row[2]).ToDictionary(group => group.Key,
            group => new Lazy<MemberInfo>(() => ResolveBinding(group.First())));

    private static MemberInfo ResolveBinding(string[] row)
    {
        Type type = bindingTypes[row[1]].Value;
        // Metadata spells out parameters as "X&"; TypeByName cannot, so suffixes become MakeByRefType/MakeArrayType.
        Type[] parameters = row[3] == "*" ? null : row[3].Length == 0 ? Type.EmptyTypes : row[3].Split(';').Select(name =>
            NativeBindingTypeNames.Resolve(name, named => GameTypes.ByName(named) ?? throw new TypeLoadException(named),
                type => type.MakeByRefType(), type => type.MakeArrayType())).ToArray();
        MemberInfo member;
        switch (row[0])
        {
            case "Field": member = AccessTools.Field(type, row[2]); break;
            case "Property": member = AccessTools.Property(type, row[2]); break;
            case "Method": member = row[2] == ".ctor" ? (MemberInfo)AccessTools.Constructor(type, parameters)
                : AccessTools.Method(type, row[2], parameters); break;
            default: throw new InvalidOperationException("Unknown binding kind: " + row[0]);
        }
        return member ?? throw new MissingMemberException(type.FullName, row[2]);
    }
    // Read/Write/Call resolve per use (some per frame); a resolved member is remembered per (type, name) so steady state
    // builds no "Type.name" keys. Failures are never remembered: the binding's Lazy rethrows them on every use.
    private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> members = new Dictionary<Type, Dictionary<string, MemberInfo>>();
    private static MemberInfo Member(Type type, string name)
    {
        lock (members)
            if (type != null && members.TryGetValue(type, out var known) && known.TryGetValue(name, out var cached)) return cached;
        for (Type current = type; current != null; current = current.BaseType)
            if (bindings.TryGetValue(current.FullName + "." + name, out var binding))
            {
                var member = binding.Value;
                lock (members)
                {
                    if (!members.TryGetValue(type, out var known)) members[type] = known = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                    known[name] = member;
                }
                return member;
            }
        throw new MissingMemberException(type?.FullName, name);
    }
    internal static MemberInfo UiBinding(string type, string name)
    {
        try { return bindings[type + "." + name].Value; }
        catch (Exception error) { Refuse("binding " + type + "." + name + ": " + error.Message); return null; }
    }
    private static Type MemberType(MemberInfo member) => member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    private static void InstallHooks(Harmony harmony)
    {
        // Each hook is independent: failed registration must not skip subsequent isolation barriers.
        foreach (var row in ReflectionBindings)
            if (row[4].Length != 0) Hook(harmony, row[1], row[2], row[4], row[5] == "postfix", row[5] == "finalizer", row[5] == "iterator-transpiler");
    }

    private static MethodInfo Method(Type type, string name) =>
        (MethodInfo)Member(type, name);
    private static MethodInfo Method(string type, string name) => (MethodInfo)bindings[type + "." + name].Value;
    private static void Hook(Harmony harmony, string type, string target, string prefix, bool postfix = false, bool finalizer = false, bool iteratorTranspiler = false)
    {
        try
        {
            var patch = new HarmonyMethod(typeof(NativeShop), prefix);
            var method = Method(type, target);
            if (iteratorTranspiler)
                method = AccessTools.EnumeratorMoveNext(method) ?? throw new MissingMethodException(type, target + ".MoveNext");
            harmony.Patch(method, prefix: postfix || finalizer || iteratorTranspiler ? null : patch,
                postfix: postfix ? patch : null, finalizer: finalizer ? patch : null, transpiler: iteratorTranspiler ? patch : null, ilmanipulator: null);
        }
        catch (Exception error) { Fail("hook " + type + "." + target, error); }
    }
    private static object Read(object obj, string name)
    {
        var member = Member(obj as Type ?? obj.GetType(), name);
        object instance = obj is Type ? null : obj;
        return member is PropertyInfo property ? property.GetValue(instance, null) : ((FieldInfo)member).GetValue(instance);
    }
    private static void Write(object obj, string name, object value)
    {
        var member = Member(obj as Type ?? obj.GetType(), name);
        object instance = obj is Type ? null : obj;
        if (member is PropertyInfo property) property.SetValue(instance, value, null);
        else ((FieldInfo)member).SetValue(instance, value);
    }
    private static void EnumValue(object obj, string name, string value) =>
        Write(obj, name, Read(MemberType(Member(obj as Type ?? obj.GetType(), name)), value));
    private static object Call(object obj, string method, params object[] args) =>
        Method(obj as Type ?? obj.GetType(), method).Invoke(obj is Type ? null : obj, args);
    private static void Trace(string message) => log?.LogInfo("[NativeShop] " + message);
    private static void Refuse(string reason) { if (reasons.Add(reason)) log?.LogWarning("[NativeShop] inert: " + reason); }
    private static void Fail(string step, Exception error)
    {
        failed = true; ready = false;
        Refuse(step + ": " + error);
        try
        {
            EnumValue(typeof(ShopInitialization), "Status", "Failed");
            Write(typeof(ShopInitialization), "StatusMessage", "Local shop unavailable: " + step);
            if (installed) StoreManager.Get()?.HandleShopAvailabilityChange();
        }
        catch (Exception cleanup) { Refuse("availability cleanup after " + step + ": " + cleanup); }
    }
    private static bool SkipIsolated() => !Isolated;
    private static bool BlockCommerce()
    {
        if (!Isolated) return true;
        throw new InvalidOperationException("CommerceWrapper is forbidden in the isolated client");
    }
    private static bool EmptyCoroutine(ref IEnumerator __result)
    { if (!Isolated) return true; __result = Enumerable.Empty<object>().GetEnumerator(); return false; }
    private static bool BlockBalance(Action<bool> __2, ref IEnumerator __result)
    {
        if (!Isolated) return true;
        __result = Enumerable.Empty<object>().GetEnumerator();
        try { __2?.Invoke(false); } catch (Exception error) { Refuse("balance refusal: " + error.Message); }
        return false;
    }
    private static bool BlockCredentials(Action<bool, string> __0)
    {
        if (!Isolated) return true;
        try { __0?.Invoke(false, null); } catch (Exception error) { Refuse("SSO refusal: " + error.Message); }
        return false;
    }
    private static bool StoreAvailable(ref bool __result)
    { if (!Isolated) return true; __result = Enabled && ready; return false; }
    private static bool CanShowStore()
    {
        if (!Isolated) return true;
        try { return Enabled && ready && Prerequisite() == null && !Profile.HasPendingRewardPresentation && !Profile.PackRevealPending; }
        catch (Exception error) { Fail("show prerequisites", error); return false; }
    }
    // Retail hides the Store button while a scene loads or the Box changes state (Box.UpdateNonBoxUIEvents, and
    // StoreButton.UpdateState(HIDDEN) from the per-state config) because its online shop loads asynchronously. The local
    // shop is always ready, so it stays put with the Packs and Collection buttons through every Box state that shows them.
    private static bool StoreStaysVisible()
    {
        if (!Isolated || !HomeStoreReady || !SceneMgr.IsInitialized()) return false;
        var box = Box.Get();
        if (box == null || SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY) return false;
        switch (box.GetState())
        {
            case Box.State.HUB: case Box.State.HUB_WITH_DRAWER: case Box.State.OPEN: case Box.State.CLOSED:
            case Box.State.LOADING: case Box.State.LOADING_HUB: return true;
            default: return false;
        }
    }
    private static void StoreButtonVisibility(ref bool __0)
    {
        if (!Isolated) return;
        if (!HomeStoreReady) __0 = false;
        else if (StoreStaysVisible()) __0 = true;
    }
    private static void StoreButtonState(ref StoreButton.State __0)
    {
        if (__0 == StoreButton.State.HIDDEN && StoreStaysVisible()) __0 = StoreButton.State.SHOWN;
    }

    // Runs every frame at home (Tick) and on home-button refreshes. Steady state: no reflection, no string building.
    private static Box railroadBox;
    private static BoxRailroadManager railroad;
    private static Func<BoxRailroadManager, Box.ButtonType, bool> shouldDisableButton;
    private static bool RailroadDisablesStore(Box box)
    {
        // Box.m_railroadManager is fetched once in Box.Awake; read it once per Box instance.
        if (!ReferenceEquals(box, railroadBox) || railroad == null) { railroad = Read(box, "m_railroadManager") as BoxRailroadManager; railroadBox = box; }
        if (railroad == null) throw new InvalidOperationException("box railroad manager unavailable");
        if (shouldDisableButton == null)
            shouldDisableButton = (Func<BoxRailroadManager, Box.ButtonType, bool>)Delegate.CreateDelegate(
                typeof(Func<BoxRailroadManager, Box.ButtonType, bool>), Method(typeof(BoxRailroadManager), "ShouldDisableButtonType"));
        return shouldDisableButton(railroad, Box.ButtonType.STORE);
    }
    private static StoreButton labelButton, homeButton;
    private static GameObject storeLabel;
    private static string homeReason, loggedReason;
    private static bool loggedVisible, loggedInteractable;
    internal static void RefreshHomeStore()
    {
        if (!Isolated) return;
        try
        {
            var box = Box.Get();
            var button = box?.m_StoreButton;
            string reason = !Enabled ? "disabled" : !ready ? "not ready" : button == null ? "button unavailable"
                : !SceneMgr.IsInitialized() || SceneMgr.Get().GetMode() != SceneMgr.Mode.HUB ? "outside home"
                : SceneMgr.Get().IsTransitionNowOrPending() || box.IsBusy() || box.GetState() == Box.State.LOADING ? "home busy"
                : Profile == null || Profile.Pending != null || LocalLadder.Screen == "Playing" || LocalLadder.Screen == "Starting" ? "match pending"
                : FriendChallengeMgr.Get()?.HasChallenge() == true ? "challenge active"
                : RailroadDisablesStore(box) ? "railroad disabled"
                : "ready";
            if (button != null)
            {
                // The label sits beside the button in the prefab; found once per button (again if missing or destroyed).
                if (!ReferenceEquals(button, labelButton) || storeLabel == null)
                { storeLabel = button.transform.parent?.Find("ButtonName")?.gameObject; labelButton = button; }
                var label = storeLabel;
                if (!HomeStoreReady)
                {
                    if (button.gameObject.activeSelf || label != null && label.activeSelf)
                        box.EnableStoreButtonGO(false, reason);
                    if (button.IsEnabled()) box.DisableButton(button);
                }
                else if (reason == "ready")
                {
                    bool shown = false;
                    if (!button.gameObject.activeSelf || label != null && !label.activeSelf)
                    { box.EnableStoreButtonGO(true, "local shop ready"); shown = true; }
                    // Hidden or unloaded buttons miss availability events; Load restores listeners, then replay native status.
                    // Once per entry into ready (or after showing it, or when Unload left it disabled or closed), not per frame.
                    if (shown || homeReason != "ready" || !ReferenceEquals(button, homeButton) || !button.IsEnabled() || button.IsVisualClosed())
                        button.Load();
                    if (button.IsVisualClosed()) Call(button, "OnStoreStatusChanged", true);
                    if (!button.IsEnabled()) box.EnableButton(button);
                }
                else
                {
                    // Busy/loading only revokes input; the button itself stays put like the other drawer buttons.
                    if (StoreStaysVisible() && (!button.gameObject.activeSelf || label != null && !label.activeSelf))
                        box.EnableStoreButtonGO(true, "local shop stays visible");
                    if (button.IsEnabled()) box.DisableButton(button);
                }
            }
            homeReason = reason; homeButton = button;
            bool visible = button != null && button.gameObject.activeInHierarchy;
            bool interactable = visible && reason == "ready" && button.IsEnabled() && button.IsEnabled(UIEventType.RELEASE) && !button.IsVisualClosed();
            if (reason != loggedReason || visible != loggedVisible || interactable != loggedInteractable)
            {
                loggedReason = reason; loggedVisible = visible; loggedInteractable = interactable;
                Trace("store button visible=" + (visible ? "true" : "false") + " interactable=" + (interactable ? "true" : "false") + " reason=" + reason);
            }
        }
        catch (Exception error) { homeReason = null; Fail("store button", error); }
    }
    private static bool LoadStore(StoreManager __instance, ShopType __0)
    {
        if (!Isolated) return true;
        try
        {
            if (!Enabled || !ready || __0 != ShopType.GENERAL_STORE) { RecoverUI(); return false; }
            if (Call(__instance, "GetCurrentStore") == null) Call(__instance, "LoadGeneralStore");
        }
        catch (Exception error) { Fail("store load", error); RecoverUI(); }
        return false; // Local booster catalog has no Mercenaries dependency.
    }
    private static bool GuardPurchase(PurchaseManager __instance, object __0, ref IEnumerator __result)
    {
        if (!Isolated) return true;
        // A second order while one is pending is dropped without touching the pending purchase.
        bool pending = transaction != null || confirmation != null;
        try
        {
            var product = __0 == null ? null : Read(__0, "Product") as ProductDataModel;
            var price = __0 == null ? null : Read(__0, "Price") as PriceDataModel;
            if (!pending && Enabled && ready && ValidProduct(product) && price?.Currency == CurrencyType.GOLD
                && (int)Read(__0, "Quantity") == 1
                && kits.Dispatch(product.PmtId, FullPrice(product), (int)product.Prices[0].Amount, price.Amount, Time.realtimeSinceStartup)) return true;
            if (__0 != null) Write(__0, "HasFinished", true);
            Call(__instance, "CancelCurrentPurchase", product, pending ? "A local purchase is already pending" : "Only local gold products are available");
        }
        catch (Exception error) { if (pending) Refuse("purchase order while pending: " + error); else Fail("purchase order", error); }
        if (!pending) RecoverUI();
        __result = Enumerable.Empty<object>().GetEnumerator(); return false;
    }
    // A click-path error never touches a pending purchase: Fail would make TickPurchaseWait recover it and report a
    // failure for gold already taken. The click is dropped; the next click with nothing pending fails closed as usual.
    private static void ClickFailed(string step, Exception error)
    {
        if (transaction != null || confirmation != null) { Refuse(step + " while a purchase is pending: " + error); return; }
        Fail(step, error); RecoverUI();
    }
    // Owner 2026-10-25: the gold price click is the decision; local gold orders skip the native confirmation
    // (the native SuppressVcConfirmation option), and a short balance shows the native not-enough-gold text.
    private static bool LocalPurchase(ProductDataModel __0, PriceDataModel __1, ref PurchaseManager.PurchaseManagerOptions __2, int __3)
    {
        if (!Isolated) return true;
        try
        {
            if (__0 == null || !NativeShopPurchase.TryProduct(__0.PmtId, out var local)) return true; // GuardPurchase refuses foreign orders.
            if (transaction != null || confirmation != null) { Trace("click ignored: purchase pending"); return false; }
            if (kitQuestion != null) { Trace("click ignored: deck kit question open"); return false; }
            // Every route, kind and quantity: the native gold-deficit stop is silent, so a short balance is said here.
            string blocker = local.Kind == "Deck" ? Profile.DeckPurchaseBlocker(local.RecipeId) : null;
            if (blocker != null) { ShowPurchaseFailure("solostone-purchase-refused", blocker); return false; }
            int full = Profile.PurchasePrice(local.Id);
            long cost = Math.Max(full, __1 != null && __1.Currency == CurrencyType.GOLD ? (long)__1.Amount : 0) * Math.Max(1, __3);
            // #57: a ready-made deck with an unredeemed kit (natively quoted after the kit) asks USE KIT / PAY FULL first;
            // the answer re-places the order: USE KIT at the after-kit quote, PAY FULL at the pre-kit price.
            if (local.Kind == "Deck")
            {
                if (kits.Answered(__0.PmtId, full, Time.realtimeSinceStartup)) cost = kits.Due(__0.PmtId, full, Time.realtimeSinceStartup);
                else
                {
                    var kit = NativeShopKitChoice.Offer(Profile, local, full);
                    if (kit != null) { AskDeckKit(__0, local, kit, full); return false; }
                }
            }
            if (Profile.Gold < cost)
            {
                kits.Cancel(); // A stopped order used its kit answer: the next click asks again.
                if (Enabled) RecoverStep("wallet refresh", () => RefreshWallet());
                ShowNotEnoughGold(cost);
                return false;
            }
            __2.SuppressVcConfirmation = true; // Owned, stale and blocked orders still stop in Buy before a transaction.
            return true;
        }
        catch (Exception error) { ClickFailed("purchase click", error); return false; }
    }
    private static void ShowNotEnoughGold(long price)
    {
        Trace("not enough gold: price " + price + " balance " + Profile.Gold);
        ShowPurchaseFailure("solostone-not-enough-gold", GameStrings.Get("GLUE_STORE_FAIL_NOT_ENOUGH_GOLD") + "\n" + Profile.Gold + " / " + price + " gold");
    }
    private static void ShowPurchaseFailure(string id, string text)
    {
        DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
            m_id = id, m_showAlertIcon = true,
            m_headerText = GameStrings.Get("GLUE_STORE_AUTH_FAIL_HEADLINE"),
            m_text = text,
            m_responseDisplay = AlertPopup.ResponseDisplay.OK, m_okText = GameStrings.Get("GLOBAL_OKAY") });
    }
    // #57 kit question: one native AlertPopup per deck order. USE KIT re-places the order at the after-kit amount, PAY FULL
    // at the catalogue quote; Escape (or anything but a release on PAY FULL) cancels with no order (NativeShopKitRelease).
    private static void AskDeckKit(ProductDataModel product, LocalShopProduct local, LocalDeckKit kit, int price)
    {
        kits.Ask(product.PmtId, price, kit);
        kitRelease.Reset(); kitHooked = null; kitProduct = product;
        var question = kitQuestion = new AlertPopup.PopupInfo {
            m_id = "solostone-kit-" + checked(++sequence),
            m_headerText = NativeShopKitText.Header,
            // The deck's own name: the native product title reads "Recipe plus missing cards: <deck>".
            m_text = NativeShopKitText.Body(LocalShop.Default.Recipe(local.RecipeId)?.Name ?? product.Name, price, kit.GoldCap),
            m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
            m_confirmText = NativeShopKitText.UseLabel, m_cancelText = NativeShopKitText.FullLabel, m_keyboardEscIsCancel = true
        };
        question.m_responseCallback = (response, data) => {
            if (!ReferenceEquals(kitQuestion, question)) return;
            if (response == AlertPopup.Response.CONFIRM) { ResolveKitQuestion(question, true); return; }
            if (kitRelease.Cancel(Time.frameCount)) ResolveKitQuestion(question, false);
            else Trace("deck kit question: cancel waits for a PAY FULL release this frame");
        };
        if (!DialogManager.Get().ShowUniquePopup(question)) { kitQuestion = null; kitProduct = null; kits.Cancel(); Refuse("deck kit question not shown"); }
        else Trace("deck kit question " + kit.Id + " price " + price);
    }
    // useKit null = cancelled (no order).
    private static void ResolveKitQuestion(AlertPopup.PopupInfo question, bool? useKit)
    {
        if (!ReferenceEquals(kitQuestion, question)) return;
        var product = kitProduct;
        kitQuestion = null; kitHooked = null; kitProduct = null; kitRelease.Reset();
        try
        {
            if (useKit == null) { kits.Cancel(); Trace("deck kit question cancelled"); return; }
            if (!Enabled || !ready || Shop.Get() == null || !Shop.Get().IsOpen() || product == null || !ValidProduct(product))
            { kits.Cancel(); Refuse("deck kit answer after the shop changed"); return; }
            int due = kits.Answer(useKit.Value, Time.realtimeSinceStartup);
            Trace("deck kit " + (useKit.Value ? "used: " + due : "declined: " + due) + " gold");
            if (!ServiceManager.TryGet<PurchaseManager>(out var manager) || manager == null)
                throw new InvalidOperationException("purchase manager unavailable");
            // USE KIT: the page's own (after-kit) quote; PAY FULL: the pre-kit price, which the gold check then requires.
            if (useKit.Value && product.Prices[0].Amount != due) throw new InvalidOperationException("deck quote changed during the kit question");
            var order = useKit.Value ? product.Prices[0] : new PriceDataModel { Currency = CurrencyType.GOLD, Amount = due };
            manager.PurchaseProduct(product, order, 1);
        }
        catch (Exception error) { kits.Cancel(); ClickFailed("deck kit answer", error); }
    }
    // #57 badges (owner 2026-10-29): native new-items badges on the hub Store button and both Decks categories
    // shows exactly while the save holds an unredeemed deck kit. Ownership only: no timers, no seen state.
    // Memoised per ledger commit (LedgerIdentity) and deck blocker: the badge query runs from native refreshes and each tick.
    private static object kitUsableLedger; private static string kitUsableBlocker; private static bool kitUsable;
    private static bool KitBadge
    {
        get
        {
            if (Profile == null || !Profile.HasUnredeemedDeckKit) return false;
            object ledger = Profile.LedgerIdentity; string blocker = NativeShopPurchase.PersonalDeckBlocker(Profile);
            if (ReferenceEquals(ledger, kitUsableLedger) && blocker == kitUsableBlocker) return kitUsable;
            kitUsable = NativeShopKitChoice.KitUsable(Profile, NativeShopPurchase.Products, blocker);
            kitUsableLedger = ledger; kitUsableBlocker = blocker;
            return kitUsable;
        }
    }
    private static bool? kitBadgeShown;
    private static bool StoreKitBadge(ref bool __result)
    {
        if (!Isolated) return true;
        try { __result = KitBadge; }
        catch (Exception error) { Refuse("store kit badge: " + (error.InnerException ?? error).Message); __result = false; }
        return false;
    }
    private static void KitTabBadges(ProductDataService __instance)
    {
        if (!Isolated || __instance == null) return;
        try
        {
            bool want = KitBadge;
            foreach (var tab in ((IEnumerable)Call(__instance, "GetTabs")).OfType<ShopTabData>())
            {
                if (!NativeShopTabs.KitBadgeTabs.Contains(tab.Id)) continue;
                if (want) { tab.NotificationEnabled = true; tab.HasUndisplayedProducts = true; }
                else if (tab.NotificationEnabled) { tab.NotificationEnabled = false; tab.HasUndisplayedProducts = false; }
            }
        }
        catch (Exception error) { Refuse("kit tab badge: " + (error.InnerException ?? error).Message); }
    }
    // The native badge refreshes on its own events; a kit used or earned in this session updates it here at once.
    private static void TickKitBadges()
    {
        if (!Enabled || !ready || Profile == null) return;
        bool want = KitBadge;
        object status = Method("ShopButtonStatus", "Get").Invoke(null, null); // ShopButtonStatus is internal to the client
        var model = status == null ? null : Read(status, "m_shopButtonDataModel") as ShopButtonDataModel;
        if (model != null && model.HasNewItems != want) { model.HasNewItems = want; Trace("kit badge " + (want ? "on" : "off")); }
        if (kitBadgeShown == want || products == null) return;
        kitBadgeShown = want;
        Call(products, "UpdateTabNotificationStates");
    }
    // #57: native quote of an offer: the after-kit amount for a ready-made deck with an unredeemed kit, else the ledger price.
    private static int Quote(LocalShopProduct local)
    {
        return NativeShopKitChoice.NativeQuote(Profile, local, Profile.PurchasePrice(local.Id));
    }
    // Ledger (pre-kit) price of a native local offer.
    private static int FullPrice(ProductDataModel product)
    {
        return Profile.PurchasePrice(NativeShopPurchase.Product(product.PmtId).Id);
    }
    private static void TickKitQuestion()
    {
        var question = kitQuestion;
        if (question == null) return;
        if (!Enabled || !ready || Shop.Get() == null || !Shop.Get().IsOpen()) { RecoverUI(); return; }
        if (kitRelease.Expired(Time.frameCount)) { ResolveKitQuestion(question, null); return; }
        if (kitHooked != null) return;
        var dialogs = DialogManager.Get();
        var popup = dialogs == null ? null : Read(dialogs, "m_currentDialog") as AlertPopup;
        if (popup == null || !ReferenceEquals(popup.GetInfo(), question) || popup.m_cancelButton == null) return;
        kitHooked = popup;
        popup.m_cancelButton.AddEventListener(UIEventType.RELEASE, _ => {
            if (!ReferenceEquals(kitQuestion, question) || !ReferenceEquals(popup.GetInfo(), question)) return;
            if (kitRelease.Released(Time.frameCount)) ResolveKitQuestion(question, false);
        });
    }
    // StartCoroutine may hand back a Coroutine for a routine that already finished synchronously (no confirmation
    // yield); a stale handle would refuse every later order as "already executed".
    private static void ClearFinishedPurchase(PurchaseManager __instance, object __0)
    {
        if (!Isolated || __0 == null) return;
        try
        {
            if ((bool)Read(__0, "HasFinished") && Read(__instance, "m_currentPurchaseCoroutine") != null)
                Write(__instance, "m_currentPurchaseCoroutine", null);
        }
        catch (Exception error) { Fail("purchase coroutine cleanup", error); }
    }
    // Owner 2026-10-25: a pack tile's gold price buys one pack at once; the booster details page adds nothing.
    // Other kinds keep the native details page (deck list, preview, cosmetic preview) and buy from there.
    private static bool BuyPackTile(ShopSlot __instance)
    {
        if (!Isolated) return true;
        try
        {
            var shown = __instance.BrowserButtonDataModel?.DisplayProduct;
            if (shown == null || !NativeShopPurchase.TryProduct(shown.PmtId, out var local)
                || (local.Kind != "Pack" && !NativeShopPurchase.IsSelectable(local))) return true;
            if (NativeShopClicks.PackCoolingDown(Time.realtimeSinceStartup, purchaseWindowAt)) { Trace("pack click ignored: purchase window cooldown"); return false; }
            // Pinned PegUI still dispatches RELEASE after a drag-scroll that starts and ends on one tile: never a purchase.
            string ignored = PackTileRelease(__instance);
            if (ignored != null) { Trace("pack tile " + ignored + " ignored"); return false; }
            // A tile under the open purchase window (the press that dismisses it can reach the tile) must not queue an
            // order: the native purchase coroutine would run it after the acknowledgement, a second unasked purchase.
            if (transaction != null || confirmation != null || auth != null) { Trace("tile click ignored: purchase window open"); return false; }
            if (!Enabled || !ready || products == null) { Refuse("pack click while shop unavailable"); return false; }
            var product = products.GetProductDataModel(shown.PmtId);
            if (!ValidProduct(product)) { Refuse("pack click on stale catalogue"); return false; }
            // Owner 2026-10-25: a theme or board tile equips what is owned and buys (then equips) what is not.
            if (NativeShopPurchase.IsSelectable(local) && NativeShopPurchase.Owned(Profile, local, 0)) { Equip(local); return false; }
            // The tile must show the quote that will be charged: the catalogue model itself or the same single gold price.
            if (!ReferenceEquals(shown, product) && (shown.Prices == null || shown.Prices.Count != 1
                || shown.Prices[0].Currency != CurrencyType.GOLD || shown.Prices[0].Amount != product.Prices[0].Amount))
            { Refuse("pack tile price differs from catalogue"); return false; }
            if (!ServiceManager.TryGet<PurchaseManager>(out var manager) || manager == null)
                throw new InvalidOperationException("purchase manager unavailable");
            manager.PurchaseProduct(product, product.Prices[0], 1);
        }
        catch (Exception error) { ClickFailed("pack click", error); }
        return false;
    }
    private static void Equip(LocalShopProduct local)
    {
        if (transaction != null || confirmation != null) { Trace("equip ignored: purchase pending"); return; }
        try
        {
            if (local.Kind == "HubTheme") Profile.SelectHubTheme(local.HubTheme);
            else if (local.Kind == "Pet") { Profile.SelectPet(local.Pet); NativeUIProbe.RefreshPets(); }
            else Profile.SelectBoard(local.Board.Value);
            Trace("equipped " + local.Id);
        }
        catch (Exception error) { Refuse("equip " + local.Id + ": " + (error.InnerException ?? error).Message); return; }
        RecoverStep("equip refresh", () => { RefreshPrices(); products.UpdateProductStatus(); });
        if (local.Kind == "HubTheme") RecoverStep("theme notice", () => ThemeNotice(local.HubTheme));
    }
    // A classic box takes a new theme live (when the shop closes); any other change waits for the next start.
    private static void ThemeNotice(string key)
    {
        if (HubTheme.Equipped(key)) return;
        DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
            m_id = "solostone-theme-next-start", m_headerText = "Main menu theme",
            m_text = "Your new theme appears the next time you start SoloStone.",
            m_responseDisplay = AlertPopup.ResponseDisplay.OK, m_okText = GameStrings.Get("GLOBAL_OKAY") });
    }
    // Pinned Clickable.OnRelease clears m_dragged and its listener may run before ShopSlot.OnRelease (registration
    // order), so the drag is recorded for the same frame's release before the native reset.
    private static object dragReleaseClickable;
    private static int dragReleaseFrame = -1;
    private static void NoteDragRelease(object __instance)
    {
        if (!Isolated || __instance == null) return;
        try
        {
            if ((bool)Read(__instance, "m_dragged")) { dragReleaseClickable = __instance; dragReleaseFrame = Time.frameCount; }
        }
        catch (Exception error) { Refuse("drag release: " + error.Message); }
    }
    // The drag decision uses this tile's own press, never PegUI.m_lastClickPosition: a semantic test-control press
    // (TriggerPress / TriggerRelease) and any release whose press PegUI did not record would compare against a stale
    // position. Clickable.OnClick is the PRESS listener of Clickables only (not every PegUIElement); the last pressed
    // one is kept by reference, so a press on tile A never makes a release on tile B a click.
    private static object pressClickable;
    private static float pressX, pressY;
    private static int pressFrame = -1, pressUpFrame = -1;
    private static void NoteTilePress(object __instance)
    {
        if (!Isolated || __instance == null) return;
        try
        {
            var at = InputCollection.GetMousePosition(); pressX = at.x; pressY = at.y; pressClickable = __instance;
            pressFrame = Time.frameCount; pressUpFrame = -1;
        }
        catch (Exception error) { pressClickable = null; Refuse("tile press: " + error.Message); }
    }
    // RELEASE listeners have all run before RELEASEALL (pinned PegUI and the semantic release alike). Roll-out and
    // disable end the press too: a release after either is never this press's click.
    private static void ClearTilePress(object __instance)
    {
        if (ReferenceEquals(__instance, pressClickable)) pressClickable = null;
    }
    // Pinned PegUI skips RELEASEALL when a custom behaviour takes over input or a mouse-up becomes a double-click, so a
    // press can outlive its cycle. Once the button has been up for two whole frames after the press (a physical or
    // semantic release has had its frame, whatever the Update order), the press is dropped.
    private static void ExpireTilePress()
    {
        if (pressClickable == null) return;
        if (InputCollection.GetMouseButton(0)) { pressUpFrame = -1; return; }
        if (pressUpFrame < 0) pressUpFrame = Math.Max(Time.frameCount, pressFrame);
        else if (Time.frameCount - pressUpFrame >= 2) { pressClickable = null; pressUpFrame = -1; }
    }
    // Null for a click; otherwise why this release is not one.
    private static string PackTileRelease(ShopSlot slot)
    {
        object clickable = Read(slot, "m_clickable") ?? throw new InvalidOperationException("pack tile clickable unavailable");
        bool pressed = ReferenceEquals(clickable, pressClickable);
        float fromX = pressX, fromY = pressY;
        if (pressed) pressClickable = null; // One press decides at most one release.
        if ((bool)Read(clickable, "m_dragged")) return "drag release";
        if (ReferenceEquals(clickable, dragReleaseClickable) && dragReleaseFrame == Time.frameCount) return "drag release";
        if (!pressed) return "release without press";
        var element = Read(clickable, "m_pegUiElement") as PegUIElement ?? throw new InvalidOperationException("pack tile element unavailable");
        var input = PegUI.Get() ?? throw new InvalidOperationException("PegUI unavailable");
        var tolerance = element.GetDragTolerance();
        var at = InputCollection.GetMousePosition();
        return NativeShopClicks.BeyondDragTolerance(at.x - fromX, at.y - fromY, tolerance.x, tolerance.y,
            (float)Read(input, "m_dragToleranceDpiAdjustmentFactor")) ? "drag release" : null;
    }
    private static bool BlockCheckout() { if (!Isolated) return true; Refuse("commerce SDK entry blocked"); return false; }
    private static bool CheckoutUnavailable(ref bool __result) { if (!Isolated) return true; __result = false; return false; }
    private static bool CheckoutInitialize(ref IEnumerator<IAsyncJobResult> __result)
    { if (!Isolated) return true; __result = Enumerable.Empty<IAsyncJobResult>().GetEnumerator(); return false; }
    private static bool CheckoutDependencies(ref Type[] __result)
    {
        if (!Isolated) return true;
        try { __result = new[] { typeof(Network), Method(typeof(AssetLoader), "Get").ReturnType }; }
        catch (Exception error) { __result = new[] { typeof(Network) }; Fail("checkout dependencies", error); }
        return false;
    }
    private static bool Bootstrap() => !Isolated; // Tick owns bounded initialization, including launches without BattlePay config.
    private static bool NoOtherCurrency(ref CurrencyType __0, ref bool __result)
    { if (!Isolated) return true; __0 = CurrencyType.NONE; __result = false; return false; }
    private static bool AvailabilityChanged(StoreManager __instance)
    {
        if (!Isolated) return true;
        try { Call(__instance, "FireStatusChangedEventIfNeeded"); }
        catch (Exception error) { failed = true; ready = false; Refuse("availability event: " + error.Message); }
        return false; // Retail implementation loops Navigation.GoBack until all prompts disappear.
    }
    private static bool LocalGamemode(ProductDataModel __0, ref IGamemodeAvailabilityService.Status __1, ref bool __result)
    {
        if (!Isolated) return true;
        __result = false; __1 = IGamemodeAvailabilityService.Status.NONE;
        try
        {
            // Runs per tile on every section refresh: identity only. Purchase paths still check the ledger quote.
            __result = Enabled && LocalModel(__0) && Prerequisite() == null;
            if (__result) __1 = IGamemodeAvailabilityService.Status.READY;
        }
        catch (Exception error) { Fail("product assets", error); }
        return false;
    }
    private static Exception NativeFailure(Exception __exception, MethodBase __originalMethod)
    {
        if (!Isolated || __exception == null) return __exception;
        Fail(__originalMethod.Name, __exception); RecoverUI(); return null;
    }
    private static void GuardCatalogJob(ref IEnumerator<IAsyncJobResult> __result)
    { if (Isolated) __result = GuardedCatalog(__result); }
    private static IEnumerator<IAsyncJobResult> GuardedCatalog(IEnumerator<IAsyncJobResult> original)
    {
        using (original)
        {
            while (Enabled)
            {
                bool more = false; IAsyncJobResult current = null;
                try { more = original.MoveNext(); if (more) current = original.Current; }
                catch (Exception error) { Fail("catalog construction", error); }
                if (!more) yield break;
                yield return current;
            }
        }
    }

    // Runs per tile on every section refresh (LocalGamemode) and from several hooks per frame: one evaluation per frame.
    // The result can be up to one frame stale: the purchase guard (Buy) re-checks with EvaluatePrerequisite directly.
    // Not cached longer: a GameDbf reload drops injected deck rows and has no cheap signal. A throwing check leaves no
    // entry; seeding, a catalogue change or another profile within the frame re-evaluates.
    private static int prerequisiteFrame = -1, prerequisiteVersion;
    private static bool prerequisiteSeeded, prerequisiteActive;
    private static LocalProfile prerequisiteProfile;
    private static string prerequisiteResult;
    private static string Prerequisite()
    {
        int frame = Time.frameCount;
        bool active = NativeUIProbe.Active;
        var profile = Profile;
        if (frame == prerequisiteFrame && active == prerequisiteActive && seeded == prerequisiteSeeded
            && NativeShopPurchase.CatalogueVersion == prerequisiteVersion && ReferenceEquals(profile, prerequisiteProfile))
            return prerequisiteResult;
        prerequisiteFrame = -1;
        string result = EvaluatePrerequisite();
        prerequisiteResult = result; prerequisiteActive = active; prerequisiteSeeded = seeded; prerequisiteProfile = profile;
        prerequisiteVersion = NativeShopPurchase.CatalogueVersion; prerequisiteFrame = frame;
        return result;
    }
    private static string EvaluatePrerequisite()
    {
        if (!NativeUIProbe.Active || Profile == null) return "local projection not active";
        if (!GameDbf.IsLoaded) return "shop DBF unavailable";
        foreach (var product in NativeShopPurchase.Products.Where(p => p.Kind == "Pack"))
        {
            int booster = FairPackRules.NativeBoosterId(product.PackType);
            var record = GameDbf.Booster.GetRecord(booster);
            if (record == null || string.IsNullOrEmpty(record.StorePrefab)) return "booster " + booster + " store asset reference missing";
        }
        // A GameDbf reload drops injected rows while bundles remain; SellableDeckProductPage would null-deref.
        if (NativeShopPurchase.Products.Any(NativeShopDecks.Missing))
        {
            NativeShopDecks.Inject(Refuse);
            // Before seeding, SeedCatalog excludes offers whose records cannot be built.
            if (seeded && NativeShopPurchase.Products.Any(NativeShopDecks.Missing)) return "synthetic deck records missing";
        }
        if (CollectionManager.Get() == null || NetCache.Get()?.GetNetObject<NetCache.NetCacheCollection>() == null
            || NetCache.Get().GetNetObject<NetCache.NetCacheCardBacks>() == null) return "collection caches unavailable";
        if (StoreManager.Get() == null || NativeUIProbe.RewardAssetLoader == null) return "store or asset loader unavailable";
        if (!ServiceManager.IsAvailable<IProductDataService>() || !ServiceManager.IsAvailable<CurrencyManager>()
            || !ServiceManager.IsAvailable<PurchaseManager>()) return "shop services unavailable";
        return null;
    }
    private static bool CatalogReady(ref string __0, ref bool __result)
    {
        if (!Isolated) return true;
        try { __0 = Enabled ? Prerequisite() : "local shop disabled"; __result = __0 == null && products != null && products.HasLoadedSections(); }
        catch (Exception error) { __0 = error.Message; __result = false; Fail("catalog prerequisites", error); }
        return false;
    }

    private static void Tick()
    {
        if (!Isolated) return;
        try
        {
            try { ExpireTilePress(); }
            catch (Exception error) { pressClickable = null; Refuse("tile press expiry: " + error.Message); } // Fails closed: no click.
            try { if (Shop.Get() != null && Shop.Get().IsOpen()) { NativeShopTabIcons.Tick(Shop.Get()); NativeShopTileArt.Tick(Shop.Get()); } }
            catch (Exception error) { Refuse("tab icons: " + error.Message); }
            try { if (Shop.Get() != null && Shop.Get().IsOpen()) NativeShopTilePrices.Tick(Shop.Get()); }
            catch (Exception error) { Refuse("recipe tile price: " + error.Message); }
            // Board plates: shown tiles while the shop is open; pre-warm only at an idle hub (no transition, purchase or box state change).
            try
            {
                bool open = Shop.Get() != null && Shop.Get().IsOpen();
                bool idle = !open && Enabled && ready && !PresentationBusy && SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB
                    && !SceneMgr.Get().IsTransitioning() && NativeUIProbe.Active;
                NativeShopTileArt.SampleInput();
                if (open || idle) NativeShopTileArt.Pump(open, idle); else NativeShopTileArt.Suspend();
            }
            catch (Exception error) { Refuse("tile art: " + error.Message); }
            try { TickSubTabs(); }
            catch (Exception error) { Refuse("sub-tab tick: " + (error.InnerException ?? error).Message); }
            confirmation?.Tick(Time.realtimeSinceStartup, Enabled && ready && Shop.Get() != null && Shop.Get().IsOpen());
            try { TickKitQuestion(); }
            catch (Exception error) { Refuse("kit question tick: " + (error.InnerException ?? error).Message); }
            try { TickKitBadges(); }
            catch (Exception error) { Refuse("kit badges: " + (error.InnerException ?? error).Message); }
            if (themeNoticeAfterPurchase != null && transaction == null && auth == null)
            {
                string key = themeNoticeAfterPurchase; themeNoticeAfterPurchase = null;
                RecoverStep("theme notice", () => ThemeNotice(key));
            }
            TickPurchaseWait();
            if (opening)
            {
                if (Shop.Get() != null && Shop.Get().IsOpen())
                {
                    opening = false; Trace("opened");
                    if (tabIcons != null) NativeShopTabIcons.Configure(tabIcons.Value, message => log?.LogInfo(message)); // cfg edits apply per open
                    if (tileArt != null) NativeShopTileArt.Configure(tileArt.Value, message => log?.LogInfo(message));
                    if (recipeTilePrice != null) NativeShopTilePrices.Configure(recipeTilePrice.Value, message => log?.LogInfo(message));
                }
                else if (Time.realtimeSinceStartup - openBegan > Timeout) throw new InvalidOperationException("native shop load timed out");
            }
            if (!Enabled || ready || !NativeUIProbe.Active || !SceneMgr.IsInitialized() || SceneMgr.Get().GetMode() != SceneMgr.Mode.HUB) return;
            if (began == 0) began = Time.realtimeSinceStartup;
            string reason = Prerequisite();
            if (reason != null)
            {
                if (Time.realtimeSinceStartup - began > Timeout) throw new InvalidOperationException(reason);
                return;
            }
            if (!building)
            {
                building = true;
                EnumValue(typeof(ShopInitialization), "Status", "Initializing");
                var features = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
                if (features?.Store == null) throw new InvalidOperationException("store features missing");
                features.Store.Store = true; features.Store.BuyWithGold = true; features.Store.VirtualCurrencyEnabled = true;
                features.Store.BuyCardBacksFromCollectionManager = true; features.Store.BuyHeroSkinsFromCollectionManager = true;
                features.Store.LargeItemBundleDetailsEnabled = true;
                features.Store.BattlePay = false; features.Store.SimpleCheckout = false; features.Store.EnableVirtualCurrencyMenu = false;
                products = ServiceManager.Get<IProductDataService>() as ProductDataService
                    ?? throw new InvalidOperationException("unexpected product service");
                RefreshWallet();
                products.CreateDataModels();
            }
            if (!products.IsCatalogReady())
            {
                if (Time.realtimeSinceStartup - began > Timeout) throw new InvalidOperationException("native catalog construction timed out");
                return;
            }
            if (!ValidCatalog() || !products.GetProductSectionDataModels().Any())
                throw new InvalidOperationException("native catalogue differs from local ledger catalogue");
            RefreshWallet();
            var store = StoreManager.Get();
            Write(store, "BattlePayAvailable", true); Write(store, "FeaturesReady", true); store.FirstNoticesProcessed = true;
            EnumValue(store, "Status", "READY");
            EnumValue(typeof(ShopInitialization), "Status", "Ready");
            Write(typeof(ShopInitialization), "StatusMessage", "SoloStone local shop ready");
            ready = true;
            store.HandleShopAvailabilityChange();
            Trace("catalog " + NativeShopPurchase.Products.Count + " products"); Trace("ready gold " + Profile.Gold + " packs " + Profile.UnopenedPacks);
        }
        catch (Exception error) { Fail("update", error); RecoverUI(); }
        finally { RefreshHomeStore(); }
    }

    private static DbfLocValue Text(string text) { var value = new DbfLocValue(); value.SetString(text); return value; }
    private static AttributeSet Attributes(params string[] pairs)
    {
        var attributes = new AttributeSet();
        for (int i = 0; i < pairs.Length; i += 2) attributes.AddAttributeIfValid(ShopAttribute.CreateFrom(pairs[i], pairs[i + 1]));
        return attributes;
    }
    // BEGIN catalogue builders (also compiled against DTO doubles by ShopTests).
    // Kit quotes already include the kit; the details text names the full price (#57).
    internal static Network.Bundle BuildBundle(LocalShopProduct product, int price) => new Network.Bundle {
        ProductEvent = null, PMTProductID = NativeShopPurchase.PmtId(product), DisplayName = Text(DisplayName(product)),
        DisplayDescription = Text(Description(product, price)),
        GoldCost = new Network.Bundle.CostInfo { CurrentCost = price, OriginalCost = price },
        VirtualCurrencyCode = "XSH", Attributes = Attributes("tags", product.Kind == "Pack"
            ? "booster,booster_allow_no_gold" : product.Kind == "Recipe" || product.Kind == "Deck"
            ? "sellable_deck,standalone_variant,show_price"
            : product.Kind == "CardBack" ? "card_back" : product.Kind == "Coin" ? "show_price"
            : product.Kind == "Pet" ? "pet,show_price" : product.Kind == "HubTheme" ? "local_hub_theme,show_price" : product.Kind == "Board" ? "local_board,show_price" : "hero_skin"),
        Items = new List<Network.BundleItem> { new Network.BundleItem {
            ItemType = ItemType(product), ProductData = NativeShopPurchase.NativeItemId(product), Quantity = 1, BaseQuantity = 1 } }
    };
    internal static string Description(LocalShopProduct product, int price) => (product.Kind == "Pack"
        ? "One local " + product.PackType + " pack."
        : product.Kind == "Recipe" ? "Recipe only — cards not included; build it in the deck builder."
        : product.Kind == "Deck" ? "Recipe plus missing cards (Core cards not yet unlocked included) and a saved, ready-to-play deck."
        : product.Kind == "HubTheme" ? (product.HubTheme == LocalHubCosmetics.RandomTheme ? "Random owned main menu theme, rolled at each start of SoloStone."
            : "Main menu theme: " + Sentence(product.Description) + " Click to equip; shown from the next start of SoloStone.")
        : product.Kind == "Board" ? (product.Board == LocalHubCosmetics.RandomBoard ? "Random owned game board, picked for each match."
            : "Game board: " + Sentence(product.Description) + " Click to equip; used from your next match.")
        : product.Kind == "Pet" ? (product.Pet == null ? "No pet in your matches. Click to unequip." : Sentence(product.Description) + " Includes its starting form. Finish matches of at least four turns without conceding for 100 pet XP. Forms unlock at 750, 2,750 and 6,750 total XP. Buying equips it for your next match.")
        : Sentence(product.Description)) + " " + price + " gold.";
    // Native details title the item ("Pirates! Includes:"); a name already ending in . ! ? gets no second stop.
    internal static string Sentence(string text)
    {
        text = (text ?? "").TrimEnd();
        return text.Length == 0 || ".!?…".IndexOf(text[text.Length - 1]) >= 0 ? text : text + ".";
    }
    // Recipe titles come from the recipe library; one broken row keeps its plain description instead of aborting a
    // whole catalogue seed or price refresh.
    internal static string DisplayName(LocalShopProduct product)
    {
        if (product.Kind != "Recipe") return product.Description;
        try { return NativeShopDecks.DisplayName(product, LocalShop.Default.Recipe(product.RecipeId)); }
        catch (Exception error) { Refuse("recipe title " + product.Id + ": " + (error.InnerException ?? error).Message); return product.Description; }
    }
    private static PegasusUtil.ProductType ItemType(LocalShopProduct product)
    {
        switch (product.Kind)
        {
            case "Pack": return PegasusUtil.ProductType.PRODUCT_TYPE_BOOSTER;
            case "CardBack": return PegasusUtil.ProductType.PRODUCT_TYPE_CARD_BACK;
            case "Coin": return PegasusUtil.ProductType.PRODUCT_TYPE_COSMETIC_COIN;
            case "HeroSkin": return PegasusUtil.ProductType.PRODUCT_TYPE_HERO;
            case "Pet": return product.Pet == null ? PegasusUtil.ProductType.PRODUCT_TYPE_DRAFT : PegasusUtil.ProductType.PRODUCT_TYPE_PET;
            case "Recipe": case "Deck": return PegasusUtil.ProductType.PRODUCT_TYPE_SELLABLE_DECK;
            // No native theme/board item exists: the tile is a plain ticket-style item with its name; clicks are local.
            case "HubTheme": case "Board": return PegasusUtil.ProductType.PRODUCT_TYPE_DRAFT;
            default: throw new InvalidOperationException("Unsupported native shop item.");
        }
    }
    internal static List<Network.ShopSection> BuildSections()
    {
        // Owner 2026-10-29: five left-drawer categories with native sub-tabs. A category or sub-tab
        // without offered products is omitted; Recipes with an offered Deck live only on its page.
        var sections = new List<Network.ShopSection>();
        var offered = NativeShopPurchase.Products;
        int tabCount = 0, subTabOrder = 0;
        string tab = null, tabLabel = null, tabIcon = null, subtab = null, subtabLabel = null;
        bool hasTab = false, hasHeader = false;
        Action<string, string, string> category = (id, label, icon) => { tab = id; tabLabel = label; tabIcon = icon; hasTab = false; subTabOrder = 0; };
        Action<string, string> begin = (id, label) => { subtab = id; subtabLabel = label; hasHeader = false; };
        Action<string, string, string, IEnumerable<LocalShopProduct>> section = (id, label, style, rows) => {
            var items = rows.ToArray();
            if (items.Length == 0) return;
            if (!hasTab) {
                sections.Add(new Network.ShopSection { InternalName = tab, Label = Text(tabLabel), SortOrder = (uint)sections.Count,
                    Attributes = Attributes("tab_id", tab, "tab_order", (tabCount++).ToString(), "tab_enabled", "true", "tab_icon", tabIcon),
                    ProductOrder = new List<Tuple<long,uint>>() });
                hasTab = true;
            }
            if (!hasHeader) {
                sections.Add(new Network.ShopSection {
                    InternalName = subtab, Label = Text(subtabLabel), SortOrder = (uint)sections.Count,
                    Attributes = Attributes("subtab_id", subtab, "tab_order", (subTabOrder++).ToString(), "tab_enabled", "true"),
                    ProductOrder = new List<Tuple<long,uint>>() });
                hasHeader = true;
            }
            // Pinned standard = 2 slots x 3 layouts; standardsingle = 4 x 3. A short last row's empty tiles are hidden
            // by HideEmptySlot, never shown as native "Reserved" teasers.
            for (int offset = 0, chunk = 0; offset < items.Length; chunk++)
            {
                int size = NativeShopLayout.Chunks(items.Length - offset, style).First();
                // Owner 2026-10-29: a deck group continuing in another native section says so ("Meta (cont.)").
                string heading = chunk > 0 && id.StartsWith("local-decks-", StringComparison.Ordinal) ? label + " (cont.)" : label;
                sections.Add(new Network.ShopSection {
                    InternalName = id + (chunk == 0 ? "" : "-" + chunk), Label = Text(heading), SortOrder = (uint)sections.Count,
                    Attributes = id == "local-packs" ? Attributes("tags", "hide_marketing_images,show_if_all_owned", "max_layout_count", NativeShopLayout.MaxRows.ToString())
                        : id.StartsWith("local-decks-", StringComparison.Ordinal) && NativeShopLayout.DeckHeaders != "none"
                        ? Attributes("style", style, "tags", "show_if_all_owned", "divider_options", NativeShopLayout.DeckHeaders)
                        : Attributes("style", style, "tags", "show_if_all_owned"),
                    ProductOrder = items.Skip(offset).Take(size).Select((p, i) => Tuple.Create(NativeShopPurchase.PmtId(p), (uint)i)).ToList() });
                offset += size;
            }
        };
        // Cards: packs only.
        category(NativeShopTabs.Cards, "Cards", NativeShopTabs.Icon(NativeShopTabs.Cards));
        begin("packs", "Packs");
        section("local-packs", "Local packs", "", offered.Where(p => p.Kind == "Pack"));
        var shopRecipes = LocalShop.Default.Recipes; // One clone of the library per catalogue build.
        foreach (var part in new[] { Tuple.Create(NativeShopTabs.Decks1, "Decks I"), Tuple.Create(NativeShopTabs.Decks2, "Decks II") }) {
            category(part.Item1, part.Item2, NativeShopTabs.Icon(part.Item1));
            foreach (var hero in NativeShopTabs.DeckClasses.Where(c => c.Tab == part.Item1)) {
                begin(hero.Id, hero.Label);
                foreach (var group in new[] { Tuple.Create("meta", "Meta"), Tuple.Create("fan", "Fan favourite") })
                    section("local-decks-" + hero.Hero.ToLowerInvariant() + "-" + group.Item1, group.Item2, NativeShopLayout.DeckRowStyle,
                        NativeShopDecks.ShopOrder(shopRecipes.Where(r => r.Hero == hero.Hero && r.Tag == group.Item1))
                            .SelectMany(r => offered.Where(p => p.RecipeId == r.Id && (p.Kind == "Deck" || p.Kind == "Recipe")
                                && !NativeShopDecks.PageVariant(p))));
            }
        }
        // Skins: hero skins, card backs, pets.
        category(NativeShopTabs.Skins, "Skins", NativeShopTabs.Icon(NativeShopTabs.Skins));
        begin("hero_skins", "Hero Skins");
        foreach (var hero in NativeShopTabs.DeckClasses)
            section("local-skins-" + hero.Hero.ToLowerInvariant(), hero.Label, "standardsingle", offered.Where(p => p.Kind == "HeroSkin" && p.Class == hero.Hero));
        begin("card_backs", "Card Backs");
        section("local-card-backs", "Card Backs", "standardsingle", offered.Where(p => p.Kind == "CardBack"));
        begin("coin_list", "Coins");
        section("local-coins", "Coins", "standardsingle", offered.Where(p => p.Kind == "Coin"));
        var pets = offered.Where(p => p.Kind == "Pet").ToArray();
        begin("pet_list", "Pets");
        section("local-pets-owned", "No pet", "standardsingle", pets.Where(p => p.Price == 0));
        section("local-pets-sale", "Pets — 5,000 gold each", "standardsingle", pets.Where(p => p.Price != 0));
        // Themes: game boards, main-menu themes. "Random owned" leads each; free looks follow, then the ones for sale.
        category(NativeShopTabs.Themes, "Themes", NativeShopTabs.Icon(NativeShopTabs.Themes));
        var boards = offered.Where(p => p.Kind == "Board").ToArray();
        begin("board_list", "Boards");
        section("local-boards-owned", "Random and classic boards", "standardsingle", boards.Where(p => p.Price == 0).OrderBy(p => p.Board.Value));
        section("local-boards-sale", "Expansion boards", "standardsingle", boards.Where(p => p.Price != 0));
        var themes = offered.Where(p => p.Kind == "HubTheme").ToArray();
        begin("theme_list", "Game Themes");
        section("local-themes-owned", "Random and Classic", "standardsingle",
            themes.Where(p => p.Price == 0).OrderBy(p => p.HubTheme == LocalHubCosmetics.RandomTheme ? 0 : 1));
        section("local-themes-sale", "Themes", "standardsingle", themes.Where(p => p.Price != 0));
        return sections;
    }
    // END catalogue builders
    private static bool SeedCatalog(ProductDataService __instance)
    {
        if (!Isolated) return true;
        if (!Enabled) return false;
        try
        {
            products = __instance; seeded = false; NativeShopPurchase.CatalogueVersion++;
            NativeShopDecks.Inject(Refuse);
            NativeShopPurchase.Seed(NativeEntryValid, Refuse);
            ((IDictionary)Read(products, "ProductMap")).Clear(); products.ProductCatalog.Clear(); products.ShopPlacementIds.Clear();
            var setProduct = Method(typeof(ProductDataService), "SetProduct");
            var refused = new HashSet<string>();
            foreach (var product in NativeShopPurchase.Products)
            {
                try
                {
                    if (!(bool)setProduct.Invoke(products, new object[] { BuildBundle(product, Quote(product)) }))
                        throw new InvalidOperationException("bundle refused");
                }
                catch (Exception error)
                {
                    // One unusable offer is excluded and logged; it never takes down the shop.
                    refused.Add(product.Id);
                    // Pinned SetProduct adds the PMT id first and ProductMap only after ProductInfo is built.
                    RecoverStep("refused bundle cleanup", () => products.ProductCatalog.Remove(NativeShopPurchase.PmtId(product)));
                    Refuse("excluded " + product.Id + ": " + (error.InnerException ?? error).Message);
                }
            }
            if (refused.Count != 0) NativeShopPurchase.Seed(p => !refused.Contains(p.Id) && NativeEntryValid(p), Refuse);
            var pages = products.CatalogNetworkPages;
            ((IDictionary)Read(pages, "m_pages")).Clear(); ((IDictionary)Read(pages, "m_placements")).Clear();
            // Commerce DTOs are runtime-only dependencies; constructing them does not enter the SDK.
            var getPage = Method(typeof(CatalogNetworkPages), "GetOrCreatePage");
            object placement = Activator.CreateInstance(getPage.GetParameters()[0].ParameterType);
            Write(placement, "placementId", PageId);
            Type pageType = MemberType(Member(placement.GetType(), "page"));
            object pageDto = Activator.CreateInstance(pageType); Write(pageDto, "pageId", PageId); Write(placement, "page", pageDto);
            var page = (CatalogNetworkPage)getPage.Invoke(pages, new[] { placement });
            page.Clear(); foreach (var section in BuildSections()) page.AddSection(section);
            products.ShopPlacementIds.Add(PageId);
            Write(products, "m_hasSetPages", true);
            products.ReconcileHearthstoneServerAndCommerceSdkProducts();
            Trace("local catalogue policy: no retail product events or preorder tags; availability from V10 catalogue");
            if (((IDictionary)Read(products, "ProductMap")).Count != NativeShopPurchase.Products.Count) throw new InvalidOperationException("local bundles failed reconciliation");
            seeded = true;
            return true;
        }
        catch (Exception error) { Fail("catalog seed", error); return false; }
    }
    private static bool NativeEntryValid(LocalShopProduct product)
    {
        int id = NativeShopPurchase.NativeItemId(product);
        if (product.Kind == "HeroSkin") return GameDbf.Card.GetRecord(id) != null && GameUtils.GetCardHeroRecordForCardId(id) != null;
        if (product.Kind == "CardBack") return GameDbf.CardBack.GetRecord(id) != null;
        if (product.Kind == "Coin") return GameDbf.CosmeticCoin.GetRecord(id)?.CardRecord != null;
        if (product.Kind == "Deck" || product.Kind == "Recipe") return GameDbf.SellableDeck.GetRecord(id) != null;
        if (product.Kind == "HubTheme") return true; // The look itself is checked live before verify=false (catalogue).
        if (product.Kind == "Board") return product.Board == LocalHubCosmetics.RandomBoard || GameDbf.Board.GetRecord(product.Board.Value) != null;
        if (product.Kind == "Pet") return product.Pet == null || NativeUIProbe.PetEntryValid(product);
        return GameDbf.Booster.GetRecord(id) != null;
    }
    private static bool UpdateAvailability(ProductCatalog __instance)
    {
        if (!Isolated) return true;
        try
        {
            Write(__instance, "m_hasUpdatedProductStatusOnce", true);
            bool available = Enabled && Prerequisite() == null, visibility = false;
            foreach (var product in __instance.Products)
            {
                var previous = product.Availability;
                // ValidProduct proves the PMT maps to an offered product with the current ledger quote.
                var local = available && ValidProduct(product) ? NativeShopPurchase.Product(product.PmtId) : null;
                // A blocked deck or recipe (e.g. full deck tray) stays visible as LOCKED: hiding it would break the
                // deck-then-recipe rows of the class tabs (owner 2026-10-29). Purchase still refuses it via Blocker.
                product.Availability = local == null ? ProductAvailability.RESTRICTED
                    : NativeShopPurchase.Owned(Profile, local, Profile.PurchasePrice(local.Id)) ? ProductAvailability.ALREADY_OWNED
                    : NativeShopPurchase.Blocker(Profile, local) == null ? ProductAvailability.CAN_PURCHASE
                    : local.Kind == "Deck" || local.Kind == "Recipe" ? ProductAvailability.LOCKED : ProductAvailability.RESTRICTED;
                visibility |= (previous == ProductAvailability.RESTRICTED) != (product.Availability == ProductAvailability.RESTRICTED);
            }
            // Pinned PopulateSectionsFromNetSections hides RESTRICTED products; Shop.Open repopulates dirty sections.
            if (visibility) __instance.MarkProductDataAsDirty();
        }
        catch (Exception error) { Fail("product availability", error); }
        return false;
    }
    private static bool ValidCatalog() => products != null
        && products.Catalog.Products.Count() == NativeShopPurchase.Products.Count
        && NativeShopPurchase.Products.All(p => ValidProduct(products.GetProductDataModel(NativeShopPurchase.PmtId(p))));
    // Identity only: the native catalogue is exactly the local one. No ledger quote, so per-frame callers stay cheap.
    private static bool LocalCatalogue() => products != null
        && products.Catalog.Products.Count() == NativeShopPurchase.Products.Count
        && NativeShopPurchase.Products.All(p => LocalModel(products.GetProductDataModel(NativeShopPurchase.PmtId(p))));
    // Purchase paths: native identity plus the current ledger quote.
    private static bool ValidProduct(ProductDataModel product)
    {
        return LocalModel(product) && Profile != null
            && product.Prices[0].Amount == Quote(NativeShopPurchase.Product(product.PmtId));
    }
    // Native identity of a local offer (PMT, one gold price, one native item of the right kind). Never prices or
    // ownership: Profile.PurchasePrice clones the offer and walks the collection for decks, far too slow per element.
    private static bool LocalModel(ProductDataModel product)
    {
        LocalShopProduct local = null;
        if (product != null) NativeShopPurchase.TryProduct(product.PmtId, out local);
        if (local != null && NativeShopPurchase.IsSelectable(local) && (local.Kind != "Pet" || local.Pet == null))
            // Pinned RewardFactory: a DRAFT bundle item becomes an ARENA_TICKET reward without an item id; the PMT is the identity.
            return product.Prices.Count == 1 && product.Prices[0].Currency == CurrencyType.GOLD
                && product.Items.Count == 1 && product.Items[0].Quantity == 1 && product.Items[0].ItemType == RewardItemType.ARENA_TICKET;
        return local != null && product.Prices.Count == 1 && product.Prices[0].Currency == CurrencyType.GOLD
            && product.Items.Count == 1 && product.Items[0].Quantity == 1
            && product.Items[0].ItemId == NativeShopPurchase.NativeItemId(local)
            && (local.Kind != "Pack" || product.Items[0].Booster != null
                && (int)product.Items[0].Booster.Type == FairPackRules.NativeBoosterId(local.PackType))
            && (local.Kind != "CardBack" || product.Items[0].CardBack?.CardBackId == local.CardBack.Value)
            && (local.Kind != "Coin" || product.Items[0].Card?.CardId != null && product.Items[0].Card.CardId == GameDbf.CosmeticCoin.GetRecord(local.Coin.Value)?.CardRecord?.NoteMiniGuid)
            && (local.Kind != "HeroSkin" || product.Items[0].Card?.CardId == local.HeroSkin)
            && product.Items[0].ItemType == (local.Kind == "Pack" ? RewardItemType.BOOSTER
                : local.Kind == "CardBack" ? RewardItemType.CARD_BACK : local.Kind == "HeroSkin" ? RewardItemType.HERO_SKIN
                : local.Kind == "Coin" ? RewardItemType.CUSTOM_COIN : local.Kind == "Pet" ? RewardItemType.PET : RewardItemType.SELLABLE_DECK);
    }

    // Every offered Deck resolves to [Deck, Recipe] when its Recipe is offered, labelled for the native SKU toggle.
    // Each variant keeps its PmtId, price and Buy (ProductPage.TryBuy buys the selected variant).
    private static void DeckVariants(ProductDataModel product, List<ProductDataModel> allProducts, ref IEnumerable<ProductDataModel> __result)
    {
        if (!Isolated || product == null || allProducts == null) return;
        try {
            if (!NativeShopPurchase.TryProduct(product.PmtId, out var deck) || deck.Kind != "Deck") return;
            var recipeProduct = NativeShopPurchase.Products.FirstOrDefault(p => p.Kind == "Recipe" && p.RecipeId == deck.RecipeId);
            if (recipeProduct == null) return;
            long recipePmt = NativeShopPurchase.PmtId(recipeProduct);
            var recipe = allProducts.FirstOrDefault(model => model != null && model.PmtId == recipePmt);
            if (recipe == null) return;
            product.VariantName = "Ready-made"; recipe.VariantName = "Recipe";
            __result = new[] { product, recipe };
        }
        catch (Exception error) { Refuse("deck variants: " + (error.InnerException ?? error).Message); }
    }
    // Preserve the local variant labels and open every deck page on its ready-made Deck.
    private static void DeckFirstVariant(SellableDeckProductPage __instance, ProductDataModel chosenProduct, ref ProductDataModel __result)
    {
        if (!Isolated || chosenProduct?.Variants == null || chosenProduct.Variants.Count != 2) return;
        try {
            if (!NativeShopPurchase.TryProduct(chosenProduct.Variants[0].PmtId, out var deck) || deck.Kind != "Deck") return;
            if (!NativeShopPurchase.TryProduct(chosenProduct.Variants[1].PmtId, out var recipe)
                || recipe.RecipeId != deck.RecipeId || !NativeShopDecks.PageVariant(recipe)) return;
            chosenProduct.Variants[0].VariantName = "Ready-made"; chosenProduct.Variants[1].VariantName = "Recipe";
            __result = chosenProduct.Variants[0];
            Write(__instance, "m_firstVariant", __result);
        }
        catch (Exception error) { Refuse("deck first variant: " + (error.InnerException ?? error).Message); }
    }
    // Owner 2026-10-30: the label of a Deck tile's Recipe price plate (NativeShopTilePrices), or null for none. Only while
    // the tile shows its own price (buyable or locked, not owned) and its page's Recipe variant is buyable or locked.
    internal static string RecipeTileText(ProductDataModel shown, NativeShopArtMath.RecipePlate plate)
    {
        if (!plate.Enabled || shown?.Variants == null || shown.Variants.Count != 2 || !Priced(shown.Availability)) return null;
        var recipe = shown.Variants[1];
        if (shown.Variants[0]?.PmtId != shown.PmtId || recipe?.Prices == null || recipe.Prices.Count == 0 || recipe.Prices[0] == null
            || !Priced(recipe.Availability)) return null;
        if (!NativeShopPurchase.TryProduct(shown.PmtId, out var deck) || deck.Kind != "Deck"
            || !NativeShopPurchase.TryProduct(recipe.PmtId, out var local) || local.Kind != "Recipe" || local.RecipeId != deck.RecipeId) return null;
        return plate.Label(((long)recipe.Prices[0].Amount).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static bool Priced(ProductAvailability availability) =>
        availability == ProductAvailability.CAN_PURCHASE || availability == ProductAvailability.LOCKED;

    private static void LocalTabs(ref List<ShopTabController.TabData> __result)
    {
        try { if (Isolated) __result.RemoveAll(tab => !NativeShopTabs.Ids.Contains(tab.ParentTabData?.Id)); }
        catch (Exception error) { Fail("local tabs", error); }
    }

    // Patch MoveNext, not the tiny DisplayDivder getter (Mono may inline it). Keep FetchDivider and its native
    // buffering, positioning and pool lifecycle; replace only previous != null && (current.DisplayDivder || previous.DisplayDivder).
    private static IEnumerable<CodeInstruction> DeckSectionDividers(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var sites = new List<int>();
        for (int i = 0; i + 8 < code.Count; i++)
            if (code[i].opcode == OpCodes.Ldloc_3 && code[i + 1].opcode == OpCodes.Brfalse
                && code[i + 2].opcode == OpCodes.Ldarg_0 && code[i + 3].opcode == OpCodes.Ldfld
                && code[i + 3].operand is FieldInfo field && field.FieldType == typeof(ProductSectionDataModel)
                && code[i + 4].operand is MethodInfo getter && getter.DeclaringType == typeof(ProductSectionDataModel)
                && getter.Name == "get_DisplayDivder" && code[i + 4].Calls(getter)
                && (code[i + 5].opcode == OpCodes.Brtrue || code[i + 5].opcode == OpCodes.Brtrue_S)
                && code[i + 6].opcode == OpCodes.Ldloc_3
                && code[i + 7].Calls(getter) && code[i + 8].opcode == OpCodes.Brfalse
                && (Equals(code[i + 1].operand, code[i + 8].operand)
                    || code[i + 1].operand is Label firstSkip && code[i + 8].operand is Label secondSkip
                        && code.Any(target => target.labels.Contains(firstSkip) && target.labels.Contains(secondSkip)))) sites.Add(i);
        if (sites.Count != 1) throw new InvalidOperationException("ShopBrowser divider guard changed: " + sites.Count);
        int at = sites[0];
        // Removed instructions have no incoming branches or exception boundaries in the pinned iterator.
        foreach (int offset in new[] { 1, 4, 5, 6, 7 })
            if (code[at + offset].labels.Count != 0 || code[at + offset].blocks.Count != 0)
                throw new InvalidOperationException("ShopBrowser divider guard has unexpected control flow");
        var currentLoad = code.GetRange(at + 2, 2);
        var skip = code[at + 8];
        code.RemoveRange(at + 1, 8);
        code.InsertRange(at + 1, currentLoad.Concat(new[] {
            new CodeInstruction(OpCodes.Call, ((Func<ProductSectionDataModel, ProductSectionDataModel, bool>)ShouldFetchDivider).Method), skip }));
        return code;
    }

    private static bool ShouldFetchDivider(ProductSectionDataModel previous, ProductSectionDataModel current)
    {
        if (previous != null) return current.DisplayDivder || previous.DisplayDivder;
        return Enabled && current != null && current.DisplayDivder
            && current.SectionId != null && current.SectionId.StartsWith("local-decks-", StringComparison.Ordinal);
    }

    // Live 2026-10-25: 5 skins in a 4-wide section showed 3 "Reserved / Something new is coming!" tiles. Pinned
    // LoadSlotsCoroutine calls this for every slot after RefreshData and before positioning; an empty local slot is
    // disabled (its GameObject inactive, skipped by positioning) instead of bound to a reserved-slot teaser. The next
    // section load re-enables it through ShopSlot.RefreshData. A failure leaves the native slot as it was.
    private static bool HideEmptySlot(ShopSection __instance, ShopSlot __0)
    {
        if (!Isolated) return true;
        try
        {
            if (__instance == null || __0 == null || !NativeShopLayout.HideEmptySlot(__instance.ProductSectionData?.SectionId, __0.IsEmpty)) return true;
            __0.SetEnabled(false);
            return false;
        }
        catch (Exception error) { Refuse("empty shop slot: " + error.Message); return true; }
    }

    // Live 2026-10-25: two empty grey sub-tab buttons after Card Backs (6 native slots, 4 local sub-tabs). Pinned
    // RebindSubTabData shows the active tab's sub-tabs and only sends HIDE_TAB to the rest. Local shop: each unused slot's
    // exclusive root (its bar container, plate included) is deactivated after the rebind and again every frame while the
    // shop is open (SetupSubTab's late Hide and the bar's own states run outside a rebind); a root this hook deactivated is
    // reactivated before a rebind that shows its slot, so SHOW_TAB never reaches an inactive widget. Objects the game
    // deactivated itself are never touched.
    private static readonly HashSet<GameObject> hiddenSubTabs = new HashSet<GameObject>();
    private static void ShowUsedSubTabs(ShopTabController __instance) => SyncSubTabs(__instance, false);
    private static void HideUnusedSubTabs(ShopTabController __instance) => SyncSubTabs(__instance, true);
    private static void SyncSubTabs(ShopTabController controller, bool hide)
    {
        if (!Isolated || controller == null) return;
        try
        {
            subTabRoots = null; subTabSlots = null; // Recomputed below; a failing or partial sync leaves no cache.
            hiddenSubTabs.RemoveWhere(root => root == null);
            int active = controller.ActiveTabIndex;
            if (active < 0 || active >= controller.GetNumberOfTabs()) return; // The native rebind returns early too.
            int used = controller.GetNumberOfActiveSubTabs();
            var list = SubTabList(controller);
            var slots = new List<Transform>();
            foreach (object tab in list)
            {
                var subTab = tab as ShopSubTab;
                if (subTab == null) // Still loading: roots are only computed over the whole bar.
                {
                    if (!Enabled) { foreach (var root in hiddenSubTabs) root.SetActive(true); hiddenSubTabs.Clear(); }
                    return;
                }
                slots.Add(subTab.transform);
            }
            var roots = NativeShopLayout.ExclusiveRoots(slots, slot => slot.parent, controller.transform);
            var snapshot = new object[list.Count];
            list.CopyTo(snapshot, 0);
            // A slot without its own subtree (shared with another slot, the controller or a scene root) would hide the bar.
            if (roots.Any(root => root == null))
            {
                Refuse("sub-tab slots share a root");
                subTabSlots = snapshot; subTabRoots = new GameObject[0]; // Refused layout: per-frame ticks skip it until it changes.
                return;
            }
            for (int i = 0; i < roots.Count; i++)
            {
                bool unused = Enabled && NativeShopLayout.UnusedSubTab(i, used);
                if (!unused)
                {
                    if (hiddenSubTabs.Count == 0) continue; // Per-frame idle: nothing to restore, no allocation.
                    var slot = slots[i];
                    foreach (var hidden in hiddenSubTabs.Where(root => NativeShopLayout.OnChain(slot, root.transform, node => node.parent)).ToList())
                    { hiddenSubTabs.Remove(hidden); hidden.SetActive(true); }
                }
                else if (hide && roots[i].gameObject.activeSelf) { roots[i].gameObject.SetActive(false); hiddenSubTabs.Add(roots[i].gameObject); }
            }
            subTabSlots = snapshot; subTabRoots = roots.Select(root => root.gameObject).ToArray();
        }
        catch (Exception error) { subTabRoots = null; subTabSlots = null; Refuse("sub-tab visibility: " + error.Message); }
    }
    // Per-frame enforcement (TickSubTabs) reuses the roots of the last full sync while the controller, its sub-tab list
    // and every slot object are unchanged: it only re-hides unused roots and restores used ones (no LINQ, allocation or
    // reflection). Any change, a destroyed root, or a missing cache runs the full sync instead. Rebinds always run it.
    private static object subTabController;
    private static IList subTabList;
    private static object[] subTabSlots;
    private static GameObject[] subTabRoots;
    // ShopTabController.m_subTabs is created once with the controller and only filled in place.
    private static IList SubTabList(ShopTabController controller)
    {
        // Full sync / rebind path only (the per-frame tick reads the cache): always re-read, so a reassigned list can never pin a dead one.
        var current = (IList)Read(controller, "m_subTabs");
        if (!ReferenceEquals(controller, subTabController) || !ReferenceEquals(current, subTabList))
        {
            subTabRoots = null; subTabSlots = null;
            subTabList = current;
            subTabController = controller;
        }
        return subTabList;
    }
    private static void SyncSubTabsFrame(ShopTabController controller)
    {
        if (!Isolated || controller == null) return;
        var roots = subTabRoots; var slots = subTabSlots; var list = subTabList;
        bool cached = roots != null && slots != null && list != null && ReferenceEquals(controller, subTabController) && list.Count == slots.Length;
        for (int i = 0; cached && i < slots.Length; i++) cached = ReferenceEquals(list[i], slots[i]);
        for (int i = 0; cached && i < roots.Length; i++) cached = roots[i] != null;
        if (!cached) { SyncSubTabs(controller, true); return; }
        try
        {
            int active = controller.ActiveTabIndex;
            if (active < 0 || active >= controller.GetNumberOfTabs()) return; // The native rebind returns early too.
            int used = controller.GetNumberOfActiveSubTabs();
            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                // Exclusive roots: the only hidden root on a used slot's chain is its own (the full sync restored older ones).
                if (!(Enabled && NativeShopLayout.UnusedSubTab(i, used))) { if (hiddenSubTabs.Count != 0 && hiddenSubTabs.Remove(root)) root.SetActive(true); }
                else if (root.activeSelf) { root.SetActive(false); hiddenSubTabs.Add(root); }
            }
        }
        catch (Exception error) { subTabRoots = null; subTabSlots = null; Refuse("sub-tab visibility: " + error.Message); }
    }
    // Shop.m_shopTabController is a serialized reference set with the shop; read once per shop instance.
    private static Shop subTabShop;
    private static ShopTabController subTabShopController;
    private static void TickSubTabs()
    {
        var shop = Shop.Get();
        if (shop == null || !shop.IsOpen()) return;
        if (!ReferenceEquals(shop, subTabShop) || subTabShopController == null)
        { subTabShopController = Read(shop, "m_shopTabController") as ShopTabController; subTabShop = shop; }
        SyncSubTabsFrame(subTabShopController);
        TickCradle(subTabShopController);
    }
    // Owner review 2026-10-28: past the used sub-tabs the bar's cradle mesh (one mesh, six sockets) still shows empty grey
    // sockets. With fewer sub-tabs than sockets the local shop hides the cradle and its shadow; the buttons stay.
    // [Shop] SubTabCradle = native keeps it.
    private static ShopTabController cradleController;
    private static Renderer[] cradle;
    private static void TickCradle(ShopTabController controller)
    {
        if (controller == null) return;
        // Rescanned while empty (the bar may still be loading), at most every 30 frames.
        if (!ReferenceEquals(controller, cradleController) || cradle == null || cradle.Length == 0 && Time.frameCount % 30 == 0)
        {
            cradle = controller.GetComponentsInChildren<Renderer>(true)
                .Where(r => r.name == "Shop_SubTab_Cradle" || r.transform.parent != null && r.transform.parent.name == "CradleShadow").ToArray();
            cradleController = controller;
        }
        // The per-frame path reads the cached sub-tab list (SubTabList, refreshed by the rebind sync), never reflection.
        var list = ReferenceEquals(controller, subTabController) ? subTabList : null;
        if (list == null) return;
        bool hide = NativeShopArtMath.HideCradle(Enabled, cradleMode?.Value, controller.GetNumberOfActiveSubTabs(), list.Count);
        foreach (var renderer in cradle) if (renderer != null && renderer.enabled == hide) renderer.enabled = !hide;
    }
#if !SOLOSTONE_RELEASE
    // Test-control `ui all=1`: every sub-tab slot with its chosen root, and each renderer the bar draws, so a live run
    // shows which object paints a plate. Read only.
    internal static MiniJSON.JsonNode SubTabDiagnostics()
    {
        try
        {
            var shop = Shop.Get();
            if (!Isolated || shop == null || !shop.IsOpen()) return null;
            var controller = Read(shop, "m_shopTabController") as ShopTabController;
            if (controller == null) return new MiniJSON.JsonNode { { "error", "no tab controller" } };
            var subTabs = ((IList)Read(controller, "m_subTabs")).Cast<object>().Select(tab => tab as ShopSubTab).ToList();
            var slots = subTabs.Where(tab => tab != null).Select(tab => tab.transform).ToList();
            var roots = subTabs.All(tab => tab != null) ? NativeShopLayout.ExclusiveRoots(slots, slot => slot.parent, controller.transform) : null;
            Transform bar = slots.Count == 0 ? null : slots[0];
            while (bar != null && slots.Any(slot => !slot.IsChildOf(bar))) bar = bar.parent;
            string Path(Transform node)
            {
                var names = new List<string>();
                for (int depth = 0; node != null && depth < 12 && node != bar; depth++, node = node.parent) names.Add(node.name);
                names.Reverse();
                return (node == bar && bar != null ? "<bar>/" : "") + string.Join("/", names);
            }
            var rows = new List<object>();
            for (int i = 0; i < subTabs.Count; i++)
            {
                var tab = subTabs[i];
                var root = roots?[i];
                rows.Add(new MiniJSON.JsonNode { { "index", i }, { "loaded", tab != null },
                    { "unused", NativeShopLayout.UnusedSubTab(i, controller.GetNumberOfActiveSubTabs()) },
                    { "id", tab?.CurrentData?.Id }, { "path", tab == null ? null : Path(tab.transform) },
                    { "activeInHierarchy", tab != null && tab.gameObject.activeInHierarchy },
                    { "widget", tab?.Widget == null ? null : Path(tab.Widget.transform) },
                    { "root", root == null ? null : Path(root) }, { "rootActiveSelf", root != null && root.gameObject.activeSelf },
                    { "hiddenByShop", root != null && hiddenSubTabs.Contains(root.gameObject) },
                    { "x", tab == null ? 0f : tab.transform.position.x }, { "z", tab == null ? 0f : tab.transform.position.z } });
            }
            var drawn = new List<object>();
            if (bar != null)
                foreach (var renderer in bar.GetComponentsInChildren<Renderer>(false).Where(item => item.enabled && item.isVisible).Take(80))
                    drawn.Add(new MiniJSON.JsonNode { { "path", Path(renderer.transform) }, { "type", renderer.GetType().Name },
                            { "x", renderer.bounds.center.x }, { "z", renderer.bounds.center.z },
                            { "slot", slots.FindIndex(slot => renderer.transform.IsChildOf(slot)) },
                            { "root", roots == null ? -1 : roots.FindIndex(root => root != null && renderer.transform.IsChildOf(root)) } });
            return new MiniJSON.JsonNode { { "activeTab", controller.ActiveTabIndex }, { "used", controller.GetNumberOfActiveSubTabs() },
                { "tabSlots", ((IList)Read(controller, "m_tabs")).Count }, { "tabs", controller.GetNumberOfTabs() },
                { "bar", bar == null ? null : bar.name }, { "slots", TestControlProtocol.List(rows) }, { "renderers", TestControlProtocol.List(drawn) } };
        }
        catch (Exception error) { return new MiniJSON.JsonNode { { "error", (error.InnerException ?? error).Message } }; }
    }
#endif

#if !SOLOSTONE_RELEASE
    // Test-control only: the browser scroller's height inputs (registered items, dividers, viewport) along its scroll axis.
    internal static MiniJSON.JsonNode ScrollDiagnostics()
    {
        try
        {
            var shop = Shop.Get();
            if (!Isolated || shop == null || !shop.IsOpen()) return null;
            var scroller = Read(shop, "m_browserScroller") as UIBScrollable;
            var browser = Read(shop, "m_browser") as ShopBrowser;
            if (scroller == null) return new MiniJSON.JsonNode { { "error", "no scroller" } };
            int axis = (int)scroller.m_ScrollPlane;
            MiniJSON.JsonNode Span(string name, Vector3 min, Vector3 max) =>
                new MiniJSON.JsonNode { { "name", name }, { "min", Math.Min(min[axis], max[axis]) }, { "max", Math.Max(min[axis], max[axis]) } };
            var items = new List<object>();
            foreach (var item in (Read(scroller, "m_scrollableItems") as List<UIBScrollableItem>) ?? new List<UIBScrollableItem>())
            {
                if (item == null || !item.IsActive()) continue;
                item.GetWorldBounds(out var min, out var max);
                var row = Span(item.name, min, max);
                var renderers = item.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled).ToArray();
                if (renderers.Length != 0)
                {
                    row["drawnMin"] = renderers.Min(r => Math.Min(r.bounds.min[axis], r.bounds.max[axis]));
                    row["drawnMax"] = renderers.Max(r => Math.Max(r.bounds.min[axis], r.bounds.max[axis]));
                }
                items.Add(row);
            }
            var dividers = new List<object>();
            if (browser != null)
                foreach (var divider in (Read(browser, "m_activeDividers") as List<ShopDivider>) ?? new List<ShopDivider>())
                {
                    if (divider == null) continue;
                    var item = Read(divider, "m_scrollableItem") as UIBScrollableItem;
                    if (item == null) { dividers.Add(new MiniJSON.JsonNode { { "name", divider.name }, { "item", false } }); continue; }
                    item.GetWorldBounds(out var min, out var max);
                    var row = Span(divider.name, min, max);
                    row["active"] = divider.isActiveAndEnabled; row["enabled"] = divider.IsElementEnabled; row["itemActive"] = item.IsActive();
                    dividers.Add(row);
                }
            var bounds = Read(scroller, "m_ScrollBounds") as BoxCollider;
            return new MiniJSON.JsonNode { { "axis", axis }, { "heightMode", Read(scroller, "m_HeightMode")?.ToString() },
                { "polled", (float)Read(scroller, "m_PolledScrollHeight") }, { "itemsHeight", (float)Call(scroller, "GetScrollableItemsHeight") },
                { "bottomPadding", (float)Read(scroller, "m_ScrollBottomPadding") }, { "scroll", scroller.GetScroll() },
                { "viewport", bounds == null ? null : Span("bounds", bounds.bounds.min, bounds.bounds.max) },
                { "items", TestControlProtocol.List(items) }, { "dividers", TestControlProtocol.List(dividers) } };
        }
        catch (Exception error) { return new MiniJSON.JsonNode { { "error", (error.InnerException ?? error).Message } }; }
    }
#endif

    // Retail drops zero gold in UpdatePrices; owned decks still need a valid gold-only model.
    private static void LocalPrice(ProductInfo __instance, Network.Bundle __0)
    {
        if (!Isolated) return;
        try {
            if (__0 == null || !__0.PMTProductID.HasValue
                || !NativeShopPurchase.TryProduct(__0.PMTProductID.Value, out _)) return;
            var price = __0.GoldCost.Value.CurrentCost;
            var amount = new PriceInfo.Price { RealPrice = price, DisplayPrice = price.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            ((IDictionary<CurrencyType, PriceInfo>)Read(__instance, "m_vcPrices"))[CurrencyType.GOLD] =
                new PriceInfo { CurrentPrice = amount, OriginalPrice = amount };
        }
        catch (Exception error) { Fail("local price", error); }
    }

    private static ProductInfo FindInfo(long pmtId)
    {
        object[] args = { pmtId, null };
        if (!(bool)Method(typeof(ProductDataService), "TryGetProduct").Invoke(products, args))
            throw new InvalidOperationException("Missing local ProductInfo.");
        return (ProductInfo)args[1];
    }

    private static void RefreshPrices()
    {
        NativeShopPurchase.CatalogueVersion++; // Quotes and models change below, even if a step throws.
        priceRefresh.Invalidate(); // Only a refresh that completes marks the models fresh.
        object profile = Profile, ledger = Profile.LedgerIdentity, shop = LocalShop.Default;
        foreach (var local in NativeShopPurchase.Products)
        {
            int price = Profile.PurchasePrice(local.Id);
            var bundle = BuildBundle(local, Quote(local));
            Call(products, "SetProduct", bundle);
            var info = FindInfo(NativeShopPurchase.PmtId(local));
            // Deck pages show this in their text panel: owned/missing copies follow every quote rebuild; a text
            // failure only falls back to the generic description.
            string description = Description(local, price);
            if (local.Kind == "Recipe" || local.Kind == "Deck") RecoverStep("deck details " + local.Id, () => description = DeckDetails(local, price));
            Write(info, "Description", description);
            var model = products.GetProductDataModel(NativeShopPurchase.PmtId(local));
            model.Prices[0] = info.GetPriceDataModel(CurrencyType.GOLD);
            model.Description = info.Description;
            if (NativeShopPurchase.IsSelectable(local))
                model.Name = NativeShopPurchase.TileName(local, NativeShopPurchase.Owned(Profile, local, price), NativeShopPurchase.Equipped(Profile, local));
        }
        priceRefresh.Complete(profile, ledger, shop, products, NativeShopPurchase.CatalogueVersion);
    }
    private static readonly NativeShopRefreshState priceRefresh = new NativeShopRefreshState();
    // Open skip decision: the memoized inputs are unchanged AND every offer's native model still carries the memoized quote
    // (native code can rebuild or edit models outside our seams; the unconditional rebuild on open used to repair that).
    private static bool RefreshNeeded()
    {
        return !priceRefresh.IsFresh(Profile, Profile.LedgerIdentity, LocalShop.Default, products, NativeShopPurchase.CatalogueVersion) || !ModelPricesMatch();
    }
    private static bool ModelPricesMatch()
    {
        try
        {
            foreach (var local in NativeShopPurchase.Products)
            {
                var model = products.GetProductDataModel(NativeShopPurchase.PmtId(local));
                if (model == null || model.Prices == null || model.Prices.Count == 0 || model.Prices[0] == null
                    || model.Prices[0].Amount != Quote(local)) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }
    // Deck page text per (offer, quote) for one committed ledger and shop: a rebuild with unchanged inputs reuses it.
    private static readonly Dictionary<string, string> deckDetails = new Dictionary<string, string>(StringComparer.Ordinal);
    private static object deckDetailsLedger, deckDetailsShop;
    private static string DeckDetails(LocalShopProduct local, int price)
    {
        object ledger = Profile.LedgerIdentity; var shop = LocalShop.Default;
        if (!ReferenceEquals(ledger, deckDetailsLedger) || !ReferenceEquals(shop, deckDetailsShop))
        { deckDetails.Clear(); deckDetailsLedger = ledger; deckDetailsShop = shop; }
        // The offered kit follows from the same ledger (unredeemed kits) and quote, so the key stays (offer, quote).
        string key = local.Id + "\n" + price.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (deckDetails.TryGetValue(key, out string text)) return text;
        int kitCap = local.Kind == "Deck" ? Profile.DeckKitFor(price)?.GoldCap ?? 0 : 0;
        text = NativeShopDecks.Details(local, shop.RecipeRef(local.RecipeId), Profile.QuoteMissingCopies(local.RecipeId), price, kitCap);
        deckDetails[key] = text;
        return text;
    }

    private static bool WalletUpdate()
    {
        if (!Isolated) return true;
        RefreshDust();
        RefreshGold();
        if (Enabled && Profile != null) KeepRealMoneyOff();
        return false;
    }
    // Dust (NetCache: published ledger balance plus uncommitted crafting) and gold (the ledger GoldBalance) are cacheable
    // here: their online refresh never runs offline, so these fill the caches the bottom-bar frames (Collection dust,
    // HUB and Shop gold) and the shop wallet read. Independent of the shop option, and a failure only logs: it must
    // never disable the shop or crafting. Runs every frame in every scene (CurrencyManager.Update prefix): an unchanged
    // balance only compares (and checks IsCached while its frame is up), and in steady state nothing allocates.
    private static readonly LocalCurrency dustCurrency = new LocalCurrency(CurrencyType.DUST, "dust");
    private static readonly LocalCurrency goldCurrency = new LocalCurrency(CurrencyType.GOLD, "gold");
    internal static void RefreshDust()
    {
        if (!Isolated || !NativeUIProbe.Active) return;
        long dust;
        try { dust = NetCache.Get()?.GetArcaneDustBalance() ?? 0; }
        catch (Exception error) { dustCurrency.Warn(error); return; }
        dustCurrency.Publish(dust, NativeCollectionStatus.DustShown);
    }
    // force: re-check the cache even when the balance is unchanged (native refresh and shop wallet refresh paths).
    internal static void RefreshGold(bool force = false)
    {
        var profile = Profile;
        if (!Isolated || !NativeUIProbe.Active || profile == null) return;
        goldCurrency.Publish(profile.Gold, force || NativeCollectionStatus.GoldShown);
    }
    private sealed class LocalCurrency
    {
        private static readonly object[] noArgs = new object[0];
        private readonly string name;
        private readonly object[] typeArgs, balanceArgs = new object[1];
        private long last = -1;
        private bool warned;
        private CurrencyManager manager;
        private object cache;
        private MethodInfo getCache, updateBalance, updateWallet, isCachedInfo;
        private Func<bool> isCached; // bound to cache: no boxing per check
        internal LocalCurrency(CurrencyType type, string name) { this.name = name; typeArgs = new object[] { type }; }
        internal long Last => last;
        // watch: its frame is up, so also re-publish if the cache lost its CACHED state.
        internal void Publish(long amount, bool watch)
        {
            if (amount == last && !watch) return;
            try
            {
                if (!ServiceManager.IsAvailable<CurrencyManager>()) return;
                var current = ServiceManager.Get<CurrencyManager>();
                if (!ReferenceEquals(current, manager)) { manager = current; cache = null; }
                if (getCache == null)
                {
                    getCache = Method(manager.GetType(), "GetCurrencyCache");
                    updateWallet = Method(manager.GetType(), "UpdateWallet");
                }
                if (cache == null)
                {
                    cache = getCache.Invoke(manager, typeArgs);
                    if (updateBalance == null)
                    {
                        Type cacheType = cache.GetType();
                        updateBalance = Method(cacheType, "UpdateBalance");
                        isCachedInfo = Method(cacheType, "IsCached");
                    }
                    try { isCached = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), cache, isCachedInfo); }
                    catch { isCached = null; } // Reflection fallback below allocates one box per check.
                }
                if (amount == last && (isCached != null ? isCached() : (bool)isCachedInfo.Invoke(cache, noArgs))) return;
                balanceArgs[0] = amount;
                updateBalance.Invoke(cache, balanceArgs);
                updateWallet.Invoke(manager, typeArgs);
                if (last != amount) Trace("wallet " + name + " " + amount);
                last = amount;
            }
            catch (Exception error)
            {
                cache = null; // Re-resolve next time instead of reusing a possibly stale object.
                Warn(error);
            }
        }
        internal void Warn(Exception error)
        {
            if (!warned) log?.LogWarning("[NativeShop] " + name + " display refresh failed: " + error);
            warned = true;
        }
    }
    // Native CurrencyManager.Update (skipped above) is the only writer of AllowRealMoneyPurchases; keep it off cheaply.
    private static CurrencyManager walletManager;
    private static WalletDataModel walletModel;
    private static void KeepRealMoneyOff()
    {
        try
        {
            if (!ServiceManager.IsAvailable<CurrencyManager>()) return;
            var manager = ServiceManager.Get<CurrencyManager>();
            if (!ReferenceEquals(manager, walletManager) || walletModel == null)
            {
                walletModel = Read(manager, "m_walletDataModel") as WalletDataModel;
                walletManager = walletModel == null ? null : manager;
            }
            if (walletModel != null && walletModel.AllowRealMoneyPurchases) walletModel.AllowRealMoneyPurchases = false;
        }
        catch (Exception error) { Fail("wallet", error); }
    }
    private static void RefreshWallet()
    {
        var manager = ServiceManager.Get<CurrencyManager>() ?? throw new InvalidOperationException("wallet service unavailable");
        RefreshGold(true);
        if (goldCurrency.Last != Profile.Gold) throw new InvalidOperationException("gold wallet not published");
        var wallet = Read(manager, "m_walletDataModel") ?? throw new InvalidOperationException("wallet model unavailable");
        Write(wallet, "AllowRealMoneyPurchases", false);
        Call(manager, "UpdateVirtualCurrencyWallet");
    }
    private static bool RefreshCurrency(object __instance)
    {
        if (!Isolated) return true;
        var type = (CurrencyType)Read(__instance, "Type");
        if (type == CurrencyType.DUST) { RefreshDust(); return false; }
        if (type == CurrencyType.GOLD) { RefreshGold(true); return false; }
        if (!Enabled) return false;
        try { Write(__instance, "m_status", Enum.ToObject(Read(__instance, "m_status").GetType(), 0)); }
        catch (Exception error) { Fail("currency refresh", error); }
        return false;
    }

    private static bool Open(Box __instance)
    {
        if (!Isolated) return true;
        try
        {
            if (!Enabled || !ready) { Refuse("shop disabled or not ready"); return false; }
            if (__instance.IsBusy() || __instance.GetState() == Box.State.LOADING || !SceneMgr.IsInitialized()
                || SceneMgr.Get().GetMode() != SceneMgr.Mode.HUB || SceneMgr.Get().IsTransitionNowOrPending()
                || LocalLadder.Screen == "Playing" || LocalLadder.Screen == "Starting" || Profile.Pending != null
                || Profile.PendingRewardPresentation != null || Profile.PackRevealPending)
            { Refuse("home busy or match pending"); return false; }
            if (FriendChallengeMgr.Get()?.HasChallenge() == true || __instance.m_StoreButton.IsVisualClosed())
            { Refuse("store button closed or challenge active"); return false; }
            if ((bool)Call(Read(__instance, "m_railroadManager"), "ShouldDisableButtonType", Box.ButtonType.STORE))
            { Refuse("railroad disables store"); return false; }
            string prerequisite = Prerequisite();
            if (prerequisite != null) { Refuse(prerequisite); return false; }
            var store = StoreManager.Get();
            if (store.IsShownOrWaitingToShow() || store.IsShopBeingDestroyed) { Refuse("store already open or closing"); return false; }
            __instance.FireButtonPressEvent(Box.ButtonType.STORE);
            FriendChallengeMgr.Get()?.OnStoreOpened();
            previousBoxState = __instance.GetState(); openingBox = __instance;
            Write(__instance, "m_previousState", previousBoxState);
            Call(__instance, "ChangeState", Box.State.LOADING);
            store.RegisterStoreShownListener((Action<ShopType>)Delegate.CreateDelegate(typeof(Action<ShopType>), __instance, Method(typeof(Box), "OnStoreShown")));
            store.RegisterStoreHiddenListener((Action<ShopType>)Delegate.CreateDelegate(typeof(Action<ShopType>), __instance, Method(typeof(Box), "OnStoreHidden")));
            opening = true; openBegan = Time.realtimeSinceStartup;
            // Collection changes while the shop is closed can lower ready-made deck quotes too; an unchanged ledger, shop
            // and catalogue since the last complete rebuild leaves the native models as they are.
            if (RefreshNeeded()) RefreshPrices();
            products.UpdateProductStatus();
            string[] deckHeroes = null; // An unavailable recipe library offers no decks; OpenTab then ignores them.
            try { deckHeroes = NativeShopPurchase.Products.Where(p => p.Kind == "Deck" || p.Kind == "Recipe")
                .Select(p => LocalShop.Default.Library.FindRecipe(p.RecipeId)?.Hero).Where(hero => hero != null).ToArray(); }
            catch (InvalidOperationException) { }
            store.StartGeneralTransaction(NativeShopTabs.OpenTab(NativeShopPurchase.Products.Select(p => p.Kind), deckHeroes));
        }
        catch (Exception error) { Fail("open", error); RecoverUI(); }
        return false;
    }
    private static Action<ShopType> BoxListener(Box box, string method) =>
        (Action<ShopType>)Delegate.CreateDelegate(typeof(Action<ShopType>), box, Method(typeof(Box), method));
    private static bool HomeStoreShown(Box __instance)
    {
        if (!Isolated) return true;
        try
        {
            var previous = (Box.State)Read(__instance, "m_previousState");
            if (previous != Box.State.INVALID) __instance.ChangeState(previous);
            Write(__instance, "m_previousState", Box.State.INVALID);
            StoreManager.Get().RemoveStoreShownListener(BoxListener(__instance, "OnStoreShown"));
        }
        catch (Exception error) { Fail("home store shown", error); RecoverUI(); }
        return false;
    }
    private static bool HomeStoreHidden(Box __instance)
    {
        if (!Isolated) return true;
        try
        {
            __instance.OnShopDestroyed();
            StoreManager.Get().RemoveStoreHiddenListener(BoxListener(__instance, "OnStoreHidden"));
        }
        catch (Exception error) { Fail("home store hidden", error); RecoverUI(); }
        return false; // Native hidden callback requests Lucky Draw and Black Market state from retail.
    }

    private static bool CollectionCardBackProduct(int __0, ref int __result)
    {
        if (!Isolated) return true;
        __result = Enabled && ready ? NativeShopPurchase.CollectionCardBackProductId(__0) : 0;
        return false;
    }
    private static bool CollectionHeroSkinProduct(string __0, ref HeroSkinUtils.HeroSkinProductData __result)
    {
        if (!Isolated) return true;
        int id = Enabled && ready ? NativeShopPurchase.CollectionHeroSkinProductId(__0) : 0;
        __result = new HeroSkinUtils.HeroSkinProductData { ProductId = id,
            CurrencyType = id > 0 ? Assets.CardHero.PortraitCurrency.GOLD : Assets.CardHero.PortraitCurrency.UNKNOWN,
            IsDelayedRelease = false };
        return false;
    }
    private static object ActiveCollectionController(Func<object> get, ref bool failed)
    {
        try {
            var controller = get();
            return controller != null && (bool)Read(controller, "m_isStoreTransactionActive") ? controller : null;
        }
        catch (Exception error) { failed = true; Refuse("collection caller: " + (error.InnerException ?? error).Message); return null; }
    }
    private static object CollectionPurchaseContext(long pmt, object controller)
    {
        if (!NativeShopPurchase.TryProduct(pmt, out var product)) return null;
        string selected = product.Kind == "CardBack" && controller is CardBackInfoManager ? ((int?)Read(controller, "m_currentCardBackIdx"))?.ToString()
            : product.Kind == "HeroSkin" && controller is HeroSkinInfoManager ? (Read(controller, "m_currentEntityDef") as EntityDef)?.GetCardId() : null;
        string cosmetic = product.Kind == "CardBack" ? product.CardBack?.ToString() : product.HeroSkin;
        return NativeShopPurchase.AllowsClosedShop(product.Kind, selected, cosmetic) ? controller : null;
    }
    private static void RefuseCollectionPurchase(StoreManager store, object controller)
    {
        // Detach only this caller; native CleanupTransaction would destroy another purchase's auth windows.
        Delegate WithoutCaller(Delegate listeners)
        {
            foreach (var listener in listeners?.GetInvocationList() ?? new Delegate[0])
                if (ReferenceEquals(listener.Target, controller)) listeners = Delegate.Remove(listeners, listener);
            return listeners;
        }
        Write(store, "OnSuccessfulPurchase", WithoutCaller(Read(store, "OnSuccessfulPurchase") as Delegate));
        Write(store, "OnSuccessfulPurchaseAck", WithoutCaller(Read(store, "OnSuccessfulPurchaseAck") as Delegate));
        Write(store, "OnFailedPurchaseAck", WithoutCaller(Read(store, "OnFailedPurchaseAck") as Delegate));
        Write(controller, "m_isStoreTransactionActive", false);
        Call(controller, "OnFailedPurchaseAck", null, PaymentMethod.VIRTUAL_CURRENCY);
    }
    private static bool Buy(StoreManager __instance, BuyProductEventArgs __0)
    {
        if (!Isolated) return true;
        object collection = null;
        Action refusalPopup = null;
        try
        {
            bool shopOpen = Shop.Get() != null && Shop.Get().IsOpen();
            bool collectionLookupFailed = false;
            if (!shopOpen) {
                var cardBack = ActiveCollectionController(() => CardBackInfoManager.Get(), ref collectionLookupFailed);
                var heroSkin = ActiveCollectionController(() => HeroSkinInfoManager.Get(), ref collectionLookupFailed);
                collection = ReferenceEquals(cardBack, collectionPurchase) ? heroSkin ?? cardBack : cardBack ?? heroSkin;
            }
            if (transaction != null || (__0 != null && !requests.Add(__0)))
            { Refuse("buy busy or duplicate request"); return false; }
            if (collectionLookupFailed) throw new InvalidOperationException("Collection caller unavailable.");
            if (!Enabled || !ready || __0 == null)
            { Refuse("buy unavailable"); RecoverUI(); return false; }
            if (__0.PaymentCurrency != CurrencyType.GOLD || __0.Quantity != 1
                || !ValidProduct(products.GetProductDataModel(__0.PmtProductId))
                || (collection == null && Profile.Pending != null))
                throw new InvalidOperationException("invalid local gold purchase or profile busy");
            if (!shopOpen && CollectionPurchaseContext(__0.PmtProductId, collection) == null) throw new InvalidOperationException("local shop is not open");
            var local = NativeShopPurchase.Product(__0.PmtProductId);
            // #57: this order consumes the answered kit question (if any), refused or not; gold is checked after the kit.
            int fullPrice = Profile.PurchasePrice(local.Id);
            int due = kits.Due(__0.PmtProductId, fullPrice, Time.realtimeSinceStartup);
            string kitId = kits.Take(__0.PmtProductId, fullPrice, Time.realtimeSinceStartup, out bool answered);
            // A kit-quoted deck is bought only through its question: never at a price the page did not offer.
            if (Quote(local) != fullPrice && !answered) throw new InvalidOperationException("Deck kit question was not answered.");
            if (NativeShopPurchase.Owned(Profile, local, Profile.PurchasePrice(local.Id)))
                throw new InvalidOperationException("Shop item already owned.");
            string blocker = NativeShopPurchase.Blocker(Profile, local);
            if (blocker != null || LocalLadder.Screen == "Playing" || LocalLadder.Screen == "Starting")
            {
                string reason = blocker ?? "Match active.";
                refusalPopup = () => ShowPurchaseFailure("solostone-purchase-refused", reason);
                throw new InvalidOperationException(reason);
            }
            if (Profile.Gold < due) {
                refusalPopup = () => ShowNotEnoughGold(due);
                throw new InvalidOperationException("Not enough local gold.");
            }
            if (EvaluatePrerequisite() != null) throw new InvalidOperationException("Local shop prerequisites unavailable."); // Uncached: the per-frame memo may be stale.
            // The ledger expects the pre-kit price; the native quote may be the after-kit amount (ValidProduct above).
            purchasePrice = fullPrice;
            purchaseKit = kitId;
            auth = ShopWindowController.Get().GetWindow<PurchaseAuthWindow>()
                ?? throw new InvalidOperationException("Purchase auth unavailable.");
            purchaseFailure = null;
            sequence = NativeShopPurchase.NextTransactionId(Profile, sequence, DateTime.UtcNow.Ticks);
            transaction = new Transaction(sequence, __0.PmtProductId, PegasusShared.BattlePayProvider.BP_PROVIDER_BLIZZARD);
            collectionPurchase = collection;
            committed = successSent = resultShown = false; purchaseBegan = Time.realtimeSinceStartup;
            Write(__instance, "m_pendingProductPurchaseArgs", new BuyProductEventArgs(__0.PmtProductId, CurrencyType.GOLD, 1, true));
            Write(__instance, "m_activeTransaction", transaction); EnumValue(__instance, "Status", "WAIT_BLIZZARD_CHECKOUT");
            Call(__instance, "SetCanTapOutConfirmationUI", true); __instance.BlockStoreInterface();
            Trace("buy start transaction " + transaction.ID);
        }
        catch (Exception error) {
            Refuse("buy: " + (error.InnerException ?? error).Message);
            RecoverStep("purchase refresh", () => { RefreshWallet(); RefreshPrices(); products.UpdateProductStatus(); });
            // Native failure queues ShowAuthDialog(null) without an active transaction.
            if (transaction == null) RecoverUI();
            else purchaseFailure = (error.InnerException ?? error).Message;
        }
        finally
        {
            if (collection != null && !ReferenceEquals(collection, collectionPurchase))
            {
                RecoverStep("collection refusal", () => RefuseCollectionPurchase(__instance, collection));
                if (refusalPopup != null) RecoverStep("collection refusal popup", refusalPopup);
            }
        }
        return false;
    }
    private static void TickPurchaseWait()
    {
        if (transaction == null) return;
        if (!Enabled) { RecoverUI(); return; }
        if (Time.realtimeSinceStartup - purchaseBegan >= Timeout)
        {
            if (resultShown) Acknowledge(StoreManager.Get(), committed, transaction);
            else
            {
                Fail("purchase timeout", new InvalidOperationException(committed
                    ? "committed purchase projection timed out; restart reprojects ledger" : "purchase UI timed out; no debit"));
                RecoverUI();
            }
            return;
        }
        if (!successSent) TickPurchase();
    }
    private static void TickPurchase()
    {
        if (auth == null || !auth.IsLoaded) return;
        var store = StoreManager.Get();
        if (!auth.IsShown)
        {
            auth.InitializeOnce((success, tx) => Acknowledge(store, success, tx), () => RecoverUI(), () => {
                try { Call(store, "OnAuthExit"); }
                catch (Exception error) { Fail("auth exit", error); RecoverUI(); }
            });
            auth.Show(transaction, false);
        }
        if (purchaseFailure != null)
        {
            successSent = true;
            Call(store, "HandleCommercePurchaseFailure", purchaseFailure);
            return;
        }
        if (!committed)
        {
            try
            {
                if (!Enabled || !ready || Profile.Pending != null
                    || NativeShopPurchase.Blocker(Profile, NativeShopPurchase.Product(transaction.PMTProductID.GetValueOrDefault())) != null
                    || LocalLadder.Screen == "Playing" || LocalLadder.Screen == "Starting")
                    throw new InvalidOperationException("local purchase context changed while loading");
                purchaseReceipt = purchases.Commit(Profile, transaction.ID, transaction.PMTProductID.GetValueOrDefault(), "XSH", 1, purchasePrice, purchaseKit);
                committed = true;
                // A bought theme is equipped by the ledger; its notice follows the purchase window (Tick).
                if (purchaseReceipt.Granted.HubTheme != null) themeNoticeAfterPurchase = purchaseReceipt.Granted.HubTheme;
                Trace("commit ok transaction " + transaction.ID + " gold " + Profile.Gold + " packs " + Profile.UnopenedPacks
                    + (purchaseReceipt.KitId == null ? "" : " kit " + purchaseReceipt.KitId + " paid " + purchaseReceipt.GoldPaid));
            }
            catch (Exception error)
            {
                RecoverStep("purchase refresh", () => { RefreshWallet(); RefreshPrices(); products.UpdateProductStatus(); });
                Call(store, "HandleCommercePurchaseFailure", (error.InnerException ?? error).Message);
                successSent = true; Refuse("commit failed: " + (error.InnerException ?? error).Message); return;
            }
        }
        try
        {
            NativeUIProbe.RefreshShopPurchase(purchaseReceipt);
            RefreshWallet(); RefreshPrices(); products.UpdateProductStatus();
            var success = Method(typeof(StoreManager), "HandlePurchaseSuccess");
            var constructor = (ConstructorInfo)bindings["Blizzard.Commerce.TransactionData..ctor"].Value;
            object productId = Method("Blizzard.Commerce.ProductId", "CreateFrom").Invoke(null, new object[] { transaction.PMTProductID.GetValueOrDefault() });
            object data = constructor.Invoke(new[] { productId, "XSH", (object)1u, true });
            successSent = true; // Native completion may fire callbacks synchronously; never schedule it twice.
            success.Invoke(store, new[] { null, (object)transaction, "", data });
            RefreshWallet();
            Trace("projected purchase " + purchaseReceipt.Sequence);
        }
        catch (Exception error)
        {
            Refuse("committed; projection/completion failed: " + (error.InnerException ?? error).Message);
            if (successSent) { Fail("committed completion", error); RecoverUI(); } // Before dispatch, retry projection without another debit.
        }
    }
    private static void SuccessShown(Transaction __0, bool __result)
    {
        if (Isolated && __result && transaction != null && __0?.ID == transaction.ID)
        { resultShown = true; purchaseWindowAt = Time.realtimeSinceStartup; Trace("success shown transaction " + transaction.ID); }
    }
    private static void FailureShown(Transaction __0, bool __result)
    { if (Isolated && __result && transaction != null && __0?.ID == transaction.ID) resultShown = true; }
    private static bool SuccessJob(Transaction __0, ref IEnumerator<IAsyncJobResult> __result)
    {
        if (!Isolated) return true;
        __result = ShowSuccess(__0); return false;
    }
    private static IEnumerator<IAsyncJobResult> ShowSuccess(Transaction purchase)
    {
        while (transaction != null && purchase?.ID == transaction.ID && committed && !resultShown)
        {
            try
            {
                if (Time.realtimeSinceStartup - purchaseBegan > Timeout) throw new InvalidOperationException("success window timed out");
                if (auth != null && auth.IsLoaded && auth.IsShown)
                {
                    RefreshWallet();
                    Call(StoreManager.Get(), "SetCanTapOutConfirmationUI", false);
                    auth.CompletePurchaseSuccess(purchase);
                }
            }
            catch (Exception error) { Fail("success UI", error); RecoverUI(); }
            yield return null;
        }
    }
    private static bool Acknowledge(StoreManager __instance, bool __0, Transaction __1)
    {
        if (!Isolated) return true;
        try
        {
            if (transaction == null || __1 == null || __1.ID != transaction.ID || __1.PMTProductID != transaction.PMTProductID)
            { Refuse("foreign or duplicate acknowledgement"); return false; }
            purchaseWindowAt = Time.realtimeSinceStartup; // Pack-tile cooldown also restarts when the window closes.
            ((HashSet<long>)Read(__instance, "m_transactionIDsConclusivelyHandled")).Add(__1.ID);
            object bundle = FindInfo(__1.PMTProductID.GetValueOrDefault());
            var callback = Read(__instance, __0 ? "OnSuccessfulPurchaseAck" : "OnFailedPurchaseAck") as Delegate;
            Write(__instance, "m_activeTransaction", null); Write(__instance, "m_pendingProductPurchaseArgs", null);
            EnumValue(__instance, "Status", "READY");
            var completedAuth = auth;
            transaction = null; auth = null; collectionPurchase = null;
            try { callback?.DynamicInvoke(bundle, PaymentMethod.VIRTUAL_CURRENCY); }
            finally
            {
                RecoverStep("auth acknowledged", () => completedAuth?.Hide());
                try { Call(__instance, "SetCanTapOutConfirmationUI", true); }
                finally { __instance.UnblockStoreInterface(); }
            }
            Trace("ack transaction " + __1.ID + " success " + __0);
        }
        catch (Exception error) { Fail("ack", error); RecoverUI(); }
        return false;
    }
    private static void RecoverUI()
    {
        // Clear ownership first: callbacks from Hide/cancel must not finish this purchase twice.
        var pendingConfirmation = confirmation; confirmation = null;
        var pendingAuth = auth; auth = null;
        var pendingTransaction = transaction; transaction = null; collectionPurchase = null;
        var pendingPopup = localConfirmation; localConfirmation = null;
        var pendingKit = kitQuestion; kitQuestion = null; kitHooked = null; kitProduct = null; kitRelease.Reset(); kits.Cancel(); purchaseKit = null;
        bool wasOpening = opening; opening = false;
        RecoverStep("kit question dismiss", () => {
            if (pendingKit != null) DialogManager.Get()?.DismissAlertOrRemoveFromQueue(pendingKit.m_id);
        });
        RecoverStep("confirmation cancel", () => pendingConfirmation?.Complete(false, null));
        RecoverStep("confirmation dismiss", () => {
            if (pendingPopup != null) DialogManager.Get()?.DismissAlertOrRemoveFromQueue(pendingPopup.m_id);
        });
        RecoverStep("auth hide", () => pendingAuth?.Hide());
        var store = StoreManager.Get();
        if (store != null)
        {
            RecoverStep("pending purchase", () => Write(store, "m_pendingProductPurchaseArgs", null));
            RecoverStep("active transaction", () => Write(store, "m_activeTransaction", null));
            RecoverStep("purchase status", () => EnumValue(store, "Status", "READY"));
            RecoverStep("tap out", () => Call(store, "SetCanTapOutConfirmationUI", true));
            RecoverStep("unblock", () => store.UnblockStoreInterface());
            if (pendingTransaction != null)
                RecoverStep("purchase failure", () => {
                    ((HashSet<long>)Read(store, "m_transactionIDsConclusivelyHandled")).Add(pendingTransaction.ID);
                    var callback = Read(store, "OnFailedPurchaseAck") as Delegate;
                    object bundle = FindInfo(pendingTransaction.PMTProductID.GetValueOrDefault());
                    callback?.DynamicInvoke(bundle, PaymentMethod.VIRTUAL_CURRENCY);
                });
            if (wasOpening || !Enabled)
            {
                RecoverStep("waiting store", () => Write(store, "m_waitingToShowStore", false));
                RecoverStep("hide store", () => store.HideStore(ShopType.GENERAL_STORE));
            }
        }
        if (wasOpening && openingBox != null) RecoverStep("home state", () => Call(openingBox, "ChangeState", previousBoxState));
    }
    private static void RecoverStep(string step, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) { Refuse(step + ": " + (error.InnerException ?? error).Message); }
    }

    private static bool Confirm(string __0, CurrencyType __1, AlertPopup.ResponseCallback __2)
    {
        if (!Isolated) return true;
        if (confirmation != null) RecoverUI();
        var pending = new NativeShopConfirmation(Time.realtimeSinceStartup + Timeout, (accepted, data) => {
            confirmation = null;
            if (accepted) localConfirmation = null;
            __2?.Invoke(accepted ? AlertPopup.Response.CONFIRM : AlertPopup.Response.CANCEL, data);
        }, RecoverUI);
        confirmation = pending;
        try
        {
            ProductPage page = Shop.Get()?.ProductPageController?.CurrentProductPage;
            if (!Enabled || !ready || __1 != CurrencyType.GOLD || page == null || !page.IsOpen
                || !ValidProduct(page.GetSelectedVariant()))
            { Refuse("foreign purchase confirmation"); pending.Complete(false, null); return false; }
            localConfirmation = new AlertPopup.PopupInfo {
                m_id = "solostone-confirm-" + checked(++sequence),
                m_headerText = __0, m_text = "Buy " + __0 + " for " + page.GetSelectedVariant().Prices[0].Amount + " gold?", m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_confirmText = GameStrings.Get("GLOBAL_CONFIRM"), m_cancelText = GameStrings.Get("GLOBAL_CANCEL"),
                m_responseCallback = (response, data) => {
                    try
                    {
                        pending.Tick(Time.realtimeSinceStartup, Enabled && ready && Shop.Get() != null && Shop.Get().IsOpen());
                        pending.Complete(response == AlertPopup.Response.CONFIRM, data);
                    }
                    catch (Exception error) { Fail("confirmation response", error); RecoverUI(); }
                }
            };
            if (!DialogManager.Get().ShowUniquePopup(localConfirmation)) pending.Complete(false, null);
        }
        catch (Exception error)
        {
            Fail("confirmation", error); RecoverUI();
            RecoverStep("confirmation cancel", () => pending.Complete(false, null));
        }
        return false;
    }
    private static bool AllowControl(UiElement element)
    {
        if (!Enabled || !ready || !(element.Handle is PegUIElement control)) return false;
        if (Box.Get() != null && control == Box.Get().m_StoreButton)
        { element.Id = "home.store"; return true; }
        if (Shop.Get() == null || !Shop.Get().IsOpen()) return false;
        if (!IsLocalShopElement(control, out string id)) return false;
        element.Id = id;
        return true;
    }

    internal static bool IsLocalShopElement(PegUIElement control, out string id)
    {
        try { return ResolveLocalShopElement(control, out id); }
        catch (Exception error)
        {
            id = null;
            Refuse("control identity: " + (error.InnerException ?? error).Message);
            return false;
        }
    }

    private static void BrowserScrollHeight(UIBScrollable __instance, ref float __result)
    {
        if (!Active || __instance == null || __instance.ScrollObject == null) return;
        try
        {
            var shop = Shop.Get();
            if (shop == null || !shop.IsOpen() || !ReferenceEquals(__instance, Read(shop, "m_browserScroller"))) return;
            // FetchDivider stages a divider at z=1000 before LoadSectionsCoroutine positions it: settled layout only.
            if (!NativeShopReveal.LaidOut(shop.IsBrowserReady, shop.IsBrowserDirty)) return;
            var browser = Read(shop, "m_browser") as ShopBrowser;
            if (browser == null) return;
            var dividers = Read(browser, "m_activeDividers") as List<ShopDivider>;
            if (dividers == null || dividers.Count == 0) return;
            int axis = (int)__instance.m_ScrollPlane;
            var items = Read(__instance, "m_scrollableItems") as List<UIBScrollableItem>;
            if (items == null) return;
            float firstSectionTop = float.MinValue;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || !item.IsActive()) continue;
                item.GetWorldBounds(out var min, out var max);
                firstSectionTop = Math.Max(firstSectionTop, Math.Max(min[axis], max[axis]));
            }
            if (firstSectionTop == float.MinValue) return;
            // Native height spans registered sections only, omitting a leading divider and its margin.
            float gap = 0f;
            for (int i = 0; i < dividers.Count; i++)
            {
                var divider = dividers[i];
                // Live 2026-10-30: drawn deck headers report IsElementEnabled false; only the GameObject state is reliable.
                if (divider == null || !divider.isActiveAndEnabled) continue;
                var item = Read(divider, "m_scrollableItem") as UIBScrollableItem;
                if (item == null) continue;
                item.GetWorldBounds(out var min, out var max);
                gap = Math.Max(gap, NativeShopLayout.LeadingDividerGap(firstSectionTop, Math.Min(min[axis], max[axis]), Math.Max(min[axis], max[axis])));
            }
            if (gap > 0f) __result += gap;
        }
        catch (Exception error) { Refuse("shop scroll height: " + (error.InnerException ?? error).Message); }
    }

#if !SOLOSTONE_RELEASE
    // Test-control only: scroll the local shop's own browser so a verified local product tile is in view. The same
    // native call as Shop.MoveToProductCoroutine; it changes the scroll position and nothing else. Anything that is
    // not a local product tile inside this shop's scroll content is refused, and nothing scrolls while a purchase,
    // details page or dialog covers the browser. Started only when the native call actually started a scroll; Pending
    // while the browser still loads or positions sections (the native MoveToProductCoroutine waits for the same), until
    // the caller's bounded layout wait is over (NativeShopReveal.LayoutWaitOver).
    internal static NativeShopReveal.Outcome RevealLocalProduct(PegUIElement control, string id, bool layoutWaitOver)
    {
        try
        {
            var shop = Shop.Get();
            if (!Active || control == null || shop == null || !shop.IsOpen() || id == null
                || !id.StartsWith("shop.product.", StringComparison.Ordinal)
                || !IsLocalShopElement(control, out string current) || current != id || RevealOverlay(shop) != null) return NativeShopReveal.Outcome.Refused;
            var slot = control.GetComponentInParent<ShopSlot>();
            var scroller = Read(shop, "m_browserScroller") as UIBScrollable;
            if (slot == null || scroller == null || scroller.ScrollObject == null || slot.GetComponentInParent<Shop>() != shop
                || !slot.transform.IsChildOf(scroller.ScrollObject.transform)) return NativeShopReveal.Outcome.Refused;
            if (!layoutWaitOver && !NativeShopReveal.LaidOut(shop.IsBrowserReady, shop.IsBrowserDirty)) return NativeShopReveal.Outcome.Pending;
            // EaseType lives in Blizzard.T5.iTween, which the plugin does not reference: the native default is passed through.
            // Pinned ScrollObjectIntoView returns false when the tile is already inside the scroll bounds (nothing moved).
            return (bool)Call(scroller, "CenterObjectInView", slot.gameObject, 0f, null, Read(scroller, "m_ScrollEaseType"), scroller.m_ScrollTweenTime, false)
                ? NativeShopReveal.Outcome.Started : NativeShopReveal.Outcome.InView;
        }
        catch (Exception error) { Refuse("shop reveal: " + (error.InnerException ?? error).Message); return NativeShopReveal.Outcome.Refused; }
    }
#endif
#if !SOLOSTONE_RELEASE
    // Null when the browser is uncovered; otherwise what covers it.
    private static string RevealOverlay(Shop shop)
    {
        if (transaction != null || confirmation != null || localConfirmation != null || kitQuestion != null) return "purchase pending";
        var pages = shop.ProductPageController;
        if (pages == null) return "product pages unavailable";
        if (pages.IsOpen || pages.IsOpening || pages.CurrentProductPage != null && pages.CurrentProductPage.IsOpen) return "product page open";
        if (StoreManager.Get()?.IsPurchaseAuthViewShown() == true) return "purchase window shown";
        var dialogs = DialogManager.Get();
        if (dialogs != null && Read(dialogs, "m_currentDialog") != null) return "dialog shown";
        return null;
    }
#endif
#if !SOLOSTONE_RELEASE
    // Test-control press detail while a reveal waits: which input of the pinned readiness holds it (ShopBrowser.IsReady:
    // its loading coroutine and every enabled section's; IsDirty: m_dataDirty, cleared only by an unsuppressed load).
    // A section listed as inactive kept a coroutine handle Unity already stopped. Read only.
    internal static string RevealLayoutState()
    {
        try
        {
            var shop = Shop.Get();
            if (shop == null) return "no shop";
            string state = "ready=" + shop.IsBrowserReady + " dirty=" + shop.IsBrowserDirty;
            var browser = Read(shop, "m_browser") as ShopBrowser;
            if (browser == null) return state + " browser=none";
            var loading = ((IList)Read(browser, "m_sections")).Cast<object>().Select(item => item as ShopSection)
                .Where(section => section != null && section.IsElementEnabled && !section.IsReady())
                .Select(section => section.name + (section.gameObject.activeInHierarchy ? "" : "(inactive)"));
            return state + " loading=" + (Read(browser, "m_loadingCoroutine") != null) + " suppressed=" + Read(browser, "m_isLoadingSuppressed")
                + " sectionsLoading=[" + string.Join(",", loading) + "]";
        }
        catch (Exception error) { return "unavailable: " + (error.InnerException ?? error).Message; }
    }
#endif

    // Single-price buy option of the shared ProductBuyButton widget. Live-confirmed under ShopDetailsBooster
    // (".../ShopDetailsBuyButtons/ShopDetailsBuyButtons/1 Option/ProductBuyButton/ProductBuyButton/Container"); deck and
    // cosmetic detail prefabs are asset data, so they rely on the same widget suffix plus page ownership below.
    private const string BuyButtonSuffix = "1 Option/ProductBuyButton/ProductBuyButton/Container";
    // #57 live 2026-10-28: a price on sale (kit decks) uses the same single-price widget under "Discount Option".
    private const string DiscountBuyButtonSuffix = "Discount Option/ProductBuyButton/ProductBuyButton/Container";
    internal static bool IsSingleBuyPath(Func<string, bool> matches) => matches(BuyButtonSuffix) || matches(DiscountBuyButtonSuffix);
    private static readonly NativeShopControlCache controlCache = new NativeShopControlCache();
    private static bool ControlContext(Shop shop, out ProductPage currentPage, out bool localPage)
    {
        currentPage = shop.ProductPageController?.CurrentProductPage;
        var variant = currentPage?.GetSelectedVariant();
        bool catalogue = controlCache.Resolve(Time.frameCount, shop, currentPage, variant, NativeShopPurchase.CatalogueVersion,
            LocalCatalogue, () => LocalModel(variant),
            error => Refuse("control identity: " + (error.InnerException ?? error).Message), out bool localVariant);
        localPage = catalogue && currentPage != null && currentPage.IsOpen && localVariant;
        return catalogue;
    }

    // Identity comes from the local catalogue and native owners, never a UI label or requested id.
    private static bool ResolveLocalShopElement(PegUIElement control, out string id)
    {
        id = null;
        var shop = Shop.Get();
        if (!Active || control == null || shop == null || !shop.IsOpen() || products == null
            || !ControlContext(shop, out var currentPage, out bool localPage)) return false;
        var popup = control.GetComponentInParent<AlertPopup>();
        if (popup != null && localConfirmation != null && ReferenceEquals(popup.GetInfo(), localConfirmation))
        {
            if (control == popup.m_confirmButton) id = "dialog.confirm";
            else if (control == popup.m_cancelButton) id = "dialog.cancel";
            return id != null;
        }
        if (popup != null && kitQuestion != null && ReferenceEquals(popup.GetInfo(), kitQuestion))
        {
            if (control == popup.m_confirmButton) id = NativeShopKitText.UseId;
            else if (control == popup.m_cancelButton) id = NativeShopKitText.FullId;
            return id != null;
        }
        var purchaseAuth = control.GetComponentInParent<StorePurchaseAuth>();
        if (purchaseAuth != null && transaction != null && control == purchaseAuth.m_okButton
            && (Read(purchaseAuth, "m_transaction") as Transaction)?.ID == transaction.ID)
        { id = "shop.ack"; return true; }
        // Widget instances and popup reparenting can put the page below a different transform than its catcher.
        var container = control.GetComponentInParent<ProductPageContainer>();
        if (localPage && container != null && container.GetCurrentProductPage() == currentPage
            && MatchesHierarchy(control, "DetailsPageContainer/Pages/OffClickCatcher"))
        { id = "shop.page.close"; return true; }
        var quantity = control.GetComponentInParent<StoreQuantityPrompt>();
        if (quantity != null && localPage && ReferenceEquals(Read(shop.ProductPageController, "m_quantityPrompt"), quantity)
            && control == quantity.m_clickCatcherClickable?.GetComponent<PegUIElement>())
        { id = "shop.quantity.cancel"; return true; }
        var page = control.GetComponentInParent<ProductPage>();
        bool inShop = control.GetComponentInParent<Shop>() == shop;
        if (!inShop && !(localPage && page == currentPage)) return false;
        var slot = control.GetComponentInParent<ShopSlot>();
        if (slot != null && (Read(slot, "m_clickable") as Component)?.GetComponent<PegUIElement>() == control
            && LocalModel(slot.BrowserButtonDataModel?.DisplayProduct))
        { id = "shop.product." + slot.BrowserButtonDataModel.DisplayProduct.PmtId; return true; }
        if (control is UIBButton button && localPage && page == currentPage)
        {
            // Any ProductPage kind (booster, sellable deck, card back, hero skin): owner is the open current local page.
            // Live 2026-10-28: the page holds both single-price widgets ("1 Option", "Discount Option"); only the shown
            // one is shop.buy, so a hidden twin never takes the id (it was listed off-screen as shop.buy, the live one #2).
            if ((button.BubbleUpEvent == ProductPage.SHOP_BUY_WITH_FIRST_CURRENCY
                || (!(button.BubbleUpEvent ?? "").StartsWith("SHOP_", StringComparison.Ordinal) && IsSingleBuyPath(path => MatchesHierarchy(button, path))))
                && button.gameObject.activeInHierarchy) id = "shop.buy";
            else
                for (int index = 0; index <= 6; index++)
                    if (button.BubbleUpEvent == "SHOP_SKU_CLICKED_" + index) id = "shop.quantity." + index;
            if (id != null) return true;
        }
        if (inShop && control is UIBButton back && IsShopBack(back, shop))
        { id = "shop.back"; return true; }
        if (inShop && control is ProxyButton && control.gameObject.name == "BackButtonClicker")
        {
            var target = (Read(control, "m_target") as GameObject)?.GetComponentInChildren<PegUIElement>() as UIBButton;
            if (target != null && IsShopBack(target, shop))
            { id = "shop.back"; return true; }
        }
        var tab = control.GetComponentInParent<ShopTabBase>();
        if (inShop && tab != null && (NativeShopTabs.Ids.Contains(tab.CurrentData?.Id) || NativeShopTabs.SubTabs.Contains(tab.CurrentData?.Id)))
        {
            var tabControl = tab is ShopTab ? (Read(tab, "m_clickable") as Component)?.GetComponent<PegUIElement>()
                : tab is ShopSubTab ? Read(tab, "m_button") as PegUIElement : null;
            if (control == tabControl) { id = "shop.tab." + tab.CurrentData.Id; return true; }
        }
        return false;
    }

    private static bool IsShopBack(UIBButton button, Shop shop)
    {
        return button.GetComponentInParent<Shop>() == shop && button.GetComponentInParent<ProductPage>() == null
            && (button.BubbleUpEvent == "SHOP_GO_BACK" || (!(button.BubbleUpEvent ?? "").StartsWith("SHOP_", StringComparison.Ordinal)
                && MatchesHierarchy(button, "Shop_Box_PC/BackButton/BackButton")));
    }

    private static bool MatchesHierarchy(Component control, string path)
    {
        var transform = control.transform;
        var parts = path.Split('/');
        for (int index = parts.Length - 1; index >= 0; index--)
        {
            if (transform == null || transform.name != parts[index]) return false;
            transform = transform.parent;
        }
        return true;
    }
}
#endif
