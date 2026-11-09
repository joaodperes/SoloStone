#if PINNED_CLIENT_ALIAS
extern alias pinnedClient;
using EmoteType = pinnedClient::EmoteType;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using PegasusGame;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
#if ENGINE_SOURCE
using SabberStoneCore.Model.Rewind;
#endif
using SabberStoneCore.Tasks.PlayerTasks;
using NativeEntity = PegasusGame.Entity;
using NativePlayer = PegasusGame.Player;
using NativeTag = PegasusGame.Tag;
using NativeBnetId = PegasusShared.BnetId;
using EngineGame = SabberStoneCore.Model.Game;
using EngineHistoryEntity = SabberStoneCore.Kettle.PowerHistoryEntity;
using NativePowerHistory = PegasusGame.PowerHistory;
using EngineCard = SabberStoneCore.Model.Card;
using EngineCards = SabberStoneCore.Model.Cards;
using EngineZone = SabberStoneCore.Enums.Zone;
using NativeOption = PegasusGame.Option;

public sealed class NativeMatch
{
    private const int HumanPlayerId = 1;
    private const int BotPlayerId = 2;
    private const int MaxBotActions = 128;
    private const int CurrentSpellPowerBaseTag = 291;
    private const GameTag TemporaryCardTag = (GameTag)3630;
    // Keep raw causes separate: removing Temporary must not remove an existing Echo visual.
    private readonly Dictionary<int, (int Temporary, int Ghostly)> historicalTemporaryVisuals = new Dictionary<int, (int, int)>();
    private readonly HashSet<int> temporaryDiscardVisuals = new HashSet<int>();
#if ENGINE_SOURCE || STANDARD_PROOF
    private readonly HashSet<int> poweredUpHighlights = new HashSet<int>();
#endif
    // Pinned entity consumers, not a blanket Card.Tags copy; see tests/native-match/printed-tags-evidence.md.
    private static readonly GameTag[] PrintedDefinitionTags = {
        GameTag.TAG_SCRIPT_DATA_NUM_1, // Card text builders' first printed parameter.
        GameTag.TAG_SCRIPT_DATA_NUM_2, // Card text builders' second printed parameter.
        GameTag.TRIGGER_VISUAL, // EntityBase.HasTriggerVisual enables the trigger bauble.
        GameTag.ELITE, // EntityBase.IsElite enables the legendary dragon crest.
        GameTag.CARD_SET, // EntityBase.GetCardSet supplies frame/set presentation.
        GameTag.WINDFURY, // EntityBase.HasWindfury and keyword help.
        GameTag.TAUNT, // EntityBase.HasTaunt and keyword help.
        GameTag.STEALTH, // EntityBase.HasStealth and keyword help.
        GameTag.SPELLPOWER, // EntityBase.HasSpellPower and spell-power help.
        GameTag.DIVINE_SHIELD, // EntityBase.HasDivineShield enables shield visuals.
        GameTag.CHARGE, // EntityBase.HasCharge and keyword help.
        GameTag.CLASS, // EntityBase.GetClasses supplies class presentation.
        GameTag.FREEZE, // TooltipPanelManager reads the printed Freeze keyword.
        GameTag.ENRAGED, // EntityBase.IsEnraged enables actor state.
        GameTag.OVERLOAD, // TooltipPanelManager reads the printed Overload amount.
        GameTag.DEATHRATTLE, // EntityBase.HasDeathrattle enables deathrattle presentation.
        GameTag.BATTLECRY, // EntityBase.HasBattlecry and InputManager battlecry targeting.
        GameTag.AI_MAKES_DECISIONS_FOR_PLAYER,
        (GameTag)219, // SECRET: EntityBase.IsSecret selects secret presentation.
        GameTag.COMBO, // Card.DoOptionHighlight combines this with controller COMBO_ACTIVE.
        GameTag.IMMUNE, // EntityBase.IsImmune and ZonePlay targeting feedback.
        GameTag.CANT_BE_TARGETED_BY_SPELLS, // EntityBase.CanBeTargetedBySpells gates spell interaction.
        GameTag.CANT_BE_TARGETED_BY_HERO_POWERS, // EntityBase.CanBeTargetedByHeroPowers gates power interaction.
        GameTag.SILENCE, // TooltipPanelManager reads the printed Silence keyword.
        (GameTag)340, // COUNTER: EntityBase.HasCounter and keyword help.
        (GameTag)363, // POISONOUS: EntityBase.IsPoisonous enables poison presentation.
        GameTag.AFFECTED_BY_SPELL_POWER, // EntityBase.IsAffectedBySpellPower controls text scaling.
        (GameTag)4128, // Fyrakk: direct Fire-spell immunity, never global IMMUNE.
        GameTag.IMMUNE_WHILE_ATTACKING, // AttackSpellController controls attack/death visuals.
        GameTag.HERO_POWER, // Card.CreateCardsInTooltip selects the hero power preview.
        GameTag.FORGETFUL, // Entity.GetPrioritizedBaubleSpellType and target arrows.
        GameTag.HEROPOWER_DAMAGE, // EntityBase.HasHeroPowerDamage controls damage presentation.
        GameTag.HIDE_STATS, // Actor.HasHideStats suppresses card statistics.
        GameTag.INSPIRE, // EntityBase.HasInspire enables inspire presentation.
        GameTag.RECEIVES_DOUBLE_SPELLDAMAGE_BONUS, // Entity.GetDamageBonus scales displayed spell damage.
        GameTag.DISCOVER, // TooltipPanelManager reads the Discover keyword.
        GameTag.JADE_GOLEM, // Entity.GetJadeGolem supplies Jade Golem presentation.
        GameTag.CHOOSE_ONE, // Player.UpdateChooseBoth and keyword help.
        GameTag.UNTOUCHABLE, // EntityBase targetability, sleep and immunity presentation.
        GameTag.SCORE_VALUE_1, // ScoreValueCountDownCardTextBuilder's first printed value.
        GameTag.SCORE_VALUE_2, // ScoreValueCountDownCardTextBuilder's second printed value.
        GameTag.QUEST, // EntityBase.IsQuest selects quest frame and UI.
        GameTag.DEFINING_ENCHANTMENT, // TooltipPanelManager selects defining-enchantment help.
        GameTag.FINISH_ATTACK_SPELL_ON_DAMAGE, // AttackSpellController selects attack completion behavior.
        GameTag.MULTIPLE_CLASSES, // EntityBase.GetClasses supplies multiclass presentation.
        (GameTag)482, // GRIMY_GOONS: EntityBase.HasFaction supplies faction presentation.
        (GameTag)483, // JADE_LOTUS: EntityBase.HasFaction supplies faction presentation.
        (GameTag)484, // KABAL: EntityBase.HasFaction supplies faction presentation.
        GameTag.QUEST_PROGRESS_TOTAL, // Quest UI and text builders need the printed goal.
        GameTag.ADAPT, // TooltipPanelManager reads the Adapt keyword.
        GameTag.HIDE_HEALTH, // Actor suppresses health text.
        GameTag.HIDE_ATTACK, // Actor suppresses attack text.
        GameTag.HIDE_COST, // Actor suppresses cost text.
        GameTag.LIFESTEAL, // EntityBase.HasLifesteal and Card healing hints.
        GameTag.RECRUIT, // TooltipPanelManager reads the Recruit keyword.
        GameTag.GHOSTLY, // ActorNames and Card choose ghostly presentation.
        GameTag.SECRET_DEATHRATTLE, // EntityBase.HasSecretDeathrattle enables its bauble.
        GameTag.RUSH, // EntityBase.HasRush and keyword help.
        GameTag.ECHO, // TooltipPanelManager reads the Echo keyword.
        (GameTag)849, // MAGNETIC: ZonePlay.OnMagneticHeld/Dropped/Play enables magnetic interaction.
        GameTag.IGNORE_HIDE_STATS_FOR_BIG_CARD, // BigCard.SetupActor overrides stat hiding.
        GameTag.HEALTH_DISPLAY, // Actor selects the health display format.
        GameTag.ENABLE_HEALTH_DISPLAY, // Actor enables the special health display.
        GameTag.VOODOO_LINK, // Card.ShowMouseOverSpell selects the linked-card visual.
        GameTag.OVERKILL, // EntityBase.HasOverKill and enchantment help.
        (GameTag)968, // START_OF_GAME_KEYWORD: TooltipPanelManager reads the Start of Game keyword.
        GameTag.ENCHANTMENT_INVISIBLE, // Entity.GetDisplayedEnchantments filters hidden enchantments.
        GameTag.FAST_BATTLECRY, // PowerSpellController selects battlecry animation.
        GameTag.TREAT_AS_PLAYED_HERO_CARD, // Card chooses hero replacement presentation.
        (GameTag)1020, // LITERALLY_UNPLAYABLE: TooltipPanelManager and Actor render locked abilities.
        GameTag.GEARS, // ChoiceCardMgr.GetChoiceEffectDataForCard selects choice presentation.
        (GameTag)1077, // CASTS_WHEN_DRAWN: TooltipPanelManager reads the Casts When Drawn keyword.
        (GameTag)4706, // Exported either-side placement companion flag.
        (GameTag)4717, // Hero spell 330: Godfrey's persistent deck portal.
        (GameTag)3128, // SUMMONED_WHEN_DRAWN: exported Tortollan Ninja keyword.
        GameTag.DISPLAY_CARD_ON_MOUSEOVER, // Card and DisplayCardsInToolip show related cards.
        GameTag.REBORN, // EntityBase.HasReborn and Card death presentation.
        GameTag.QUEST_REWARD_DATABASE_ID, // QuestController and GameUtils show the printed reward.
        GameTag.HIDE_WATERMARK, // Actor.UpdateWatermark hides set watermark.
        GameTag.PLAYER_TAG_THRESHOLD_TAG_ID, // PlayerTagThresholdCardTextBuilder selects the counter.
        GameTag.PLAYER_TAG_THRESHOLD_VALUE, // PlayerTagThresholdCardTextBuilder supplies its threshold.
        GameTag.AFFECTED_BY_HEALING_DOES_DAMAGE, // Card.CanPlayHealingDoesDamageHint selects hints.
        GameTag.ALTERNATE_MOUSE_OVER_CARD, // TooltipPanelManager selects alternate hover identity.
        GameTag.ENCHANTMENT_BANNER_TEXT, // Entity and EnchantmentBanner render banner text.
        GameTag.MOUSE_OVER_CARD_APPEARANCE, // BigCard.SetupActor selects hover appearance.
        (GameTag)1192, // SIDE_QUEST: EntityBase.IsSideQuest selects sidequest UI.
        GameTag.TWINSPELL, // TooltipPanelManager reads the Twinspell keyword.
        GameTag.COIN_MANA_GEM, // Actor.UseCoinManaGem selects the cost gem.
        GameTag.MEGA_WINDFURY, // TooltipPanelManager reads the Mega-Windfury keyword.
        (GameTag)1211, // ELUSIVE: Actor immunity visuals and keyword help.
        GameTag.EMPOWER, // TooltipPanelManager reads the Invoke keyword.
        (GameTag)1333, // OUTCAST: TooltipPanelManager reads Outcast; POWERED_UP remains separately computed.
        GameTag.USE_DISCOVER_VISUALS, // ChoiceCardMgr selects Discover presentation.
        (GameTag)1427, // SPELLBURST: EntityBase.HasSpellburst enables spellburst presentation.
        GameTag.COLLECTION_RELATED_CARD_DATABASE_ID, // GameUtils.GetRelatedCardIds supplies related-card tooltips.
        GameTag.SUPPRESS_ALL_SUMMON_VO, // Card.WillSuppressPlaySounds suppresses summon voiceover.
        (GameTag)1508, // CAN_TARGET_CARDS_IN_HAND: Entity.HasHandTarget enables hand targeting.
        (GameTag)1524, // CORRUPT: TooltipPanelManager reads the Corrupt keyword.
        (GameTag)1551, // CORRUPTED_CARD: TooltipPanelManager reads the Corrupted keyword.
        (GameTag)1635, // SPELL_SCHOOL: EntityBase.GetSpellSchool and Actor.UpdateRace label spells.
        (GameTag)1637, // FRENZY: EntityBase.HasFrenzy enables frenzy presentation.
        (GameTag)1719, // TRANSFORMED_FROM_CARD_VISUAL_TYPE: HistoryManager selects transform history visuals.
        (GameTag)1720, // TRADEABLE: EntityBase.IsTradeable enables deck-drop Trade and icon.
        (GameTag)1725, // QUESTLINE: EntityBase.IsQuestline selects questline UI.
        (GameTag)1743, // DECK_ACTION_COST: Card.HasEnoughManaToDeckAction gates deck-drop mana.
        (GameTag)1749, // SIGIL: EntityBase.IsSigil and ActorNames select sigil presentation.
        (GameTag)1816, // SHOW_DISCOVER_FROM_DECK: ChoiceCardMgr selects deck Discover presentation.
        (GameTag)1920, // HONORABLE_KILL: EntityBase.HasHonorableKill and enchantment help.
        (GameTag)1932, // HAS_DIAMOND_QUALITY: EntityBase.GetPremiumType and related-card actors.
        (GameTag)1944, // NON_KEYWORD_POISONOUS: EntityBase.IsPoisonous also checks this printed flag.
        (GameTag)1945, // SPELLPOWER_ARCANE: EntityBase.GetSpellPowerSchool selects Arcane bonus.
        (GameTag)1946, // SPELLPOWER_FIRE: EntityBase.GetSpellPowerSchool selects Fire bonus.
        (GameTag)1947, // SPELLPOWER_FROST: EntityBase.GetSpellPowerSchool selects Frost bonus.
        (GameTag)1948, // SPELLPOWER_NATURE: EntityBase.GetSpellPowerSchool selects Nature bonus.
        (GameTag)1949, // SPELLPOWER_HOLY: EntityBase.GetSpellPowerSchool selects Holy bonus.
        (GameTag)1950, // SPELLPOWER_SHADOW: EntityBase.GetSpellPowerSchool selects Shadow bonus.
        (GameTag)1951, // SPELLPOWER_FEL: EntityBase.GetSpellPowerSchool selects Fel bonus.
        (GameTag)1992, // QUESTLINE_FINAL_REWARD_DATABASE_ID: GameUtils.GetRelatedCardIds supplies final questline reward.
        (GameTag)1993, // QUESTLINE_PART: QuestlineProgressUI labels the current part.
        (GameTag)2108, // TARGETING_ARROW_TYPE: TargetReticleManager selects targeting arrow style.
        (GameTag)2196, // COST_BLOOD: RunePattern and EntityBase.GetBloodCost render Blood runes.
        (GameTag)2197, // COST_FROST: RunePattern and EntityBase.GetFrostCost render Frost runes.
        (GameTag)2198, // COST_UNHOLY: RunePattern and EntityBase.GetUnholyCost render Unholy runes.
        (GameTag)2247, // COLOSSAL: DisplayCardsInToolip and history show Colossal parts.
        GameTag.MODULAR_ENTITY_PART_1, // ModularEntityCardTextBuilder: ordered component dbfIds.
        GameTag.MODULAR_ENTITY_PART_2,
        (GameTag)782, // EntityDef.GetCardTextBuilder: composed potion/Creation builder override.
        (GameTag)4960, // Ritual keyword.
        (GameTag)2311, // OBJECTIVE: ActorNames and EntityBase.IsObjective select objective UI.
        (GameTag)2329, // OBJECTIVE_AURA: ActorNames selects objective aura actor.
        (GameTag)2332, // DREDGE: TooltipPanelManager reads the Dredge keyword.
        (GameTag)2456, // INFUSE: TooltipPanelManager reads the Infuse requirement.
        (GameTag)2457, // INFUSED: TooltipPanelManager reads the Infused keyword.
        (GameTag)2459, // ENTITY_TAG_THRESHOLD_TAG_ID: EntityTagThresholdCardTextBuilder selects the counter.
        (GameTag)2460, // ENTITY_TAG_THRESHOLD_VALUE: EntityTagThresholdCardTextBuilder supplies its threshold.
        (GameTag)2469, // COLOSSAL_LIMB_ON_LEFT: DisplayCardsInToolip selects limb placement.
        (GameTag)2498, // MANATHIRST: TooltipPanelManager reads the Manathirst threshold.
        (GameTag)2589, // HAS_SIGNATURE_QUALITY: EntityBase.GetPremiumType and related-card actors.
        (GameTag)2772, // TITAN: EntityBase.IsTitan and TooltipPanelManager gate ability UI.
        (GameTag)2785, // FORGE: EntityBase.IsForgeable enables deck-drop Forge and icon.
        (GameTag)2820, // FINALE: TooltipPanelManager reads the Finale keyword.
        (GameTag)2821, // OVERHEAL: EntityBase.HasOverheal enables overheal presentation.
        (GameTag)2840, // HAS_ACTIVATE_POWER: Actor.UpdateCostTextMesh selects activatable cost presentation.
        (GameTag)2853, // VENOMOUS: EntityBase.IsVenomous enables poison presentation.
        (GameTag)2859, // MAGNETIC_TO_RACE: Entity.CanBeMagnitizedBy gates magnetic target race.
        (GameTag)2889, // TAG_SCRIPT_DATA_NUM_3: ReferenceScriptDataNumCardRaceCardTextBuilder's third parameter.
        (GameTag)2905, // QUICKDRAW: TooltipPanelManager reads the Quickdraw keyword.
        (GameTag)2919, // TAG_SCRIPT_DATA_NUM_4: ReferenceScriptDataNumCardRaceCardTextBuilder's fourth parameter.
        (GameTag)2920, // TAG_SCRIPT_DATA_NUM_5: ReferenceScriptDataNumCardRaceCardTextBuilder's fifth parameter.
        (GameTag)2921, // TAG_SCRIPT_DATA_NUM_6: ReferenceScriptDataNumCardRaceCardTextBuilder's sixth parameter.
        (GameTag)3074, // FORGES_INTO: HistoryManager.CreateForgeTransformedEntity selects result preview.
        (GameTag)3078, // RITUALIST_MINION: TooltipPanelManager reads the Ritual keyword.
        (GameTag)3114, // EXCAVATE: TooltipPanelManager reads the Excavate keyword.
        (GameTag)3919, // ADDITIONAL_HERO_POWER_INDEX: ZoneHeroPower selects second slot.
        (GameTag)4029, // ADDITIONAL_HERO_POWER_ENTITY_1: player secondary reference.
        (GameTag)3130, // IS_ALTERNATE_HEROPOWER: Card.NotifyOfHeroPowerPlayed selects power presentation.
        (GameTag)3211, // HERO_DOESNT_MOVE_ON_ATTACK: AttackSpellController.LaunchAttack chooses motion.
        (GameTag)3228, // TOURIST: TooltipPanelManager selects Tourist help by class.
        (GameTag)3318, // MINIATURIZE: Card and TwinspellHoldSpell select Miniaturize visuals.
        (GameTag)3319, // MINI: TwinspellHoldSpell selects Mini visuals.
        (GameTag)3382, // KEEP_HERO_CLASS: Player.SetHero preserves displayed class.
        (GameTag)3399, // GIGANTIFY: Card and TwinspellHoldSpell select Gigantify visuals.
        (GameTag)3400, // GIGANTIC: TwinspellHoldSpell selects Gigantic visuals.
        (GameTag)3429, // PALADIN_AURA: TooltipPanelManager reads the Aura keyword.
        (GameTag)3438, // SUPPRESS_HERO_STANDARD_SUMMON_FX: Card suppresses standard hero summon effect.
        (GameTag)3457, // ZERG: EntityBase.HasFaction supplies faction presentation.
        (GameTag)3458, // TERRAN: EntityBase.HasFaction supplies faction presentation.
        (GameTag)3465, // EXTRA_TURNS_SPELL_OVERRIDE: ExtraTurnSpellConfig.GetSpell selects extra-turn presentation.
        (GameTag)3469, // PROTOSS: EntityBase.HasFaction supplies faction presentation.
        (GameTag)3471, // HIDE_HEALTH_NUMBER: Actor suppresses numeric health.
        (GameTag)3472, // HIDE_ATTACK_NUMBER: Actor suppresses numeric attack.
        (GameTag)3482, // IGNORE_SUPPRESS_MILL_ANIMATION: Card.MillCard selects visible mill animation.
        (GameTag)3523, // QUEST_HIDE_PROGRESS: Entity.GetCustomObjectiveBannerText hides quest progress.
        (GameTag)3555, // STARSHIP: EntityBase.IsStarship and InputManager select starship interaction.
        (GameTag)3564, // CORNER_REPLACEMENT_TYPE: CornerSpellReplacementManager selects hero board corners.
        (GameTag)3567, // IS_NIGHTMARE_BONUS: Actor description and TooltipPanelManager show Dark Gift help.
        (GameTag)3568, // STARSHIP_PIECE: StarshipHUDManager selects piece previews.
        (GameTag)3597, // ROGUE_TOURIST: TooltipPanelManager selects Rogue Tourist help.
        (GameTag)3598, // WARLOCK_TOURIST: TooltipPanelManager selects Warlock Tourist help.
        (GameTag)3599, // DEATH_KNIGHT_TOURIST: TooltipPanelManager selects Death Knight Tourist help.
        (GameTag)3600, // SHAMAN_TOURIST: TooltipPanelManager selects Shaman Tourist help.
        (GameTag)3601, // DEMON_HUNTER_TOURIST: TooltipPanelManager selects Demon Hunter Tourist help.
        (GameTag)3602, // PRIEST_TOURIST: TooltipPanelManager selects Priest Tourist help.
        (GameTag)3603, // HUNTER_TOURIST: TooltipPanelManager selects Hunter Tourist help.
        (GameTag)3604, // WARRIOR_TOURIST: TooltipPanelManager selects Warrior Tourist help.
        (GameTag)3605, // DRUID_TOURIST: TooltipPanelManager selects Druid Tourist help.
        (GameTag)3606, // MAGE_TOURIST: TooltipPanelManager selects Mage Tourist help.
        (GameTag)3607, // PALADIN_TOURIST: TooltipPanelManager selects Paladin Tourist help.
        (GameTag)3626, // IMBUE: TooltipPanelManager reads the Imbue keyword.
        (GameTag)3664, // FALLBACK_ENCHANTMENT_PORTRAIT_DBID: Entity.SetEnchantmentPortraitCardID selects portrait.
        (GameTag)3692, // CARES_ABOUT_IMBUE_CARDS: EntityBase selects neutral Imbue presentation.
        (GameTag)3744, // END_OF_TURN_TRIGGER: EntityBase.HasEndOfTurnTrigger enables trigger presentation.
        (GameTag)3837, // SKIP_ARMOR_ANIMATION: Actor.IsSkipArmorAnimationActive selects armor presentation.
        (GameTag)3934, // REWIND: RewindUIManager selects rewind history choices.
        (GameTag)4013, // STARSHIP_LAUNCH_TRIGGER: Entity.GetPrioritizedBaubleSpellType selects launch bauble.
        (GameTag)4028, // KINDRED: TooltipPanelManager reads Kindred; POWERED_UP remains separately computed.
        (GameTag)4058, // CORPSE_SPENDER: CorpseCounter.ShouldShowCorpseCounter enables corpse UI.
        (GameTag)4088, // HEROPOWER_UNLIMITED_USES: InputManager keeps repeatable hero-power interaction enabled.
        (GameTag)4089, // INTERACTABLE_OBJECT: Entity and InputManager select interactable object behavior.
        (GameTag)4090, // INTERACTABLE_OBJECT_COST: Entity.InteractableObjectCanBeActivatedNow gates activation.
        (GameTag)4161, // DYNAMIC_KEYWORD1: DynamicKeywordCardTextBuilder and keyword help resolve keyword one.
        (GameTag)4162, // DYNAMIC_KEYWORD2: DynamicKeywordCardTextBuilder and keyword help resolve keyword two.
        (GameTag)4174, // DONT_PLAY_VFX_FROM_EXTRA_TRIGGERED_POWER: TriggerSpellController suppresses duplicate effects.
        (GameTag)4213, // SUPPRESS_IMMOLATE_VISUAL_FOR_OPPONENT: Actor.UpdateImmolating controls opponent visuals.
        (GameTag)4238, // REPEATABLE: QuestProgressUI renders repeatable quest arrow.
        (GameTag)4239, // SHATTER: EntityBase.IsShatterable enables Shatter interaction.
        (GameTag)4257, // USES_CHARGES: PlayErrors selects charge-cost error text.
        (GameTag)4300, // SUPPRESS_ALT_CARD_TEXT_FOR_OPPONENT: CardTextBuilder.GetAltTextIndex controls opponent text.
        (GameTag)4309, // HERALD: TooltipPanelManager reads the Herald keyword.
        (GameTag)4354, // PREPARE: EntityBase.IsPrepareable enables deck-drop Prepare and icon.
        (GameTag)4410, // NOZDORMU_DORMANT_EFFECT_WATCHER: EntityBase and ZonePlay select Nozdormu hover UI.
        (GameTag)4414, // LOCK_VISUAL: EntityBase.HasLockVisual enables lock presentation.
        (GameTag)4508, // VISIBLE_PLAYER_ENCHANTMENT_CARD: BigCard displays player enchantment cards.
        (GameTag)LocalNativeText.FirstCompositeHalfTag, // Retail composed upper text.
        (GameTag)LocalNativeText.SecondCompositeHalfTag, // Retail composed lower text.
        (GameTag)4628, // SKIP_HERO_TWEEN: Zone.UpdateLayout suppresses hero movement.
        (GameTag)4690, // DISGUISED: EntityBase.HasDisguisedAction selects disguised presentation.
        (GameTag)4754, // OLD_GOD: EntityBase.IsOldGod selects Old God presentation.
        (GameTag)4786, // ACTIVATABLE_MINION: EntityBase.IsActivatableMinion enables minion ability interaction.
        (GameTag)4839, // INTERACTABLE_OBJECT_PASSIVE_ANIMATION_TYPE: Actor and EntityBase.HasActivate select object presentation.
        (GameTag)4911, // OLD_GOD_CTHUN_TOOLTIP: TooltipPanelManager selects C'Thun keyword help.
        (GameTag)4947, // ULDIR_QUARANTINE_TRACKER: Actor.UpdateUldirQuarantinePips renders the printed tracker.
        (GameTag)4960, // OBJECTIVE_COUNTDOWN: ActorNames selects countdown objective actor.
    };

    private static readonly GameTag[] CopyCompletionTags = {
        GameTag.ATK, GameTag.HEALTH, GameTag.DAMAGE, GameTag.NUM_ATTACKS_THIS_TURN,
        GameTag.EXHAUSTED, GameTag.STEALTH, GameTag.IMMUNE, GameTag.TAUNT,
        GameTag.CANT_BE_TARGETED_BY_SPELLS, GameTag.CANT_BE_TARGETED_BY_HERO_POWERS,
        GameTag.DIVINE_SHIELD, GameTag.FROZEN, GameTag.SILENCED, GameTag.WINDFURY,
        GameTag.CHARGE, GameTag.RUSH, GameTag.POISONOUS, GameTag.CANT_ATTACK,
        GameTag.CANNOT_ATTACK_HEROES
    };

    private sealed class CopyCompletion
    {
        internal List<IPowerHistoryEntry> Entries;
        internal int HistoryIndex;
        internal int EntityId;
        internal Dictionary<GameTag, int> Tags;
        internal bool RequiresPublicReplay;
    }

    private static readonly string[] DeckCardIds =
    {
        "CS2_231", "CS2_168", "CS2_172", "CS2_120", "CS2_121",
        "CS2_173", "CS2_125", "CS2_127", "CS2_182", "CS2_179",
        "CS2_131", "CS2_187", "CS2_200", "CS2_186", "CS2_201"
    };

    private readonly NativePetReactions petReactions = new NativePetReactions();
    private readonly NativePetReactions npcPetReactions = new NativePetReactions();
    private readonly Action<int, IProtoBuf> send;
    private readonly HashSet<int> revealedEntities = new HashSet<int>();
    private readonly Dictionary<int, Dictionary<int, int>> visibleTransportTags = new Dictionary<int, Dictionary<int, int>>();
    private readonly Dictionary<int, int> historicalControllers = new Dictionary<int, int>();
    private readonly Dictionary<int, int> historicalPositions = new Dictionary<int, int>();
    private readonly Dictionary<int, EngineZone> historicalZones = new Dictionary<int, EngineZone>();
    // Transport-only IDs descend from the positive limit; engine IDs ascend from one.
    private int nextDeckChoiceId = int.MaxValue;
#if ENGINE_SOURCE
    private readonly HashSet<int> leylineTextEntities = new HashSet<int>();
    private readonly Dictionary<int, HashSet<int>> privacyTargets = new Dictionary<int, HashSet<int>>();
    // Active offer originals make their gift attachments private to this chooser, never their DECK actors.
    private readonly HashSet<int> privateDeckOffers = new HashSet<int>();
    private readonly Dictionary<int, int> deckChoiceOriginals = new Dictionary<int, int>();
    private readonly HashSet<int> privateChoiceOffers = new HashSet<int>();
    private readonly Stack<bool> visibleSubSpells = new Stack<bool>();
    private readonly Stack<int> visualBlockSources = new Stack<int>();
    private readonly Stack<bool> revealedTriggerScopes = new Stack<bool>();
    private readonly HashSet<int> pendingTriggerReveals = new HashSet<int>();
    private readonly HashSet<int> startOfGameSources = new HashSet<int>();
    private readonly HashSet<int> presentedStartOfGameSources = new HashSet<int>();
    private bool startOfGameClosed, startOfGameMainBegin, startOfGameBarrier;
    // CardDef.m_TriggerEffectDefs slots (data/fx/start-of-game-trigger-slots-37.0.0.254477.tsv). Retail calibration:
    // the reveal uses index = trigger count (out of range, no VFX) and the 968 effect the first StartOfGameSpell slot.
    // -1 keeps the trigger VFX off where no StartOfGameSpell slot exists. Mug'Zee's effect slot depends on its branch.
    private static readonly Dictionary<string, (int Reveal, int Effect)> StartOfGameEffectSlots =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal) {
            ["JAIL_800"] = (10, 0), ["JAIL_384"] = (3, 1), ["EDR_000"] = (4, 0),
            ["EDR_845"] = (3, 1), ["JAIL_860"] = (2, 0), ["JAIL_509"] = (-1, -1),
            ["TIME_020"] = (2, 1), ["TIME_875t"] = (1, 0), ["CS3_035"] = (2, 0)
        };
#endif
    private readonly Dictionary<int, (int Used, int Locked)> historicalMana = new Dictionary<int, (int Used, int Locked)>();
    private readonly Dictionary<int, int> lastSpellPower = new Dictionary<int, int>();
    private readonly List<CopyCompletion> copyCompletions = new List<CopyCompletion>();
#if ENGINE_SOURCE
    // Retain the complete client map across HIDE; the ordinary visibility cache intentionally forgets private stats.
    private readonly Dictionary<int, Dictionary<int, int>> clientTransportTags = new Dictionary<int, Dictionary<int, int>>();
    private readonly Dictionary<int, string> clientTransportCards = new Dictionary<int, string>();
    private sealed class RewardCheckpoint
    {
        internal HashSet<string> Played;
        internal HashSet<int> Board, Defeated;
        internal Dictionary<int, int> Controllers;
        internal int MinimumHealth, Source;
        internal string Card;
        internal bool HumanPlay;
    }
    private RewardCheckpoint rewindRewards;
    private Dictionary<int, HashSet<int>> rewindPrivacy;
    private Dictionary<int, int> rewindDerivedPremiums;
    private HashSet<int> rewindHighlights, rewindChoiceOffers, rewindAnonymousEntities;
    private bool hasRewound;
#endif
    private PowerAllOptions humanOptions;
    private PowerEntityChoices humanChoices;
#if ENGINE_SOURCE
    private long humanDecisionId;
    private int humanDecisionController;
    private bool humanPlacementBoardHadLocations;
#endif
    private readonly LocalDeck playerDeck, opponentDeck;
    private readonly EngineCard playerHero;
    private readonly List<EngineCard> playerCards;
    private readonly HashSet<string> playedCardIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<int> enemyBoardMinions = new HashSet<int>();
    private readonly HashSet<int> defeatedEnemyMinions = new HashSet<int>();
    private readonly Dictionary<int, int> rewardControllers = new Dictionary<int, int>();
    private int minimumHealth = 30;
    private readonly int seed, startingStars;
    private int planningTurn = -1, decisionIndex;
    private readonly System.Diagnostics.Stopwatch turnClock = new System.Diagnostics.Stopwatch();
    public Action<string> BotTrace;
    internal Action<string> ChoiceTrace;
    // One line per match event for both seats (MatchEventLog). Null: no log sink; the journal row is still written.
    internal Action<string> EventTrace;
#if ENGINE_SOURCE
    private MatchEventLog eventLog;
    private List<string> eventBatch;
#endif
    internal Func<int, Dictionary<string, object>> NativeEntityDiagnostic;
    // Test-only: bound bot search by nodes alone and drop the per-turn clock fallback, so suite output is reproducible.
    // The plugin never sets this; in-game search keeps its millisecond budgets.
    internal static bool ClockFreeBot { get; set; }
#if STANDARD_PROOF
    public Action<string> TradeTrace;
    internal string TraceInputNonce { get; set; } = "";
    public int LastTradeSource { get; private set; }
    public int LastTradeAction { get; private set; }
    private int proofAction;
    private int receivedOptions = -1, receivedIndex = -1;
    private StandardProofNative.TradeRecord pendingTrade;
    private readonly Dictionary<int, Dictionary<int, int>> proofHistoricalLiveTags = new Dictionary<int, Dictionary<int, int>>();
    private readonly ProofBot proofBot = new ProofBot();
    internal bool IsProofMatch => StandardProofNative.IsProof(playerDeck?.Id);
    internal readonly ProofBot HumanProofBot = new ProofBot();
    internal int ProofOptionIndex(BotAction action)
    {
        int keyword = action.Type == PlayerTaskType.TRADE_CARD ? 1720 : action.Type == PlayerTaskType.PREPARE_CARD ? 4354 : 0;
        int index = humanOptions?.PowerOptionList.FindIndex(option => option.MainOption?.EntityId == action.Source
            && option.MainOption.PowerKeyword == keyword) ?? -1;
        if (index < 0) throw new InvalidOperationException("Advice option/keyword is not synchronized.");
        return index;
    }
#endif
    private NpcProfile npcProfile;
    private IGoldenPilot npcPilot;
    private NpcTurnRunner npcRunner;
    private NpcPresentation npcPresentation;
    private bool npcConcessionPending;
    // Rivals-lite: the NPC's ConcedeTask waits (bounded) for the second emote of a rival farewell. The concession is already
    // decided and the runner cancelled, so the hold never reaches the pilot, its search budget or its turn clock.
    internal const double ConcessionFarewellHoldMilliseconds = 3000;
    private double npcConcessionQueuedAt;
    private long? npcLastPublicValue;
    private long npcTurnStartValue;
    private bool npcBigTurnSeen;
    private readonly NpcPublicTurn npcHumanTurn = new NpcPublicTurn();
    public int? NpcTurnNodes { get; set; }
    public NpcPresentationSettings PresentationSettings { get; set; } = new NpcPresentationSettings();
    public Func<PegasusGame.UserUI, bool> NativeNpcUserUI { get; set; }
    public Func<bool> NativeNpcStartingTaunt { get; set; }
    public bool OpponentConceded { get; private set; }
    public bool HumanConceded { get; private set; }
    private int npcTurn = -1, recorderOptionIndex = -1, publicEventIndex;
    private bool npcStopped, resultRecorded;
    private LocalAchievementMatchEvidence achievementEvidence;
    private readonly MatchRecorder matchRecorder;
    private GoldenDecision npcDiagnosticDecision;
    public IMatchRecorder Recorder { get => matchRecorder.Inner; set => matchRecorder.Inner = value ?? NullMatchRecorder.Instance; }
    public Func<EngineGame, string> ReplayFingerprint { get => matchRecorder.ReplayHash; set => matchRecorder.ReplayHash = value ?? MatchRecorder.Fingerprint; }
    public string MatchId { get; set; } = "headless";
    public bool SynchronousNpc { get; set; }
    internal Func<double> NpcClock { get; set; } = () => MonotonicMilliseconds;
    internal Func<bool> NativeNpcIdle { get; set; }
    // Diagnostic only: which native condition kept the board busy when an NPC idle wait hit its guard.
    internal Func<string> NativeNpcBusyReason { get; set; }
    // Native squelch (EnemyEmoteHandler) of the NPC; never blocks the human's own emotes, unlike EmotesSquelched.
    internal Func<bool> NativeNpcSquelched { get; set; }
    private bool NpcEmotesSquelched
    {
        get
        {
            if (EmotesSquelched) return true;
            try { return NativeNpcSquelched?.Invoke() == true; } catch (Exception) { return false; }
        }
    }
    // The opponent's name for the native name plate: the NPC's DisplayName, else LocalDeck.Opponent.
    public string OpponentName { get; private set; }
    // Rivals-lite: the NPC's memory of the player (settled head-to-head, set before Start; null = first meeting or none).
    // Presentation only: it feeds OpponentPlateName and the NPC's emote choice, never the engine, pilot or decks.
    public NpcMemory NpcMemory { get; set; }
    // The native name plate string: OpponentName plus the head-to-head record. Engine Player2Name stays OpponentName.
    public string OpponentPlateName { get; private set; }
    internal static NpcMemory MemoryFrom(LocalNpcRecord record) => record == null || record.Games == 0 ? null
        : new NpcMemory(record.Wins, record.Losses, record.Ties,
            record.LastResult == "Won" ? NpcLastMeeting.PlayerWon : record.LastResult == "Tied" ? NpcLastMeeting.Tied : NpcLastMeeting.PlayerLost,
            record.IsRival);
    internal NpcTurnRunner NpcRunner => npcRunner;
    internal int NpcPublicEventIndex => publicEventIndex;
    public void StopNpc() { HideNpcRope(); npcStopped = true; npcConcessionPending = false; npcRunner?.Cancel(); npcPresentation?.Cancel(); }

    public int BotNodes { get; private set; }
    public int BotFallbacks { get; private set; }
    public long BotMilliseconds { get; private set; }

#if !ENGINE_SOURCE
    internal int WireReturnId(int id) => id;
    internal int EngineReturnId(int id) => id;
#endif

    public NativeMatch(Action<int, IProtoBuf> send)
    {
        if (send == null) throw new ArgumentNullException(nameof(send));
        this.send = (id, packet) => {
#if ENGINE_SOURCE
            RemapPrivateReturnPacket(packet);
#endif
            send(id, packet);
        };
        seed = 1337;
        matchRecorder = new MatchRecorder(line => BotTrace?.Invoke(line));
    }

    public NativeMatch(Action<int, IProtoBuf> send, string playerDeck, string opponentDeck, int seed, int startingStars = 0) : this(send)
    {
#if STANDARD_PROOF
        if ((StandardProofNative.IsProof(playerDeck) || StandardProofNative.IsProof(opponentDeck))
            && (!StandardProofNative.IsProof(playerDeck) || opponentDeck != StandardProofNative.Opponent(playerDeck) || seed != 0))
            throw new ArgumentException("Proof match requires opposite exact deck and qualified seed0.");
#endif
        this.playerDeck = LocalDecks.Get(playerDeck);
        this.opponentDeck = LocalDecks.Get(opponentDeck);
        playerHero = ResolveHero(this.playerDeck.Hero);
        playerCards = this.playerDeck.Build();
        this.seed = seed;
        this.startingStars = startingStars;
    }

    public NativeMatch(Action<int, IProtoBuf> send, LocalDeck player, string opponent, int seed, int startingStars = 0,
        IEnumerable<LocalDeckCard> cardEntries = null, bool dungeon = false) : this(send)
    {
        if (player == null) throw new ArgumentNullException(nameof(player));
        if (dungeon && !player.DungeonDeck)
            throw new InvalidOperationException("Only validated temporary dungeon decks can bypass constructed rules.");
        // Regular custom decks keep the exact old ownership, capacity and validation path.
        // Dungeon decks are never stored in the account collection and can grow after draft rewards.
        playerDeck = dungeon ? player : LocalProfile.PlayableDeck(player.Name, player.Hero, player.CardIds,
            player.Sideboards.Select(row => new LocalSideboard { Source = row.Key, Cards = row.Value.ToList() }));
        opponentDeck = LocalDecks.Get(opponent);
        playerHero = ResolveHero(playerDeck.Hero);
        playerCards = playerDeck.Build();
        premiumDeckCards = PremiumEntries(playerCards.Select(card => card.Id), cardEntries);
        this.seed = seed;
        this.startingStars = startingStars;
    }

    internal NativeMatch(Action<int, IProtoBuf> send, EngineCard fixtureHero, IEnumerable<EngineCard> fixtureCards, int fixtureSeed) : this(send)
    {
        if (fixtureHero == null || fixtureHero.Type != CardType.HERO)
            throw new ArgumentException("Fixture hero is invalid.", nameof(fixtureHero));
        var cards = fixtureCards == null ? null : fixtureCards.ToList();
        if (cards == null || cards.Count != 30 || cards.Any(card => card == null || !LocalCardCatalog.Current.Any(known => known.Id == card.Id)
            || (card.Class != CardClass.NEUTRAL && card.Class != fixtureHero.Class)))
            throw new ArgumentException("Fixture cards must be 30 supported class-correct cards.", nameof(fixtureCards));
        playerHero = fixtureHero;
        playerCards = cards;
        seed = fixtureSeed;
    }

    // One-shot debug scenario: opening overrides before offers; remaining settings after both mulligans.
    internal DebugScenario DebugScenario { get; set; }
    internal Action<string> DebugScenarioLog { get; set; }
    internal string DebugScenarioApplied { get; private set; }
#if ENGINE_SOURCE
    // Called right after StartGame, BEFORE anything is recorded: a scenario that cannot fit (deck or hand room) is dropped whole,
    // so neither the game nor the playtest header (written at the first recorded action) ever mentions it.
    private void ValidateDebugScenario()
    {
        if (DebugScenario == null) return;
        string problem;
        try { problem = DebugScenario.Validate(Game); } catch (Exception error) { problem = error.Message; }
        if (problem == null) return;
        global::DebugScenario.RecordOutcome("aborted", problem, DebugScenario.BoundRunId ?? global::DebugScenario.RunId);
        DebugScenario = null;
        try { DebugScenarioLog?.Invoke("DEBUG_SCENARIO aborted, nothing applied: " + problem); } catch (Exception) { }
    }
    private void ApplyDebugScenario()
    {
        if (DebugScenario == null || DebugScenarioApplied != null) return;
        string status = "applied";
        try { DebugScenarioApplied = DebugScenario.Apply(Game); }   // validates first: it fails before it changes anything
        catch (Exception error) { status = "aborted"; DebugScenarioApplied = "FAILED " + error.GetType().Name + ": " + error.Message; }
        global::DebugScenario.RecordOutcome(status, DebugScenarioApplied, DebugScenario.BoundRunId ?? global::DebugScenario.RunId);
        try { DebugScenarioLog?.Invoke("DEBUG_SCENARIO " + status + " " + DebugScenarioApplied); } catch (Exception) { }
    }
#endif
    public EngineGame Game { get; private set; }
    public string EngineStartHero { get; private set; }
    public string EngineStartOpponentHero { get; private set; }
    public string EngineStartCards { get; private set; }
    public int EngineStartCount { get; private set; }
    public int Seed => seed;
    // The pending record supplies this on launch/replay; null rolls only for a new standalone match.
    internal int? StartingPlayer { get; set; }
    public int FirstPlayer => Game?.FirstPlayer?.PlayerId ?? StartingPlayer ?? StableSeed.MatchFirstPlayer(seed);
    // The back draws from its own stream; DeckPet seeds Random with the raw match seed, so sharing it would pair pet and back.
    internal static int CoinSeed(int seed) => StableSeed.Derive(seed, 0x434F494E /* COIN */);
    internal static int CardBackSeed(int seed) => StableSeed.Derive(seed, 0x4241434B /* BACK */);
    public int HumanOptionsId => humanOptions?.Index ?? -1;
    internal int HumanChoiceId => humanChoices?.Index ?? -1;
    public LocalRewardFacts RewardFacts => new LocalRewardFacts {
        DistinctCardsPlayed = playedCardIds.Count,
        EnemyMinionsDefeated = defeatedEnemyMinions.Count,
        MinimumHealth = minimumHealth
    };
    internal static bool SupportsAchievementEvidence => LocalAchievementCollector.SupportsAuthoritativeFacts;

    internal LocalAchievementMatchEvidence AchievementEvidence(LocalAchievementPendingContext pending)
    {
        if (!SupportsAchievementEvidence) return null;
        if (Game == null || Game.State != State.COMPLETE)
            throw new InvalidOperationException("Achievement evidence is available only after a terminal result.");
        if (pending == null || !string.Equals(pending.MatchId, MatchId, StringComparison.Ordinal))
            throw new InvalidOperationException("Achievement pending context does not match the native match.");
        if (achievementEvidence == null)
            achievementEvidence = LocalAchievementCollector.From(Game, Game.Player1.Id, pending)
                .WithConcessions(HumanConceded, OpponentConceded);
        else if (!string.Equals(achievementEvidence.ContextBinding, LocalAchievementContextBinding.Create(pending), StringComparison.Ordinal))
            throw new InvalidOperationException("Achievement retry context changed after terminal collection.");
        return achievementEvidence;
    }

    internal static Func<LocalProfile> CosmeticsProfile { get; set; }
    // The personal deck whose own card back / hero skin (deck tray cosmetics) override the profile favourites.
    internal long? CosmeticDeckId { get; set; }
    internal static Func<string, int> NativePetVariant { get; set; }
    private string cosmeticPet;
    private int cosmeticPetVariant, cosmeticPetEntityId;
    // The NPC opponent's own pet (PROPOSED, docs/design/npc-pets.md): wire-only, resolved by the caller from the NPC's
    // identity. It never touches the pending record, profile, pet progress or any ownership.
    internal string OpponentPetCard { get; set; }
    private int opponentPetVariant, opponentPetEntityId, opponentPetFamilyId, cosmeticPetFamilyId;
    // Pet treats (retail "Level B", docs/design/pet-xp.md): the pinned client asks for them while NetCacheFeatures allows it
    // (NativeUIProbe sets MaxPetTreatsAllowedPerGame). The counters mirror retail's server tags on the human pet.
    // Presentation state only: treats never touch ownership or the profile. Their sole effect is one XP tick per feed.
    internal const int MaxPetTreatsPerGame = PetXpPolicy.MaxTreats;
    private int treatsGenerated, treatsFed;
    // Pet XP likes and treat ticks. Exists only when the equipped pet has a durable XP recipient (pending PetXpBefore) and
    // the policy uses counters; otherwise no counter tag is ever sent and settlement records nothing.
    private NativePetLikes petLikes;
    private bool petLikesFaulted;
    // The per-match XP events for settlement (null when there was no recipient).
    internal LocalPetXpCounts PetXpCounts => petLikes == null ? null
        : new LocalPetXpCounts { Small = petLikes.Small, Medium = petLikes.Medium, Large = petLikes.Large };
    internal NativePetLikes PetLikes => petLikes;
    private int? cosmeticCardBack;
    private int cosmeticCoin = 1, cosmeticCoinEntityId;
    private string cosmeticCoinCardId;
    internal int CosmeticCoin => cosmeticCoin;
    internal int CosmeticCoinEntityId => cosmeticCoinEntityId;
    internal string CosmeticCoinCardId => cosmeticCoinCardId;
    // NativeMatch stays client-free (proof/transport builds compile it): ClientBridge installs the DBF lookup.
    internal static Func<int, string> NativeCoinCard = _ => null;

    private void IdentifyStartingCoin()
    {
        // Only the engine's second-player gift, before any later generated cards can exist.
        cosmeticCoinEntityId = Game.FirstPlayer.PlayerId == HumanPlayerId ? 0 : Game.Player1.HandZone
            .Where(card => card.Card.Id == "GAME_005" && card[GameTag.CREATOR] == HumanPlayerId)
            .Select(card => card.Id).SingleOrDefault();
    }

    internal string TransportCardId(int entityId, string cardId)
    {
        if (cardId != "GAME_005" || cosmeticCoinEntityId <= 0 || string.IsNullOrEmpty(cosmeticCoinCardId)) return TransportCardId(cardId);
#if ENGINE_SOURCE
        if (deckChoiceOriginals.TryGetValue(entityId, out int original)) entityId = original;
        // Bound offers display originals except Intertwined Fate, which binds its new-copy previews too.
        if (Game != null && Game.IdEntityDic.TryGetValue(entityId, out var offer) && offer.Zone?.Type == EngineZone.SETASIDE
            && offer[SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag] > 0
            && offer[SabberStoneCore.Actions.DiscoverRealRules.BoundEntity] > 0
            && Game.IdEntityDic.TryGetValue(offer[GameTag.CREATOR], out var source) && source.Card.Id != "TIME_432")
            entityId = offer[SabberStoneCore.Actions.DiscoverRealRules.BoundEntity];
#endif
        return entityId == cosmeticCoinEntityId ? cosmeticCoinCardId : TransportCardId(cardId);
    }

    internal bool IsMappedStartingCoin(int entityId, string cardId) => cosmeticCoinEntityId > 0
        && entityId == cosmeticCoinEntityId && !string.IsNullOrEmpty(cosmeticCoinCardId) && cardId == cosmeticCoinCardId
        && Game != null && Game.IdEntityDic.TryGetValue(entityId, out var entity) && entity.Card.Id == "GAME_005";

    private string cosmeticHero, cosmeticBaseHero;
    private int cosmeticHeroPremium;
    // Frozen seat definitions + each history entity's own controller/name survive replacement and Rewind.
    private readonly Dictionary<int, Dictionary<string, string>> cosmeticPowerCards = new Dictionary<int, Dictionary<string, string>>();
    private readonly Dictionary<int, int> powerControllers = new Dictionary<int, int>();
    // Transfer is permanent presentation history, including transfers on an abandoned Rewind timeline.
    private readonly HashSet<int> transferredPowers = new HashSet<int>();
    private readonly LocalDeckCard[] premiumDeckCards;
    // The NPC whose deck copies show premium finishes for its displayed rank; null = all Normal (legacy, fixtures).
    internal NpcPremiumSeat OpponentPremiumSeat { get; set; }
    // Presentation belongs to original entity IDs, never shared definitions or engine copy state.
    private readonly Dictionary<int, int> entityPremiums = new Dictionary<int, int>();
    // Retail: a created card takes the highest quality among the cards that created it; draws and moves never change it.
    // Values are already resolved against the result card. Either side's premium cards credit either side's creations.
    private readonly Dictionary<int, int> derivedPremiums = new Dictionary<int, int>();
    private readonly Stack<KeyValuePair<int, int>> premiumBlocks = new Stack<KeyValuePair<int, int>>();

    public void Start()
    {
        if (Game != null)
            throw new InvalidOperationException("Match already started.");

        if (StartingPlayer.HasValue && StartingPlayer != 1 && StartingPlayer != 2)
            throw new InvalidOperationException("Starting player must be 1 or 2.");
        var cosmetics = CosmeticsProfile?.Invoke();
        if (cosmetics != null && playerDeck != null)
        {
            var pendingCosmetics = cosmetics.Pending;
            cosmeticPet = pendingCosmetics != null ? pendingCosmetics.Pet
                : CosmeticDeckId is long petDeckId ? cosmetics.DeckPet("custom:" + petDeckId, seed) : cosmetics.EffectiveSelectedPet;
            if (cosmeticPet != null) {
                cosmeticPetVariant = NativePetVariant?.Invoke(cosmeticPet) ?? throw new InvalidOperationException("Native pet mapping unavailable.");
                if (cosmeticPetVariant <= 0) throw new InvalidOperationException("Native pet variant unavailable.");
                cosmeticPetEntityId = nextDeckChoiceId--;
                cosmeticPetFamilyId = LocalShop.Default.PetNativeId(LocalShop.Default.PetFamily(cosmeticPet));
                treatsGenerated = 1;
                if (pendingCosmetics != null && pendingCosmetics.PetXpBefore.HasValue && PetXpPolicy.Current.UsesCounters)
                    petLikes = new NativePetLikes(cosmeticPetFamilyId, PetXpPolicy.Current);
            }
            var pendingCoin = pendingCosmetics?.CosmeticCoin;
            cosmeticCoin = pendingCoin ?? (CosmeticDeckId is long coinDeckId
                ? cosmetics.DeckCoin(coinDeckId, new Random(CoinSeed(seed))) : cosmetics.MatchCoin(new Random(CoinSeed(seed))));
            cosmeticCoinCardId = pendingCoin.HasValue ? pendingCosmetics.CosmeticCoinCardId
                : cosmeticCoin == 1 ? null : NativeCoinCard?.Invoke(cosmeticCoin);
            if (string.IsNullOrEmpty(cosmeticCoinCardId) || cosmeticCoinCardId == "GAME_005") cosmeticCoinCardId = null;
            if (pendingCosmetics != null) cosmetics.SnapshotMatchCoin(cosmeticCoin, cosmeticCoinCardId);
            cosmeticBaseHero = playerDeck.Hero;
            cosmeticHero = cosmetics.DeckHero(CosmeticDeckId, cosmeticBaseHero);
            if (CosmeticDeckId is long deckId)
            {
                cosmeticCardBack = cosmetics.DeckCardBack(deckId, new Random(CardBackSeed(seed)));
            }
            else
            {
                cosmeticCardBack = cosmetics.MatchCardBack(new Random(CardBackSeed(seed)));
            }
            // Presentation only: a premium lookup failure shows the Normal portrait instead of refusing the match.
            try { cosmeticHeroPremium = cosmetics.HeroPremium(cosmeticBaseHero, cosmeticHero); }
            catch (Exception error) { cosmeticHeroPremium = 0; BotTrace?.Invoke("premium hero fallback: " + error.Message); }
        }
        if (OpponentPetCard != null)
        {
            // Allocated after the human pet so its id never shifts. A missing native mapping only hides the NPC's pet.
            int variant = 0;
            int family = 0;
            // Both lookups sit in the same guard: a card the shop cannot resolve hides the NPC pet, it never fails match start
            // or the CreateGame packet.
            try
            {
                variant = NativePetVariant?.Invoke(OpponentPetCard) ?? 0;
                family = LocalShop.Default.PetNativeId(LocalShop.Default.PetFamily(OpponentPetCard));
            }
            catch (Exception error) { variant = 0; BotTrace?.Invoke("opponent pet unavailable: " + error.Message); }
            if (variant > 0 && family > 0) { opponentPetVariant = variant; opponentPetFamilyId = family; opponentPetEntityId = nextDeckChoiceId--; }
            else OpponentPetCard = null;
        }
        npcProfile = NpcCatalog.ForDeck(opponentDeck?.Id);
        if (npcProfile != null) npcPilot = NpcPilots.Create(npcProfile, line => BotTrace?.Invoke(line));
        EngineCard hero = playerHero ?? ResolveHero("HERO_08");
        EngineCard ordinaryBotHero = ResolveHero(opponentDeck == null ? "HERO_08" : opponentDeck.Hero);
        EngineCard botHero = ResolveHero(DebugScenario?.NpcSkin != null ? HeroSkinData.All[DebugScenario.NpcSkin].BaseHero
            : ordinaryBotHero.Id);
        var humanCards = playerCards == null ? BuildDeck() : new List<EngineCard>(playerCards);
        var botCards = opponentDeck == null ? BuildDeck() : opponentDeck.Build();
        int firstPlayer = StartingPlayer ?? StableSeed.MatchFirstPlayer(seed);
#if MATCH_TESTS
        // Preserve legacy fixtures' exact raw StartPlayer; production-path seat tests clear the fixture override.
        if (!StableSeed.TestFirstPlayer.HasValue)
#endif
            firstPlayer = StableSeed.ApplyFirstPlayerRules(firstPlayer, humanCards.Select(card => card.Id), botCards.Select(card => card.Id));
        var config = new GameConfig
        {
            Player1Name = "Player",
            Player2Name = OpponentName = opponentDeck == null ? "Innkeeper" : OpponentDisplayName(npcProfile, opponentDeck),
            Player1HeroClass = hero.Class,
            Player1HeroCard = hero,
            Player1Deck = humanCards,
            Player2HeroClass = botHero.Class,
            Player2HeroCard = botHero,
            Player2Deck = botCards,
            FormatType = FormatType.FT_WILD,
            StartPlayer = firstPlayer,
            Shuffle = true,
            RandomSeed = seed,
            SkipMulligan = false,
            History = true,
#if ENGINE_SOURCE
            EnableCorpses = true,
            Player1Sideboards = playerDeck?.Sideboards,
            Player2Sideboards = opponentDeck?.Sideboards,
#endif
            Logging = false
        };
        Game = new EngineGame(config);
        OpponentPlateName = NpcMemoryText.Plate(OpponentName, npcProfile != null ? NpcMemory : null);

        if (premiumDeckCards != null)
        {
            var premiums = premiumDeckCards.GroupBy(card => card.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(card => card.Premium)), StringComparer.Ordinal);
            // Game constructs original deck entities before StartGame shuffles or runs effects.
            foreach (var card in Game.Player1.DeckZone.ToArray())
                if (premiums.TryGetValue(card.Card.Id, out var copies) && copies.Count > 0
                    && copies.Dequeue() is int premium && premium != 0)
                    entityPremiums.Add(card.Id, premium);
        }
        if (OpponentPremiumSeat is NpcPremiumSeat seat)
        {
            // Presentation only: a lookup failure shows the NPC's cards Normal instead of refusing the match.
            try
            {
                var deck = Game.Player2.DeckZone.ToArray();
                int[] premiums = NpcPremiums.Assign(seat.NpcId, seat.DeckId, seat.StarIndex, seat.PersonalityId,
                    deck.Select(card => card.Card.Id).ToArray(), id => ResolvePremium(2, LookupCard(id)));
                for (int i = 0; i < deck.Length; i++)
                    if (premiums[i] != 0) entityPremiums.Add(deck[i].Id, premiums[i]);
            }
            catch (Exception error) { BotTrace?.Invoke("npc premium fallback: " + error.Message); }
        }
        Game.StartGame();
#if ENGINE_SOURCE
        ValidateDebugScenario();
        DebugScenario?.ApplyOpening(Game);
        if (DebugScenario == null && botHero.Id != ordinaryBotHero.Id)
        {
            // Rejected scenarios must not leave an NPC class override behind. Nothing has been sent/recorded yet;
            // recreate from the same seed/decks, whose original entity IDs also preserve the premium assignments.
            config.Player2HeroCard = ordinaryBotHero; config.Player2HeroClass = ordinaryBotHero.Class;
            Game = new EngineGame(config);
            Game.StartGame();
        }
#endif
        IdentifyStartingCoin();
        if (npcProfile != null)
            npcPresentation = new NpcPresentation(npcProfile, PresentationSettings, MatchId, Game.Player2.PlayerId,
                () => NpcClock(), () => NpcEmotesSquelched, SendNpcPresentation, NpcMemory);
        CaptureCosmeticPowerCards();
        EngineStartHero = Game.Player1.Hero.Card.Id;
        EngineStartOpponentHero = Game.Player2.Hero.Card.Id;
        string[] initialCards = Game.Player1.DeckZone.Concat(Game.Player1.HandZone)
            .Where(card => card.Card.Id != "GAME_005").Select(card => card.Card.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        EngineStartCards = string.Join(",", initialCards);
        EngineStartCount = initialCards.Length;
        ObserveRewardHistory(Game.PowerHistory.Last);
        SendHistory(Game.PowerHistory.Last);

        if (Game.Player2.Choice == null || Game.Player2.Choice.ChoiceType != ChoiceType.MULLIGAN)
            throw new InvalidOperationException("Bot mulligan was not created.");
        PowerEntityChoices botChoices = PowerChoicesBuilder.EntityChoices(Game, Game.Player2.Choice);
#if ENGINE_SOURCE
        var botDecision = Game.GetPendingDecisions().Single(d => d.ControllerId == Game.Player2.Id);
#endif
        var npc = NpcCatalog.ForDeck(opponentDeck?.Id);
        // A world profile whose plan does not resolve keeps an empty mulligan (and the legacy search).
        var botKeeps = npc == null || DeckPlans.ForId(npc.PlanId) == null ? new List<int>() : new List<int>(BotDriver.Mulligan(
            new MulliganView(Game.Player2.Choice.Choices.Select(id => new MulliganCard(id,
                Game.IdEntityDic[id].Card.Id, Game.IdEntityDic[id].Card.Cost)), Game.FirstPlayer == Game.Player1,
                opponentDeck.CardIds, Game.Player1.Hero.Card.Class), BotMulliganPolicy(npc)));
        send(17, ToNativeChoice(botChoices));
#if ENGINE_SOURCE
        AnswerDecision(botDecision.Id, botDecision.ControllerId, botKeeps);
#else
        Process(ChooseTask.Mulligan(Game.Player2, botKeeps));
#endif
        SendOpposingChosen(botChoices, botKeeps);
        SendHistory(Game.PowerHistory.Last);
        SendHumanChoice();
    }

    internal PlayerTask ValidateOption(int id, int index, int target, int sub, int pos)
    {
        TickTurnTimer();
        if (expiredHumanOptions.Contains(id)) throw new TurnExpiredException();
        RequireHumanInput();
        if (humanChoices != null)
            throw new InvalidOperationException("Entity choice is pending.");
        if (humanOptions == null || humanOptions.Index != id)
            throw new InvalidOperationException("Option set is stale or unknown.");
        if (index < 0 || index >= humanOptions.PowerOptionList.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

#if ENGINE_SOURCE
        target = EngineReturnId(target);
#endif
        return ResolveTask(humanOptions, index, target, sub, pos);
    }

    public void SendOption(int id, int index, int target, int sub, int pos)
    {
        PlayerTask task = ValidateOption(id, index, target, sub, pos);
#if STANDARD_PROOF
        receivedOptions = id; receivedIndex = index;
#endif
        NoteHumanTimerAction();
        humanOptions = null;
        recorderOptionIndex = index;
        try { Process(task); }
        finally { recorderOptionIndex = -1; }
#if STANDARD_PROOF
        receivedOptions = -1; receivedIndex = -1;
#endif
        SendHistory(Game.PowerHistory.Last);
        DriveUntilHumanInput();
    }

    public void SendChoices(int id, List<int> picks)
    {
        TickTurnTimer();
        if (expiredHumanChoices.Contains(id)) throw new TurnExpiredException();
#if ENGINE_SOURCE
        if (picks != null) picks = picks.Select(EngineReturnId).ToList();
#endif
        CompleteHumanChoice(id, picks, drive: true);
    }

    private void CompleteHumanChoice(int id, List<int> picks, bool drive)
    {
        RequireHumanInput();
        if (picks == null)
            throw new ArgumentNullException(nameof(picks));
        if (humanChoices == null || humanChoices.Index != id)
            throw new InvalidOperationException("Choice set is stale or unknown.");
        if (picks.Count < humanChoices.CountMin || picks.Count > humanChoices.CountMax ||
            picks.Distinct().Count() != picks.Count || picks.Any(p => !humanChoices.Entities.Contains(p)))
            throw new ArgumentException("Choice selection is not legal.", nameof(picks));

        if (drive) NoteHumanTimerAction();
        PowerEntityChoices choices = humanChoices;
        IEnumerable<int> acknowledged = picks;
        var retainedPicks = new HashSet<int>();
#if ENGINE_SOURCE
        var rewind = Game.Rewind;
        // Rewind's own timeline cards belong to RewindUIManager and the reset graph.
        if (rewind != null) { retainedPicks.Add(rewind.RewindEntityId); retainedPicks.Add(rewind.KeepEntityId); }
        var proxyPicks = new HashSet<int>(picks.Where(deckChoiceOriginals.ContainsKey));
        AnswerDecision(humanDecisionId, humanDecisionController,
            picks.Select(p => deckChoiceOriginals.TryGetValue(p, out int original) ? original : p).ToList());
        deckChoiceOriginals.Clear();
        // Preserve gift visibility for recorded hand deliveries (including Sweet Dreams), never burns.
        privateDeckOffers.IntersectWith(Game.PowerHistory.Last.OfType<PowerHistoryShowEntity>()
            .Where(show => TagValue(show.Entity.Tags, GameTag.ZONE) == (int)EngineZone.HAND).Select(show => show.Entity.Id));
        // HideChoices resumes an executing Rewind only for TIME_000ta; keep-both retains this timeline without GAME_RESET.
        if (rewind is { KeepBoth: true } && picks.Count == 1 && picks[0] == rewind.RewindEntityId)
            acknowledged = new[] { rewind.KeepEntityId };
#else
        ChoiceType type = choices.ChoiceType;
        PlayerTask task = type == ChoiceType.MULLIGAN
            ? (PlayerTask)ChooseTask.Mulligan(Game.Player1, new List<int>(picks))
            : ChooseTask.Pick(Game.Player1, picks[0]);
        Process(task);
#endif
        humanChoices = null;
        SendChosen(choices, acknowledged);

        if (Game.Step == Step.BEGIN_MULLIGAN &&
            Game.Player1.MulliganState == Mulligan.DONE && Game.Player2.MulliganState == Mulligan.DONE)
        {
#if ENGINE_SOURCE
            ApplyDebugScenario();
#endif
            Game.MainBegin();
            RecordedAction("main-begin");
        }

        SendHistory(Game.PowerHistory.Last);
#if ENGINE_SOURCE
        RetireChosenChoiceActors(choices, picks, proxyPicks, retainedPicks);
        privateDeckOffers.Clear();
#else
        RetireChosenChoiceActors(choices, picks, new HashSet<int>(), retainedPicks);
#endif
        if (drive) DriveUntilHumanInput();
    }

    // ChoiceCardMgr keeps the chosen GENERAL card on screen (HideChosen=false) until a zone change moves it.
    // Discover-to-hand moves it; Adapt-style option cards and deck-Discover proxies stay SETASIDE, which has no
    // client Zone, so their big card would linger. Retire them like retail spent entities: ZONE=REMOVEDFROMGAME
    // queues GameState.RemoveEntity and destroys the Card.
    private readonly HashSet<int> retiredChoiceActors = new HashSet<int>();

    private void RetireChosenChoiceActors(PowerEntityChoices choices, IEnumerable<int> picks, ISet<int> proxies, ISet<int> retained)
    {
        if (choices.ChoiceType != ChoiceType.GENERAL || Game.State == State.COMPLETE) return;
        var packet = new NativePowerHistory();
        foreach (int id in picks.Distinct())
        {
            bool lingering = proxies.Contains(id) || !retained.Contains(id)
                && Game.IdEntityDic.TryGetValue(id, out var chosen) && chosen.Zone?.Type == EngineZone.SETASIDE
                && Game.Player1.Choice?.Choices?.Contains(id) != true && Game.Player2.Choice?.Choices?.Contains(id) != true;
            if (!lingering || !retiredChoiceActors.Add(id)) continue;
            var retire = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = id, Tag = (int)GameTag.ZONE, Value = (int)EngineZone.REMOVEDFROMGAME
            } };
            packet.List.Add(retire);
            TrackVisibility(retire);
        }
        SendPowerHistory(packet);
    }

    // A retired entity no longer exists on the client; a later engine reference recreates it hidden-in-place first.
    private void RestoreRetiredChoiceActor(NativePowerHistory packet, IPowerHistoryEntry entry)
    {
        if (retiredChoiceActors.Count == 0) return;
        int id = entry is SabberStoneCore.Kettle.PowerHistoryTagChange tag ? tag.EntityId
            : entry is PowerHistoryShowEntity show ? show.Entity.Id
            : entry is PowerHistoryChangeEntity change ? change.Entity.Id
            : entry is PowerHistoryHideEntity hide ? hide.EntityID
            : entry is PowerHistoryFullEntity full ? full.Entity.Id
            : entry is PowerHistoryBlockStart block ? block.Source : 0;
        if (!retiredChoiceActors.Remove(id) || entry is PowerHistoryFullEntity || !Game.IdEntityDic.TryGetValue(id, out var entity)) return;
#if ENGINE_SOURCE
        var at = IdentityAt(identityPosition, id, currentFirst: true, allowCurrent: true);
        // Recreation may precede a transform; an unknown retired actor is recreated anonymously.
        var snapshot = new EngineHistoryEntity { Id = id, Name = at?.Name ?? "",
            Tags = at == null ? new Dictionary<GameTag, int>() : new Dictionary<GameTag, int>(at.Tags) };
#else
        var snapshot = PowerHistoryBuilder.ShowEntity(entity).Entity;
#endif
        snapshot.Tags[GameTag.ZONE] = (int)EngineZone.SETASIDE;
        snapshot.Tags[GameTag.ZONE_POSITION] = 0;
        var create = new PowerHistoryData { FullEntity = ToNativeHistoryEntity(snapshot) };
        packet.List.Add(create);
        TrackVisibility(create);
    }

#if ENGINE_SOURCE
    // Retail sends the opponent's GENERAL choice to this client too: ChoiceCardMgr fans the chooser's offers in front of
    // the opposing hero while it decides, then EntitiesChosen conceals them. Offers are listed only as nameless actors
    // (card backs): ids and public zone tags reach the client, never a CARD_ID.
    private PowerEntityChoices opposingChoices;
    private Choice opposingChoiceSource;
    private List<int> opposingChoiceOriginals, opposingPicks;
    // Presentation-only nameless SETASIDE stand-ins (client id -> engine id) for offers outside SETASIDE or already
    // named on the client: ChoiceCardMgr forces hand actors on every offer and hides the unchosen ones.
    private readonly Dictionary<int, int> opposingProxies = new Dictionary<int, int>();
    private double opposingShownAt = -1, opposingRevealedAt = -1, opposingHoldStarted = -1;
    // EntitiesChosen waiting for the client to process its EntityChoices. GameState processes an EntitiesChosen at once
    // when no choice of that player is mapped; arriving before its EntityChoices it would leave that choice mapped
    // forever and queue every later choice (the human's too) behind it. Each entry also retires its spent picks.
    private readonly List<(PowerEntityChoices Choices, List<int> Picks, HashSet<int> Proxies)> deferredOpposing =
        new List<(PowerEntityChoices Choices, List<int> Picks, HashSet<int> Proxies)>();
    internal const double OpposingDealWaitMilliseconds = 3000;
    // Native ChoiceCardMgr has dealt (revealed) the opposing choice with this id. Null headless: shown counts as dealt.
    internal Func<int, bool> NativeOpposingChoiceRevealed { get; set; }
    // Native GameState has processed the opposing EntityChoices with this id. Null headless: sent counts as processed.
    internal Func<int, bool> NativeOpposingChoiceReceived { get; set; }
    internal int OpposingChoiceId => opposingChoices?.Index ?? -1;
    internal int DeferredOpposingChosen => deferredOpposing.Count;

    private bool OpposingChoicePending(out Choice choice)
    {
        choice = Game?.State == State.COMPLETE ? null : Game?.Player2.Choice;
        return choice != null && choice.ChoiceType == ChoiceType.GENERAL && choice.ChoiceAction != ChoiceAction.REWIND
            && choice.Choices != null && choice.Choices.Count > 0 && choice.Choices.All(Game.IdEntityDic.ContainsKey);
    }

    // Called by every answer path of the opponent (task, timeout); EntitiesChosen follows before the answer's history.
    private void NoteOpposingAnswer(int controller, IEnumerable<int> picks)
    {
        if (opposingChoices != null && controller == Game.Player2.Id) opposingPicks = picks.ToList();
    }

    private bool OpposingChoiceReceived(int id)
    {
        if (NativeOpposingChoiceReceived == null) return true;
        try { return NativeOpposingChoiceReceived(id); }
        catch (Exception) { return false; }
    }

    // Before a history batch: an answered or vanished opposing choice is concealed first, as for the human's own.
    private (PowerEntityChoices Choices, List<int> Picks, HashSet<int> Proxies) ConcealOpposingChoice()
    {
        FlushDeferredOpposing();
        if (opposingChoices == null) return (null, null, null);
        bool current = opposingPicks == null && OpposingChoicePending(out var choice) && ReferenceEquals(choice, opposingChoiceSource)
            && choice.Choices.SequenceEqual(opposingChoiceOriginals);
        if (current) return (null, null, null);
        var shown = opposingChoices;
        // An engine id offered twice keeps its first stand-in.
        var toClient = opposingProxies.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);
        var picks = (opposingPicks ?? new List<int>())
            .Select(id => toClient.TryGetValue(id, out int proxy) ? proxy : id).Where(shown.Entities.Contains).Distinct().ToList();
        var proxies = new HashSet<int>(picks.Where(opposingProxies.ContainsKey));
        opposingChoices = null; opposingChoiceSource = null; opposingChoiceOriginals = null; opposingPicks = null;
        opposingProxies.Clear(); opposingShownAt = opposingRevealedAt = opposingHoldStarted = -1;
        // Game over: the native OnGameOver cancels every shown choice itself.
        if (Game.State == State.COMPLETE) { deferredOpposing.Clear(); return (null, null, null); }
        if (deferredOpposing.Count > 0 || !OpposingChoiceReceived(shown.Index))
        {
            deferredOpposing.Add((shown, picks, proxies));
            return (null, null, null);
        }
        SendChosen(shown, picks);
        return (shown, picks, proxies);
    }

    // Each frame and before each history batch: release deferred EntitiesChosen, in order, once the client has
    // processed the matching EntityChoices; then retire their spent picks.
    private void FlushDeferredOpposing()
    {
        if (deferredOpposing.Count == 0) return;
        if (Game == null || Game.State == State.COMPLETE) { deferredOpposing.Clear(); return; }
        while (deferredOpposing.Count > 0 && OpposingChoiceReceived(deferredOpposing[0].Choices.Index))
        {
            var entry = deferredOpposing[0];
            deferredOpposing.RemoveAt(0);
            SendChosen(entry.Choices, entry.Picks);
            RetireChosenChoiceActors(entry.Choices, entry.Picks, entry.Proxies, new HashSet<int>());
        }
    }

    // After a history batch: retire a spent SETASIDE pick (Adapt, refresh token, proxy), then show a pending choice.
    private void PublishOpposingChoice((PowerEntityChoices Choices, List<int> Picks, HashSet<int> Proxies) concealed)
    {
        if (concealed.Choices != null)
            RetireChosenChoiceActors(concealed.Choices, concealed.Picks, concealed.Proxies, new HashSet<int>());
        if (opposingChoices != null || !OpposingChoicePending(out var choice)) return;
        var shown = PowerChoicesBuilder.EntityChoices(Game, choice);
        var proxies = new NativePowerHistory();
        // Hidden identity only: no CARD_ID, definition, creator or link to an original.
        void HiddenActor(int id)
        {
            var create = new PowerHistoryData { FullEntity = new PegasusGame.PowerHistoryEntity { Entity = id, Name = "",
                Tags = new List<NativeTag> {
                    new NativeTag { Name = (int)GameTag.ENTITY_ID, Value = id },
                    new NativeTag { Name = (int)GameTag.CONTROLLER, Value = Game.Player2.PlayerId },
                    new NativeTag { Name = (int)GameTag.ZONE, Value = (int)EngineZone.SETASIDE },
                    new NativeTag { Name = (int)GameTag.ZONE_POSITION, Value = 0 } } } };
            proxies.List.Add(create); TrackVisibility(create);
        }
        for (int i = 0; i < shown.Entities.Count; i++)
        {
            int original = shown.Entities[i];
            bool known = clientTransportCards.TryGetValue(original, out string clientName);
            if (Game.IdEntityDic[original].Zone?.Type == EngineZone.SETASIDE && !(known && !string.IsNullOrEmpty(clientName)))
            {
                // GameState queues EntityChoices until every listed entity exists, and that queue also holds the human's
                // later choices: never list an id the client destroyed (retired) or never received.
                if (retiredChoiceActors.Remove(original) || !known) HiddenActor(original);
                continue;
            }
            int proxy = nextDeckChoiceId--;
            if (proxy <= Game.IdEntityDic.Values.Max(entity => entity.Id)) throw new InvalidOperationException("Choice proxy IDs exhausted.");
            HiddenActor(proxy);
            opposingProxies.Add(proxy, original);
            shown.Entities[i] = proxy;
        }
        SendPowerHistory(proxies);
        opposingChoices = shown; opposingChoiceSource = choice; opposingChoiceOriginals = new List<int>(choice.Choices);
        opposingPicks = null;
        opposingShownAt = NpcClock(); opposingHoldStarted = -1;
        opposingRevealedAt = NativeOpposingChoiceRevealed == null ? opposingShownAt : -1;
        send(17, ToNativeChoice(shown));
    }

    // Native reveal of the opposing backs, polled once per NPC frame while one is pending.
    private void ObserveOpposingChoiceReveal()
    {
        if (opposingChoices == null || opposingRevealedAt >= 0) return;
        bool revealed;
        try { revealed = NativeOpposingChoiceRevealed?.Invoke(opposingChoices.Index) ?? true; }
        catch (Exception) { revealed = true; }
        if (revealed) opposingRevealedAt = NpcClock();
    }

    // Before the NPC commits a pick: wait (outside the runner's 4 s pacing budget) until the client has dealt this
    // choice's backs, at most 3 s from the first time the pick is ready. A late deal is still safe: EntitiesChosen waits.
    internal double OpposingChoiceHold(BotAction action)
    {
        if (opposingChoices == null || action == null || action.Type != PlayerTaskType.CHOOSE) return 0;
        ObserveOpposingChoiceReveal();
        if (opposingRevealedAt >= 0) return 0;
        double now = NpcClock();
        if (opposingHoldStarted < 0) opposingHoldStarted = now;
        return now - opposingHoldStarted < OpposingDealWaitMilliseconds ? 100 : 0;
    }

    // A human-plausible look at the dealt backs before the pick (within the pacing budget); thinking time counts.
    internal static double OpposingChoiceBeatMilliseconds(Personality personality) => personality == null ? 1200
        : Math.Min(2500, Math.Max(1200, 1000 * (personality.ActionMinSeconds + personality.ActionMaxSeconds)));

    internal double OpposingChoiceDwell(BotAction action)
    {
        if (opposingChoices == null || action == null || action.Type != PlayerTaskType.CHOOSE) return 0;
        ObserveOpposingChoiceReveal();
        if (opposingRevealedAt < 0) return 0;
        return Math.Max(0, OpposingChoiceBeatMilliseconds(npcProfile?.Personality) - (NpcClock() - opposingRevealedAt));
    }
#else
    internal int OpposingChoiceId => -1;
#endif

    public void Concede()
    {
        if (Game == null)
            throw new InvalidOperationException("Match has not started.");
        if (Game.State == State.COMPLETE)
            throw new InvalidOperationException("Match is already complete.");
        npcPresentation?.Queue(NpcReaction.Concession, publicEventIndex, delayMilliseconds: 0);
        npcPresentation?.Tick((NativeNpcIdle?.Invoke() ?? true) && (NativePresentationInputReady?.Invoke() ?? true), false);
        StopNpc();
        HumanConceded = true;
        Process(ConcedeTask.Any(Game.Player1));
        SendHistory(Game.PowerHistory.Last);
    }

    // Test control only (debug instant win): the NPC concedes now on any turn, recorded as its own concession.
    internal void ConcedeNpcForTest() => EndForTest(Game?.Player2, npc: true);

    // Test control only (debug instant loss): the human loses now as a plain loss. The engine's concede task ends the game,
    // but nothing records a concession (HumanConceded, LocalLadder.MarkConcede), so it settles like a lost game.
    internal void LoseForTest() => EndForTest(Game?.Player1, npc: false);

    // Test control only: seed real corpse-spending evidence; victory, evaluation and grants still run normally.
    internal void PrimeAchievementForTest(string id)
    {
#if ENGINE_SOURCE
        if (id != "death-knight-grave-expenditure") throw new ArgumentException("Unsupported achievement fixture.");
        if (Game == null || Game.State == State.COMPLETE || Game.Step == Step.BEGIN_MULLIGAN || Game.CurrentPlayer != Game.Player1
            || Game.Player1.Hero.Card.Class != CardClass.DEATHKNIGHT || !Game.EnableCorpses)
            throw new InvalidOperationException("Prime Grave Expenditure in a live Death Knight match after mulligan.");
        int missing = Math.Max(0, 30 - Game.HistoryFacts.Records.OfType<CorpseSpentFact>()
            .Where(row => row.ControllerId == Game.Player1.Id).Sum(row => row.Amount));
        if (missing == 0) return;
        Game.PowerHistory.Last.Clear();
        SabberStoneCore.Actions.CorpseRules.Gain(Game.Player1, missing);
        if (!SabberStoneCore.Actions.CorpseRules.TrySpend(Game.Player1, missing))
            throw new InvalidOperationException("Corpse fixture could not spend its corpses.");
        Game.DeathProcessingAndAuraUpdate();
        SendHistory(Game.PowerHistory.Last);
        DriveUntilHumanInput();
#else
        throw new NotSupportedException("Achievement fixtures require EngineSource.");
#endif
    }

    private void EndForTest(Controller loser, bool npc)
    {
        if (Game == null)
            throw new InvalidOperationException("Match has not started.");
        if (Game.State == State.COMPLETE)
            throw new InvalidOperationException("Match is already complete.");
        StopNpc();
        npcConcessionPending = false;
        if (npc) OpponentConceded = true;
        Process(ConcedeTask.Any(loser));
        SendHistory(Game.PowerHistory.Last);
    }

    // Captured when the match is created. Options changes apply to the next match.
    public bool PlayerTurnTimerEnabled { get; set; }
    internal Func<long> TurnTimerClock { get; set; }
    internal Action<string> TimerTrace { get; set; }
    private bool humanActed, humanAfk, humanShortFuse;
    private long humanTurnStarted;
    private int humanTimeoutTag;
    private Random timeoutRandom;
    private readonly HashSet<int> expiredHumanOptions = new HashSet<int>();
    private readonly HashSet<int> expiredHumanChoices = new HashSet<int>();
    private int HumanTurnSeconds => Game.CurrentPlayer[GameTag.TIMEOUT] == 15 ? 15 : Game.Turn <= 2 ? 45 : 75;
    private long TimerNow => TurnTimerClock?.Invoke() ?? MonotonicMilliseconds;

    private void NoteHumanTimerAction()
    {
        if (!PlayerTurnTimerEnabled || Game.CurrentPlayer != Game.Player1 || Game.Step != Step.MAIN_ACTION) return;
        // Accepted input proves playability even if it arrived before the first native readiness sample.
        RefreshTurnDeadline(TimerNow);
        humanActed = true; humanAfk = false;
        if (!humanShortFuse) return;
        humanShortFuse = false;
        turnDeadline = humanTurnStarted + HumanTurnSeconds * 1000L;
        HideNpcRope();
        PublishHumanTimerTag(HumanTurnSeconds);
        send(9, new PegasusGame.TurnTimer { Turn = Game.Turn,
            Seconds = Math.Max(1, (int)((turnDeadline - TimerNow + 999) / 1000)), Show = true });
        ropeTurn = Game.Turn;
    }

    private void PublishHumanTimerTag(int seconds)
    {
        send(19, new NativePowerHistory { List = new List<PowerHistoryData> {
            new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = Game.Player1.Id, Tag = (int)GameTag.TIMEOUT, Value = seconds } }
        } });
    }

    private bool CompleteExpiredHumanChoice(bool random)
    {
        if (humanChoices == null) return false; // Wait for the offer to be published.
        var picks = humanChoices.Entities.Take(humanChoices.CountMin).ToList();
        if (random)
        {
            timeoutRandom = timeoutRandom ?? new Random(Seed ^ 0x71a3);
            var remaining = humanChoices.Entities.ToList();
            picks.Clear();
            while (picks.Count < humanChoices.CountMin)
            {
                int index = timeoutRandom.Next(remaining.Count);
                picks.Add(remaining[index]); remaining.RemoveAt(index);
            }
        }
        CompleteHumanChoice(humanChoices.Index, picks, drive: false);
        return true;
    }

    // Test-control only: one current player turn, no config key and no effect on NPC clocks or later turns.
    internal void ShortenPlayerTurnForTest(int seconds)
    {
        if (seconds < 2 || seconds > 75) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!PlayerTurnTimerEnabled || Game == null || Game.State == State.COMPLETE
            || Game.Step != Step.MAIN_ACTION || Game.CurrentPlayer != Game.Player1
            || NativeHumanInputReady?.Invoke() != true)
            throw new InvalidOperationException("Enable Turn Timer before this match and wait for native player input.");
        long now = TimerNow;
        RefreshTurnDeadline(now);
        HideNpcRope();
        turnDeadline = now + seconds * 1000L;
        PublishHumanTimerTag(seconds);
        TickTurnTimer(now);
    }

    private int timedTurn = -1;
    private long turnDeadline;
    private long npcClockPausedAt = -1;
    private int ropeTurn = -1;
    internal Func<int, bool> NativeNpcTurnSynchronized { get; set; }
    internal Func<bool> NativeHumanInputReady { get; set; }
    internal Func<bool> NativeHumanTimerReady { get; set; }
    internal Func<bool> NativePresentationInputReady { get; set; }
    internal const double OpeningNpcSyncWaitMilliseconds = 30000;
    private double openingNpcWaitStarted = -1;
    private bool openingNpcReady, openingNpcWaiting;
    private static long MonotonicMilliseconds => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));

    private bool OpeningNpcReady()
    {
        if (Game.Turn != 1 || Game.CurrentPlayer != Game.Player2 || openingNpcReady
            || NativeNpcTurnSynchronized == null) return true;
        if (NativeNpcTurnSynchronized(Game.Turn)) return openingNpcReady = true;
        if (openingNpcWaitStarted < 0) openingNpcWaitStarted = NpcClock();
        // A broken strict sync probe may fall back to a settled native board, never an active mulligan screen.
        if (NpcClock() - openingNpcWaitStarted < OpeningNpcSyncWaitMilliseconds
            || NativeNpcIdle?.Invoke() != true || NativePresentationInputReady?.Invoke() != true) return false;
        BotTrace?.Invoke("npc opening-sync-timeout=" + OpeningNpcSyncWaitMilliseconds + " fallback=native-idle-presentation-ready");
        return openingNpcReady = true;
    }

    private void RefreshTurnDeadline(long now)
    {
        if (Game == null || Game.State == State.COMPLETE || Game.Turn <= 0 || Game.Step == Step.BEGIN_MULLIGAN) return;
        if (timedTurn == Game.Turn) return;
        // Opening mulligan/coin presentation must finish before the NPC's first turn deadline begins.
        if (!OpeningNpcReady()) return;
        HideNpcRope();
        timedTurn = Game.Turn;
        npcClockPausedAt = -1;
        humanActed = false;
        humanShortFuse = PlayerTurnTimerEnabled && Game.CurrentPlayer == Game.Player1 && humanAfk;
        humanTurnStarted = now;
        humanTimeoutTag = Game.Player1[GameTag.TIMEOUT];
        turnDeadline = Game.CurrentPlayer == Game.Player1 && PlayerTurnTimerEnabled
            ? now + (humanShortFuse ? 7 : HumanTurnSeconds) * 1000L
            : Game.CurrentPlayer[GameTag.TIMEOUT] == 15 ? now + 15000
            : Game.CurrentPlayer == Game.Player2 ? now + 75000 : long.MaxValue;
        if (Game.CurrentPlayer == Game.Player1 && PlayerTurnTimerEnabled)
        {
            PublishHumanTimerTag(humanShortFuse ? 7 : HumanTurnSeconds);
            TimerTrace?.Invoke("start turn=" + Game.Turn + " seconds=" + (humanShortFuse ? 7 : HumanTurnSeconds));
        }
    }

    private void HideNpcRope()
    {
        if (ropeTurn < 0) return;
        // A zero timer clears client options even when Show=false.
        send(9, new PegasusGame.TurnTimer { Turn = ropeTurn, Seconds = 1, Show = false });
        ropeTurn = -1;
    }

    public void TickTurnTimer() => TickTurnTimer(TimerNow);

    internal void TickTurnTimer(long now)
    {
        if (npcStopped && npcProfile != null) { HideNpcRope(); return; }
        if (Game == null || Game.State == State.COMPLETE) { HideNpcRope(); return; }
        if (Game.Step != Step.MAIN_ACTION) return;
        bool npcTurn = Game.CurrentPlayer == Game.Player2;
        bool shortened = Game.CurrentPlayer[GameTag.TIMEOUT] == 15;
        if (!npcTurn && !PlayerTurnTimerEnabled) HideNpcRope();
        if (!npcTurn && !shortened && !PlayerTurnTimerEnabled) return;
        // Wait only at the start (animation slush). Once armed, animations/menus/choices cannot pause the human clock.
        if (!npcTurn && !PlayerTurnTimerEnabled && NativeHumanInputReady?.Invoke() != true) return;
        if (!npcTurn && PlayerTurnTimerEnabled && timedTurn != Game.Turn
            && (NativeHumanTimerReady ?? NativeHumanInputReady)?.Invoke() != true) return;
        RefreshTurnDeadline(now);
        if (timedTurn != Game.Turn || Game.Step == Step.BEGIN_MULLIGAN) return;

        if (!npcTurn && PlayerTurnTimerEnabled && humanTimeoutTag != Game.Player1[GameTag.TIMEOUT])
        {
            humanTimeoutTag = Game.Player1[GameTag.TIMEOUT];
            turnDeadline = now + (humanShortFuse ? 7 : HumanTurnSeconds) * 1000L;
            HideNpcRope();
            PublishHumanTimerTag(humanShortFuse ? 7 : HumanTurnSeconds);
        }

        // The ordinary NPC clock pauses while the human owns a mandatory choice.
        if (npcTurn && !shortened && Game.Player1.Choice != null)
        {
            if (npcClockPausedAt < 0) npcClockPausedAt = now;
            HideNpcRope();
            return;
        }
        if (npcClockPausedAt >= 0)
        {
            turnDeadline += Math.Max(0, now - npcClockPausedAt);
            npcClockPausedAt = -1;
        }
        if (npcTurn && Game.Player1.Choice != null)
            HideNpcRope();
        else if ((npcTurn || PlayerTurnTimerEnabled) && now < turnDeadline && turnDeadline - now <= 20000
            && ropeTurn != Game.Turn && (!npcTurn || NativeNpcTurnSynchronized?.Invoke(Game.Turn) == true))
        {
            // Whole-second protocol rounds up by less than a second, never ending the display early.
            send(9, new PegasusGame.TurnTimer { Turn = Game.Turn,
                Seconds = (int)((turnDeadline - now + 999) / 1000), Show = true });
            ropeTurn = Game.Turn;
        }
        if (now < turnDeadline
            || ((npcTurn || !PlayerTurnTimerEnabled) && Game.Player1.Choice != null && NativeHumanInputReady?.Invoke() != true)) return;
        int expiredTurn = Game.Turn;
        if (!npcTurn && PlayerTurnTimerEnabled)
        {
            // Remember expired identities, including late native sends after a timer tick already advanced the turn.
            if (humanOptions != null) expiredHumanOptions.Add(humanOptions.Index);
            if (humanChoices != null) expiredHumanChoices.Add(humanChoices.Index);
        }
        npcRunner?.Cancel();
        npcConcessionPending = false;
        npcPresentation?.ClearHover();
#if ENGINE_SOURCE
        // Resolve one mandatory choice per frame; nested decisions cannot extend the expired turn.
        var decision = Game.GetPendingDecisions().FirstOrDefault(d => d.Kind != SabberStoneCore.Model.Decisions.DecisionKind.Options);
        if (decision != null)
        {
            var owner = decision.ControllerId == Game.Player1.Id ? Game.Player1 : Game.Player2;
            if (owner == Game.Player1)
            {
                if (!CompleteExpiredHumanChoice(!npcTurn && PlayerTurnTimerEnabled)) return;
            }
            else
            {
                var choice = PowerChoicesBuilder.EntityChoices(Game, owner.Choice);
                var picks = choice.Entities.Take(choice.CountMin).ToList();
                bool rewind = owner.Choice.ChoiceAction == ChoiceAction.REWIND;
                if (rewind) send(17, ToNativeChoice(choice));
                RecordNpcTimeout(ChooseTask.Mulligan(owner, picks));
                AnswerDecision(decision.Id, decision.ControllerId, picks);
                if (rewind) SendChosen(choice, picks);
                SendHistory(Game.PowerHistory.Last);
                if (rewind) RetireChosenChoiceActors(choice, picks, new HashSet<int>(), new HashSet<int>());
            }
            if (Game.GetPendingDecisions().Any(d => d.Kind != SabberStoneCore.Model.Decisions.DecisionKind.Options))
            {
                if (Game.Player1.Choice != null) SendHumanChoice();
                return;
            }
        }
#else
        if (!npcTurn && PlayerTurnTimerEnabled && Game.Player1.Choice != null)
        {
            if (!CompleteExpiredHumanChoice(random: true)) return;
            if (Game.Player1.Choice != null) { SendHumanChoice(); return; }
        }
#endif
        if (Game.State != State.COMPLETE && Game.Turn == expiredTurn && Game.Step == Step.MAIN_ACTION)
        {
            if (Game.CurrentPlayer == Game.Player1 && PlayerTurnTimerEnabled)
            {
                humanAfk = !humanActed;
                // A zero packet invokes native OnTimeout (clears targeting/options and plays the timeout spell).
                send(9, new PegasusGame.TurnTimer { Turn = Game.Turn, Seconds = 0, Show = true });
                ropeTurn = -1;
                TimerTrace?.Invoke("expired turn=" + Game.Turn + " afk=" + humanAfk);
            }
            humanOptions = null;
            var end = EndTurnTask.Any(Game.CurrentPlayer);
            if (Game.CurrentPlayer == Game.Player2) RecordNpcTimeout(end);
            Process(end);
            SendHistory(Game.PowerHistory.Last);
        }
        DriveUntilHumanInput();
    }

    public enum OfflineEmote { Greetings, WellPlayed, Thanks, Wow, Oops, Threaten, Sorry }
    public bool NativeSorryTransportAvailable => false;
    public IReadOnlyList<OfflineEmote> AvailableEmotes(int controllerId)
    {
        if (Game == null || controllerId != Game.Player1.Id && controllerId != Game.Player2.Id)
            throw new ArgumentOutOfRangeException(nameof(controllerId));
        var owner = controllerId == Game.Player1.Id ? Game.Player1 : Game.Player2;
        bool sorry = false;
#if ENGINE_SOURCE
        sorry = owner[SabberStoneCore.Actions.NativeTailRules.SorryEnabled] != 0;
#endif
        return new[] { OfflineEmote.Greetings, OfflineEmote.WellPlayed, OfflineEmote.Thanks,
            sorry ? OfflineEmote.Sorry : OfflineEmote.Wow, OfflineEmote.Oops, OfflineEmote.Threaten };
    }
    private readonly Dictionary<int, long> lastEmotes = new Dictionary<int, long>();
    public bool EmotesSquelched { get; set; }
    internal bool ReceiveNativeEmote(int nativeEmote)
    {
        OfflineEmote emote;
        switch ((EmoteType)nativeEmote)
        {
            case EmoteType.GREETINGS: emote = OfflineEmote.Greetings; break;
            case EmoteType.WELL_PLAYED: emote = OfflineEmote.WellPlayed; break;
            case EmoteType.THANKS: emote = OfflineEmote.Thanks; break;
            case EmoteType.OOPS: emote = OfflineEmote.Oops; break;
            case EmoteType.SORRY: emote = OfflineEmote.Sorry; break;
            case EmoteType.WOW: emote = OfflineEmote.Wow; break;
            case EmoteType.THREATEN: emote = OfflineEmote.Threaten; break;
            default: return false;
        }
        return Game != null && TryEmote(Game.Player1.Id, emote);
    }
    public bool TryEmote(int controllerId, OfflineEmote emote)
    {
        if (Game == null || Game.State == State.COMPLETE || EmotesSquelched
            || controllerId != Game.Player1.Id && controllerId != Game.Player2.Id) return false;
        long now = MonotonicMilliseconds;
        if (!AvailableEmotes(controllerId).Contains(emote)
            || lastEmotes.TryGetValue(controllerId, out long previous) && now - previous < 1500) return false;
        lastEmotes[controllerId] = now;
        matchRecorder.OnEmote(controllerId, emote.ToString(), publicEventIndex);
        if (controllerId == Game.Player1.Id)
            npcPresentation?.Queue(NpcReaction.ReplyToHumanEmote, publicEventIndex, emote.ToString());
        return true;
    }

    private static List<EngineCard> BuildDeck()
    {
        var deck = new List<EngineCard>(30);
        foreach (string id in DeckCardIds)
        {
            EngineCard card = EngineCards.FromId(id);
            if (card == null)
                throw new InvalidOperationException("Missing fixed deck card " + id + ".");
            deck.Add(card);
            deck.Add(card);
        }
        return deck;
    }

    private static EngineCard ResolveHero(string id) => EngineCards.FromId(id)
        ?? throw new InvalidOperationException("Missing fixed hero " + id + ".");

    private void RequireHumanInput()
    {
#if ENGINE_SOURCE
        if (SabberStoneCore.Actions.AutonomousTurnRules.IsActive(Game))
            throw new InvalidOperationException("Autonomous turn controls input.");
#endif
        if (Game == null)
            throw new InvalidOperationException("Match has not started.");
        if (Game.State == State.COMPLETE)
            throw new InvalidOperationException("Match is complete.");
        if (Game.CurrentPlayer != Game.Player1 && Game.Player1.Choice == null)
            throw new InvalidOperationException("Waiting for bot.");
    }

#if ENGINE_SOURCE
    private void AnswerDecision(long id, int controller, IEnumerable<int> picks)
    {
        var kept = picks.ToArray();
        var owner = controller == Game.Player1.Id ? Game.Player1 : Game.Player2;
        if (matchRecorder.Enabled)
        {
            if (owner.Choice?.ChoiceType == ChoiceType.MULLIGAN) matchRecorder.OnMulligan(owner.PlayerId, kept);
            if (owner == Game.Player2)
                matchRecorder.Write(r => (r as PlaytestRecord)?.OnNpcAction(Game.Turn,
                    BotAction.From(ChooseTask.Mulligan(owner, kept.ToList())).ToString()));
            if (owner == Game.Player1)
            {
                // Both factories produce CHOOSE; this preserves empty and multi-pick answers in the record.
                matchRecorder.Write(r => r.OnHumanAction(Game.Turn,
                    BotAction.From(ChooseTask.Mulligan(owner, kept.ToList())).ToString(), -1, kept, false));
            }
        }
        CopyTransportBridge.Enter(this, Game);
        try
        {
            if (!Game.TryAnswer(new SabberStoneCore.Model.Decisions.DecisionAnswer(id, controller, kept), out var error))
                throw new InvalidOperationException(error);
            NoteOpposingAnswer(controller, kept);
        }
        finally { CopyTransportBridge.Exit(); }
        ObserveRewardHistory(Game.PowerHistory.Last);
        minimumHealth = Math.Min(minimumHealth, Math.Max(0, Game.Player1.Hero.Health));
        if (Game.Rewind == null) rewindRewards = null;
        RecordedAction();
    }
#endif

#if ENGINE_SOURCE
    // Flip off if the pinned client cannot render the selected spell name/related-card panel.
    internal const bool ArchaedasNativeEnabled = true;
    internal static bool NativeActionEnabled(PlayerTask task) => ArchaedasNativeEnabled
        || !(task is PlayCardTask && (task.Source?.Card.Id == "BE_055" || task.Source?.Card.Id == "BE_055t")
            || task.PlayerTaskType == PlayerTaskType.HERO_ATTACK && task.Controller.Hero.Weapon?.Card.Id == "BE_055t");
#endif

    private void TraceNpcDecision(PlayerTask task, GoldenDecision diagnosticDecision)
    {
        // Read authoritative state only after selection. Never send it back to the pilot/fair view.
        try
        {
            if (BotTrace != null)
            {
                string prefix = "match=" + MatchId + " turn=" + Game.Turn + " action=" + BotAction.From(task) + " ";
                BotTrace(prefix + MatchSnapshot.Side(Game.Player1, false));
                BotTrace(prefix + MatchSnapshot.Side(Game.Player2, true));
                if (diagnosticDecision != null)
                    BotTrace(prefix + "candidates=[" + MatchSnapshot.Candidates(diagnosticDecision) + "]");
            }
        }
        catch (Exception error) { try { BotTrace?.Invoke("snapshot-error=" + error.GetType().Name); } catch { } }
    }

    private void Process(PlayerTask task)
    {
        if (task.Controller == Game.Player2)
        {
            matchRecorder.Write(r => (r as PlaytestRecord)?.OnNpcAction(Game.Turn, BotAction.From(task).ToString()));
            TraceNpcDecision(task, npcDiagnosticDecision);
            npcDiagnosticDecision = null;
        }
#if ENGINE_SOURCE
        if (!NativeActionEnabled(task)) throw new InvalidOperationException("Archaedas native rendering is disabled.");
#endif
#if ENGINE_SOURCE
        if (task is ChooseTask choice)
        {
            var decision = Game.GetPendingDecisions().Single(d => d.ControllerId == task.Controller.Id);
            AnswerDecision(decision.Id, decision.ControllerId, choice.Choices);
            return;
        }
        if (RewindRules.WouldBegin(Game, task))
        {
            var rewindSource = Game.IdEntityDic[SourceId(task)];
            rewindPrivacy = privacyTargets.ToDictionary(pair => pair.Key, pair => new HashSet<int>(pair.Value));
            rewindDerivedPremiums = new Dictionary<int, int>(derivedPremiums);
            rewindChoiceOffers = new HashSet<int>(privateChoiceOffers);
            rewindAnonymousEntities = new HashSet<int>(clientTransportCards.Where(pair => string.IsNullOrEmpty(pair.Value)).Select(pair => pair.Key));
            rewindHighlights = new HashSet<int>(poweredUpHighlights);
            rewindRewards = new RewardCheckpoint {
                Played = new HashSet<string>(playedCardIds, StringComparer.Ordinal),
                Board = new HashSet<int>(enemyBoardMinions), Defeated = new HashSet<int>(defeatedEnemyMinions),
                Controllers = new Dictionary<int, int>(rewardControllers), MinimumHealth = minimumHealth,
                Source = rewindSource.Id, Card = rewindSource.Card.Id,
                HumanPlay = task is PlayCardTask && task.Controller == Game.Player1 &&
                    rewindSource.Zone == Game.Player1.HandZone && rewindSource.Card.Id != "GAME_005"
            };
        }
#endif
        if (matchRecorder.Enabled && task.Controller == Game.Player1)
            matchRecorder.Write(r => r.OnHumanAction(Game.Turn, BotAction.From(task).ToString(), recorderOptionIndex,
                (task as ChooseTask)?.Choices?.ToArray() ?? new int[0], task is ConcedeTask));
        if (task is ConcedeTask) matchRecorder.OnConcession(task.Controller.PlayerId, Game.Turn);
#if !ENGINE_SOURCE
        if (task is ChooseTask mulliganTask && task.Controller.Choice?.ChoiceType == ChoiceType.MULLIGAN)
            matchRecorder.OnMulligan(task.Controller.PlayerId, mulliganTask.Choices.ToArray());
#endif
        bool humanCardPlay = task is PlayCardTask &&
            task.Controller == Game.Player1 && task.Source?.Zone?.Type == EngineZone.HAND;
        string playedCard = humanCardPlay ? task.Source.Card.Id : null;
#if STANDARD_PROOF
        proofAction++;
        bool humanTrade = false;
        int[] beforeHand = null;
        if (task?.PlayerTaskType == PlayerTaskType.TRADE_CARD)
        {
            var player = task.Controller;
            humanTrade = player.PlayerId == HumanPlayerId;
            beforeHand = humanTrade ? player.HandZone.Select(card => card.Id).ToArray() : null;
            pendingTrade = new StandardProofNative.TradeRecord {
                action=proofAction, player=player.PlayerId, turn=Game.Turn, source=task.Source.Id, card=task.Source.Card.Id,
                sourceZoneBefore=(int)task.Source.Zone.Type, inputNonce=player.PlayerId==HumanPlayerId?TraceInputNonce:"",
                optionsId=player.PlayerId==HumanPlayerId?receivedOptions:-1, optionIndex=player.PlayerId==HumanPlayerId?receivedIndex:-1,
                manaBefore=player.RemainingMana, handBefore=player.HandZone.Count, deckBefore=player.DeckZone.Count
            };
        }
#endif
        bool observeHuman = npcPresentation != null && task.Controller == Game.Player1
            && Game.Step == Step.MAIN_ACTION && !(task is ChooseTask);
        var publicBefore = observeHuman ? NpcPublicPosition() : null;
        int publicTurn = Game.Turn;
        var publicAction = task is EndTurnTask ? NpcPublicAction.EndTurn
            : task.PlayerTaskType == PlayerTaskType.MINION_ATTACK ? NpcPublicAction.Attack
            : task is PlayCardTask && task.Source?.Card.Type == CardType.SPELL ? NpcPublicAction.Spell : NpcPublicAction.Other;
        int publicSource = SourceId(task);
#if ENGINE_SOURCE
        int publicTarget = task.ActionTarget?.Id ?? 0;
#else
        int publicTarget = task.Target?.Id ?? 0;
#endif
        copyCompletions.Clear();
        CopyTransportBridge.Enter(this, Game);
        try
        {
            if (!Game.Process(task))
                throw new InvalidOperationException("Engine rejected a previously legal task.");
        }
        finally
        {
            CopyTransportBridge.Exit();
        }
        if (task is ConcedeTask)
        {
            humanOptions = null;
            humanChoices = null;
#if ENGINE_SOURCE
            humanDecisionId = 0;
            humanDecisionController = 0;
#endif
        }
#if STANDARD_PROOF
        if (pendingTrade != null)
        {
            var player = task.Controller;
            pendingTrade.sourceZoneAfter=(int)task.Source.Zone.Type;
            pendingTrade.manaAfter=player.RemainingMana;
            pendingTrade.handAfter=player.HandZone.Count;
            pendingTrade.deckAfter=player.DeckZone.Count;
            if (humanTrade)
            {
                // Summoned When Drawn can chain into the traded card itself, or leave no hand card;
                // a stale LastCardDrawn already in hand must not become a no-draw replacement.
                var replacement = player.HandZone.SingleOrDefault(card => card.Id == player.LastCardDrawn
                    && (!beforeHand.Contains(card.Id) || card.Id == task.Source.Id));
                pendingTrade.humanReplacementEntity=replacement?.Id;
                pendingTrade.humanReplacementCard=replacement?.Card.Id;
            }
            pendingTrade.engineHistory=NormalizeTradeEngineHistory(Game.PowerHistory.Last,pendingTrade.source,pendingTrade.player);
        }
#endif
        if (playedCard != null && playedCard != "GAME_005")
            playedCardIds.Add(playedCard);
        ObserveRewardHistory(Game.PowerHistory.Last);
        minimumHealth = Math.Min(minimumHealth, Math.Max(0, Game.Player1.Hero.Health));
#if ENGINE_SOURCE
        if (Game.Rewind == null) rewindRewards = null;
#endif
        RecordedAction();
        if (observeHuman && Game.State != State.COMPLETE)
            foreach (string blunder in npcHumanTurn.Observe(publicTurn, publicAction, publicBefore,
                NpcPublicPosition(), publicSource, publicTarget))
                npcPresentation.Queue(NpcReaction.HumanMisplay, publicEventIndex, blunder);
    }

    private void RecordedAction(string implicitKind = null)
    {
        publicEventIndex++;
        matchRecorder.PostAction(Game, implicitKind);
        if (Game.State != State.COMPLETE) return;
        npcRunner?.Cancel();
        npcPresentation?.Cancel();
        if (resultRecorded) return;
        resultRecorded = true;
        matchRecorder.OnResult(Game.Player1.PlayState.ToString(), Game.Turn);
    }

    private void ObserveRewardHistory(IEnumerable<IPowerHistoryEntry> entries)
    {
        foreach (IPowerHistoryEntry entry in entries ?? Enumerable.Empty<IPowerHistoryEntry>())
        {
#if ENGINE_SOURCE
            if (entry is SabberStoneCore.Kettle.PowerHistoryResetGame)
            {
                if (rewindRewards == null) throw new InvalidOperationException("Rewind reward checkpoint is missing.");
                playedCardIds.Clear(); playedCardIds.UnionWith(rewindRewards.Played);
                enemyBoardMinions.Clear(); enemyBoardMinions.UnionWith(rewindRewards.Board);
                defeatedEnemyMinions.Clear(); defeatedEnemyMinions.UnionWith(rewindRewards.Defeated);
                rewardControllers.Clear();
                foreach (var pair in rewindRewards.Controllers) rewardControllers.Add(pair.Key, pair.Value);
                minimumHealth = rewindRewards.MinimumHealth;
            }
            else if (entry is PowerHistoryBlockStart replay && NativePetReactions.IsCardPlay(replay) &&
                rewindRewards != null && replay.Source == rewindRewards.Source &&
                rewindRewards.HumanPlay)
                playedCardIds.Add(rewindRewards.Card);
            else
#endif
            if (entry is PowerHistoryFullEntity full) ObserveRewardEntity(full.Entity.Id, full.Entity.Tags);
            else if (entry is PowerHistoryShowEntity show) ObserveRewardEntity(show.Entity.Id, show.Entity.Tags);
            else if (entry is PowerHistoryChangeEntity change) ObserveRewardEntity(change.Entity.Id, change.Entity.Tags);
            else if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange tag)
            {
                if (tag.Tag == GameTag.CONTROLLER) rewardControllers[tag.EntityId] = tag.Value;
                else if (tag.Tag == GameTag.ZONE) ObserveRewardZone(tag.EntityId, (EngineZone)tag.Value);
            }
        }
        foreach (Minion minion in Game.Player2.BoardZone)
            enemyBoardMinions.Add(minion.Id);
        foreach (int id in enemyBoardMinions)
            if (Game.IdEntityDic.TryGetValue(id, out IPlayable entity) && entity.Zone?.Type == EngineZone.GRAVEYARD)
                defeatedEnemyMinions.Add(id);
    }

    private void ObserveRewardEntity(int id, IDictionary<GameTag, int> tags)
    {
        if (tags == null) return;
        if (tags.TryGetValue(GameTag.CONTROLLER, out int controller)) rewardControllers[id] = controller;
        if (tags.TryGetValue(GameTag.ZONE, out int zone)) ObserveRewardZone(id, (EngineZone)zone);
    }

    private void ObserveRewardZone(int id, EngineZone zone)
    {
        if (zone == EngineZone.PLAY && rewardControllers.TryGetValue(id, out int controller) && controller == BotPlayerId &&
            Game.IdEntityDic.TryGetValue(id, out IPlayable entity) && entity is Minion)
            enemyBoardMinions.Add(id);
        else if (zone == EngineZone.GRAVEYARD && enemyBoardMinions.Contains(id))
            defeatedEnemyMinions.Add(id);
    }

    private int ChooseOneBranch(int source, int subOptionEntityId)
    {
        IPlayable[] playables = Game.IdEntityDic.TryGetValue(source, out IPlayable entity) ? entity.ChooseOnePlayables : null;
        int branch = playables == null ? -1 : Array.FindIndex(playables, playable => playable != null && playable.Id == subOptionEntityId);
        if (branch < 0)
            throw new ArgumentException("Sub-option is not a Choose One branch of its source.", "sub");
        return branch + 1;
    }

    private PlayerTask ResolveTask(PowerAllOptions options, int index, int target, int sub, int position)
    {
        PowerOption option = options.PowerOptionList[index];
        if (option.OptionType == OptionType.END_TURN)
        {
            if (target != 0 || sub != -1 || position != 0)
                throw new ArgumentException("End-turn option has unexpected parameters.");
            return options.PlayerTaskList.Single(t => t.PlayerTaskType == PlayerTaskType.END_TURN);
        }
        if (option.OptionType != OptionType.POWER || option.MainOption == null)
            throw new ArgumentException("Unsupported option type.", nameof(index));
        if (position < 0)
            throw new ArgumentOutOfRangeException(nameof(position));
        if (sub < -1 || sub >= option.SubOptions.Count || (option.SubOptions.Count > 0 && sub < 0))
            throw new ArgumentException("Sub-option is not legal.", nameof(sub));

        int source = option.MainOption.EntityId;
#if STANDARD_PROOF || ENGINE_SOURCE
        if (option.MainOption.PowerKeyword == 4354)
        {
            if (target != 0 || sub != -1 || position != 0 || option.SubOptions.Count != 0 || option.MainOption.Targets.Count != 0)
                throw new ArgumentException("Prepare option has unexpected parameters.");
            return options.PlayerTaskList.Single(task => task.PlayerTaskType == PlayerTaskType.PREPARE_CARD && SourceId(task) == source);
        }
        if (option.MainOption.PowerKeyword == 1720)
        {
            if (target != 0 || sub != -1 || position != 0 || option.SubOptions.Count != 0 || option.MainOption.Targets.Count != 0)
                throw new ArgumentException("Trade option has unexpected parameters.");
            return options.PlayerTaskList.Single(task => task.PlayerTaskType == PlayerTaskType.TRADE_CARD && SourceId(task) == source);
        }
        if (option.MainOption.PowerKeyword != 0
#if ENGINE_SOURCE
            && option.MainOption.PowerKeyword != 4089
#endif
            )
            throw new ArgumentException("Unsupported option keyword.");
#endif
#if ENGINE_SOURCE
        if (options.PlayerTaskList.Any(task => task is ActivateOldGodTask && SourceId(task) == source))
        {
            if (target != 0 || position != 0 || sub != -1 ||
                !Game.GetPendingDecisions().Any(d => d.Id == humanDecisionId && d.ControllerId == Game.Player1.Id))
                throw new ArgumentException("Old God activation parameters or decision are stale.");
            return options.PlayerTaskList.OfType<ActivateOldGodTask>().Single(task => task.Source.Id == source
                && SabberStoneCore.Actions.OldGodRules.CanActivate(Game.Player1,(Minion)task.Source));
        }
        if (options.PlayerTaskList.Any(task => task is ActivateLocationTask && SourceId(task) == source))
        {
            var location = Game.IdEntityDic.TryGetValue(source, out var entity) ? entity : null;
            if (position != 0 || sub != -1)
                throw new ArgumentException("Location activation has unexpected parameters.");
            if (!Game.GetPendingDecisions().Any(decision =>
                decision.Kind == SabberStoneCore.Model.Decisions.DecisionKind.Options &&
                decision.ControllerId == Game.Player1.Id && decision.Id == humanDecisionId))
                throw new ArgumentException("Location activation decision is stale.");
            var activations = options.PlayerTaskList.OfType<ActivateLocationTask>().Where(task =>
                task.Source.Id == source && (task.ActionTarget?.Id ?? 0) == target
                && SabberStoneCore.Actions.LocationRules.CanActivate(Game.Player1, location, task.ActionTarget)).ToList();
            if (activations.Count != 1)
                throw new ArgumentException("Location activation is not legal.");
            return activations[0];
        }
#endif
        // Native sub-option indexes address the built SubOptions list, which omits an unplayable branch;
        // map the chosen sub-option entity back to its Choose One branch instead of assuming index + 1.
        int wantedChooseOne = sub < 0 ? -1 : ChooseOneBranch(source, option.SubOptions[sub].EntityId);
        int wantedPosition = position - 1;
        var matches = options.PlayerTaskList.Where(task =>
            task.PlayerTaskType != PlayerTaskType.END_TURN &&
#if STANDARD_PROOF || ENGINE_SOURCE
            task.PlayerTaskType != PlayerTaskType.TRADE_CARD && task.PlayerTaskType != PlayerTaskType.PREPARE_CARD &&
#endif
            SourceId(task) == source &&
#if ENGINE_SOURCE
            (task.ActionTarget?.Id ?? 0) == target &&
#else
            (task.Target?.Id ?? 0) == target &&
#endif
            (!(task is PlayCardTask play) || play.ChooseOne == wantedChooseOne)).ToList();
        bool minionPlay = matches.Any(task => task is PlayCardTask play && (play.Source is Minion
#if ENGINE_SOURCE
            || play.Source is BoardObject
#endif
            ));
        if (minionPlay)
        {
            if (position == 0)
                throw new ArgumentException("Minion play requires a board position.", nameof(position));
#if ENGINE_SOURCE
            var placementSource = matches[0].Source;
            bool eitherSide = SabberStoneCore.Actions.EitherSideRules.Supports(placementSource.Card);
            var placementBoard = eitherSide ? SabberStoneCore.Actions.EitherSideRules.Destination(Game.Player1, wantedChooseOne).BoardZone : Game.Player1.BoardZone;
            if (eitherSide && !SabberStoneCore.Actions.EitherSideRules.Valid(Game.Player1, placementSource, null, wantedPosition, wantedChooseOne))
                throw new ArgumentException("Selected board placement is no longer legal.");
            bool locationPlacement = humanPlacementBoardHadLocations || Game.Player1.BoardZone.HasLocations || Game.Player2.BoardZone.HasLocations ||
                matches.Any(task => task.Source is BoardObject || task.ActionTarget is BoardObject);
            if (locationPlacement && (wantedPosition > placementBoard.SlotCount ||
                !Game.GetPendingDecisions().Any(decision =>
                    decision.Kind == SabberStoneCore.Model.Decisions.DecisionKind.Options &&
                    decision.ControllerId == Game.Player1.Id && decision.Options.Any(task =>
                        task.TaskType == PlayerTaskType.PLAY_CARD && task.EntityId == source &&
                        task.TargetId == target && task.BranchIndex == wantedChooseOne && task.ZonePosition == wantedPosition))))
                throw new ArgumentException("Card placement is no longer legal.");
#endif
            matches = matches.Where(task => !(task is PlayCardTask play) || play.ZonePosition == wantedPosition).ToList();
        }
        else if (position != 0)
        {
            throw new ArgumentException("This option does not accept a board position.", nameof(position));
        }
        if (matches.Count != 1)
            throw new ArgumentException("Option target, sub-option, or board position is not legal.");
        return matches[0];
    }

    private static int SourceId(PlayerTask task)
    {
        if (task.Source != null)
            return task.Source.Id;
        if (task.PlayerTaskType == PlayerTaskType.HERO_ATTACK)
            return task.Controller.Hero.Id;
        if (task.PlayerTaskType == PlayerTaskType.HERO_POWER)
            return task.Controller.Hero.HeroPower.Id;
        return 0;
    }

    private void DriveUntilHumanInput()
    {
        if (npcPresentation != null && Game.Step == Step.MAIN_ACTION
            && Game.Player1.Choice == null && Game.Player2.Choice == null)
        {
            npcPresentation.Queue(NpcReaction.MatchStart, publicEventIndex,
                delayMilliseconds: NativeNpcStartingTaunt?.Invoke() != false ? 3500 : 1000);
            if (!npcLastPublicValue.HasValue) npcLastPublicValue = NpcPublicPosition().Value;
        }
#if ENGINE_SOURCE
        if (SabberStoneCore.Actions.AutonomousTurnRules.IsActive(Game))
        {
            humanChoices = null; humanOptions = null;
            SabberStoneCore.Actions.AutonomousTurnRules.Run(Game);
            RecordedAction("autonomous");
            SendHistory(Game.PowerHistory.Last);
        }
#endif
        for (int count = 0; count < MaxBotActions; count++)
        {
            // Bot thinking can run ahead of the client; only native readiness starts the human clock.
            if (Game.CurrentPlayer != Game.Player1) RefreshTurnDeadline(MonotonicMilliseconds);
            if (Game.State == State.COMPLETE)
                return;
            if (Game.Player1.Choice != null || (Game.CurrentPlayer == Game.Player1 && Game.Player2.Choice == null))
            {
                if (Game.Player1.Choice != null)
                    SendHumanChoice();
                else
                    SendHumanOptions();
                return;
            }

            if (!OpeningNpcReady()) { openingNpcWaiting = true; return; }
            openingNpcWaiting = false;
            if (npcProfile != null && npcPilot != null)
            {
                if (npcStopped) return;
                if (npcRunner == null || npcTurn != Game.Turn)
                    StartNpcTurn();
                else if (!npcRunner.Active) npcRunner.Resume();
                if (SynchronousNpc) TickNpc();
                return;
            }

            PlayerTask task;
#if ENGINE_SOURCE
            PowerEntityChoices rewindChoice = null;
            if (Game.Player2.Choice?.ChoiceAction == ChoiceAction.REWIND)
            {
                rewindChoice = PowerChoicesBuilder.EntityChoices(Game, Game.Player2.Choice);
                send(17, ToNativeChoice(rewindChoice));
            }
#endif
            PowerEntityChoices mulliganChoice = null;
            if (Game.Player2.Choice != null)
            {
                Choice choice = Game.Player2.Choice;
                if (choice.ChoiceType == ChoiceType.MULLIGAN)
                {
                    mulliganChoice = PowerChoicesBuilder.EntityChoices(Game, choice);
                    var npc = NpcCatalog.ForDeck(opponentDeck?.Id);
                    var keeps = npc == null || DeckPlans.ForId(npc.PlanId) == null ? new List<int>() : new List<int>(BotDriver.Mulligan(
                        new MulliganView(choice.Choices.Select(id => new MulliganCard(id,
                            Game.IdEntityDic[id].Card.Id, Game.IdEntityDic[id].Card.Cost)), Game.FirstPlayer == Game.Player1,
                            opponentDeck.CardIds, Game.Player1.Hero.Card.Class), BotMulliganPolicy(npc)));
                    send(17, ToNativeChoice(mulliganChoice));
                    task = ChooseTask.Mulligan(Game.Player2, keeps);
                }
                else task = ChooseBotTask(Game.Player2.Options());
            }
            else
            {
                task = ChooseBotTask(Game.Player2.Options());
            }
            Process(task);
            if (mulliganChoice != null) SendOpposingChosen(mulliganChoice, ((ChooseTask)task).Choices);
#if ENGINE_SOURCE
            if (rewindChoice != null) SendChosen(rewindChoice, ((ChooseTask)task).Choices);
#endif
            SendHistory(Game.PowerHistory.Last);
#if ENGINE_SOURCE
            if (rewindChoice != null) RetireChosenChoiceActors(rewindChoice, ((ChooseTask)task).Choices,
                new HashSet<int>(), new HashSet<int>());
#endif
        }
        throw new InvalidOperationException("Bot exceeded bounded action count.");
    }

    private void RecordNpcTimeout(PlayerTask task)
    {
        if (npcProfile == null) return;
        var context = new GoldenContext(Game.Turn, ++decisionIndex,
            unchecked(997 * Game.Turn + decisionIndex * 31), npcProfile);
        var decision = new ReadyNpcDecision(DriverTrace.Forced("timeout", task)).Result;
        matchRecorder.OnDecision(context, decision, "forced", false, 0, 0, 0);
        if (task is ChooseTask) TraceNpcDecision(task, decision);
        BotTrace?.Invoke("npc=" + npcProfile.Id + " turn=" + Game.Turn
            + " reuse=0 lethal=forced slice=0 ms=0 nodes=0 timeout=1 action=" + decision.Action);
    }

    private bool NpcCanAct() => !npcStopped && Game != null && Game.State != State.COMPLETE
        && Game.Turn == npcTurn && Game.Player1.Choice == null
        && (Game.CurrentPlayer == Game.Player2 || Game.Player2.Choice != null);

    private List<PlayerTask> NpcTasks()
    {
        var tasks = Game.Player2.Options();
#if ENGINE_SOURCE
        tasks = tasks.Where(NativeActionEnabled).ToList();
#endif
        return tasks;
    }

    private void StartNpcTurn()
    {
        npcRunner?.Cancel(); npcTurn = Game.Turn;
        long value = NpcPublicPosition().Value;
        if (NpcConcession.ProvesPublicBoardLethal(Game, BotPlayerId))
            npcPresentation?.Queue(NpcReaction.BotDoomed, publicEventIndex);
        if (npcLastPublicValue.HasValue && value - npcLastPublicValue.Value >= npcProfile.Personality.Emotes.SwingThreshold)
            npcPresentation?.Queue(NpcReaction.HumanBigTurn, publicEventIndex);
        npcLastPublicValue = value;
        npcTurnStartValue = value; npcBigTurnSeen = false;
        npcRunner = npcRunner ?? new NpcTurnRunner(npcProfile, MatchId, NpcClock, NpcCanAct,
            () => new GoldenContext(Game.Turn, ++decisionIndex, unchecked(997 * Game.Turn + decisionIndex * 31), npcProfile),
            BeginNpcDecision, NpcFallback, ActNpc, () => NativeNpcIdle?.Invoke() ?? true,
            line => BotTrace?.Invoke(line), matchRecorder, () => publicEventIndex, NpcTurnNodes,
            action => Math.Max(npcPresentation?.ActedCardDwell(action.Source,
                new HashSet<int>(Game.Player2.HandZone.Concat(Game.Player2.BoardZone).Select(card => card.Id))) ?? 0,
#if ENGINE_SOURCE
                OpposingChoiceDwell(action)),
#else
                0),
#endif
            () => NativeNpcBusyReason?.Invoke(),
            // Nozdormu-style finite turn deadline (monotonic clock), so elite+ never searches into a timed-out turn.
            () => timedTurn == Game.Turn && turnDeadline != long.MaxValue ? turnDeadline - MonotonicMilliseconds : double.PositiveInfinity,
#if ENGINE_SOURCE
            OpposingChoiceHold);
#else
            null);
#endif
        npcRunner.Start();
    }

    internal IGoldenDecisionJob BeginNpcDecision(GoldenContext context)
    {
        var tasks = NpcTasks();
        if (DebugScenario?.HeroAttack == "npc")
        {
            var attack = tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.HERO_ATTACK && t.Target == Game.Player1.Hero);
            if (attack != null) return new ReadyNpcDecision(DriverTrace.Forced("hero-skin-scenario", attack));
        }
        if (DebugScenario?.HeroPower == "npc")
        {
            var power = tasks.Where(t => t.PlayerTaskType == PlayerTaskType.HERO_POWER)
                .OrderByDescending(t => t.Target == Game.Player1.Hero).FirstOrDefault();
            if (power != null) return new ReadyNpcDecision(DriverTrace.Forced("hero-skin-power-scenario", power));
        }
        var end = timedTurn == Game.Turn && MonotonicMilliseconds >= turnDeadline
            ? tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.END_TURN) : null;
        if (end != null) return new ReadyNpcDecision(BotDriver.Timeout(end).Trace);
        var forced = BotDriver.Forced(Game, tasks, out var trace, (npcPilot as IGoldenPilotHost)?.Options);
        if (forced != null) return new ReadyNpcDecision(trace);
        var options = (npcPilot as IGoldenPilotHost)?.Options;
        var view = options?.FairViewFactory == null
            ? FairBotState.Create(Game, BotPlayerId, context.ArtificialSeed, opponentDeck.Style)
            : options.FairViewFactory(Game, BotPlayerId, context.ArtificialSeed, opponentDeck.Style);
        view.FaultReporter = line => BotTrace?.Invoke(line);
        var publicContext = new GoldenContext(context.Turn, context.DecisionIndex, context.ArtificialSeed,
            context.NpcProfile, (npcPilot as IGoldenPilotHost)?.PublicInputs);
        return ((IFrameSlicedGoldenPilot)npcPilot).Begin(view, publicContext);
    }

    private GoldenDecision NpcFallback(string reason)
    {
        var tasks = NpcTasks();
        var forced = BotDriver.Forced(Game, tasks, out var trace, (npcPilot as IGoldenPilotHost)?.Options);
        var task = forced ?? (reason == "action-limit" ? tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.END_TURN) : null);
        // A search-free fast-finish decision still honours a hard turn deadline (Nozdormu) like BeginNpcDecision.
        if (task == null && reason == FastFinish.Reason && timedTurn == Game.Turn && MonotonicMilliseconds >= turnDeadline)
            task = tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.END_TURN);
        string fastFinish = null;
        IReadOnlyList<KeyValuePair<BotAction, double>> compared = null;
        if (task == null)
        {
            try {
                int seed = unchecked(997 * Game.Turn + decisionIndex * 31);
                var options = (npcPilot as IGoldenPilotHost)?.Options;
                var view = options?.FairViewFactory == null ? FairBotState.Create(Game, BotPlayerId, seed, opponentDeck.Style)
                    : options.FairViewFactory(Game, BotPlayerId, seed, opponentDeck.Style);
                view.FaultReporter = line => BotTrace?.Invoke(line);
                // Turn budget exhausted (clock cut, no planner line): never pass with free useful actions left because the
                // one-ply fallback had no time to look at them (FastFinish.Fallback; other reasons keep the bounded fallback).
                task = FastFinish.Fallback(view, tasks.Select(BotAction.From).ToArray(), reason,
                    () => npcRunner.SpendFallbackNode(), out fastFinish, out compared).Resolve(tasks);
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) {
                BotTrace?.Invoke("fallback-evaluation-fault=" + error.GetType().Name);
                fastFinish = null;
                task = tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.END_TURN) ?? LocalBot.Choose(tasks);
            }
        }
        var decision = new GoldenDecision(BotAction.From(task), 0, 0, 0, "Forced", reason, compared,
            fastFinish != null ? FastFinish.DriverKind : trace?.Kind ?? "fallback");
        if (fastFinish != null) decision.SearchInfo.FastFinish = fastFinish;
        return decision;
    }

    internal void ActNpc(GoldenDecision decision)
    {
        npcPresentation?.ClearHover();
        PlayerTask task;
        try { task = decision.Action.Resolve(NpcTasks()); }
        catch (Exception error)
        {
            decision = NpcFallback(error.GetType().Name + ":" + error.Message);
            task = decision.Action.Resolve(NpcTasks());
            matchRecorder.Write(r => (r as PlaytestRecord)?.CorrectDecision(decision));
            BotTrace?.Invoke("npc=" + npcProfile.Id + " resolve-fallback=" + decision.FallbackReason);
        }
        if (task.PlayerTaskType == PlayerTaskType.END_TURN && decision.DriverKind == "search"
            && string.IsNullOrEmpty(decision.FallbackReason) && decision.Stop != "Clock" && npcRunner?.ClockCut != true
            && NpcConcession.ShouldConcede(Game, BotPlayerId, npcProfile, MatchId, publicEventIndex, true))
        {
            npcPresentation?.Queue(NpcReaction.Concession, publicEventIndex, delayMilliseconds: 0, npcLoses: true);
            npcPresentation?.Tick((NativeNpcIdle?.Invoke() ?? true) && (NativePresentationInputReady?.Invoke() ?? true), false);
            npcConcessionPending = true;
            npcConcessionQueuedAt = NpcClock();
            npcRunner?.Cancel();
            return;
        }
        bool lethalReaction = (npcRunner?.LethalFound == true && decision.DriverKind == "search"
            && string.IsNullOrEmpty(decision.FallbackReason)) || NpcFinalAttack(task);
#if ENGINE_SOURCE
        PowerEntityChoices rewindChoice = null;
        if (Game.Player2.Choice?.ChoiceAction == ChoiceAction.REWIND)
        {
            rewindChoice = PowerChoicesBuilder.EntityChoices(Game, Game.Player2.Choice);
            send(17, ToNativeChoice(rewindChoice));
        }
#endif
        npcDiagnosticDecision = decision;
        try { Process(task); }
        finally { npcDiagnosticDecision = null; }
#if ENGINE_SOURCE
        if (rewindChoice != null) SendChosen(rewindChoice, ((ChooseTask)task).Choices);
#endif
        npcLastPublicValue = NpcPublicPosition().Value;
        if (Game.State != State.COMPLETE && Game.CurrentPlayer == Game.Player2)
        {
            if (lethalReaction) npcPresentation?.Queue(NpcReaction.BotLethalFound, publicEventIndex);
            bool big = npcTurnStartValue - npcLastPublicValue.Value >= npcProfile.Personality.Emotes.SwingThreshold;
#if ENGINE_SOURCE
            big |= task is PlayCardTask && task.Source?.Card[GameTag.BATTLECRY] > 0 && NpcDoubledBattlecry(task);
#endif
            if (big && !npcBigTurnSeen)
            {
                npcBigTurnSeen = true;
                npcPresentation?.Queue(NpcReaction.OwnBigTurn, publicEventIndex);
            }
        }
        BotNodes += decision.Nodes;
        if (decision.DriverKind == "fallback") BotFallbacks++;
        SendHistory(Game.PowerHistory.Last);
#if ENGINE_SOURCE
        if (rewindChoice != null) RetireChosenChoiceActors(rewindChoice, ((ChooseTask)task).Choices,
            new HashSet<int>(), new HashSet<int>());
#endif
    }

    public void TickNpc()
    {
#if ENGINE_SOURCE
        FlushDeferredOpposing();
        FlushChoiceRecovery();
#endif
        if (openingNpcWaiting && !npcStopped && Game != null && Game.State != State.COMPLETE)
        {
            if (!OpeningNpcReady()) return;
            DriveUntilHumanInput();
            return;
        }
        if (!npcStopped && Game != null && Game.State != State.COMPLETE)
            npcPresentation?.Tick((NativeNpcIdle?.Invoke() ?? true) && (NativePresentationInputReady?.Invoke() ?? true)
                && Game.Step == Step.MAIN_ACTION
                && Game.Player1.Choice == null && Game.Player2.Choice == null
                && (Game.CurrentPlayer != Game.Player1 || (NativeHumanInputReady?.Invoke() ?? true)),
                // Choices interrupt the move too; a waiting reaction must never block answering one.
                Game.CurrentPlayer == Game.Player1 || Game.Player1.Choice != null || Game.Player2.Choice != null);
        if (npcConcessionPending)
        {
            if (npcStopped || Game.State == State.COMPLETE) { npcConcessionPending = false; return; }
            if (Game.CurrentPlayer != Game.Player2 || Game.Player1.Choice != null || Game.Player2.Choice != null)
            { npcConcessionPending = false; DriveUntilHumanInput(); return; }
            if (NativeNpcIdle?.Invoke() == false) return;
            if (HoldConcessionForFarewell()) return;
            npcConcessionPending = false;
            if (NpcConcession.ProvesPublicBoardLethal(Game, BotPlayerId))
            {
                OpponentConceded = true;
                Process(ConcedeTask.Any(Game.Player2));
                SendHistory(Game.PowerHistory.Last);
                return;
            }
            Process(EndTurnTask.Any(Game.Player2));
            SendHistory(Game.PowerHistory.Last);
            DriveUntilHumanInput();
            return;
        }
#if ENGINE_SOURCE
        ObserveOpposingChoiceReveal();
#endif
        if (npcRunner == null || !npcRunner.Active || npcPresentation?.WaitingForReaction == true) return;
        double before = npcRunner.SearchMilliseconds;
        if (SynchronousNpc) npcRunner.RunSynchronously(); else npcRunner.Tick();
        BotMilliseconds += (long)Math.Max(0, npcRunner.SearchMilliseconds - before);
        if (!npcStopped && Game.State != State.COMPLETE
            && npcRunner.State == NpcTurnState.Thinking)
            npcPresentation?.Hover(() => npcRunner.Candidates.Select(action => action.Source),
                new HashSet<int>(Game.Player2.HandZone.Concat(Game.Player2.BoardZone).Select(card => card.Id)),
                Game.Player2.HandZone.Select(card => card.Id));
        if (!npcRunner.Active && !npcStopped && !npcConcessionPending && Game.State != State.COMPLETE)
            DriveUntilHumanInput();
    }

    // Never past the hold bound, and never so close to the turn deadline that the timer could drop the concession.
    private bool HoldConcessionForFarewell() => npcPresentation?.FarewellFollowUpPending == true
        && NpcClock() - npcConcessionQueuedAt < ConcessionFarewellHoldMilliseconds
        && !(timedTurn == Game.Turn && turnDeadline != long.MaxValue
            && turnDeadline - MonotonicMilliseconds < ConcessionFarewellHoldMilliseconds + 1000);

    private NpcPublicPosition NpcPublicPosition() => new NpcPublicPosition(
        Game.Player1.BoardZone.Select(m => new NpcPublicMinion(m.Id, m.AttackDamage, m.Health,
            string.IsNullOrWhiteSpace(m.Card.Text) && (m.AppliedEnchantments?.Count ?? 0) == 0)),
        Game.Player2.BoardZone.Select(m => new NpcPublicMinion(m.Id, m.AttackDamage, m.Health,
            string.IsNullOrWhiteSpace(m.Card.Text) && (m.AppliedEnchantments?.Count ?? 0) == 0)),
        (long)Game.Player1.Hero.Health + Game.Player1.Hero.Armor, (long)Game.Player2.Hero.Health + Game.Player2.Hero.Armor,
        Game.Player1.RemainingMana, Game.Player1.HandZone.Count);

    private bool NpcFinalAttack(PlayerTask task)
    {
#if ENGINE_SOURCE
        int target = task.ActionTarget?.Id ?? 0;
#else
        int target = task.Target?.Id ?? 0;
#endif
        var human = Game.Player1;
        var attacker = task.PlayerTaskType == PlayerTaskType.HERO_ATTACK ? Game.Player2.Hero : task.Source as Character;
        return (task.PlayerTaskType == PlayerTaskType.MINION_ATTACK || task.PlayerTaskType == PlayerTaskType.HERO_ATTACK)
            && target == human.Hero.Id && attacker != null && human.SecretZone.Count == 0
            && !human.Hero.IsImmune && human.Hero[GameTag.DIVINE_SHIELD] == 0 && human.Hero[GameTag.HEALTH_MINIMUM] == 0
            && human.Hero[GameTag.INCOMING_DAMAGE_CAP] == 0 && human.Hero[GameTag.INCOMING_COMBAT_DAMAGE_CAP] == 0
            && attacker.AttackDamage >= (long)human.Hero.Health + human.Hero.Armor;
    }
#if ENGINE_SOURCE
    private bool NpcDoubledBattlecry(PlayerTask task)
    {
        var player = Game.Player2;
        return player.HeroPowers.Any(power => power.Card.Id == "JAIL_800hp2")
            && Game.HistoryFacts.Query.Plays().Count(play => play.ControllerId == player.Id
                && play.CardType == CardType.MINION) % 5 == 0;
    }
#endif
    // NPC World seam S4: a skill-aware mulligan for world encounters. The wrapper guards Keep() itself, because
    // BotDriver.Mulligan runs it unguarded at match start: a throwing or invalid hook policy (not offered, duplicate,
    // coin) falls back to the plan mulligan instead of aborting the match or freezing the NPC.
    internal static MulliganPolicy BotMulliganPolicy(NpcProfile npc)
    {
        var plan = DeckPlans.ForId(npc.PlanId);
        var fallback = new DeckPlanMulligan(plan);
        MulliganPolicy hook;
        try { hook = NpcWorldHooks.MulliganFor?.Invoke(npc, plan); }
        catch (Exception) { return fallback; }
        return hook == null ? (MulliganPolicy)fallback : new GuardedMulligan(hook, fallback);
    }
    private sealed class GuardedMulligan : MulliganPolicy
    {
        private readonly MulliganPolicy hook, fallback;
        internal GuardedMulligan(MulliganPolicy hook, MulliganPolicy fallback) { this.hook = hook; this.fallback = fallback; }
        public override IReadOnlyList<int> Keep(MulliganView view)
        {
            try
            {
                var keeps = hook.Keep(view)?.ToArray();
                var offered = new HashSet<int>(view.Offered.Where(c => c.CardId != "GAME_005").Select(c => c.EntityId));
                if (keeps != null && keeps.Distinct().Count() == keeps.Length && keeps.All(offered.Contains))
                    return Array.AsReadOnly(keeps);
            }
            catch (Exception) { }
            return fallback.Keep(view);
        }
    }
    // The name plate shows the encounter NPC; LocalDeck.Opponent (NPC World seam S5) is the fallback and normally equal.
    internal static string OpponentDisplayName(NpcProfile npc, LocalDeck deck) =>
        npc != null && !string.IsNullOrEmpty(npc.DisplayName) ? npc.DisplayName : deck.Opponent;
    internal bool SendNpcPresentation(NpcPresentationEvent presentation)
    {
        if (Game == null || NativeNpcUserUI == null
            || (npcStopped || Game.State == State.COMPLETE) && presentation.OverCard != 0) return false;
        if (presentation.PlayerId != Game.Player2.PlayerId
            || presentation.Emote != null && (!PresentationSettings.Emotes || NpcEmotesSquelched)) return false;
        var packet = new PegasusGame.UserUI { PlayerId = presentation.PlayerId };
        if (presentation.OverCard.HasValue)
            packet.MouseInfo = new PegasusGame.MouseInfo { OverCard = presentation.OverCard.Value };
        else
        {
            // Pinned Assembly-CSharp EmoteType; WOW is 33, not the semantic enum's ordinal.
            switch (presentation.Emote)
            {
                case "Greetings": packet.Emote = (int)EmoteType.GREETINGS; break;
                case "WellPlayed": packet.Emote = (int)EmoteType.WELL_PLAYED; break;
                case "Thanks": packet.Emote = (int)EmoteType.THANKS; break;
                case "Oops": packet.Emote = (int)EmoteType.OOPS; break;
                case "Sorry": packet.Emote = (int)EmoteType.SORRY; break;
                case "Wow": packet.Emote = (int)EmoteType.WOW; break;
                case "Threaten": packet.Emote = (int)EmoteType.THREATEN; break;
                default: return false;
            }
        }
        try
        {
#if ENGINE_SOURCE
            if (packet.MouseInfo != null) packet.MouseInfo.OverCard = WireReturnId(packet.MouseInfo.OverCard);
#endif
            if (!NativeNpcUserUI(packet)) return false;
            if (presentation.Emote != null)
            {
                matchRecorder.OnEmote(Game.Player2.PlayerId, presentation.Emote, presentation.PublicEventIndex ?? publicEventIndex);
                BotTrace?.Invoke("npc=" + (npcProfile?.Id ?? "") + " emote=" + presentation.Emote + " event=" + (presentation.PublicEventIndex ?? publicEventIndex));
            }
            return true;
        }
        catch (Exception error) { BotTrace?.Invoke("npc-presentation=" + error.GetType().Name); return false; }
    }

    private sealed class ReadyNpcDecision : IGoldenDecisionJob
    {
        public bool IsDone => true;
        public string LethalResult => "forced";
        public bool PlanReused => false;
        public GoldenDecision Result { get; }
        internal ReadyNpcDecision(DriverTrace trace)
        {
            var value = trace.PilotDecision;
            Result = new GoldenDecision(value.Action, value.Value, value.Nodes, value.Depth,
                value.Stop.ToString(), trace.FallbackReason, value.Candidates, trace.Kind);
        }
        public void StepNode() { }
    }

    private PlayerTask ChooseBotTask(List<PlayerTask> tasks)
    {
        npcDiagnosticDecision = null;
        if (DebugScenario?.HeroAttack == "npc")
        {
            var attack = tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.HERO_ATTACK && t.Target == Game.Player1.Hero);
            if (attack != null) return attack;
        }
        if (DebugScenario?.HeroPower == "npc")
        {
            var power = tasks.Where(t => t.PlayerTaskType == PlayerTaskType.HERO_POWER)
                .OrderByDescending(t => t.Target == Game.Player1.Hero).FirstOrDefault();
            if (power != null) return power;
        }
        if (timedTurn == Game.Turn && MonotonicMilliseconds >= turnDeadline)
        {
            var end = tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.END_TURN);
            if (end != null) return BotDriver.Timeout(end).Task;
        }
#if ENGINE_SOURCE
        tasks = tasks.Where(NativeActionEnabled).ToList();
        var forced = BotDriver.Forced(Game, tasks, out _);
        if (forced != null) return forced;
#endif
#if STANDARD_PROOF
        if (IsProofMatch) return proofBot.Choose(tasks);
#endif
        if (opponentDeck != null)
        {
            if (planningTurn != Game.Turn) { planningTurn = Game.Turn; turnClock.Restart(); }
            var budget = ClockFreeBot ? BotBudget.ForStars(startingStars).WithoutClock() : BotBudget.ForStars(startingStars);
            if (!ClockFreeBot && turnClock.ElapsedMilliseconds >= 80 + Math.Min(5, startingStars / 30) * 45)
            { BotFallbacks++; return LocalBot.Choose(tasks); }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = BotDriver.Decide(Game, BotPlayerId, opponentDeck.Style,
                    state => PilotDecision.From(BotSearch.Choose(state, budget)), ref decisionIndex, tasks: tasks);
                npcDiagnosticDecision = new ReadyNpcDecision(result.Trace).Result;
                matchRecorder.OnDecision(new GoldenContext(Game.Turn, decisionIndex,
                    unchecked(997 * Game.Turn + decisionIndex * 31), npcProfile), npcDiagnosticDecision,
                    "none", false, npcDiagnosticDecision.Nodes, clock.Elapsed.TotalMilliseconds, 0);
                if (result.Trace.Kind == "fallback")
                {
                    BotFallbacks++;
                    BotTrace?.Invoke("fallback=" + result.Trace.FallbackReason);
                    return result.Task;
                }
                var plan = result.Trace.PilotDecision;
                BotNodes += plan.Nodes;
                BotTrace?.Invoke($"turn={Game.Turn} stars={startingStars} depth={plan.Depth} nodes={plan.Nodes} ms={clock.ElapsedMilliseconds} action={plan.Action}");
                return result.Task;
            }
            catch (Exception error)
            {
                BotFallbacks++;
                BotTrace?.Invoke("fallback=" + error.GetType().Name + ":" + error.Message);
                return LocalBot.Choose(tasks);
            }
            finally { BotMilliseconds += clock.ElapsedMilliseconds; }
        }
        if (tasks == null || tasks.Count == 0)
            throw new InvalidOperationException("Bot has no legal task.");
        if (tasks.Any(t => t is ChooseTask)) return LocalBot.Choose(tasks);
        int enemyHero = Game.Player1.Hero.Id;
        return tasks.FirstOrDefault(t =>
                   (t.PlayerTaskType == PlayerTaskType.MINION_ATTACK || t.PlayerTaskType == PlayerTaskType.HERO_ATTACK) &&
                   t.Target?.Id == enemyHero)
               ?? tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.MINION_ATTACK || t.PlayerTaskType == PlayerTaskType.HERO_ATTACK)
               ?? tasks.Where(t => t.PlayerTaskType == PlayerTaskType.PLAY_CARD)
                   .OrderByDescending(t => t.Source.Card.Cost).FirstOrDefault()
               ?? tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.HERO_POWER && t.Target?.Id == enemyHero)
               ?? tasks.FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.HERO_POWER)
#if ENGINE_SOURCE
               ?? tasks.OfType<ActivateOldGodTask>().FirstOrDefault()
#endif
               ?? tasks.Single(t => t.PlayerTaskType == PlayerTaskType.END_TURN);
    }

    private void SendHumanChoice()
    {
#if ENGINE_SOURCE
        if (SabberStoneCore.Actions.AutonomousTurnRules.IsActive(Game)) { humanChoices = null; humanOptions = null; return; }
#endif
        PowerEntityChoices choices = PowerChoicesBuilder.EntityChoices(Game, Game.Player1.Choice);
        if (choices == null)
            throw new InvalidOperationException("Human choice was not available.");
        if (choices.ChoiceType == ChoiceType.MULLIGAN)
        {
            // Retail MulliganManager hides the final Coin actor; test-control holds must match its selectable cards.
            choices.Entities.RemoveAll(id => Game.IdEntityDic[id].Card.Id == "GAME_005");
            choices.CountMax = choices.Entities.Count;
        }
#if ENGINE_SOURCE
        var decision = Game.GetPendingDecisions().Single(d => d.ControllerId == Game.Player1.Id);
        humanDecisionId = decision.Id;
        humanDecisionController = decision.ControllerId;
#endif
        humanChoices = choices;
        humanOptions = null;
#if ENGINE_SOURCE
        deckChoiceOriginals.Clear(); privateDeckOffers.Clear();
        if (choices.ChoiceType == ChoiceType.GENERAL && choices.Entities.Any(id => Game.IdEntityDic[id].Zone?.Type == EngineZone.DECK))
        {
            var history = new NativePowerHistory();
            // ChoiceCardMgr forces hand actors. Never let it touch an original DECK Card.
            // Match DiscoverRealRules' private SETASIDE display actors without changing engine decisions/RNG.
            privateDeckOffers.UnionWith(choices.Entities.Where(id => Game.IdEntityDic[id].Zone?.Type == EngineZone.DECK));
            RefreshPrivacyTargets();
            AppendPrivateAttachments(history);
            for (int i = 0; i < choices.Entities.Count; i++)
            {
                int original = choices.Entities[i];
                var offered = Game.IdEntityDic[original];
                if (offered.Zone?.Type != EngineZone.DECK) continue;
                // Published after SendHistory at the suspended decision boundary: no earlier entry is being named.
                var snapshot = PowerHistoryBuilder.ShowEntity(offered).Entity;
                int proxy = nextDeckChoiceId--;
                if (proxy <= Game.IdEntityDic.Values.Max(entity => entity.Id)) throw new InvalidOperationException("Choice proxy IDs exhausted.");
                snapshot.Id = proxy;
                snapshot.Tags[GameTag.ZONE] = (int)EngineZone.SETASIDE;
                snapshot.Tags[GameTag.ZONE_POSITION] = 0;
                snapshot.Tags[GameTag.CREATOR] = choices.SourceId;
                snapshot.Tags[GameTag.DISPLAYED_CREATOR] = choices.SourceId;
                snapshot.Tags[GameTag.LINKED_ENTITY] = original;
                deckChoiceOriginals.Add(proxy, original); // Before the snapshot: the proxy shows its original's premium.
                var create = new PowerHistoryData { FullEntity = ToNativeHistoryEntity(snapshot, true) };
                // Presentation-only actors have no engine entity for stat refresh.
                history.List.Add(create); TrackClientState(create);
                choices.Entities[i] = proxy;
            }
            SendPowerHistory(history);
        }
#endif
        var packet = ToNativeChoice(choices);
#if ENGINE_SOURCE
        humanTargetSourceSubstituted = choices.ChoiceType == ChoiceType.TARGET && packet.Source != choices.SourceId;
        var sourceHistory = new NativePowerHistory();
        ClearSubstitutedTargetSource(sourceHistory);
        if (humanTargetSourceSubstituted)
        {
            substitutedTargetSource = packet.Source;
            substitutedTargetHandTag = visibleTransportTags.TryGetValue(packet.Source, out var tags)
                && tags.TryGetValue(1508, out int previous) ? previous : 0;
            // EnterChoiceMode -> HasHandTarget reads only this capability and the offered hand actors.
            AppendTargetSourceTag(sourceHistory, packet.Source,
                choices.Entities.Any(id => Game.IdEntityDic[id].Zone?.Type == EngineZone.HAND) ? 1 : 0);
        }
        SendPowerHistory(sourceHistory);
#endif
        send(17, packet);
    }

    internal Dictionary<string, object> DiagnosticEntity(int id, bool includeTags = true)
    {
        bool found = Game.IdEntityDic.TryGetValue(id, out var entity);
        var row = new Dictionary<string, object> {
            { "entityId", id }, { "exists", found }, { "cardId", entity?.Card?.Id },
            { "zone", entity?.Zone?.Type.ToString() }, { "controller", entity?.Controller?.PlayerId ?? 0 },
            { "creator", found ? entity[GameTag.CREATOR] : 0 }
        };
#if ENGINE_SOURCE
        bool projected = wireEntities.TryGetValue(id, out var wire);
        row["projection"] = new Dictionary<string, object> {
            { "present", projected }, { "cardId", wire?.Card }, { "zone", wire?.Zone }, { "controller", wire?.Controller }
        };
#endif
        if (includeTags) row["tags"] = found ? entity.NativeTags.OrderBy(tag => (int)tag.Key).ToDictionary(tag => tag.Key.ToString(), tag => (object)tag.Value) : null;
        row["clientEntityId"] = WireReturnId(id);
        row["client"] = NativeEntityDiagnostic?.Invoke(WireReturnId(id));
        return row;
    }

    private int ChoiceEngineId(int id)
    {
#if ENGINE_SOURCE
        if (deckChoiceOriginals.TryGetValue(id, out int original) || opposingProxies.TryGetValue(id, out original)) return original;
        return EngineReturnId(id);
#else
        return id;
#endif
    }

    private Dictionary<string, object> DiagnosticChoiceEntity(int clientId)
    {
        int engineId = ChoiceEngineId(clientId);
        var row = DiagnosticEntity(engineId, includeTags: false);
        row["clientEntityId"] = WireReturnId(clientId);
        if (clientId != engineId)
        {
            var projected = DiagnosticEntity(clientId, includeTags: false);
#if ENGINE_SOURCE
            row["projection"] = projected["projection"];
#endif
            row["client"] = projected["client"];
        }
        return row;
    }

    private void RecordChoiceOpen(PowerEntityChoices choices)
    {
        // Immutable scalars only. Neither diagnostics sink may break the authoritative match.
        try
        {
            var row = new Dictionary<string, object> {
                { "type", "choice-open" }, { "matchId", MatchId }, { "turn", Game.Turn },
                { "choiceId", choices.Index }, { "published", choices.Index >= 0 }, { "choiceType", choices.ChoiceType.ToString() }, { "seat", choices.PlayerId },
                { "countMin", choices.CountMin }, { "countMax", choices.CountMax },
                { "source", DiagnosticChoiceEntity(choices.SourceId) },
                { "offers", choices.Entities.Select(DiagnosticChoiceEntity).ToArray() }
            };
#if ENGINE_SOURCE
            row["engineDecisionId"] = (choices.PlayerId == HumanPlayerId ? Game.Player1 : Game.Player2).Choice?.DecisionId;
#endif
            string serialized = null;
            matchRecorder.Write(r => serialized = (r as PlaytestRecord)?.OnChoiceOpen(row));
            ChoiceTrace?.Invoke(serialized ?? PlaytestRecord.Serialize(row));
#if ENGINE_SOURCE
            LogChoiceOpen(choices);
#endif
        }
        catch (Exception error) { try { BotTrace?.Invoke("choice-diagnostics-error=" + error.GetType().Name); } catch { } }
    }

#if ENGINE_SOURCE
    private Choice lastUnpublishedNpcChoice;
    private void RecordUnpublishedNpcChoice()
    {
        var choice = Game.Player2.Choice;
        // GENERAL/MULLIGAN/REWIND have a real packet log; ordinary NPC TARGET has no human-facing packet.
        if (choice == null || choice.ChoiceType != ChoiceType.TARGET || ReferenceEquals(choice, lastUnpublishedNpcChoice)) return;
        lastUnpublishedNpcChoice = choice;
        RecordChoiceOpen(new PowerEntityChoices(-1, choice.ChoiceType, choice.Choices ?? new List<int>()) {
            SourceId = choice.SourceId, PlayerId = BotPlayerId, CountMin = 1, CountMax = 1
        });
    }

    // Diagnostics recovery validates engine membership, independently of the client's missing choice packet.
    internal int[] ValidateRecoveryChoice(int id, int[] enginePicks)
    {
        var choice = Game.Player1.Choice;
        var decision = Game.GetPendingDecisions().SingleOrDefault(d => d.ControllerId == Game.Player1.Id);
        if (humanChoices == null || humanChoices.Index != id || choice == null || decision?.Id != humanDecisionId
            || choice.SourceId != humanChoices.SourceId || choice.ChoiceType != humanChoices.ChoiceType)
            throw new InvalidOperationException("Engine choice stale or missing");
        if (choice.ChoiceType != ChoiceType.GENERAL && choice.ChoiceType != ChoiceType.TARGET)
            throw new InvalidOperationException("Recovery supports GENERAL/TARGET choices only");
        if (enginePicks == null || enginePicks.Length < decision.MinSelections || enginePicks.Length > decision.MaxSelections
            || enginePicks.Distinct().Count() != enginePicks.Length || enginePicks.Any(pick => !decision.Options.Any(option => option.OptionId == pick)))
            throw new InvalidOperationException("Invalid engine choice membership/count");
        return enginePicks.Select(pick => WireReturnId(humanChoices.Entities.Single(clientId => ChoiceEngineId(clientId) == pick))).ToArray();
    }

    internal const double ChoiceRecoveryWaitMilliseconds = 3000;
    private (int Id, int[] Picks, Func<int, bool> Registered, Action<List<int>> Submit,
        Action<Exception> Failed, double Deadline)? pendingChoiceRecovery;

    private void FlushChoiceRecovery()
    {
        if (!pendingChoiceRecovery.HasValue) return;
        var recovery = pendingChoiceRecovery.Value;
        try
        {
            if (npcStopped || Game == null || Game.State == State.COMPLETE)
                throw new InvalidOperationException("Choice recovery interrupted before native registration");
            if (NpcClock() >= recovery.Deadline)
                throw new InvalidOperationException("Choice recovery timed out waiting for native registration: " + recovery.Id);
            var picks = ValidateRecoveryChoice(recovery.Id, recovery.Picks);
            if (!recovery.Registered(recovery.Id)) return;
            // Consume before sending: a callback or later tick must never submit this recovery twice.
            pendingChoiceRecovery = null;
            recovery.Submit(picks.ToList());
        }
        catch (Exception error)
        {
            pendingChoiceRecovery = null;
            recovery.Failed(error);
        }
    }

    internal void RecoverUnregisteredChoice(int id, int[] enginePicks, Func<int, bool> clientHasEntity,
        Func<int, bool> clientChoiceRegistered, Action<List<int>> submit, Action<Exception> failed)
    {
        ValidateRecoveryChoice(id, enginePicks);
        if (pendingChoiceRecovery.HasValue) throw new InvalidOperationException("Choice recovery already pending");
        var packet = new NativePowerHistory();
        // Rehydrate only the human's own source/offers. Queued EntityChoices registers one frame after history drains.
        foreach (int clientId in humanChoices.Entities.Concat(new[] { humanChoices.SourceId }).Where(value => value > 0).Distinct())
        {
            if (clientHasEntity(WireReturnId(clientId))) continue;
            int engineId = ChoiceEngineId(clientId);
            if (!Game.IdEntityDic.TryGetValue(engineId, out var entity))
                throw new InvalidOperationException("Missing engine choice entity " + engineId);
            var snapshot = PowerHistoryBuilder.ShowEntity(entity).Entity;
            snapshot.Id = clientId;
            if (clientId != engineId)
            {
                snapshot.Tags[GameTag.ZONE] = (int)EngineZone.SETASIDE;
                snapshot.Tags[GameTag.ZONE_POSITION] = 0;
            }
            var create = new PowerHistoryData { FullEntity = ToNativeHistoryEntity(snapshot, true) };
            packet.List.Add(create); TrackVisibility(create);
        }
        SendPowerHistory(packet);
        // As with opposing receipts, EntitiesChosen must wait until GameState registers the matching choice.
        pendingChoiceRecovery = (id, enginePicks.ToArray(), clientChoiceRegistered, submit, failed,
            NpcClock() + ChoiceRecoveryWaitMilliseconds);
    }
#endif

    private EntityChoices ToNativeChoice(PowerEntityChoices choices)
    {
        RecordChoiceOpen(choices);
        var packet = new EntityChoices
        {
            Id = choices.Index,
            ChoiceType = (int)choices.ChoiceType,
            CountMin = choices.CountMin,
            CountMax = choices.CountMax,
            Entities = new List<int>(choices.Entities),
            PlayerId = choices.PlayerId,
            HideChosen = choices.HideChosen
        };
        if (choices.SourceId > 0)
        {
            packet.Source = choices.SourceId;
#if ENGINE_SOURCE
            // Removed enchantments remain recipe sources in the engine, but native removes their entities.
            // ChoiceCardMgr.LoadChoiceCardActors dereferences its source before ForceLoadHandActor.
            bool Survives(int id) => Game.IdEntityDic.TryGetValue(id, out var entity)
                && entity[GameTag.ZONE] != (int)EngineZone.REMOVEDFROMGAME
                && wireEntities.TryGetValue(id, out var wire) && wire.Zone != (int)EngineZone.REMOVEDFROMGAME;
            int presentationSource = 0, candidate = packet.Source;
            var visited = new HashSet<int>();
            while (visited.Add(candidate) && Game.IdEntityDic.TryGetValue(candidate, out var source))
            {
                if (source is SabberStoneCore.Model.Entities.Enchantment enchantment)
                {
                    if (presentationSource == 0 && Survives(candidate)) presentationSource = candidate;
                    // TARGET must retain its recipe's input tags instead of preferring the printed creator.
                    if (choices.ChoiceType == ChoiceType.TARGET && presentationSource != 0) break;
                    if (enchantment.Creator == null) break;
                    candidate = enchantment.Creator.Id;
                }
                else
                {
                    if (Survives(candidate)) presentationSource = candidate;
                    break;
                }
            }
            // A persistent controller enchantment can outlive even its printed creator (Osk banish).
            // Players are public CREATE_GAME entities and support source.HasTag without revealing hidden cards.
            packet.Source = presentationSource != 0 ? presentationSource
                : choices.PlayerId == Game.Player2.PlayerId ? Game.Player2.Id : Game.Player1.Id;
            // TARGET arrows dereference source.GetCard(); a public hero also supplies a safe arrow origin.
            if (choices.ChoiceType == ChoiceType.TARGET && packet.Source != choices.SourceId)
                packet.Source = choices.PlayerId == Game.Player2.PlayerId ? Game.Player2.Hero.Id : Game.Player1.Hero.Id;
#endif
        }
        return packet;
    }

#if ENGINE_SOURCE
    // Native sampling and headless tests feed the same predicate; no injectable readiness verdict.
    internal sealed class ChoicePresentation
    {
        internal int ChoiceId { get; set; }
        internal ChoiceType Type { get; set; }
        internal bool Settled { get; set; }
        internal bool SourceExists { get; set; }
        internal bool CanTargetCardsInHand { get; set; } = true;
        internal int CountMin { get; set; } = 1;
        internal int StateChoiceId { get; set; } = -1;
        internal bool StateRevealed { get; set; }
        internal bool ToggleButtonExists { get; set; }
        internal bool ChoicesShown { get; set; } = true;
        internal int DisplayType { get; set; }
        internal bool ShopChoice { get; set; }
        internal bool MagicItemDiscover { get; set; }
        internal OfferPresentation[] Offers = Array.Empty<OfferPresentation>();
        internal bool? Renderable(int expectedId, bool sourceSubstituted = false)
        {
            bool sameState = StateChoiceId == ChoiceId;
            bool hidden = sameState && StateRevealed && ToggleButtonExists && !ChoicesShown;
            if (!Settled || ChoiceId != expectedId || sameState && (DisplayType != 0 || ShopChoice || MagicItemDiscover)
                || Type != ChoiceType.GENERAL && Type != ChoiceType.TARGET) return null;
            var offers = Offers.Where(offer => offer.CardId != "JAIL_319t").ToArray();
            // Native skips missing offers. Never take a choice the player can still complete.
            if (offers.Count(offer => offer.CardExists && offer.HasCardDef && offer.ActorExists
                && (hidden || !offer.InputBlocked
                    && (Type != ChoiceType.TARGET || !offer.InHand || CanTargetCardsInHand)
                    && offer.CardActive && offer.ActorActive && offer.ActorShown && offer.ColliderEnabled))
                >= (sourceSubstituted ? 1 : Math.Max(1, CountMin))) return true;
            if (offers.Any(offer => offer.Loading)) return null;
            if (!SourceExists) return false;
            if (offers.Length == 0) return null;
            return false;
        }
    }
    internal struct OfferPresentation
    {
        internal bool CardExists { get; set; }
        internal bool HasCardDef { get; set; }
        internal bool ActorExists { get; set; }
        internal bool CardActive { get; set; }
        internal bool ActorActive { get; set; }
        internal bool ActorShown { get; set; }
        internal bool ColliderEnabled { get; set; }
        internal bool Loading { get; set; }
        internal bool InHand { get; set; }
        internal bool InputBlocked { get; set; }
        internal string CardId { get; set; }
    }
    internal Func<ChoicePresentation> NativeHumanChoicePresentation { get; set; }
    private readonly NativeStallClock choiceClock = new NativeStallClock();
    private bool humanTargetSourceSubstituted;
    private static bool choiceApplicationPaused, choiceApplicationFocused = true;
    private static int choiceApplicationRevision;
    private int choiceObservedApplicationRevision;
    internal static void ChoiceApplicationPause(bool paused) { choiceApplicationPaused = paused; choiceApplicationRevision++; }
    internal static void ChoiceApplicationFocus(bool focused) { choiceApplicationFocused = focused; choiceApplicationRevision++; }
    internal void TickChoiceWatchdog()
    {
        if (choiceObservedApplicationRevision != choiceApplicationRevision)
        { choiceObservedApplicationRevision = choiceApplicationRevision; choiceClock.Invalidate(); }
        if (Game?.State != State.RUNNING || humanChoices == null || humanChoices.ChoiceType == ChoiceType.MULLIGAN
            || choiceApplicationPaused || !choiceApplicationFocused
            || NativeHumanChoicePresentation?.Invoke()?.Renderable(humanChoices.Index, humanTargetSourceSubstituted) != false)
        { choiceClock.Invalidate(); return; }
        if (!choiceClock.Observe(true, MatchId, this, humanChoices, "", NpcClock() / 1000, deadline: 15)) return;
        BotTrace?.Invoke("CHOICE_STALL_RECOVERY choice=" + humanChoices.Index + " source=" + humanChoices.SourceId
            + " picks=" + string.Join(",", humanChoices.Entities.Take(humanChoices.CountMin)) + " reason=missing-native-actors");
        // Use the ordinary validated/recorded answer path, including bound deck offers and follow-up prompts.
        if (CompleteExpiredHumanChoice(random: false)) DriveUntilHumanInput();
    }
#endif

    // The NPC mulligan's EntitiesChosen follows its EntityChoices in the same frame. At match start the client queues
    // both until the Gameplay scene has players; replaying the queued history then dequeues the EntityChoices (processed
    // a frame later) and handles the EntitiesChosen at once, which leaves the mulligan mapped for the NPC forever: every
    // later NPC choice (and any human choice that has to queue) waits behind it, so NPC card backs never show. It
    // therefore waits for the client's receipt, in order with the NPC's other deferred EntitiesChosen.
    private void SendOpposingChosen(PowerEntityChoices choices, List<int> picks)
    {
#if ENGINE_SOURCE
        if (Game.State != State.COMPLETE && (deferredOpposing.Count > 0 || !OpposingChoiceReceived(choices.Index)))
        {
            deferredOpposing.Add((choices, picks.ToList(), new HashSet<int>()));
            return;
        }
#endif
        SendChosen(choices, picks);
    }

    private void SendChosen(PowerEntityChoices choices, IEnumerable<int> picks)
    {
        send(13, new EntitiesChosen
        {
            ChooseEntities = new ChooseEntities { Id = choices.Index, Entities = picks.ToList() },
            PlayerId = choices.PlayerId,
            ChoiceType = (int)choices.ChoiceType
        });
    }

    private void SendHumanOptions()
    {
#if ENGINE_SOURCE
        if (SabberStoneCore.Actions.AutonomousTurnRules.IsActive(Game)) { humanChoices = null; humanOptions = null; return; }
#endif
        List<PlayerTask> tasks = Game.Player1.Options();
#if ENGINE_SOURCE
        tasks.RemoveAll(task => !NativeActionEnabled(task));
#endif
        if (tasks.Count == 0)
            throw new InvalidOperationException("Human has no legal options.");
        PowerAllOptions options = PowerOptionsBuilder.AllOptions(Game, tasks);
        // The pinned builder filters ChooseOne == 0, but ordinary generated tasks use -1.
        // Derive targets from the legal tasks so targeted spells/battlecries remain playable.
        foreach (var option in options.PowerOptionList.Where(o => o.MainOption != null))
            option.MainOption.Targets = tasks.Where(t => SourceId(t) == option.MainOption.EntityId &&
                    t.ChooseOne <= 0 &&
#if ENGINE_SOURCE
                t.ActionTarget != null
#else
                t.Target != null
#endif
#if STANDARD_PROOF || ENGINE_SOURCE
                    && (option.MainOption.PowerKeyword == 0 || option.MainOption.PowerKeyword == 4089)
                    && t.PlayerTaskType != PlayerTaskType.TRADE_CARD && t.PlayerTaskType != PlayerTaskType.PREPARE_CARD
#endif
                )
#if ENGINE_SOURCE
                .Select(t => t.ActionTarget.Id).Distinct().ToList();
#else
                .Select(t => t.Target.Id).Distinct().ToList();
#endif
#if ENGINE_SOURCE
        humanPlacementBoardHadLocations = Game.Player1.BoardZone.HasLocations || Game.Player2.BoardZone.HasLocations;
        if (tasks.Any(task => task is ActivateLocationTask || task is ActivateOldGodTask))
            humanDecisionId = Game.GetPendingDecisions().Single(decision =>
                decision.Kind == SabberStoneCore.Model.Decisions.DecisionKind.Options && decision.ControllerId == Game.Player1.Id).Id;
#endif
        humanOptions = options;
        humanChoices = null;
        send(14, new AllOptions
        {
            Id = options.Index,
            Options = options.PowerOptionList.Select(ToNativeOption).ToList()
        });
    }

    private static NativeOption ToNativeOption(PowerOption option)
    {
        return new NativeOption
        {
            Type_ = (NativeOption.Type)(int)option.OptionType,
            MainOption = ToNativeSubOption(option.MainOption),
            SubOptions = option.SubOptions.Select(ToNativeSubOption).ToList()
        };
    }

    private static SubOption ToNativeSubOption(PowerSubOption option)
    {
        if (option == null)
            return null;
        return new SubOption
        {
            Id = option.EntityId,
            PlayError = 0,
#if STANDARD_PROOF || ENGINE_SOURCE
            PowerKeyword = option.PowerKeyword,
#else
            PowerKeyword = 0,
#endif
            Targets = option.Targets == null
                ? new List<TargetOption>()
                : option.Targets.Distinct().Select(id => new TargetOption { Id = id, PlayError = 0 }).ToList()
        };
    }

#if ENGINE_SOURCE
    private readonly Dictionary<int, int> temporaryHandOrigins = new Dictionary<int, int>();
    private void TrackTemporaryOrigins(List<IPowerHistoryEntry> entries)
    {
        var marked = new HashSet<int>(entries.OfType<SabberStoneCore.Kettle.PowerHistoryTagChange>()
            .Where(t => t.Tag == SabberStoneCore.Actions.TemporaryRules.Temporary && t.Value == 1).Select(t => t.EntityId));
        if (marked.Count == 0) return;
        foreach (var full in entries.OfType<PowerHistoryFullEntity>())
            if (marked.Contains(full.Entity.Id) &&
                (TagValue(full.Entity.Tags, GameTag.ZONE) == (int)EngineZone.INVALID || TagValue(full.Entity.Tags, GameTag.ZONE) == (int)EngineZone.SETASIDE))
                temporaryHandOrigins[full.Entity.Id] = TagValue(full.Entity.Tags, GameTag.CONTROLLER);
    }
#endif

#if ENGINE_SOURCE
    // Local diagnostics only: decodes the real game's history for the match log and the journal. Reads, never writes.
    private bool EnsureEventLog()
    {
        if (EventTrace == null && !matchRecorder.Enabled) return false;
        if (eventLog == null)
        {
            eventBatch = new List<string>();
            eventLog = new MatchEventLog(MatchId, line => {
                eventBatch.Add(line);
                try { EventTrace?.Invoke(line); } catch (Exception) { }
            }, HumanPlayerId.ToString(), BotPlayerId.ToString());
        }
        eventBatch.Clear();
        return true;
    }

    private void FlushEventBatch()
    {
        if (eventBatch.Count == 0) return;
        var lines = eventBatch.ToArray();
        matchRecorder.Write(r => (r as PlaytestRecord)?.OnEvents(lines));
    }

    private void ObserveMatchEvents(List<IPowerHistoryEntry> entries)
    {
        try
        {
            if (!EnsureEventLog()) return;
            eventLog.Observe(Game, entries);
            FlushEventBatch();
        }
        catch (Exception error) { try { BotTrace?.Invoke("event-log-error=" + error.GetType().Name); } catch { } }
    }

    private void LogChoiceOpen(PowerEntityChoices choices)
    {
        try
        {
            if (!EnsureEventLog()) return;
            eventLog.ChoiceOpen(Game, choices.PlayerId, choices.ChoiceType.ToString(), choices.SourceId, choices.Entities);
            FlushEventBatch();
        }
        catch (Exception error) { try { BotTrace?.Invoke("event-log-error=" + error.GetType().Name); } catch { } }
    }
#endif

    private void SendHistory(List<IPowerHistoryEntry> entries)
    {
        if (entries == null)
            return;
#if ENGINE_SOURCE
        ObserveMatchEvents(entries);
        identityBatch = entries;
        identityPosition = -1;
        var concealedOpposing = ConcealOpposingChoice();
        if (Game.State == State.COMPLETE) startOfGameClosed = true;
        // Register eligible originals before their initial hidden snapshots, without capturing future effects.
        if (!startOfGameClosed)
        {
            foreach (var source in Game.IdEntityDic.Values.Where(e => e[SabberStoneCore.Actions.StartOfGameRules.Pending] == 1))
                startOfGameSources.Add(source.Id);
            foreach (var source in entries.OfType<PowerHistoryBlockStart>().Where(s => s.BlockType == BlockType.TRIGGER
                && s.EffectIndex == -1 && !s.TriggerKeyword.HasValue))
                if (Game.IdEntityDic.TryGetValue(source.Source, out var entity) && SabberStoneCore.Actions.StartOfGameRules.HasEffect(LookupCard(IdentityAt(entries.IndexOf(source), source.Source, true, true, definitionOnly: true)?.Name)))
                    startOfGameSources.Add(source.Source);
        }
        RefreshPrivacyTargets();
        TrackTemporaryOrigins(entries);
        int startOfGameSource = 0, startOfGameBody = 0;
        bool startOfGameRevealed = false;
        PowerHistoryStart startOfGameEffect = null;
        EngineHistoryEntity startOfGameIdentity = null;
#endif
        var packet = new NativePowerHistory();
        var petEvents = cosmeticPet == null ? null : petReactions.Project(entries, HumanPlayerId, cosmeticPetEntityId);
        if (petLikes != null && !petLikesFaulted)
        {
            // Presentation and XP only: a fault here must never break the history stream. Log once, stop evaluating for the
            // rest of the match (what was banked so far still settles) and carry on.
            try
            {
                foreach (var pair in petLikes.Project(entries, HumanPlayerId, cosmeticPetEntityId))
                {
                    // Counter ticks follow the pet's own reactions to the same entry.
                    if (petEvents.TryGetValue(pair.Key, out var reactionsHere)) reactionsHere.AddRange(pair.Value);
                    else petEvents[pair.Key] = pair.Value;
                }
            }
            catch (Exception error)
            {
                petLikesFaulted = true;
                BotTrace?.Invoke("pet likes disabled for this match: " + error.GetType().Name + " " + error.Message);
            }
        }
        // The NPC pet sees the same history from its owner's side: friendly = NPC events, opposing = the human's.
        var npcPetEvents = OpponentPetCard == null ? null : npcPetReactions.Project(entries, BotPlayerId, opponentPetEntityId);
#if STANDARD_PROOF || ENGINE_SOURCE
        int tradeSource = 0;
        bool tradeHidden = false;
#endif
        for (int index = 0; index < entries.Count; index++)
        {
#if ENGINE_SOURCE
            identityPosition = index;
#endif
            AppendCopyCompletions(packet, entries, index);
            IPowerHistoryEntry entry = entries[index];
            bool firstPowerTransfer = entry is SabberStoneCore.Kettle.PowerHistoryTagChange controllerChange
                && controllerChange.Tag == GameTag.CONTROLLER && controllerChange.Value != 0
                && powerControllers.TryGetValue(controllerChange.EntityId, out int previousController)
                && previousController != controllerChange.Value && !transferredPowers.Contains(controllerChange.EntityId);
            RestoreRetiredChoiceActor(packet, entry);
#if ENGINE_SOURCE
            EndEffectPlayScope(entry);
            if (entry is PowerHistoryRetainedOverdraw overdraw)
            {
                AppendRetainedOverdraw(packet, overdraw);
                continue;
            }
            if (entry is PowerHistoryBlockStart resetStart && resetStart.BlockType == BlockType.GAME_RESET)
            {
                HideNpcRope();
                startOfGameClosed = true;
                index = AppendRewindReset(packet, entries, index);
                identityPosition = index; // Privacy reconstruction belongs to the restored checkpoint, not the abandoned attempt.
                RefreshPrivacyTargets();
                continue;
            }
            if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange openingStep && openingStep.EntityId == Game.Id)
            {
                if ((openingStep.Tag == GameTag.STEP || openingStep.Tag == GameTag.NEXT_STEP) && openingStep.Value == (int)Step.MAIN_BEGIN)
                    startOfGameMainBegin = true;
                if (openingStep.Tag == GameTag.STEP && openingStep.Value == (int)Step.MAIN_READY)
                    startOfGameClosed = true;
                // Only the opening barrier was projected early; every later turn keeps its own NEXT_STEP=MAIN_READY.
                if (!startOfGameClosed && openingStep.Tag == GameTag.NEXT_STEP && openingStep.Value == (int)Step.MAIN_READY)
                {
                    if (startOfGameBarrier) continue;
                    startOfGameBarrier = true;
                }
            }
            if (!startOfGameClosed && entry is PowerHistoryBlockStart sog && visualBlockSources.Count == 0
                && sog.BlockType == BlockType.TRIGGER && sog.EffectIndex == -1 && !sog.TriggerKeyword.HasValue
                && (StartOfGameMulligansDone() || startOfGameMainBegin && !clientTransportTags.Values.Any(tags => tags.ContainsKey((int)GameTag.MULLIGAN_STATE)))
                && Game.IdEntityDic.TryGetValue(sog.Source, out var sogSource)
                && IdentityAt(index, sog.Source, true, true, definitionOnly: true) is { } sogIdentity
                && SabberStoneCore.Actions.StartOfGameRules.HasEffect(LookupCard(sogIdentity.Name))
                && presentedStartOfGameSources.Add(sog.Source))
            {
                AppendStartOfGameBarrier(packet);
                startOfGameSource = sog.Source;
                startOfGameIdentity = sogIdentity;
                startOfGameRevealed = AppendStartOfGameReveal(packet, sogSource, sogIdentity);
                var effect = ToNativeHistory(sog);
                startOfGameEffect = effect.PowerStart;
                effect.PowerStart.TriggerKeyword = 968;
                effect.PowerStart.ShowInHistory = true;
                packet.List.Add(effect);
                TrackVisibility(effect);
                var historySource = new PowerHistoryData { MetaData = new PegasusGame.PowerHistoryMetaData {
                    Type = (HistoryMeta.Type)8, Data = 0, Info = new List<int> { sog.Source }
                } };
                packet.List.Add(historySource);
                TrackVisibility(historySource);
                startOfGameBody = packet.List.Count;
                continue;
            }
            // Keep a revealed source alive through its real effects/history targets; hide once at the effect tail.
            if (startOfGameRevealed && startOfGameSource != 0 && entry is PowerHistoryHideEntity sourceHide
                && sourceHide.EntityID == startOfGameSource)
            {
                TrackHistoricalState(entry);
                continue;
            }
            if (entry is PowerHistoryBlockEnd && visualBlockSources.Count == 1)
            {
                if (startOfGameSource != 0)
                {
                    startOfGameEffect.EffectIndex = StartOfGameSlots(Game.IdEntityDic[startOfGameSource], startOfGameIdentity.Name).Effect;
                    AppendStartOfGameEnd(packet, startOfGameSource, startOfGameRevealed, startOfGameBody);
                    startOfGameSource = 0;
                }
                // DONE has reached the client, but its animations have not. Arm native mulliganBusy inside its scope.
                else if (!startOfGameClosed && StartOfGameMulligansDone()
                    && (visualBlockSources.Peek() == Game.Player1.Id || visualBlockSources.Peek() == Game.Player2.Id))
                    AppendStartOfGameBarrier(packet);
            }
#endif
            SabberStoneCore.Kettle.PowerHistoryTagChange movedCost = null;
            if (entry is PowerHistoryBlockStart play && play.BlockType == BlockType.PLAY
#if ENGINE_SOURCE
                // Historical zone distinguishes activation from a location just played from hand.
                && !(LookupCard(IdentityAt(index, play.Source, true, true, definitionOnly: true)?.Name)?.Type == CardType.HERO_POWER
                    || LookupCard(IdentityAt(index, play.Source, true, true, definitionOnly: true)?.Name)?.Type == CardType.LOCATION
                    && historicalZones.TryGetValue(play.Source, out var playZone) && playZone == EngineZone.PLAY)
#endif
                )
            {
                EngineHistoryEntity frozenPlay = null;
                for (int ahead = index + 1; ahead < entries.Count; ahead++)
                {
                    // An exit belongs to this PLAY only while the source keeps its identity and visibility.
                    if (entries[ahead] is PowerHistoryChangeEntity changed && changed.Entity.Id == play.Source
                        || entries[ahead] is PowerHistoryFullEntity created && created.Entity.Id == play.Source
                        || entries[ahead] is PowerHistoryHideEntity hidden && hidden.EntityID == play.Source) break;
                    if (entries[ahead] is PowerHistoryShowEntity show && show.Entity.Id == play.Source
                        && IsPublicExitReveal(entries, ahead))
                    {
                        frozenPlay = show.Entity;
                        break;
                    }
                }
                bool automaticPlay = !NativePetReactions.IsCardPlay(play);
                bool shownForPlay = AddMissingPlayReveal(packet, play.Source, frozenPlay, automaticPlay, index);
#if ENGINE_SOURCE
                if (shownForPlay && automaticPlay) scopedRevealSource = play.Source;
#endif
                if (index + 1 < entries.Count
                    && entries[index + 1] is SabberStoneCore.Kettle.PowerHistoryTagChange next
                    && next.EntityId == play.Source && next.Tag == GameTag.TAG_LAST_KNOWN_COST_IN_HAND)
                    movedCost = next;
                // An automatic play paid nothing from hand: no last-known hand cost.
                if (revealedEntities.Contains(play.Source) && NativePetReactions.IsCardPlay(play))
                {
                    int paidCost = movedCost?.Value ?? VisibleTransportTag(play.Source, GameTag.COST);
                    PowerHistoryData cost = ToNativeHistory(PowerHistoryBuilder.TagChange(
                        play.Source, GameTag.TAG_LAST_KNOWN_COST_IN_HAND, paidCost));
                    if (cost != null)
                    {
                        packet.List.Add(cost);
                        TrackVisibility(cost);
                    }
                    else movedCost = null;
                }
                else movedCost = null;
            }
            if (entry is PowerHistoryBlockStart trigger && trigger.BlockType != BlockType.PLAY)
                AddSecretActivationReveal(packet, trigger.Source);
#if STANDARD_PROOF || ENGINE_SOURCE
            bool prepare = false;
            if (entry is PowerHistoryBlockStart tradeStart && tradeStart.BlockType == BlockType.DECK_ACTION)
            {
                prepare = index + 1 < entries.Count
                    && entries[index + 1] is SabberStoneCore.Kettle.PowerHistoryTagChange prepareFlag
                    && prepareFlag.EntityId == tradeStart.Source && prepareFlag.Tag == GameTag.IS_USING_PREPARE_OPTION && prepareFlag.Value == 1;
                if (!prepare)
                {
#if ENGINE_SOURCE
                    var traded = IdentityAt(index, tradeStart.Source, true, true, definitionOnly: true);
                    // Composed cards can be Tradeable even when their registry template is not.
                    int tradeable = traded != null && traded.Tags.TryGetValue(GameTag.TRADEABLE, out int captured)
                        ? captured : DefinitionTag(traded?.Name, GameTag.TRADEABLE, 0);
#else
                    int tradeable = Game.IdEntityDic.TryGetValue(tradeStart.Source, out var traded) ? traded.Card[GameTag.TRADEABLE] : 0;
#endif
                    if (tradeable != 1) throw new InvalidOperationException("Unexpected deck-action source.");
                }
                // Prepare retains hidden hand identity; only Trade has a forced reveal.
                tradeSource = prepare ? 0 : tradeStart.Source;
                tradeHidden = false;
            }
            PowerHistoryData native;
            if (entry is PowerHistoryShowEntity tradeShow && tradeShow.Entity.Id == tradeSource
                && TagValue(tradeShow.Entity.Tags, GameTag.IS_USING_TRADE_OPTION) == 1)
                // Trade names the engine's snapshot at this entry, never its current entity.
                native = new PowerHistoryData { ShowEntity = ToNativeHistoryEntity(tradeShow.Entity, true) };
            else if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange tradeReset && tradeHidden
                && tradeReset.EntityId == tradeSource && tradeReset.Tag == GameTag.IS_USING_TRADE_OPTION && tradeReset.Value == 0)
            {
                native = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange
                    { Entity = tradeSource, Tag = 2045, Value = 0 } };
                tradeSource = 0;
            }
            else if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange tradeFlag && tradeFlag.EntityId == tradeSource
                && tradeFlag.Tag == GameTag.IS_USING_TRADE_OPTION && tradeFlag.Value == 1)
                native = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange { Entity=tradeSource,Tag=2045,Value=1 } };
            else native = ToNativeHistory(entry);
            if (entry is PowerHistoryHideEntity tradeHide && tradeHide.EntityID == tradeSource) tradeHidden = true;
#else
            PowerHistoryData native = ToNativeHistory(entry);
#endif
            // Keep the pre-PLAY reveal; an identical frozen exit must not reload native identity twice.
            if (IsPublicExitReveal(entries, index))
            {
                var exit = ((PowerHistoryShowEntity)entry).Entity;
                native = new PowerHistoryData { ShowEntity = ToNativeHistoryEntity(exit, true) };
                var previous = packet.List.LastOrDefault(p =>
                    p.HasShowEntity && p.ShowEntity.Entity == exit.Id || p.HasFullEntity && p.FullEntity.Entity == exit.Id
                    || p.HasChangeEntity && p.ChangeEntity.Entity == exit.Id || p.HasHideEntity && p.HideEntity.Entity == exit.Id);
                var snapshot = previous == null ? null : previous.HasShowEntity ? previous.ShowEntity
                    : previous.HasFullEntity ? previous.FullEntity : previous.HasChangeEntity ? previous.ChangeEntity : null;
                if (snapshot != null && snapshot.Name == native.ShowEntity.Name
                    && snapshot.Tags.Count == native.ShowEntity.Tags.Count
                    && snapshot.Tags.All(t => native.ShowEntity.Tags.Any(other => other.Name == t.Name && other.Value == t.Value))
                    && visibleTransportTags.TryGetValue(exit.Id, out var currentTags)
                    && native.ShowEntity.Tags.All(t => currentTags.TryGetValue(t.Name, out int value) && value == t.Value))
                    native = null;
            }
            if (native != null)
            {
#if ENGINE_SOURCE
                AppendTemporaryHelperClears(packet, native);
#endif
#if ENGINE_SOURCE || STANDARD_PROOF
                // HIDE retains POWERED_UP on the client; clear our hand glow before losing visibility.
                if (native.HasHideEntity && poweredUpHighlights.Contains(native.HideEntity.Entity)
                    && Game.IdEntityDic.TryGetValue(native.HideEntity.Entity, out var hiding)
#if !ENGINE_SOURCE
                    && hiding.Card[(GameTag)1333] == 1
#endif
                    )
                    AppendPoweredUpHighlight(packet, hiding.Id, 0);
#endif
                if (native.HasTagChange && native.TagChange.Tag == (int)GameTag.OVERLOAD_LOCKED
                    && historicalMana.TryGetValue(native.TagChange.Entity, out var mana))
                {
                    // Retail spends locked crystals before applying their lock visuals.
                    var spent = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                        Entity = native.TagChange.Entity, Tag = (int)GameTag.RESOURCES_USED, Value = mana.Used + mana.Locked
                    } };
                    packet.List.Add(spent);
                    TrackVisibility(spent);
                }
#if ENGINE_SOURCE
                RecordEngineIdentity(native, entry);
                nativeEngineOrder.Add(new KeyValuePair<PowerHistoryData, int>(native, index));
#endif
                packet.List.Add(native);
#if ENGINE_SOURCE
                if (native.HasFullEntity && native.FullEntity.Name == "EX1_323w" && visualBlockSources.Count > 0
                    && IdentityAt(index, visualBlockSources.Peek(), true, true, definitionOnly: true)?.Name == "CORE_EX1_323")
                {
                    // SetupCards resolves the weapon's current zone before queued tasks run; INVALID leaves no summon callback.
                    if (index + 1 < entries.Count && entries[index + 1] is SabberStoneCore.Kettle.PowerHistoryTagChange equip
                        && equip.EntityId == native.FullEntity.Entity && equip.Tag == GameTag.ZONE && equip.Value == (int)EngineZone.PLAY)
                        native.FullEntity.Tags.Single(tag => tag.Name == (int)GameTag.ZONE).Value = equip.Value;
                    MoveJaraxxusPowerCreation(packet, line => BotTrace?.Invoke(line));
                }
#endif
                if (native.HasHideEntity && visibleTransportTags.TryGetValue(native.HideEntity.Entity, out var hidingTags)
                    && hidingTags.TryGetValue((int)GameTag.REVEALED, out int wasRevealed) && wasRevealed != 0)
                {
                    // Entity.OnHideEntity clears CardID/stats but retains REVEALED. Hidden draws must be backs.
                    var clearReveal = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                        Entity = native.HideEntity.Entity, Tag = (int)GameTag.REVEALED, Value = 0
                    } };
                    packet.List.Add(clearReveal);
                    TrackVisibility(clearReveal);
                }
                TrackVisibility(native);
                if (firstPowerTransfer && native.HasTagChange)
                    AppendTransferredPowerIdentity(packet, native.TagChange.Entity);
                if (native.CreateGame != null || native.ResetGame != null) {
                    if (cosmeticPet != null) packet.List.Add(NativePetEntity(cosmeticPetEntityId, cosmeticPet, HumanPlayerId, cosmeticPetVariant, cosmeticPetFamilyId, HumanPetTreatTags()));
                    if (OpponentPetCard != null) packet.List.Add(NativePetEntity(opponentPetEntityId, OpponentPetCard, BotPlayerId, opponentPetVariant, opponentPetFamilyId));
                }
#if ENGINE_SOURCE
                AppendMovedBoardPosition(packet, native, entries, index);
#endif
#if STANDARD_PROOF || ENGINE_SOURCE
                if (prepare)
                {
                    // PowerProcessor pre-applies 4459 inside DECK_ACTION; hidden history otherwise waits forever.
                    var marker = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                        Entity = native.PowerStart.Source, Tag = (int)GameTag.PREPARE_REVEALED, Value = 1
                    } };
                    packet.List.Add(marker);
                    TrackVisibility(marker);
                }
#endif
                // Engine resets Combo at the next own turn; retail clears it when this turn ends.
                if (native.HasTagChange && native.TagChange.Entity == Game.Player1.Id
                    && native.TagChange.Tag == (int)GameTag.CURRENT_PLAYER && native.TagChange.Value == 0
                    && Game.Player1.IsComboActive)
                {
                    var comboReset = ToNativeHistory(PowerHistoryBuilder.TagChange(Game.Player1.Id, GameTag.COMBO_ACTIVE, 0));
                    packet.List.Add(comboReset);
                    TrackVisibility(comboReset);
                }
            }
            if (petEvents != null && petEvents.TryGetValue(index, out var reactions)) packet.List.AddRange(reactions);
            if (npcPetEvents != null && npcPetEvents.TryGetValue(index, out var npcReactions)) packet.List.AddRange(npcReactions);
            TrackHistoricalState(entry);
            if (movedCost != null)
            {
                TrackHistoricalState(movedCost);
                AppendCopyCompletions(packet, entries, index + 1);
                index++;
            }
        }
#if ENGINE_SOURCE
        identityPosition = entries.Count; // Endpoint refreshes describe now, never an earlier history entry.
#endif
        AppendCopyCompletions(packet, entries, entries.Count);
        copyCompletions.RemoveAll(completion => ReferenceEquals(completion.Entries, entries));
#if ENGINE_SOURCE
        AppendPrivateAttachments(packet);
#endif
        // Historical batches retain their immutable copy/aura values, not today's getters.
        if (ReferenceEquals(entries, Game.PowerHistory.Last)) AppendVisibleStats(packet);
        // shortcut: batch-end summary covers current static Azure/Thalnos; upgrade exact history timing before intra-action spellpower-changing damage cards.
        AppendSpellPowerSummary(packet, Game.Player1);
        AppendSpellPowerSummary(packet, Game.Player2);
#if ENGINE_SOURCE || STANDARD_PROOF
        AppendPoweredUpHighlights(packet);
#endif
#if ENGINE_SOURCE || MODERN_MECHANICS
        AppendHeraldState(packet);
#endif
#if ENGINE_SOURCE
        AppendDecisionHandTarget(packet);
#endif
#if ENGINE_SOURCE
        AppendSharedStoredReveals(packet);
        AppendEntityReferenceRefresh(packet);
#endif
        SendPowerHistory(packet);
#if ENGINE_SOURCE
        PublishOpposingChoice(concealedOpposing);
        RecordUnpublishedNpcChoice();
        if (Game.Rewind == null) { rewindPrivacy = null; rewindDerivedPremiums = null; rewindHighlights = null; rewindChoiceOffers = null; rewindAnonymousEntities = null; }
#endif
#if STANDARD_PROOF
        if (pendingTrade != null)
        {
            pendingTrade.nativeHistory=NormalizeTradeNativeHistory(packet.List,pendingTrade.source,pendingTrade.player);
            LastTradeSource=pendingTrade.source; LastTradeAction=pendingTrade.action;
            TradeTrace?.Invoke(StandardProofNative.Json(pendingTrade));
            pendingTrade=null;
        }
#endif
    }

#if STANDARD_PROOF
    private static string[] NormalizeTradeEngineHistory(IEnumerable<IPowerHistoryEntry> entries,int source,int player)
    {
        var rows = new List<string>();
        foreach (var entry in entries)
        {
            if (entry is PowerHistoryBlockStart start) rows.Add("start:"+(int)start.BlockType);
            else if (entry is PowerHistoryBlockEnd) rows.Add("end");
            else if (entry is PowerHistoryShowEntity show)
            {
                if (show.Entity.Id == source) rows.Add("source-show:"+show.Entity.Name+":2045="+TagValue(show.Entity.Tags,(GameTag)2045));
                else rows.Add(player==HumanPlayerId?"draw:"+show.Entity.Id+":"+show.Entity.Name:"draw:hidden");
            }
            else if (entry is PowerHistoryHideEntity hide && hide.EntityID==source) rows.Add("source-hide:"+(int)hide.Zone);
            else if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange tag && tag.EntityId==source)
                rows.Add("source-tag:"+(int)tag.Tag+":"+(tag.Tag==GameTag.ZONE_POSITION?"private":tag.Value.ToString()));
        }
        return rows.ToArray();
    }
    private static string[] NormalizeTradeNativeHistory(IEnumerable<PowerHistoryData> entries,int source,int player)
    {
        var rows = new List<string>();
        foreach(var entry in entries)
        {
            if(entry.HasPowerStart) rows.Add("start:"+(int)entry.PowerStart.Type);
            else if(entry.HasPowerEnd) rows.Add("end");
            else if(entry.HasShowEntity)
            {
                var show=entry.ShowEntity;
                if(show.Entity==source) rows.Add("source-show:"+show.Name+":2045="+(show.Tags.FirstOrDefault(tag=>tag.Name==2045)?.Value??0));
                else rows.Add(player==HumanPlayerId?"draw:"+show.Entity+":"+show.Name:"draw:hidden");
            }
            else if(entry.HasHideEntity && entry.HideEntity.Entity==source) rows.Add("source-hide:"+entry.HideEntity.Zone);
            else if(entry.HasTagChange && entry.TagChange.Entity==source)
                rows.Add("source-tag:"+entry.TagChange.Tag+":"+(entry.TagChange.Tag==263?"private":entry.TagChange.Value.ToString()));
        }
        return rows.ToArray();
    }
#endif

    private int VisibleTransportTag(int entityId, GameTag tag)
    {
        if (!visibleTransportTags.TryGetValue(entityId, out var tags)
            || !tags.TryGetValue((int)tag, out int value))
            throw new InvalidOperationException("Visible transport tag is unavailable: " + entityId + ":" + tag);
        return value;
    }

#if ENGINE_SOURCE
    private int decisionHandTargetSource;
    private int substitutedTargetSource, substitutedTargetHandTag;

    private void AppendTargetSourceTag(NativePowerHistory packet, int source, int value)
    {
        var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
            Entity = source, Tag = 1508, Value = value } };
        packet.List.Add(change);
        TrackVisibility(change);
    }

    private void ClearSubstitutedTargetSource(NativePowerHistory packet)
    {
        if (substitutedTargetSource == 0) return;
        AppendTargetSourceTag(packet, substitutedTargetSource, substitutedTargetHandTag);
        substitutedTargetSource = 0;
    }

    private void AppendDecisionHandTarget(NativePowerHistory packet)
    {
        if (humanChoices == null || Game.State != State.RUNNING || Game.Player1.Choice == null)
            ClearSubstitutedTargetSource(packet);
        // Endpoint-only decision marker, after all identity changes; emits no card name.
        var choice = Game.Player1.Choice;
        int source = Game.State == State.RUNNING && Game.Resolution.HasSuspendedEffect &&
            choice?.ChoiceType == ChoiceType.TARGET &&
            choice.Choices.Any(id => Game.IdEntityDic[id].Zone?.Type == EngineZone.HAND)
                ? choice.SourceId : 0;
        // Every hand selector needs the capability, including mixed hand/board and trigger sources.
        foreach (int id in new[] { decisionHandTargetSource, source }.Where(id => id != 0).Distinct())
        {
            int value = id == source ? 1 : Game.IdEntityDic[id][(GameTag)1508];
            if (visibleTransportTags.TryGetValue(id, out var tags) && tags.TryGetValue(1508, out int old) && old == value) continue;
            AppendTargetSourceTag(packet, id, value);
        }
        decisionHandTargetSource = source;
    }
#endif

#if ENGINE_SOURCE
    private static readonly GameTag[] CardTextTags = {
        GameTag.TAG_SCRIPT_DATA_NUM_1, GameTag.TAG_SCRIPT_DATA_NUM_2, (GameTag)955, (GameTag)1115, (GameTag)1116
    };

    private static int AlternateCardText(string cardId, EngineZone zone, int number, int turnsLeft, int current)
    {
        switch (cardId)
        {
            case "FIR_911": case "FIR_914": case "FIR_916": return turnsLeft == 1 ? 1 : 0;
            case "CATA_132": return zone == EngineZone.PLAY ? 2 : number == 0 ? 1 : 0;
            default: return current;
        }
    }
#endif

    // Owned premium of a deck copy or the starting hero; 0 leaves engine-authored values (e.g. Zola's Golden copy) intact.
    private int EntityPremium(int id, string cardId)
    {
        int premium = SourcePremium(id);
        if (premium != 0) return premium;
        return cosmeticHero != null && id == Game.Player1.Hero.Id && cardId == cosmeticBaseHero
            ? cosmeticHeroPremium : 0;
    }

    // Owned or derived premium only (never the cosmetic hero); derived wins because it only exists where it beats owned.
    // A deck-choice proxy shows its original's premium.
    private int SourcePremium(int id)
    {
#if ENGINE_SOURCE
        if (deckChoiceOriginals.TryGetValue(id, out int original)) id = original;
#endif
        return derivedPremiums.TryGetValue(id, out int premium) || entityPremiums.TryGetValue(id, out premium) ? premium : 0;
    }

    private static int PremiumRank(int premium) => premium == 2 ? 3 : premium == 3 ? 2 : premium;

    // Diamond -> Diamond/Signature/Golden by what the result card defines; Signature -> Signature/Golden; Golden -> Golden.
    internal static int ResolvePremium(int source, EngineCard result) =>
        source == 2 ? (result != null && result[(GameTag)1932] == 1 ? 2 : result != null && result[(GameTag)2589] == 1 ? 3 : 1)
        : source == 3 ? (result != null && result[(GameTag)2589] == 1 ? 3 : 1)
        : source == 1 ? 1 : 0;

    // Best-ranked premium of the innermost block's source and target, from either side (wiki: quality is contagious).
    private int BlockPremium()
    {
        if (premiumBlocks.Count == 0) return 0;
        var block = premiumBlocks.Peek();
        int source = InvolvedPremium(block.Key), target = InvolvedPremium(block.Value);
        return PremiumRank(target) > PremiumRank(source) ? target : source;
    }

    // A hidden NPC card credits nothing, so a public creation never reveals its premium; the human's own cards always do.
    private int PublicSourcePremium(int id) =>
        Game.IdEntityDic.TryGetValue(id, out var entity) && (entity.Controller?.PlayerId == HumanPlayerId || !IsHiddenFromHuman(id, null))
            ? SourcePremium(id) : 0;

    // An enchantment-owned effect carries the quality of the card that granted it and of the card it sits on.
    private int InvolvedPremium(int id)
    {
        int premium = PublicSourcePremium(id);
        if (!Game.IdEntityDic.TryGetValue(id, out var entity) || !(entity is SabberStoneCore.Model.Entities.Enchantment enchantment)) return premium;
        int creator = PublicSourcePremium(enchantment.Creator?.Id ?? 0), attached = PublicSourcePremium(enchantment.Target?.Id ?? 0);
        foreach (int other in new[] { creator, attached })
            if (PremiumRank(other) > PremiumRank(premium)) premium = other;
        return premium;
    }

    // Anonymous deck FULLs omit identity tags; resolve their premium policy at that FULL's history position.
    private bool CarriesPremium(EngineHistoryEntity snapshot, out string cardId)
    {
        Game.IdEntityDic.TryGetValue(snapshot.Id, out var entity);
#if ENGINE_SOURCE
        var identity = string.IsNullOrEmpty(snapshot.Name) ? IdentityAt(identityPosition, snapshot.Id, true, true, definitionOnly: true) : snapshot;
        cardId = identity?.Name;
#else
        cardId = string.IsNullOrEmpty(snapshot.Name) ? entity?.Card.Id : snapshot.Name;
#endif
        int controller = snapshot.Tags.TryGetValue(GameTag.CONTROLLER, out int tagged) ? tagged : entity?.Controller?.PlayerId ?? 0;
        int type = snapshot.Tags.TryGetValue(GameTag.CARDTYPE, out tagged) ? tagged : (int?)LookupCard(cardId)?.Type ?? 0;
        return controller != 0
            && type != (int)CardType.ENCHANTMENT && type != (int)CardType.PLAYER && type != (int)CardType.GAME;
    }

    private static EngineCard LookupCard(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return null;
        try { return EngineCards.FromId(cardId); }
        catch (KeyNotFoundException) { return null; }
    }

    private void DeriveCreatedPremium(int id, string cardId)
    {
        int source = BlockPremium();
        var result = source == 0 ? null : LookupCard(cardId);
        if (result != null) derivedPremiums[id] = ResolvePremium(source, result);
    }

    // A transform re-resolves the entity's premium (and the involved cards') against the new card, downward too.
    private void DeriveChangedPremium(int id, string cardId, bool inBlock)
    {
        var result = LookupCard(cardId);
        int current = SourcePremium(id);
        if (result == null || current == 0 && !inBlock) return;
        int premium = ResolvePremium(current, result);
        int fromBlock = inBlock ? ResolvePremium(BlockPremium(), result) : 0;
        if (PremiumRank(fromBlock) > PremiumRank(premium)) premium = fromBlock;
        if (premium != 0 || current != 0) derivedPremiums[id] = premium;
    }

    // Copies of the saved deck's per-copy premiums when they match the deck exactly; otherwise null (all Normal).
    // Never throws: premium is presentation only and must not refuse a match.
    internal static LocalDeckCard[] PremiumEntries(IEnumerable<string> deckIds, IEnumerable<LocalDeckCard> entries,
        Action<string> warn = null)
    {
        if (entries == null) return null;
        try
        {
            var copies = entries.Select(card => card == null ? null : new LocalDeckCard { Id = card.Id, Premium = card.Premium }).ToArray();
            if (copies.Any(card => card == null || card.Id == null || card.Premium < 0 || card.Premium > 3)
                || !copies.Select(card => card.Id).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual((deckIds ?? Enumerable.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal)))
            {
                warn?.Invoke("Premium entries do not match the selected deck; showing Normal cards.");
                return null;
            }
            return copies.Any(card => card.Premium != 0) ? copies : null;
        }
        catch (Exception error)
        {
            warn?.Invoke("Premium entries unavailable; showing Normal cards: " + error.Message);
            return null;
        }
    }

    // Owned premium overrides PREMIUM and both quality flags (matching 1, other 0); null keeps the engine value.
    private static int? OwnedPremiumValue(int tag, int premium) =>
        premium == 0 ? (int?)null
        : tag == (int)GameTag.PREMIUM ? premium
        : tag == 1932 ? (premium == 2 ? 1 : 0)
        : tag == 2589 ? (premium == 3 ? 1 : 0)
        : (int?)null;

    // Live getters below serve endpoint stat refresh/audits, never names or historical privacy decisions.
    private int PresentedTagValue(IPlayable entity, GameTag tag) =>
        tag == GameTag.GHOSTLY && temporaryDiscardVisuals.Contains(entity.Id) && entity.Zone?.Type == EngineZone.GRAVEYARD ? 1
        : SkinTag(entity.Id, entity.Card.Id, (int)tag) ?? OwnedPremiumValue((int)tag, EntityPremium(entity.Id, entity.Card.Id)) ?? PrintedTagValue(entity, tag);

    private IEnumerable<KeyValuePair<GameTag, int>> PresentedStats(IPlayable entity)
    {
        int premium = EntityPremium(entity.Id, entity.Card.Id);
        var stats = VisibleStats(entity);
#if ENGINE_SOURCE
        // Player restrictions have no card-local setter; send explicit expiry zeros once published, in any zone.
        if (entity.Zone?.Type == EngineZone.HAND && SabberStoneCore.Actions.PlayLockRules.BlocksPlay(entity)
            || visibleTransportTags.TryGetValue(entity.Id, out var playTags) && playTags.ContainsKey((int)GameTag.CANT_PLAY))
            stats = stats.Append(new KeyValuePair<GameTag, int>(GameTag.CANT_PLAY, entity[GameTag.CANT_PLAY]));
#endif
        return stats.Select(stat => (SkinTag(entity.Id, entity.Card.Id, (int)stat.Key) ?? OwnedPremiumValue((int)stat.Key, premium)) is int value
            ? new KeyValuePair<GameTag, int>(stat.Key, value) : stat);
    }

    private static int PrintedTagValue(IPlayable entity, GameTag tag)
    {
        if (tag == GameTag.GHOSTLY) return Math.Max(entity[GameTag.GHOSTLY], entity[TemporaryCardTag]);
#if ENGINE_SOURCE
        if (entity.Card.Id == "JAIL_EVENT_100t" && tag == GameTag.MODULAR_ENTITY_PART_1) return 0;
#endif
        // Elusive is an alias: Silence and targeting auras never write tag 1211.
        if ((int)tag == 1211 && entity is Character character)
            return character.CantBeTargetedBySpells && character.CantBeTargetedByHeroPowers ? 1 : 0;
        // AuraEffects.Rush has storage only on minions; other entities can still carry raw tags.
        if (tag == GameTag.RUSH && !(entity is Minion))
            return entity.NativeTags.TryGetValue(tag, out int value) ? value : entity.Card[tag];
        return entity[tag];
    }

    // Pinned Player.TotalSpellpower reads local bonus from 291, not keyword 192.
    private static GameTag NativeStatTag(IPlayable entity, GameTag tag) =>
        entity is SabberStoneCore.Model.Entities.Spell && tag == GameTag.SPELLPOWER
            ? GameTag.CURRENT_SPELLPOWER : tag;

#if ENGINE_SOURCE
    // A stored entity that was sent anonymous (its host sat in a deck or a hidden hand) becomes nameable the moment its
    // host reaches a zone where its owner may read it: the client builds the text once, when the host is first shown, so
    // the named SHOW must precede the host's own first appearance in this packet (or come last when the host is already shown).
    private void AppendSharedStoredReveals(NativePowerHistory packet)
    {
        foreach (var stored in Game.IdEntityDic.Values.Where(e => e[SabberStoneCore.Actions.PayloadRules.Stored] != 0
                     && (e[SabberStoneCore.Actions.PayloadRules.Policy] & (int)(SabberStoneCore.Actions.PayloadPolicy.ShowOwner | SabberStoneCore.Actions.PayloadPolicy.ShowAll)) != 0)
                     .OrderBy(e => e.Id).ToArray())
        {
            if (!clientTransportCards.TryGetValue(stored.Id, out string sent) || sent.Length != 0 || IsHiddenFromHuman(stored.Id, null)) continue;
            var snapshot = SabberStoneCore.Kettle.PowerHistoryBuilder.FullEntity(stored).Entity;
            var reveal = new PowerHistoryData { ShowEntity = ToNativeHistoryEntity(
                new EngineHistoryEntity { Id = stored.Id, Name = stored.Card.Id, Tags = new Dictionary<GameTag, int>(snapshot.Tags) }, true) };
            int host = stored[SabberStoneCore.Actions.PayloadRules.Owner], at = packet.List.Count;
            if (host != 0)
                for (int i = 0; i < packet.List.Count; i++)
                {
                    var row = packet.List[i];
                    if (row.HasShowEntity && row.ShowEntity.Entity == host || row.HasFullEntity && row.FullEntity.Entity == host && row.FullEntity.Name.Length > 0
                        || row.HasTagChange && row.TagChange.Entity == host && row.TagChange.Tag == (int)GameTag.ZONE
                            && (row.TagChange.Value == (int)EngineZone.HAND || row.TagChange.Value == (int)EngineZone.PLAY || row.TagChange.Value == (int)EngineZone.SECRET))
                    { at = i; break; }
                }
            packet.List.Insert(at, reveal);
            TrackVisibility(reveal);
        }
    }

    // The pinned client builds entity-reference text (builders 19/20/23) once, when the card is shown, and its
    // OnTagChange ignores tag 2. A reference that arrives after the host is already in hand/play (Highborne Pupil:
    // the spell is taught after the Discover) therefore needs the client's own rebuild trigger: a change of
    // OVERRIDECARDTEXTBUILDER clears the entity's builder and calls Actor.UpdatePowersText.
    private void AppendEntityReferenceRefresh(NativePowerHistory packet)
    {
        for (int index = 0; index < packet.List.Count; index++)
        {
            var row = packet.List[index];
            if (!row.HasTagChange || row.TagChange.Tag != (int)GameTag.TAG_SCRIPT_DATA_NUM_1 || row.TagChange.Value == 0) continue;
            int id = row.TagChange.Entity;
            if (!Game.IdEntityDic.TryGetValue(id, out var host)) continue;
            int builder = SabberStoneCore.Actions.PayloadRules.ReferenceBuilderType(host.Card);
            if (builder == 0) continue;
            bool shownLater = false;
            for (int next = index + 1; next < packet.List.Count && !shownLater; next++)
            {
                var later = packet.List[next];
                shownLater = later.HasShowEntity && later.ShowEntity.Entity == id
                    || later.HasTagChange && later.TagChange.Entity == id && later.TagChange.Tag == (int)GameTag.ZONE
                        && (later.TagChange.Value == (int)EngineZone.HAND || later.TagChange.Value == (int)EngineZone.PLAY);
            }
            if (shownLater) continue;
            var refresh = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = id, Tag = (int)GameTag.OVERRIDECARDTEXTBUILDER, Value = builder } };
            packet.List.Insert(++index, refresh);
            TrackVisibility(refresh);
        }
    }

    // An entity reference (client text builders 19/20/23) the human's client may not name is sent as 0.
    // Tag 2 of a card whose client builder resolves entities. Declared hosts send an entity id, or 0 when this viewer may not
    // name it. Every other such card (PayloadRules.MasksReference) still holds a dbfId or a stat in tag 2, which the client
    // would resolve to some unrelated entity and which would leak a hidden card's identity: sent as 0 by default.
    private static int ProjectReference(SabberStoneCore.Model.Game game, EngineCard card, int value)
    {
        if (value == 0) return 0;
        if (SabberStoneCore.Actions.PayloadRules.MasksReference(card)) return 0;
        return SabberStoneCore.Actions.PayloadRules.ReferencesEntity(card, GameTag.TAG_SCRIPT_DATA_NUM_1)
            && ReferenceHiddenFromHuman(game, value) ? 0 : value;
    }

    // Live packets: the static rule above, plus: a grant that names a board minion (the donor of a copied Deathrattle) is named only
    // while the human may see that entity at all, so the reference is never a way to read a hidden card.
    private int ProjectLiveReference(SabberStoneCore.Model.Game game, EngineCard card, int value)
    {
        value = ProjectReference(game, card, value);
        return value != 0 && SabberStoneCore.Actions.PayloadRules.NamesPublicEntity(card) && IsHiddenFromHuman(value, null) ? 0 : value;
    }

    // A Rewind reset describes the restored snapshots while the live game still holds the abandoned timeline: the stored entity
    // is named to the human exactly when the reset itself sends it named (IsHiddenFromHuman with the snapshots), never by the live game.
    private int ProjectResetReference(EngineCard card, int value, IReadOnlyDictionary<int, EngineHistoryEntity> resetEntities)
    {
        if (value == 0 || SabberStoneCore.Actions.PayloadRules.MasksReference(card)) return 0;
        return SabberStoneCore.Actions.PayloadRules.ReferencesEntity(card, GameTag.TAG_SCRIPT_DATA_NUM_1)
            && IsHiddenFromHuman(value, null, resetEntities) ? 0 : value;
    }

    private static bool ReferenceHiddenFromHuman(SabberStoneCore.Model.Game game, int id) =>
        !game.IdEntityDic.TryGetValue(id, out var target)
        || SabberStoneCore.Actions.PayloadRules.IsStored(target)
            && !SabberStoneCore.Actions.PayloadRules.SharedWith(target, HumanPlayerId);

#endif

    private static IEnumerable<KeyValuePair<GameTag, int>> VisibleStats(IPlayable entity)
    {
        foreach (GameTag tag in PrintedDefinitionTags)
        {
#if ENGINE_SOURCE
            // Card text counters below own their derived values.
            if (CardTextTags.Contains(tag)) continue;
#endif
            yield return new KeyValuePair<GameTag, int>(NativeStatTag(entity, tag), PrintedTagValue(entity, tag));
        }
        // Hand cost resets can be filtered before an opponent's move to PLAY becomes public.
        yield return new KeyValuePair<GameTag, int>(GameTag.COST, entity.Cost);
#if ENGINE_SOURCE
        yield return new KeyValuePair<GameTag, int>((GameTag)2837, (int)SabberStoneCore.Actions.CostRules.PaymentKind(entity));
#else
        yield return new KeyValuePair<GameTag, int>((GameTag)2837, entity[(GameTag)2837]);
#endif
        if (entity is Character character)
        {
            yield return new KeyValuePair<GameTag, int>(GameTag.ATK, character.AttackDamage);
            yield return new KeyValuePair<GameTag, int>(GameTag.HEALTH, character.BaseHealth);
            yield return new KeyValuePair<GameTag, int>(GameTag.DAMAGE, character.Damage);
            yield return new KeyValuePair<GameTag, int>(GameTag.ARMOR, character.Armor);
            yield return new KeyValuePair<GameTag, int>(GameTag.TAUNT, character.HasTaunt ? 1 : 0);
            yield return new KeyValuePair<GameTag, int>(GameTag.DIVINE_SHIELD, character[GameTag.DIVINE_SHIELD] > 0 ? 1 : 0);
            // MEGA_WINDFURY is a separate tag; preserve WINDFURY's own value on minions.
            yield return new KeyValuePair<GameTag, int>(GameTag.WINDFURY, character is Hero windfuryHero
                ? (windfuryHero.HasWindfury ? 1 : 0)
                : character[GameTag.WINDFURY]);
            yield return new KeyValuePair<GameTag, int>(GameTag.POISONOUS, character[GameTag.POISONOUS]);
            yield return new KeyValuePair<GameTag, int>(GameTag.LIFESTEAL, character.HasLifeSteal ? 1 : 0);
            yield return new KeyValuePair<GameTag, int>(GameTag.STEALTH, character.HasStealth ? 1 : 0);
        }
#if ENGINE_SOURCE
        if (entity is BoardObject location)
        {
            yield return new KeyValuePair<GameTag, int>(GameTag.HEALTH, location[GameTag.HEALTH]);
            yield return new KeyValuePair<GameTag, int>(GameTag.DAMAGE, location[GameTag.DAMAGE]);
            yield return new KeyValuePair<GameTag, int>(GameTag.EXHAUSTED, location.IsExhausted ? 1 : 0);
            yield return new KeyValuePair<GameTag, int>(GameTag.LOCATION_ACTION_COOLDOWN, location[GameTag.LOCATION_ACTION_COOLDOWN]);
        }
        if (entity.Card.Id == "BE_055t")
            foreach (int tag in new[] { 1078, 1452 })
                yield return new KeyValuePair<GameTag, int>((GameTag)tag,
                    ReferenceHiddenFromHuman(entity.Game, SabberStoneCore.Actions.PayloadRules.WeaponSpellReference(entity))
                        ? 0 : SabberStoneCore.Actions.PayloadRules.WeaponSpellDbfId(entity));
        if (SabberStoneCore.Actions.LeylineRules.IsLeyline(entity.Card))
        {
            yield return new KeyValuePair<GameTag, int>(GameTag.TAG_SCRIPT_DATA_NUM_1, SabberStoneCore.Actions.LeylineRules.Magnitude(entity));
            yield return new KeyValuePair<GameTag, int>(GameTag.TAG_SCRIPT_DATA_NUM_2, SabberStoneCore.Actions.LeylineRules.Times(entity));
        }
        // Threshold builder reads controller 412 but refreshes its actor on N1 changes.
        int number = entity.Card.Id == "BE_055t"
            ? SabberStoneCore.Actions.PayloadRules.WeaponSpellReference(entity)
            : entity.Card.Id == "EDR_430" ? entity.Game.HistoryFacts.Query.FriendlyDeathCount(entity.Controller.Id)
            : entity[GameTag.TAG_SCRIPT_DATA_NUM_1];
        int number2 = entity[GameTag.TAG_SCRIPT_DATA_NUM_2];
        // The client resolves these through GameState.GetEntity; an entity this viewer cannot name prints "another card".
        number = ProjectReference(entity.Game, entity.Card, number);
        if (entity.Card.Id == "BE_850t" && entity is Minion grimlynx)
        {
            number = grimlynx.Health;
            number2 = grimlynx.AttackDamage;
        }
        yield return new KeyValuePair<GameTag, int>(GameTag.TAG_SCRIPT_DATA_NUM_1, number);
        yield return new KeyValuePair<GameTag, int>(GameTag.TAG_SCRIPT_DATA_NUM_2, number2);
        yield return new KeyValuePair<GameTag, int>((GameTag)1115, entity.Card[(GameTag)1115]);
        yield return new KeyValuePair<GameTag, int>((GameTag)1116, entity.Card[(GameTag)1116]);
        yield return new KeyValuePair<GameTag, int>((GameTag)955, AlternateCardText(entity.Card.Id,
            entity.Zone?.Type ?? (EngineZone)entity[GameTag.ZONE], number, number2, entity[(GameTag)955]));
#endif
        if (entity is Minion minion)
        {
            yield return new KeyValuePair<GameTag, int>(GameTag.SPELLPOWER, minion.SpellPower);
            yield return new KeyValuePair<GameTag, int>(GameTag.RUSH, minion.IsRush ? 1 : 0);
            yield return new KeyValuePair<GameTag, int>(GameTag.CHARGE, minion.HasCharge ? 1 : 0);
        }
        if (entity is Weapon weapon)
        {
            yield return new KeyValuePair<GameTag, int>(GameTag.ATK, weapon.AttackDamage);
            // Native weapon durability is HEALTH - DAMAGE.
            yield return new KeyValuePair<GameTag, int>(GameTag.HEALTH, weapon.Durability + weapon.Damage);
            yield return new KeyValuePair<GameTag, int>(GameTag.DAMAGE, weapon.Damage);
            yield return new KeyValuePair<GameTag, int>(GameTag.WINDFURY, weapon[GameTag.WINDFURY]);
            yield return new KeyValuePair<GameTag, int>(GameTag.POISONOUS, weapon[GameTag.POISONOUS]);
            yield return new KeyValuePair<GameTag, int>(GameTag.LIFESTEAL, weapon[GameTag.LIFESTEAL]);
        }
    }

    private void AppendVisibleStats(NativePowerHistory packet)
    {
        // Final getter values belong after engine/copy/aura entries, before their trailing block ends.
        int index = packet.List.Count;
        while (index > 0 && packet.List[index - 1].HasPowerEnd) index--;
#if ENGINE_SOURCE
        // Reset exposes effective aura tags (including aliases); keep that projection current thereafter.
        if (hasRewound)
            foreach (var controller in new[] { Game.Player1, Game.Player2 })
                foreach (var stat in PowerHistoryBuilder.ControllerAuraState(controller))
                {
                    clientTransportTags[controller.Id].TryGetValue((int)stat.Key, out int previous);
                    if (previous == stat.Value) continue;
                    var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                        Entity = controller.Id, Tag = (int)stat.Key, Value = stat.Value } };
                    packet.List.Insert(index++, change);
                    TrackVisibility(change);
                }
        foreach (var controller in new[] { Game.Player1, Game.Player2 })
        {
            int bonus = SabberStoneCore.Actions.HealingRules.Bonus(controller);
            int previous = clientTransportTags.TryGetValue(controller.Id, out var sent)
                && sent.TryGetValue(2333, out int value) ? value : 0;
            if (bonus == previous) continue;
            var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = controller.Id, Tag = 2333, Value = bonus } };
            packet.List.Insert(index++, change);
            TrackVisibility(change);
        }
        // Public deaths are independent of whether any hidden threshold card exists.
        int deaths = Game.HistoryFacts.Query.FriendlyDeathCount(Game.Player1.Id);
        if (!visibleTransportTags.TryGetValue(Game.Player1.Id, out var playerTags))
            visibleTransportTags[Game.Player1.Id] = playerTags = new Dictionary<int, int>();
        if (!playerTags.TryGetValue(412, out int before) || before != deaths)
        {
            var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = Game.Player1.Id, Tag = 412, Value = deaths } };
            packet.List.Insert(index++, change);
            TrackVisibility(change);
        }
#endif
        foreach (int id in revealedEntities.OrderBy(value => value))
        {
            if (!Game.IdEntityDic.TryGetValue(id, out var entity) || IsHiddenFromHuman(id, null)) continue;
            EngineZone zone = entity.Zone?.Type ?? (EngineZone)entity[GameTag.ZONE];
            bool active = !(entity is SabberStoneCore.Model.Entities.Enchantment)
                && (zone == EngineZone.PLAY || (entity.Controller?.PlayerId == HumanPlayerId
                    && (zone == EngineZone.HAND || zone == EngineZone.SECRET)));
            // A final move can leave a historical actor visible while its engine object has reset.
            if (!visibleTransportTags[id].TryGetValue((int)GameTag.ZONE, out int sentZone)
                || sentZone != (int)zone) continue;
            foreach (var stat in active ? PresentedStats(entity)
                : PrintedDefinitionTags.Select(tag => new KeyValuePair<GameTag, int>(NativeStatTag(entity, tag), PresentedTagValue(entity, tag))))
            {
#if ENGINE_SOURCE
                // Inactive text counters retain their captured snapshot values.
                if (!active && CardTextTags.Contains(stat.Key)) continue;
#endif
                visibleTransportTags[id].TryGetValue((int)stat.Key, out int previous);
                if (previous == stat.Value) continue;
                var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                    Entity = id, Tag = (int)stat.Key, Value = stat.Value } };
                packet.List.Insert(index++, change);
                TrackVisibility(change);
            }
        }
    }

    private void AppendSpellPowerSummary(NativePowerHistory packet, Controller controller)
    {
        int current = controller.CurrentSpellPower;
        if (lastSpellPower.TryGetValue(controller.PlayerId, out int previous) && current == previous)
            return;
        var update = new PowerHistoryData
        {
            TagChange = new PegasusGame.PowerHistoryTagChange
                { Entity = controller.Id, Tag = CurrentSpellPowerBaseTag, Value = current }
        };
        packet.List.Add(update);
        TrackVisibility(update);
        lastSpellPower[controller.PlayerId] = current;
    }

#if ENGINE_SOURCE || MODERN_MECHANICS
    // Pinned HeraldCardTextBuilder reads controller 4534; see tests/transport-invariants/herald-native-evidence.md.
    private void AppendHeraldState(NativePowerHistory packet)
    {
        foreach (int id in revealedEntities.ToArray())
        {
            if (!Game.IdEntityDic.TryGetValue(id, out var entity) || IsHiddenFromHuman(id, null)) continue;
            bool herald = entity.Card[SabberStoneCore.Actions.HeraldRules.Keyword] > 0
                || SabberStoneCore.Actions.HeraldRules.IsSoldier(entity.Card);
            // The client text builder reads only the CONTROLLER tag, so it carries the source-independent class;
            // the entity tag carries this card's own resolution (differs only for a cross-class card in hand).
            int controllerValue = herald ? (int)SabberStoneCore.Actions.HeraldRules.WireClass(entity.Controller) : 0;
            int entityValue = herald ? (int)SabberStoneCore.Actions.HeraldRules.Class(entity.Controller, entity.Card) : 0;
            // The per-card tag change also invalidates native card text after class selection.
            foreach (int targetId in herald ? new[] { entity.Controller.Id, id } : new[] { id })
            {
                int value = targetId == id ? entityValue : controllerValue;
                visibleTransportTags.TryGetValue(targetId, out var previous);
                int tag = (int)SabberStoneCore.Actions.HeraldRules.Profile;
                int before = previous != null && previous.TryGetValue(tag, out int old) ? old : 0;
                if (value == before) continue;
                var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                    Entity = targetId, Tag = tag, Value = value } };
                packet.List.Add(change);
                TrackVisibility(change);
            }
        }
    }
#endif

#if ENGINE_SOURCE || STANDARD_PROOF
#if ENGINE_SOURCE
    internal static bool HasPoweredUpHighlight(IPlayable entity) => SabberStoneCore.Model.PoweredUp.HasCondition(entity.Card);

    private int PoweredUpHighlight(IPlayable entity) =>
        Game.State == State.RUNNING
        && Game.CurrentPlayer == Game.Player1 && entity.Controller == Game.Player1
        && entity.Zone?.Type == EngineZone.HAND
        && SabberStoneCore.Model.PoweredUp.IsMet(entity) ? 1 : 0;
#else
    internal static bool HasPoweredUpHighlight(IPlayable entity) => entity.Card[(GameTag)1333] == 1
#if MODERN_MECHANICS
        || HasKindredHighlight(entity)
#endif
        ;

#if MODERN_MECHANICS
    private static bool HasKindredHighlight(IPlayable entity) =>
        entity.Card.Implemented && entity.Card[(GameTag)4028] == 1;
#endif

    private int PoweredUpHighlight(IPlayable entity) =>
        Game.State == State.RUNNING
        && Game.CurrentPlayer == Game.Player1 && entity.Controller == Game.Player1
        && entity.Zone?.Type == EngineZone.HAND
        && ((entity.Card[(GameTag)1333] == 1
            && (entity.ZonePosition == 0 || entity.ZonePosition == Game.Player1.HandZone.Count - 1))
#if MODERN_MECHANICS
            || (HasKindredHighlight(entity) && SabberStoneCore.Conditions.SelfCondition.Kindred.Eval(entity))
#endif
        ) ? 1 : 0;
#endif

    private void AppendPoweredUpHighlights(NativePowerHistory packet)
    {
        // Hidden snapshots also lose visibility (for example Dark Bribe giving a hand card away).
        foreach (int id in poweredUpHighlights.Where(id => !revealedEntities.Contains(id)).ToArray())
            AppendPoweredUpHighlight(packet, id, 0);
        foreach (int id in revealedEntities.ToArray())
        {
            if (!Game.IdEntityDic.TryGetValue(id, out var entity)
                || (!HasPoweredUpHighlight(entity) && !poweredUpHighlights.Contains(id))) continue;
            int value = PoweredUpHighlight(entity);
            if (value == 0 && !poweredUpHighlights.Contains(id)) continue;
            if (value == 1 && visibleTransportTags.TryGetValue(id, out var tags)
                && tags.TryGetValue((int)GameTag.POWERED_UP, out int previous) && previous == 1) continue;
            AppendPoweredUpHighlight(packet, id, value);
        }
    }

    private void AppendPoweredUpHighlight(NativePowerHistory packet, int id, int value)
    {
#if ENGINE_SOURCE
        // Publish positives only for the human's cards; clears always go out (control may have moved).
        if (value == 1 && (!Game.IdEntityDic.TryGetValue(id, out var entity) || entity.Controller != Game.Player1)) return;
#endif
        var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
            Entity = id, Tag = (int)GameTag.POWERED_UP, Value = value } };
        packet.List.Add(change);
        TrackVisibility(change);
        if (value == 1) poweredUpHighlights.Add(id);
        else poweredUpHighlights.Remove(id);
    }
#endif

    private static bool IsPublicExitReveal(List<IPowerHistoryEntry> entries, int index)
    {
        // Snapshot plus immediately adjacent exit: both identity and Secret policy are captured at that exit.
        return entries[index] is PowerHistoryShowEntity burnShow
                && (TagValue(burnShow.Entity.Tags, GameTag.ZONE) == (int)EngineZone.DECK
                    || TagValue(burnShow.Entity.Tags, GameTag.ZONE) == (int)EngineZone.HAND)
                && index + 1 < entries.Count
                && entries[index + 1] is SabberStoneCore.Kettle.PowerHistoryTagChange burnExit
                && burnExit.EntityId == burnShow.Entity.Id && burnExit.Tag == GameTag.ZONE
                && (burnExit.Value == (int)EngineZone.PLAY || burnExit.Value == (int)EngineZone.GRAVEYARD
                    || burnExit.Value == (int)EngineZone.REMOVEDFROMGAME)
                && !(burnExit.Value == (int)EngineZone.PLAY && TagValue(burnShow.Entity.Tags, GameTag.SECRET) == 1
                    && TagValue(burnShow.Entity.Tags, GameTag.QUEST) != 1);
    }

    private bool AddMissingPlayReveal(NativePowerHistory packet, int sourceId, EngineHistoryEntity frozenPlay, bool automatic = false,
        int position = -1)
    {
        // Known opponent hand cards may gain private keywords before being played again.
        if (!Game.IdEntityDic.ContainsKey(sourceId)
            || (revealedEntities.Contains(sourceId) && !IsHiddenFromHuman(sourceId, null)))
            return false;
        IPlayable source = Game.IdEntityDic[sourceId];
        int owner = historicalControllers.TryGetValue(sourceId,out int beforeOwner) ? beforeOwner : source.Controller?.PlayerId ?? 0;
        // An automatic play out of the human's own deck is shown to them too (retail big card).
        if (owner != BotPlayerId && !automatic) return false;
#if ENGINE_SOURCE
        // The card as it was at this PLAY: never a later transform or concealment of the same entity in this batch.
        var played = frozenPlay ?? IdentityAt(position, sourceId, currentFirst: true, allowCurrent: true);
        if (played == null) return false;
#else
        var played = frozenPlay ?? PowerHistoryBuilder.ShowEntity(source).Entity;
#endif
        // A bot Secret is never shown; judged by the card played, not a later transform of the entity.
        if (owner == BotPlayerId && DefinitionTag(played.Name, GameTag.SECRET, TagValue(played.Tags, GameTag.SECRET)) == 1)
        {
            // Opponent PLAY always opens a big card; CanDuplicateEntity needs SECRET even for a nameless actor.
            foreach (var tag in new[] { new KeyValuePair<GameTag, int>(GameTag.SECRET, 1),
                new KeyValuePair<GameTag, int>(GameTag.CARDTYPE, (int)CardType.SPELL) })
            {
                var change = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                    Entity = sourceId, Tag = (int)tag.Key, Value = tag.Value } };
                packet.List.Add(change);
                TrackVisibility(change);
            }
            return false;
        }
        var snapshot = new EngineHistoryEntity { Id = played.Id, Name = played.Name, Tags = played.Tags };
        snapshot.Tags = new Dictionary<GameTag,int>(snapshot.Tags);
        snapshot.Tags[GameTag.CONTROLLER] = owner;
        if (historicalZones.TryGetValue(sourceId,out var beforeZone)) snapshot.Tags[GameTag.ZONE] = (int)beforeZone;
        if (historicalPositions.TryGetValue(sourceId,out int beforePosition)) snapshot.Tags[GameTag.ZONE_POSITION] = beforePosition;
        // Resolution may already have moved or stolen the source; reveal its pre-play location/owner.
        PowerHistoryData reveal = new PowerHistoryData { ShowEntity = ToNativeHistoryEntity(snapshot, true) };
        if (reveal.ShowEntity.Name.Length == 0)
            throw new InvalidOperationException("Public bot play could not be revealed.");
        packet.List.Add(reveal);
        TrackVisibility(reveal);
        TrackHistoricalEntity(reveal.ShowEntity.Entity, reveal.ShowEntity.Tags);
#if ENGINE_SOURCE
        AppendPrivateAttachments(packet, sourceId);
#endif
        return true;
    }

    private void AddSecretActivationReveal(NativePowerHistory packet, int sourceId)
    {
        if (revealedEntities.Contains(sourceId) || !Game.IdEntityDic.TryGetValue(sourceId, out IPlayable source)) return;
#if ENGINE_SOURCE
        var activated = IdentityAt(identityPosition, sourceId, currentFirst: true, allowCurrent: true, definitionOnly: true);
#else
        var activated = PowerHistoryBuilder.ShowEntity(source).Entity;
#endif
        if (activated == null || DefinitionTag(activated.Name, GameTag.SECRET, TagValue(activated.Tags, GameTag.SECRET)) != 1) return;
        int owner = historicalControllers.TryGetValue(sourceId, out int beforeOwner) ? beforeOwner : source.Controller?.PlayerId ?? 0;
        if (owner != BotPlayerId)
            return;
#if ENGINE_SOURCE
        activated = IdentityAt(identityPosition, sourceId, currentFirst: true, allowCurrent: true);
#endif
        var snapshot = new EngineHistoryEntity { Id = sourceId, Name = activated.Name,
            Tags = new Dictionary<GameTag, int>(activated.Tags) };
        snapshot.Tags[GameTag.CONTROLLER] = owner;
        if (historicalZones.TryGetValue(sourceId, out var beforeZone)) snapshot.Tags[GameTag.ZONE] = (int)beforeZone;
        if (historicalPositions.TryGetValue(sourceId, out int beforePosition)) snapshot.Tags[GameTag.ZONE_POSITION] = beforePosition;
        PowerHistoryData reveal = new PowerHistoryData { ShowEntity = ToNativeHistoryEntity(snapshot, true) };
        if (reveal.ShowEntity.Name.Length == 0)
            throw new InvalidOperationException("Activated opponent secret could not be revealed.");
        packet.List.Add(reveal);
        TrackVisibility(reveal);
        TrackHistoricalEntity(reveal.ShowEntity.Entity, reveal.ShowEntity.Tags);
    }

#if ENGINE_SOURCE
    private bool StartOfGameMulligansDone() => new[] { Game.Player1, Game.Player2 }.All(player =>
        clientTransportTags.TryGetValue(player.Id, out var tags)
        && tags.TryGetValue((int)GameTag.MULLIGAN_STATE, out int state) && state == (int)Mulligan.DONE);

    private void AppendStartOfGameBarrier(NativePowerHistory packet)
    {
        if (startOfGameBarrier) return;
        startOfGameBarrier = true;
        var barrier = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
            Entity = Game.Id, Tag = (int)GameTag.NEXT_STEP, Value = (int)Step.MAIN_READY
        } };
        packet.List.Add(barrier);
        TrackVisibility(barrier);
    }

    private (int Reveal, int Effect) StartOfGameSlots(IPlayable source, string cardId)
    {
        var slots = StartOfGameEffectSlots.TryGetValue(cardId, out var known) ? known : (Reveal: -1, Effect: -1);
        if (cardId == "JAIL_800")
        {
            // Zee slots 0/1/4/5, combined 8/9; the Mug-only slot is unresolved.
            bool mug = source.Controller.HeroPowers.Any(p => IdentityAt(identityPosition, p.Id, true, true, definitionOnly: true)?.Name == "JAIL_800hp1");
            bool zee = source.Controller.HeroPowers.Any(p => IdentityAt(identityPosition, p.Id, true, true, definitionOnly: true)?.Name == "JAIL_800hp2");
            slots.Effect = mug && zee ? 8 : zee ? 0 : -1;
        }
        return slots;
    }

    private bool AppendStartOfGameReveal(NativePowerHistory packet, IPlayable source, EngineHistoryEntity identity)
    {
        startOfGameSources.Add(source.Id);
        // Only historical location/visible stats and printed defaults belong to the pre-effect actor.
        var tags = new Dictionary<GameTag, int> {
            [GameTag.ENTITY_ID] = source.Id,
            [GameTag.ZONE] = (int)historicalZones[source.Id],
            [GameTag.CONTROLLER] = historicalControllers[source.Id]
        };
        if (!IsHiddenFromHuman(source.Id, tags)) return false;
        visibleTransportTags.TryGetValue(source.Id, out var visible);
        foreach (var tag in new[] { GameTag.CARDTYPE, GameTag.COST, GameTag.ATK, GameTag.HEALTH, GameTag.DAMAGE, GameTag.PREMIUM })
            tags[tag] = visible != null && visible.TryGetValue((int)tag, out int value) ? value : DefinitionTag(identity.Name, tag, TagValue(identity.Tags, tag));
        void Append(PowerHistoryData data) { packet.List.Add(data); TrackVisibility(data); }
        Append(new PowerHistoryData { PowerStart = new PowerHistoryStart {
            Type = HistoryBlock.Type.TRIGGER, Source = source.Id, Target = 0, SubOption = -1,
            EffectCardId = "", EffectIndex = StartOfGameSlots(source, identity.Name).Reveal
        } });
        tags[GameTag.REVEALED] = 1;
        var shown = ToNativeHistoryEntity(new EngineHistoryEntity { Id = source.Id, Name = identity.Name, Tags = tags },
            revealToHuman: true, completeSnapshot: false);
        shown.Tags.RemoveAll(tag => CardTextTags.Contains((GameTag)tag.Name));
        Append(new PowerHistoryData { ShowEntity = shown });
        Append(new PowerHistoryData { PowerEnd = new PowerHistoryEnd() });
        return true;
    }

    private void AppendStartOfGameEnd(NativePowerHistory packet, int source, bool revealed, int bodyStart)
    {
        void Append(PowerHistoryData data) { packet.List.Add(data); TrackVisibility(data); }
        var targets = packet.List.Skip(bodyStart).Select(data => data.HasFullEntity ? data.FullEntity.Entity
            : data.HasShowEntity ? data.ShowEntity.Entity : data.HasChangeEntity ? data.ChangeEntity.Entity
            : data.HasTagChange ? data.TagChange.Entity : 0).Distinct()
            .Where(id => id != 0 && id != source && revealedEntities.Contains(id)
                && historicalZones.TryGetValue(id, out var zone) && zone == EngineZone.PLAY
                && !IsHiddenFromHuman(id, null)).ToList();
        if (targets.Count > 0)
            Append(new PowerHistoryData { MetaData = new PegasusGame.PowerHistoryMetaData {
                Type = (HistoryMeta.Type)7, Data = 0, Info = targets
            } });
        if (revealed && IsHiddenFromHuman(source, null))
        {
            Append(new PowerHistoryData { HideEntity = new PowerHistoryHide { Entity = source, Zone = (int)historicalZones[source] } });
            Append(new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = source, Tag = (int)GameTag.REVEALED, Value = 0
            } });
        }
    }

    private void TrackClientState(PowerHistoryData data)
    {
        void Merge(int id, IEnumerable<NativeTag> values)
        {
            if (!clientTransportTags.TryGetValue(id, out var tags))
                clientTransportTags[id] = tags = new Dictionary<int, int>();
            foreach (var tag in values) tags[tag.Name] = tag.Value;
        }
        var create = data.HasCreateGame ? data.CreateGame : data.HasResetGame ? data.ResetGame.CreateGame : null;
        if (create != null)
        {
            Merge(create.GameEntity.Id, create.GameEntity.Tags);
            foreach (var player in create.Players) Merge(player.Entity.Id, player.Entity.Tags);
        }
        var entity = data.HasFullEntity ? data.FullEntity : data.HasShowEntity ? data.ShowEntity : data.HasChangeEntity ? data.ChangeEntity : null;
        if (entity != null)
        {
            clientTransportCards[entity.Entity] = entity.Name;
            Merge(entity.Entity, entity.Tags);
        }
        if (data.HasTagChange)
            Merge(data.TagChange.Entity, new[] { new NativeTag { Name = data.TagChange.Tag, Value = data.TagChange.Value } });
        if (data.HasHideEntity)
        {
            int id = data.HideEntity.Entity;
            string card = clientTransportCards.TryGetValue(id, out var known) ? known : "";
            EngineCard definition = null;
            if (card.Length > 0)
                try { definition = EngineCards.FromId(card); }
                catch (KeyNotFoundException) { }
            foreach (var tag in new[] { GameTag.ATK, GameTag.HEALTH, GameTag.COST })
            {
                var printedTag = tag == GameTag.HEALTH && definition?.Type == CardType.WEAPON ? GameTag.DURABILITY : tag;
                Merge(id, new[] { new NativeTag { Name = (int)tag, Value = definition?[printedTag] ?? 0 } });
            }
            Merge(id, new[] { new NativeTag { Name = (int)GameTag.DAMAGE, Value = 0 },
                new NativeTag { Name = (int)GameTag.ZONE, Value = data.HideEntity.Zone } });
            clientTransportCards[id] = "";
        }
    }

    // Identity the native client holds after every sent history list. Builder-time trackers already describe the end of
    // the packet being built, so this view is advanced only by the choke point, entry by entry, as the client applies it.
    private sealed class WireEntity { internal string Card = ""; internal int Zone, Controller, Position, CardType, Attached; }
    private readonly Dictionary<int, WireEntity> wireEntities = new Dictionary<int, WireEntity>();
    private readonly HashSet<int> anonymousPublicMoveLogged = new HashSet<int>();
    private readonly Dictionary<int, NativeTag[]> wireResetLocations = new Dictionary<int, NativeTag[]>();
    // The engine snapshot behind each native snapshot of the list being built: a reveal shows the card as it was at
    // the move (privacy and identity judged then), never the end-of-batch engine state.
    private readonly List<KeyValuePair<PowerHistoryData, EngineHistoryEntity>> engineIdentities = new List<KeyValuePair<PowerHistoryData, EngineHistoryEntity>>();
    // Each entity's latest engine snapshot from lists already sent (advanced only after a list is normalized; rebuilt
    // from a rewind reset's snapshots; forgotten with removed entities).
    private readonly Dictionary<int, EngineHistoryEntity> engineIdentityHistory = new Dictionary<int, EngineHistoryEntity>();
    // Engine order of this batch: which engine entry produced each native entry, and every engine entry that can change
    // what an entity is or where it is (CHANGE, HIDE, FULL, ZONE/CONTROLLER). A current definition is the identity at a
    // move only if nothing of the kind follows that move in the batch.
    private readonly List<KeyValuePair<PowerHistoryData, int>> nativeEngineOrder = new List<KeyValuePair<PowerHistoryData, int>>();
    // The engine batch being sent (null outside SendHistory): identity of an entity as of a history position.
    private List<IPowerHistoryEntry> identityBatch;
    private int identityPosition;

    // Open blocks, innermost first: the automatic play's source revealed for that block (0 otherwise).
    private readonly Stack<int> effectPlayScopes = new Stack<int>();
    private int scopedRevealSource;

    private void RecordEngineIdentity(PowerHistoryData native, IPowerHistoryEntry entry)
    {
        var engine = NamedSnapshot(entry);
        if (engine != null && !string.IsNullOrEmpty(engine.Name) && HistoryEntity(native) == engine.Id)
            engineIdentities.Add(new KeyValuePair<PowerHistoryData, EngineHistoryEntity>(native, engine));
    }

    private static int SnapshotOrHideEntity(IPowerHistoryEntry entry) => entry is PowerHistoryShowEntity show ? show.Entity.Id
        : entry is PowerHistoryFullEntity full ? full.Entity.Id : entry is PowerHistoryChangeEntity change ? change.Entity.Id
        : entry is PowerHistoryHideEntity hide ? hide.EntityID : 0;

    private static EngineHistoryEntity NamedSnapshot(IPowerHistoryEntry entry) => entry is PowerHistoryShowEntity show ? show.Entity
        : entry is PowerHistoryFullEntity full ? full.Entity
        : entry is PowerHistoryChangeEntity change ? new EngineHistoryEntity { Id = change.Entity.Id, Name = change.CardId, Tags = change.Entity.Tags } : null;

    // Identity of an entity as of engine position P of this batch, for every reveal path (move fallback, pre-PLAY
    // reveal). The current definition stands only when no later CHANGE or re-FULL of it follows P before the
    // flush; otherwise the latest named snapshot at or before P, else the first named SHOW after P that precedes any
    // CHANGE/re-FULL, else the cache from earlier lists. Null when none is certain.
    private EngineHistoryEntity IdentityAt(int position, int id, bool currentFirst, bool allowCurrent, bool definitionOnly = false)
    {
        var batch = identityBatch;
        // HIDE changes visibility, not identity (e.g. Llane's shuffle or a consumed attachment). Its location/privacy
        // remain the caller's responsibility; CHANGE and re-FULL are the definition barriers even when hidden.
        bool stable = batch == null || !batch.Skip(Math.Max(0, position + 1)).Any(entry =>
            (entry is PowerHistoryChangeEntity || entry is PowerHistoryFullEntity) && SnapshotOrHideEntity(entry) == id);
        // Policy reads must not evaluate snapshot getters (some populate engine caches/state).
        EngineHistoryEntity Current()
        {
            if (!Game.IdEntityDic.TryGetValue(id, out var entity)) return null;
            if (!definitionOnly) return entity is SabberStoneCore.Model.Entities.Enchantment
                ? PowerHistoryBuilder.FullEntity(entity).Entity : PowerHistoryBuilder.ShowEntity(entity).Entity;
            var tags = new Dictionary<GameTag, int>(entity.Card.Tags);
            foreach (var tag in entity.NativeTags) tags[tag.Key] = tag.Value;
            return new EngineHistoryEntity { Id = id, Name = entity.Card.Id, Tags = tags };
        }
        if (currentFirst && allowCurrent && stable) return Current();
        if (batch != null && position >= 0)
        {
            for (int back = Math.Min(position, batch.Count - 1); back >= 0; back--)
                if (SnapshotOrHideEntity(batch[back]) == id && NamedSnapshot(batch[back]) is { } before && !string.IsNullOrEmpty(before.Name))
                    return before;
            for (int ahead = position + 1; ahead < batch.Count; ahead++)
            {
                if (SnapshotOrHideEntity(batch[ahead]) != id) continue;
                if (batch[ahead] is PowerHistoryChangeEntity || batch[ahead] is PowerHistoryFullEntity) break;
                if (batch[ahead] is PowerHistoryShowEntity later && !string.IsNullOrEmpty(later.Entity.Name)) return later.Entity;
            }
        }
        if (engineIdentityHistory.TryGetValue(id, out var cached)) return cached;
        return !currentFirst && allowCurrent && stable ? Current() : null;
    }

    // The exemption ends when its source moves, is concealed (HIDE) or replaced (CHANGE, re-FULL).
    private void EndEffectPlayScope(IPowerHistoryEntry entry)
    {
        int id = SnapshotOrHideEntity(entry);
        if (entry is PowerHistoryShowEntity) id = 0;
        if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange tag && (tag.Tag == GameTag.ZONE || tag.Tag == GameTag.CONTROLLER)) id = tag.EntityId;
        if (id == 0 || !effectPlayScopes.Contains(id)) return;
        var scopes = effectPlayScopes.Reverse().Select(source => source == id ? 0 : source).ToArray();
        effectPlayScopes.Clear();
        foreach (int source in scopes) effectPlayScopes.Push(source);
    }

    private static void MoveJaraxxusPowerCreation(NativePowerHistory packet, Action<string> warn)
    {
        // JaraxxusHeroSpell.AddPowerTargets requires both new power and weapon FULL_ENTITY tasks in its POWER list.
        var list = packet.List;
        bool Start(PowerHistoryData row) => row.HasPowerStart || row.HasSubSpellStart;
        bool End(PowerHistoryData row) => row.HasPowerEnd || row.HasSubSpellEnd;
        int EnclosingStart(int before)
        {
            int nesting = 0;
            for (int at = before - 1; at >= 0; at--)
            {
                if (End(list[at])) nesting++;
                else if (Start(list[at]))
                {
                    if (nesting == 0) return at;
                    nesting--;
                }
            }
            return -1;
        }
        int weapon = list.Count - 1;
        if (weapon < 0 || !list[weapon].HasFullEntity || list[weapon].FullEntity.Name != "EX1_323w") return;
        int power = EnclosingStart(weapon);
        if (power < 0 || !list[power].HasPowerStart || list[power].PowerStart.Type != HistoryBlock.Type.POWER) return;
        int play = EnclosingStart(power);
        if (play < 0 || !list[play].HasPowerStart || list[play].PowerStart.Type != HistoryBlock.Type.PLAY
            || list[play].PowerStart.Source != list[power].PowerStart.Source) return;

        int creation = -1, depth = 0;
        for (int at = power - 1; at > play; at--)
        {
            var row = list[at];
            if (End(row)) depth++;
            else if (Start(row)) depth--;
            else if (depth == 0 && row.HasFullEntity && row.FullEntity.Name == "EX1_tk33")
            {
                creation = at;
                break;
            }
        }
        if (creation < 0) return;

        int id = list[creation].FullEntity.Entity;
        bool References(PowerHistoryData row) => HistoryEntity(row) == id
            || row.HasPowerStart && (row.PowerStart.Source == id || row.PowerStart.Target == id)
            || row.HasMetaData && row.MetaData.Info.Contains(id)
            || row.HasSubSpellStart && (row.SubSpellStart.SourceEntityId == id || row.SubSpellStart.TargetEntityIds.Contains(id));
        var moved = new List<int> { creation };
        for (int at = creation + 1; at < weapon; at++)
        {
            var row = list[at];
            if (References(row))
            {
                // Never move blocks or multi-entity rows, or cross an update that needs this creation first.
                if (at >= power || depth != 0 || Start(row) || End(row)
                    || row.HasMetaData && row.MetaData.Info.Any(entity => entity != id))
                {
                    warn?.Invoke($"WARN Jaraxxus creation {id}: unsafe dependency before weapon FULL_ENTITY; history unchanged");
                    return;
                }
                moved.Add(at);
            }
            if (Start(row)) depth++;
            if (End(row)) depth--;
        }
        var rows = moved.Select(at => list[at]).ToList();
        for (int at = moved.Count - 1; at >= 0; at--) list.RemoveAt(moved[at]);
        list.InsertRange(weapon - moved.Count, rows);
    }

    // ZoneMgr binds a Card's actor (ActorNames.GetZoneActor) when its zone/controller change is processed; SHOW_ENTITY
    // never rebinds it (Entity.OnShowEntity updateActor=false). A nameless entity moved into a public zone therefore
    // keeps the Card_Hidden actor for good: bodyless, untargetable. Give it its identity before the move:
    // (1) hoist a later named snapshot of the same list (no new disclosure), else (2) reveal it if the privacy rules
    // call the destination public, else (3) leave it hidden and log once.
    private void NormalizePublicIdentity(NativePowerHistory packet)
    {
        var list = packet.List;
        NormalizeHeroPowerOwnership(list);
        // New FULLs initialize logical tags on receipt (Entity.OnRealTimeFullEntity); SHOWs do not.
        var receivedFulls = new HashSet<PowerHistoryData>(WireEntryComparer.Instance);
        foreach (var data in list)
            if (data.HasFullEntity && !wireEntities.ContainsKey(data.FullEntity.Entity))
            {
                ApplyWire(data, false);
                receivedFulls.Add(data);
            }
        var effectSegments = IndexImpactSegments(list);
        var retiring = new HashSet<int>(list.Where(t => t.HasTagChange && t.TagChange.Tag == (int)GameTag.ZONE
            && t.TagChange.Value == (int)EngineZone.REMOVEDFROMGAME).Select(t => t.TagChange.Entity));
        var inserted = new List<PowerHistoryData>();
        var blocks = new Stack<int>();
        bool reset = false;
        for (int index = 0; index < list.Count; index++)
        {
            var data = list[index];
            if (data.HasPowerStart && (int)data.PowerStart.Type == (int)BlockType.GAME_RESET) reset = true;
            // Hoisted SHOWs are still deferred tasks: judge continuation entry before applying their logical tags.
            if (!reset && effectSegments.TryGetValue(data, out var segment)
                && SafeImpactTargets(segment, retiring) is PowerHistoryData targets)
            {
                list.Insert(segment.AfterAnchor ? index + 1 : index, targets);
                if (!segment.AfterAnchor) index++;
            }
            // Inside a rewind reset only the plugin's own tag/SHOW entries can move a Card the client already has.
            if ((!reset || data.HasTagChange) && PublicIdentityNeed(list, index, blocks.Count > 0 ? blocks.Peek() : 0) is (int id, int zone, int controller, bool own))
            {
                // A when-drawn card is named right after it leaves the hidden hand (SETASIDE), still before any public
                // move: its identity then never sits in the bot's hand on the wire.
                if (own) ApplyWire(data, reset);
                // Engine state is post-replay by now: inside a reset only the reset's own snapshots may name the card.
                PowerHistoryData reveal = HoistedIdentity(list, index, id, reset) ?? (reset ? null : RevealAtMove(list, index, id, zone, controller, own));
                if (reveal != null)
                {
                    int at = own ? index + 1 : index;
                    list.Insert(at, reveal);
                    BotTrace?.Invoke($"PUBLIC_IDENTITY match={MatchId} turn={Game.Turn} entity={id} card={reveal.ShowEntity.Name} from={(EngineZone)wireEntities[id].Zone} to={(EngineZone)zone} controller={controller} beforeMove={!own}");
                    inserted.Add(reveal);
                    ApplyWire(reveal, false);
                    index = own ? at : at + 1;
                }
                // A nameless card on the board or in the human's own zones is a defect; a hidden graveyard entry is not.
                else if ((zone == (int)EngineZone.PLAY || controller == HumanPlayerId && zone != (int)EngineZone.SETASIDE)
                    && !privateChoiceOffers.Contains(id) && anonymousPublicMoveLogged.Add(id))
                {
                    var wire = wireEntities[id];
                    // Diagnostic only, never a client packet or a naming/privacy input.
                    BotTrace?.Invoke($"ANON_PUBLIC_MOVE {id} {Game.IdEntityDic[id].Card.Id} {(EngineZone)wire.Zone}->{(EngineZone)zone}");
                }
            }
            if (list[index].HasPowerStart && (int)list[index].PowerStart.Type == (int)BlockType.GAME_RESET) ForgetRemoved();
            if (!receivedFulls.Contains(list[index])) ApplyWire(list[index], reset);
            if (!reset && list[index].HasPowerStart) MarkTriggerVisual(list, index);
            if (list[index].HasPowerStart) blocks.Push(list[index].PowerStart.Source);
            if (list[index].HasPowerEnd && blocks.Count > 0) blocks.Pop();
            if (reset && list[index].HasPowerEnd)
            {
                reset = false;
                foreach (var pair in wireResetLocations)
                    foreach (var tag in pair.Value)
                        ApplyWire(new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                            Entity = pair.Key, Tag = tag.Name, Value = tag.Value } }, false);
                wireResetLocations.Clear();
            }
        }
        // Merge this list's snapshots first, then evict: a card named and removed in the same list stays forgotten.
        foreach (var pair in engineIdentities) engineIdentityHistory[pair.Value.Id] = pair.Value;
        ForgetRemoved();
        engineIdentities.Clear();
        nativeEngineOrder.Clear();
        identityBatch = null;
        foreach (var frame in impactFrames)
            foreach (var sub in frame.Subs) sub.Sent = true;
        // Builder trackers already hold the list's end state; replay each inserted reveal and the same entity's later
        // entries so the end state (zone, tags, revealed set) stays the one the client reaches.
        // Target metadata may shift earlier reveal indices; retain the actual inserted entry.
        foreach (var reveal in inserted)
        {
            int at = list.IndexOf(reveal);
            int id = reveal.ShowEntity.Entity;
            for (int next = at; next < list.Count; next++)
                if (HistoryEntity(list[next]) == id) TrackVisibility(list[next]);
        }
    }

    // Retail SHOW is the DECK/HAND -> GRAVEYARD transition, not a separate reveal followed by a move.
    // ZoneChangeList must not wait for the just-milled opponent actor a second time.
    private static void NormalizeBurnTransitions(NativePowerHistory packet)
    {
        var rows = packet.List;
        for (int i = 0; i + 1 < rows.Count; i++)
        {
            var show = rows[i].ShowEntity;
            var move = rows[i + 1].TagChange;
            if (show == null || string.IsNullOrEmpty(show.Name) || move == null || move.Entity != show.Entity
                || move.Tag != (int)GameTag.ZONE || move.Value != (int)EngineZone.GRAVEYARD) continue;
            var zone = show.Tags.FirstOrDefault(t => t.Name == (int)GameTag.ZONE);
            if (zone == null || zone.Value != (int)EngineZone.DECK && zone.Value != (int)EngineZone.HAND) continue;
            zone.Value = move.Value;
            var revealed = show.Tags.FirstOrDefault(t => t.Name == (int)GameTag.REVEALED);
            if (revealed != null) revealed.Value = 0;
            rows.RemoveAt(i + 1);
        }
    }

    // SETASIDE has no Zone in the native client. Hand -> null calls Card.HideCard; null -> PLAY with
    // an existing, different actor falls through OnActorChanged and preserves that hidden state.
    // Engine tasks use SETASIDE as temporary storage. Keep the last real zone for a body that enters
    // PLAY in this packet. Cross-packet null-zone arrivals are repaired by ClientBridge.RepairNullToPlayActor.
    // The ordinary reveal normalizer still owns identity/privacy before the native PLAY move.
    private void NormalizeBoardTransit(NativePowerHistory packet)
    {
        var list = packet.List;
        // Rewind owns moves inside its reset block; the replay after its end still needs normal board transitions.
        bool reset = false;
        var zones = wireEntities.ToDictionary(pair => pair.Key, pair => pair.Value.Zone);
        for (int index = 0; index < list.Count; index++)
        {
            var row = list[index];
            int id = HistoryEntity(row);
            var snapshot = row.FullEntity ?? row.ShowEntity ?? row.ChangeEntity;
            if (row.HasPowerStart && (int)row.PowerStart.Type == (int)BlockType.GAME_RESET) reset = true;
            if (!reset && row.HasTagChange && row.TagChange.Tag == (int)GameTag.ZONE
                && row.TagChange.Value == (int)EngineZone.SETASIDE
                && zones.TryGetValue(id, out int origin)
                && origin is (int)EngineZone.HAND or (int)EngineZone.DECK or (int)EngineZone.GRAVEYARD)
            {
                int destination = -1;
                for (int ahead = index + 1; ahead < list.Count; ahead++)
                {
                    var next = list[ahead];
                    if (next.HasPowerStart && (int)next.PowerStart.Type == (int)BlockType.GAME_RESET) break;
                    if (HistoryEntity(next) != id) continue;
                    if (next.HasHideEntity || next.HasFullEntity) break;
                    var shown = next.ShowEntity ?? next.ChangeEntity;
                    int nextZone = next.HasTagChange && next.TagChange.Tag == (int)GameTag.ZONE ? next.TagChange.Value
                        : shown?.Tags.FirstOrDefault(t => t.Name == (int)GameTag.ZONE)?.Value ?? (int)EngineZone.SETASIDE;
                    if (nextZone == (int)EngineZone.SETASIDE) continue;
                    if (nextZone == (int)EngineZone.PLAY) destination = ahead;
                    break;
                }
                if (destination >= 0)
                {
                    int position = -1;
                    foreach (var pair in nativeEngineOrder)
                        if (ReferenceEquals(pair.Key, list[destination])) { position = pair.Value; break; }
                    var identity = IdentityAt(position, id, true, true, definitionOnly: true);
                    if (LookupCard(identity?.Name)?.Type is CardType.MINION or CardType.LOCATION)
                    {
                        for (int at = index + 1; at <= destination; at++)
                        {
                            var changed = list[at].ShowEntity ?? list[at].ChangeEntity;
                            if (changed?.Entity == id)
                                foreach (var tag in changed.Tags)
                                    if (tag.Name == (int)GameTag.ZONE && tag.Value == (int)EngineZone.SETASIDE) tag.Value = origin;
                        }
                        list.RemoveAt(index--);
                        BotTrace?.Invoke($"BOARD_TRANSIT match={MatchId} entity={id} card={identity.Name} from={(EngineZone)origin} via=SETASIDE to=PLAY turn={Game.Turn}");
                        continue;
                    }
                }
            }
            if (snapshot != null)
            {
                var zone = snapshot.Tags.FirstOrDefault(t => t.Name == (int)GameTag.ZONE);
                if (zone != null) zones[id] = zone.Value;
            }
            else if (row.HasTagChange && row.TagChange.Tag == (int)GameTag.ZONE) zones[id] = row.TagChange.Value;
            else if (row.HasHideEntity) zones[id] = row.HideEntity.Zone;
            if (reset && row.HasPowerEnd) reset = false;
        }
    }

    // Protobuf entries compare by value and are mutable; distinct equal tasks must keep distinct segment anchors.
    private sealed class WireEntryComparer : IEqualityComparer<PowerHistoryData>
    {
        internal static readonly WireEntryComparer Instance = new();
        public bool Equals(PowerHistoryData x, PowerHistoryData y) => ReferenceEquals(x, y);
        public int GetHashCode(PowerHistoryData entry) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry);
    }
    private sealed class ImpactSubSpell
    {
        internal PegasusGame.PowerHistorySubSpellStart Start;
        internal readonly List<PowerHistoryData> Tasks = new();
        internal bool Closed;
        internal bool Sent;
    }
    private sealed class ImpactFrame
    {
        internal PowerHistoryStart Start;
        internal readonly Stack<ImpactSubSpell> Subs = new();
    }
    private sealed class ImpactSegment
    {
        internal PowerHistoryStart Start;
        internal ImpactSubSpell Sub;
        internal bool AfterAnchor;
        internal readonly List<PowerHistoryData> Tasks = new();
    }
    private readonly Stack<ImpactFrame> impactFrames = new();

    // BuildTaskList (0x5fdf) enqueues at nested blocks, SUB_SPELL_START/END and FLUSH (21).
    // OnPowerHistory (0x5fd4) creates fresh continuation lists, retaining block/subspell origins.
    private Dictionary<PowerHistoryData, ImpactSegment> IndexImpactSegments(List<PowerHistoryData> list)
    {
        foreach (var frame in impactFrames)
            foreach (var sub in frame.Subs) sub.Tasks.Clear();
        var segments = new Dictionary<PowerHistoryData, ImpactSegment>(WireEntryComparer.Instance);
        ImpactSegment current = null;
        ImpactSegment Begin(PowerHistoryData anchor, bool after)
        {
            var frame = impactFrames.Peek();
            return segments[anchor] = new ImpactSegment { Start = frame.Start, AfterAnchor = after,
                Sub = frame.Subs.Count > 0 ? frame.Subs.Peek() : null };
        }
        foreach (var data in list)
        {
            if (data.HasPowerStart)
            {
                impactFrames.Push(new ImpactFrame { Start = data.PowerStart });
                current = Begin(data, true);
            }
            else if (data.HasPowerEnd)
            {
                if (impactFrames.Count > 0) impactFrames.Pop();
                current = null;
            }
            else if (impactFrames.Count > 0)
            {
                var frame = impactFrames.Peek();
                if (data.HasSubSpellStart)
                {
                    frame.Subs.Push(new ImpactSubSpell { Start = data.SubSpellStart });
                    current = Begin(data, true);
                }
                if (current == null) current = Begin(data, false);
                current.Tasks.Add(data);
                current.Sub?.Tasks.Add(data);
                if (data.HasSubSpellEnd)
                {
                    if (frame.Subs.Count > 0) frame.Subs.Pop().Closed = true;
                    current = null;
                }
                else if (data.HasMetaData && (int)data.MetaData.Type == 21) current = null;
            }
        }
        return segments;
    }

    // Pinned DH/Frost child FSMs have no INVALID exit (draw-spell-impact-evidence.md).
    // Card/slot bindings come from data/fx/empty-playeffect-37.0.0.254477.tsv; unknown FX retain native inference.
    internal static bool RequiresTypedImpactTarget(PowerHistoryStart start, string sourceCard,
        PegasusGame.PowerHistorySubSpellStart sub = null)
    {
        if (sub != null)
        {
            string guid = sub.SpellPrefabGuid ?? "";
            guid = guid.Substring(guid.LastIndexOf(':') + 1);
            return guid == "4694fc2ee619cd54c9352938dbf84940" || guid == "2084d102cd356b04ca792130b749cc21";
        }
        if (start.EffectIndex != 0) return false;
        string card = string.IsNullOrEmpty(start.EffectCardId) ? sourceCard : start.EffectCardId;
        return (int)start.Type == (int)BlockType.POWER
            ? card is "CORE_BT_035" or "CORE_BT_491" or "RLK_708"
            : (int)start.Type == (int)BlockType.TRIGGER && card is "CORE_BT_351" or "RLK_708";
    }

    private PowerHistoryData SafeImpactTargets(ImpactSegment segment, ISet<int> retiring)
    {
        // This override installs a spawn on a Card, including an anonymous opposing Card.
        if (segment.Sub?.Start.SpellPrefabGuid == SabberStoneCore.Actions.DrawReplacementRules.ReturnPrefab)
            return null;
        // Every continuation shares the opening descriptor, even within one packet: choose its IDs only at opening.
        if (segment.Sub != null && (segment.Sub.Sent || !segment.AfterAnchor)) return null;
        var start = segment.Start;
        int sourceId = segment.Sub?.Start.SourceEntityId is > 0 ? segment.Sub.Start.SourceEntityId : start.Source;
        if (segment.Sub == null && (int)start.Type != (int)BlockType.POWER && (int)start.Type != (int)BlockType.TRIGGER
            || !wireEntities.TryGetValue(sourceId, out var source) || source.CardType is 1 or 2) return null;
        bool Typed(int id) => wireEntities.TryGetValue(id, out var entity) && entity.CardType is not (0 or 1 or 2 or 6)
            && entity.Zone != (int)EngineZone.REMOVEDFROMGAME
            && (segment.Sub == null || !retiring.Contains(id));
        var metadata = (segment.Sub?.Tasks ?? segment.Tasks).Where(t => t.HasMetaData && (int)t.MetaData.Type == 0
            && (t.MetaData.Data == 0 || t.MetaData.Data == start.Source)).ToArray();
        // Native scans the whole segment, not just tasks before the first hidden tag. Preserve valid late TARGETs.
        if (segment.Sub == null && metadata.Any(t => t.MetaData.Info.Any(Typed))
            && metadata.All(t => t.MetaData.Info.All(Typed))) return null;
        if (segment.Sub?.Start.TargetEntityIds.Count > 0 && segment.Sub.Start.TargetEntityIds.All(Typed)) return null;
        var seen = new HashSet<int>();
        var typed = new List<int>();
        bool untyped = false;
        // Explicit subspell IDs apply to its linked lists. Choose them at the opening's logical state;
        // an open subspell must already be bounded before a later packet can append hidden tasks.
        foreach (var task in segment.Sub?.Tasks ?? segment.Tasks)
        {
            if (!task.HasTagChange) continue;
            int id = task.TagChange.Entity;
            if (id == sourceId || !seen.Add(id) || !wireEntities.TryGetValue(id, out var target)
                || segment.Sub == null && target.Zone == (int)EngineZone.SETASIDE || target.CardType is 1 or 2 or 6) continue;
            if (target.CardType == 0) untyped = true;
            else if (Typed(id)) typed.Add(id);
        }
        // Spawn-to-hand FX can animate this anchor itself. Invent one only for proven no-INVALID-exit impacts.
        if (typed.Count == 0 && !RequiresTypedImpactTarget(start, source.Card, segment.Sub?.Start)) return null;
        var safe = typed.Count > 0 ? typed : Typed(sourceId) ? new List<int> { sourceId }
            : Typed(source.Attached) ? new List<int> { source.Attached } : new List<int>();
        // A detached enchantment / anonymous trigger cannot safely target itself. Its public hero is a
        // presentation anchor only; choose it from logical wire state, never private engine definitions.
        if (safe.Count == 0)
        {
            int hero = wireEntities.Where(p => Typed(p.Key) && p.Value.CardType == (int)CardType.HERO
                && p.Value.Controller == source.Controller && p.Value.Zone == (int)EngineZone.PLAY)
                .Select(p => p.Key).FirstOrDefault();
            if (hero != 0) safe.Add(hero);
        }
        if (safe.Count == 0) return null; // No safe public anchor exists; admission must report the defect.
        if (segment.Sub != null)
        {
            // SubSpellController (0x117d5) ignores TARGET metadata; only nonempty explicit IDs stop its fallback.
            if (untyped || !segment.Sub.Closed || metadata.Any(t => t.MetaData.Info.Any(id => !Typed(id)))
                || segment.Sub.Start.TargetEntityIds.Any(id => !Typed(id)))
            {
                var authored = segment.Sub.Start.TargetEntityIds.Where(Typed).ToList();
                segment.Sub.Start.TargetEntityIds = authored.Count > 0 ? authored : safe;
            }
            return null;
        }
        if (metadata.Length > 0 && metadata.Any(t => t.MetaData.Info.Any(id => !Typed(id))))
        {
            foreach (var task in metadata)
            {
                var valid = task.MetaData.Info.Where(Typed).ToList();
                task.MetaData.Info = valid.Count > 0 ? valid : safe;
            }
            return null;
        }
        if (!untyped) return null;
        // SHOW tasks have not executed at entry. Bound impacts to logical typed candidates without disclosure.
        return new PowerHistoryData { MetaData = new PegasusGame.PowerHistoryMetaData {
            Type = (HistoryMeta.Type)0, Data = 0, Info = safe
        } };
    }

    // A played hero's replacement power is constructed before its battlecry POWER. Put that FULL in
    // the hero's own effect list: strict native scanners (JaraxxusHeroSpell) cannot see the enclosing PLAY's FULL.
    // Cross only unrelated tags under the same-source PLAY; never cross a use of the new entity.
    private void NormalizeHeroPowerOwnership(List<PowerHistoryData> list)
    {
        var heroes = wireEntities.Where(p => p.Value.CardType == (int)CardType.HERO)
            .ToDictionary(p => p.Key, p => p.Value.Controller);
        var blocks = new Stack<PowerHistoryStart>();
        for (int i = 0; i < list.Count; i++)
        {
            var data = list[i];
            var snapshot = data.HasFullEntity ? data.FullEntity : data.HasShowEntity ? data.ShowEntity : data.HasChangeEntity ? data.ChangeEntity : null;
            if (snapshot != null)
            {
                var type = snapshot.Tags.FirstOrDefault(t => t.Name == (int)GameTag.CARDTYPE);
                if (type != null && type.Value != (int)CardType.HERO) heroes.Remove(snapshot.Entity);
                else if (type?.Value == (int)CardType.HERO || heroes.ContainsKey(snapshot.Entity))
                    heroes[snapshot.Entity] = snapshot.Tags.FirstOrDefault(t => t.Name == (int)GameTag.CONTROLLER)?.Value
                        ?? (heroes.TryGetValue(snapshot.Entity, out int owner) ? owner : 0);
            }
            if (data.HasTagChange && data.TagChange.Tag == (int)GameTag.CONTROLLER && heroes.ContainsKey(data.TagChange.Entity))
                heroes[data.TagChange.Entity] = data.TagChange.Value;
            if (data.HasFullEntity && snapshot.Tags.Any(t => t.Name == (int)GameTag.CARDTYPE && t.Value == (int)CardType.HERO_POWER)
                && blocks.Count > 0)
            {
                int nextIndex = i + 1;
                while (nextIndex < list.Count && list[nextIndex].HasTagChange
                    && list[nextIndex].TagChange.Entity != snapshot.Entity && list[nextIndex].TagChange.Value != snapshot.Entity
                    && !(list[nextIndex].TagChange.Entity == blocks.Peek().Source && list[nextIndex].TagChange.Tag == (int)GameTag.CONTROLLER)) nextIndex++;
                if (nextIndex >= list.Count || !list[nextIndex].HasPowerStart) continue;
                var next = list[nextIndex].PowerStart;
                var parent = blocks.Peek();
                if ((int)next.Type == (int)BlockType.POWER && (int)parent.Type == (int)BlockType.PLAY
                    && parent.Source == next.Source && heroes.TryGetValue(next.Source, out int owner)
                    && owner != 0 && snapshot.Tags.Any(t => t.Name == (int)GameTag.CONTROLLER && t.Value == owner))
                {
                    list.RemoveAt(i); list.Insert(nextIndex, data);
                    data = list[i];
                }
            }
            if (data.HasPowerStart) blocks.Push(data.PowerStart);
            else if (data.HasPowerEnd && blocks.Count > 0) blocks.Pop();
        }
    }

    private static int HistoryEntity(PowerHistoryData data) => data.HasFullEntity ? data.FullEntity.Entity
        : data.HasShowEntity ? data.ShowEntity.Entity : data.HasChangeEntity ? data.ChangeEntity.Entity
        : data.HasHideEntity ? data.HideEntity.Entity : data.HasTagChange ? data.TagChange.Entity : 0;

    // A nameless engine card about to be moved into PLAY, a GRAVEYARD, the human's HAND or a public SECRET, or a hidden
    // when-drawn card leaving HAND/DECK for SETASIDE inside its own TOPDECK block (Summoned/Casts When Drawn resolve
    // in the open).
    private (int Id, int Zone, int Controller, bool Own)? PublicIdentityNeed(List<PowerHistoryData> list, int index, int openSource)
    {
        var data = list[index];
        if (!data.HasTagChange || data.TagChange.Tag != (int)GameTag.ZONE && data.TagChange.Tag != (int)GameTag.CONTROLLER) return null;
        int id = data.TagChange.Entity;
        if (!wireEntities.TryGetValue(id, out var wire)) return null;
        int zone = wire.Zone, controller = wire.Controller;
        // One location change may span adjacent tag entries (ZONE then CONTROLLER): judge where it lands. A second
        // ZONE (SETASIDE then PLAY) is a separate client move.
        bool zoneSeen = false, controllerSeen = false;
        for (int next = index; next < list.Count && list[next].HasTagChange && list[next].TagChange.Entity == id; next++)
        {
            var tag = list[next].TagChange;
            if (tag.Tag == (int)GameTag.ZONE && !zoneSeen) { zone = tag.Value; zoneSeen = true; }
            else if (tag.Tag == (int)GameTag.CONTROLLER && !controllerSeen) { controller = tag.Value; controllerSeen = true; }
            else if (tag.Tag == (int)GameTag.ZONE || tag.Tag == (int)GameTag.CONTROLLER) break;
        }
        if (zone == wire.Zone && controller == wire.Controller) return null;
        // Already named actors and presentation-only IDs need no engine identity lookup.
        if (wire.CardType is 1 or 2 || wire.Card.Length > 0 || id <= 0 || !Game.IdEntityDic.ContainsKey(id)) return null;
        int position = -1;
        foreach (var pair in nativeEngineOrder) if (ReferenceEquals(pair.Key, data)) { position = pair.Value; break; }
        var identity = IdentityAt(position, id, currentFirst: true, allowCurrent: true, definitionOnly: true);
        var definition = LookupCard(identity?.Name);
        bool own = id == openSource && zone == (int)EngineZone.SETASIDE && (wire.Zone == (int)EngineZone.HAND || wire.Zone == (int)EngineZone.DECK)
            && definition?.Power?.TopdeckTask != null;
        var entity = Game.IdEntityDic[id];
        if (entity is SabberStoneCore.Model.Entities.Enchantment) return null;
        bool human = controller == HumanPlayerId;
        bool needed = own || zone == (int)EngineZone.PLAY || zone == (int)EngineZone.GRAVEYARD
            || human && zone == (int)EngineZone.HAND
            || zone == (int)EngineZone.SECRET && (human || definition != null && !definition.IsSecret);
        return needed ? (id, zone, controller, own) : ((int, int, int, bool)?)null;
    }

    // A later named snapshot of the same entity in this list, rewritten to the client's current location.
    // HIDE/CHANGE/re-FULL/reset are hard boundaries: no later identity crosses the move.
    // A transform (CHANGE) never moves ahead of the move: its post-transform card would bind the actor too early.
    // Inside a rewind reset the card's identity is its checkpoint FULL (the client drops that FULL for a known entity).
    private PowerHistoryData HoistedIdentity(List<PowerHistoryData> list, int index, int id, bool reset)
    {
        PegasusGame.PowerHistoryEntity Copy(PegasusGame.PowerHistoryEntity snapshot) => new PegasusGame.PowerHistoryEntity { Entity = id,
            Name = snapshot.Name, Tags = AtWireLocation(id, snapshot.Tags.Select(tag => new NativeTag { Name = tag.Name, Value = tag.Value })) };
        if (reset)
        {
            int start = list.FindLastIndex(index, data => data.HasPowerStart && (int)data.PowerStart.Type == (int)BlockType.GAME_RESET);
            for (int at = start + 1; at < list.Count && !list[at].HasPowerEnd; at++)
                if (list[at].HasFullEntity && list[at].FullEntity.Entity == id)
                    return list[at].FullEntity.Name.Length == 0 ? null : new PowerHistoryData { ShowEntity = Copy(list[at].FullEntity) };
            return null;
        }
        for (int ahead = index + 1; ahead < list.Count; ahead++)
        {
            var data = list[ahead];
            if (data.HasHideEntity && data.HideEntity.Entity == id) return null;
            if (data.HasPowerStart && (int)data.PowerStart.Type == (int)BlockType.GAME_RESET) return null;
            if (data.HasChangeEntity && data.ChangeEntity.Entity == id || data.HasFullEntity && data.FullEntity.Entity == id) return null;
            var snapshot = data.HasFullEntity ? data.FullEntity : data.HasShowEntity ? data.ShowEntity : null;
            if (snapshot == null || snapshot.Entity != id) continue;
            return snapshot.Name.Length == 0 ? null : new PowerHistoryData { ShowEntity = Copy(snapshot) };
        }
        return null;
    }

    // Reveal only where the card is public at the move: entering PLAY (not a Secret), the human's HAND or SECRET, or a
    // when-drawn card leaving the hand. Never a GRAVEYARD or opponent SECRET (destroyed Secrets, rejected or destroyed
    // deck offers stay hidden: retail reveals none of them unprompted). Identity and privacy come from the engine
    // snapshot this list carried for the card at or before the move.
    private PowerHistoryData RevealAtMove(List<PowerHistoryData> list, int index, int id, int zone, int controller, bool own)
    {
        var entity = Game.IdEntityDic[id];
        bool human = controller == HumanPlayerId;
        if (privateChoiceOffers.Contains(id)) return null;
        // The card as it was at the move (IdentityAt); the current definition only for the human's own card entering
        // the human's own zone or public PLAY (the engine never names a deck card). IdentityAt rejects a current
        // definition when a later CHANGE/re-FULL could make it an identity from the future.
        int position = -1;
        foreach (var pair in nativeEngineOrder) if (ReferenceEquals(pair.Key, list[index])) { position = pair.Value; break; }
        var engine = IdentityAt(position, id, currentFirst: false,
            allowCurrent: (zone == (int)EngineZone.PLAY || human && entity.Controller?.PlayerId == HumanPlayerId)
                && (position >= 0 || identityBatch == null));
        if (engine == null) return null;
        var definition = LookupCard(engine.Name);
        bool publicZone = own || zone == (int)EngineZone.PLAY && definition != null && !(definition.IsSecret && !definition.IsQuest)
            || human && (zone == (int)EngineZone.HAND || zone == (int)EngineZone.SECRET);
        if (!publicZone) return null;
        // Hidden if the snapshot at the move says so, or the engine has since marked it private (internal markers
        // such as a Shatter hand owner carry no history): never reveal on either doubt.
        var destination = new Dictionary<GameTag, int> { [GameTag.ZONE] = zone, [GameTag.CONTROLLER] = controller };
        var tags = new Dictionary<GameTag, int>(engine.Tags) { [GameTag.ZONE] = zone, [GameTag.CONTROLLER] = controller };
        if (TagValue(engine.Tags, SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag) == BotPlayerId
            || IsHiddenFromHuman(id, tags, new Dictionary<int, EngineHistoryEntity>()) || IsHiddenFromHuman(id, destination)) return null;
        var shown = ToNativeHistoryEntity(new EngineHistoryEntity { Id = id, Name = engine.Name, Tags = engine.Tags }, true);
        if (shown.Name.Length == 0) return null;
        shown.Tags = AtWireLocation(id, shown.Tags);
        return new PowerHistoryData { ShowEntity = shown };
    }

    private void ForgetRemoved()
    {
        // ZONE=REMOVEDFROMGAME queues GameState.RemoveEntity: a later FULL of the id is a new entity to the client.
        foreach (int id in wireEntities.Where(pair => pair.Value.Zone == (int)EngineZone.REMOVEDFROMGAME).Select(pair => pair.Key).ToArray())
        {
            wireEntities.Remove(id);
            engineIdentityHistory.Remove(id);
        }
    }

    private List<NativeTag> AtWireLocation(int id, IEnumerable<NativeTag> tags)
    {
        var wire = wireEntities[id];
        var located = tags.Where(tag => tag.Name != (int)GameTag.ZONE && tag.Name != (int)GameTag.ZONE_POSITION
            && tag.Name != (int)GameTag.CONTROLLER).ToList();
        located.Add(new NativeTag { Name = (int)GameTag.ZONE, Value = wire.Zone });
        located.Add(new NativeTag { Name = (int)GameTag.ZONE_POSITION, Value = wire.Position });
        located.Add(new NativeTag { Name = (int)GameTag.CONTROLLER, Value = wire.Controller });
        return located;
    }

    private void ApplyWire(PowerHistoryData data, bool reset)
    {
        WireEntity Wire(int id)
        {
            if (!wireEntities.TryGetValue(id, out var known)) wireEntities[id] = known = new WireEntity();
            return known;
        }
        void Locate(WireEntity wire, IEnumerable<NativeTag> tags)
        {
            foreach (var tag in tags)
                if (tag.Name == (int)GameTag.ZONE) wire.Zone = tag.Value;
                else if (tag.Name == (int)GameTag.CONTROLLER) wire.Controller = tag.Value;
                else if (tag.Name == (int)GameTag.ZONE_POSITION) wire.Position = tag.Value;
                else if (tag.Name == (int)GameTag.CARDTYPE) wire.CardType = tag.Value;
                else if (tag.Name == (int)GameTag.ATTACHED) wire.Attached = tag.Value;
        }
        var create = data.CreateGame ?? data.ResetGame?.CreateGame;
        if (create != null)
        {
            Locate(Wire(create.GameEntity.Id), create.GameEntity.Tags);
            foreach (var player in create.Players) Locate(Wire(player.Entity.Id), player.Entity.Tags);
        }
        var snapshot = data.HasFullEntity ? data.FullEntity : data.HasShowEntity ? data.ShowEntity : data.HasChangeEntity ? data.ChangeEntity : null;
        if (snapshot != null)
        {
            // The client discards a rewind reset FULL for an entity it already has: only a transform reloads its card id,
            // and its priority-tag reset tasks move the Card when the reset block closes, after the plugin's own entries.
            if (reset && data.HasFullEntity && wireEntities.TryGetValue(snapshot.Entity, out var known))
            {
                if (known.Card.Length > 0 && snapshot.Name.Length > 0 && known.Card != snapshot.Name) known.Card = snapshot.Name;
                wireResetLocations[snapshot.Entity] = snapshot.Tags.Where(tag => tag.Name == (int)GameTag.ZONE
                    || tag.Name == (int)GameTag.CONTROLLER || tag.Name == (int)GameTag.ZONE_POSITION).ToArray();
                return;
            }
            var wire = Wire(snapshot.Entity);
            wire.Card = snapshot.Name;
            Locate(wire, snapshot.Tags);
        }
        else if (data.HasHideEntity)
        {
            var wire = Wire(data.HideEntity.Entity);
            wire.Card = "";
            wire.Zone = data.HideEntity.Zone;
        }
        else if (data.HasTagChange)
            Locate(Wire(data.TagChange.Entity), new[] { new NativeTag { Name = data.TagChange.Tag, Value = data.TagChange.Value } });
    }

    private int AppendRewindReset(NativePowerHistory packet, List<IPowerHistoryEntry> entries, int start)
    {
        if (start + 1 >= entries.Count || !(entries[start + 1] is SabberStoneCore.Kettle.PowerHistoryResetGame reset) || !reset.SourceIsRewind)
            throw new InvalidOperationException("GAME_RESET requires a Rewind reset payload.");
        int end = entries.FindIndex(start + 2, entry => entry is PowerHistoryBlockEnd);
        if (end < 0 || entries.Skip(start + 2).Take(end - start - 2).Any(entry => !(entry is PowerHistoryFullEntity)))
            throw new InvalidOperationException("Rewind reset must be one complete snapshot block.");
        var fulls = entries.Skip(start + 2).Take(end - start - 2).Cast<PowerHistoryFullEntity>().ToArray();
        int max = fulls.Max(full => full.Entity.Id);
        var previous = clientTransportTags.ToDictionary(pair => pair.Key, pair => new Dictionary<int, int>(pair.Value));
        var cards = new Dictionary<int, string>(clientTransportCards);
        if (rewindAnonymousEntities == null)
            rewindAnonymousEntities = new HashSet<int>(cards.Where(pair => string.IsNullOrEmpty(pair.Value)).Select(pair => pair.Key));
        revealedEntities.Clear(); visibleTransportTags.Clear(); clientTransportTags.Clear(); clientTransportCards.Clear();
        historicalControllers.Clear(); historicalZones.Clear(); historicalPositions.Clear(); historicalTemporaryVisuals.Clear(); temporaryDiscardVisuals.Clear();
        privacyTargets.Clear(); retiredChoiceActors.Clear(); opposingProxies.Clear(); deckChoiceOriginals.Clear(); privateDeckOffers.Clear(); privateChoiceOffers.Clear(); visibleSubSpells.Clear(); visualBlockSources.Clear(); revealedTriggerScopes.Clear(); pendingTriggerReveals.Clear(); lastSpellPower.Clear(); poweredUpHighlights.Clear();
        opposingChoices = null; opposingChoiceSource = null; opposingChoiceOriginals = null; opposingPicks = null; deferredOpposing.Clear();
        decisionHandTargetSource = 0;
        substitutedTargetSource = 0;
        leylineTextEntities.Clear();
        foreach (int id in temporaryHandOrigins.Keys.Where(id => id > max).ToArray()) temporaryHandOrigins.Remove(id);
        premiumBlocks.Clear();
        // Derived premium is restored to the checkpoint's (transforms and creations after it are undone).
        if (rewindDerivedPremiums != null)
        {
            derivedPremiums.Clear();
            foreach (var pair in rewindDerivedPremiums) derivedPremiums[pair.Key] = pair.Value;
        }
        foreach (int id in derivedPremiums.Keys.Where(id => id > max).ToArray()) derivedPremiums.Remove(id);
        humanChoices = null; humanOptions = null; humanDecisionId = 0; humanDecisionController = 0;
        humanPlacementBoardHadLocations = false;
        // Replay completions are already recorded against the new graph; retain only those after this reset.
        copyCompletions.RemoveAll(completion => !ReferenceEquals(completion.Entries, entries) || completion.HistoryIndex <= end || !Game.IdEntityDic.ContainsKey(completion.EntityId));
#if STANDARD_PROOF
        proofHistoricalLiveTags.Clear();
#endif
        TrackHistoricalState(reset.CreateGame);
        foreach (var full in fulls) TrackHistoricalState(full);
        // The restored checkpoint is the identity now: a discarded attempt's transforms must not survive in the cache.
        engineIdentityHistory.Clear();
        foreach (var full in fulls.Where(full => !string.IsNullOrEmpty(full.Entity.Name)))
            engineIdentityHistory[full.Entity.Id] = full.Entity;
        effectPlayScopes.Clear(); scopedRevealSource = 0;
        if (rewindPrivacy != null)
            foreach (var pair in rewindPrivacy.Where(pair => pair.Key <= max))
                privacyTargets[pair.Key] = new HashSet<int>(pair.Value.Where(id => id <= max));
        if (rewindChoiceOffers != null) privateChoiceOffers.UnionWith(rewindChoiceOffers.Where(id => id <= max));
        var resetEntities = fulls.ToDictionary(full => full.Entity.Id, full => full.Entity);
        foreach (var entity in new[] { reset.CreateGame.Game }.Concat(reset.CreateGame.Players.Select(player => player.PowerEntity)))
            resetEntities.Add(entity.Id, new EngineHistoryEntity { Id = entity.Id, Tags = entity.Tags });
        RestoreResetPrivacy(resetEntities);
        // Retire discarded actors before the native reset filters the old timeline.
        foreach (var pair in previous.OrderBy(pair => pair.Key))
        {
            if (resetEntities.ContainsKey(pair.Key) || pair.Key == cosmeticPetEntityId || OpponentPetCard != null && pair.Key == opponentPetEntityId ||
                pair.Value.TryGetValue((int)GameTag.ZONE, out int zone) && zone == (int)EngineZone.REMOVEDFROMGAME) continue;
            packet.List.Add(new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = pair.Key, Tag = (int)GameTag.ZONE, Value = (int)EngineZone.REMOVEDFROMGAME
            } });
        }
        packet.List.Add(ToNativeHistory(entries[start]));
        var resetPacket = ToNativeHistory(reset);
        packet.List.Add(resetPacket); TrackVisibility(resetPacket);
        var restored = new Dictionary<int, Dictionary<int, int>>();
        restored[resetPacket.ResetGame.CreateGame.GameEntity.Id] = resetPacket.ResetGame.CreateGame.GameEntity.Tags.ToDictionary(tag => tag.Name, tag => tag.Value);
        foreach (var player in resetPacket.ResetGame.CreateGame.Players)
            restored[player.Entity.Id] = player.Entity.Tags.ToDictionary(tag => tag.Name, tag => tag.Value);
        var snapshots = new List<PegasusGame.PowerHistoryEntity>();
        foreach (var full in fulls)
        {
            var entity = new EngineHistoryEntity { Id = full.Entity.Id, Name = full.Entity.Name,
                Tags = new Dictionary<GameTag, int>(full.Entity.Tags) };
            if (rewindHighlights?.Contains(entity.Id) == true) entity.Tags[GameTag.POWERED_UP] = 1;
            var native = ToNativeHistoryEntity(entity, resetEntities: resetEntities);
            snapshots.Add(native);
            restored[native.Entity] = native.Tags.ToDictionary(tag => tag.Name, tag => tag.Value);
            var update = new PowerHistoryData { FullEntity = native };
            packet.List.Add(update); TrackVisibility(update);
        }
        if (cosmeticPet != null) packet.List.Add(NativePetEntity(cosmeticPetEntityId, cosmeticPet, HumanPlayerId, cosmeticPetVariant, cosmeticPetFamilyId, HumanPetTreatTags()));
        if (OpponentPetCard != null) packet.List.Add(NativePetEntity(opponentPetEntityId, OpponentPetCard, BotPlayerId, opponentPetVariant, opponentPetFamilyId));
        // FULL merges and the client only diffs priority tags. Explicitly correct every formerly held tag.
        foreach (var pair in restored.OrderBy(pair => pair.Key))
        {
            if (!previous.TryGetValue(pair.Key, out var old)) continue;
            bool moved = new[] { (int)GameTag.ZONE, (int)GameTag.CONTROLLER }.Any(tag =>
                old.TryGetValue(tag, out int before) != pair.Value.TryGetValue(tag, out int current) || before != current);
            foreach (int tag in old.Keys.Concat(pair.Value.Keys).Distinct().OrderBy(tag => tag))
            {
                int value = pair.Value.TryGetValue(tag, out int current) ? current : 0;
                // ZoneChangeList clears Card position on a move even when the entity's position tag is unchanged.
                // A retained card's wire reference can change between zero and an alias while its engine ID stays fixed.
                bool returnReference = ReturnEntityTag(tag) && retainedOverdrawEntities.Contains(value);
                if (old.TryGetValue(tag, out int before) && before == value && !(moved && tag == (int)GameTag.ZONE_POSITION) && !returnReference) continue;
                if (!old.ContainsKey(tag) && value == 0) continue;
                var update = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                    Entity = pair.Key, Tag = tag, Value = value
                } };
                packet.List.Add(update); TrackVisibility(update);
            }
        }
        foreach (var snapshot in snapshots)
            if (cards.TryGetValue(snapshot.Entity, out var old) && old != snapshot.Name)
            {
                // The pinned Rewind controller discards CHANGE_ENTITY to base Clocksworth.
                var update = snapshot.Name == "TIME_038"
                    ? new PowerHistoryData { ShowEntity = snapshot }
                    : new PowerHistoryData { ChangeEntity = snapshot };
                packet.List.Add(update); TrackVisibility(update);
            }
        packet.List.Add(ToNativeHistory(entries[end]));
        if (rewindHighlights != null) poweredUpHighlights.UnionWith(rewindHighlights.Where(revealedEntities.Contains));
        // All caches were rebuilt from the restored registry; candidate IDs above max are gone.
        if (clientTransportTags.Keys.Any(id => id > max)) throw new InvalidOperationException("Rewind cache cutoff failed.");
        hasRewound = true;
        return end;
    }

    private void RestoreResetPrivacy(IReadOnlyDictionary<int, EngineHistoryEntity> entities)
    {
        // Checkpoint snapshots exclusively; discarded-attempt definitions cannot participate.
        void Link(int id, int target)
        {
            if (id == target || !entities.ContainsKey(id) || !entities.ContainsKey(target)) return;
            if (!privacyTargets.TryGetValue(id, out var targets)) privacyTargets[id] = targets = new HashSet<int>();
            targets.Add(target);
        }
        void Gift(int id, int target)
        {
            if (!entities.TryGetValue(id, out var spell) || !DarkGifts.Registry.Any(gift => gift.SpellId == spell.Name)) return;
            Link(id, target);
            int creator = TagValue(spell.Tags, GameTag.CREATOR);
            if (entities.TryGetValue(creator, out var executor) && executor.Name == "EDR_102t") Link(creator, target);
        }
        foreach (var entity in entities.Values)
        {
            if (TagValue(entity.Tags, SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag) != 0)
                Link(entity.Id, TagValue(entity.Tags, GameTag.PARENT_CARD));
            Gift(TagValue(entity.Tags, GameTag.DARK_GIFT_ENTITY), entity.Id);
            int attachment = TagValue(entity.Tags, GameTag.ATTACHED);
            if (entities.TryGetValue(attachment, out var target) &&
                TagValue(target.Tags, SabberStoneCore.Actions.HandProgressRules.PrivateAttachmentTarget) != 0)
                Link(entity.Id, target.Id);
            bool gift = TagValue(entity.Tags, GameTag.IS_NIGHTMARE_BONUS) == 1;
            if (attachment != 0) Link(entity.Id, attachment);
            if (gift) Gift(TagValue(entity.Tags, GameTag.CREATOR), TagValue(entity.Tags, GameTag.ATTACHED));
        }
    }
#endif

#if ENGINE_SOURCE
    // ZoneChangeList.ProcessChanges clears a Card's position on every zone/controller transition and then applies only
    // an explicit destination position. The engine omits ZONE_POSITION when a card keeps its slot number (opposing hand
    // slot 3 -> board slot 3), so the actor stays at position 0 and Zone.CardSortComparison sorts it first on the board;
    // ZoneMgr.AutoCorrectZones never repairs the opposing battlefield. A board move always carries its position.
    private void AppendMovedBoardPosition(NativePowerHistory packet, PowerHistoryData native, List<IPowerHistoryEntry> entries, int index)
    {
        if (!native.HasTagChange) return;
        var change = native.TagChange;
        if (change.Tag != (int)GameTag.ZONE && change.Tag != (int)GameTag.CONTROLLER) return;
        int id = change.Entity;
        // Engine values at this point of the batch: the client copy can hold a presentation-only 0 (restored choice
        // actors, deck-offer snapshots) while the engine position is unchanged and therefore never re-sent.
        var zone = change.Tag == (int)GameTag.ZONE ? (EngineZone)change.Value
            : historicalZones.TryGetValue(id, out var held) ? held : EngineZone.INVALID;
        if (zone != EngineZone.PLAY || !Game.IdEntityDic.TryGetValue(id, out var moved)
            || !(LookupCard(IdentityAt(index, id, true, true, definitionOnly: true)?.Name)?.Type is CardType.MINION or CardType.LOCATION)
            || !historicalPositions.TryGetValue(id, out int position) || position <= 0) return;
        // The engine's own later position counts only within this block: a nested block is a separate client task
        // list, so its position (a Colossal appendage or a death shifting the card) arrives after this move is drawn.
        for (int ahead = index + 1; ahead < entries.Count; ahead++)
        {
            if (entries[ahead] is PowerHistoryBlockStart || entries[ahead] is PowerHistoryBlockEnd) break;
            if (entries[ahead] is SabberStoneCore.Kettle.PowerHistoryTagChange next && next.EntityId == id)
            {
                if (next.Tag == GameTag.ZONE_POSITION) return;
                if (next.Tag == GameTag.ZONE || next.Tag == GameTag.CONTROLLER) break;
            }
        }
        var update = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
            Entity = id, Tag = (int)GameTag.ZONE_POSITION, Value = position } };
        packet.List.Add(update);
        TrackVisibility(update);
    }
#endif

    private void TrackVisibility(PowerHistoryData data)
    {
#if ENGINE_SOURCE
        if (data.HasCreateGame || data.HasResetGame) publishedShatterActors.Clear();
        if (data.HasFullEntity) publishedShatterActors.Add(data.FullEntity.Entity);
#endif
#if ENGINE_SOURCE
        void ForgetTriggerReveal(int id)
        {
            pendingTriggerReveals.Remove(id);
            var sources = visualBlockSources.ToArray();
            var scopes = revealedTriggerScopes.ToArray();
            for (int i = 0; i < scopes.Length && i < sources.Length; i++)
                if (sources[i] == id) scopes[i] = false;
            revealedTriggerScopes.Clear();
            for (int i = scopes.Length - 1; i >= 0; i--) revealedTriggerScopes.Push(scopes[i]);
        }
        var identity = data.HasFullEntity ? data.FullEntity : data.HasShowEntity ? data.ShowEntity : data.HasChangeEntity ? data.ChangeEntity : null;
        bool changedIdentity = identity != null && clientTransportCards.TryGetValue(identity.Entity, out var priorIdentity)
            && priorIdentity.Length > 0 && identity.Name != priorIdentity;
        if (changedIdentity) ForgetTriggerReveal(identity.Entity);
        TrackClientState(data);
        if (data.ResetGame != null)
        {
            pendingTriggerReveals.Clear();
            foreach (int id in visualBlockSources.ToArray()) ForgetTriggerReveal(id);
        }
        if (data.HasHideEntity) ForgetTriggerReveal(data.HideEntity.Entity);
        if (data.HasTagChange && data.TagChange.Tag == (int)GameTag.ZONE
            && (data.TagChange.Value == (int)EngineZone.HAND || data.TagChange.Value == (int)EngineZone.DECK || data.TagChange.Value == (int)EngineZone.SECRET))
            ForgetTriggerReveal(data.TagChange.Entity);
        if (!changedIdentity && data.HasShowEntity && data.ShowEntity.Name.Length > 0 && (data.ShowEntity.Name == "END_024" || data.ShowEntity.Name == "FIR_902"))
            pendingTriggerReveals.Add(data.ShowEntity.Entity);
        if (data.HasShowEntity && data.ShowEntity.Name.Length == 0) ForgetTriggerReveal(data.ShowEntity.Entity);
#endif
        if (data.HasHideEntity)
        {
            revealedEntities.Remove(data.HideEntity.Entity);
            // HIDE retains printed tags, race flags and alternate payment; reveal must clear stale values.
            if (visibleTransportTags.TryGetValue(data.HideEntity.Entity, out var hiddenTags))
            {
                foreach (int tag in hiddenTags.Keys.Where(tag =>
                    (!PrintedDefinitionTags.Contains((GameTag)tag)
#if ENGINE_SOURCE
                        && !EngineCard.AdditionalRaceTags.ContainsKey((GameTag)tag) && tag != 2837
#endif
                    ) || hiddenTags[tag] == 0).ToArray())
                    hiddenTags.Remove(tag);
                if (hiddenTags.Count == 0) visibleTransportTags.Remove(data.HideEntity.Entity);
            }
        }
        var snapshot = data.HasFullEntity ? data.FullEntity
            : data.HasShowEntity ? data.ShowEntity : data.HasChangeEntity ? data.ChangeEntity : null;
#if ENGINE_SOURCE
        if (snapshot != null && snapshot.Name.Length == 0) ForgetTriggerReveal(snapshot.Entity);
        if (snapshot != null && !data.HasShowEntity && snapshot.Tags.Any(tag => tag.Name == (int)GameTag.ZONE
            && (tag.Value == (int)EngineZone.HAND || tag.Value == (int)EngineZone.DECK || tag.Value == (int)EngineZone.SECRET)))
            ForgetTriggerReveal(snapshot.Entity);
#endif
        // Hidden snapshots clear native identity too; a later public play must reveal it again.
        if (snapshot != null && snapshot.Name.Length == 0)
            revealedEntities.Remove(snapshot.Entity);
        if (snapshot != null && snapshot.Name.Length > 0)
        {
            revealedEntities.Add(snapshot.Entity);
            // Native TagMap.SetTags merges snapshots; omitted tags retain their last sent values.
            if (!visibleTransportTags.TryGetValue(snapshot.Entity, out var changedTags))
                visibleTransportTags[snapshot.Entity] = changedTags = new Dictionary<int, int>();
            foreach (NativeTag tag in snapshot.Tags)
                changedTags[tag.Name] = tag.Value;
        }
        if (data.HasTagChange && visibleTransportTags.TryGetValue(data.TagChange.Entity, out var tags))
            tags[data.TagChange.Tag] = data.TagChange.Value;
    }

#if STANDARD_PROOF
    internal bool RequiredLiveTagMatches(IPlayable entity, int tag, bool nativePresent, int nativeValue)
    {
        if (!StandardProofNative.RequiresLiveTag(TransportCardId(entity.Card.Id), tag)) return true;
        var zone = entity.Zone?.Type ?? EngineZone.INVALID;
        int expected;
        if (!(entity is SabberStoneCore.Model.Entities.Enchantment) && (zone == EngineZone.HAND || zone == EngineZone.PLAY))
            expected = entity[(GameTag)tag];
        else if (!proofHistoricalLiveTags.TryGetValue(entity.Id, out var history) || !history.TryGetValue(tag, out expected))
            return false;
        return nativePresent && nativeValue == expected;
    }
#endif
    internal IReadOnlyDictionary<int, int> SnapshotVisibleTransportTags(IPlayable entity)
    {
        // Read-only endpoint audit; the converted snapshot returns tags only and is never sent as SHOW/FULL.
        if (entity == null) throw new InvalidOperationException("Transport snapshot requires an already-visible entity: null entity");
        if (!revealedEntities.Contains(entity.Id))
            throw new TransportEntityNotVisibleException("Transport snapshot requires an already-visible entity: "
                + "engine id=" + entity.Id + " card=" + entity.Card.Id + " zone=" + (entity.Zone?.Type.ToString() ?? "none")
                + " controller=" + (entity.Controller?.PlayerId ?? 0) + " revealed=false tagsCached=" + visibleTransportTags.ContainsKey(entity.Id)
                + " hiddenFromHuman=" + IsHiddenFromHuman(entity.Id, null));
        if (!visibleTransportTags.TryGetValue(entity.Id, out var cached))
            throw new InvalidOperationException("Visible transport snapshot is unavailable: " + entity.Id);
        EngineZone zone = entity.Zone?.Type ?? EngineZone.INVALID;
        if (!(entity is SabberStoneCore.Model.Entities.Enchantment)
            && (zone == EngineZone.HAND || zone == EngineZone.PLAY))
        {
            var snapshot = PowerHistoryBuilder.FullEntity(entity).Entity;
            if (entity is Character)
                snapshot.Tags[GameTag.HEALTH] = entity[GameTag.HEALTH];
            foreach (int nativeTag in cached.Keys)
            {
                var tag = (GameTag)nativeTag;
                // Reset clears old-only metadata; an audit must not resurrect its unprojected CardDef default.
                snapshot.Tags[tag] = cached[nativeTag] == 0 && !snapshot.Tags.ContainsKey(tag) && !PrintedDefinitionTags.Contains(tag)
                    ? 0 : PresentedTagValue(entity, tag);
            }
            foreach (var stat in PresentedStats(entity))
                if (stat.Value != 0 || cached.ContainsKey((int)stat.Key))
                    snapshot.Tags[stat.Key] = stat.Value;
#if ENGINE_SOURCE || STANDARD_PROOF
            // Derive the audit expectation even before the first highlight packet is published.
            if ((HasPoweredUpHighlight(entity) || poweredUpHighlights.Contains(entity.Id))
                && (cached.ContainsKey((int)GameTag.POWERED_UP) || PoweredUpHighlight(entity) == 1))
                snapshot.Tags[GameTag.POWERED_UP] = PoweredUpHighlight(entity);
#endif
            // No-op engine setters can create zero entries without emitting history (for example Silence).
            foreach (GameTag tag in entity.NativeTags.Keys)
                if (!cached.ContainsKey((int)tag) && entity.Card[tag] == 0 && entity[tag] == 0
                    && snapshot.Tags.TryGetValue(tag, out int value) && value == 0
#if STANDARD_PROOF
                    && !StandardProofNative.RequiresLiveTag(TransportCardId(entity.Card.Id), (int)tag)
#endif
                )
                    snapshot.Tags.Remove(tag);
            return AuditReturnReferences(ToNativeHistoryEntity(snapshot, true).Tags.ToDictionary(tag => tag.Name, tag => tag.Value));
        }
        var historical = new Dictionary<int, int>(cached);
#if ENGINE_SOURCE || STANDARD_PROOF
        if (entity.Card[(GameTag)1333] == 1 && cached.ContainsKey((int)GameTag.POWERED_UP))
            historical[(int)GameTag.POWERED_UP] = PoweredUpHighlight(entity);
#endif
        return AuditReturnReferences(historical);
    }

    private IReadOnlyDictionary<int, int> AuditReturnReferences(Dictionary<int, int> tags)
    {
#if ENGINE_SOURCE
        foreach (int tag in tags.Keys.Where(ReturnEntityTag).ToArray()) tags[tag] = WireReturnReference(tag, tags[tag]);
#endif
        return tags;
    }

    internal void RecordCopyCompletion(List<IPowerHistoryEntry> entries, int historyIndex, IPlayable destination)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (historyIndex < 0 || historyIndex > entries.Count)
            throw new ArgumentOutOfRangeException(nameof(historyIndex));
        if (!ReferenceEquals(destination.Game, Game) || destination.Zone?.Type != EngineZone.PLAY)
            throw new InvalidOperationException("Copy completion destination is outside the active game board.");
        var tags = new Dictionary<GameTag, int>();
        foreach (GameTag tag in CopyCompletionTags)
            tags[tag] = destination[tag];
        RecordTransportCorrection(entries, historyIndex, destination.Id, tags, false);
    }

    internal void RecordTransportCorrection(List<IPowerHistoryEntry> entries, int historyIndex,
        int entityId, IReadOnlyDictionary<GameTag, int> tags, bool requiresPublicReplay)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        if (tags == null) throw new ArgumentNullException(nameof(tags));
        if (historyIndex < 0 || historyIndex > entries.Count)
            throw new ArgumentOutOfRangeException(nameof(historyIndex));
        if (entityId <= 0) throw new ArgumentOutOfRangeException(nameof(entityId));
        if (tags.Count == 0) return;
        copyCompletions.Add(new CopyCompletion {
            Entries = entries,
            HistoryIndex = historyIndex,
            EntityId = entityId,
            Tags = tags.ToDictionary(pair => pair.Key, pair => pair.Value),
            RequiresPublicReplay = requiresPublicReplay
        });
    }

    private void AppendCopyCompletions(NativePowerHistory packet, List<IPowerHistoryEntry> entries, int historyIndex)
    {
        foreach (CopyCompletion completion in copyCompletions.Where(candidate =>
            ReferenceEquals(candidate.Entries, entries) && candidate.HistoryIndex == historyIndex))
        {
            if (completion.RequiresPublicReplay && !IsPublicAtReplay(completion.EntityId))
                continue;
            foreach (GameTag tag in completion.Tags.Keys)
            {
                var change = new PowerHistoryData {
                    TagChange = new PegasusGame.PowerHistoryTagChange {
                        Entity = completion.EntityId,
                        Tag = (int)tag,
                        Value = completion.Tags[tag]
                    }
                };
                packet.List.Add(change);
                TrackVisibility(change);
            }
        }
    }

    private bool IsPublicAtReplay(int entityId)
    {
        if (!revealedEntities.Contains(entityId)) return false;
        int zone = historicalZones.TryGetValue(entityId, out EngineZone historicalZone)
            ? (int)historicalZone
            : visibleTransportTags.TryGetValue(entityId, out var tags)
                && tags.TryGetValue((int)GameTag.ZONE, out int visibleZone) ? visibleZone : (int)EngineZone.INVALID;
        if (zone == (int)EngineZone.PLAY) return true;
        int controller = historicalControllers.TryGetValue(entityId, out int historicalController)
            ? historicalController
            : visibleTransportTags.TryGetValue(entityId, out tags)
                && tags.TryGetValue((int)GameTag.CONTROLLER, out int visibleController) ? visibleController : 0;
        return zone == (int)EngineZone.HAND && controller == HumanPlayerId;
    }

    private PowerHistoryData ToNativeHistory(IPowerHistoryEntry entry)
    {
#if ENGINE_SOURCE
        if (entry is SabberStoneCore.Kettle.PowerHistoryResetGame resetGame)
            return new PowerHistoryData { ResetGame = new PegasusGame.PowerHistoryResetGame {
                CreateGame = ToNativeCreateGame(resetGame.CreateGame, true), SourceIsRewind = resetGame.SourceIsRewind
            } };
#endif
        if (entry is SabberStoneCore.Kettle.PowerHistoryCreateGame create)
            return new PowerHistoryData { CreateGame = ToNativeCreateGame(create) };
        // FULL/SHOW/CHANGE and EffectCardId below are immutable engine-entry values, not live identity getters.
        if (entry is PowerHistoryFullEntity full)
        {
            if (!entityPremiums.ContainsKey(full.Entity.Id) && CarriesPremium(full.Entity, out string createdId))
                DeriveCreatedPremium(full.Entity.Id, createdId);
            return new PowerHistoryData { FullEntity = ToNativeHistoryEntity(full.Entity, currentHealthSnapshot: true) };
        }
        if (entry is PowerHistoryShowEntity show)
            return new PowerHistoryData
            {
                // Human draws reveal identity in their destination-zone SHOW; opponent draws stay hidden.
                ShowEntity = ToNativeHistoryEntity(show.Entity,
                    ((
#if ENGINE_SOURCE
                    TagValue(show.Entity.Tags, SabberStoneCore.Actions.TemporaryRules.DiscardReveal) == 1 ||
#endif
                    TagValue(show.Entity.Tags, GameTag.CONTROLLER) == HumanPlayerId)
#if ENGINE_SOURCE
                    && !privacyTargets.ContainsKey(show.Entity.Id)
#endif
                    ) || TagValue(show.Entity.Tags, GameTag.REVEALED) == 1)
            };
        if (entry is PowerHistoryHideEntity hide)
            return new PowerHistoryData { HideEntity = new PowerHistoryHide { Entity = hide.EntityID, Zone = HiddenHistoryZone(hide.EntityID, (int)hide.Zone) } };
        if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange tag)
        {
            if (tag.Tag == GameTag.CONTROLLER && powerControllers.ContainsKey(tag.EntityId))
                ObservePowerController(tag.EntityId, tag.Value);
            // Engine-only discard evidence is consumed by pet projection, never native controller state.
            if (tag.Tag == GameTag.LAST_CARD_DISCARDED) return null;
            // Native HERALD_COLOSSAL_AMOUNT semantics are unqualified; never publish our internal count.
            if ((int)tag.Tag == 4327) return null;
            if (IsDecisionInternalTag(tag.Tag)) return null;
#if ENGINE_SOURCE
            // A subsequently revealed deck source must not be linked to an earlier hidden deck position.
            if (tag.Tag == GameTag.ZONE_POSITION && startOfGameSources.Contains(tag.EntityId)
                && historicalZones.TryGetValue(tag.EntityId, out var sourceZone) && sourceZone == EngineZone.DECK)
                return null;
#endif
            // A scoped presentation flag on an automatic source the human was explicitly shown (pre-PLAY reveal) is public,
            // even while its historical zone is still DECK (Ohn'ahra playing Slice and Dice from the deck).
#if ENGINE_SOURCE
            bool scopedFlag = tag.Tag == GameTag.CAST_RANDOM_SPELLS && effectPlayScopes.Contains(tag.EntityId)
                || IsShatterPresentationIndex(tag);
#else
            bool scopedFlag = false;
#endif
            if (IsHiddenFromHuman(tag.EntityId, null) && !IsPublicHiddenTag(tag.Tag) && !scopedFlag)
                return null;
            GameTag nativeTag = tag.Tag;
            if (nativeTag == GameTag.SPELLPOWER
                && visibleTransportTags.TryGetValue(tag.EntityId, out var sent)
                && sent.TryGetValue((int)GameTag.CARDTYPE, out int type) && type == (int)CardType.SPELL)
                nativeTag = GameTag.CURRENT_SPELLPOWER;
            int value = tag.Value;
            if (tag.Tag == TemporaryCardTag || tag.Tag == GameTag.GHOSTLY)
            {
#if ENGINE_SOURCE
                // Hand removal clears gameplay Temporary before its frozen discard reveal/ZONE.
                // Keep the native flag through that exit: Card.GetBestNullZoneSpell selects spell 127.
                if (tag.Tag == TemporaryCardTag && tag.Value == 0
                    && historicalControllers.TryGetValue(tag.EntityId, out int owner) && owner == HumanPlayerId
                    && identityBatch != null && identityBatch.Skip(identityPosition + 1)
                        .TakeWhile(row => !(row is SabberStoneCore.Kettle.PowerHistoryTagChange move
                            && move.EntityId == tag.EntityId && move.Tag == GameTag.ZONE))
                        .OfType<PowerHistoryShowEntity>().Any(show => show.Entity.Id == tag.EntityId
                            && TagValue(show.Entity.Tags, SabberStoneCore.Actions.TemporaryRules.DiscardReveal) == 1))
                    temporaryDiscardVisuals.Add(tag.EntityId);
#endif
                historicalTemporaryVisuals.TryGetValue(tag.EntityId, out var visual);
                nativeTag = GameTag.GHOSTLY;
                value = Math.Max(value, tag.Tag == TemporaryCardTag ? visual.Ghostly : visual.Temporary);
                if (temporaryDiscardVisuals.Contains(tag.EntityId)) value = Math.Max(value, 1);
            }
            if (tag.Tag == GameTag.PREMIUM || (int)tag.Tag == 1932 || (int)tag.Tag == 2589)
            {
#if ENGINE_SOURCE
                int premium = EntityPremium(tag.EntityId, IdentityAt(identityPosition, tag.EntityId, true, true, definitionOnly: true)?.Name);
#else
                int premium = EntityPremium(tag.EntityId, Game.IdEntityDic.TryGetValue(tag.EntityId, out var premiumEntity)
                    ? premiumEntity.Card.Id : null);
#endif
                value = OwnedPremiumValue((int)tag.Tag, premium) ?? value;
            }
            if (tag.Tag == GameTag.ZONE && IsHiddenFromHuman(tag.EntityId, null))
                value = HiddenHistoryZone(tag.EntityId, value);
#if ENGINE_SOURCE
            // Entity-reference text: a stored entity the human's client may not name is sent as 0.
            if (tag.Tag == GameTag.TAG_SCRIPT_DATA_NUM_1 && value != 0
                && Game.IdEntityDic.TryGetValue(tag.EntityId, out var referenceHost))
            {
                // Enchantments whose engine tag 2 is a stat or dbfId (Feed Me! copies) project their reference entity instead.
                if (SabberStoneCore.Actions.PayloadRules.UsesReferenceTag(referenceHost.Card))
                    value = referenceHost[SabberStoneCore.Actions.PayloadRules.ReferenceEntity];
                value = ProjectLiveReference(Game, referenceHost.Card, value);
            }
#endif
            if ((tag.EntityId == Game.Player1.Id || tag.EntityId == Game.Player2.Id)
                && (tag.Tag == GameTag.RESOURCES_USED || tag.Tag == GameTag.OVERLOAD_LOCKED))
            {
                historicalMana.TryGetValue(tag.EntityId, out var mana);
                if (tag.Tag == GameTag.RESOURCES_USED) mana.Used = tag.Value;
                else mana.Locked = tag.Value;
                historicalMana[tag.EntityId] = mana;
                // Native available mana subtracts RESOURCES_USED only; engine keeps locks separate.
                if (tag.Tag == GameTag.RESOURCES_USED) value = mana.Used + mana.Locked;
            }
            return new PowerHistoryData
            {
                TagChange = new PegasusGame.PowerHistoryTagChange
                    { Entity = tag.EntityId, Tag = (int)(nativeTag == GameTag.DURABILITY && IsWeapon(tag.EntityId)
                        ? GameTag.HEALTH : nativeTag), Value = value }
            };
        }
        if (entry is PowerHistoryBlockStart start)
        {
#if ENGINE_SOURCE
            visualBlockSources.Push(start.Source);
            // The pre-PLAY reveal of an automatic play scopes its presentation flags to this block only.
            effectPlayScopes.Push(start is PowerHistoryEffectPlayStart && start.Source == scopedRevealSource ? start.Source : 0);
            scopedRevealSource = 0;
            revealedTriggerScopes.Push(start.BlockType == BlockType.TRIGGER && pendingTriggerReveals.Remove(start.Source));
#endif
            // A TRIGGER block's target is the card that fired it, not a creator (a Normal Antonidas stays Normal);
            // except an enchantment, whose deathrattle block names it as the target.
            premiumBlocks.Push(new KeyValuePair<int, int>(start.Source, start.BlockType != BlockType.TRIGGER
                || Game.IdEntityDic.TryGetValue(start.Target, out var blockTarget) && blockTarget is SabberStoneCore.Model.Entities.Enchantment
                ? start.Target : 0));
            var native = new PowerHistoryData
            {
                PowerStart = new PowerHistoryStart
                {
                    Type = (HistoryBlock.Type)(int)start.BlockType,
                    SubOption = start.Index,
                    Source = start.Source,
                    Target = start.Target,
                    EffectCardId = TransportCardId(start.Source, start.EffectCardId)
                }
            };
#if ENGINE_SOURCE
            if (start.EffectIndex.HasValue) native.PowerStart.EffectIndex = start.EffectIndex.Value;
            if (start.TriggerKeyword.HasValue) native.PowerStart.TriggerKeyword = start.TriggerKeyword.Value;
            // Automatic plays: PowerProcessor creates and holds the big card only when asked (no hand-play reveal).
            if (start is PowerHistoryEffectPlayStart effectPlay && effectPlay.ForceShowBigCard) native.PowerStart.ForceShowBigCard = true;
#endif
            return native;
        }
        if (entry is PowerHistoryBlockEnd)
        {
#if ENGINE_SOURCE
            if (visualBlockSources.Count > 0) visualBlockSources.Pop();
            if (revealedTriggerScopes.Count > 0) revealedTriggerScopes.Pop();
            if (effectPlayScopes.Count > 0) effectPlayScopes.Pop();
#endif
            if (premiumBlocks.Count > 0) premiumBlocks.Pop();
            return new PowerHistoryData { PowerEnd = new PowerHistoryEnd() };
        }
#if ENGINE_SOURCE
        if (entry is SabberStoneCore.Kettle.PowerHistorySubSpellStart subSpell)
        {
            bool Known(int id) => revealedEntities.Contains(id) && !IsHiddenFromHuman(id, null);
            bool publicOrigin = visualBlockSources.Count > 0 && Known(visualBlockSources.Peek());
            bool visible = (subSpell.Source == 0 ? publicOrigin : Known(subSpell.Source)) && subSpell.Targets.All(Known);
            int returnOwner = 0;
            if (subSpell.SpellPrefabGuid == SabberStoneCore.Actions.DrawReplacementRules.ReturnPrefab)
                visible = Known(subSpell.Source) && subSpell.Targets.Count == 1
                    && historicalControllers.TryGetValue(subSpell.Source, out returnOwner)
                    && historicalControllers.TryGetValue(subSpell.Targets[0], out int cardOwner) && cardOwner == returnOwner
                    && historicalZones.TryGetValue(subSpell.Targets[0], out var returnZone) && returnZone == EngineZone.SETASIDE;
            if (SabberStoneCore.Presentation.ShatterHistory.IsConceal(subSpell.SpellPrefabGuid)
                || SabberStoneCore.Presentation.ShatterHistory.IsSpawn(subSpell.SpellPrefabGuid))
                visible = IsShatterPresentation(subSpell);
            if (subSpell.RequirePublicOrigin) visible &= publicOrigin || revealedTriggerScopes.Count > 0 && revealedTriggerScopes.Peek();
            // The generic spawn uses the public destination hero and anonymous DECK FULLs, not its causal card.
            // Other zero-target recipes may infer their causal definition, so still require a public origin.
            if (subSpell.Targets.Count == 0 && subSpell.SpellPrefabGuid != SabberStoneCore.Kettle.PowerHistory.GeneratedShufflePrefab)
                visible &= publicOrigin;
            // SpawnToDeckSpell accepts anonymous DECK FULLs: INVALID definitions select Card_Hidden.
            // Keep the scope for both viewers; snapshot projection still owns identity privacy.
            visibleSubSpells.Push(visible);
            if (!visible) return null;
            var row = new PowerHistoryData { SubSpellStart = new PegasusGame.PowerHistorySubSpellStart {
                SpellPrefabGuid = subSpell.SpellPrefabGuid, SourceEntityId = subSpell.Source,
                TargetEntityIds = new List<int>(subSpell.Targets)
            } };
            if (returnOwner != 0 && returnOwner != HumanPlayerId) privateReturnOwners[row] = returnOwner;
            return row;
        }
        if (entry is SabberStoneCore.Kettle.PowerHistorySubSpellEnd)
        {
            if (visibleSubSpells.Count == 0) throw new InvalidOperationException("SUB_SPELL_END without SUB_SPELL_START.");
            return visibleSubSpells.Pop() ? new PowerHistoryData { SubSpellEnd = new PegasusGame.PowerHistorySubSpellEnd() } : null;
        }
        if (entry is SabberStoneCore.Kettle.PowerHistoryShuffleDeck shuffleDeck)
            return new PowerHistoryData { ShuffleDeck = new PegasusGame.PowerHistoryShuffleDeck { PlayerId = shuffleDeck.PlayerId } };
#endif
        if (entry is SabberStoneCore.Kettle.PowerHistoryMetaData meta)
            return new PowerHistoryData
            {
                MetaData = new PegasusGame.PowerHistoryMetaData
                {
                    Info = meta.Info == null ? new List<int>() : new List<int>(meta.Info),
                    Type = (HistoryMeta.Type)(int)meta.Type,
                    Data = meta.Data
                }
            };
        if (entry is PowerHistoryChangeEntity change)
        {
            EngineHistoryEntity entity = change.Entity;
            var tags = new Dictionary<GameTag, int>(entity.Tags);
            DeriveChangedPremium(entity.Id, change.CardId, premiumBlocks.Count > 0
                && (premiumBlocks.Peek().Key == entity.Id || premiumBlocks.Peek().Value == entity.Id));
            if (TagValue(tags, GameTag.CARDTYPE) == (int)CardType.MINION)
            {
                tags[GameTag.DAMAGE] = 0;
                tags[GameTag.NUM_ATTACKS_THIS_TURN] = 0;
                EngineCard definition = EngineCards.FromId(change.CardId)
                    ?? throw new InvalidOperationException("Changed minion definition is unavailable: " + change.CardId);
                foreach (GameTag reset in new[] {
                    GameTag.STEALTH, GameTag.TAUNT,
                    GameTag.CANT_BE_TARGETED_BY_SPELLS, GameTag.CANT_BE_TARGETED_BY_HERO_POWERS
                })
                {
                    if (reset == GameTag.TAUNT && tags.ContainsKey(reset)) continue;
                    tags[reset] = reset == GameTag.CANT_BE_TARGETED_BY_SPELLS
                        ? (definition.CantBeTargetedBySpells ? 1 : 0)
                        : reset == GameTag.CANT_BE_TARGETED_BY_HERO_POWERS
                            ? (definition.CantBeTargetedByHeroPowers ? 1 : 0)
                            : definition[reset];
                }
            }
            return new PowerHistoryData
            {
                ChangeEntity = ToNativeHistoryEntity(new EngineHistoryEntity
                {
                    Id = entity.Id,
                    Name = change.CardId,
                    Tags = tags
                }, completeSnapshot: false)
            };
        }
        throw new NotSupportedException("Unmapped power-history entry " + entry.GetType().FullName + ".");
    }

    private PegasusGame.PowerHistoryCreateGame ToNativeCreateGame(SabberStoneCore.Kettle.PowerHistoryCreateGame create, bool reset = false)
    {
        return new PegasusGame.PowerHistoryCreateGame
        {
            GameEntity = ToNativeEntity(create.Game),
            Players = create.Players.Select((player, index) =>
            {
                Controller controller = player.PlayerId == Game.Player1.PlayerId ? Game.Player1 : Game.Player2;
                NativeEntity entity = ToNativeEntity(player.PowerEntity);
                var mana = (Used: TagValue(player.PowerEntity.Tags, GameTag.RESOURCES_USED),
                    Locked: TagValue(player.PowerEntity.Tags, GameTag.OVERLOAD_LOCKED));
                historicalMana[entity.Id] = mana;
                entity.Tags.RemoveAll(tag => tag.Name == (int)GameTag.RESOURCES_USED);
                entity.Tags.Add(new NativeTag { Name = (int)GameTag.RESOURCES_USED, Value = mana.Used + mana.Locked });
                entity.Tags.RemoveAll(tag => tag.Name == CurrentSpellPowerBaseTag);
                entity.Tags.Add(new NativeTag
                    { Name = CurrentSpellPowerBaseTag, Value = reset ? TagValue(player.PowerEntity.Tags, (GameTag)CurrentSpellPowerBaseTag) : controller.CurrentSpellPower });
#if ENGINE_SOURCE
                // An omitted zero must clear a bonus earned during the discarded attempt.
                if (reset && !entity.Tags.Any(tag => tag.Name == 2333))
                    entity.Tags.Add(new NativeTag { Name = 2333, Value = 0 });
#endif
                lastSpellPower[controller.PlayerId] = entity.Tags.Last(tag => tag.Name == CurrentSpellPowerBaseTag).Value;
                if (player.PlayerId == HumanPlayerId && cosmeticPet != null) {
                    entity.Tags.Add(new NativeTag { Name = 4017, Value = cosmeticPetEntityId });
                    // CornerSpellReplacementManager reads the player's variant to create the pet's layout anchor.
                    entity.Tags.Add(new NativeTag { Name = 4037, Value = cosmeticPetVariant });
                }
                if (player.PlayerId == BotPlayerId && OpponentPetCard != null) {
                    entity.Tags.Add(new NativeTag { Name = 4017, Value = opponentPetEntityId });
                    // The opposing corner spell (TOP_RIGHT) reads the opponent's variant, like the friendly one reads ours.
                    entity.Tags.Add(new NativeTag { Name = 4037, Value = opponentPetVariant });
                }
                return new NativePlayer
                {
                    Id = player.PlayerId,
                    GameAccountId = new NativeBnetId { Hi = 0, Lo = (ulong)(index + 1) },
                    CardBack = player.PlayerId == HumanPlayerId && cosmeticCardBack.HasValue ? cosmeticCardBack.Value : npcPresentation?.CardBack(player.PlayerId, player.CardBack) ?? player.CardBack,
                    Entity = entity
                };
            }).ToList()
        };
    }

    // Presentation-only wire entity: never add it to engine stat/privacy caches or the array-backed engine entity registry.
    // Selected cosmetic settings are fixed presentation identities, independent of engine cards/history.
    private static PowerHistoryData NativePetEntity(int entityId, string card, int controller, int variant, int family,
        IEnumerable<KeyValuePair<int, int>> extra = null) => new PowerHistoryData {
        FullEntity = new PegasusGame.PowerHistoryEntity { Entity = entityId, Name = card,
            Tags = new List<NativeTag> {
                new NativeTag { Name = (int)GameTag.ENTITY_ID, Value = entityId },
                new NativeTag { Name = (int)GameTag.CONTROLLER, Value = controller },
                new NativeTag { Name = (int)GameTag.CARDTYPE, Value = 45 },
                new NativeTag { Name = (int)GameTag.ZONE, Value = 9 },
                new NativeTag { Name = 4079, Value = family },
                new NativeTag { Name = 4037, Value = variant }
            }.Concat((extra ?? Enumerable.Empty<KeyValuePair<int, int>>()).Select(pair => new NativeTag { Name = pair.Key, Value = pair.Value })).ToList() }
    };

    // The human pet's treat and XP counters as the client has seen them: retail starts PET_TREATS_GENERATED at 1, and a
    // rewind or reset reinjection must carry the current values so the client sees no change (and no duplicate XP effect).
    private IEnumerable<KeyValuePair<int, int>> HumanPetTreatTags()
    {
        yield return new KeyValuePair<int, int>(PetTreatsGeneratedTag, treatsGenerated);
        if (treatsFed > 0) yield return new KeyValuePair<int, int>(PetTreatsFedTag, treatsFed);
        if (petLikes != null)
            foreach (var shown in petLikes.ShownTags()) yield return shown;
    }
    internal const int PetTreatsFedTag = 4280, PetTreatsGeneratedTag = 4281;

    // Client packets 68 (GeneratePetTreat) and 67 (FeedPet) are empty; retail answers with standalone tag changes on the pet.
    // Requests outside a running match, without a pet or over the caps are ignored (the client keeps its own visuals).
    internal bool ReceiveNativePetTreat(int packetId)
    {
        if (cosmeticPet == null || Game == null || Game.State == State.COMPLETE) return false;
        var packet = new NativePowerHistory();
        if (packetId == 68)
        {
            if (treatsGenerated >= MaxPetTreatsPerGame) return false;
            treatsGenerated++;
            packet.List.Add(PetTagChange(PetTreatsGeneratedTag, treatsGenerated));
        }
        else if (packetId == 67)
        {
            if (treatsFed >= Math.Min(MaxPetTreatsPerGame, treatsGenerated)) return false;
            treatsFed++;
            packet.List.Add(PetTagChange(PetTreatsFedTag, treatsFed));
            // Retail pairs an accepted feed with an XP tick in the same list; the display gate decides whether it is shown yet.
            if (petLikes != null && !petLikesFaulted) packet.List.AddRange(petLikes.Treat(cosmeticPetEntityId));
        }
        else return false;
        SendPowerHistory(packet);
        return true;
    }
    // Every outgoing power-history list passes here: the one place that sees exactly what the client will apply.
    private void SendPowerHistory(NativePowerHistory packet)
    {
        if (packet.List.Count == 0) return;
#if ENGINE_SOURCE
        SuppressEmptyTriggers(packet);
        NormalizeBoardTransit(packet);
        NormalizePublicIdentity(packet);
        NormalizeBurnTransitions(packet);
        // PowerProcessor.BuildTaskList: END as a packet's last row falls out of its switch, enqueues the list twice and
        // replays the prefab. Blocks stay open across choices, so any recipe can reach this; zero SLUSH_TIME is inert.
        if (packet.List.Count > 0 && packet.List[packet.List.Count - 1].HasSubSpellEnd)
            packet.List.Add(new PowerHistoryData { MetaData = new PegasusGame.PowerHistoryMetaData {
                Type = HistoryMeta.Type.SLUSH_TIME, Data = 0 } });
#endif
        if (packet.List.Count > 0) send(19, packet);
    }

#if ENGINE_SOURCE
    // Engine/privacy caches retain engine IDs. Only the final wire boundary uses these aliases.
    // These four audited prefabs operate on anonymous Card_Hidden actors and never read card definitions.
    private readonly HashSet<int> publishedShatterActors = new HashSet<int>();

    private bool IsShatterPresentation(SabberStoneCore.Kettle.PowerHistorySubSpellStart spell)
    {
        bool conceal = SabberStoneCore.Presentation.ShatterHistory.IsConceal(spell.SpellPrefabGuid);
        bool spawn = SabberStoneCore.Presentation.ShatterHistory.IsSpawn(spell.SpellPrefabGuid);
        int count = spell.Targets.Count;
        if ((!conceal && !spawn) || count < 1 || count > 2 || spell.Targets.Distinct().Count() != count
            || spell.SpellPrefabGuid == SabberStoneCore.Presentation.ShatterHistory.Combine && count != 2
            || spell.SpellPrefabGuid == SabberStoneCore.Presentation.ShatterHistory.Separate && count != 1
            || spell.SpellPrefabGuid == SabberStoneCore.Presentation.ShatterHistory.CombinedSpawn && count != 1
            || !revealedEntities.Contains(spell.Source) || IsHiddenFromHuman(spell.Source, null)
            || !historicalControllers.TryGetValue(spell.Source, out int owner)
            || !historicalZones.TryGetValue(spell.Source, out var sourceZone) || sourceZone != EngineZone.PLAY
            || !visibleTransportTags.TryGetValue(spell.Source, out var sourceTags)
            || !sourceTags.TryGetValue((int)GameTag.CARDTYPE, out int sourceType) || sourceType != (int)CardType.HERO) return false;
        return spell.Targets.All(id => historicalControllers.TryGetValue(id, out int targetOwner) && owner == targetOwner
            && publishedShatterActors.Contains(id) && historicalZones.TryGetValue(id, out var zone)
            && zone == (conceal ? EngineZone.HAND : EngineZone.SETASIDE));
    }

    private bool IsShatterPresentationIndex(SabberStoneCore.Kettle.PowerHistoryTagChange tag)
    {
        if ((int)tag.Tag != SabberStoneCore.Presentation.ShatterHistory.IndexTag || identityBatch == null) return false;
        // Only the exact two setup/clear rows adjoining a validated combine pulse can cross privacy masking.
        for (int ordinal = 0; ordinal < 2; ordinal++)
        {
            int setup = identityPosition + 2 - ordinal;
            int clear = identityPosition - 2 - ordinal;
            bool Match(int at) => at >= 0 && at + 1 < identityBatch.Count
                && identityBatch[at] is SabberStoneCore.Kettle.PowerHistorySubSpellStart spell
                && spell.SpellPrefabGuid == SabberStoneCore.Presentation.ShatterHistory.Combine
                && identityBatch[at + 1] is SabberStoneCore.Kettle.PowerHistorySubSpellEnd
                && IsShatterPresentation(spell) && spell.Targets[ordinal] == tag.EntityId;
            if (tag.Value == ordinal && Match(setup) || tag.Value == 0 && Match(clear)) return true;
        }
        return false;
    }

    private readonly Dictionary<PowerHistoryData, int> privateReturnOwners = new Dictionary<PowerHistoryData, int>(WireEntryComparer.Instance);
    private readonly Dictionary<int, int> privateReturnIds = new Dictionary<int, int>();
    private readonly Dictionary<int, int> privateReturnOriginals = new Dictionary<int, int>();
    private readonly HashSet<int> retainedOverdrawEntities = new HashSet<int>();
    private readonly HashSet<int> publicReturnReferences = new HashSet<int>();
    private readonly Dictionary<int, int> retiredReturnActors = new Dictionary<int, int>();

    private int NewPrivateReturnId(int original, int owner)
    {
        int alias = nextDeckChoiceId--;
        if (alias <= Game.IdEntityDic.Values.Max(entity => entity.Id))
            throw new InvalidOperationException("Private return IDs exhausted.");
        int retired = WireReturnId(original);
        retiredReturnActors[retired] = owner;
        privateReturnOriginals.Remove(retired);
        privateReturnIds[original] = alias; privateReturnOriginals[alias] = original;
        return alias;
    }

    private static PowerHistoryData AnonymousReturnActor(int id, int owner) => new PowerHistoryData {
        FullEntity = new PegasusGame.PowerHistoryEntity {
            Entity = id, Name = "", Tags = new List<NativeTag> {
                new NativeTag { Name = (int)GameTag.ENTITY_ID, Value = id },
                new NativeTag { Name = (int)GameTag.CONTROLLER, Value = owner },
                new NativeTag { Name = (int)GameTag.ZONE, Value = (int)EngineZone.SETASIDE },
                new NativeTag { Name = (int)GameTag.ZONE_POSITION, Value = 0 }
            }
        }
    };
    internal int WireReturnId(int id) => privateReturnIds.TryGetValue(id, out int alias) ? alias : id;
    internal int EngineReturnId(int id) => privateReturnOriginals.TryGetValue(id, out int original) ? original : id;

    private static bool ReturnEntityTag(int tag) => tag == (int)GameTag.ENTITY_ID
        || tag == (int)GameTag.ATTACHED || tag == (int)GameTag.CREATOR || tag == (int)GameTag.DISPLAYED_CREATOR
        || tag == (int)GameTag.LINKED_ENTITY || tag == (int)GameTag.PARENT_CARD || tag == (int)GameTag.LAST_AFFECTED_BY
        || tag == (int)GameTag.CARD_TARGET || tag == (int)GameTag.WEAPON || tag == (int)GameTag.LAST_CARD_PLAYED
        || tag == (int)GameTag.LAST_CARD_DRAWN || tag == (int)GameTag.HISTORY_PROXY
        || tag == (int)GameTag.TAG_SCRIPT_DATA_ENT_1 || tag == (int)GameTag.TAG_SCRIPT_DATA_ENT_2
        || tag == (int)GameTag.PROPOSED_ATTACKER || tag == (int)GameTag.PROPOSED_DEFENDER
        || tag == (int)GameTag.DARK_GIFT_ENTITY || tag == (int)GameTag.HERO_ENTITY || tag == 4029;

    // A historical reference (e.g. LAST_CARD_DRAWN) must not become a link from a known burn to a private alias.
    private int WireReturnReference(int tag, int id) => tag != (int)GameTag.ENTITY_ID
        && retainedOverdrawEntities.Contains(id) && !publicReturnReferences.Contains(id) ? 0 : WireReturnId(id);

    private void RemapPrivateReturnPacket(IProtoBuf packet)
    {
        void Tags(IEnumerable<NativeTag> tags) {
            foreach (var tag in tags) if (ReturnEntityTag(tag.Name)) tag.Value = WireReturnReference(tag.Name, tag.Value);
        }
        void Snapshot(PegasusGame.PowerHistoryEntity entity) {
            if (entity == null) return;
            if (string.IsNullOrEmpty(entity.Name)) publicReturnReferences.Remove(entity.Entity);
            else publicReturnReferences.Add(entity.Entity);
            entity.Entity = WireReturnId(entity.Entity); Tags(entity.Tags);
        }
        if (packet is NativePowerHistory history)
        {
            var rows = new List<PowerHistoryData>();
            for (int index = 0; index < history.List.Count; index++)
            {
                var row = history.List[index];
                // Full replay starts a new client graph. Reset snapshots keep the existing anonymous identities.
                if (row.HasCreateGame) { privateReturnIds.Clear(); privateReturnOriginals.Clear(); retiredReturnActors.Clear(); publicReturnReferences.Clear(); }
                if (row.HasResetGame)
                {
                    // Reset snapshots are simultaneous; ordinary replay must retain historical visibility order.
                    publicReturnReferences.Clear();
                    for (int next = index + 1; next < history.List.Count && history.List[next].HasFullEntity; next++)
                    {
                        var snapshot = history.List[next].FullEntity;
                        if (!string.IsNullOrEmpty(snapshot.Name)) publicReturnReferences.Add(snapshot.Entity);
                    }
                }
                if ((row.HasResetGame || row.HasCreateGame) && privateReturnIds.Count == 0
                    && !history.List.Any(r => r.HasSubSpellStart && r.SubSpellStart.SpellPrefabGuid == SabberStoneCore.Actions.DrawReplacementRules.ReturnPrefab))
                {
                    // A reconnect can start with snapshots instead of replaying the return pulses.
                    retainedOverdrawEntities.UnionWith(Game.PowerHistory.Full.OfType<PowerHistoryRetainedOverdraw>().Select(burn => burn.Entity.Id));
                    foreach (int original in Game.PowerHistory.Full.OfType<SabberStoneCore.Kettle.PowerHistorySubSpellStart>()
                        .Where(s => s.SpellPrefabGuid == SabberStoneCore.Actions.DrawReplacementRules.ReturnPrefab
                            && Game.IdEntityDic.TryGetValue(s.Source, out var source) && source.Controller.PlayerId != HumanPlayerId)
                        .SelectMany(s => s.Targets).Distinct())
                        if (Game.IdEntityDic.ContainsKey(original)) NewPrivateReturnId(original, BotPlayerId);
                }
                if (privateReturnOwners.TryGetValue(row, out int owner))
                {
                    privateReturnOwners.Remove(row);
                    int original = row.SubSpellStart.TargetEntityIds[0];
                    int alias = NewPrivateReturnId(original, owner);
                    // Never move or reference the old actor here: that would identify the randomly selected burn.
                    rows.Add(AnonymousReturnActor(alias, owner));
                }
                Snapshot(row.FullEntity); Snapshot(row.ShowEntity); Snapshot(row.ChangeEntity);
                if (row.HasHideEntity) {
                    publicReturnReferences.Remove(row.HideEntity.Entity);
                    row.HideEntity.Entity = WireReturnId(row.HideEntity.Entity);
                }
                if (row.HasTagChange) {
                    if (row.TagChange.Tag == (int)GameTag.ZONE && (row.TagChange.Value == (int)EngineZone.SETASIDE
                        || row.TagChange.Value == (int)EngineZone.DECK || row.TagChange.Value == (int)EngineZone.HAND))
                        publicReturnReferences.Remove(row.TagChange.Entity);
                    row.TagChange.Entity = WireReturnId(row.TagChange.Entity);
                    if (ReturnEntityTag(row.TagChange.Tag)) row.TagChange.Value = WireReturnReference(row.TagChange.Tag, row.TagChange.Value);
                }
                if (row.HasPowerStart) {
                    row.PowerStart.Source = WireReturnId(row.PowerStart.Source);
                    row.PowerStart.Target = WireReturnId(row.PowerStart.Target);
                }
                if (row.HasSubSpellStart) {
                    row.SubSpellStart.SourceEntityId = WireReturnId(row.SubSpellStart.SourceEntityId);
                    row.SubSpellStart.TargetEntityIds = row.SubSpellStart.TargetEntityIds.Select(WireReturnId).ToList();
                }
                if (row.HasMetaData) {
                    row.MetaData.Info = row.MetaData.Info.Select(WireReturnId).ToList();
                    // TARGET.Data is a source entity; JOUST.Data is the winner entity (GameUtils.GetJoustWinner).
                    // DAMAGE/HEALING.Data are numeric amounts.
                    // Assigning Data marks the optional field present; write it only when an alias really replaces it.
                    if ((row.MetaData.Type == HistoryMeta.Type.TARGET || row.MetaData.Type == HistoryMeta.Type.JOUST)
                        && row.MetaData.HasData && WireReturnId(row.MetaData.Data) != row.MetaData.Data)
                        row.MetaData.Data = WireReturnId(row.MetaData.Data);
                }
                var create = row.CreateGame ?? row.ResetGame?.CreateGame;
                if (create != null) {
                    Tags(create.GameEntity.Tags);
                    foreach (var player in create.Players) Tags(player.Entity.Tags);
                }
                rows.Add(row);
                // Preserve the old, indistinguishable SETASIDE actors across reconstruction too.
                if (create != null)
                    foreach (var retired in retiredReturnActors) rows.Add(AnonymousReturnActor(retired.Key, retired.Value));
            }
            // Snapshot order must be wire-ID order, never engine-ID order with aliases substituted in place.
            for (int i = 0; i < rows.Count; i++)
                if (retiredReturnActors.Count > 0 && (rows[i].HasResetGame || rows[i].HasCreateGame))
                {
                    int end = i + 1;
                    while (end < rows.Count && rows[end].HasFullEntity) end++;
                    var snapshots = rows.Skip(i + 1).Take(end - i - 1).OrderBy(r => r.FullEntity.Entity).ToList();
                    foreach (var snapshot in snapshots)
                        if (string.IsNullOrEmpty(snapshot.FullEntity.Name)
                            && (snapshot.FullEntity.Tags.FirstOrDefault(t => t.Name == (int)GameTag.ZONE)?.Value ?? 0) == (int)EngineZone.SETASIDE)
                            snapshot.FullEntity.Tags = AnonymousReturnActor(snapshot.FullEntity.Entity,
                                (snapshot.FullEntity.Tags.FirstOrDefault(t => t.Name == (int)GameTag.CONTROLLER)?.Value ?? 0)).FullEntity.Tags;
                    rows.RemoveRange(i + 1, end - i - 1); rows.InsertRange(i + 1, snapshots);
                    int finish = end;
                    while (finish < rows.Count && (rows[finish].HasTagChange || rows[finish].HasShowEntity || rows[finish].HasChangeEntity)) finish++;
                    var corrections = rows.Skip(end).Take(finish - end)
                        .OrderBy(r => r.HasTagChange ? r.TagChange.Entity : (r.ShowEntity ?? r.ChangeEntity).Entity).ToList();
                    rows.RemoveRange(end, finish - end); rows.InsertRange(end, corrections);
                    i = finish - 1;
                }
            history.List = rows;
        }
        else if (packet is EntityChoices choices) {
            if (choices.HasSource) choices.Source = WireReturnId(choices.Source);
            choices.Entities = choices.Entities.Select(WireReturnId).ToList();
        }
        else if (packet is EntitiesChosen chosen)
            chosen.ChooseEntities.Entities = chosen.ChooseEntities.Entities.Select(WireReturnId).ToList();
        else if (packet is AllOptions options)
            foreach (var option in options.Options)
                foreach (var sub in new[] { option.MainOption }.Concat(option.SubOptions).Where(sub => sub != null)) {
                    sub.Id = WireReturnId(sub.Id);
                    foreach (var target in sub.Targets) target.Id = WireReturnId(target.Id);
                }
    }

    private void AppendRetainedOverdraw(NativePowerHistory packet, PowerHistoryRetainedOverdraw overdraw)
    {
        int display = nextDeckChoiceId--;
        if (display <= Game.IdEntityDic.Values.Max(entity => entity.Id))
            throw new InvalidOperationException("Overdraw display IDs exhausted.");
        var frozen = overdraw.Entity;
        retainedOverdrawEntities.Add(frozen.Id);
        // No retained ID, creator, payload, attachment or entity-valued tag reaches this public actor.
        var tags = frozen.Tags.Where(pair => pair.Key == GameTag.CONTROLLER || pair.Key == GameTag.CARDTYPE
            || pair.Key == GameTag.COST || pair.Key == GameTag.ATK || pair.Key == GameTag.HEALTH
            || pair.Key == GameTag.PREMIUM).ToDictionary(pair => pair.Key, pair => pair.Value);
        tags[GameTag.ZONE] = (int)EngineZone.DECK;
        tags[GameTag.REVEALED] = 0;
        tags[(GameTag)4722] = 319; // Card.MillCardWithTiming selects the hand actor's Godfrey burn spell.
        entityPremiums[display] = EntityPremium(frozen.Id, frozen.Name);
        var shown = ToNativeHistoryEntity(new EngineHistoryEntity { Id = display, Name = frozen.Name, Tags = tags }, true);
        void Add(PowerHistoryData row) { packet.List.Add(row); TrackVisibility(row); }
        Add(new PowerHistoryData { PowerStart = new PowerHistoryStart {
            Type = HistoryBlock.Type.TRIGGER, Source = overdraw.Player, EffectIndex = -1, SubOption = -1
        } });
        Add(new PowerHistoryData { FullEntity = new PegasusGame.PowerHistoryEntity {
            Entity = display, Name = "", Tags = new List<NativeTag> {
                // GameState.AddEntity indexes this tag, not FullEntity.Entity.
                new NativeTag { Name = (int)GameTag.ENTITY_ID, Value = display },
                new NativeTag { Name = (int)GameTag.CONTROLLER, Value = TagValue(tags, GameTag.CONTROLLER) },
                new NativeTag { Name = (int)GameTag.ZONE, Value = (int)EngineZone.DECK }
            }
        } });
        Add(new PowerHistoryData { ShowEntity = shown });
        Add(new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
            Entity = display, Tag = (int)GameTag.ZONE, Value = (int)EngineZone.GRAVEYARD
        } });
        Add(new PowerHistoryData { PowerEnd = new PowerHistoryEnd() });
        // Disposable public actor stays in GRAVEYARD. Moving it again waits on the native mill
        // flag, which MillCardWithTiming never clears; retained engine identity is already separate.
        revealedEntities.Remove(display); // Presentation-only ID has no engine state to refresh.
    }

    // Retail heads a printed trigger card's own activation with TriggerKeyword=TRIGGER_VISUAL: every non-keyword TRIGGER
    // from a PLAY entity whose card prints TRIGGER_VISUAL (docs/reports/2026-11-01-trigger-visual-keyword.md).
    // TriggerSpellController.GetActorTriggerSpellType(32) plays the actor's trigger bauble spell and sound, then any
    // CardDef trigger effect; keyword 0 plays only the CardDef slot, nothing at all for empty slots such as EDR_810t's.
    // Judged on the wire view at this row (identity and zone the client holds), so a hidden source is never classified.
    // Empty boundaries (EffectIndex -1 after SuppressEmptyTriggers) and engine keywords keep their headers. A Colossal
    // appendage summon is the Colossal power, not the printed trigger (retail TSC_962 TAG_NOT_SET,
    // data/rules-evidence/colossal/col-appendage-wire.yaml), so a block creating the source's own appendage stays unkeyworded.
    private void MarkTriggerVisual(List<PowerHistoryData> list, int index)
    {
        var start = list[index].PowerStart;
        if (start.Type != HistoryBlock.Type.TRIGGER || start.HasTriggerKeyword && start.TriggerKeyword != 0
            || !start.HasEffectIndex || start.EffectIndex < 0 || !wireEntities.TryGetValue(start.Source, out var source)
            || source.Zone != (int)EngineZone.PLAY || LookupCard(source.Card) is not { } card
            || card[GameTag.TRIGGER_VISUAL] != 1) return;
        var appendages = card.ColossalAppendageIds;
        if (appendages != null)
            for (int i = index + 1, depth = 1; i < list.Count && depth > 0; i++)
            {
                if (list[i].HasPowerStart) depth++;
                else if (list[i].HasPowerEnd) depth--;
                else if ((list[i].HasFullEntity ? list[i].FullEntity : list[i].HasShowEntity ? list[i].ShowEntity : null) is { } entity
                    && appendages.Contains(entity.Name)) return;
            }
        start.TriggerKeyword = (int)GameTag.TRIGGER_VISUAL;
    }

    private void SuppressEmptyTriggers(NativePowerHistory packet)
    {
        // Pruning must depend only on this viewer's wire rows, including identities already sent.
        var parents = new Dictionary<PowerHistoryData, PowerHistoryData>(WireEntryComparer.Instance);
        var taskParents = new HashSet<PowerHistoryData>(WireEntryComparer.Instance);
        var completeParents = new HashSet<PowerHistoryData>(WireEntryComparer.Instance);
        var blocks = new Stack<PowerHistoryData>();
        foreach (var frame in impactFrames) blocks.Push(null); // Incoming parents cannot be proved taskless here.
        foreach (var row in packet.List)
        {
            if (row.HasPowerStart)
            {
                parents[row] = blocks.Count > 0 ? blocks.Peek() : null;
                blocks.Push(row);
            }
            else if (row.HasPowerEnd)
            {
                if (blocks.Count > 0 && blocks.Pop() is { } parent) completeParents.Add(parent);
            }
            else if (blocks.Count > 0 && blocks.Peek() is { } parent) taskParents.Add(parent);
        }
        var empty = new List<int>();
        var identities = new Dictionary<int, string>();
        int depth = impactFrames.Count;
        for (int i = 0; i < packet.List.Count; i++)
        {
            var row = packet.List[i];
            if (row.HasPowerEnd) depth = Math.Max(0, depth - 1);
            if (row.HasPowerStart) depth++;
            var snapshot = row.HasFullEntity ? row.FullEntity : row.HasShowEntity ? row.ShowEntity : row.HasChangeEntity ? row.ChangeEntity : null;
            if (snapshot != null) identities[snapshot.Entity] = snapshot.Name;
            else if (row.HasHideEntity) identities[row.HideEntity.Entity] = "";
            if (!row.HasPowerStart || row.PowerStart.Type != HistoryBlock.Type.TRIGGER
                || i + 1 >= packet.List.Count || !packet.List[i + 1].HasPowerEnd) continue;
            var start = row.PowerStart;
            if ((start.HasTriggerKeyword && start.TriggerKeyword != (int)GameTag.DEATHRATTLE) || start.ShowInHistory) continue;
            // Lifecycle boundaries may contain only private display refreshes; keep them independent of hidden identity.
            if (Game != null && (start.Source == Game.Id || start.Source == Game.Player1.Id || start.Source == Game.Player2.Id)) continue;
            var card = LookupCard(identities.TryGetValue(start.Source, out string name) ? name
                : wireEntities.TryGetValue(start.Source, out var known) ? known.Card : null);
            if (card?.IsSecret == true) continue;
            // Empty task lists still load slot 0; retain required boundaries without playing an effect.
            start.EffectIndex = -1;
            start.EffectCardId = "";
            // Every engine Deathrattle activation keeps its keyword-217 boundary, as nested ones already do: pruning an
            // empty one (Ravenous Felhunter with no target) merged its source's next Deathrattle/Lifesteal attribution.
            if (start.TriggerKeyword == (int)GameTag.DEATHRATTLE) continue;
            // Only taskless, complete parents can lose a child without merging/exposing native task lists.
            if (depth > 1 && (parents[row] is not { } parent || !completeParents.Contains(parent) || taskParents.Contains(parent))) continue;
            empty.Add(i);
        }
        // Remove right-to-left so adjacent empty roots cannot collectively merge loose task segments.
        for (int i = empty.Count - 1; i >= 0; i--)
        {
            int at = empty[i];
            if (parents[packet.List[at]] == null && at > 0 && at + 2 < packet.List.Count
                && !packet.List[at - 1].HasPowerStart && !packet.List[at - 1].HasPowerEnd
                && !packet.List[at + 2].HasPowerStart && !packet.List[at + 2].HasPowerEnd) continue;
            packet.List.RemoveRange(at, 2);
        }
    }
#endif

    private string PresentedHero(int id, string cardId)
    {
        if (id == Game.Player1.Hero.Id && cardId == (cosmeticBaseHero ?? playerDeck?.Hero ?? playerHero?.Id))
            return DebugScenario?.HumanSkin ?? cosmeticHero ?? cardId;
        if (id == Game.Player2.Hero.Id && cardId == (DebugScenario?.NpcSkin != null ? HeroSkinData.All[DebugScenario.NpcSkin].BaseHero : opponentDeck?.Hero))
            return DebugScenario?.NpcSkin ?? npcPresentation?.HeroCardId(id, Game.Player2.Hero.Id, cardId) ?? cardId;
        return cardId;
    }

    private void CaptureCosmeticPowerCards()
    {
        cosmeticPowerCards.Clear();
        foreach (var player in new[] { Game.Player1, Game.Player2 })
        {
            string skin = PresentedHero(player.Hero.Id, player.Hero.Card.Id);
            if (skin == player.Hero.Card.Id || !HeroSkinData.All.TryGetValue(skin, out var definition)) continue;
            var basePower = EngineCards.FromAssetId(player.Hero.Card[GameTag.HERO_POWER]);
            if (basePower == null) continue;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (definition.Power.Length != 0) names[basePower.Id] = definition.Power;
            int upgrade = basePower[(GameTag)1086];
            if (upgrade != 0 && EngineCards.FromAssetId(upgrade) is { } upgraded && definition.UpgradedPower.Length != 0)
                names[upgraded.Id] = definition.UpgradedPower;
            cosmeticPowerCards[player.PlayerId] = names;
        }
    }

    private void AppendTransferredPowerIdentity(NativePowerHistory packet, int entityId)
    {
        if (!revealedEntities.Contains(entityId) || !visibleTransportTags.TryGetValue(entityId, out var tags)) return;
#if ENGINE_SOURCE
        string cardId = IdentityAt(identityPosition, entityId, currentFirst: false, allowCurrent: true, definitionOnly: true)?.Name;
#else
        string cardId = Game.IdEntityDic.TryGetValue(entityId, out var entity) ? entity.Card.Id : null;
#endif
        if (string.IsNullOrEmpty(cardId)) return;
        // Pinned OnShowEntity reloads CardID without replacing the actor or restarting state spells.
        var update = new PowerHistoryData { ShowEntity = new PegasusGame.PowerHistoryEntity {
            Entity = entityId, Name = TransportCardId(cardId),
            Tags = tags.Select(tag => new NativeTag { Name = tag.Key, Value = tag.Value }).ToList()
        } };
        packet.List.Add(update);
        TrackVisibility(update);
    }

    private void ObservePowerController(int entityId, int controller)
    {
        if (controller == 0) return;
        if (powerControllers.TryGetValue(entityId, out int previous) && previous != controller)
            transferredPowers.Add(entityId);
        powerControllers[entityId] = controller;
    }

    private string PresentedCardId(EngineHistoryEntity entity)
    {
        if (TagValue(entity.Tags, GameTag.CARDTYPE) == (int)CardType.HERO_POWER)
            ObservePowerController(entity.Id, TagValue(entity.Tags, GameTag.CONTROLLER));
        return PresentationCardId(entity.Id, entity.Name, TagValue(entity.Tags, GameTag.CONTROLLER));
    }

    // Side-effect free: the projection and the test-control audit share this one cosmetic mapping.
    internal string PresentationCardId(int id, string cardId, int controller)
    {
        string hero = PresentedHero(id, cardId);
        if (hero != cardId) return hero;
        // Never consult current HeroPower/hero/zone. Retained SETASIDE powers and abandoned-timeline
        // snapshots carry their own identity and owner; arbitrary replacement powers have no mapping.
        if (!transferredPowers.Contains(id) && cardId != null && cosmeticPowerCards.TryGetValue(controller, out var names)
            && names.TryGetValue(cardId, out string power)) return power;
        return TransportCardId(id, cardId);
    }

    private int? SkinTag(int id, string cardId, int tag)
    {
        if (!HeroSkinData.Tags.Contains(tag)) return null;
        string skin = PresentedHero(id, cardId);
        return skin != cardId && HeroSkinData.All.TryGetValue(skin, out var definition) ? definition.Tag(tag) : (int?)null;
    }

    private PowerHistoryData PetTagChange(int tag, int value) => new PowerHistoryData {
        TagChange = new PegasusGame.PowerHistoryTagChange { Entity = cosmeticPetEntityId, Tag = tag, Value = value } };

    private NativeEntity ToNativeEntity(PowerEntity entity)
    {
        return new NativeEntity { Id = entity.Id, Tags = ToNativeTags(entity.Tags) };
    }

    private PegasusGame.PowerHistoryEntity ToNativeHistoryEntity(EngineHistoryEntity entity, bool revealToHuman = false, bool completeSnapshot = true,
        IReadOnlyDictionary<int, EngineHistoryEntity> resetEntities = null, bool currentHealthSnapshot = false)
    {
#if ENGINE_SOURCE
        entity = SabberStoneCore.Actions.DiscoverRealRules.ProjectOfferForNative(entity);
#endif
        // Callers supply an entry snapshot, IdentityAt result, checkpoint, or endpoint-only snapshot.
        // All definition-derived naming/privacy/printed metadata here use that supplied name.
        string name = PresentedCardId(entity);
        // Secret privacy follows the snapshot's own card, not the entity's current (possibly transformed) definition.
        var privacyTags = entity.Tags;
        if (!revealToHuman && privacyTags != null && !privacyTags.ContainsKey(GameTag.SECRET) && !string.IsNullOrEmpty(entity.Name))
            privacyTags = new Dictionary<GameTag, int>(privacyTags) { [GameTag.SECRET] = DefinitionTag(entity.Name, GameTag.SECRET, 0) };
        bool hidden = !revealToHuman && IsHiddenFromHuman(entity.Id, privacyTags, resetEntities);
        if (name.Length > 0 && hidden)
            name = "";
        IEnumerable<KeyValuePair<GameTag, int>> outboundTags = entity.Tags;
        if (entity.Tags != null && entity.Tags.ContainsKey(GameTag.ENTITY_ID))
            outboundTags = entity.Tags.Select(pair => pair.Key == GameTag.ENTITY_ID
                ? new KeyValuePair<GameTag, int>(pair.Key, entity.Id) : pair);
        var tags = ToNativeTags(hidden && outboundTags != null
            ? outboundTags.Where(pair => IsPublicHiddenTag(pair.Key)) : outboundTags);
        if (!hidden && (!completeSnapshot || temporaryDiscardVisuals.Contains(entity.Id)))
        {
            historicalTemporaryVisuals.TryGetValue(entity.Id, out var visual);
            int ghostly = temporaryDiscardVisuals.Contains(entity.Id) ? 1 : Math.Max(visual.Temporary, visual.Ghostly);
            // CHANGE carries definition deltas; retained instance state comes from preceding history.
            if (ghostly > 0)
            {
                tags.RemoveAll(tag => tag.Name == (int)GameTag.GHOSTLY);
                tags.Add(new NativeTag { Name = (int)GameTag.GHOSTLY, Value = ghostly });
            }
        }
#if ENGINE_SOURCE
        if (hidden && startOfGameSources.Contains(entity.Id) && TagValue(entity.Tags, GameTag.ZONE) == (int)EngineZone.DECK)
            tags.RemoveAll(tag => tag.Name == (int)GameTag.ZONE_POSITION);
#endif
        // Anonymous attachments are not board actors; reveal their real zone with their public target.
        if (hidden)
            foreach (var tag in tags.Where(tag => tag.Name == (int)GameTag.ZONE))
                tag.Value = HiddenHistoryZone(entity.Id, tag.Value);
        if (!hidden)
            foreach (int tag in HeroSkinData.Tags)
                if (SkinTag(entity.Id, entity.Name, tag) is int value)
                {
                    tags.RemoveAll(item => item.Name == tag);
                    tags.Add(new NativeTag { Name = tag, Value = value });
                }
        // Only complete snapshots imply Mana when omitted; CHANGE_ENTITY carries definition deltas.
        if (completeSnapshot && !hidden && name.Length > 0 && !tags.Any(tag => tag.Name == 2837)
            && visibleTransportTags.TryGetValue(entity.Id, out var previousTags)
            && previousTags.TryGetValue(2837, out int alternate) && alternate != 0)
            tags.Add(new NativeTag { Name = 2837, Value = 0 });
        EngineCard raceDefinition = null;
        if (!hidden && !string.IsNullOrEmpty(entity.Name))
        {
            // Definition metadata is optional for snapshot names absent from the engine registry.
            try { raceDefinition = EngineCards.FromId(entity.Name); }
            catch (KeyNotFoundException) { }
        }
        if (raceDefinition != null)
        {
#if ENGINE_SOURCE
            if (SabberStoneCore.Actions.LeylineRules.IsLeyline(raceDefinition)) leylineTextEntities.Add(entity.Id);
            else if (leylineTextEntities.Remove(entity.Id))
                foreach (GameTag text in new[] { GameTag.TAG_SCRIPT_DATA_NUM_1, GameTag.TAG_SCRIPT_DATA_NUM_2 })
                {
                    tags.RemoveAll(tag => tag.Name == (int)text);
                    tags.Add(new NativeTag { Name = (int)text, Value = raceDefinition[text] });
                }
            // Captured values/defaults only: old snapshots must never read today's entity getters.
            visibleTransportTags.TryGetValue(entity.Id, out var previousTextTags);
            int TextValue(GameTag tag) => entity.Tags != null && entity.Tags.TryGetValue(tag, out int value)
                ? value : raceDefinition[tag];
            // FullEntity captures current Health; ShowEntity captures maximum HEALTH plus DAMAGE.
            int number = raceDefinition.Id == "BE_850t"
                ? TextValue(GameTag.HEALTH) - (currentHealthSnapshot ? 0 : TextValue(GameTag.DAMAGE))
                // An enchantment whose engine tag 2 means something else carries its reference entity separately; without one it
                // names nothing (never its dbfId or stat, which the client would resolve as some unrelated entity id).
                : entity.Tags != null && entity.Tags.TryGetValue(SabberStoneCore.Actions.PayloadRules.ReferenceEntity, out int referenced) && referenced != 0
                    ? referenced
                    : SabberStoneCore.Actions.PayloadRules.UsesReferenceTag(raceDefinition) ? 0 : TextValue(GameTag.TAG_SCRIPT_DATA_NUM_1);
            int number2 = raceDefinition.Id == "BE_850t" ? TextValue(GameTag.ATK) : TextValue(GameTag.TAG_SCRIPT_DATA_NUM_2);
            int alternateText = AlternateCardText(raceDefinition.Id, (EngineZone)TextValue(GameTag.ZONE),
                number, number2, TextValue((GameTag)955));
            bool formattedText = raceDefinition.Text?.Contains("@") == true
                || System.Text.RegularExpressions.Regex.IsMatch(raceDefinition.Text ?? "", @"\{[0-9]+\}");
            // Entity-reference text (Reference*Entity builders): a stored entity this viewer cannot name is sent as 0.
            number = resetEntities != null ? ProjectResetReference(raceDefinition, number, resetEntities) : ProjectLiveReference(Game, raceDefinition, number);
            foreach (GameTag textTag in CardTextTags)
            {
                int value = textTag == GameTag.TAG_SCRIPT_DATA_NUM_1 ? number
                    : textTag == GameTag.TAG_SCRIPT_DATA_NUM_2 ? number2
                    : (int)textTag == 955 ? alternateText : TextValue(textTag);
                // Indexed placeholders include composed text (e.g. Recruit stats), not only script numbers.
                if (formattedText || value != 0
                    || tags.Any(tag => tag.Name == (int)textTag)
                    || (previousTextTags != null && previousTextTags.TryGetValue((int)textTag, out int oldText) && oldText != 0))
                {
                    tags.RemoveAll(tag => tag.Name == (int)textTag);
                    tags.Add(new NativeTag { Name = (int)textTag, Value = value });
                }
            }
            // Tag 2 is the spell's entity reference (set above); the related-card panel keeps the dbfId.
            if (raceDefinition.Id == "BE_055t")
            {
                int related = number != 0 && Game.IdEntityDic.TryGetValue(number, out var spellEntity) ? spellEntity.Card.AssetId : 0;
                foreach (int tag in new[] { 1078, 1452 })
                {
                    tags.RemoveAll(value => value.Name == tag);
                    tags.Add(new NativeTag { Name = tag, Value = related });
                }
            }
            tags.RemoveAll(tag => tag.Name == (int)GameTag.CARDRACE);
            tags.Add(new NativeTag { Name = (int)GameTag.CARDRACE, Value = TextValue(GameTag.CARDRACE) });
#endif
            if (raceDefinition.Type == CardType.SPELL)
            {
                var local = tags.FirstOrDefault(tag => tag.Name == (int)GameTag.SPELLPOWER);
                if (local != null)
                {
                    tags.RemoveAll(tag => tag.Name == (int)GameTag.CURRENT_SPELLPOWER);
                    local.Name = (int)GameTag.CURRENT_SPELLPOWER;
                }
            }
            visibleTransportTags.TryGetValue(entity.Id, out var previousRaceTags);
            foreach (GameTag printedTag in PrintedDefinitionTags)
            {
                // Frozen instance values (including zero) win; never read today's entity while replaying history.
                if (tags.Any(tag => tag.Name == (int)printedTag)) continue;
                if (raceDefinition.Type == CardType.SPELL && printedTag == GameTag.SPELLPOWER) continue;
                int value = raceDefinition[printedTag];
                bool printed = value != 0;
                // Targeting overrides are frozen in the engine snapshot, including Silence's zero.
                if ((int)printedTag == 1211
                    && (raceDefinition.Type == CardType.MINION || raceDefinition.Type == CardType.HERO))
                    value = entity.Tags.TryGetValue(GameTag.CANT_BE_TARGETED_BY_SPELLS, out int targeting)
                        ? (targeting > 0 ? 1 : 0) : (raceDefinition.CantBeTargetedBySpells ? 1 : 0);
                if (value != 0 || printed || (previousRaceTags != null
                    && previousRaceTags.TryGetValue((int)printedTag, out int previous) && previous != 0))
                    tags.Add(new NativeTag { Name = (int)printedTag, Value = value });
            }
#if ENGINE_SOURCE
            foreach (GameTag raceTag in EngineCard.AdditionalRaceTags.Keys)
            {
                int value = TextValue(raceTag);
                // Native snapshots merge: explicitly clear flags from the previous identity.
                if (value != 0 || tags.Any(tag => tag.Name == (int)raceTag)
                    || (previousRaceTags != null && previousRaceTags.TryGetValue((int)raceTag, out int previous) && previous != 0))
                {
                    tags.RemoveAll(tag => tag.Name == (int)raceTag);
                    tags.Add(new NativeTag { Name = (int)raceTag, Value = value });
                }
            }
#endif
        }
#if STANDARD_PROOF
        if (!hidden)
        {
            var required = StandardProofNative.RequiredLiveSnapshot(name, entity.Tags);
            if (required != null)
                foreach (var pair in required)
                    if (!tags.Any(tag => tag.Name == pair.Key)) tags.Add(new NativeTag { Name = pair.Key, Value = pair.Value });
        }
#endif
        if (!hidden && entity.Tags != null && entity.Tags.TryGetValue(GameTag.DURABILITY, out int durability)
            && (raceDefinition?.Type == CardType.WEAPON || !tags.Any(tag => tag.Name == (int)GameTag.HEALTH)))
        {
            // Native HEALTH aliases DURABILITY, including its zero reset when a weapon changes type.
            tags.RemoveAll(tag => tag.Name == (int)GameTag.HEALTH);
            tags.Add(new NativeTag { Name = (int)GameTag.HEALTH, Value = durability });
        }
        // Premium is presentation owned by the original deck entity ID (and the player's starting hero).
        int premium = hidden || string.IsNullOrEmpty(name) ? 0 : EntityPremium(entity.Id, entity.Name);
        // Native renders Diamond/Signature only with the matching quality flag; the other flag is cleared.
        if (premium != 0)
            foreach (int premiumTag in new[] { (int)GameTag.PREMIUM, 1932, 2589 })
            {
                tags.RemoveAll(tag => tag.Name == premiumTag);
                tags.Add(new NativeTag { Name = premiumTag, Value = OwnedPremiumValue(premiumTag, premium).Value });
            }
        return new PegasusGame.PowerHistoryEntity
        {
            Entity = entity.Id,
            Name = name,
            // Missing Jade sizes share native art, but their printed base must not become a buff.
            DefTags = raceDefinition != null && name.Length > 0
                && raceDefinition.Id.StartsWith("CFM_712_t", StringComparison.Ordinal)
                ? new[] { GameTag.ATK, GameTag.HEALTH, GameTag.COST }.Select(tag =>
                    new NativeTag { Name = (int)tag, Value = raceDefinition[tag] }).ToList()
                : new List<NativeTag>(),
            Tags = tags.OrderBy(tag => tag.Name).ToList()
        };
    }

    internal static void ValidateHistoryEntityIdentity(PegasusGame.PowerHistoryEntity entity)
    {
        if (entity == null) return;
        foreach (NativeTag tag in entity.Tags.Where(tag => tag.Name == (int)GameTag.ENTITY_ID))
            if (tag.Value != entity.Entity)
                throw new InvalidOperationException("Native history identity mismatch: outer="
                    + entity.Entity + " embedded=" + tag.Value + ".");
    }

    private bool IsWeapon(int id) => Game.IdEntityDic.TryGetValue(id, out var playable)
        && playable is SabberStoneCore.Model.Entities.Weapon;

#if ENGINE_SOURCE
    private void RefreshPrivacyTargets()
    {
        // A new attachment's first FULL is its birth identity; it has no earlier actor to leak.
        // Existing actors must use the identity at this position, even if a later transform is a gift.
        EngineHistoryEntity Identity(int id) => IdentityAt(identityPosition, id, true, true, definitionOnly: true)
            ?? (!historicalZones.ContainsKey(id) ? identityBatch?.OfType<PowerHistoryFullEntity>()
                .FirstOrDefault(full => full.Entity.Id == id)?.Entity : null);
        void Link(int id, int target)
        {
            if (id == target) return;
            if (!privacyTargets.TryGetValue(id, out var targets))
                privacyTargets[id] = targets = new HashSet<int>();
            targets.Add(target);
        }
        void Gift(int spellId, int target)
        {
            if (!Game.IdEntityDic.TryGetValue(spellId, out var spell)) return;
            var identity = Identity(spellId);
            if (LookupCard(identity?.Name)?.Type != CardType.SPELL
                || !DarkGifts.Registry.Any(gift => gift.SpellId == identity.Name)) return;
            Link(spellId, target);
            int creator = spell[GameTag.CREATOR];
            if (Identity(creator)?.Name == "EDR_102t")
                Link(creator, target);
        }
        foreach (var entity in Game.IdEntityDic.Values)
        {
            if (entity[SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag] != 0 && entity[GameTag.PARENT_CARD] > 0)
                Link(entity.Id, entity[GameTag.PARENT_CARD]);
            // Hidden generated Choose One cards must not reveal their identity through branch actors.
            int parent = entity[GameTag.PARENT_CARD];
            if (parent > 0 && Game.IdEntityDic.ContainsKey(parent)
                && Game.IdEntityDic[parent].ChooseOnePlayables?.Any(option => option.Id == entity.Id) == true)
                Link(entity.Id, parent);
            int spell = entity[GameTag.DARK_GIFT_ENTITY];
            if (spell > 0) Gift(spell, entity.Id);
            if (!(entity is SabberStoneCore.Model.Entities.Enchantment enchant)) continue;
            int cardStateTarget = enchant[SabberStoneCore.Actions.HandProgressRules.PrivateAttachmentTarget];
            if (cardStateTarget != 0) Link(enchant.Id, cardStateTarget);
            if (enchant.Target != null && SabberStoneCore.Actions.CardStateBoundary.HasPrivateAttachments(enchant.Target))
                Link(enchant.Id, enchant.Target.Id);
            bool gift = enchant[GameTag.IS_NIGHTMARE_BONUS] == 1;
            Link(enchant.Id, enchant.Target.Id);
            if (gift) Gift(enchant.Creator.Id, enchant.Target.Id);
        }
        // Retain edges after links/attachments clear: orphaned SETASIDE spells must not become public.
    }

    private void AppendTemporaryHelperClears(NativePowerHistory packet, PowerHistoryData update)
    {
        if (!update.HasHideEntity || !Game.IdEntityDic.TryGetValue(update.HideEntity.Entity, out var actor)
            || IdentityAt(identityPosition, actor.Id, true, true, definitionOnly: true)?.Name != "GBL_999e" || !clientTransportTags.TryGetValue(actor.Id, out var tags)) return;
        // HIDE retains native attachment/creator fields, including after an earlier visibility-cache hide.
        foreach (var tag in tags.Where(pair => pair.Value != 0 && !IsPublicHiddenTag((GameTag)pair.Key)).ToArray())
        {
            var clear = new PowerHistoryData { TagChange = new PegasusGame.PowerHistoryTagChange {
                Entity = actor.Id, Tag = tag.Key, Value = 0
            } };
            packet.List.Add(clear);
            TrackVisibility(clear);
        }
    }

    private void AppendPrivateAttachments(NativePowerHistory packet, int publicTarget = 0)
    {
        foreach (int id in Game.Player1.Choice?.Choices ?? new List<int>())
        {
            var offer = Game.IdEntityDic[id];
            if (offer.Zone?.Type != EngineZone.SETASIDE || offer[SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag] == 0
                || revealedEntities.Contains(id) || IsHiddenFromHuman(id, null)) continue;
            var offered = IdentityAt(identityPosition, id, currentFirst: true, allowCurrent: true);
            if (offered == null) continue;
            var reveal = new PowerHistoryData { ShowEntity = ToNativeHistoryEntity(offered) };
            packet.List.Add(reveal);
            TrackVisibility(reveal);
        }
        foreach (var pair in privacyTargets.OrderBy(p => p.Key))
        {
            var entity = Game.IdEntityDic[pair.Key];
            bool attached = entity is SabberStoneCore.Model.Entities.Enchantment actor
                && (actor.Target.AppliedEnchantments?.Contains(actor) == true
                    || pair.Value.Contains(publicTarget) && historicalZones.TryGetValue(actor.Id, out var zone)
                        && zone == EngineZone.PLAY);
            bool choiceOption = entity[SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag] != 0 && entity[GameTag.PARENT_CARD] > 0;
            bool choice = entity[GameTag.PARENT_CARD] > 0 && Game.IdEntityDic.ContainsKey(entity[GameTag.PARENT_CARD])
                && Game.IdEntityDic[entity[GameTag.PARENT_CARD]].ChooseOnePlayables?.Any(option => option.Id == entity.Id) == true;
            var identity = IdentityAt(identityPosition, entity.Id, currentFirst: true, allowCurrent: true, definitionOnly: true);
            if (identity == null) continue;
            if (identity.Name != "EDR_102t" && entity[GameTag.IS_NIGHTMARE_BONUS] != 1 && !attached && !choiceOption && !choice) continue;
            bool hidden = !pair.Value.Contains(publicTarget) && IsHiddenFromHuman(entity.Id, null);
            PowerHistoryData update;
            if (hidden && revealedEntities.Contains(entity.Id))
                update = new PowerHistoryData { HideEntity = new PowerHistoryHide { Entity = entity.Id, Zone = HiddenHistoryZone(entity.Id, entity[GameTag.ZONE]) } };
            else if (!hidden && !revealedEntities.Contains(entity.Id)
                && (!(entity is SabberStoneCore.Model.Entities.Enchantment) || attached || entity[GameTag.ATTACHED] != 0))
            {
                identity = IdentityAt(identityPosition, entity.Id, currentFirst: true, allowCurrent: true);
                var snapshot = new EngineHistoryEntity { Id = entity.Id, Name = identity.Name,
                    Tags = new Dictionary<GameTag, int>(identity.Tags) };
                // A whole spell may already have resolved; replay the attachment's pre-removal zone.
                if (entity is SabberStoneCore.Model.Entities.Enchantment && pair.Value.Contains(publicTarget)
                    && historicalZones.TryGetValue(entity.Id, out var beforeZone))
                    snapshot.Tags[GameTag.ZONE] = (int)beforeZone;
                update = new PowerHistoryData { ShowEntity = ToNativeHistoryEntity(snapshot, true) };
            }
            else continue;
            AppendTemporaryHelperClears(packet, update);
            packet.List.Add(update);
            TrackVisibility(update);
        }
    }
#endif

    private bool IsHiddenFromHuman(int id, IDictionary<GameTag, int> tags,
        IReadOnlyDictionary<int, EngineHistoryEntity> resetEntities = null)
    {
        if (resetEntities != null && tags == null)
        {
            if (!resetEntities.TryGetValue(id, out var snapshot)) return true;
            tags = snapshot.Tags;
        }
#if ENGINE_SOURCE
        if (privacyTargets.TryGetValue(id, out var targets))
        {
            var visited = new HashSet<int> { id };
            var pending = new Stack<int>(targets);
            while (pending.Count > 0)
            {
                int target = pending.Pop();
                if (!visited.Add(target)) continue;
                if (privacyTargets.TryGetValue(target, out var linked))
                {
                    foreach (int next in linked) pending.Push(next);
                }
                else if (privateDeckOffers.Contains(target) || !IsHiddenFromHuman(target, null, resetEntities)) return false;
            }
            // Cycles without a public terminal target stay private.
            return true;
        }
#endif
        int controller = 0;
        EngineZone zone;
        bool hasController = tags != null && tags.TryGetValue(GameTag.CONTROLLER, out controller);
        int zoneValue = 0;
        bool hasZone = tags != null && tags.TryGetValue(GameTag.ZONE, out zoneValue);
        zone = hasZone ? (EngineZone)zoneValue : EngineZone.INVALID;
        if (!hasController)
            hasController = historicalControllers.TryGetValue(id, out controller);
        if (!hasZone)
            hasZone = historicalZones.TryGetValue(id, out zone);
#if ENGINE_SOURCE
        // SETASIDE has no public identity of its own; retain this observer's baseline disclosure.
        if (resetEntities != null && zone == EngineZone.SETASIDE && rewindAnonymousEntities?.Contains(id) == true) return true;
        // The immutable grant/mark batch owns creation visibility, even if discard happened before flush.
        if ((zone == EngineZone.INVALID || zone == EngineZone.SETASIDE)
            && temporaryHandOrigins.TryGetValue(id, out int origin) && origin == BotPlayerId) return true;
#endif
        if (resetEntities == null && (!hasController || !hasZone || zone == EngineZone.INVALID) && Game.IdEntityDic.ContainsKey(id))
        {
            IPlayable entity = Game.IdEntityDic[id];
            if (!hasController)
                controller = entity.Controller?.PlayerId ?? 0;
            // Fresh copies emit INVALID before entering their destination zone.
            if (!hasZone || zone == EngineZone.INVALID)
                zone = entity.Zone?.Type ?? EngineZone.INVALID;
        }
#if ENGINE_SOURCE
        int stored = resetEntities != null ? TagValue(tags, SabberStoneCore.Actions.PayloadRules.Stored)
            : Game.IdEntityDic.TryGetValue(id, out var payload) ? payload[SabberStoneCore.Actions.PayloadRules.Stored] : 0;
        // A stored payload entity is private unless a declared entity reference lets this viewer name it
        // (Bashana's carved spell is visible to its owner only; see PayloadRules.SharedWith).
        if (stored != 0)
        {
            if (resetEntities == null)
                return !Game.IdEntityDic.TryGetValue(id, out var storedEntity)
                    || !SabberStoneCore.Actions.PayloadRules.SharedWith(storedEntity, HumanPlayerId);
            // Rewind reset: the same rule, read from the restored snapshots.
            int policy = TagValue(tags, SabberStoneCore.Actions.PayloadRules.Policy);
            int hostZone = resetEntities.TryGetValue(TagValue(tags, SabberStoneCore.Actions.PayloadRules.Owner), out var hostSnapshot)
                ? TagValue(hostSnapshot.Tags, GameTag.ZONE) : 0;
            // An enchantment host is as public as the object it is attached to (PayloadRules.SharedWith).
            int publicZone = hostSnapshot != null && TagValue(hostSnapshot.Tags, GameTag.CARDTYPE) == (int)CardType.ENCHANTMENT
                && resetEntities.TryGetValue(TagValue(hostSnapshot.Tags, GameTag.ATTACHED), out var attachedSnapshot)
                ? TagValue(attachedSnapshot.Tags, GameTag.ZONE) : hostZone;
            bool all = (policy & (int)SabberStoneCore.Actions.PayloadPolicy.ShowAll) != 0 && publicZone == (int)EngineZone.PLAY;
            bool owner = (policy & (int)SabberStoneCore.Actions.PayloadPolicy.ShowOwner) != 0
                && hostZone != (int)EngineZone.DECK && hostZone != (int)EngineZone.SETASIDE
                && hostZone != (int)EngineZone.GRAVEYARD && hostZone != (int)EngineZone.REMOVEDFROMGAME
                && TagValue(tags, GameTag.CONTROLLER) == HumanPlayerId;
            return !(all || owner);
        }
        int replacedZone = resetEntities != null ? TagValue(tags, SabberStoneCore.Actions.CardStateBoundary.ReplacedHiddenZone)
            : Game.IdEntityDic.TryGetValue(id, out var replaced) ? replaced[SabberStoneCore.Actions.CardStateBoundary.ReplacedHiddenZone] : 0;
        if (zone == EngineZone.SETASIDE && (replacedZone == (int)EngineZone.DECK
            || replacedZone == (int)EngineZone.HAND && controller == BotPlayerId)) return true;
        if (zone == EngineZone.SETASIDE && controller == BotPlayerId
            && (privateChoiceOffers.Contains(id)
                || TagValue(tags, SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag) == BotPlayerId)) return true;
        int privateOffer = resetEntities != null ? TagValue(tags, SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag)
            : Game.IdEntityDic.TryGetValue(id, out var offered) ? offered[SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag] : 0;
        if (privateOffer == BotPlayerId && zone == EngineZone.SETASIDE) return true;
        if (privateOffer == HumanPlayerId && (zone == EngineZone.SETASIDE || zone == EngineZone.INVALID))
            for (var next = Game.Player1.Choice?.NextChoice; next != null; next = next.NextChoice)
                if (next.Choices.Contains(id)) return true;
        int privateHandOwner = resetEntities != null ? TagValue(tags, SabberStoneCore.Actions.ShatterRules.PrivateHandOwner)
            : Game.IdEntityDic.TryGetValue(id, out var shatter) ? shatter[SabberStoneCore.Actions.ShatterRules.PrivateHandOwner] : 0;
        if (privateHandOwner == BotPlayerId &&
            (zone == EngineZone.INVALID || zone == EngineZone.SETASIDE || zone == EngineZone.REMOVEDFROMGAME)) return true;
#endif
        if (zone == EngineZone.DECK || controller == BotPlayerId && zone == EngineZone.HAND) return true;
        if (controller != BotPlayerId || zone != EngineZone.SECRET) return false;
        // Resolve a definition only for Secret-zone policy; other zones need no identity lookup.
        if (tags != null && tags.TryGetValue(GameTag.SECRET, out int secret)) return secret == 1;
#if ENGINE_SOURCE
        // Reset FULLs use ordinary tag families; linked privacy checks need the frozen definition, never the replay's entity.
        if (resetEntities != null)
            return !resetEntities.TryGetValue(id, out var resetSecret) || (LookupCard(resetSecret.Name)?.IsSecret ?? true);
        return LookupCard(IdentityAt(identityPosition, id, true, true, definitionOnly: true)?.Name)?.IsSecret ?? true;
#else
        return Game.IdEntityDic.TryGetValue(id, out var secretEntity) && secretEntity.Card.IsSecret;
#endif
    }

    private void TrackHistoricalState(IPowerHistoryEntry entry)
    {
#if STANDARD_PROOF
        var proofSnapshot = entry is PowerHistoryFullEntity proofFull ? proofFull.Entity
            : entry is PowerHistoryShowEntity proofShow ? proofShow.Entity
            : entry is PowerHistoryChangeEntity proofChange ? proofChange.Entity : null;
        if (proofSnapshot != null)
        {
            string id = entry is PowerHistoryChangeEntity changed ? changed.CardId : proofSnapshot.Name;
            var required = StandardProofNative.RequiredLiveSnapshot(TransportCardId(id), proofSnapshot.Tags);
            if (required == null) proofHistoricalLiveTags.Remove(proofSnapshot.Id);
            else proofHistoricalLiveTags[proofSnapshot.Id] = required;
        }
        else if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange proofTag
            && proofHistoricalLiveTags.TryGetValue(proofTag.EntityId, out var required) && required.ContainsKey((int)proofTag.Tag))
            required[(int)proofTag.Tag] = proofTag.Value;
#endif
        if (entry is SabberStoneCore.Kettle.PowerHistoryCreateGame create)
        {
            TrackHistoricalEntity(create.Game.Id, create.Game.Tags);
            foreach (PowerPlayer player in create.Players)
                TrackHistoricalEntity(player.PowerEntity.Id, player.PowerEntity.Tags);
        }
        else if (entry is PowerHistoryFullEntity full)
            TrackHistoricalEntity(full.Entity.Id, full.Entity.Tags);
        else if (entry is PowerHistoryShowEntity show)
            TrackHistoricalEntity(show.Entity.Id, show.Entity.Tags);
        else if (entry is PowerHistoryChangeEntity change)
            TrackHistoricalEntity(change.Entity.Id, change.Entity.Tags, completeSnapshot: false);
        else if (entry is PowerHistoryHideEntity hide)
        {
            historicalZones[hide.EntityID] = hide.Zone;
            temporaryDiscardVisuals.Remove(hide.EntityID);
        }
        else if (entry is SabberStoneCore.Kettle.PowerHistoryTagChange tag)
        {
            if (tag.Tag == TemporaryCardTag || tag.Tag == GameTag.GHOSTLY)
            {
                historicalTemporaryVisuals.TryGetValue(tag.EntityId, out var visual);
                historicalTemporaryVisuals[tag.EntityId] = tag.Tag == TemporaryCardTag
                    ? (tag.Value, visual.Ghostly) : (visual.Temporary, tag.Value);
            }
            if (tag.Tag == GameTag.CONTROLLER)
                historicalControllers[tag.EntityId] = tag.Value;
            else if (tag.Tag == GameTag.ZONE)
            {
                if (tag.Value != (int)EngineZone.GRAVEYARD) temporaryDiscardVisuals.Remove(tag.EntityId);
                historicalZones[tag.EntityId] = (EngineZone)tag.Value;
#if ENGINE_SOURCE
                if (tag.Value != (int)EngineZone.DECK) privateDeckOffers.Remove(tag.EntityId);
#endif
            }
            else if (tag.Tag == GameTag.ZONE_POSITION)
                historicalPositions[tag.EntityId] = tag.Value;
        }
    }

    private void TrackHistoricalEntity(int id, IEnumerable<NativeTag> tags)
    {
        NativeTag controller = tags?.FirstOrDefault(tag => tag.Name == (int)GameTag.CONTROLLER);
        NativeTag zone = tags?.FirstOrDefault(tag => tag.Name == (int)GameTag.ZONE);
        if (controller != null)
            historicalControllers[id] = controller.Value;
        if (zone != null)
            historicalZones[id] = (EngineZone)zone.Value;
        var position = tags?.FirstOrDefault(tag => tag.Name == (int)GameTag.ZONE_POSITION);
        if (position != null) historicalPositions[id] = position.Value;
    }

    private void TrackHistoricalEntity(int id, IDictionary<GameTag, int> tags, bool completeSnapshot = true)
    {
        if (tags == null)
            return;
        historicalTemporaryVisuals.TryGetValue(id, out var visual);
        historicalTemporaryVisuals[id] = (
            tags.TryGetValue(TemporaryCardTag, out int temporary) ? temporary : completeSnapshot ? 0 : visual.Temporary,
            tags.TryGetValue(GameTag.GHOSTLY, out int ghostly) ? ghostly : completeSnapshot ? 0 : visual.Ghostly);
        if (tags.TryGetValue(GameTag.ZONE, out int location)
            && location != (int)EngineZone.HAND && location != (int)EngineZone.GRAVEYARD)
            temporaryDiscardVisuals.Remove(id);
#if ENGINE_SOURCE
        if (TagValue(tags, SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag) == BotPlayerId)
            privateChoiceOffers.Add(id);
#endif
        if (tags.TryGetValue(GameTag.CONTROLLER, out int controller))
            historicalControllers[id] = controller;
        if (tags.TryGetValue(GameTag.ZONE, out int zone))
            historicalZones[id] = (EngineZone)zone;
        if (tags.TryGetValue(GameTag.ZONE_POSITION,out int position)) historicalPositions[id] = position;
    }

    private int HiddenHistoryZone(int id, int zone) => zone == (int)EngineZone.PLAY
        && Game.IdEntityDic.TryGetValue(id, out var entity) && entity is SabberStoneCore.Model.Entities.Enchantment
            ? (int)EngineZone.SETASIDE : zone;

    private static bool IsPublicHiddenTag(GameTag tag)
    {
        return tag == GameTag.ENTITY_ID || tag == GameTag.CONTROLLER ||
               tag == GameTag.ZONE || tag == GameTag.ZONE_POSITION;
    }

    private static int DefinitionTag(string cardId, GameTag tag, int fallback)
    {
        if (string.IsNullOrEmpty(cardId)) return fallback;
        try { return EngineCards.FromId(cardId)?[tag] ?? fallback; }
        catch (KeyNotFoundException) { return fallback; }
    }

    private static int TagValue(IDictionary<GameTag, int> tags, GameTag tag)
    {
        return tags != null && tags.TryGetValue(tag, out int value) ? value : 0;
    }

    internal static string TransportCardId(string cardId)
    {
        // The pinned client keeps four Jade art tiers; legacy sizes retain their exact entity stats.
        if (cardId != null && cardId.Length == 11 && cardId.StartsWith("CFM_712_t", StringComparison.Ordinal)
            && int.TryParse(cardId.Substring(9), out int size) && size >= 1 && size <= 30)
            return size >= 20 ? "CFM_712_t20" : size >= 7 ? "CFM_712_t07" : size >= 4 ? "CFM_712_t04" : "CFM_712_t01";
        switch (cardId)
        {
            case "CS2_034": return "HERO_08bp";
            case "CS2_102": return "HERO_01bp";
            case "CS1h_001": return "HERO_09bp";
            case "CS2_101": return "HERO_04bp";
            case "CS2_017": return "HERO_06bp";
            case "DS1h_292": return "HERO_05bp";
            case "CS2_083b": return "HERO_03bp";
            case "CS2_056": return "HERO_07bp";
            case "CS2_049": return "HERO_02bp";
            case "AT_132_WARRIOR": return "HERO_01bp2";
            case "AT_132_SHAMAN": return "HERO_02bp2";
            case "AT_132_ROGUE": return "HERO_03bp2";
            case "AT_132_PALADIN": return "HERO_04bp2";
            case "AT_132_HUNTER": return "HERO_05bp2";
            case "AT_132_DRUID": return "HERO_06bp2";
            case "AT_132_WARLOCK": return "HERO_07bp2";
            case "AT_132_MAGE": return "HERO_08bp2";
            case "AT_132_PRIEST": return "HERO_09bp2";
            default: return cardId ?? "";
        }
    }

    private static bool IsDecisionInternalTag(GameTag tag)
    {
        // Infinite turn, Defile wave trackers and companion roster are private on every transport path.
        if ((int)tag == 31020 || (int)tag == 31143 || ((int)tag >= 31060 && (int)tag <= 31073)) return true;
#if ENGINE_SOURCE
        return SabberStoneCore.Actions.CompanionRules.IsInternalTag(tag) || SabberStoneCore.Actions.NativeTailRules.IsInternalTag(tag) || SabberStoneCore.Actions.ObjectiveRules.IsInternalTag(tag) || SabberStoneCore.Actions.DeathContextRules.IsInternalTag(tag) || SabberStoneCore.Actions.StartOfGameRules.IsInternalTag(tag) || SabberStoneCore.Actions.CombatRules.IsInternalTag(tag) || SabberStoneCore.Actions.BasicHeroPowerRules.IsInternalTag(tag) || SabberStoneCore.Actions.QuestRules.IsInternalTag(tag) || SabberStoneCore.Actions.CostRules.IsInternalTag(tag) || SabberStoneCore.Actions.Generic.IsDecisionInternalTag(tag) || SabberStoneCore.Actions.LeylineRules.IsInternalTag(tag)
            || SabberStoneCore.Actions.StatGainRules.IsInternalTag(tag) || SabberStoneCore.Actions.SwapRules.IsInternalTag(tag)
            || tag == SabberStoneCore.Actions.ColossalRules.RealizedFactor
            || SabberStoneCore.Actions.DormantRules.IsPrivateTag(tag) || SabberStoneCore.Actions.TemporaryRules.IsPrivateTag(tag)
            || SabberStoneCore.Actions.PlayLockRules.IsPrivateTag(tag)
            || SabberStoneCore.Actions.PayloadRules.IsInternalTag(tag)
            || SabberStoneCore.Actions.AuraSpellRules.IsInternalTag(tag)
            || SabberStoneCore.Actions.HealingRules.IsInternalTag(tag) || SabberStoneCore.Actions.ShatterRules.IsInternalTag(tag) || SabberStoneCore.Actions.HandProgressRules.IsInternalTag(tag)
            || SabberStoneCore.Actions.CthunRules.IsInternalTag(tag) || SabberStoneCore.Actions.OldGodRules.IsInternalTag(tag)
            || SabberStoneCore.Actions.AutonomousTurnRules.IsInternalTag(tag)
            || SabberStoneCore.Actions.DiscoverRealRules.IsInternalTag(tag);
#else
        return false;
#endif
    }

    private static List<NativeTag> ToNativeTags(IEnumerable<KeyValuePair<GameTag, int>> tags)
    {
        var result = tags == null
            ? new List<NativeTag>()
            : tags.Where(pair => pair.Key != GameTag.LAST_CARD_DISCARDED && (int)pair.Key != 4327 && !IsDecisionInternalTag(pair.Key)).Select(pair => new NativeTag { Name = (int)pair.Key, Value = pair.Value }).ToList();
        // Retail TagVisualConfiguration reads GHOSTLY (hand spell 128/200), not keyword reference 3630.
        var temporary = result.FirstOrDefault(tag => tag.Name == (int)TemporaryCardTag);
        if (temporary != null)
        {
            int ghostly = Math.Max(temporary.Value, result.FirstOrDefault(tag => tag.Name == (int)GameTag.GHOSTLY)?.Value ?? 0);
            result.RemoveAll(tag => tag.Name == (int)TemporaryCardTag || tag.Name == (int)GameTag.GHOSTLY);
            result.Add(new NativeTag { Name = (int)GameTag.GHOSTLY, Value = ghostly });
        }
        return result;
    }
}

// A legal native send lost the turn deadline; callers must still finish native input cleanup.
internal sealed class TurnExpiredException : InvalidOperationException
{
    internal TurnExpiredException() : base("Player turn expired before submission.") { }
}

// The host no longer lists this entity as revealed. Distinct from other snapshot failures (such as a revealed entity with no
// cached tags, a real host inconsistency): the live audit treats only this one as pending/unrevealed.
internal sealed class TransportEntityNotVisibleException : InvalidOperationException
{
    internal TransportEntityNotVisibleException(string message) : base(message) { }
}
