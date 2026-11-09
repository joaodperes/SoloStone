public static partial class DeckPlans
{
    public static readonly DeckPlan ZeeShaman = new DeckPlan(
        "zee-shaman.v1", "npc-zee-shaman", DefaultMatchups,
        new[] {
            new MulliganRule("CORE_WC_042"), new MulliganRule("CORE_UNG_809", maxCopies: 2),
            new MulliganRule("CATA_556"), new MulliganRule("CORE_GIL_531"),
            new MulliganRule("CORE_ULD_191", condition: MulliganCondition.HasOneDrop),
            new MulliganRule("JAIL_802"), new MulliganRule("JAIL_850"),
            new MulliganRule("DINO_411", condition: MulliganCondition.HasOneDrop),
            new MulliganRule("CATA_565", Matchup.Aggro),
            new MulliganRule("CORE_UNG_205", Matchup.Aggro | Matchup.Midrange),
            new MulliganRule("JAIL_457", condition: MulliganCondition.CoinAndCheapMinions, minimumEarlyPlays: 2)
        },
        new[] {
            new CardRoleEntry("CORE_GIL_531", CardRole.Development | CardRole.Generator),
            new CardRoleEntry("CORE_ULD_191", CardRole.Development),
            new CardRoleEntry("CORE_UNG_205", CardRole.Development | CardRole.Removal),
            new CardRoleEntry("CORE_UNG_809", CardRole.Development | CardRole.Generator),
            new CardRoleEntry("CORE_WC_042", CardRole.Development),
            new CardRoleEntry("CATA_556", CardRole.Development | CardRole.Dragon | CardRole.Generator),
            new CardRoleEntry("DINO_411", CardRole.Development | CardRole.Refill),
            new CardRoleEntry("CATA_565", CardRole.Development | CardRole.Herald),
            new CardRoleEntry("JAIL_802", CardRole.Development),
            new CardRoleEntry("JAIL_850", CardRole.Development | CardRole.Payoff),
            new CardRoleEntry("EDR_856", CardRole.Generator | CardRole.Refill),
            new CardRoleEntry("JAIL_457", CardRole.Payoff),
            new CardRoleEntry("JAIL_458", CardRole.Removal | CardRole.Generator),
            new CardRoleEntry("TLC_482", CardRole.Development | CardRole.Payoff),
            new CardRoleEntry("CATA_722", CardRole.Herald),
            new CardRoleEntry("CATA_497", CardRole.Herald),
            new CardRoleEntry("JAIL_800", CardRole.Development),
            new CardRoleEntry("CATA_153", CardRole.Rush | CardRole.Refill | CardRole.Finisher),
            new CardRoleEntry("CATA_190h", CardRole.Removal | CardRole.Finisher)
        },
        new HoldRule[0],
        new[] { new RemovalThreshold("CORE_UNG_205", 3, 1), new RemovalThreshold("CATA_190h", 4, 4) },
        new[] {
            new AggressionPoint(1, int.MaxValue, int.MaxValue, .95),
            new AggressionPoint(4, int.MaxValue, int.MaxValue, 1.45),
            new AggressionPoint(7, int.MaxValue, int.MaxValue, 1.6),
            new AggressionPoint(1, int.MaxValue, 16, 1.9),
            new AggressionPoint(1, 8, int.MaxValue, .7),
            new AggressionPoint(1, 8, 10, 2)
        },
        new PlanHint[0],
        new[] {
            new DiscoverPreference("EDR_856", DiscoverPool.OwnDeckMinions,
                "Board impact, hand refill, then Herald; take finishers only when affordable.", "JAIL_457", "DINO_411", "CATA_565", "CATA_497", "CATA_153"),
            new DiscoverPreference("EDR_856", DiscoverPool.DarkGifts, "Immediate tempo and useful keywords; do not choose an unplayable expensive body for speculative value."),
            new DiscoverPreference("JAIL_458", DiscoverPool.TinyPalAmmo, "Removal when behind; face pressure when racing; re-evaluate after every hero attack."),
            new DiscoverPreference("CATA_190h", DiscoverPool.DeathwingCataclysms, "Guaranteed lethal first, survival second, board/value third; use current upgrade level.")
        }, new RewindPreference[0],
        new EvaluatorWeights(.7, 1.4, 2.7, 1.1, 1.6, 1.5, 0, 0, 0, 2, 2, 0));
}
