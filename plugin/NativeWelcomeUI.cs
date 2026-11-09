using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Hearthstone.DataModels;
using Hearthstone.Progression;
using Hearthstone.UI;
using UnityEngine;

// #57 welcome (owner 2026-10-28): one native AlertPopup (NativeWelcomeText) at the idle HUB of a save whose welcome gift
// is unseen, then the retail reward scroll ("You've received": Standard packs, gold, dust; cards and the deck kit in its
// description). The gift itself was committed when the save was created; dismissing the scroll only commits Seen
// (NativeWelcomeFlow has the rules). One attempt per hub session: a failed Seen commit shows both again on the next
// launch, never re-grants. Fails closed: nothing throws into the probe's Update or a native callback.
internal static class NativeWelcomeUI
{
    private const float MaxCountedFrameSeconds = 0.25f;
    private static readonly FieldInfo ClickablePeg = AccessTools.Field(typeof(Clickable), "m_pegUiElement");
    private static ManualLogSource log;
    private static bool disabled;
    private static AlertPopup.PopupInfo raised;
    private static readonly NativeWelcomeFlow flow = new NativeWelcomeFlow();
    private static LocalProfile owner;
    private static Widget widget;
    private static RewardScroll scroll;
    private static Clickable dismiss;
    private static int token;

    internal static void Initialize(ManualLogSource logger)
    {
        log = logger;
        UiRules.WelcomeControlAllowed = AllowControl;
    }

    // From OK until the scroll is gone it owns the HUB like a reward presentation (NativeRewardUI.BlocksHome, toasts,
    // tier catch-up): nothing else opens between the popup and the scroll or over the scroll.
    internal static bool ScrollActive => flow.ScrollActive;

    internal static bool OwnsInput(PegUIElement element) => flow.ScrollActive && element != null && widget != null
        && (ReferenceEquals(element, DismissElement) || element.transform.IsChildOf(widget.transform));

    // Test control: the scroll's dismiss control (Clickable spawns DontSave PegUIElements missed by global enumeration).
    internal static IEnumerable<PegUIElement> ScrollControls
    {
        get
        {
            PegUIElement element = flow.Phase == NativeWelcomePhase.Shown && widget != null ? DismissElement : null;
            return element != null ? new[] { element } : Enumerable.Empty<PegUIElement>();
        }
    }

    internal static IEnumerable<PegUIElementProxy> ScrollProxies
    {
        get
        {
            PegUIElement element = ScrollControls.FirstOrDefault();
            return element == null ? Enumerable.Empty<PegUIElementProxy>()
                : widget.GetComponentsInChildren<PegUIElementProxy>(true).Where(proxy => ReferenceEquals(proxy.Owner, element));
        }
    }

    private static PegUIElement DismissElement
    {
        get
        {
            // Resolved lazily (review): the ready callback runs before retail's Initialize + Show.
            if (dismiss == null && widget != null && flow.Phase == NativeWelcomePhase.Shown) dismiss = widget.GetComponentInChildren<Clickable>();
            if (dismiss == null) return null;
            return ClickablePeg?.GetValue(dismiss) as PegUIElement ?? dismiss.GetComponent<PegUIElement>();
        }
    }

    // Escape over the scroll dismisses it (same Hide as its own click); while it loads Escape does nothing.
    internal static bool DismissOnEscape()
    {
        if (!flow.ScrollActive) return false;
        try { if (flow.Phase == NativeWelcomePhase.Shown && scroll != null) scroll.Hide(false); }
        catch (Exception error) { log?.LogWarning("NATIVE_WELCOME scroll escape failed: " + error.Message); }
        return true;
    }

    // Test control: only the OK button of the welcome popup this session raised (same PopupInfo instance), or the shown
    // welcome scroll's own dismiss control, is lifted from the copy's word filter. Purchase, confirm and kit dialogs never match.
    private static bool AllowControl(UiElement element)
    {
        if (!(element.Handle is PegUIElement control)) return false;
        if (element.Id == NativeWelcomeText.ScrollDoneId)
            return flow.Phase == NativeWelcomePhase.Shown && widget != null && ReferenceEquals(control, DismissElement);
        if (raised == null) return false;
        AlertPopup popup = control.GetComponentInParent<AlertPopup>();
        return popup != null && ReferenceEquals(popup.GetInfo(), raised) && popup.GetInfo().m_id == NativeWelcomeText.PopupId
            && ReferenceEquals(control, popup.m_okayButton);
    }

    // Idle = the tier catch-up's reward context plus everything that owns the HUB (profile selector, results, rewards,
    // achievement toasts, Feats, shop, any dialog or menu: NativeUIProbe.AchievementToastContextReady).
    // The idle predicate scans the scene, so it runs only while a popup is actually due.
    internal static void Update(LocalProfile profile, Func<bool> idle, Func<bool> hubStable)
    {
        if (disabled) return;
        try
        {
            if (flow.CanShowAlert)
            {
                if (profile == null || !profile.WelcomePopupPending || profile.PendingMilestoneCatchUp != null || !idle()) return;
                if (!Show(profile)) return;
                flow.AlertShown();
                log?.LogInfo("NATIVE_WELCOME shown");
                return;
            }
            if (!flow.ScrollActive) return;
            if (!ReferenceEquals(owner, NativeUIProbe.RewardProfile)) { Lost("profile-changed"); return; }
            if ((flow.Phase == NativeWelcomePhase.Loading || flow.Phase == NativeWelcomePhase.Shown) && widget == null) { Lost("widget-destroyed"); return; }
            // Scene scans only while the scroll is still awaited (review): Loading/Shown never read the dialog or HUB state.
            bool awaiting = flow.Phase == NativeWelcomePhase.AwaitScroll;
            bool dialogShowing = awaiting && DialogManager.Get()?.ShowingDialog() == true;
            if (Application.isFocused && Time.timeScale > 0f && flow.Tick(Math.Min(Time.unscaledDeltaTime, MaxCountedFrameSeconds), dialogShowing))
            {
                Destroy();
                log?.LogWarning("NATIVE_WELCOME scroll not ready after " + NativeWelcomeFlow.ScrollTimeoutSeconds + " s of focused time; seen after the popup alone");
                CommitSeen(owner);
                return;
            }
            if (awaiting && !dialogShowing && flow.ReadyToOpenScroll(Time.frameCount, false, hubStable())) OpenScroll();
        }
        catch (Exception error) { Fail(error); }
    }

    private static bool Show(LocalProfile profile)
    {
        DialogManager dialogs = DialogManager.Get();
        if (dialogs == null) return false;
        var info = new AlertPopup.PopupInfo {
            m_id = NativeWelcomeText.PopupId, m_headerText = NativeWelcomeText.Header, m_text = NativeWelcomeText.Body,
            m_showAlertIcon = false, m_iconSet = AlertPopup.PopupInfo.IconSet.None,
            m_responseDisplay = AlertPopup.ResponseDisplay.OK, m_okText = NativeWelcomeText.OkLabel,
            m_responseCallback = (response, data) => Acknowledged(profile)
        };
        if (!dialogs.ShowUniquePopup(info)) return false;
        raised = info;
        return true;
    }

    // OK no longer commits Seen: it chains into the reward scroll (a later frame, once the popup's dialog is gone).
    private static void Acknowledged(LocalProfile profile)
    {
        try
        {
            if (!ReferenceEquals(profile, NativeUIProbe.RewardProfile)) { flow.Abandon(); return; }
            owner = profile;
            flow.AlertAcknowledged(Time.frameCount);
            log?.LogInfo("NATIVE_WELCOME ok; reward scroll next");
        }
        catch (Exception error) { Fail(error); }
    }

    private static void OpenScroll()
    {
        flow.ScrollRequested();
        int expected = ++token;
        try
        {
            RewardScrollDataModel model = ScrollModel();
            widget = RewardScroll.ShowFakeRewardWidget(model, () => Hidden(expected), shown => Ready(expected, shown))
                ?? throw new InvalidOperationException("RewardScroll widget missing");
            log?.LogInfo("NATIVE_WELCOME scroll requested items=" + model.RewardList.Items.Count);
        }
        catch (Exception error) { ScrollFailed(error); }
    }

    // Retail item models (RewardUtils for the booster; gold/dust built like RewardUtils does, since its GOLD path refuses
    // while virtual currency is off outside the shop). Replay the saved receipt, including historical V1 gifts.
    private static RewardScrollDataModel ScrollModel()
    {
        var items = new DataModelList<RewardItemDataModel>();
        var gift = owner.Welcome;
        foreach (var entry in NativeWelcomeText.ScrollItems(gift.Packs, gift.Gold, gift.Dust))
            items.Add(Item(entry.Key, entry.Value));
        return new RewardScrollDataModel {
            DisplayName = NativeWelcomeText.ScrollTitle, Description = NativeWelcomeText.ScrollDescription(gift.KitGoldCap, LocalWelcomeGift.CardSummary(gift)),
            RewardList = new RewardListDataModel { Items = items },
            // No favourite, quick equip or Go to Collection: those deep links and telemetry are retail-only.
            WillFavoriteItem = false, AllowQuickEquip = false, AllowGoToCollection = false
        };
    }

    private static RewardItemDataModel Item(NativeWelcomeText.ScrollItemKind kind, int quantity)
    {
        if (kind == NativeWelcomeText.ScrollItemKind.StandardPacks)
        {
            var booster = new RewardItemDataModel { ItemType = RewardItemType.BOOSTER, Quantity = quantity,
                ItemId = FairPackRules.NativeBoosterId(LocalWelcomeGift.PackType) };
            if (!RewardUtils.InitializeRewardItemDataModel(booster, TAG_RARITY.INVALID, TAG_PREMIUM.NORMAL, out string reason))
                throw new InvalidOperationException("welcome booster item: " + reason);
            return booster;
        }
        RewardItemType type = kind == NativeWelcomeText.ScrollItemKind.Gold ? RewardItemType.GOLD : RewardItemType.DUST;
        return new RewardItemDataModel { ItemType = type, Quantity = quantity,
            Currency = new PriceDataModel { Currency = RewardUtils.RewardItemTypeToCurrencyType(type), Amount = quantity,
                DisplayText = quantity.ToString() } };
    }

    // RewardScroll.ShowFakeRewardWidget's ready callback, right before Initialize + Show (QuestToast/tutorial path).
    // Retail still runs Initialize + Show after this callback, so the scroll is ours and Shown even if the diagnostics below fail.
    private static void Ready(int expected, RewardScroll shown)
    {
        try
        {
            if (expected != token || shown == null || !flow.ScrollReady()) return;
            scroll = shown;
        }
        catch (Exception error) { ScrollFailed(error); return; }
        try
        {
            // Retail's own dismiss for this prefab: the widget's first Clickable (TutorialProgressScreen.OnRewardChest).
            dismiss = widget != null ? widget.GetComponentInChildren<Clickable>() : null;
            log?.LogInfo("NATIVE_WELCOME scroll shown dismiss=" + (dismiss == null ? "none" : dismiss.name)
                + " clickables=[" + string.Join(",", (widget == null ? new Clickable[0] : widget.GetComponentsInChildren<Clickable>(true))
                    .Take(12).Select(clickable => clickable.name + (clickable.gameObject.activeInHierarchy ? "" : "(off)"))) + "]");
        }
        catch (Exception error) { dismiss = null; log?.LogWarning("NATIVE_WELCOME scroll shown; dismiss lookup failed: " + error.Message); }
    }

    // RewardScroll.Hide runs this before it destroys the widget. Its fatal-error listener also hides: that is no dismiss.
    private static void Hidden(int expected)
    {
        try
        {
            if (expected != token) return;
            bool fatal = FatalErrorMgr.IsInitialized() && FatalErrorMgr.Get().HasError();
            LocalProfile profile = owner;
            bool seen = flow.ScrollHidden(fatal);
            Release();
            if (seen) CommitSeen(profile);
            else log?.LogInfo("NATIVE_WELCOME scroll hidden without a dismiss" + (fatal ? " (fatal error)" : "") + "; seen not saved");
        }
        catch (Exception error) { log?.LogWarning("NATIVE_WELCOME scroll hide failed: " + error.Message); }
    }

    private static void CommitSeen(LocalProfile profile)
    {
        try
        {
            if (profile == null || !ReferenceEquals(profile, NativeUIProbe.RewardProfile)) { log?.LogInfo("NATIVE_WELCOME seen skipped: profile changed"); return; }
            profile.MarkWelcomeSeen();
            log?.LogInfo("NATIVE_WELCOME seen gold=" + profile.Gold + " dust=" + profile.DustBalance + " packs=" + profile.UnopenedPacks);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_WELCOME seen not saved: " + error.Message); }
    }

    // A scroll that could not be created or readied: the popup alone counts as seen. One that failed after it was shown
    // commits nothing (it shows again next launch).
    private static void ScrollFailed(Exception error)
    {
        bool fallback = flow.ScrollFailed();
        LocalProfile profile = owner;
        Destroy();
        log?.LogWarning("NATIVE_WELCOME scroll unavailable: " + error.Message + (fallback ? "; seen after the popup alone" : ""));
        if (fallback) CommitSeen(profile);
    }

    // Destroyed without a hide (scene change) or the profile changed: release the HUB; nothing is committed.
    private static void Lost(string reason)
    {
        flow.ScrollLost();
        Destroy();
        log?.LogInfo("NATIVE_WELCOME scroll gone (" + reason + "); seen not saved, shows again next launch");
    }

    // Retail's Show registered the scroll as a UIContext popup (blur over the HUB); RewardScroll.Hide dismisses it before
    // destroying. Do the same here (review), so a failed or lost scroll never leaves the blur behind.
    private static void Destroy()
    {
        token++;
        try { if (scroll != null) UIContext.GetRoot()?.DismissPopup(scroll.gameObject); }
        catch (Exception error) { log?.LogWarning("NATIVE_WELCOME scroll popup dismiss failed: " + error.Message); }
        try { if (widget != null) UnityEngine.Object.Destroy(widget.gameObject); } catch { }
        Release();
    }

    private static void Release() { widget = null; scroll = null; dismiss = null; }

    private static void Fail(Exception error)
    {
        disabled = true;
        log?.LogWarning("NATIVE_WELCOME unavailable: " + error.Message);
        // Mid-chain: release the HUB; before the scroll was shown the popup alone counts as seen (never stuck).
        if (flow.ScrollActive) ScrollFailed(error);
        else flow.Abandon();
    }
}
