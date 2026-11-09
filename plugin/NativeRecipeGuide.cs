using Blizzard.T5.Jobs;
using Blizzard.T5.Core.Utils;
using Hearthstone.Core;
using UnityEngine;
using System;

// Clone the tray's native Back button so PegUI and test control use the same release path.
internal sealed class NativeRecipeGuide : MonoBehaviour
{
    private CollectionManagerDisplay owner;
    private bool showing;
    private UIBButton button;
    private UIBButton source;
    private Camera camera;

    internal static void Attach(CollectionManagerDisplay display)
    {
        var guide = display.GetComponent<NativeRecipeGuide>() ?? display.gameObject.AddComponent<NativeRecipeGuide>();
        guide.owner = display;
    }

    private void LateUpdate()
    {
        try
        {
            var recipe = owner == null ? null : NativeUIProbe.CurrentRecipeGuide(owner);
            if (recipe != null && button == null)
            {
                var tray = CollectionDeckTray.Get();
                source = tray?.m_doneButton;
                if (source == null) return;
                button = Instantiate(source, source.transform.parent);
                button.name = "LocalRecipeGuideButton";
                button.ClearEventListeners();
                button.BubbleUpEvent = "";
                button.SetText("Recipe guide");
                camera = CameraUtils.FindFirstByLayerMask(1 << source.gameObject.layer)
                    ?? throw new InvalidOperationException("Native guide button camera unavailable.");
                // 1.4x the Done plate: the centre slices stretch and the end caps stay native (a root x-scale stretched the rivet caps 40%).
                NativeButtonKit.FitOrThrow(button, 1.4f);
                button.SetEnabled(true);
                button.AddEventListener(UIEventType.RELEASE, _ => {
                    var current = NativeUIProbe.CurrentRecipeGuide(owner);
                    if (!showing && current != null) ShowPage(current, 0);
                });
            }
            if (button != null)
            {
                // Anchor above the page, clear of the tray's card counter and native Back.
                var screen = camera.WorldToScreenPoint(source.transform.position);
                screen.x = Screen.width * 0.5f;
                screen.y = Screen.height * 0.965f;
                button.transform.position = camera.ScreenToWorldPoint(screen);
                button.gameObject.SetActive(recipe != null && !showing
                    && UniversalInputManager.Get()?.IsDialogActive() != true);
            }
        }
        catch (Exception error)
        {
            NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-button", error));
            if (button != null) button.gameObject.SetActive(false);
            enabled = false;
        }
    }

    private void OnDestroy()
    {
        if (button != null) Destroy(button.gameObject);
    }

    private void ShowPage(DeckRecipe recipe, int page)
    {
        ShowPages(recipe, page, () => owner != null && NativeUIProbe.CurrentRecipeGuide(owner)?.Id == recipe.Id,
            () => this != null, open => showing = open);
    }

    // The shared two-page guide dialog (Strategy, Mulligan) (the builder's Recipe guide and the picker's Deck Guide). stillValid is asked before each page
    // (the deck the guide belongs to may have changed); alive guards the delayed next page; state(true) while a page is up, state(false)
    // when the player closes it or the chain ends. Returns whether the first page was raised.
    internal static bool ShowPages(DeckRecipe recipe, int page, Func<bool> stillValid, Func<bool> alive, Action<bool> state)
    {
        if (!stillValid()) { state(false); return false; }
        // A first page is already on its way (a second press while the card popup loads): one chain only, never a text page on top of it.
        if (page == 0 && NativeGuideCards.Current != null) return true;
        string[] bodies = NativeRecipeGuideText.Sections(recipe);
        bool next = page < bodies.Length - 1;
        // Cards first: the page's native card pictures (CardListPopup). Anything wrong falls back to the text page below.
        Action<bool> advance = pressedNext => {
            state(false);
            if (next && pressedNext)
            {
                state(true);
                Processor.ScheduleCallback(0.05f, false, _ => { if (alive()) ShowPages(recipe, page + 1, stillValid, alive, state); });
            }
        };
        if (NativeGuideCards.TryShow(NativeDeckGuideText.PopupId(recipe.Id, page), NativeRecipeGuideText.PageHeader(recipe, page),
            NativeRecipeGuideText.PageBody(recipe, page), NativeRecipeGuideText.Cards(recipe, page), next, advance,
            () => { if (stillValid()) ShowTextPage(recipe, page, next, stillValid, alive, state); else state(false); }))
        { state(true); return true; }
        return ShowTextPage(recipe, page, next, stillValid, alive, state);
    }

    private static bool ShowTextPage(DeckRecipe recipe, int page, bool next, Func<bool> stillValid, Func<bool> alive, Action<bool> state)
    {
        var info = new AlertPopup.PopupInfo {
            m_id = NativeDeckGuideText.PopupId(recipe.Id, page),
            m_headerText = NativeRecipeGuideText.PageHeader(recipe, page),
            m_text = NativeRecipeGuideText.PageBody(recipe, page), m_showAlertIcon = false,
            m_responseDisplay = next ? AlertPopup.ResponseDisplay.CONFIRM_CANCEL : AlertPopup.ResponseDisplay.CANCEL,
            m_confirmText = "NEXT", m_cancelText = "CLOSE", m_keyboardEscIsCancel = true,
            m_responseCallback = (response, data) => {
                state(false);
                if (next && response == AlertPopup.Response.CONFIRM)
                {
                    state(true);
                    Processor.ScheduleCallback(0.05f, false, _ => { if (alive()) ShowPages(recipe, page + 1, stillValid, alive, state); });
                }
            }
        };
        bool shown = DialogManager.Get() != null && DialogManager.Get().ShowUniquePopup(info);
        state(shown);
        return shown;
    }

    internal static void ShowError(string title, string text)
    {
        DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
            m_id = "LOCAL_RECIPE_SAVE_ERROR", m_headerText = title, m_text = text, m_showAlertIcon = false,
            m_responseDisplay = AlertPopup.ResponseDisplay.CANCEL, m_cancelText = "CLOSE", m_keyboardEscIsCancel = true
        });
    }
}
