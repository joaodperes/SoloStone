using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EngineGame = SabberStoneCore.Model.Game;

// Recorder failures must never abort an authoritative action or reward settlement.
public sealed class MatchRecorder : IMatchRecorder
{
    private readonly Action<string> log;
    private long sequence;
    public IMatchRecorder Inner { get; set; } = NullMatchRecorder.Instance;
    public Func<EngineGame, string> ReplayHash { get; set; } = Fingerprint;
    public MatchRecorder(Action<string> log = null) { this.log = log; }
    public bool Enabled { get { try { return Inner != null && Inner.Enabled; } catch { return false; } } }
    public void Write(Action<IMatchRecorder> write)
    {
        if (!Enabled) return;
        try { write(Inner); }
        catch (Exception error) { try { log?.Invoke("recorder-error=" + error.GetType().Name); } catch { } }
    }
    public void PostAction(EngineGame game, string implicitKind = null)
    {
        Write(r => {
            if (implicitKind != null) (r as PlaytestRecord)?.OnImplicitAction(implicitKind);
            (r as PlaytestRecord)?.CaptureState(game);
            r.OnPostAction(++sequence, ReplayHash(game));
        });
    }
    public void OnMulligan(int playerId, IReadOnlyList<int> keptEntityIds) => Write(r => r.OnMulligan(playerId, keptEntityIds));
    public void OnDecision(GoldenContext context, GoldenDecision trace, string lethalResult, bool planReused,
        int nodes, double thinkMilliseconds, double maxSliceMilliseconds) => Write(r => r.OnDecision(context, trace, lethalResult, planReused, nodes, thinkMilliseconds, maxSliceMilliseconds));
    public void OnHumanAction(int turn, string botAction, int optionsIndex, IReadOnlyList<int> choices, bool conceded) => Write(r => r.OnHumanAction(turn, botAction, optionsIndex, choices, conceded));
    public void OnEmote(int playerId, string emote, int publicEventIndex) => Write(r => r.OnEmote(playerId, emote, publicEventIndex));
    public void OnConcession(int playerId, int turn) => Write(r => r.OnConcession(playerId, turn));
    public void OnResult(string result, int turns) => Write(r => r.OnResult(result, turns));
    public void OnPostAction(long actionSequence, string replayFingerprintSha256) => Write(r => r.OnPostAction(actionSequence, replayFingerprintSha256));

#if ENGINE_SOURCE
    private static readonly PropertyInfo RandomProperty = typeof(EngineGame).GetProperty("Random", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo CaptureRandom = RandomProperty.PropertyType.GetMethod("CaptureState");
#endif
    // Additive to the v1 fingerprint: old journals still replay; new ones also verify RNG state after every action.
    public static string RandomState(EngineGame game)
    {
#if ENGINE_SOURCE
        var state = (SabberStoneCore.Model.RngState)CaptureRandom.Invoke(RandomProperty.GetValue(game, null), null);
        return string.Join(":", new[] { (long)state.AlgorithmVersion, state.State0, state.State1, state.DrawCount }
            .Select(n => n.ToString(CultureInfo.InvariantCulture)));
#else
        return null;
#endif
    }

    // Preserve the v1 replay contract for older journals. New action rows verify RNG separately via RandomState.
    public static string Fingerprint(EngineGame game) => Hash(FingerprintText(game));
    // Canonical text behind the digest, for diagnosing a mismatch.
    public static string FingerprintText(EngineGame game)
    {
        var text = new StringBuilder("game:").Append(Tags(game.NativeTags));
        foreach (var entity in game.IdEntityDic.Values.OrderBy(e => e.Id))
            text.Append(';').Append(Number(entity.Id)).Append(':').Append(entity.Card?.Id).Append(':').Append(Tags(entity.NativeTags));
        foreach (var player in new[] { game.Player1, game.Player2 })
        {
            text.Append("|player:").Append(Number(player.PlayerId)).Append(':').Append(Tags(player.NativeTags));
            foreach (var zone in new IEnumerable<SabberStoneCore.Model.Entities.IPlayable>[] {
                player.HandZone, player.DeckZone, player.BoardZone, player.SecretZone, player.GraveyardZone })
                text.Append('/').Append(string.Join(",", zone.Select(c => Number(c.Id))));
            if (player.Choice != null)
                text.Append("|choice:").Append(Number((int)player.Choice.ChoiceType)).Append(':')
                    .Append(string.Join(",", player.Choice.Choices.Select(Number)));
        }
#if ENGINE_SOURCE
        text.Append("|history:").Append(game.HistoryFacts.CanonicalText());
#endif
        return text.ToString();
    }
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Tags(IDictionary<SabberStoneCore.Enums.GameTag, int> tags) =>
        string.Join(",", tags.OrderBy(t => (int)t.Key).Select(t => Number((int)t.Key) + "=" + Number(t.Value)));
    public static string Hash(string text)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }
    public static double PersonalityRoll(int version, string matchId, int publicEventIndex, string purpose = "emote")
    {
        string key = version.ToString(CultureInfo.InvariantCulture) + ":" + Hash(matchId ?? "").Substring(0, 8)
            + ":" + publicEventIndex.ToString(CultureInfo.InvariantCulture) + ":" + purpose;
        return uint.Parse(Hash(key).Substring(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 4294967296.0;
    }
}
