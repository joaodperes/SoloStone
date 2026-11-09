using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using PegasusShared;
using UnityEngine;

internal static class NativeCrafting
{
    private sealed class BatchCompletion
    {
        internal ICraftingLedger Ledger;
        internal string CardId, Error;
        internal CraftDirection Direction;
        internal int Count;
        internal TAG_PREMIUM Premium;
        internal CraftPlan Plan;
    }

    private static ManualLogSource log;
    private static Action<object> publish;
    private static BatchCompletion batch;
    private static bool closing;
    private static bool massCompleting, massListenersFailed;
    private static bool massRefreshErrorShown;
    private static Network.MassDisenchantResponse massResponse;
    private static bool Enabled => NativeUIProbe.Active && CraftingLedgerRegistry.Current != null;
    internal static bool PresentationBusy
    {
        get
        {
            try { return closing || batch != null || massCompleting
                || CraftingManager.Get()?.GetPendingClientTransaction()?.HasPendingTransactions() == true; }
            catch { return true; }
        }
    }

    internal static void Initialize(Harmony harmony, ManualLogSource logger, Action<object> publisher)
    {
        log = logger;
        publish = publisher;
        harmony.Patch(Required(typeof(LocalProfile), "Settle"),
            postfix: new HarmonyMethod(typeof(NativeCrafting), nameof(SettlementDust)));
        Prefix(harmony, typeof(Network), "CraftingTransaction", nameof(CommitBatch));
        Prefix(harmony, typeof(Network), "MassDisenchant", nameof(CommitMass));
        Prefix(harmony, typeof(Network), "GetMassDisenchantResponse", nameof(MassResponse));
        Prefix(harmony, typeof(CollectionManager), "OnMassDisenchantResponse", nameof(MassCompleted));
        Prefix(harmony, typeof(MassDisenchant), "UpdateContents", nameof(PreviewMass));
        Prefix(harmony, typeof(CraftingManager), "GetCardValue", nameof(Price),
            typeof(string), typeof(TAG_PREMIUM), typeof(CardValueDbfRecord).MakeByRefType());
        Prefix(harmony, typeof(CraftingManager), "CanCraftCardRightNow", nameof(CanCraft));
        Prefix(harmony, typeof(CraftingUI), "DoCreate", nameof(BeforeCreate), typeof(bool));
        Prefix(harmony, typeof(CraftingUI), "DoDisenchant", nameof(BeforeDisenchant));
        Prefix(harmony, typeof(UpgradeToGoldenPopup), "HandleEvent", nameof(BeforeUpgradeEvent), typeof(string));
        harmony.Patch(Required(typeof(CraftingUI), "UpdateCraftingButtonsAndSoulboundText"),
            postfix: new HarmonyMethod(typeof(NativeCrafting), nameof(ProtectPremiumButton)));
        Prefix(harmony, typeof(FixedRewardsMgr), "CanCraftCard", nameof(Permission));
        Prefix(harmony, typeof(CraftingUI), "IsCraftingEventForCardActive", nameof(CraftingEvent));
        Prefix(harmony, typeof(CollectionManager), "RequestDeckContentsForDecksWithoutContentsLoaded", nameof(DeckContentsReady));
        Prefix(harmony, typeof(CollectionDeck), "SendChanges", nameof(PreserveRecipe));
        // These transports otherwise run as side effects of options, warnings and reconciliation.
        foreach (string name in new[] { "ValidateAchieve", "SetClientOptions", "SendDeckData", "RequestDeckContents" })
            Prefix(harmony, typeof(Network), name, nameof(LocalOnly));
        harmony.Patch(Required(typeof(CraftingManager), "TellServerAboutWhatUserDid"),
            prefix: new HarmonyMethod(typeof(NativeCrafting), nameof(BeforeClose)),
            postfix: new HarmonyMethod(typeof(NativeCrafting), nameof(AfterClose)),
            finalizer: new HarmonyMethod(typeof(NativeCrafting), nameof(CloseFailed)));
        foreach (var target in new[] {
            Required(typeof(CreateButton), "OnRelease"), Required(typeof(DisenchantButton), "OnRelease"),
            Required(typeof(CraftingUI), "UpdateCraftingButtonsAndSoulboundText") })
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(NativeCrafting), nameof(CraftingLogin)));
        // FX uses temporary int/float currency text. Restore the long authoritative balance at unblock.
        harmony.Patch(Required(typeof(MassDisenchant), "BlockUI"),
            postfix: new HarmonyMethod(typeof(NativeCrafting), nameof(MassUnblocked)));
        // Cosmetic only (card-set label of cards without a set), so it is last and fails soft: a patch-time throw here
        // must never skip the required hooks above or the rest of NativeUIProbe.Initialize.
        PatchMissingCardSet(harmony);
    }

    private static void PatchMissingCardSet(Harmony harmony)
    {
        MethodInfo target = null;
        try
        {
            target = Required(typeof(CraftingUI), "UpdateCraftingButtonsAndSoulboundText");
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(NativeCrafting), nameof(MissingCardSet)));
        }
        catch (Exception error)
        {
            log.LogWarning("CRAFTING_MISSING_CARD_SET_PATCH_SKIPPED (cosmetic card-set label stays native): " + error);
            try { if (target != null) harmony.Unpatch(target, AccessTools.Method(typeof(NativeCrafting), nameof(MissingCardSet))); }
            catch (Exception cleanup) { log.LogWarning("CRAFTING_MISSING_CARD_SET_UNPATCH_FAILED: " + cleanup.Message); }
        }
    }

    private static MethodInfo Required(Type type, string name, Type[] args = null) =>
        (args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args))
        ?? throw new MissingMethodException(type.FullName, name);

    private static void Prefix(Harmony harmony, Type type, string name, string patch, params Type[] args) =>
        harmony.Patch(Required(type, name, args.Length == 0 ? null : args),
            prefix: new HarmonyMethod(typeof(NativeCrafting), patch) { priority = Priority.First });

    private static bool Eligible(string cardId, TAG_PREMIUM premium) => Enabled
        && !CraftingLedgerRegistry.Current.PackRevealPending
        && LocalCrafting.TryGetPrice(cardId, (int)premium, out _, out _)
        && (premium == TAG_PREMIUM.NORMAL || Disenchantable(cardId, premium));
    // Premium copies earned through progression are never sold: only the count beyond them is disenchantable.
    private static bool Disenchantable(string cardId, TAG_PREMIUM premium) =>
        (NativeUIProbe.RewardProfile?.DisenchantablePremiumCount(cardId, (int)premium) ?? 0) > 0;

    private static bool PresentationEligible(string cardId, TAG_PREMIUM premium) => Eligible(cardId, premium)
        || (Enabled && !CraftingLedgerRegistry.Current.PackRevealPending && premium == TAG_PREMIUM.GOLDEN
            && (LocalCrafting.TryGetUpgradePrice(cardId, out _)
                || (CraftingManager.Get()?.GetPendingClientTransaction()?.CardID == cardId
                    && CraftingManager.Get().GetPendingClientTransaction().GoldenUpgradeFromNormalCount > 0)));

    private static bool Price(string __0, TAG_PREMIUM __1, ref CardValueDbfRecord __2, ref NetCache.CardValue __result)
    {
        if (!Enabled) return true;
        __2 = null;
        bool priced = LocalCrafting.TryGetPrice(__0, (int)__1, out int buy, out int sell);
        // Native multiplies per-quality counts by every quality's price, so premium versions always get a value object.
        // Grant-only cards and copies earned through progression have sell value 0 here and Eligible refuses
        // the sale, so the UI shows it as not disenchantable and a queued sale is refused instead of dropped silently.
        if (__1 != TAG_PREMIUM.NORMAL && !Disenchantable(__0, __1)) sell = 0;
        __result = priced || __1 != TAG_PREMIUM.NORMAL
            ? new NetCache.CardValue { BaseBuyValue = buy, BaseSellValue = sell,
                BaseUpgradeValue = __1 == TAG_PREMIUM.NORMAL && LocalCrafting.TryGetUpgradePrice(__0, out int upgrade) ? upgrade : 0,
                OverrideEvent = EventTimingType.SPECIAL_EVENT_NEVER }
            : null;
        return false;
    }

    private static bool CanCraft(EntityDef __0, TAG_PREMIUM __1, ref CraftingManager.CanCraftCardResult __result)
    {
        if (!Enabled) return true;
        if (__1 == TAG_PREMIUM.NORMAL && __0 != null && Eligible(__0.GetCardId(), __1)) return true;
        __result = CraftingManager.CanCraftCardResult.NotCraftable;
        return false;
    }

    // Refuse mixed actions before native mutates dust, counts or animations; opposite-button Undo stays native.
    private static bool AllowAction(CraftDirection direction, TAG_PREMIUM premium)
    {
        var pending = CraftingManager.Get()?.GetPendingClientTransaction();
        if (pending?.HasPendingTransactions() == true
            && (!TryReadBatch(pending, out var previous, out _, out var quality) || previous != direction || quality != premium))
        {
            ShowError("Close the card to save this change, then reopen it for a different crafting action.");
            return false;
        }
        if (direction != CraftDirection.Craft || premium == TAG_PREMIUM.NORMAL) return true;
        ShowError("Golden cards can only be made by upgrading Normal copies.");
        return false;
    }

    private static bool BeforeCreate(bool __0)
    {
        if (!Enabled) return true;
        var manager = CraftingManager.Get();
        if (manager?.GetPendingClientTransaction()?.GetLastTransactionWasDisenchant() == true) return true;
        if (manager == null || !manager.GetShownCardInfo(out EntityDef card, out TAG_PREMIUM premium)) return false;
        return AllowAction(__0 ? CraftDirection.Upgrade : CraftDirection.Craft, __0 ? TAG_PREMIUM.GOLDEN : premium)
            && (!__0 || AllowUpgrade(card.GetCardId()));
    }

    private static bool BeforeUpgradeEvent(string __0)
    {
        if (!Enabled || (__0 != "CODE_CREATE" && __0 != "CODE_UPGRADE")) return true;
        var manager = CraftingManager.Get();
        if (manager == null || !manager.GetShownCardInfo(out EntityDef card, out TAG_PREMIUM premium)) return false;
        return AllowAction(__0 == "CODE_UPGRADE" ? CraftDirection.Upgrade : CraftDirection.Craft,
            __0 == "CODE_UPGRADE" ? TAG_PREMIUM.GOLDEN : premium)
            && (__0 != "CODE_UPGRADE" || AllowUpgrade(card.GetCardId()));
    }

#if !SOLOSTONE_RELEASE
    // Live evidence for the retail Upgrade entry: native's own verdict for the shown card plus each input it reads
    // (feature flag, Golden permission, Normal upgrade tariff, pending pack reveal, dust). Read-only.
    internal static string UpgradeEntryStatus()
    {
        try
        {
            var manager = CraftingManager.Get();
            if (manager == null || !manager.GetShownCardInfo(out EntityDef card, out TAG_PREMIUM premium) || card == null)
                return "craftUpgrade=none";
            string id = card.GetCardId();
            var golden = CollectionManager.Get()?.GetCard(id, TAG_PREMIUM.GOLDEN);
            return "craftUpgrade=" + id + "/" + premium + ",flag=" + manager.HasUpgradeToGoldenEnabled()
                + ",canUpgrade=" + manager.CanUpgradeCardToGolden(id, premium, card) + ",canCraft=" + manager.CanCraftCardRightNow(card, premium)
                + ",goldenCraftable=" + (golden?.IsCraftable.ToString() ?? "missing")
                + ",tariff=" + (CraftingManager.GetCardValue(id, TAG_PREMIUM.NORMAL)?.BaseUpgradeValue.ToString() ?? "none")
                + ",packPending=" + CraftingLedgerRegistry.Current?.PackRevealPending + ",dust=" + NetCache.Get()?.GetArcaneDustBalance()
                + ",button=" + CraftingManager.Get()?.m_craftingUI?.m_buttonCreate?.GetCraftingState();
        }
        catch (Exception error) { return "craftUpgrade=error:" + error.GetType().Name; }
    }
#endif

    private static bool AllowUpgrade(string cardId)
    {
        var ledger = CraftingLedgerRegistry.Current;
        var profile = NativeUIProbe.RewardProfile;
        var counts = ledger.OwnedCounts();
        int normal = counts.TryGetValue(cardId, out int owned) ? owned : 0;
        int golden = profile.OwnedCount(cardId, 1);
        var pending = CraftingManager.Get()?.GetPendingClientTransaction();
        int count = checked((pending?.CardID == cardId ? pending.GoldenUpgradeFromNormalCount : 0) + 1);
        var plan = LocalCrafting.PlanUpgrade(new CraftLedgerView {
            Dust = profile.InfiniteDust ? ProfileCatalog.UnlimitedDust : ledger.DustBalance,
            Counts = counts, Acquired = profile.Acquired, PackRevealPending = ledger.PackRevealPending
        }, cardId, count, golden, profile.OwnedCards.SingleOrDefault(row => row.Id == cardId && row.Premium == 1)?.Acquired ?? 0,
            normal, golden);
        if (!plan.Ok) ShowError(plan.Error);
        return plan.Ok;
    }

    private static int PendingDisenchants(CraftingPendingTransaction pending, string id, TAG_PREMIUM premium)
    {
        if (pending == null || pending.CardID != id) return 0;
        return premium == TAG_PREMIUM.GOLDEN ? pending.GoldenDisenchantCount
            : premium == TAG_PREMIUM.DIAMOND ? pending.DiamondDisenchantCount : pending.SignatureDisenchantCount;
    }

    private static bool PremiumSaleAvailable(string id, TAG_PREMIUM premium) =>
        (NativeUIProbe.RewardProfile?.DisenchantablePremiumCount(id, (int)premium) ?? 0)
            > PendingDisenchants(CraftingManager.Get()?.GetPendingClientTransaction(), id, premium);

    private static bool BeforeDisenchant()
    {
        if (!Enabled) return true;
        var manager = CraftingManager.Get();
        if (manager?.GetPendingClientTransaction()?.GetLastTransactionWasCrafting() == true) return true;
        if (manager == null || !manager.GetShownCardInfo(out EntityDef card, out TAG_PREMIUM premium)) return false;
        if (!AllowAction(CraftDirection.Disenchant, premium)) return false;
        if (premium == TAG_PREMIUM.NORMAL || PremiumSaleAvailable(card.GetCardId(), premium)) return true;
        ShowError("Premium cards earned through progression cannot be disenchanted.");
        return false;
    }

    private static void ProtectPremiumButton(CraftingUI __instance)
    {
        if (!Enabled) return;
        var manager = CraftingManager.Get();
        if (manager == null || manager.GetPendingClientTransaction()?.GetLastTransactionWasCrafting() == true
            || !manager.GetShownCardInfo(out EntityDef card, out TAG_PREMIUM premium) || premium == TAG_PREMIUM.NORMAL
            || PremiumSaleAvailable(card.GetCardId(), premium)) return;
        __instance.m_buttonDisenchant.DisableButton();
        Required(typeof(CraftingUI), "SetDisenchantCostBarActive").Invoke(__instance, new object[] { false });
    }

    private static bool Permission(string __0, TAG_PREMIUM __1, ref bool __result)
    {
        if (!Enabled) return true;
        __result = PresentationEligible(__0, __1);
        return false;
    }

    private static bool CraftingEvent(string __0, TAG_PREMIUM __1, ref bool __2, ref bool __result)
    {
        if (!Enabled) return true;
        __2 = false;
        __result = PresentationEligible(__0, __1);
        return false;
    }

    private static IEnumerable<CodeInstruction> CraftingLogin(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        MethodInfo original = Required(typeof(Network), "IsLoggedIn");
        MethodInfo replacement = Required(typeof(NativeCrafting), nameof(LoggedInForCrafting));
        int count = 0;
        foreach (CodeInstruction instruction in code)
        {
            if (!instruction.Calls(original)) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
            count++;
        }
        int expected = __originalMethod.DeclaringType == typeof(CraftingUI) ? 3 : 1;
        if (count != expected) throw new InvalidOperationException("Crafting login call sites changed: " + __originalMethod + ": " + count);
        return code;
    }

    private static bool LoggedInForCrafting() => Enabled || Network.IsLoggedIn();

    // Offline, EntityDef.GetCardSet() is INVALID for cards whose set comes only from server event timings (Core
    // reprints such as First Flame). GameDbf has no record for it, and the native soulbound text dereferenced that null
    // for every owned copy: the card view threw NullReferenceException before its buttons were drawn.
    private static IEnumerable<CodeInstruction> MissingCardSet(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var replacements = new Dictionary<MethodInfo, MethodInfo> {
            [AccessTools.PropertyGetter(typeof(CardSetDbfRecord), "IsFeaturedCardSet")] = Required(typeof(NativeCrafting), nameof(FeaturedCardSet)),
            [AccessTools.PropertyGetter(typeof(CardSetDbfRecord), "IsCoreCardSet")] = Required(typeof(NativeCrafting), nameof(CoreCardSet))
        };
        if (replacements.Keys.Any(getter => getter == null)) throw new InvalidOperationException("Card set record getters changed.");
        var counts = replacements.Keys.ToDictionary(getter => getter, getter => 0);
        foreach (CodeInstruction instruction in code)
            foreach (var replacement in replacements)
            {
                if (!instruction.Calls(replacement.Key)) continue;
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement.Value;
                counts[replacement.Key]++;
            }
        if (counts.Values.Any(count => count != 1)) throw new InvalidOperationException("Crafting card set call sites changed: "
            + string.Join(",", counts.Values));
        return code;
    }

    // A missing set record is not featured, and is Core exactly when the shown card is a Core card.
    internal static bool FeaturedCardSet(CardSetDbfRecord record)
    {
        return record != null && record.IsFeaturedCardSet;
    }
    internal static bool CoreCardSet(CardSetDbfRecord record)
    {
        return record != null ? record.IsCoreCardSet : ShownCardIsCore();
    }
    private static bool ShownCardIsCore()
    {
        CraftingManager manager = CraftingManager.Get();
        return manager != null && manager.GetShownCardInfo(out EntityDef shown, out TAG_PREMIUM _)
            && LocalCardCatalog.IsCore(shown?.GetCardId());
    }

    private static bool DeckContentsReady(CollectionManager __instance, CollectionManager.DelOnAllDeckContents __0, ref bool __result)
    {
        if (!Enabled) return true;
        foreach (DeckType type in new[] { DeckType.NORMAL_DECK, DeckType.TAVERN_BRAWL_DECK })
            foreach (CollectionDeck deck in __instance.GetDecks(type))
            {
                deck.MarkNetworkContentsLoaded();
                __instance.GetBaseDeck(deck.ID)?.MarkNetworkContentsLoaded();
            }
        __result = false;
        __0?.Invoke();
        return false;
    }

    private static bool PreserveRecipe(CollectionDeck.ChangeSource __0) =>
        !Enabled || __0 != CollectionDeck.ChangeSource.ReconcileCardOwnership;

    private static bool LocalOnly() => !Enabled;

    private static void BeforeClose()
    {
        if (!Enabled) return;
        if (closing) throw new InvalidOperationException("Reentrant crafting close.");
        closing = true;
        batch = null;
        // Native prices the sale with GetCardValue(card, pending.Premium) and sends nothing when that is null.
        // SwitchPremiumView only changes the view's quality, so make Premium the quality the batch really uses
        // (a Core card has no Normal price: a golden sale seen from the Normal view would otherwise be dropped).
        var pending = CraftingManager.Get()?.GetPendingClientTransaction();
        if (pending != null && TryReadBatch(pending, out _, out _, out TAG_PREMIUM premium)) pending.Premium = premium;
    }

    internal static bool TryReadBatch(CraftingPendingTransaction transaction, out CraftDirection direction, out int count,
        out TAG_PREMIUM premium)
    {
        direction = CraftDirection.Craft;
        count = 0;
        premium = TAG_PREMIUM.NORMAL;
        if (transaction == null || string.IsNullOrEmpty(transaction.CardID)
            || transaction.GoldenCreateCount != 0
            || transaction.GoldenUpgradeFromNothingCount != 0) return false;
        int[] operations = { transaction.NormalCreateCount, transaction.NormalDisenchantCount,
            transaction.GoldenDisenchantCount, transaction.DiamondDisenchantCount, transaction.SignatureDisenchantCount,
            transaction.GoldenUpgradeFromNormalCount };
        if (operations.Any(value => value < 0) || operations.Count(value => value > 0) != 1) return false;
        int index = Array.FindIndex(operations, value => value > 0);
        count = operations[index];
        if (index == 5) { direction = CraftDirection.Upgrade; premium = TAG_PREMIUM.GOLDEN; return true; }
        if (index == 0) return true;
        direction = CraftDirection.Disenchant;
        premium = index == 1 ? TAG_PREMIUM.NORMAL : (TAG_PREMIUM)(index - 1);
        return true;
    }

    private static bool CommitBatch(CraftingPendingTransaction __0, int __2, int __3, int __4, int __5)
    {
        if (!NativeUIProbe.Active) return true;
        if (!Enabled)
        {
            log.LogWarning("NATIVE_CRAFT blocked transport without a crafting ledger.");
            return false;
        }
        if (!closing || batch != null)
        {
            ShowError("Crafting transaction arrived outside its card-view close.");
            return false;
        }
        batch = new BatchCompletion { Ledger = CraftingLedgerRegistry.Current, CardId = __0?.CardID };
        try
        {
            if (!TryReadBatch(__0, out batch.Direction, out batch.Count, out batch.Premium))
                throw new InvalidOperationException("Only one quality and direction per crafting batch is supported.");
            bool eligible = batch.Direction == CraftDirection.Upgrade
                ? LocalCrafting.TryGetUpgradePrice(batch.CardId, out _) && !batch.Ledger.PackRevealPending
                : Eligible(batch.CardId, batch.Premium);
            if (!eligible) throw new InvalidOperationException("This card cannot be crafted or disenchanted now.");
            int expected = batch.Premium == TAG_PREMIUM.GOLDEN ? __3 : batch.Premium == TAG_PREMIUM.SIGNATURE ? __4
                : batch.Premium == TAG_PREMIUM.DIAMOND ? __5 : __2;
            batch.Plan = batch.Direction == CraftDirection.Upgrade
                ? LocalCrafting.CommitUpgrade(NativeUIProbe.RewardProfile, batch.CardId, batch.Count, __2, __3)
                : batch.Premium == TAG_PREMIUM.NORMAL
                ? batch.Ledger.CommitBatch(batch.CardId, batch.Direction, batch.Count, expected)
                : LocalCrafting.CommitPremiumBatch(NativeUIProbe.RewardProfile, batch.CardId, (int)batch.Premium,
                    batch.Direction, batch.Count, expected);
            if (batch.Plan == null || !batch.Plan.Ok) batch.Error = batch.Plan?.Error ?? "Crafting ledger refused the transaction.";
        }
        catch (Exception error) { batch.Error = error.Message; log.LogError("NATIVE_CRAFT commit refused: " + error); }
        return false;
    }

    private static bool SafeStep(string step, Action action)
    {
        try { action(); return true; }
        catch (Exception error) { log.LogError("NATIVE_CRAFT " + step + " failed: " + error); return false; }
    }

    private static bool ClearPending(CraftingManager manager)
    {
        bool cleared = SafeStep("clear client pending", () => AccessTools.Field(typeof(CraftingManager), "m_pendingClientTransaction").SetValue(manager, null));
        cleared &= SafeStep("clear server pending", () => AccessTools.Field(typeof(CraftingManager), "m_pendingServerTransaction").SetValue(manager, null));
        cleared &= SafeStep("clear dust overlay", manager.ResetUnCommitedArcaneDustChanges);
        cleared &= SafeStep("close crafting mode", () => AccessTools.Property(typeof(CraftingManager), "IsInCraftingMode").SetValue(manager, false, null));
        return cleared;
    }

    private static void AfterClose(CraftingManager __instance)
    {
        FinishClose(__instance, false);
    }

    private static void FinishClose(CraftingManager manager, bool closeFailed)
    {
        if (!Enabled || !closing) return;
        BatchCompletion completion = batch;
        batch = null;
        closing = false;
        bool refreshed = ClearPending(manager) & !closeFailed;
        if (completion == null)
        {
            RefreshDust(CraftingLedgerRegistry.Current);
            if (closeFailed) SafeStep("close error popup", () => ShowError("Crafting could not finish. Reopen the collection and try again."));
            return;
        }
        bool committed = completion.Plan != null && completion.Plan.Ok;
        refreshed &= SafeStep("synchronize collection", () => Synchronize(completion.Ledger,
            committed ? completion.Plan.Cards.Select(card => card.CardId) : new[] { completion.CardId },
            completion.Direction == CraftDirection.Upgrade ? new[] { TAG_PREMIUM.NORMAL, TAG_PREMIUM.GOLDEN } : new[] { completion.Premium }));
        refreshed &= RefreshDust(completion.Ledger);
        if (committed && completion.Direction == CraftDirection.Upgrade)
            refreshed &= SafeStep("upgrade decks", () => NativeUIProbe.RefreshUpgradedDecks(completion.CardId));
        refreshed &= RefreshPage();
        if (!committed) { SafeStep("refusal popup", () => ShowError(completion.Error)); return; }
        if (completion.Direction == CraftDirection.Craft || completion.Direction == CraftDirection.Upgrade)
            refreshed &= SafeStep("achievement cosmetics", NativeUIProbe.RefreshCosmetics);
        for (int i = 0; i < completion.Count; i++)
            refreshed &= SafeStep("card notification", () => {
                var sale = new Network.CardSaleResult {
                    AssetID = GameUtils.TranslateCardIdToDbId(completion.CardId), AssetName = completion.CardId,
                    Premium = completion.Premium,
                    Action = completion.Direction == CraftDirection.Craft ? Network.CardSaleResult.SaleResult.CARD_WAS_BOUGHT
                        : completion.Direction == CraftDirection.Upgrade ? Network.CardSaleResult.SaleResult.CARD_WAS_UPGRADED
                        : Network.CardSaleResult.SaleResult.CARD_WAS_SOLD
                };
                if (completion.Direction == CraftDirection.Craft) manager.OnCardCreated(sale);
                else if (completion.Direction == CraftDirection.Upgrade) manager.OnCardUpgraded(sale);
                else manager.OnCardDisenchanted(sale);
            });
        if (!refreshed) SafeStep("refresh error popup", ShowRefreshError);
    }

    private static Exception CloseFailed(CraftingManager __instance, Exception __exception)
    {
        if (__exception == null || !Enabled || !closing) return __exception;
        log.LogError("NATIVE_CRAFT close failed: " + __exception);
        FinishClose(__instance, true);
        return null;
    }

    private static void Synchronize(ICraftingLedger ledger, IEnumerable<string> cardIds, params TAG_PREMIUM[] premiums)
    {
        var manager = CollectionManager.Get();
        var changes = new List<CollectionManager.CardModification>();
        var owned = ledger.OwnedCounts();
        if (premiums.Length == 0) premiums = new[] { TAG_PREMIUM.NORMAL };
        foreach (string id in cardIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal))
        foreach (TAG_PREMIUM premium in premiums)
        {
            int count = premium == TAG_PREMIUM.NORMAL ? (owned.TryGetValue(id, out int normal) ? normal : 0)
                : NativeUIProbe.RewardProfile.OwnedCount(id, (int)premium);
            int difference = checked(count - manager.GetNumCopiesInCollection(id, premium));
            if (difference == 0) continue;
            changes.Add(new CollectionManager.CardModification {
                cardID = id, premium = premium, count = checked(Math.Abs(difference)), seenBefore = true,
                modificationType = difference > 0 ? CollectionManager.CardModification.ModificationType.Add
                    : CollectionManager.CardModification.ModificationType.Remove
            });
        }
        if (changes.Count > 0)
            NativeUIProbe.ApplyCraftingChanges(changes);
    }

    private static void SettlementDust(LocalProfile __instance)
    {
        if (Enabled && ReferenceEquals(__instance, NativeUIProbe.RewardProfile)) RefreshDust(CraftingLedgerRegistry.Current);
    }

    private static bool RefreshDust(ICraftingLedger ledger)
    {
        bool refreshed = SafeStep("publish dust", () => publish(new NetCache.NetCacheArcaneDustBalance { Balance = ledger?.DustBalance ?? 0 }));
        refreshed &= SafeStep("refresh bank", () => { if (CraftingManager.IsInitialized) CraftingManager.Get().UpdateBankText(); });
        ArcaneDustAmount[] displays = Array.Empty<ArcaneDustAmount>();
        refreshed &= SafeStep("find dust displays", () => displays = UnityEngine.Object.FindObjectsByType<ArcaneDustAmount>(FindObjectsSortMode.None));
        foreach (ArcaneDustAmount amount in displays)
            refreshed &= SafeStep("refresh dust display", amount.UpdateCurrentDustAmount);
        return refreshed;
    }

    private static bool RefreshPage()
    {
        bool refreshed = SafeStep("refresh page contents", () => CollectionManager.Get().GetCollectibleDisplay()
            ?.GetPageManager()?.RefreshCurrentPageContents());
        refreshed &= SafeStep("refresh card locks", () => CollectionManager.Get().GetCollectibleDisplay()?.UpdateCurrentPageCardLocks());
        return refreshed;
    }

    private static void ShowRefreshError()
    {
        ShowError("Crafting view could not refresh. Your progress is saved. Reopen the collection to refresh its display.");
    }

    private static void WarnMassRefresh()
    {
        if (massRefreshErrorShown) return;
        massRefreshErrorShown = true;
        SafeStep("mass refresh error popup", ShowRefreshError);
    }

    private static bool TryMassTotals(IEnumerable<KeyValuePair<int, int>> countsAndValues, long dust, out long amount)
    {
        amount = 0;
        if (dust < 0) return false;
        try
        {
            long cards = 0;
            foreach (var entry in countsAndValues)
            {
                if (entry.Key < 0 || entry.Value < 0) return false;
                cards = checked(cards + entry.Key);
                amount = checked(amount + checked((long)entry.Key * entry.Value));
            }
            return cards <= int.MaxValue && amount <= int.MaxValue && checked(dust + amount) <= int.MaxValue;
        }
        catch (OverflowException) { return false; }
    }

    private static void PreviewMass(MassDisenchant __instance, ref List<CollectibleCard> __0)
    {
        if (!Enabled) return;
        var cards = (__0 ?? new List<CollectibleCard>()).Where(card => card.PremiumType == TAG_PREMIUM.NORMAL
            && LocalCrafting.TryGetPrice(card.CardId, out _, out _)).ToList();
        // Native counts every version; the ledger disenchants Normal extras only, so preview from the ledger:
        // native gets copies reporting exactly those extras (nothing to sell means not listed).
        var owned = CraftingLedgerRegistry.Current.OwnedCounts();
        var preview = cards.Select(card => LedgerPreviewCard(card, LocalCrafting.MassExtras(owned, card.CardId)))
            .Where(card => card.IsCraftableDisenchantCount > 0).ToList();
        bool valid = TryMassTotals(preview.Select(card => {
            LocalCrafting.TryGetPrice(card.CardId, out _, out int sell);
            return new KeyValuePair<int, int>(card.IsCraftableDisenchantCount, sell);
        }), CraftingLedgerRegistry.Current.DustBalance, out long amount);
        // Empty input resets the native bars and total without running its unchecked int arithmetic.
        __0 = valid ? preview : new List<CollectibleCard>();
        if (!valid) log.LogWarning("NATIVE_CRAFT mass preview exceeds the native display limit.");
        if (!valid || amount == 0) SafeStep("mass preview button", () => __instance.m_disenchantButton.SetEnabled(false));
    }

    // IsCraftableDisenchantCount is max(OwnedCount - DefaultMaxCopiesPerDeck, 0): shift OwnedCount on a copy so it reads `extras`.
    private static CollectibleCard LedgerPreviewCard(CollectibleCard card, int extras)
    {
        var clone = (CollectibleCard)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(card, null);
        clone.OwnedCount = (int)Math.Min((long)extras + clone.DefaultMaxCopiesPerDeck, int.MaxValue);
        return clone;
    }

    private static bool HasMassExtras()
    {
        var ledger = CraftingLedgerRegistry.Current;
        if (ledger.PackRevealPending) return false;
        var owned = ledger.OwnedCounts();
        return TryMassTotals(GameUtils.GetAllCollectibleCardIds()
            .Where(id => LocalCrafting.TryGetPrice(id, out _, out _)).Select(id => {
                LocalCrafting.TryGetPrice(id, out _, out int sell);
                return new KeyValuePair<int, int>(LocalCrafting.MassExtras(owned, id), sell);
            }), ledger.DustBalance, out long amount) && amount > 0;
    }

    private static bool CommitMass()
    {
        if (!NativeUIProbe.Active) return true;
        if (!Enabled)
        {
            log.LogWarning("NATIVE_CRAFT blocked transport without a crafting ledger.");
            return false;
        }
        var ledger = CraftingLedgerRegistry.Current;
        CraftPlan plan = null;
        string errorMessage = null;
        string[] ids = Array.Empty<string>();
        try
        {
            if (closing || CraftingManager.Get()?.GetPendingClientTransaction()?.HasPendingTransactions() == true
                || ledger.PackRevealPending) throw new InvalidOperationException("Finish the pending card view or pack reveal first.");
            var preview = ledger.PlanMassDisenchant();
            if (preview == null || !preview.Ok) throw new InvalidOperationException(preview?.Error ?? "Mass disenchant refused.");
            ids = preview.Cards.Select(card => card.CardId).ToArray();
            if (!TryMassTotals(preview.Cards.Select(card => {
                LocalCrafting.TryGetPrice(card.CardId, out _, out int sell);
                return new KeyValuePair<int, int>(checked(-card.CountDelta), sell);
            }), ledger.DustBalance, out long amount) || amount < 1 || amount != preview.DustDelta)
                throw new InvalidOperationException("No extras to disenchant, or total exceeds the native display limit.");
            plan = ledger.CommitMassDisenchant();
            if (plan == null || !plan.Ok) throw new InvalidOperationException(plan?.Error ?? "Mass disenchant refused.");
        }
        catch (Exception error)
        {
            errorMessage = error.Message;
            log.LogError("NATIVE_CRAFT mass refused: " + error);
        }
        bool committed = plan?.Ok == true;
        massCompleting = true;
        massListenersFailed = false;
        massRefreshErrorShown = false;
        try
        {
            bool refreshed = SafeStep("mass synchronize", () => Synchronize(ledger,
                committed ? plan.Cards.Select(card => card.CardId) : ids));
            refreshed &= RefreshDust(ledger);
            refreshed &= RefreshPage();
            bool notified = false;
            if (committed)
            {
                notified = SafeStep("mass completion", () => {
                    massResponse = new Network.MassDisenchantResponse { Amount = checked((int)plan.DustDelta) };
                    Required(typeof(CollectionManager), "OnMassDisenchantResponse").Invoke(CollectionManager.Get(), null);
                });
            }
            if (!committed || !refreshed || !notified || massListenersFailed)
            {
                SafeStep("mass unblock", () => {
                    MassDisenchant modal = MassDisenchant.Get();
                    if (modal != null) Required(typeof(MassDisenchant), "BlockUI").Invoke(modal, new object[] { false });
                });
                SafeStep("mass restore button", () => MassDisenchant.Get()?.m_disenchantButton.SetEnabled(HasMassExtras()));
                if (committed) WarnMassRefresh();
                else SafeStep("mass error popup", () => ShowError(errorMessage));
            }
        }
        finally { massResponse = null; massCompleting = false; }
        return false;
    }

    private static bool MassCompleted(CollectionManager __instance)
    {
        if (!Enabled || massResponse == null) return true;
        var listeners = ((List<CollectionManager.OnMassDisenchant>)AccessTools.Field(typeof(CollectionManager),
            "m_massDisenchantListeners").GetValue(__instance)).ToArray();
        foreach (var listener in listeners)
            if (!SafeStep("mass listener", () => listener(massResponse.Amount))) massListenersFailed = true;
        return false;
    }

    private static bool MassResponse(ref Network.MassDisenchantResponse __result)
    {
        if (!Enabled || massResponse == null) return true;
        __result = massResponse;
        return false;
    }

    private static void MassUnblocked(bool __0)
    {
        if (!Enabled || __0 || massCompleting) return;
        bool refreshed = RefreshDust(CraftingLedgerRegistry.Current);
        refreshed &= RefreshPage();
        refreshed &= SafeStep("mass final button", () => MassDisenchant.Get()?.m_disenchantButton.SetEnabled(HasMassExtras()));
        if (!refreshed) WarnMassRefresh();
    }

    private static void ShowError(string message)
    {
        log.LogWarning("NATIVE_CRAFT " + message);
        DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
            m_headerText = "Crafting unavailable", m_text = message ?? "Crafting transaction refused.",
            m_showAlertIcon = true, m_responseDisplay = AlertPopup.ResponseDisplay.OK
        });
    }
}
