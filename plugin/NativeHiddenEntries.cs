using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Release policy: remove one row to restore its objects and handlers together.
internal static class NativeHiddenEntries
{
    internal sealed class Entry
    {
        internal readonly string Id, Targets, Reason, Backlog, Handlers, Buttons;
        internal readonly bool OutsideMatch, HideSiblingLabel, DisabledMode;
        internal Entry(string id, string targets, string reason, string backlog, string handlers, bool outsideMatch = false, string buttons = "", bool hideSiblingLabel = false, bool disabledMode = false)
        { Id = id; Targets = targets; Reason = reason; Backlog = backlog; Handlers = handlers; OutsideMatch = outsideMatch; Buttons = buttons; HideSiblingLabel = hideSiblingLabel; DisabledMode = disabledMode; }
    }

    internal static readonly Entry[] Entries = {
        new Entry("home.arena", "Box.m_ArenaButton", "No local Arena", "#6", "Box.OnArenaButtonPressed|Hub.HandleAreanaButtonPressed", buttons: "ARENA", disabledMode: true),
        new Entry("home.battlegrounds", "Box.m_BattleGroundsButton", "Online mode; face relabelled Fan-made modes (community mods, modder toolkit), later", "Fan-made modes (community mods, modder toolkit), later; Battlegrounds #50 unscheduled", "Box.OnBattleGroundsButtonPressed|Box.PlayBattlegroundsMode|Box.ShowBattlegroundsPreviewVideo|Box.StartBattlegroundsTutorial|Hub.HandleBaconButtonPressed", buttons: "BACON|BACON_PREVIEW", disabledMode: true),
        new Entry("home.modes", "Box.m_GameModesButton", "Twist, Duels, Adventures, Mercenaries and Books unsupported", "#50; #8 separate roguelike", "Box.OnModesButtonPressed|Hub.HandleGameModesButtonPressed", buttons: "GAME_MODES|PVP_DUNGEON_RUN|MERCENARIES", disabledMode: true),
        new Entry("home.brawl", "Box.m_prtbButtonWidget|Box.m_ribbonButtons.m_prtbRibbon|Box.m_ribbonButtons.m_prtbButtonWidget|PRTBButton.$self", "No local Tavern Brawl", "#21", "Box.OnPRTBButtonPressed|Box.OnTavernBrawlButtonPressed|PRTBButton.GoToTavernBrawl", buttons: "TAVERN_BRAWL"),
        new Entry("home.journal", "Box.m_QuestLogButton|Box.m_journalButtonWidget|Hearthstone.Progression.JournalButton.$self", "Retail quests, achievements and reward track", "#2; #3", "Hearthstone.Progression.JournalButton.ShowJournal", buttons: "JOURNAL|QUEST_LOG", hideSiblingLabel: true),
        new Entry("home.store", "Box.m_StoreButton|StoreButton.$self", "Retail money store; local shop is separate", "#4 local shop only; #50 retail stays hidden", "Box.OnStoreButtonReleased|StoreManager.StartGeneralTransaction", buttons: "STORE", hideSiblingLabel: true),
        new Entry("home.tutorial", "Box.m_tutorialPreview|Box.m_newPlayerModeBanner", "Native tutorial gets stuck", "#48", "Box.StartTraditionalTutorial|Box.ShowTraditionalPreviewVideo", buttons: "TRADITIONAL_PREVIEW"),
        new Entry("home.product-banner", "BoxProductBanner.$self|BoxProductBanner.m_boxProductBannerWidgetInstance", "Retail product promotion", "#27 local news; #50 retail stays hidden", "BoxProductBanner.OnWidgetEvent|BoxProductBanner.UnfurlBanner|BoxProductBanner.UnfurlPayload"),
        new Entry("home.lucky-draw", "Box.m_luckyDrawButtonController|LuckyDrawButtonController.$self|LuckyDrawButton.$self", "Retail paid draw", "#24 local cosmetics; #50 retail stays hidden", "Box.OnLuckyDrawButtonPressed|LuckyDrawButton.ShowLuckyDraw", buttons: "LUCKY_DRAW|BLACK_MARKET", hideSiblingLabel: true),
        new Entry("home.spj", "Box.m_spjEntryButton|SPJEntryButton.$self", "Retail seasonal progression", "#2; #26", "SPJEntryButton.ShowSpj", buttons: "SPJ"),
        new Entry("home.set-rotation", "Box.m_setRotationButton|Box.m_setRotationDisk", "Retail set rotation introduction", "#26", "Box.OnSetRotationButtonPressed", buttons: "SET_ROTATION"),
        new Entry("picker.tutorial", "DeckPickerTrayDisplay.m_replayableTutorial|ReplayableTutorialPopup.$self", "Replay resets native progress then launches unsupported tutorial", "#48", "ReplayableTutorialPopup.HandleEvent|GameUtils.ReplayTraditionalTutorial"),
        new Entry("bar.friends", "BnetBar.m_friendButton|BnetBar.FriendButtonMesh", "Battle.net social and challenges", "#25 local guilds; #50 online stays hidden", "BnetBar.OnFriendButtonReleased"),
        new Entry("bar.skip-tutorial", "BnetBar.SkipTutorialButton|BnetBar.m_tutorialSkipButton|BnetBar.m_tutorialSkipPopup", "No local tutorial progress", "#48", "BnetBar.OnSkipTutorialButtonClicked"),
        new Entry("bar.bg-guide", "BnetBar.m_battlegroundsGuide", "Unsupported mode guide", "#50; no feature scheduled", "BattlegroundsGuideButton.OnTrayEvent|BattlegroundsGuideButton.StartGuideTutorial"),
        new Entry("bar.paid-currencies", "BnetBar.m_currencyFrames|CurrencyFrame.$self", "Keep gold and dust; hide every other currency and recharge", "#4; #24 local economy only", "CurrencyFrame.OnAttemptRecharge|CurrencyFrame.AllowRecharge"),
        new Entry("menu.account", "GameMenu.m_switchAccountButton", "Battle.net account or logout", "#43 local saves only", "GameMenu.LogoutButtonPressed"),
        new Entry("menu.login", "GameMenu.m_loginButton", "Online account merge", "#50; online stays hidden", "GameMenu.LoginButtonPressed|GameMenu.ShowLoginTooltipIfNeeded"),
        new Entry("menu.downloads", "GameMenu.m_downloadManagerButton", "Retail content download", "#50; installer owns content", "GameMenu.DownloadManagerPressed"),
        new Entry("menu.signup", "GameMenu.m_signUpButton", "Retail account creation", "#50; online stays hidden", "GameMenu.OnSignUpPressed"),
        new Entry("menu.skip-apprentice", "GameMenu.m_skipNprButton", "Retail apprentice progression", "#48", "GameMenu.OnSkipNprButtonReleased"),
        new Entry("menu.skip-tutorial", "GameMenu.m_skipTutorial", "Retail tutorial progress", "#48", "GameMenu.OnSkipTutorialButtonReleased"),
        new Entry("menu.restart", "GameMenu.m_restartButton", "Unsupported native mission restart", "#28", "GameMenu.RestartButtonPressed"),
        new Entry("menu.spectator", "GameMenu.m_leaveButton", "No local spectator mode", "#39", "GameMenu.LeaveButtonPressed"),
        new Entry("menu.concede-outside-match", "GameMenu.m_concedeButton|GameMenu.m_endGameButton", "Concede is only meaningful in a match", "#50; kept in matches", "GameMenu.ConcedeButtonPressed", true),
        new Entry("options.account", "OptionsMenu.m_switchAccountButton", "Battle.net region/account switch", "#43 local saves only", "OptionsMenu.OnSwitchAccountButtonReleased"),
        new Entry("options.miscellaneous", "OptionsMenu.m_miscellaneousButton", "Credits scene and retail restore purchases", "#50; no feature scheduled", "OptionsMenu.OnMiscellaneousButtonReleased|OptionsMenu.LoadMiscellaneousMenu|MiscellaneousMenu.OnCreditsButtonReleased|MiscellaneousMenu.OnRestorePurchasesButtonReleased"),
        new Entry("options.privacy", "OptionsMenu.m_privacyButton", "Retail account and external privacy services", "#50; online stays hidden", "OptionsMenu.OnPrivacyButtonReleased|OptionsMenu.LoadPrivacyMenu"),
        new Entry("options.spectator", "OptionsMenu.m_spectatorOpenJoinCheckbox", "No online spectators", "#39 local replays only", "OptionsMenu.ToggleSpectatorOpenJoin"),
        new Entry("options.language-download", "OptionsMenu.m_languageGroup", "Locale switch downloads retail content and restarts", "#50; installer must bundle locales first", "OptionsMenu.OnNewLanguage|OptionsMenu.OnChangeLanguageConfirmationResponse"),
    };

    // These native methods can show controls after startup, transitions, callbacks or resizing.
    internal static readonly string[] RefreshHooks = {
        "Box.Start", "Box.UpdateState", "Box.UpdateUI", "Box.UpdateUIEvents", "Box.UpdateBoxButtons",
        "Box.UpdateNonBoxUIEvents", "Box.EnableStoreButtonGO", "Box.UpdateRibbonUI",
        "Box.UpdateUIState", "Box.InitializeTutorialPreviewController", "Box.ShowGameModesButton",
        "StoreButton.UpdateState", "StoreButton.OnEnable", "LuckyDrawButtonController.Show",
        "BnetBar.Start", "BnetBar.Update", "BnetBar.UpdateLayout",
        "GameMenu.Awake", "GameMenu.Show", "GameMenu.LayoutMenu",
        "OptionsMenu.Awake", "OptionsMenu.Show", "OptionsMenu.UpdateUI", "OptionsMenu.UpdateOtherUI",
        "DeckPickerTrayDisplay.SetReplayableTutorialActive", "ReplayableTutorialPopup.Awake",
        "BoxProductBanner.Start", "PRTBButton.Start", "PRTBButton.ShowInternal",
        "SPJEntryButton.Awake", "SPJEntryButton.ShowButton", "LuckyDrawButton.OnEnable",
        "LuckyDrawButton.SetButtonEnabledAndVisible", "Hearthstone.Progression.JournalButton.Awake",
        "Hearthstone.Progression.JournalButton.OnReady",
        "CurrencyFrame.Bind", "CurrencyFrame.Show"
    };

    private sealed class Binding
    {
        internal Entry Entry;
        internal FieldInfo[] Fields;
        internal object Read(object owner)
        {
            foreach (var field in Fields)
            {
                if (owner == null || owner is UnityEngine.Object unity && unity == null) return null;
                owner = field.GetValue(owner);
            }
            return owner;
        }
    }
    private static readonly Dictionary<Type, List<Binding>> bindings = new Dictionary<Type, List<Binding>>();
    private static readonly Dictionary<MethodBase, List<Entry>> blocked = new Dictionary<MethodBase, List<Entry>>();
    private static readonly HashSet<string> warned = new HashSet<string>();
    private static ManualLogSource log;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        foreach (var entry in Entries)
        {
            foreach (string target in entry.Targets.Split('|'))
            {
                var parts = target.Split('.');
                Type type = null;
                int member = parts.Length - 1;
                for (; member > 0; member--)
                    if ((type = GameTypes.ByName(string.Join(".", parts.Take(member)))) != null) break;
                if (type == null) { Warn(target); continue; }
                Type fieldType = type;
                var fields = new List<FieldInfo>();
                foreach (string name in parts.Skip(member).Where(name => name != "$self"))
                {
                    var field = AccessTools.Field(fieldType, name);
                    if (field == null) { Warn(target); fieldType = null; break; }
                    fields.Add(field); fieldType = field.FieldType;
                }
                if (fieldType == null) continue;
                if (!bindings.TryGetValue(type, out var list)) bindings[type] = list = new List<Binding>();
                list.Add(new Binding { Entry = entry, Fields = fields.ToArray() });
            }
            foreach (string handler in entry.Handlers.Split('|'))
            {
                MethodInfo method = ResolveMethod(handler);
                if (method == null) continue;
                if (!blocked.TryGetValue(method, out var list))
                {
                    blocked[method] = list = new List<Entry>();
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeHiddenEntries), nameof(Block)));
                }
                list.Add(entry);
            }
        }
        foreach (string hook in RefreshHooks)
        {
            MethodInfo method = ResolveMethod(hook);
            if (method != null) harmony.Patch(method, postfix: new HarmonyMethod(typeof(NativeHiddenEntries), nameof(Apply)));
        }
        foreach (string route in new[] { "Box.FireButtonPressEvent", "Hub.OnBoxButtonPressed" })
        {
            MethodInfo method = ResolveMethod(route);
            if (method != null) harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeHiddenEntries), nameof(GuardBoxRoute)));
        }
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "GetButtons"),
            postfix: new HarmonyMethod(typeof(NativeHiddenEntries), nameof(FilterMenuButtons)));
    }

    private static MethodInfo ResolveMethod(string path)
    {
        int split = path.LastIndexOf('.');
        Type type = GameTypes.ByName(path.Substring(0, split));
        MethodInfo method = type == null ? null : AccessTools.Method(type, path.Substring(split + 1));
        if (method == null) Warn(path);
        return method;
    }
    private static void Warn(string path)
    {
        if (warned.Add(path)) log?.LogWarning("NATIVE_HIDDEN missing binding: " + path);
    }
    private static bool Applies(Entry entry) => (entry.Id != "home.store" || !NativeShop.HomeStoreReady)
        && (!entry.OutsideMatch || !SceneMgr.IsInitialized() || SceneMgr.Get().GetMode() != SceneMgr.Mode.GAMEPLAY);
    // The drawer Journal slot stays visible as SoloStone's Feats entry; its retail routes and handlers stay blocked.
    private static bool Hides(Entry entry) => entry.Id != "home.journal" || !NativeAchievements.HomeJournalReady;
    // True while the entry's policy is in force (its release-policy row exists and applies here).
    internal static bool Active(string id) => Entries.Any(entry => entry.Id == id && Applies(entry) && Hides(entry));
    private static bool Block(MethodBase __originalMethod) =>
        !blocked.TryGetValue(__originalMethod, out var entries) || !entries.Any(Applies);

    private static bool GuardBoxRoute(Box.ButtonType __0, bool __1)
    {
        string button = __0 + (__1 ? "_PREVIEW" : "");
        return !Entries.Any(entry => Applies(entry) && entry.Buttons.Split('|').Contains(button));
    }

    internal static void Apply(object __instance)
    {
        if (__instance is Box box)
        {
            NativeHomeUI.HideJournalLabels(box, message => log?.LogInfo(message));
            NativeHomeUI.ApplyModePolicy(box, message => log?.LogInfo(message), Active);
        }
        else if (__instance is Hearthstone.Progression.JournalButton)
            NativeHomeUI.HideJournalLabels(Box.Get(), message => log?.LogInfo(message));
        if (__instance == null || !bindings.TryGetValue(__instance.GetType(), out var list)) return;
        foreach (var binding in list)
            if (Applies(binding.Entry) && Hides(binding.Entry))
            {
                object value = binding.Read(__instance);
                if (binding.Entry.DisabledMode) NativeHomeUI.DisableMode(__instance as Box, value as BoxMenuButton);
                else Hide(value, binding.Entry.HideSiblingLabel);
            }
    }
    private static void Hide(object value, bool hideSiblingLabel = false)
    {
        if (value is UnityEngine.Object unity && unity == null) return;
        if (value is IEnumerable values && !(value is string))
        {
            foreach (object item in values) Hide(item, hideSiblingLabel);
            return;
        }
        if (value is CurrencyFrame currency
            && (currency.CurrentCurrencyType == CurrencyType.GOLD || currency.CurrentCurrencyType == CurrencyType.DUST)) return;
        var gameObject = value as GameObject ?? (value as Component)?.gameObject;
        if (gameObject == null) return;
        if (gameObject.activeSelf) gameObject.SetActive(false);
        // Native drawer icons/widgets can own a separate sibling label.
        if (!hideSiblingLabel) return;
        var label = gameObject.transform.parent?.Find("ButtonName")?.gameObject;
        if (label != null && label.activeSelf) label.SetActive(false);
    }
    private static void FilterMenuButtons(GameMenu __instance, object __result)
    {
        if (!bindings.TryGetValue(__instance.GetType(), out var list) || !(__result is IList buttons)) return;
        var hidden = new HashSet<object>(list.Where(binding => Applies(binding.Entry)).Select(binding => binding.Read(__instance)).Where(value => value != null));
        for (int i = buttons.Count - 1; i >= 0; i--)
            if (buttons[i] != null && hidden.Contains(buttons[i])) { Hide(buttons[i]); buttons.RemoveAt(i); }
        for (int i = buttons.Count - 1; i >= 0; i--)
            if (buttons[i] == null && (i == 0 || i == buttons.Count - 1 || buttons[i - 1] == null)) buttons.RemoveAt(i);
    }
}
