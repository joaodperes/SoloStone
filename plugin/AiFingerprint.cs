#if !SOLOSTONE_RELEASE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using EngineGame = SabberStoneCore.Model.Game;

// Canonical text stays available for diagnosing a digest mismatch. Never mutate the input.
public static class AiFingerprint
{
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Tags(IEnumerable<KeyValuePair<GameTag, int>> tags) => string.Join(",",
        tags.OrderBy(p => (int)p.Key).Select(p => N((int)p.Key) + "=" + N(p.Value)));
    private static string EntityTags(IEntity entity) => Tags(entity.Card.Tags.Concat(entity.NativeTags)
        .GroupBy(p => p.Key).Select(g => g.Last()));
    public static string Hash(string text)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }
    public static string Public(FairBotState state) => Hash(PublicText(state));
    public static string PublicText(FairBotState state)
    {
        var facts = state.Facts;
        return Dump(state.FingerprintGame, true) + "\nsecrets:" + N(facts.OpponentSecretCount)
            + "\nknown-deck:" + string.Join(",", facts.OwnDeckKnowledge.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + N(p.Value)));
    }
    public static string Replay(EngineGame game) => Hash(ReplayText(game));
    public static string ReplayText(EngineGame game)
    {
#if ENGINE_SOURCE
        // Random is internal to the engine; reflection keeps this shared helper usable in net48 hosts too.
        var random = typeof(EngineGame).GetProperty("Random", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(game, null);
        var rng = (RngState)random.GetType().GetMethod("CaptureState").Invoke(random, null);
        return Dump(game) + "\nrng:" + N(rng.AlgorithmVersion) + ":" + N(rng.State0) + ":" + N(rng.State1) + ":" + N(rng.DrawCount);
#else
        throw new NotSupportedException("RNG-aware replay fingerprints require the source engine.");
#endif
    }
    private static string Dump(EngineGame game, bool includeBranches = false)
    {
        var text = new StringBuilder("game:").Append(EntityTags(game));
        foreach (var entity in game.IdEntityDic.Values.OrderBy(e => e.Id))
        {
            text.Append("\nentity:").Append(N(entity.Id)).Append(':').Append(entity.Card.Id).Append(':').Append(EntityTags(entity));
            if (includeBranches && entity.ChooseOnePlayables != null)
                text.Append(":branches:").Append(string.Join(",", entity.ChooseOnePlayables
                    .Select(branch => branch == null ? "null" : N(branch.Id) + "=" + branch.Card.Id)));
        }
        foreach (var player in new[] { game.Player1, game.Player2 })
        {
            text.Append("\nplayer:").Append(N(player.Id)).Append(':').Append(EntityTags(player));
            foreach (var zone in new IEnumerable<IPlayable>[] { player.HandZone, player.DeckZone,
#if ENGINE_SOURCE
                player.BoardZone.Slots,
#else
                player.BoardZone,
#endif
                player.SecretZone, player.GraveyardZone, player.SetasideZone })
                text.Append('/').Append(string.Join(",", zone.Select(e => N(e.Id))));
            if (player.Choice != null)
                text.Append("\nchoice:").Append(N((int)player.Choice.ChoiceType)).Append(':')
                    .Append(string.Join(",", player.Choice.Choices.Select(e => N(e))));
        }
#if ENGINE_SOURCE
        text.Append("\nhistory:").Append(game.HistoryFacts.CanonicalText());
#endif
        return text.ToString();
    }
}
#endif
