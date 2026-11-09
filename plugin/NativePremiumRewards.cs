using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using Hearthstone.DataModels;
using Hearthstone.Progression;
using Hearthstone.UI;
using MiniJSON;
using UnityEngine;

// Presentation only. The pending match receipt owns persistence and the final reward Done owns acknowledgement.
internal sealed class NativePremiumRewards : ILocalTierRewardStep
{
    internal const string DoneId = "results.premium.done";
    private readonly ManualLogSource log;
    private Action done;
    private LocalResultsReceipt receipt;
    private LocalCardCount[] items = Array.Empty<LocalCardCount>();
    private LocalCardCount[][] pages = Array.Empty<LocalCardCount[]>();
    private readonly List<RewardItemDataModel> boundItems = new List<RewardItemDataModel>();
    private Widget widget;
    private RewardScroll scroll;
    private DialogBase dialog;
    private int generation, page, nextPageFrame;
    private float began, readyAt = -1, clickedAt = -1, fallbackAt;
    private readonly Dictionary<PegUIElement, UIEvent.Handler> dismissHandlers = new Dictionary<PegUIElement, UIEvent.Handler>();
    private GameObject resultsHitbox;
    private bool resultsHitboxActive;
    internal int DismissalCount { get; private set; }
    private string lastDismissal = "", lastMatch = "";
    internal bool DismissalSettled => !nextPage && (done == null || (fallback ? dialog != null : DismissReady));
    internal JsonNode State => new JsonNode {
        { "active", done != null }, { "page", done == null ? 0 : page + 1 }, { "pages", pages.Length },
        { "dismissReady", DismissReady }, { "dismissed", DismissalCount > 0 && (receipt == null || lastMatch == receipt.MatchId) }, { "dismissals", DismissalCount },
        { "lastDismissal", lastDismissal }, { "match", receipt?.MatchId ?? lastMatch }
    };
    private bool fallback, popup, nextPage;
    private static readonly System.Reflection.FieldInfo ClickablePeg = AccessTools.Field(typeof(Clickable), "m_pegUiElement");

    internal NativePremiumRewards(ManualLogSource logger) { log = logger; }
    public bool AwaitingPlayer => done != null && (DismissReady || dialog != null);
    // CARD widgets have their own Clickables; prefer the scroll's catcher, never infer dismissal from a card rect.
    private Clickable DismissClickable => scroll == null || widget == null ? null : widget.GetComponentsInChildren<Clickable>(true)
        .OrderByDescending(clickable => ReferenceEquals(clickable.Owner, scroll.GetComponent<WidgetTemplate>()))
        .FirstOrDefault(clickable => clickable.isActiveAndEnabled && clickable.Active);
    internal PegUIElement DismissElement
    {
        get
        {
            var clickable = DismissClickable;
            return clickable == null ? null : ClickablePeg?.GetValue(clickable) as PegUIElement ?? clickable.GetComponent<PegUIElement>();
        }
    }
    private bool DismissReady
    {
        get
        {
            try
            {
                // The factory's ready callback precedes Initialize/Show; only live dismiss input pauses the watchdog.
                var clickable = DismissClickable;
                var element = DismissElement;
                return clickable != null && clickable.isActiveAndEnabled && clickable.Active
                    && clickable.Owner != null && !clickable.Owner.IsDesiredHiddenInHierarchy
                    && element != null && element.gameObject.activeInHierarchy && element.IsEnabled()
                    && element.IsEnabled(UIEventType.PRESS) && element.IsEnabled(UIEventType.RELEASE)
                    && (element.GetComponents<Collider>().Any(collider => collider.enabled && collider.gameObject.activeInHierarchy)
                        || Proxies.Any(proxy => ReferenceEquals(proxy.Owner, element) && proxy.GetComponents<Collider>().Any(collider => collider.enabled && collider.gameObject.activeInHierarchy)));
            }
            catch { return false; }
        }
    }
    internal IEnumerable<PegUIElement> Controls => done == null ? Enumerable.Empty<PegUIElement>() : dismissHandlers.Keys;
    internal string ControlId(PegUIElement element) => ReferenceEquals(element, DismissElement) ? DoneId
        : DoneId + "." + element.GetInstanceID();
    internal IEnumerable<PegUIElementProxy> Proxies => widget == null ? Enumerable.Empty<PegUIElementProxy>()
        : widget.GetComponentsInChildren<PegUIElementProxy>(true).Where(proxy => proxy.Owner != null && dismissHandlers.ContainsKey(proxy.Owner));
    internal bool OwnsInput(PegUIElement element) => done != null && element != null
        && (ReferenceEquals(element, DismissElement) || widget != null && element.transform.IsChildOf(widget.transform)
            || dialog != null && element.transform.IsChildOf(dialog.transform));
#if !SOLOSTONE_RELEASE
    internal JsonList ItemState() => TestControlProtocol.List((scroll == null ? Enumerable.Empty<RewardItemDataModel>() : boundItems).Select(item => (object)new JsonNode {
        { "type", item.ItemType.ToString() }, { "cardId", item.Card.CardId }, { "dbfId", item.ItemId },
        { "premium", (int)item.Card.Premium }, { "quantity", item.Quantity }, { "page", page + 1 }, { "pages", pages.Length },
        { "ledgerCount", NativeUIProbe.RewardProfile?.OwnedCount(item.Card.CardId, (int)item.Card.Premium) },
        { "nativeCount", CollectionManager.Get()?.GetOwnedCount(item.Card.CardId, item.Card.Premium) }
    }));
#endif

    public void Present(LocalResultsReceipt value, Action completed)
    {
        Retire();
        pages = value.PremiumUnlockPages;
        if (pages.Length == 0) { completed(); return; }
        receipt = value; done = completed; began = Time.realtimeSinceStartup;
        // RewardPresenter.ShowNextReward does this; ShowFakeRewardWidget (welcome/HUB path) does not.
        var end = EndGameScreen.Get();
        if (end != null && NativeResultsUI.OwnsScreen(end) && end.m_hitbox != null)
        {
            resultsHitbox = end.m_hitbox.gameObject; resultsHitboxActive = resultsHitbox.activeSelf;
            resultsHitbox.SetActive(false);
        }
        ShowPage();
    }
    private void ShowPage()
    {
        readyAt = clickedAt = -1;
        items = pages[page];
        boundItems.Clear();
        int token = ++generation;
        try
        {
            var nativeItems = new DataModelList<RewardItemDataModel>();
            foreach (var item in items)
            {
                var model = new RewardItemDataModel { ItemType = RewardItemType.CARD,
                    ItemId = GameUtils.TranslateCardIdToDbId(item.Id), Quantity = item.Count };
                if (model.ItemId <= 0 || !RewardUtils.InitializeRewardItemDataModel(model, TAG_RARITY.INVALID, (TAG_PREMIUM)item.Premium, out string reason))
                    throw new InvalidOperationException("Premium reward card unavailable: " + item.Id);
                nativeItems.Add(model);
                boundItems.Add(model);
            }
            var modelList = new RewardScrollDataModel {
                DisplayName = "Premium cards unlocked", Description = "Earned through Card Mastery and class levels."
                    + (pages.Length > 1 ? " (" + (page + 1) + "/" + pages.Length + ")" : ""),
                RewardList = new RewardListDataModel { Items = nativeItems },
                WillFavoriteItem = false, AllowQuickEquip = false, AllowGoToCollection = false
            };
            widget = RewardScroll.ShowFakeRewardWidget(modelList, () => {
                if (token != generation || done == null) return;
                if (scroll == null || FatalErrorMgr.IsInitialized() && FatalErrorMgr.Get().HasError())
                { Failed(new InvalidOperationException("Premium scroll hidden without completion.")); return; }
                AdvancePage("native", nativeHidden: true);
            }, shown => {
                if (token != generation || done == null)
                {
                    if (shown != null) UnityEngine.Object.Destroy(shown.GetComponentInParent<WidgetInstance>()?.gameObject ?? shown.gameObject);
                    return;
                }
                scroll = shown;
                log?.LogInfo("NATIVE_PREMIUM_SHOWN match=" + receipt.MatchId + ";items=" + items.Length);
            }) ?? throw new InvalidOperationException("Premium reward scroll missing.");
        }
        catch (Exception error) { Failed(error); }
    }

    private void BindDismissInput()
    {
        if (scroll == null || widget == null) return;
        int token = generation;
        // Bind native catchers AND item Clickables: a CARD hit is not wired to CODE_HIDE by every prefab variant.
        var controls = widget.GetComponentsInChildren<Clickable>(true).Select(clickable =>
            ClickablePeg?.GetValue(clickable) as PegUIElement ?? clickable.GetComponent<PegUIElement>())
            .Concat(widget.GetComponentsInChildren<PegUIElement>(true)).Where(element => element != null).Distinct();
        foreach (var element in controls)
        {
            if (dismissHandlers.ContainsKey(element)) continue;
            UIEvent.Handler handler = _ => {
                if (token != generation || done == null || scroll == null || clickedAt >= 0) return;
                clickedAt = Time.realtimeSinceStartup;
                log?.LogInfo("NATIVE_PREMIUM_CLICK match=" + receipt.MatchId + ";page=" + (page + 1) + ";control=" + element.name);
                try { scroll.Hide(false); }
                catch (Exception error) { log?.LogWarning("NATIVE_PREMIUM_HIDE_FAILED " + error.Message); }
            };
            element.AddEventListener(UIEventType.RELEASE, handler);
            dismissHandlers.Add(element, handler);
            log?.LogInfo("NATIVE_PREMIUM_CONTROL match=" + receipt.MatchId + ";page=" + (page + 1) + ";control=" + element.name);
        }
    }

    private void AdvancePage(string reason, bool nativeHidden = false)
    {
        log?.LogInfo("NATIVE_PREMIUM_DISMISSED match=" + receipt.MatchId + ";page=" + (page + 1)
            + ";pages=" + pages.Length + ";reason=" + reason);
        DismissalCount++; lastDismissal = reason; lastMatch = receipt.MatchId;
        generation++;
        if (nativeHidden) { UnbindDismissInput(); scroll = null; widget = null; }
        else Cleanup();
        page++;
        if (page < pages.Length) { nextPage = true; nextPageFrame = Time.frameCount + 1; }
        else Finish();
    }

    internal void Update()
    {
        if (done == null) return;
        if (nextPage)
        {
            if (Time.frameCount < nextPageFrame) return;
            nextPage = false; began = Time.realtimeSinceStartup; ShowPage();
        }
        float now = Time.realtimeSinceStartup;
        BindDismissInput();
        if (!fallback && DismissReady)
        {
            if (readyAt < 0) readyAt = now;
            began = now;
        }
        if (!fallback && (clickedAt >= 0 && now - clickedAt >= 5f || readyAt >= 0 && now - readyAt >= 120f))
        {
            AdvancePage(clickedAt >= 0 ? "click-timeout" : "ready-timeout");
            return;
        }
        // Native Hide can destroy the widget before a missing callback; keep the short click recovery deadline.
        if (!fallback && clickedAt >= 0) return;
        // Even an unavailable/unclickable fallback must eventually release results.
        if (fallback && now - fallbackAt >= 120f) { Finish(); return; }
        if (!fallback && (widget == null || Time.realtimeSinceStartup - began >= 45f))
            Failed(new InvalidOperationException("Premium reward scroll lost or timed out."));
        if (!fallback || popup || !(NativeUIProbe.RewardContextReady || NativeUIProbe.GameplayRewardContextReady)) return;
        int token = generation;
        popup = DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
            m_id = "LOCAL_PREMIUM_REWARDS_" + receipt.MatchId, m_headerText = "PREMIUM CARDS UNLOCKED",
            m_text = string.Join("\n", receipt.PremiumUnlocks.Select(item => LocalResultsReceipt.PremiumName(item.Premium) + " "
                + (SabberStoneCore.Model.Cards.FromId(item.Id)?.Name ?? item.Id) + " x" + item.Count))
                + "\n\nYour cards are already saved.",
            m_showAlertIcon = false, m_responseDisplay = AlertPopup.ResponseDisplay.CANCEL,
            m_cancelText = "CONTINUE", m_keyboardEscIsCancel = false,
            m_responseCallback = (response, _) => {
                if (token != generation || done == null || response != AlertPopup.Response.CANCEL) return;
                dialog = null; Finish();
            }
        }, (shown, _) => {
            if (token != generation || done == null) return false;
            dialog = shown; return true;
        }) == true;
    }

    private void Failed(Exception error)
    {
        log?.LogWarning("NATIVE_PREMIUM_FALLBACK match=" + receipt?.MatchId + ";" + error.Message);
        generation++;
        Cleanup(); fallback = true; fallbackAt = Time.realtimeSinceStartup;
    }
    private void Finish()
    {
        var completed = done;
        Retire(); completed?.Invoke();
    }
    public void Retire()
    {
        generation++; done = null;
        if (resultsHitbox != null) resultsHitbox.SetActive(resultsHitboxActive);
        resultsHitbox = null;
        Cleanup(); receipt = null; items = Array.Empty<LocalCardCount>(); pages = Array.Empty<LocalCardCount[]>();
        boundItems.Clear();
        page = 0; fallback = popup = nextPage = false;
    }
    private void UnbindDismissInput()
    {
        foreach (var handler in dismissHandlers)
            if (handler.Key != null) handler.Key.RemoveEventListener(UIEventType.RELEASE, handler.Value);
        dismissHandlers.Clear();
    }
    private void Cleanup()
    {
        UnbindDismissInput();
        try
        {
            if (scroll != null && SceneMgr.Get()?.GetMode() != SceneMgr.Mode.GAMEPLAY)
                UIContext.GetRoot()?.DismissPopup(scroll.gameObject);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_PREMIUM_POPUP_CLEANUP " + error.Message); }
        try
        {
            // The native factory can throw in Show after its ready callback, before returning the widget.
            var owner = widget != null ? widget.gameObject : scroll?.GetComponentInParent<WidgetInstance>()?.gameObject ?? scroll?.gameObject;
            if (owner != null) UnityEngine.Object.Destroy(owner);
            if (dialog != null) dialog.Hide();
        }
        catch (Exception error) { log?.LogWarning("NATIVE_PREMIUM_CLEANUP " + error.Message); }
        finally { widget = null; scroll = null; dialog = null; }
    }
}
