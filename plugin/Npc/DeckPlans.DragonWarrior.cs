using System.Linq;

public static partial class DeckPlans
{
    public static readonly DeckPlan DragonWarrior = new DeckPlan(
        "dragon-warrior.v1", "npc-dragon-warrior", DefaultMatchups,
        new[] {
            new MulliganRule("CATA_556", maxCopies: 2),
            new MulliganRule("CAP_107", maxCopies: 2),
            new MulliganRule("EDR_889"),
            new MulliganRule("EDR_456", condition: MulliganCondition.KeptDragon),
            new MulliganRule("EDR_457", condition: MulliganCondition.KeptDragon),
            new MulliganRule("CATA_585", Matchup.Aggro, MulliganCondition.HasOneDrop),
            new MulliganRule("CAP_105", Matchup.Midrange | Matchup.Control, MulliganCondition.EarlyCurve, minimumEarlyPlays: 1),
            new MulliganRule("FIR_939", Matchup.Aggro | Matchup.Midrange, MulliganCondition.HasOneDrop),
            new MulliganRule("EDR_492", Matchup.Aggro, MulliganCondition.EarlyCurve, minimumEarlyPlays: 1),
            new MulliganRule("EDR_492", Matchup.Midrange, MulliganCondition.EarlyCurve, minimumEarlyPlays: 2),
            new MulliganRule("TIME_034", condition: MulliganCondition.EarlyCurveAndDragon, minimumEarlyPlays: 2),
            new MulliganRule("JAIL_421", Matchup.Midrange, MulliganCondition.EarlyCurve, minimumEarlyPlays: 2)
        },
        new[] {
            new CardRoleEntry("EDR_456", CardRole.Development | CardRole.Generator),
            new CardRoleEntry("CATA_556", CardRole.Development | CardRole.Dragon | CardRole.Generator),
            new CardRoleEntry("CATA_585", CardRole.Removal | CardRole.Fire),
            new CardRoleEntry("CAP_107", CardRole.Development | CardRole.Generator),
            new CardRoleEntry("EDR_457", CardRole.Development | CardRole.Removal),
            new CardRoleEntry("EDR_889", CardRole.Development | CardRole.Dragon),
            new CardRoleEntry("FIR_939", CardRole.Removal | CardRole.Fire | CardRole.Generator),
            new CardRoleEntry("CATA_582", CardRole.Removal | CardRole.Fire | CardRole.DamageEnabler),
            new CardRoleEntry("CAP_105", CardRole.Development | CardRole.Generator),
            new CardRoleEntry("CATA_584", CardRole.Removal | CardRole.Payoff),
            new CardRoleEntry("EDR_492", CardRole.Rush | CardRole.DamageEnabler),
            new CardRoleEntry("TIME_034", CardRole.Dragon | CardRole.Generator),
            new CardRoleEntry("JAIL_421", CardRole.Payoff | CardRole.Finisher),
            new CardRoleEntry("END_033", CardRole.Dragon | CardRole.Payoff),
            new CardRoleEntry("TLC_600", CardRole.Dragon | CardRole.Removal | CardRole.Finisher),
            new CardRoleEntry("JAIL_384", CardRole.Payoff)
        },
        new[] {
            new HoldRule(HoldCondition.DragonActivator, 3, new[] { "CATA_556", "EDR_889", "TIME_034", "END_033", "TLC_600" },
                "EDR_456", "EDR_457", "END_033"),
            new HoldRule(HoldCondition.DamagedTargetWithExcess, 2, new[] { "CATA_585" }),
            new HoldRule(HoldCondition.ThreatOrLethal, 4, new[] { "TLC_600" })
        },
        new[] {
            new RemovalThreshold("CATA_585", 2, 1, 1),
            new RemovalThreshold("FIR_939", 2, 2),
            new RemovalThreshold("TLC_600", 5, 4)
        },
        new[] {
            new AggressionPoint(1, int.MaxValue, int.MaxValue, .65),
            new AggressionPoint(4, int.MaxValue, int.MaxValue, 1),
            new AggressionPoint(7, int.MaxValue, int.MaxValue, 1.3),
            new AggressionPoint(1, int.MaxValue, 12, 1.65),
            new AggressionPoint(1, 10, int.MaxValue, .45),
            new AggressionPoint(1, 10, 8, 1.8)
        },
        new[] {
            new PlanHint(PlanHintKind.WarptoothDamage, 4, 8,
                "Four distinct friendly characters actually damaged this turn; only reward setup with own Warptooth in hand/deck and room to summon. " +
                "Fissure, friendly minion trades, hero attacks after Fissure and Cannoneer-target trades enable it; pings alone damage enemies, not friendly characters.",
                "JAIL_421", "CATA_582", "EDR_492", "CAP_107", "CAP_105"),
            new PlanHint(PlanHintKind.FireBeforeVolcano, 1, 3,
                "Play Fire before ACTIVATION this turn for +3 damage; location placement may precede Fire. Random splits are not guaranteed lethal.",
                "CATA_584", "CATA_585", "CATA_582", "FIR_939"),
            new PlanHint(PlanHintKind.EarlyRushTrades, 4, 2,
                "Use Duckling Rush and Cannoneer-supported trades early; count damage before deaths, protect lethal blockers.", "EDR_492", "CAP_107", "CAP_105"),
            new PlanHint(PlanHintKind.KindredDragon, 1, 2,
                "Use previous-turn Dragon history for Wyrm's Kindred; preserve another held Dragon for Slitherdrake's live discount.", "TLC_600", "END_033")
        },
        new[] {
            new DiscoverPreference("EDR_456", DiscoverPool.ClassDragons,
                "Prefer playable curve/board impact, then another Dragon activator; Dark Gift scores survivability and immediate tempo.", "CATA_556", "EDR_889", "END_033", "TLC_600"),
            new DiscoverPreference("EDR_456", DiscoverPool.DarkGifts, "Choose tempo or survival on the offered Dragon; no fixed gift beats lethal."),
            new DiscoverPreference("FIR_939", DiscoverPool.WarriorMinions,
                "Removal or playable Warrior body before expensive value; Warptooth only with a live four-damage setup.", "EDR_457", "TLC_600", "JAIL_421"),
            new DiscoverPreference("FIR_939", DiscoverPool.DarkGifts, "Prefer immediate impact and survivability over speculative future value."),
            new DiscoverPreference("CAP_105", DiscoverPool.ClassPirates,
                "Prefer affordable Pirates and Cannoneer support; offers are class-legal plus neutral, not every class's Pirates.", "CAP_107"),
            new DiscoverPreference("TIME_034", DiscoverPool.AnyWeapon, "Stadium rolls both weapons from this documented generated pool; use the Rewind table.")
        },
        new[] { DragonWeapons() },
        new EvaluatorWeights(.8, 1, 2.4, 1, 1.8, 1.5, 3, 8, 0, 1, 0, 0));

    private static RewindPreference DragonWeapons() => new RewindPreference("TIME_034", 1.0 / 3, 3,
        // Scores value equipped weapons, so Battlecries do not count. Retain only the active meta's weapons.
        new[] {
        new WeaponPreference("CORE_BT_921", 7), new WeaponPreference("CORE_TRL_111", 4),
        new WeaponPreference("RLK_067", 13), new WeaponPreference("CORE_RLK_086", 14),
        new WeaponPreference("EDR_416", 8), new WeaponPreference("EDR_812", 4),
        new WeaponPreference("EDR_525", 3), new WeaponPreference("EDR_253", 12),
        new WeaponPreference("EDR_842", 9), new WeaponPreference("FIR_922", 2),
        new WeaponPreference("CORE_BT_781", 7), new WeaponPreference("TLC_833", 7),
        new WeaponPreference("DINO_408", 7), new WeaponPreference("TLC_478", 8),
        new WeaponPreference("TLC_EVENT_402", 6),
        new WeaponPreference("CORE_DAL_720", 10), new WeaponPreference("TIME_444", 5),
        new WeaponPreference("END_012", 4), new WeaponPreference("END_016", 6),
        new WeaponPreference("END_001", 6), new WeaponPreference("CATA_467", 7),
        new WeaponPreference("CATA_580", 6), new WeaponPreference("CATA_472", 5),
        new WeaponPreference("MEND_803", 6), new WeaponPreference("JAIL_503", 8),
        new WeaponPreference("JAIL_380", 2), new WeaponPreference("JAIL_875", 6),
        new WeaponPreference("JAIL_450", 5), new WeaponPreference("JAIL_376", 3),
        new WeaponPreference("JAIL_329", 9), new WeaponPreference("JAIL_730", 9),
        new WeaponPreference("CORE_OG_031", 10), new WeaponPreference("CAP_103", 11),
        new WeaponPreference("JAIL_458", 10), new WeaponPreference("BE_000", 7),
        new WeaponPreference("CORE_LOOT_044", 2), new WeaponPreference("BE_552", 7),
        new WeaponPreference("BE_558", 4), new WeaponPreference("BE_227", 1),
        new WeaponPreference("CORE_GVG_059", 6), new WeaponPreference("BE_349", 10),
        new WeaponPreference("BE_771", 2)
        }.Where(weapon => LocalCardCatalog.InActiveMeta(SabberStoneCore.Model.Cards.FromId(weapon.CardId))).ToArray());
}
