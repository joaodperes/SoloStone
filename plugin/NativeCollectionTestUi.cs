#if !SOLOSTONE_RELEASE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Test-control adapters for the pinned Collection owners. No clicks or direct deck mutations.
internal static class NativeCollectionTestUi
{
    private static readonly FieldInfo NewDeck = (FieldInfo)NativeShop.UiBinding("DeckTrayDeckListContent", "m_newDeckButton");
    private static readonly FieldInfo Scrollbar = (FieldInfo)NativeShop.UiBinding("DeckTray", "m_scrollbar");
    private static readonly FieldInfo Templates = (FieldInfo)NativeShop.UiBinding("DeckTemplatePicker", "m_pickerButtons");
    private static readonly FieldInfo TemplateClass = (FieldInfo)NativeShop.UiBinding("DeckTemplatePicker", "m_currentSelectedClass");
    private static readonly MethodInfo Center = (MethodInfo)NativeShop.UiBinding("UIBScrollable", "CenterObjectInView");
    private static readonly FieldInfo Ease = (FieldInfo)NativeShop.UiBinding("UIBScrollable", "m_ScrollEaseType");
    private static readonly MethodInfo TweenCount = (MethodInfo)NativeShop.UiBinding("iTween", "Count");

    internal static string PlayId(CollectionManagerDisplay display, AbsDeckPickerTrayDisplay tray) =>
        display != null && display.IsSelectingNewDeckHero() && tray.GetComponentInParent<HeroPickerDisplay>() != null
            && tray.GetComponentInParent<HeroPickerDisplay>() == display.GetHeroPickerDisplay() ? "collection.class.choose" : "picker.play";

    internal static void Name(CollectionManagerDisplay display, Action<PegUIElement, string> name)
    {
        void Active(PegUIElement element, string id) { if (element != null && element.gameObject.activeInHierarchy) name(element, id); }
        var tray = CollectionDeckTray.Get();
        var content = tray?.GetDecksContent();
        if (content != null) Active(NewDeck?.GetValue(content) as PegUIElement, "collection.newdeck");
        if (display.IsSelectingNewDeckHero())
        {
            var picker = display.GetHeroPickerDisplay();
            if (picker != null)
                foreach (var hero in picker.GetComponentsInChildren<HeroPickerButton>(true))
                    Active(hero, "collection.class." + hero.m_heroClass.ToString().ToLowerInvariant());
        }
        var templates = UniversalInputManager.UsePhoneUI ? display.GetPhoneDeckTemplateTray()
            : (display.GetPageManager() as CollectionPageManager)?.GetDeckTemplatePicker();
        if (templates != null && templates.gameObject.activeInHierarchy)
        {
            Active(templates.m_customDeckButton, "collection.template.custom");
            Active(templates.m_chooseButton, "collection.template.choose");
            // Same ordered native list used by SetDeckFormatAndClass and SelectButtonWithIndex.
            if (Templates?.GetValue(templates) is List<DeckTemplatePickerButton> buttons
                && TemplateClass?.GetValue(templates) is TAG_CLASS heroClass)
            {
                var choices = CollectionManager.Get()?.GetNonStarterTemplateDecks(templates.CurrentSelectedFormat, heroClass);
                if (choices != null)
                    for (int index = 0; index < Math.Min(buttons.Count, choices.Count); index++)
                        Active(buttons[index], "collection.template." + choices[index].m_id);
            }
        }
        if (tray != null)
            foreach (var tile in tray.GetComponentsInChildren<DeckTrayDeckTileVisual>(true))
                if (!string.IsNullOrEmpty(tile.GetCardID())) Active(tile, "collection.deckcard." + tile.GetCardID());
    }

    private static UIBScrollable Scroller(UiElement item)
    {
        if (SceneMgr.Get()?.GetMode() != SceneMgr.Mode.COLLECTIONMANAGER || !(item?.Handle is PegUIElement control) || control == null) return null;
        var tray = CollectionDeckTray.Get();
        if (tray == null || !tray.gameObject.activeInHierarchy) return null;
        var scroller = Scrollbar?.GetValue(tray) as UIBScrollable;
        // The scrollbar can be a sibling of its contents: use its native owner, not a nearby hierarchy guess.
        return scroller != null && scroller.gameObject.activeInHierarchy && scroller.ScrollObject != null && scroller.m_ScrollBounds != null
            && control.transform.IsChildOf(scroller.ScrollObject.transform) ? scroller : null;
    }

    internal static string Scroll(UiElement item)
    {
        NativeTestControl.AssertMainThread();
        var scroller = Scroller(item);
        if (scroller == null || Center == null || Ease == null || TweenCount == null) return "Element is not in a supported Collection tray scrollable";
        if (!scroller.IsEnabled()) return "Collection tray scrollable disabled";
        // Same native centring API as the shop reveal. The caller waits for geometry, hit testing and native motion.
        Center.Invoke(scroller, new object[] { ((PegUIElement)item.Handle).gameObject, 0f, null, Ease.GetValue(scroller), scroller.m_ScrollTweenTime, false });
        return null;
    }

    internal static bool Clipped(UiElement item, List<Bounds> bounds)
    {
        var scroller = Scroller(item);
        if (scroller == null || bounds.Count == 0) return false;
        int axis = (int)scroller.m_ScrollPlane;
        float centre = (bounds.Min(bound => bound.min[axis]) + bounds.Max(bound => bound.max[axis])) / 2f;
        var viewport = scroller.m_ScrollBounds.bounds;
        return centre < viewport.min[axis] || centre > viewport.max[axis];
    }

    private sealed class ScrollSample { internal float Value; internal int Frame = -1; internal bool Moving; }
    private static readonly Dictionary<UIBScrollable, ScrollSample> samples = new Dictionary<UIBScrollable, ScrollSample>();

    internal static bool Scrolling(UiElement item)
    {
        var scroller = Scroller(item);
        if (scroller == null) return false;
        bool known = samples.TryGetValue(scroller, out var sample);
        if (!known)
        {
            foreach (var dead in samples.Keys.Where(key => key == null).ToList()) samples.Remove(dead);
            samples[scroller] = sample = new ScrollSample();
        }
        if (sample.Frame != Time.frameCount)
        {
            float value = scroller.GetScroll();
            sample.Moving = !known || Math.Abs(value - sample.Value) > 0.0001f || scroller.IsDragging()
                || TweenCount == null || (int)TweenCount.Invoke(null, new object[] { scroller.gameObject }) != 0
                || (int)TweenCount.Invoke(null, new object[] { scroller.ScrollObject }) != 0;
            sample.Value = value; sample.Frame = Time.frameCount;
        }
        return sample.Moving;
    }
}
#endif
