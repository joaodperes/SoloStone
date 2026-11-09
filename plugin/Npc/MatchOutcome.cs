using SabberStoneCore.Enums;
using EngineGame = SabberStoneCore.Model.Game;

public sealed class MatchOutcome
{
    public string Result { get; private set; }
    public int Turns { get; private set; }
    public bool Conceded { get; private set; }
    public static MatchOutcome From(EngineGame game, bool humanConceded)
    {
        var state = game.Player1.PlayState;
        return new MatchOutcome {
            Result = state == PlayState.WON ? "Won" : state == PlayState.LOST ? "Lost" : state == PlayState.TIED ? "Tied" : null,
            Turns = game.Turn, Conceded = humanConceded
        };
    }
}
