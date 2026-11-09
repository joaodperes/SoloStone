public sealed partial class DeckPlanEvaluator
{
    // doomed-minions (AiFixes): a minion carrying a delayed self-destroy earns no board credit on either side; set from the
    // profile by the searches that own this evaluator. Default OFF = base.
    internal bool ZeroDoomed { get; set; }
    // The board price every caller uses; Board() itself stays the plain printed-stat price.
    private double Priced(FairCard c) => ZeroDoomed && c.Doomed ? 0 : Board(c);
    // What an ENEMY doomed minion is still worth to the enemy at our end of turn: one swing in its reply, gone at our next
    // turn start (Nightmare/Corruption we cast on it). Static leaves only (Evaluate, never ReplyValue): inside reply search
    // an unspent swing would be a credit that attacking destroys, demoting the attack in the beam. Our own doomed minions
    // get nothing: at our end of turn they cannot swing, and mid-turn the same inverse incentive would discourage attacks.
    internal double DoomedSwing(FairPlayer owner, FairFacts facts)
    {
        if (!ZeroDoomed || owner.PlayerId != facts.CurrentPlayerId) return 0;
        double swing = 0;
        for (int i = 0; i < owner.Board.Count; i++) swing += Swing(owner.Board[i]);
        return swing;
    }
    internal double Swing(FairCard c) => c.Doomed && !c.Exhausted && !c.Frozen && !c.CantAttack && !c.Untouchable && c.AttacksThisTurn < c.AttackLimit
        ? c.Attack * (W.BoardAttack * k.BoardAttack) : 0;
}
