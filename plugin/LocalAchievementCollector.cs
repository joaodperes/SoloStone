using System;
#if ENGINE_SOURCE
using System.Linq;
#endif
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineGame = SabberStoneCore.Model.Game;

internal static class LocalAchievementCollector
{
#if ENGINE_SOURCE
    internal const bool SupportsAuthoritativeFacts = true;
#else
    internal const bool SupportsAuthoritativeFacts = false;
#endif

    internal static LocalAchievementMatchEvidence From(EngineGame game, int humanControllerId,
        LocalAchievementPendingContext pending)
    {
        if (game == null) throw new ArgumentNullException(nameof(game));
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        if (game.State != State.COMPLETE) throw new InvalidOperationException("Achievement evidence requires a terminal game.");
#if ENGINE_SOURCE
        var human = game.Player1.Id == humanControllerId ? game.Player1
            : game.Player2.Id == humanControllerId ? game.Player2
            : throw new ArgumentOutOfRangeException(nameof(humanControllerId));
        var opponent = human.Opponent;
        if (!game.HistoryFacts.EvidenceValid)
        {
            // Live validation already rejected this ledger: settle the match, earn no achievements from it.
            var played = human.PlayState;
            string settled = played == PlayState.WON ? "Won" : played == PlayState.LOST ? "Lost"
                : played == PlayState.TIED ? "Tied" : throw new InvalidOperationException("Terminal game has no player result.");
            return new LocalAchievementMatchEvidence(pending.MatchId, settled, pending.StartingHero,
                pending.QueuedNpcId, pending.QueuedNpcBand, false, false, false,
                LocalAchievementContextBinding.Create(pending),
                LocalAchievementFingerprint.Hash("invalid-evidence\n" + game.HistoryFacts.CanonicalText()), true,
                human.Id, opponent.Id, human.Hero.Id, opponent.Hero.Id, Array.Empty<HistoryFact>())
                .Invalidated(game.HistoryFacts.EvidenceFailure);
        }
        var facts = game.HistoryFacts.Records.ToArray();
        var terminal = facts.OfType<MatchTerminalFact>().Single();
        var state = terminal.Player1Id == humanControllerId ? terminal.Player1State : terminal.Player2State;
        string result = state == PlayState.WON ? "Won" : state == PlayState.LOST ? "Lost"
            : state == PlayState.TIED ? "Tied" : throw new InvalidOperationException("Terminal game has no player result.");
        var heroFatals = facts.OfType<HeroFatalFact>().Where(x => x.ResolutionId > 0).ToArray();
        var humanFatal = heroFatals.LastOrDefault(x => x.HeroId == human.Hero.Id);
        var opponentFatal = heroFatals.LastOrDefault(x => x.HeroId == opponent.Hero.Id);
        int humanHealth = terminal.Player1Id == human.Id ? terminal.Player1Health : terminal.Player2Health;
        int opponentHealth = terminal.Player1Id == opponent.Id ? terminal.Player1Health : terminal.Player2Health;
        bool naturalDraw = result == "Tied" && humanHealth <= 0 && opponentHealth <= 0
            && humanFatal != null && opponentFatal != null && humanFatal.ResolutionId == opponentFatal.ResolutionId;
        return new LocalAchievementMatchEvidence(pending.MatchId, result, pending.StartingHero,
            pending.QueuedNpcId, pending.QueuedNpcBand, false, false, naturalDraw,
            LocalAchievementContextBinding.Create(pending), LocalAchievementFingerprint.Hash(game.HistoryFacts.CanonicalText()), true,
            human.Id, opponent.Id, human.Hero.Id, opponent.Hero.Id, facts);
#else
        return new LocalAchievementMatchEvidence(pending.MatchId, null, pending.StartingHero,
            pending.QueuedNpcId, pending.QueuedNpcBand, false, false, false, false,
            humanControllerId, 0, 0, 0);
#endif
    }
}
