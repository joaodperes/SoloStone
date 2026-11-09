using System;
using System.Linq;
using SabberStoneCore.Enums;
using EngineGame = SabberStoneCore.Model.Game;
using SabberStoneCore.Model.Entities;

public static class NpcConcession
{
    public static bool ShouldConcede(EngineGame game, int botPlayerId, NpcProfile profile, string matchId,
        int publicEventIndex, bool usefulSequenceFinished, int maxNodes = 32)
    {
        return usefulSequenceFinished && game != null && profile != null && game.Turn >= Math.Max(8, profile.Personality.ConcedeFromTurn)
            && MatchRecorder.PersonalityRoll(profile.Version, matchId, publicEventIndex, "concede") < profile.Personality.ConcessionWillingness
            && ProvesPublicBoardLethal(game, botPlayerId, maxNodes);
    }

    // shortcut: prove only plain minion attacks into an empty board; extend with a deterministic combat model before accepting defenses/triggers.
    public static bool ProvesPublicBoardLethal(EngineGame game, int botPlayerId, int maxNodes = 32)
    {
        if (game == null || game.State == State.COMPLETE || game.Step != Step.MAIN_ACTION || maxNodes <= 0) return false;
        var bot = game.Player1.PlayerId == botPlayerId ? game.Player1 : game.Player2.PlayerId == botPlayerId ? game.Player2 : null;
        if (bot == null || game.CurrentPlayer != bot || bot.Choice != null || bot.Opponent.Choice != null) return false;
        var human = bot.Opponent;
        // Start-of-turn fatigue can kill the human before any of these attacks.
        if (human.DeckZone.Count == 0 || bot.SecretZone.Count != 0 || human.SecretZone.Count != 0 || bot.BoardZone.Count != 0
            || bot.Hero.Weapon != null || human.Hero.Weapon != null
            || HasEnchantments(bot) || HasEnchantments(human) || HasEnchantments(bot.Hero) || HasEnchantments(human.Hero)
            || bot.Hero[GameTag.INCOMING_DAMAGE_CAP] > 0 || bot.Hero[GameTag.INCOMING_COMBAT_DAMAGE_CAP] > 0
            || bot.Hero.HasStealth || bot.Hero.IsImmune || bot.Hero[GameTag.DIVINE_SHIELD] != 0 || bot.Hero[GameTag.HEALTH_MINIMUM] > 0
            || bot.Hero.Card.Id.Length != 7 || human.Hero.Card.Id.Length != 7
            || !bot.Hero.Card.Id.StartsWith("HERO_", StringComparison.Ordinal)
            || !human.Hero.Card.Id.StartsWith("HERO_", StringComparison.Ordinal)) return false;
#if ENGINE_SOURCE
        if (bot.BoardZone.GetLocations().Any() || human.BoardZone.GetLocations().Any()) return false;
#endif
        long damage = 0;
        int nodes = 0;
        foreach (var minion in human.BoardZone)
        {
            if (++nodes > maxNodes || HasEnchantments(minion) || !string.IsNullOrWhiteSpace(minion.Card.Text)
                || minion[GameTag.DEATHRATTLE] != 0 || minion[GameTag.WINDFURY] != 0
                || minion[GameTag.CANT_ATTACK] != 0 || minion[GameTag.CANNOT_ATTACK_HEROES] != 0
                || minion[GameTag.OUTGOING_DAMAGE_ADJUSTMENT] != 0
                || minion.NativeTags.TryGetValue(GameTag.OUTGOING_DAMAGE_ADJUSTMENT, out int adjustment) && adjustment != 0
                || minion[GameTag.OUTGOING_DAMAGE_CAP] > 0 || minion[GameTag.OUTGOING_COMBAT_DAMAGE_CAP] > 0 || minion[GameTag.UNTOUCHABLE] != 0
#if ENGINE_SOURCE
                || minion[GameTag.DORMANT] != 0
#endif
                ) return false;
            if (!minion.IsFrozen) damage += Math.Max(0, minion.AttackDamage);
        }
        return bot.Hero.Health > 0 && damage >= (long)bot.Hero.Health + bot.Hero.Armor;
    }
    private static bool HasEnchantments(SabberStoneCore.Model.Entities.Entity entity) => entity.AppliedEnchantments?.Count > 0;
}
