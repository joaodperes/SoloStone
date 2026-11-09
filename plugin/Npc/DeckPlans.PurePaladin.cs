public static partial class DeckPlans
{
    public static readonly DeckPlan PurePaladin = new DeckPlan(
        "pure-paladin.v1", "npc-pure-paladin", DefaultMatchups,
        new[] {
            new MulliganRule("CORE_ICC_038", maxCopies: 2),
            new MulliganRule("CATA_472"),
            new MulliganRule("MEND_802"),
            new MulliganRule("END_011", Matchup.Midrange | Matchup.Control, MulliganCondition.HasOneDrop),
            new MulliganRule("JAIL_035", Matchup.Aggro | Matchup.Midrange),
            new MulliganRule("JAIL_327", condition: MulliganCondition.EarlyCurve, minimumEarlyPlays: 1),
            new MulliganRule("TIME_019", Matchup.Aggro, MulliganCondition.HasAura),
            new MulliganRule("CATA_479", Matchup.Control, MulliganCondition.EarlyCurve, minimumEarlyPlays: 2),
            new MulliganRule("CATA_473", Matchup.Control, MulliganCondition.EarlyCurve, minimumEarlyPlays: 2)
        },
        new[] {
            new CardRoleEntry("CORE_ICC_038", CardRole.Development | CardRole.Shield),
            new CardRoleEntry("END_011", CardRole.Aura | CardRole.Development),
            new CardRoleEntry("CATA_472", CardRole.Removal),
            new CardRoleEntry("MEND_802", CardRole.Development | CardRole.Shield),
            new CardRoleEntry("JAIL_035", CardRole.Development | CardRole.Payoff),
            new CardRoleEntry("JAIL_327", CardRole.Aura | CardRole.Refill),
            new CardRoleEntry("TIME_009t1", CardRole.Aura),
            new CardRoleEntry("TIME_019", CardRole.Removal | CardRole.Payoff),
            new CardRoleEntry("CATA_474", CardRole.Generator),
            new CardRoleEntry("CATA_479", CardRole.Development | CardRole.Shield),
            new CardRoleEntry("EDR_258", CardRole.Shield | CardRole.Payoff),
            new CardRoleEntry("TIME_009t2", CardRole.Aura | CardRole.Shield),
            new CardRoleEntry("TIME_700", CardRole.Aura | CardRole.Refill),
            new CardRoleEntry("CATA_473", CardRole.Shield | CardRole.Finisher),
            new CardRoleEntry("JAIL_397", CardRole.Payoff),
            new CardRoleEntry("JAIL_516", CardRole.Refill | CardRole.Rush),
            new CardRoleEntry("CATA_475", CardRole.Removal | CardRole.Finisher),
            new CardRoleEntry("TIME_009", CardRole.Aura | CardRole.Refill | CardRole.Payoff)
        },
        new[] {
            new HoldRule(HoldCondition.AuraForTimeways, 4,
                new[] { "END_011", "JAIL_327", "TIME_009t1", "TIME_009t2", "TIME_700" }, "TIME_019"),
            new HoldRule(HoldCondition.RefillAfterClear, 3, new[] { "JAIL_516", "TIME_009", "MEND_802" })
        },
        new[] { new RemovalThreshold("TIME_019", 2, 2), new RemovalThreshold("CATA_475", 2, 2) },
        new[] {
            new AggressionPoint(1, int.MaxValue, int.MaxValue, .3),
            new AggressionPoint(5, int.MaxValue, int.MaxValue, .65),
            new AggressionPoint(8, int.MaxValue, int.MaxValue, 1),
            new AggressionPoint(1, int.MaxValue, 10, 1.3),
            new AggressionPoint(1, 12, int.MaxValue, .2)
        },
        new[] {
            new PlanHint(PlanHintKind.AuraTiming, 1, 4,
                "Preserve active Aura coverage through Timeways' Battlecry; count remaining duration. Acceleration pays next turn; end-turn Auras pay this turn. Gelbin deploys Auras still in deck.",
                "END_011", "JAIL_327", "TIME_009t1", "TIME_009t2", "TIME_700", "TIME_019", "TIME_009"),
            new PlanHint(PlanHintKind.BoardSpace, 3, 3,
                "Reserve slots for Sentry's two copies, Recruiter's two recruits, and Aura end-turn summons. Trade first only when safe; locations also occupy board slots.",
                "JAIL_035", "JAIL_516", "JAIL_327", "TIME_700"),
            new PlanHint(PlanHintKind.ShatterHalves, 2, 3,
                "Evaluate each Flight Maneuvers half independently: summon before buff with room for two Drakes; buff a ready wide board when slots are scarce.", "CATA_479"),
            new PlanHint(PlanHintKind.ProtectDivineShields, 3, 4,
                "Avoid spending Shields on trivial pings; retain Nozdormu shields for +3/+3. Toreth makes a Shield take three hits, not three extra Shields.",
                "CORE_ICC_038", "MEND_802", "EDR_258", "CATA_473", "TIME_009t2")
        },
        new DiscoverPreference[0], new RewindPreference[0],
        new EvaluatorWeights(1.1, .7, 2.2, 1.3, .8, 4, 0, 0, 5, 3, 0, 0));
}
