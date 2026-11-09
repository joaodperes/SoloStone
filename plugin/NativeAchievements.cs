using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets;
using BepInEx.Logging;
using HarmonyLib;
using Hearthstone.DataModels;
using Hearthstone.Progression;
using Hearthstone.UI;
using MiniJSON;
using UnityEngine;

// SoloStone Feats (owner 2026-10-28): the native Journal popup opened on its Feats tab, fed from local achievements.
// Data layer: AchievementManager's lazily built category/section/achievement models (normally from the ACHIEVEMENT*
// DBF tables plus server PlayerAchievementState) are replaced by models projected from LocalAchievementFeats; a small
// set of prefixes keeps native code from rebuilding them from DBF or reading server state for our synthetic ids.
// Network: Journal events that reach the server (telemetry, mercenary collection load, season pass, apprentice skip,
// achievement claim) are swallowed while Feats is open, and other Journal tabs cannot be selected.
// Entry point: the native drawer Journal button (relabelled "Feats", NativeHomeUI) opens Feats instead of the retail Journal.
internal static class NativeAchievements
{
    internal const string HomeId = "home.feats", ButtonLabel = "Feats";
    internal const string TooltipBody = "Your achievements, points and rewards.";
    // JournalButton.popupAssetReference of the pinned client.
    internal const string JournalPrefab = "JournalPopup.prefab:b61d6e9bd58789647a62494cf92fb93e";
    private const int FeatsTab = (int)JournalTrayDisplay.JournalTab.Achievment;
    private static readonly FieldInfo Categories = Field("m_categories"), CategoryMap = Field("m_categoryMap"),
        SectionMap = Field("m_sectionMap"), SubcategoryMap = Field("m_subcategoryMap"),
        AchievementToSubcategory = Field("m_achievementToSubcategoryMap"), SubcategoryToCategory = Field("m_subcategoryToCategoryMap"),
        AchievementModels = Field("m_achievementDataModelMap"), Completed = Field("m_completedAchievements"),
        SelectedCategory = Field("m_selectedCategory");
    private static readonly FieldInfo InspectedAchievement = AccessTools.Field(typeof(AchievementInspectionPopup), "m_achievementDataModel");
    private static readonly FieldInfo HeroPreviewEntity = AccessTools.Field(typeof(BaseHeroSkinInfoManager), "m_currentEntityDef");
    private static readonly FieldInfo CardBackPreviewId = AccessTools.Field(typeof(CardBackInfoManager), "m_currentCardBackIdx");
    private static readonly FieldInfo HeroVanillaDescription = AccessTools.Field(typeof(BaseHeroSkinInfoManager), "m_vanillaHeroDescription");
    private static readonly FieldInfo HeroNewDescription = AccessTools.Field(typeof(BaseHeroSkinInfoManager), "m_newHeroDescription");
    private static readonly FieldInfo CardBackDescription = AccessTools.Field(typeof(CardBackInfoManager), "m_description");
    private static readonly PropertyInfo DescriptionText = AccessTools.Property(HeroVanillaDescription?.FieldType, "Text");
#if !SOLOSTONE_RELEASE
    private static readonly MethodInfo ScrollCenter = AccessTools.Method(typeof(UIBScrollable), "CenterObjectInView");
    private static readonly FieldInfo ScrollEase = AccessTools.Field(typeof(UIBScrollable), "m_ScrollEaseType");
    private static readonly FieldInfo RevealDialog = AccessTools.Field(typeof(DialogManager), "m_currentDialog");
#endif
    private static readonly FieldInfo SubcategorySections = AccessTools.Field(typeof(AchievementSubcategory), "m_sections");
    private static readonly HashSet<string> loggedEvents = new HashSet<string>();
    private static ManualLogSource log;
    private static LocalAchievementFeats feats;
    private static LocalProfile ownerProfile;
    private static WidgetInstance widget;
    private static JournalPopup popup;
    private static bool active, shown, disabled, diagnosed;
    // A Journal that never becomes ready would leave Feats "active" and block HUB input, profile switch and toasts.
    // Only focused, unpaused frames count (Deck suspend, alt-tab), each capped so a resume frame cannot jump the clock.
    internal const float ReadyTimeoutSeconds = 10f, MaxCountedFrameSeconds = 0.25f;
    private static float readyWait;
    private static int generation;

    internal static bool Active => active;
    // The drawer Journal slot shows as Feats unless a native failure retired it for the session.
    internal static bool HomeJournalReady => !disabled;
    internal static LocalAchievementFeats Snapshot => feats;

    private static FieldInfo Field(string name) => AccessTools.Field(typeof(AchievementManager), name);

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        Patch(harmony, typeof(AchievementManager), "get_TotalPointsExcludeRetired", prefix: nameof(TotalPoints));
        Patch(harmony, typeof(AchievementManager), "LoadReward", prefix: nameof(SkipLocalReward));
        Patch(harmony, typeof(AchievementManager), "GetClaimedDate", prefix: nameof(LocalClaimedDate));
        Patch(harmony, typeof(AchievementManager), "GetAchievementDataModel", prefix: nameof(LocalAchievementModel));
        Patch(harmony, typeof(AchievementManager), "ClaimAchievementReward", prefix: nameof(RefuseLocalClaim));
        Patch(harmony, typeof(Section), "LoadAchievements", prefix: nameof(KeepLocalSection));
        Patch(harmony, typeof(JournalPopup), "HandleEvent", prefix: nameof(PopupEvent));
        Patch(harmony, typeof(JournalPopup), "OnDestroy", postfix: nameof(PopupDestroyed));
        Patch(harmony, typeof(JournalTrayDisplay), "HandleTabSelected", prefix: nameof(TabSelected));
        Patch(harmony, typeof(JournalButton), "HandleEvent", prefix: nameof(JournalButtonEvent));
        Patch(harmony, typeof(JournalButton), "ShowJournal", prefix: nameof(JournalButtonShow));
        Patch(harmony, typeof(AchievementInspectionPopup), "UpdateAchievement", postfix: nameof(InspectionDetail));
        Patch(harmony, typeof(AchievementSubcategory), "SetAchievementDisplayDelay", postfix: nameof(CapDisplayDelay));
        harmony.Patch(AccessTools.Method(typeof(BaseHeroSkinInfoManager), "EnterPreview", new[] { typeof(CollectionCardVisual) })
                ?? throw new MissingMethodException("BaseHeroSkinInfoManager", "EnterPreview"),
            postfix: new HarmonyMethod(typeof(NativeAchievements), nameof(HeroAcquisitionCopy)));
        harmony.Patch(AccessTools.Method(typeof(CardBackInfoManager), "EnterPreview", new[] { typeof(int), typeof(CollectionCardVisual) })
                ?? throw new MissingMethodException("CardBackInfoManager", "EnterPreview"),
            postfix: new HarmonyMethod(typeof(NativeAchievements), nameof(CardBackAcquisitionCopy)));
        foreach (var field in new[] { Categories, CategoryMap, SectionMap, SubcategoryMap, AchievementToSubcategory,
            SubcategoryToCategory, AchievementModels, Completed, SelectedCategory, InspectedAchievement })
            if (field == null) throw new MissingFieldException("Feats native binding missing");
    }

    private static void Patch(Harmony harmony, Type type, string method, string prefix = null, string postfix = null)
    {
        MethodInfo target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
        harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(NativeAchievements), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(NativeAchievements), postfix));
    }

    // Native staggers each row's entrance by AchievementCell.ACHIEVEMENT_SHOW_DELAY and blocks every tab click until the
    // last row is in (AchievementManager.IsSubcategoryTransitionInProgress). Retail tabs hold a few dozen rows; local Card
    // Mastery tabs hold up to 416, so tab clicks were dropped for 15+ s (live Deck 2026-10-30). Cap the whole entrance.
    internal const float MaxDisplayDelay = 1f;
    private static void CapDisplayDelay(AchievementSubcategory __instance, ref float __result)
    {
        if (!Owns(__instance) || __result <= MaxDisplayDelay) return;
        try
        {
            var sections = SubcategorySections?.GetValue(__instance) as AchievementSectionListDataModel;
            if (sections?.Sections == null) return;
            foreach (var section in sections.Sections)
            {
                section.DisplayDelay = Math.Min(section.DisplayDelay, MaxDisplayDelay);
                if (section.DisplayedAchievements == null) continue;
                foreach (var row in section.DisplayedAchievements) row.DisplayDelay = Math.Min(row.DisplayDelay, MaxDisplayDelay);
            }
            __result = MaxDisplayDelay;
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS display delay cap: " + error.Message); }
    }

#if !SOLOSTONE_RELEASE
    // Test-control busy gate: native drops subcategory clicks during this window, so a press must wait, not settle.
    internal static bool SubcategoryTransition
    {
        get { try { return shown && AchievementManager.Get()?.IsSubcategoryTransitionInProgress == true; } catch { return false; } }
    }
#endif

    // ---- Data layer: native AchievementManager reads our models for synthetic ids. ----

    private static bool TotalPoints(ref int __result)
    {
        if (feats == null) return true;
        __result = feats.Stats.Points;
        return false;
    }

    private static bool SkipLocalReward(AchievementDataModel achievement) =>
        achievement == null || !LocalAchievementFeats.IsNativeAchievementId(achievement.ID);

    private static bool LocalClaimedDate(int achievementId, ref long __result)
    {
        if (!LocalAchievementFeats.IsNativeAchievementId(achievementId)) return true;
        __result = feats?.Find(achievementId)?.UnlockedUnixSeconds ?? 0;
        return false;
    }

    private static bool LocalAchievementModel(int achievementId, ref AchievementDataModel __result)
    {
        if (!LocalAchievementFeats.IsNativeAchievementId(achievementId)) return true;
        __result = models.TryGetValue(achievementId, out AchievementDataModel model) ? model : null;
        return false;
    }

    // Local feats are granted when earned; nothing is ever claimed through the server.
    private static bool RefuseLocalClaim(int achievementId, ref bool __result)
    {
        if (!LocalAchievementFeats.IsNativeAchievementId(achievementId) && !active) return true;
        __result = false;
        return false;
    }

    private static bool KeepLocalSection(AchievementSectionDataModel source) =>
        source == null || !LocalAchievementFeats.IsNativeSectionId(source.ID);

    private static readonly Dictionary<int, AchievementDataModel> models = new Dictionary<int, AchievementDataModel>();

    private static void Install(AchievementManager manager, LocalAchievementFeats snapshot)
    {
        var icons = RetailIcons();
        var list = new AchievementCategoryListDataModel { Categories = new DataModelList<AchievementCategoryDataModel>(), Stats = Stats(snapshot.Stats) };
        var categoryMap = new Dictionary<int, AchievementCategoryDataModel>();
        var sectionMap = new Dictionary<int, AchievementSectionDataModel>();
        var subcategoryMap = new Dictionary<int, AchievementSubcategoryDataModel>();
        var achievementToSub = new Dictionary<int, int>();
        var subToCategory = new Dictionary<int, int>();
        var built = new Dictionary<int, AchievementDataModel>();
        foreach (LocalFeatCategory category in snapshot.Categories)
        {
            icons.TryGetValue(category.RetailIcon, out RetailIcon icon);
            var model = new AchievementCategoryDataModel {
                // Live: the medallion art follows the retail category id, not Icon; reuse the borrowed category's id.
                Name = category.Name, Icon = icon?.Icon ?? "", ID = icon?.Id ?? category.NativeId, Stats = Stats(category.Stats),
                Subcategories = new AchievementSubcategoryListDataModel { Subcategories = new DataModelList<AchievementSubcategoryDataModel>() }
            };
            for (int index = 0; index < category.Subcategories.Count; index++)
            {
                LocalFeatSubcategory sub = category.Subcategories[index];
                var achievements = new DataModelList<AchievementDataModel>();
                foreach (LocalFeat feat in sub.Feats)
                {
                    var achievement = Achievement(feat);
                    achievements.Add(achievement);
                    built[feat.NativeId] = achievement; achievementToSub[feat.NativeId] = sub.NativeId;
                }
                achievements.UpdateTiers();
                var section = new AchievementSectionDataModel { Name = sub.SectionName ?? sub.Name, ID = sub.SectionNativeId,
                    Achievements = new AchievementListDataModel { Achievements = achievements } };
                var subModel = new AchievementSubcategoryDataModel {
                    Name = sub.Name, ID = sub.NativeId, Stats = Stats(sub.Stats),
                    FullName = GameStrings.Format("GLOBAL_PROGRESSION_ACHIEVEMENT_SUBCATEGORY_NAME", category.Name, sub.Name),
                    Icon = !string.IsNullOrEmpty(sub.RetailIcon) ? sub.RetailIcon
                        : icon == null || icon.Subcategories.Count == 0 ? model.Icon : icon.Subcategories[index % icon.Subcategories.Count],
                    Sections = new AchievementSectionListDataModel { Sections = new DataModelList<AchievementSectionDataModel> { section } }
                };
                model.Subcategories.Subcategories.Add(subModel);
                sectionMap[section.ID] = section; subcategoryMap[subModel.ID] = subModel; subToCategory[subModel.ID] = model.ID;
            }
            list.Categories.Add(model);
            categoryMap[model.ID] = model;
        }
        Categories.SetValue(manager, new Lazy<AchievementCategoryListDataModel>(() => list));
        CategoryMap.SetValue(manager, new Lazy<Dictionary<int, AchievementCategoryDataModel>>(() => categoryMap));
        SectionMap.SetValue(manager, new Lazy<Dictionary<int, AchievementSectionDataModel>>(() => sectionMap));
        SubcategoryMap.SetValue(manager, new Lazy<Dictionary<int, AchievementSubcategoryDataModel>>(() => subcategoryMap));
        AchievementToSubcategory.SetValue(manager, new Lazy<Dictionary<int, int>>(() => achievementToSub));
        SubcategoryToCategory.SetValue(manager, new Lazy<Dictionary<int, int>>(() => subToCategory));
        AchievementModels.SetValue(manager, new Lazy<Dictionary<int, AchievementDataModel>>(() => built));
        SelectedCategory.SetValue(manager, null);
        var completed = Completed.GetValue(manager) as DataModelList<AchievementDataModel>
            ?? throw new InvalidOperationException("native recently-completed list unavailable");
        completed.Clear();
        foreach (LocalFeat feat in snapshot.RecentlyCompleted) completed.Add(built[feat.NativeId]);
        models.Clear();
        foreach (var pair in built) models[pair.Key] = pair.Value;
        feats = snapshot;
    }

    private static AchievementStatsDataModel Stats(LocalFeatStats stats)
    {
        var model = new AchievementStatsDataModel { Points = stats.Points, AvailablePoints = stats.AvailablePoints,
            Unclaimed = 0, CompletedAchievements = stats.Completed, TotalAchievements = stats.Total };
        if (stats.AvailablePoints > 0) model.UpdateCompletionPercentage();
        else model.CompletionPercentage = "0%";
        return model;
    }

    private static AchievementDataModel Achievement(LocalFeat feat)
    {
        RewardListDataModel rewards = Rewards(feat);
        var model = new AchievementDataModel {
            Name = feat.Name, Description = feat.Summary, Progress = feat.Quota == 0 && feat.Earned ? 1 : feat.Progress,
            Quota = Math.Max(1, feat.Quota), Points = feat.Points,
            // REWARD_ACKED: earned and already granted locally, so no native Claim button; ACTIVE otherwise.
            Status = feat.Earned ? AchievementManager.AchievementStatus.REWARD_ACKED : AchievementManager.AchievementStatus.ACTIVE,
            CompletionDate = feat.Earned && feat.UnlockedUnixSeconds > 0 ? ProgressUtils.FormatAchievementCompletionDate(feat.UnlockedUnixSeconds) : "",
            ID = feat.NativeId, NextTierID = 0, SortOrder = feat.SortOrder, AllowExceedQuota = false, Retired = false,
            RewardList = rewards, RewardSummary = ProgressUtils.FormatRewardsSummary(rewards, 0, feat.Points, false)
        };
        model.UpdateProgress();
        return model;
    }

    private static RewardListDataModel Rewards(LocalFeat feat)
    {
        var list = new RewardListDataModel { Items = new DataModelList<RewardItemDataModel>(), Description = feat.RewardText };
        foreach (var card in feat.Cards.Where(card => card.DbfId > 0 && card.Copies > 0))
            AddReward(list, new RewardItemDataModel { ItemType = RewardItemType.CARD, ItemId = card.DbfId, Quantity = card.Copies }, feat,
                (TAG_PREMIUM)card.Premium);
        LocalAchievementReward reward = feat.Reward;
        if (reward.Gold > 0) AddReward(list, new RewardItemDataModel { ItemType = RewardItemType.GOLD, Quantity = reward.Gold }, feat);
        if (reward.Dust > 0) AddReward(list, new RewardItemDataModel { ItemType = RewardItemType.DUST, Quantity = reward.Dust }, feat);
        if (reward.StandardPacks > 0)
            AddReward(list, new RewardItemDataModel { ItemType = RewardItemType.BOOSTER, Quantity = reward.StandardPacks,
                ItemId = FairPackRules.NativeBoosterId(FairPackRules.Standard) }, feat);
        if (reward.CardBackId > 0) AddReward(list, new RewardItemDataModel { ItemType = RewardItemType.CARD_BACK, ItemId = reward.CardBackId }, feat);
        if (reward.HeroSkinId != null)
            AddReward(list, new RewardItemDataModel { ItemType = RewardItemType.HERO_SKIN, Quantity = 1,
                ItemId = GameUtils.TranslateCardIdToDbId(reward.HeroSkinId) }, feat, (TAG_PREMIUM)feat.RewardPremium);
        return list;
    }

    private static void AddReward(RewardListDataModel list, RewardItemDataModel item, LocalFeat feat, TAG_PREMIUM premium = TAG_PREMIUM.NORMAL)
    {
        try
        {
            if (RewardUtils.InitializeRewardItemDataModel(item, TAG_RARITY.INVALID, premium, out string reason)) list.Items.Add(item);
            else log?.LogWarning("NATIVE_FEATS reward preview skipped " + feat.Id + ": " + reason);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS reward preview failed " + feat.Id + ": " + error.Message); }
    }

    private sealed class RetailIcon
    {
        internal int? Id;
        internal string Icon;
        internal List<string> Subcategories = new List<string>();
    }

    // Borrow the retail medallion/subcategory art by the non-localized ACHIEVEMENT_CATEGORY Icon code (general, gameplay, ...).
    private static Dictionary<string, RetailIcon> RetailIcons()
    {
        var icons = new Dictionary<string, RetailIcon>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (AchievementCategoryDbfRecord record in GameDbf.AchievementCategory?.GetRecords() ?? new List<AchievementCategoryDbfRecord>())
            {
                string name = record?.Icon;
                if (string.IsNullOrEmpty(name) || icons.ContainsKey(name)) continue;
                icons[name] = new RetailIcon { Id = record.ID, Icon = record.Icon,
                    Subcategories = (record.Subcategories ?? new List<AchievementSubcategoryDbfRecord>()).Where(sub => sub != null)
                        .OrderBy(sub => sub.SortOrder).Select(sub => sub.Icon).Where(icon => !string.IsNullOrEmpty(icon)).ToList() };
            }
            if (!diagnosed)
                log?.LogInfo("NATIVE_FEATS retail categories " + string.Join("; ", icons.Select(pair => pair.Key + "=" + pair.Value.Icon
                    + "[" + string.Join(",", pair.Value.Subcategories) + "]")));
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS retail icons unavailable: " + error.Message); }
        return icons;
    }

    // ---- Popup lifecycle ----

    internal static bool Open()
    {
        LocalProfile profile = NativeUIProbe.RewardProfile;
        if (!CanOpen(profile)) return false;
        try
        {
            var manager = AchievementManager.Get() ?? throw new InvalidOperationException("AchievementManager unavailable");
            var snapshot = LocalAchievementFeats.Create(profile.Achievements, profile.FeatsSnapshot());
            Install(manager, snapshot);
            JournalTrayDisplay.SetActiveTabForTrackType(Global.RewardTrackType.GLOBAL, JournalTrayDisplay.JournalTab.Achievment);
            var meta = new JournalMetaDataModel { TabIndex = FeatsTab, DoneChangingTabs = false, HidingAllBadges = true,
                HasSeenWelcomeApprentice = true, HasPassedTavernGuideButtonIntro = true };
            var created = WidgetInstance.Create(JournalPrefab) ?? throw new InvalidOperationException("Journal widget missing");
            widget = created; popup = null; ownerProfile = profile; active = true; shown = false;
            readyWait = 0f;
            int token = ++generation;
            created.BindDataModel(meta);
            created.RegisterReadyListener(_ => Ready(token, created));
            log?.LogInfo("NATIVE_FEATS open points=" + snapshot.Stats.Points + "/" + snapshot.Stats.AvailablePoints
                + ";completed=" + snapshot.Stats.Completed + "/" + snapshot.Stats.Total);
            return true;
        }
        catch (Exception error) { Fail(error); return false; }
    }

    private static bool CanOpen(LocalProfile profile) => !active && !disabled && profile != null
        && ReferenceEquals(profile, NativeUIProbe.RewardProfile) && !JournalPopup.s_isShowing
        && !NativeAchievementToast.Active && !NativeRewardUI.BlocksHome && !NativeProfileSelector.BlocksHome
        && Box.Get() != null && Box.Get().IsInStateWithButtons();

    private static void Ready(int token, WidgetInstance expected)
    {
        if (!active || token != generation || !ReferenceEquals(expected, widget)) return;
        try
        {
            popup = expected.GetComponentInChildren<JournalPopup>(true) ?? throw new InvalidOperationException("JournalPopup component missing");
            popup.Show();
            shown = true;
            Diagnose(expected);
        }
        catch (Exception error) { Fail(error); }
    }

    internal static void Update()
    {
        EnableDrawerButton();
        if (!active) return;
        try
        {
            if (widget == null) { Retire("widget-destroyed"); return; }
            if (!shown && Application.isFocused && Time.timeScale > 0f)
                readyWait += Math.Min(Time.unscaledDeltaTime, MaxCountedFrameSeconds);
            if (!shown && readyWait > ReadyTimeoutSeconds) { TimeOut(); return; }
            if (shown) HideUnsupportedTabs();
            if (!ReferenceEquals(ownerProfile, NativeUIProbe.RewardProfile)) Close("profile-changed");
        }
        catch (Exception error) { Fail(error); }
    }

    // BnetBar's Escape (NativeRewardUI.BeforeRewardEscape): true when it closed an open Feats window.
    internal static bool CloseOnEscape()
    {
        if (!active) return false;
        try { Close("escape"); }
        catch (Exception error) { Fail(error); }
        return true;
    }

    internal static void Close(string reason = "close")
    {
        log?.LogInfo("NATIVE_FEATS close request reason=" + reason);
        if (popup != null) popup.Close();
        else if (widget != null) UnityEngine.Object.Destroy(widget.gameObject);
        Retire(reason);
    }

    // Live: the left tab column is JournalPopup/Root/ButtonManager/{QuestButton, RewardButton, AchievementButton, ProfileButton}.
    // Only Feats is supported offline; the others are hidden (and TabSelected refuses them anyway).
    private static void HideUnsupportedTabs()
    {
        Transform tabs = popup == null ? null : FindChild(popup.transform, "ButtonManager");
        if (tabs == null) return;
        foreach (Transform tab in tabs)
            if (tab.gameObject.activeSelf && tab.name.IndexOf("Achievement", StringComparison.OrdinalIgnoreCase) < 0)
            {
                tab.gameObject.SetActive(false);
                if (loggedEvents.Add("hide:" + tab.name)) log?.LogInfo("NATIVE_FEATS hid journal tab " + tab.name);
            }
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (Transform child in root)
        {
            if (child.name == name) return child;
            Transform found = FindChild(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private static void PopupDestroyed(JournalPopup __instance)
    {
        if (active && (ReferenceEquals(__instance, popup) || Owns(__instance))) Retire("popup-destroyed");
    }

    private static void Retire(string reason)
    {
        if (active) log?.LogInfo("NATIVE_FEATS closed reason=" + reason);
        active = shown = false; generation++; widget = null; popup = null; ownerProfile = null;
        // Drop the snapshot: the points override and synthetic-id lookups fall back to retail until the next Open.
        feats = null; models.Clear();
        loggedEvents.Clear();
    }

    // A slow or lost Journal load is not a broken Feats: close and release everything; the next Open simply retries.
    private static void TimeOut()
    {
        log?.LogWarning("NATIVE_FEATS Journal not ready after " + ReadyTimeoutSeconds + " s of focused time; closed, next open retries");
        try { if (widget != null) UnityEngine.Object.Destroy(widget.gameObject); } catch { }
        Retire("ready-timeout");
    }

    // Fail closed: a native failure retires Feats for the session and NativeHomeUI hides the drawer slot again.
    private static void Fail(Exception error)
    {
        disabled = true;
        log?.LogError("NATIVE_FEATS disabled after failure: " + error);
        try { if (widget != null) UnityEngine.Object.Destroy(widget.gameObject); } catch { }
        Retire("failure");
    }

    private static bool Owns(Component component) => active && component != null && widget != null
        && component.transform.IsChildOf(widget.transform);

    // Only Close leaves the Journal; every other popup event is retail-only and may reach the server
    // (ACHIEVEMENT_SELECTED loads Mercenaries, MARK_WELCOME_SEEN saves server data). Owns covers the window
    // before Ready: JournalPopup.Awake registers this handler while the widget is still loading.
    private static bool PopupEvent(JournalPopup __instance, string eventName)
    {
        if (!Owns(__instance) || eventName == "CODE_CLOSE") return true;
        if (loggedEvents.Add("popup:" + eventName)) log?.LogInfo("NATIVE_FEATS swallowed journal event " + eventName);
        return false;
    }

    // Feats is the only supported tab; selecting another tab does nothing.
    private static bool TabSelected(JournalTrayDisplay __instance)
    {
        if (!Owns(__instance)) return true;
        object payload = __instance.GetComponent<WidgetTemplate>()?.GetDataModel<EventDataModel>()?.Payload;
        if (!(payload is IConvertible) || Convert.ToInt32(payload) == FeatsTab) return true;
        if (loggedEvents.Add("tab:" + payload)) log?.LogInfo("NATIVE_FEATS blocked journal tab " + payload);
        return false;
    }

    // Only the HUB drawer's Journal button is Feats; other JournalButtons (Battlegrounds) keep retail handling,
    // where NativeHiddenEntries still blocks ShowJournal.
    private static bool IsDrawerJournal(JournalButton button)
    {
        var drawer = Box.Get()?.m_journalButtonWidget;
        return button != null && drawer != null && button.transform.IsChildOf(drawer.transform);
    }

    private static bool JournalButtonEvent(JournalButton __instance, string eventName)
    {
        if (!IsDrawerJournal(__instance)) return true;
        try
        {
            switch (eventName)
            {
                case "BUTTON_CLICKED": Open(); return false;
                case "SHOW_TOOLTIP": ShowTooltip(__instance); return false;
                default: return true;
            }
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS journal button: " + error.Message); return false; }
    }

    // The Journal widget's tooltip bone tilts with its icon, so the box tooltip came out skewed. Level it the way the
    // Store tooltip sits: that button's bone, else the prefab's own rotation (what an unboned zone keeps).
    // Measured on the Deck: Journal bone yaw 8.8°, Store bone about (-2°, 0, -4°).
    private static void ShowTooltip(JournalButton button)
    {
        var zone = button.GetComponent<TooltipZone>();
        var panel = zone?.ShowTooltip(ButtonLabel, TooltipBody, TooltipPanel.BOX_SCALE);
        if (panel == null) return;
        var store = Box.Get()?.m_StoreButton?.GetComponent<TooltipZone>();
        Quaternion level = store?.tooltipDisplayLocation != null ? store.tooltipDisplayLocation.rotation
            : zone.tooltipPrefab != null ? zone.tooltipPrefab.transform.rotation : panel.transform.rotation;
        panel.transform.rotation = level;
        TransformUtil.ConstrainToScreen(panel.gameObject, zone.gameObject.layer);
    }

    // The drawer's retail Journal never opens offline; any route to it opens Feats.
    private static bool JournalButtonShow(JournalButton __instance)
    {
        if (!IsDrawerJournal(__instance)) return true;
        Open();
        return false;
    }

    private static void InspectionDetail(AchievementInspectionPopup __instance)
    {
        try
        {
            if (!(InspectedAchievement.GetValue(__instance) is AchievementDataModel model) || feats == null) return;
            LocalFeat feat = feats.Find(model.ID);
            if (feat != null) model.Description = feats.Detail(feat);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS inspection detail: " + error.Message); }
    }

    // One concise hierarchy note per open, to pin tab/close controls during live review.
    private static void Diagnose(WidgetInstance root)
    {
        if (diagnosed) return;
        diagnosed = true;
        try
        {
            var rows = root.GetComponentsInChildren<PegUIElement>(true).Take(80)
                .Select(element => PathOf(element.transform, root.transform) + (element.gameObject.activeInHierarchy ? "" : "(off)"));
            log?.LogInfo("NATIVE_FEATS controls " + string.Join(" | ", rows));
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS diagnose: " + error.Message); }
    }

    private static float nextDrawerCheck;

    // Offline the retail Journal widget never receives its data, and its interaction state stays disabled
    // (Clickable.Active=false). While the drawer shows Feats, keep its own Clickable active.
    private static void EnableDrawerButton()
    {
        if (disabled || Time.realtimeSinceStartup < nextDrawerCheck) return;
        nextDrawerCheck = Time.realtimeSinceStartup + 0.5f;
        try
        {
            Box box = Box.Get();
            var widget = box?.m_journalButtonWidget;
            if (widget == null || box.m_StoreButton == null || !box.m_StoreButton.gameObject.activeInHierarchy) return;
            foreach (Clickable clickable in widget.GetComponentsInChildren<Clickable>(false))
                if (!clickable.Active)
                {
                    clickable.Active = true;
                    if (loggedEvents.Add("drawer-clickable")) log?.LogInfo("NATIVE_FEATS drawer Journal clickable enabled");
                }
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS drawer clickable: " + error.Message); }
    }

    private static string PathOf(Transform transform, Transform root)
    {
        string path = transform.name;
        for (var parent = transform.parent; parent != null && parent != root; parent = parent.parent) path = parent.name + "/" + path;
        return path;
    }

    // ---- Test-control and input ownership ----

    internal static bool OwnsInput(PegUIElement element) => Owns(element);

    private static readonly Dictionary<AchievementTileTray, UIBScrollable> subcategoryScrollers = new Dictionary<AchievementTileTray, UIBScrollable>();
    private static int subcategoryScrollerFrame = -1;

#if !SOLOSTONE_RELEASE
    private static UIBScrollable SubcategoryScroller(PegUIElement control, string id)
    {
        if (!shown || !NativeFeatsReveal.IsSubcategory(id) || ControlId(control) != id) return null;
        var tray = control.GetComponentInParent<AchievementTileTray>();
        var list = tray?.m_subcategoryButtonList;
        if (list == null || !control.transform.IsChildOf(list.transform)) return null;
        if (subcategoryScrollerFrame != Time.frameCount)
        {
            subcategoryScrollers.Clear();
            subcategoryScrollerFrame = Time.frameCount;
        }
        // The scrollbar may be a sibling of its content. Bind by the tray's Listable, never an arbitrary nearby scroller.
        if (!subcategoryScrollers.TryGetValue(tray, out var scroller))
        {
            scroller = widget.GetComponentsInChildren<UIBScrollable>(true).SingleOrDefault(candidate =>
                candidate.gameObject.activeInHierarchy && candidate.ScrollObject != null && candidate.m_ScrollBounds != null
                && list.transform.IsChildOf(candidate.ScrollObject.transform));
            subcategoryScrollers[tray] = scroller;
        }
        return scroller;
    }
#endif

#if !SOLOSTONE_RELEASE
    // Clipped only when the tray's scroller is bound and the tab's centre is outside it; an unbound or non-scrolling list falls back to the
    // normal screen/hit gates instead of refusing every press.
    internal static bool SubcategoryClipped(PegUIElement control, string id, List<Bounds> bounds)
    {
        try
        {
            var scroller = SubcategoryScroller(control, id);
            if (scroller == null || bounds.Count == 0 || !scroller.IsScrollNeeded()) return false;
            // Pinned UIBScrollable uses world bounds along m_ScrollPlane, expanded by its visible threshold.
            int axis = (int)scroller.m_ScrollPlane;
            Bounds viewport = scroller.m_ScrollBounds.bounds;
            float min = viewport.min[axis] - scroller.m_VisibleObjectThreshold;
            float max = viewport.max[axis] + scroller.m_VisibleObjectThreshold;
            // Clipped only when the tab's centre lies outside the viewport. Live Deck 2026-10-30: at the list's end the last
            // tab's hitbox overhangs m_ScrollBounds slightly, so a whole-bounds rule could never clear by scrolling;
            // partial overhang is left to the native hit test, which decides where the press lands.
            float low = bounds.Min(bound => bound.min[axis]), high = bounds.Max(bound => bound.max[axis]);
            float centre = (low + high) / 2f;
            return centre < min || centre > max;
        }
        catch { return false; }
    }
#endif

    private sealed class ScrollSample { internal float Value; internal int Frame = -1; internal bool Moving; }
    private static readonly Dictionary<UIBScrollable, ScrollSample> scrollSamples = new Dictionary<UIBScrollable, ScrollSample>();

#if !SOLOSTONE_RELEASE
    // The tab rect looks still during the scroll's ease-out tail, and the native scroll is not always an iTween on the
    // tab. Live Deck 2026-10-30: a press right after the long upward reveal to Library was swallowed. The list counts
    // as moving while its own scroll value changes between frames or a tween runs on the scroller or its content.
    internal static bool SubcategoryScrolling(PegUIElement control, string id, Func<GameObject, bool> tweening)
    {
        try
        {
            var scroller = SubcategoryScroller(control, id);
            if (scroller == null) return false;
            // One sample per scroller per frame, so interleaved trays keep their own history.
            bool known = scrollSamples.TryGetValue(scroller, out var sample);
            if (!known)
            {
                foreach (var dead in scrollSamples.Keys.Where(key => key == null).ToList()) scrollSamples.Remove(dead);
                scrollSamples[scroller] = sample = new ScrollSample();
            }
            if (sample.Frame != Time.frameCount)
            {
                float value = scroller.GetScroll();
                sample.Moving = known && Math.Abs(value - sample.Value) > 0.0001f
                    || tweening(scroller.gameObject) || scroller.ScrollObject != null && tweening(scroller.ScrollObject);
                sample.Value = value; sample.Frame = Time.frameCount;
            }
            return sample.Moving;
        }
        catch { return false; }
    }
#endif

#if !SOLOSTONE_RELEASE
    // Test-control only: move this list's scroll position, then let normal visibility/hit/stability gates decide the press.
    internal static bool RevealSubcategory(PegUIElement control, string id)
    {
        try
        {
            var scroller = SubcategoryScroller(control, id);
            if (scroller == null || ScrollCenter == null || ScrollEase == null || RevealDialog == null) return false;
            var dialogs = DialogManager.Get();
            if (dialogs != null && RevealDialog.GetValue(dialogs) != null) return false;
            if (Controls.Any(element => element != null && element.gameObject.activeInHierarchy && element.name == "DismissClickable")) return false;
            // EaseType lives in Blizzard.T5.iTween; pass the native default through cached bindings, as the shop does.
            return (bool)ScrollCenter.Invoke(scroller, new object[] {
                control.gameObject, 0f, null, ScrollEase.GetValue(scroller), scroller.m_ScrollTweenTime, false
            });
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FEATS reveal: " + (error.InnerException ?? error).Message); return false; }
    }
#endif

    internal static PegUIElement HomeButton
    {
        get
        {
            var journal = Box.Get()?.m_journalButtonWidget;
            if (journal == null || !HomeJournalReady) return null;
            // The widget's own Clickable carries the click (BUTTON_CLICKED); the legacy QuestLogButton inside has no collider.
            var elements = journal.GetComponentsInChildren<PegUIElement>(false).Where(element => element.gameObject.activeInHierarchy).ToArray();
            return elements.FirstOrDefault(element => element.GetComponent<Clickable>() != null) ?? elements.FirstOrDefault();
        }
    }

    internal static IEnumerable<PegUIElement> Controls => active && widget != null
        ? widget.GetComponentsInChildren<PegUIElement>(true) : Enumerable.Empty<PegUIElement>();

    internal static IEnumerable<PegUIElementProxy> Proxies => active && widget != null
        ? widget.GetComponentsInChildren<PegUIElementProxy>(true) : Enumerable.Empty<PegUIElementProxy>();

#if !SOLOSTONE_RELEASE
    internal static string ControlId(PegUIElement element)
    {
        if (!Owns(element)) return null;
        // Live names: JournalPopup/Root/ClicktoDismiss closes Feats; JournalPopupManager/Root/DismissClickable closes an
        // inspection or reward preview; the inspection's See Rewards button carries the retail rewards-button key.
        if (element.name == "ClicktoDismiss") return "feats.close";
        if (element.name == "DismissClickable") return "feats.popup.dismiss";
        if (element is UIBButton && element.GetComponentInParent<AchievementInspectionPopup>() != null) return "feats.inspect.rewards";
        var template = element.GetComponentInParent<WidgetTemplate>();
        if (template == null) return null;
        if (template.GetDataModel<AchievementDataModel>() is AchievementDataModel achievement && feats?.Find(achievement.ID) is LocalFeat feat)
            return "feats.tile:" + feat.Id;
        if (template.GetDataModel<AchievementSubcategoryDataModel>() is AchievementSubcategoryDataModel sub)
            return "feats.subcategory:" + feats?.Categories.SelectMany(c => c.Subcategories).FirstOrDefault(s => s.NativeId == sub.ID)?.Id;
        if (template.GetDataModel<AchievementCategoryDataModel>() is AchievementCategoryDataModel category)
            return "feats.category:" + feats?.Categories.FirstOrDefault(c => c.Name == category.Name)?.Id;
        return null;
    }
#endif

#if !SOLOSTONE_RELEASE
    internal static JsonNode State()
    {
        var stats = feats?.Stats;
        var tray = widget == null ? null : widget.GetComponentInChildren<JournalTrayDisplay>(true);
        var categories = new JsonList();
        foreach (LocalFeatCategory category in feats?.Categories ?? new LocalFeatCategory[0])
            categories.Add(new JsonNode { { "id", category.Id }, { "name", category.Name }, { "points", category.Stats.Points },
                { "available", category.Stats.AvailablePoints }, { "percent", category.Stats.Percent },
                { "completed", category.Stats.Completed }, { "total", category.Stats.Total } });
        return new JsonNode {
            { "open", active }, { "shown", shown && JournalPopup.s_isShowing },
            { "tab", tray == null ? "none" : tray.GetActiveJournalTab().ToString() },
            { "points", stats?.Points ?? 0 }, { "availablePoints", stats?.AvailablePoints ?? 0 },
            { "earnedCount", stats?.Completed ?? 0 }, { "catalogCount", LocalAchievementCatalog.All.Count },
            { "categories", categories }
        };
    }
#endif

    // ---- Collection acquisition copy for achievement-exclusive cosmetics ----

    // Preserve retail flavor and the separately bound artist credit; replace only stale acquisition copy for the closed 17.
    private static void HeroAcquisitionCopy(BaseHeroSkinInfoManager __instance)
    {
        try
        {
            if (!(__instance is HeroSkinInfoManager)) return;
            var entity = HeroPreviewEntity?.GetValue(__instance) as EntityDef;
            string copy = LocalAchievementCosmeticCopy.ForHeroSkin(entity?.GetCardId(), entity?.GetFlavorText());
            if (copy == null) return;
            SetDescription(HeroVanillaDescription, __instance, copy);
            SetDescription(HeroNewDescription, __instance, copy);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_ACHIEVEMENTS hero acquisition copy unavailable: " + error.Message); }
    }

    private static void CardBackAcquisitionCopy(CardBackInfoManager __instance, int cardBackIdx)
    {
        try
        {
            object current = CardBackPreviewId?.GetValue(__instance);
            if (!(current is int shownId) || shownId != cardBackIdx) return;
            string copy = LocalAchievementCosmeticCopy.ForCardBack(cardBackIdx, GameDbf.CardBack.GetRecord(cardBackIdx)?.Description);
            if (copy != null) SetDescription(CardBackDescription, __instance, copy);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_ACHIEVEMENTS card-back acquisition copy unavailable: " + error.Message); }
    }

    private static void SetDescription(FieldInfo field, object ownerObject, string copy)
    {
        object text = field?.GetValue(ownerObject);
        if (text == null || DescriptionText == null) throw new InvalidOperationException("native description text unavailable");
        DescriptionText.SetValue(text, copy, null);
    }
}
