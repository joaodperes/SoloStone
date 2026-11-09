#if !SOLOSTONE_RELEASE
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hearthstone.UI;
using MiniJSON;
using UnityEngine;

// Read only, called exclusively by the enabled test-control UI sampler. Clicks remain NativeUiTree's PegUI path.
internal static class NativePackTestUi
{
    internal static object Field(object owner, string name) => owner == null ? null : AccessTools.Field(owner.GetType(), name)?.GetValue(owner);

    internal static Dictionary<PegUIElement, JsonNode> Name(UiSnapshot snapshot, System.Action<PegUIElement, string> name,
        List<PegUIElement> controls, List<PegUIElementProxy> proxies)
    {
        var details = new Dictionary<PegUIElement, JsonNode>();
        if (snapshot.Scene != "PACKOPENING") return details;
        var pack = PackOpening.Get();
        var director = Field(pack, "m_director") as PackOpeningDirector;
        if (pack == null || director == null) return details;
        bool mass = director.IsMassPackOpening();
        snapshot.PackOpening = new JsonNode { { "mass", mass }, { "playing", director.IsPlaying() },
            { "packRevealPending", LocalLadder.Profile?.PackRevealPending == true } };
        void Add(PegUIElement element, string id, bool shown, JsonNode detail = null)
        {
            if (element == null) return;
            name(element, id); controls.Add(element);
            detail = detail ?? new JsonNode(); detail["shown"] = shown && element.gameObject.activeInHierarchy;
            details[element] = detail;
        }
        void Button(object owner, string field, string id, bool shown)
        {
            var root = Field(owner, field) as GameObject;
            if (root == null) return;
            proxies.AddRange(root.GetComponentsInChildren<PegUIElementProxy>(true));
            var clickables = root.GetComponentsInChildren<Clickable>(true)
                .Select(clickable => Field(clickable, "m_pegUiElement") as PegUIElement ?? clickable.GetComponent<PegUIElement>());
            var elements = clickables.Concat(root.GetComponentsInChildren<PegUIElement>(true)).Where(element => element != null).Distinct().ToArray();
            // A pinned button has one Clickable; ambiguity must stay visibly unpressable rather than pick a bypass.
            if (elements.Length == 1) Add(elements[0], id, shown);
            else snapshot.Elements.Add(new UiElement { Id = id, Type = "PackButton", Instance = root.GetInstanceID(),
                Pack = new JsonNode { { "shown", shown } }, Diag = "Native button adapter expected one PegUI element; found " + elements.Length });
        }
        Add(pack.m_BackButton, TestControlPacks.Back, true);
        Add(NativePackFlip.Button, PackFlipText.ButtonId, true);
        var done = Field(director, "m_doneButton") as PegUIElement;
        Add(done, TestControlPacks.Done, director.IsDoneButtonShown,
            new JsonNode { { "clickReady", done != null && done.HasEventListener(UIEventType.RELEASE) } });
        var highlights = Field(director, "m_massPackOpeningHighlights");
        var summary = Field(director, "m_massPackOpeningSummary");
        Button(highlights, "m_buttonReveal", TestControlPacks.Reveal, director.IsMassPackOpeningHighlightsRevealButtonShowing());
        Button(highlights, "m_buttonContinue", TestControlPacks.Next, director.IsMassPackOpeningHighlightsContinueButtonShowing());
        Button(summary, "m_buttonDone", TestControlPacks.SummaryDone, director.IsMassPackOpeningSummaryDoneButtonShowing());
        if (mass && Field(summary, "m_cards") is List<NetCache.BoosterCard> summaryCards)
            snapshot.PackOpening["summaryCards"] = TestControlProtocol.List(summaryCards.Select(card => (object)new JsonNode {
                { "cardId", card.Def.Name }, { "premium", (int)card.Def.Premium } }));
        // Include inactive actors too: a missing/hidden reveal remains diagnosable. Instance ids survive every flip.
        IEnumerable<PackOpeningCard> Cards(object owner) => Field(owner, "m_cards") as IEnumerable<PackOpeningCard> ?? Enumerable.Empty<PackOpeningCard>();
        foreach (var card in Cards(Field(director, "m_hiddenCards")).Concat(Cards(highlights))
            .Concat(pack.GetComponentsInChildren<PackOpeningCard>(true))
            .Concat(Object.FindObjectsByType<PackOpeningCard>(FindObjectsSortMode.None)).Where(card => card != null).Distinct())
        {
            string id = TestControlPacks.CardId(card.GetInstanceID());
            var detail = new JsonNode { { "cardId", card.GetCardId() }, { "premium", (int)card.GetPremium() },
                { "revealed", card.IsRevealed() }, { "ready", card.IsReady() },
                { "inputEnabled", card.IsInputEnabled() }, { "revealEnabled", card.IsRevealEnabled() }, { "shown", card.gameObject.activeInHierarchy } };
            var element = card.GetCardRevealButton();
            if (element != null) Add(element, id, card.gameObject.activeInHierarchy, detail);
            else snapshot.Elements.Add(new UiElement { Id = id, Type = "PackOpeningCard", Instance = card.GetInstanceID(),
                Pack = detail, Diag = "Native card reveal button not loaded", Enabled = false });
        }
        foreach (var root in new Component[] { pack, director.m_massPackOpeningHighlightsWidget, director.m_massPackOpeningSummaryWidget })
            if (root != null) proxies.AddRange(root.GetComponentsInChildren<PegUIElementProxy>(true));
        return details;
    }
}
#endif
