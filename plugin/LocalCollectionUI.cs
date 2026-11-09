using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SabberStoneCore.Enums;
using UnityEngine;
using EngineCard = SabberStoneCore.Model.Card;

internal sealed class LocalCollectionUI
{
    private const int CollectionPageSize = 12;
    private const int EditorPageSize = 6;
    private static readonly string[] Heroes = LocalDecks.PlayerHeroes.ToArray();
    private readonly LocalProfile profile;
    private readonly List<string> draftCards = new List<string>();
    private long? compatibilityDraftId;
    private string selectedDeck = "mage";
    private string draftName;
    private string draftHero;
    private string collectionFilter = "All";
    private int collectionPage, catalogPage, draftPage;
    private bool actionBusy;
    private string actionError, notice, hoveredTooltip;

    internal LocalCollectionUI(LocalProfile profile)
    {
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        IReadOnlyList<LocalCustomDeck> savedDecks = profile.CustomDecks;
        if (savedDecks.Count == 1)
        {
            LocalCustomDeck saved = savedDecks[0];
            compatibilityDraftId = saved.Id;
            draftName = saved.Name;
            draftHero = saved.Hero;
            draftCards.AddRange(saved.Cards);
        }
        else
        {
            SeedFromLoaner(0);
            // Several saved decks are normal (native Collection, ready-made deck purchases): inform, never an error state.
            if (savedDecks.Count > 1) notice = AmbiguousLibraryMessage();
        }
    }

    internal string SelectedDeck => selectedDeck;
    internal string DraftName => draftName ?? "";
    internal string DraftHero => draftHero ?? "";
    internal int DraftCount => draftCards.Count;
    internal bool DraftValid => DraftErrors().Count == 0;
    internal bool ActionBusy => actionBusy;
    internal string ActionError => actionError ?? "";
    internal string Notice => notice ?? "";
    internal string DraftCardIds => string.Join(",", SortedDraft().SelectMany(group => Enumerable.Repeat(group.Card.Id, group.Count)));

    internal void SelectCustom()
    {
        LocalCustomDeck saved = BoundDraft();
        selectedDeck = PersonalKey(saved.Id);
    }

    internal void SelectLoaner(string id) { selectedDeck = LocalDecks.Get(id).Id; }
    internal void LoadLoaner(int index) { SeedFromLoaner(index); }

    internal LocalDeck ResolveSelectedDeck()
    {
        if (!selectedDeck.StartsWith("custom:", StringComparison.Ordinal)) return LocalDecks.Get(selectedDeck);
        if (!long.TryParse(selectedDeck.Substring("custom:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            throw new InvalidOperationException("Personal deck identity is invalid.");
        LocalCustomDeck saved = profile.GetDraft(id) ?? throw new InvalidOperationException("Personal deck no longer exists.");
        return LocalDecks.Custom(saved.Name, saved.Hero, saved.Cards).WithSideboards(saved.Sideboards, true);
    }

    internal int PageFor(string screen) => screen == "Collection" ? collectionPage : screen == "DeckBuilder" ? catalogPage : 0;
    internal string FilterFor(string screen) => screen == "Collection" ? collectionFilter : screen == "DeckBuilder" ? HeroName(draftHero) : "All";
    internal string VisibleCardIds(string screen)
    {
        IEnumerable<EngineCard> cards = screen == "Collection" ? CollectionCards().Skip(collectionPage * CollectionPageSize).Take(CollectionPageSize)
            : screen == "DeckBuilder" ? CatalogCards().Skip(catalogPage * EditorPageSize).Take(EditorPageSize)
            : Enumerable.Empty<EngineCard>();
        return string.Join(",", cards.Select(card => card.Id));
    }

    internal void SetError(string message)
    {
        actionError = message;
        notice = null;
    }

    internal void ClearError() { actionError = null; }

    internal void Draw(string screen, Action<string> navigate, Action start, GUIStyle label, GUIStyle title, GUIStyle button)
    {
        hoveredTooltip = null;
        Panel(new Rect(40, 20, 1200, 680));
        // A public build carries no "Hearthstone" in its labels (I2b).
        GUI.Label(new Rect(65, 34, 410, 38),
#if SOLOSTONE_RELEASE
            "SOLOSTONE",
#else
            "HEARTHSTONE / LOCAL",
#endif
            title);
        GUI.Label(new Rect(500, 39, 590, 30), profile.Rank + "  |  " + StarText() + "  |  " + profile.Gold
            + " gold  |  " + profile.UnopenedPacks + " packs", label);
        if (GUI.Button(new Rect(1110, 30, 100, 42), "Exit", button)) Application.Quit();

        NavButton(new Rect(65, 84, 170, 44), "Menu", "Menu", screen, navigate, button);
        NavButton(new Rect(245, 84, 170, 44), "Shop", "Shop", screen, navigate, button);
        NavButton(new Rect(425, 84, 170, 44), "Packs", "Packs (" + profile.UnopenedPacks + ")", screen, navigate, button);
        NavButton(new Rect(605, 84, 220, 44), "Collection", "Collection", screen, navigate, button);
        NavButton(new Rect(835, 84, 255, 44), "DeckBuilder", "Deck Builder", screen, navigate, button);

        if (screen == "Menu") DrawMenu(navigate, start, label, button);
        else if (screen == "Shop") DrawShop(navigate, label, title, button);
        else if (screen == "Packs") DrawPacks(navigate, label, title, button);
        else if (screen == "Collection") DrawCollection(label, title, button);
        else if (screen == "DeckBuilder") DrawDeckBuilder(label, button);

        string feedback = actionError ?? notice;
        if (!string.IsNullOrEmpty(feedback))
        {
            var feedbackStyle = new GUIStyle(label) { fontSize = 16 };
            feedbackStyle.normal.textColor = actionError == null ? new Color(0.45f, 0.9f, 0.55f) : new Color(1f, 0.4f, 0.4f);
            GUI.Label(new Rect(65, 658, 1120, 28), feedback, feedbackStyle);
        }
        if (string.IsNullOrEmpty(actionError) && (screen == "Collection" || screen == "DeckBuilder")) DrawTooltip(label);
    }

    private void NavButton(Rect rect, string target, string text, string current, Action<string> navigate, GUIStyle button)
    {
        bool enabled = target != current && !actionBusy;
        if (Button(rect, text, enabled, button))
        {
            notice = null;
            navigate(target);
        }
    }

    private void DrawMenu(Action<string> navigate, Action start, GUIStyle label, GUIStyle button)
    {
        GUI.Label(new Rect(65, 146, 1120, 34), "Choose a deck: 8 free loaners + personal decks", label);
        var menuButton = new GUIStyle(button) { fontSize = 16, wordWrap = true };
        for (int i = 0; i < LocalDecks.All.Count; i++)
        {
            LocalDeck deck = LocalDecks.All[i];
            Rect rect = new Rect(65 + (i % 4) * 280, 190 + (i / 4) * 62, 270, 52);
            if (Button(rect, (selectedDeck == deck.Id ? "> " : "") + deck.Name, !actionBusy, menuButton))
                SelectLoaner(deck.Id);
        }

        IReadOnlyList<LocalCustomDeck> savedDecks = profile.CustomDecks;
        LocalCustomDeck custom = savedDecks.Count == 1 ? savedDecks[0] : null;
        string customText = savedDecks.Count > 1 ? savedDecks.Count + " personal decks · Manage in native Collection"
            : custom == null ? "No personal deck · Open Deck Builder"
            : "Personal: " + custom.Name + " · " + HeroName(custom.Hero) + " · " + custom.Cards.Count + " cards";
        bool personalSelected = selectedDeck.StartsWith("custom:", StringComparison.Ordinal);
        if (Button(new Rect(65, 318, 1120, 58), (personalSelected ? "> " : "") + customText,
            !actionBusy && savedDecks.Count <= 1, button))
        {
            if (custom == null) Navigate(navigate, "DeckBuilder");
            else SelectCustom();
        }

        LocalDeck selected = personalSelected ? ResolveSelectedDeck() : LocalDecks.Get(selectedDeck);
        GUI.Label(new Rect(65, 398, 680, 126), selected == null ? "No saved personal deck."
            : selected.Name + "\n" + (personalSelected ? "Your saved local deck" : selected.Strategy), label);
        LocalDeck opponent = LocalDecks.NextOpponent(profile.RankedHistory.Select(match => match.Opponent));
        GUI.Label(new Rect(785, 398, 400, 100), "Next opponent: " + opponent.Opponent + "\n" + opponent.Name, label);
        if (Button(new Rect(785, 516, 400, 64), "Play ladder match", selected != null && !actionBusy, button)) start();
        GUI.Label(new Rect(65, 604, 1120, 42), "Local 79-card Basic/Classic test pool · no real money · no online collection",
            new GUIStyle(label) { fontSize = 16 });
    }

    private void DrawShop(Action<string> navigate, GUIStyle label, GUIStyle title, GUIStyle button)
    {
        GUI.Label(new Rect(65, 150, 700, 38), "Local Shop", title);
        GUI.Label(new Rect(65, 204, 690, 330), "Supported Classic pack\n\n100 local gold\n5 cards\n\n"
            + "First slot base rolls\nRare 75% / Epic 20% / Legendary 5%\n\n"
            + "Other slot base rolls\nCommon 75% / Rare 20% / Epic 4% / Legendary 1%", label);
        bool canBuy = !actionBusy && profile.Gold >= LocalEconomy.PackPrice && !profile.PackPoolComplete;
        if (Button(new Rect(795, 204, 390, 64), "Buy pack · 100 gold", canBuy, button))
            Perform(profile.BuyPack, "Pack bought.");
        if (Button(new Rect(795, 282, 390, 52), "Open packs", !actionBusy, button)) Navigate(navigate, "Packs");
        GUI.Label(new Rect(795, 350, 390, 96), profile.PackPoolComplete ? "Supported collection complete"
            : profile.Gold < LocalEconomy.PackPrice ? "Need 100 gold" : "Ready to buy", label);
        GUI.Label(new Rect(65, 554, 1120, 86), "Missing copies preferred up to Legendary 1 / other cards 2. "
            + "When qualifying missing cards are exhausted, a duplicate converts to 10 local gold. "
            + "Base rarity rolls shown before missing-copy fallback. Local pseudo-random packs only.",
            new GUIStyle(label) { fontSize = 16 });
    }

    private void DrawPacks(Action<string> navigate, GUIStyle label, GUIStyle title, GUIStyle button)
    {
        GUI.Label(new Rect(65, 150, 700, 38), "Packs available: " + profile.UnopenedPacks, title);
        if (Button(new Rect(795, 146, 390, 60), "Open one pack", !actionBusy && profile.UnopenedPacks > 0, button))
            Perform(() => { profile.OpenPack(); }, "Pack opened.");
        GUI.Label(new Rect(65, 220, 720, 32), "Last committed pack", label);
        LocalPackResult receipt = profile.LastPack;
        if (receipt == null) GUI.Label(new Rect(65, 262, 720, 50), "No pack opened yet.", label);
        else for (int i = 0; i < receipt.Cards.Count; i++)
        {
            LocalPackCard item = receipt.Cards[i];
            EngineCard card = FindCard(item.Id);
            int owned = Owned(item.Id);
            string text = CardName(card) + " · " + RarityName(card.Rarity)
                + " · owned " + owned + "/" + LocalEconomy.PlayableCap(card)
                + (item.DuplicateGold > 0 ? " · Duplicate: +" + item.DuplicateGold + " gold" : "");
            GUI.Label(new Rect(65, 262 + 58 * i, 720, 50), text, label);
        }
        GUI.Label(new Rect(815, 220, 370, 280), "Base rarity rolls\n\nFirst: Rare 75%\nEpic 20%\nLegendary 5%\n\n"
            + "Others: Common 75%\nRare 20%\nEpic 4%\nLegendary 1%\n\nMissing-copy fallback; exhausted duplicates give 10 gold.",
            new GUIStyle(label) { fontSize = 16 });
        if (Button(new Rect(815, 518, 370, 52), "Go to Shop", !actionBusy, button)) Navigate(navigate, "Shop");
    }

    private void DrawCollection(GUIStyle label, GUIStyle title, GUIStyle button)
    {
        GUI.Label(new Rect(65, 146, 240, 36), "Collection", title);
        var filterButton = new GUIStyle(button) { fontSize = 16 };
        string[] filters = new[] { "All" }.Concat(Heroes.Select(HeroName)).ToArray();
        for (int i = 0; i < filters.Length; i++)
            FilterButton(new Rect(315 + i % 6 * 145, 138 + i / 6 * 28, 140, 27), filters[i], filterButton);
        List<EngineCard> cards = CollectionCards();
        collectionPage = ClampPage(collectionPage, cards.Count, CollectionPageSize);
        int pages = Math.Max(1, (cards.Count + CollectionPageSize - 1) / CollectionPageSize);
        if (Button(new Rect(65, 198, 130, 38), "Previous", collectionPage > 0, button)) collectionPage--;
        GUI.Label(new Rect(205, 202, 200, 30), "Page " + (collectionPage + 1) + "/" + pages, label);
        if (Button(new Rect(415, 198, 130, 38), "Next", collectionPage + 1 < pages, button)) collectionPage++;
        List<EngineCard> visible = cards.Skip(collectionPage * CollectionPageSize).Take(CollectionPageSize).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            EngineCard card = visible[i];
            Rect rect = new Rect(i < 6 ? 65 : 625, 250 + 64 * (i % 6), i < 6 ? 540 : 560, 56);
            string text = CardName(card) + " · " + card.Cost + " mana · " + RarityName(card.Rarity)
                + "\nowned " + Owned(card.Id) + "/" + LocalEconomy.PlayableCap(card);
            CardLabel(rect, text, card, new GUIStyle(label) { fontSize = 16 });
        }
    }

    private void FilterButton(Rect rect, string filter, GUIStyle button)
    {
        if (Button(rect, (collectionFilter == filter ? "> " : "") + filter,
            collectionFilter != filter, button))
        {
            collectionFilter = filter;
            collectionPage = 0;
        }
    }

    private void DrawDeckBuilder(GUIStyle label, GUIStyle button)
    {
        GUI.Label(new Rect(65, 145, 80, 38), "Name", label);
        int oldFieldFontSize = GUI.skin.textField.fontSize;
        GUI.skin.textField.fontSize = 20;
        draftName = GUI.TextField(new Rect(145, 143, 400, 42), draftName ?? "");
        GUI.skin.textField.fontSize = oldFieldFontSize;
        GUI.Label(new Rect(565, 147, 450, 34), draftCards.Count + "/" + LocalDecks.DeckSize(draftCards) + " · " + (DraftValid ? "Valid" : "Needs changes"), label);
        if (Button(new Rect(1040, 142, 145, 44), "Save", DraftValid && !actionBusy, button))
            Perform(SaveCompatibilityDraft, "Saved personal deck.");

        for (int i = 0; i < Heroes.Length; i++)
        {
            string hero = Heroes[i];
            if (Button(new Rect(65 + i % 5 * 135, 188 + i / 5 * 26, 130, 25), (draftHero == hero ? "> " : "") + HeroName(hero), draftHero != hero, new GUIStyle(button) { fontSize = 14 }))
            {
                draftHero = hero;
                catalogPage = 0;
                draftPage = 0;
            }
        }
        if (Button(new Rect(760, 193, 130, 40), "New", !actionBusy, button)) NewDraft();
        if (Button(new Rect(900, 193, 130, 40), "Clear cards", draftCards.Count > 0 && !actionBusy, button))
        {
            draftCards.Clear();
            draftPage = 0;
        }
        if (Button(new Rect(1040, 193, 145, 40), "Reset", !actionBusy, button)) ResetDraft();

        for (int i = 0; i < LocalDecks.All.Count; i++)
        {
            int index = i;
            LocalDeck deck = LocalDecks.All[index];
            bool owned = Owns(deck);
            string text = owned ? "Load " + (index + 1) + " " + HeroName(deck.Hero) : "Need cards " + (index + 1);
            if (Button(new Rect(65 + 140 * index, 241, 130, 40), text, owned && !actionBusy, new GUIStyle(button) { fontSize = 14 }))
                LoadLoaner(index);
        }

        GUI.Label(new Rect(65, 286, 1120, 34), ValidationText(), new GUIStyle(label) { fontSize = 16 });
        GUI.Label(new Rect(65, 324, 650, 30), HeroName(draftHero) + " + Neutral catalog", label);
        GUI.Label(new Rect(760, 324, 425, 30), "Draft cards", label);

        List<EngineCard> catalog = CatalogCards();
        catalogPage = ClampPage(catalogPage, catalog.Count, EditorPageSize);
        int catalogPages = Math.Max(1, (catalog.Count + EditorPageSize - 1) / EditorPageSize);
        if (Button(new Rect(65, 356, 100, 34), "Previous", catalogPage > 0, new GUIStyle(button) { fontSize = 16 })) catalogPage--;
        GUI.Label(new Rect(175, 359, 170, 28), "Page " + (catalogPage + 1) + "/" + catalogPages, label);
        if (Button(new Rect(355, 356, 100, 34), "Next", catalogPage + 1 < catalogPages, new GUIStyle(button) { fontSize = 16 })) catalogPage++;

        List<DraftGroup> groups = SortedDraft();
        draftPage = ClampPage(draftPage, groups.Count, EditorPageSize);
        int draftPages = Math.Max(1, (groups.Count + EditorPageSize - 1) / EditorPageSize);
        if (Button(new Rect(760, 356, 100, 34), "Previous", draftPage > 0, new GUIStyle(button) { fontSize = 16 })) draftPage--;
        GUI.Label(new Rect(870, 359, 150, 28), "Page " + (draftPage + 1) + "/" + draftPages, label);
        if (Button(new Rect(1030, 356, 100, 34), "Next", draftPage + 1 < draftPages, new GUIStyle(button) { fontSize = 16 })) draftPage++;

        List<EngineCard> visibleCatalog = catalog.Skip(catalogPage * EditorPageSize).Take(EditorPageSize).ToList();
        for (int i = 0; i < visibleCatalog.Count; i++)
        {
            EngineCard card = visibleCatalog[i];
            int deckCount = draftCards.Count(id => id == card.Id);
            CardLabel(new Rect(65, 396 + 40 * i, 605, 38), CardName(card) + " · " + card.Cost + " · "
                + RarityName(card.Rarity) + " · owned " + Owned(card.Id) + " · deck " + deckCount, card,
                new GUIStyle(label) { fontSize = 16 });
            bool canAdd = CanAdd(card, deckCount);
            if (Button(new Rect(675, 396 + 40 * i, 40, 38), "+", canAdd, button)) draftCards.Add(card.Id);
        }

        List<DraftGroup> visibleDraft = groups.Skip(draftPage * EditorPageSize).Take(EditorPageSize).ToList();
        for (int i = 0; i < visibleDraft.Count; i++)
        {
            DraftGroup group = visibleDraft[i];
            CardLabel(new Rect(760, 396 + 40 * i, 380, 38), CardName(group.Card)
                + " · " + group.Count + " copies", group.Card, new GUIStyle(label) { fontSize = 16 });
            if (Button(new Rect(1145, 396 + 40 * i, 40, 38), "-", true, button)) draftCards.Remove(group.Card.Id);
        }
    }

    private void Perform(Action action, string success, Action after = null)
    {
        if (actionBusy) return;
        actionBusy = true;
        actionError = null;
        notice = null;
        try
        {
            action();
            after?.Invoke();
            notice = success;
        }
        catch (Exception failure) { actionError = failure.Message; }
        finally { actionBusy = false; }
    }

    private void Navigate(Action<string> navigate, string target)
    {
        notice = null;
        navigate(target);
    }

    private void NewDraft()
    {
        draftCards.Clear();
        draftName = "My " + HeroName(draftHero) + " Deck";
        catalogPage = draftPage = 0;
        actionError = notice = null;
    }

    internal void ResetDraft()
    {
        LocalCustomDeck saved = null;
        string resetError = null;
        try { if (compatibilityDraftId.HasValue || profile.CustomDecks.Count > 0) saved = BoundDraft(); }
        catch (InvalidOperationException failure) { resetError = failure.Message; }
        if (saved != null)
        {
            draftName = saved.Name;
            draftHero = saved.Hero;
            draftCards.Clear();
            draftCards.AddRange(saved.Cards);
        }
        else
        {
            int selectedIndex = LocalDecks.All.ToList().FindIndex(deck => deck.Id == selectedDeck);
            LocalDeck preferred = selectedIndex >= 0 ? LocalDecks.All[selectedIndex] : LocalDecks.All.First(deck => deck.Hero == draftHero);
            LocalDeck owned = Owns(preferred) ? preferred : LocalDecks.All.First(deck => deck.Hero == preferred.Hero && Owns(deck));
            SeedFromLoaner(LocalDecks.All.ToList().IndexOf(owned));
        }
        catalogPage = draftPage = 0;
        notice = null;
        actionError = resetError;
    }

    internal void SaveCompatibilityDraft()
    {
        IReadOnlyList<LocalCustomDeck> savedDecks = profile.CustomDecks;
        if (savedDecks.Count > 1) throw new InvalidOperationException(AmbiguousLibraryMessage());
        if (savedDecks.Count == 0)
        {
            if (compatibilityDraftId.HasValue)
                throw new InvalidOperationException("Personal deck library changed; reopen the editor.");
            profile.SaveDeck(draftName, draftHero, draftCards.ToArray());
            LocalCustomDeck created = profile.CustomDeck ?? throw new InvalidOperationException("Personal deck was not created.");
            compatibilityDraftId = created.Id;
            selectedDeck = PersonalKey(created.Id);
            return;
        }
        LocalCustomDeck saved = BoundDraft();
        profile.SaveDraft(saved.Id, draftName, draftHero, draftCards.ToArray(), saved.SortOrder);
        selectedDeck = PersonalKey(saved.Id);
    }

    private LocalCustomDeck BoundDraft()
    {
        IReadOnlyList<LocalCustomDeck> savedDecks = profile.CustomDecks;
        if (savedDecks.Count > 1) throw new InvalidOperationException(AmbiguousLibraryMessage());
        if (savedDecks.Count == 0) throw new InvalidOperationException("No personal deck is saved.");
        if (!compatibilityDraftId.HasValue || savedDecks[0].Id != compatibilityDraftId.Value)
            throw new InvalidOperationException("Personal deck library changed; reopen the editor.");
        return profile.GetDraft(compatibilityDraftId.Value)
            ?? throw new InvalidOperationException("Personal deck no longer exists.");
    }

    private static string PersonalKey(long id) => "custom:" + id;
    private static string AmbiguousLibraryMessage() =>
        "Multiple personal decks are managed in native Collection; this compatibility editor cannot choose one.";

    private void SeedFromLoaner(int index)
    {
        LocalDeck deck = LocalDecks.All[index];
        if (!Owns(deck)) throw new InvalidOperationException("The local collection does not own this loaner list.");
        draftHero = deck.Hero;
        draftName = "My " + HeroName(deck.Hero) + " Deck";
        draftCards.Clear();
        draftCards.AddRange(deck.CardIds);
        catalogPage = draftPage = 0;
        actionError = notice = null;
    }

    private string ValidationText()
    {
        List<string> errors = DraftErrors();
        return errors.Count == 0 ? "Valid · " + draftCards.Count + "/" + LocalDecks.DeckSize(draftCards) + " · owned · " + HeroName(draftHero) + " class · copy limits OK"
            : string.Join(" | ", errors);
    }

    private List<string> DraftErrors()
    {
        var errors = new List<string>();
        if (draftCards.Count < LocalDecks.DeckSize(draftCards)) errors.Add("Need " + (LocalDecks.DeckSize(draftCards) - draftCards.Count) + " more cards");
        else if (draftCards.Count > LocalDecks.DeckSize(draftCards)) errors.Add("Remove " + (draftCards.Count - LocalDecks.DeckSize(draftCards)) + " cards");
        if (!ValidDraftName()) errors.Add("Name must be 1-32 visible characters");

        EngineCard hero = SabberStoneCore.Model.Cards.FromId(draftHero);
        foreach (var group in draftCards.GroupBy(id => id).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            EngineCard card = LocalDecks.CustomCards.SingleOrDefault(candidate => candidate.Id == group.Key);
            if (card == null) continue;
            int count = group.Count();
            if (hero == null || !LocalDecks.IsAllowedInDeck(card, hero.Class))
                errors.Add("Wrong class: " + CardName(card) + " (" + card.Id + ")");
            int owned = Owned(card.Id);
            if (count > owned) errors.Add("Not owned: " + CardName(card) + " " + count + "/" + owned);
            int cap = LocalEconomy.PlayableCap(card);
            if (count > cap) errors.Add("Copy limit: " + CardName(card) + " " + count + "/" + cap);
        }
        if (hero != null)
        {
            try { LocalDecks.ValidateRunes(draftCards.Select(SabberStoneCore.Model.Cards.FromId), hero.Class); }
            catch (InvalidOperationException error) { errors.Add(error.Message); }
        }
        return errors;
    }

    private bool ValidDraftName()
    {
        try
        {
            LocalDeck sample = LocalDecks.All.First(deck => deck.Hero == draftHero);
            LocalDecks.Custom(draftName, draftHero, sample.CardIds);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private bool CanAdd(EngineCard card, int deckCount)
    {
        EngineCard hero = SabberStoneCore.Model.Cards.FromId(draftHero);
        int size = card[(SabberStoneCore.Enums.GameTag)997];
        if (size <= 0) size = LocalDecks.DeckSize(draftCards);
        return hero != null && LocalDecks.IsAllowedInDeck(card, hero.Class)
            && deckCount < Owned(card.Id) && deckCount < LocalEconomy.PlayableCap(card) && draftCards.Count < size;
    }

    private List<EngineCard> CollectionCards()
    {
        string heroId = Heroes.SingleOrDefault(id => HeroName(id) == collectionFilter);
        var hero = heroId == null ? null : SabberStoneCore.Model.Cards.FromId(heroId);
        return Sort(LocalDecks.CustomCards.Where(card => collectionFilter == "All" || (hero != null && LocalDecks.IsAllowedInDeck(card, hero.Class))));
    }

    private List<EngineCard> CatalogCards()
    {
        EngineCard hero = SabberStoneCore.Model.Cards.FromId(draftHero);
        return Sort(LocalDecks.CustomCards.Where(card => hero != null && LocalDecks.IsAllowedInDeck(card, hero.Class)));
    }

    private static List<EngineCard> Sort(IEnumerable<EngineCard> cards) => cards.OrderBy(card => card.Cost)
        .ThenBy(CardName, StringComparer.Ordinal).ThenBy(card => card.Id, StringComparer.Ordinal).ToList();

    private List<DraftGroup> SortedDraft() => Sort(draftCards.Distinct(StringComparer.Ordinal).Select(FindCard))
        .Select(card => new DraftGroup { Card = card, Count = draftCards.Count(id => id == card.Id) }).ToList();

    private bool Owns(LocalDeck deck)
    {
        // Any owned version counts (a golden-only class reward owns its Fabled companions too).
        var total = profile.OwnedCards.GroupBy(card => card.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(card => card.Count), StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> owned = LocalDecks.WithDeckCompanions(total, deck.CardIds);
        return deck.CardIds.GroupBy(id => id).All(group => owned.TryGetValue(group.Key, out int count) && count >= group.Count());
    }

    private int Owned(string id) => profile.Collection.TryGetValue(id, out int count) ? count : 0;
    // Fabled bundle companions (Timethief Rafaam's nine past Rafaams) are deck cards outside CustomCards; resolve them
    // from the engine so a saved Fabled deck never throws here (WriteStatus reads DraftCardIds every 3 s).
    private static EngineCard FindCard(string id) =>
        LocalDecks.CustomCards.FirstOrDefault(card => card.Id == id) ?? SabberStoneCore.Model.Cards.FromId(id);
    private static string CardName(EngineCard card) => string.IsNullOrWhiteSpace(card.Name) ? card.Id : card.Name;
    private static string RarityName(Rarity rarity) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(rarity.ToString().ToLowerInvariant());
    private static string HeroName(string hero) => LocalCoreProgression.ClassName(hero);
    private static int ClampPage(int page, int count, int size) => Math.Max(0, Math.Min(page, Math.Max(0, (count - 1) / size)));
    private string StarText() => profile.Stars >= 150 ? "Legend" : profile.Stars % 3 + "/3 stars";
    private static string Tooltip(EngineCard card) => CardName(card) + "\n" + RulesText(card);
    internal static string RulesText(EngineCard card)
    {
        string cleaned = Regex.Replace(card?.Text ?? "", @"<[^>]+>|\[x\]|@\w+|[$#]", "");
        return Regex.Replace(cleaned.Replace('_', ' '), @"\s+", " ").Trim();
    }

    private void CardLabel(Rect rect, string text, EngineCard card, GUIStyle style)
    {
        GUI.Label(rect, text, style);
        Event current = Event.current;
        if (current == null || UnityEngine.Screen.width <= 0 || UnityEngine.Screen.height <= 0) return;
        var pointer = new Vector2(current.mousePosition.x * 1280f / UnityEngine.Screen.width,
            current.mousePosition.y * 720f / UnityEngine.Screen.height);
        if (rect.Contains(pointer)) hoveredTooltip = Tooltip(card);
    }

    private static bool Button(Rect rect, string text, bool enabled, GUIStyle style)
    {
        bool old = GUI.enabled;
        GUI.enabled = enabled;
        bool clicked = GUI.Button(rect, text, style);
        GUI.enabled = old;
        return clicked;
    }

    private void DrawTooltip(GUIStyle label)
    {
        if (string.IsNullOrEmpty(hoveredTooltip)) return;
        Panel(new Rect(40, 638, 1200, 62));
        GUI.Label(new Rect(65, 641, 1120, 55), hoveredTooltip, new GUIStyle(label) { fontSize = 15 });
    }

    private static void Panel(Rect rectangle)
    {
        Color old = GUI.color;
        GUI.color = new Color(0.035f, 0.045f, 0.065f, 0.98f);
        GUI.DrawTexture(rectangle, Texture2D.whiteTexture);
        GUI.color = old;
    }

    private sealed class DraftGroup
    {
        internal EngineCard Card;
        internal int Count;
    }
}
