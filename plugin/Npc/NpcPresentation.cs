using System;
using System.Collections.Generic;
using System.Linq;

// Value-only transport intent: NativeMatch supplies the pinned UserUI enum/packet mapping.
public sealed class NpcPresentationEvent
{
    public int PlayerId { get; }
    public string Emote { get; }
    public int? OverCard { get; }
    public int? PublicEventIndex { get; }
    public NpcPresentationEvent(int playerId, string emote = null, int? overCard = null, int? publicEventIndex = null)
    { PlayerId = playerId; Emote = emote; OverCard = overCard; PublicEventIndex = publicEventIndex; }
}

public sealed class NpcPresentation
{
    private readonly NpcProfile profile;
    private readonly NpcPresentationSettings settings;
    private readonly string matchId;
    private readonly int playerId;
    private readonly Func<double> clock;
    private readonly Func<bool> squelched;
    private readonly Func<NpcPresentationEvent, bool> send;
    // Rivals-lite memory (null = first meeting / no memory). On a repeat meeting it swaps WHICH MatchStart emote is sent,
    // 1:1 with the normal rules (same rolls, same timing: sent exactly when the normal rules would send). On the NPC's own
    // concession to a rival it picks the farewell. BotDoomed and every other trigger keep the normal rules, so memory never
    // adds waiting (WaitingForReaction) to the NPC's turns. No extra rolls: no personality stream is touched.
    private readonly string memoryGreeting;
    private readonly IReadOnlyList<string> beatRival;
    internal const double FollowUpMilliseconds = 1500;
    private int reactions, hovered, hoverIndex, replies, mocks;
    private string lastMockKind;
    private readonly Dictionary<int, int> uses = new Dictionary<int, int>();
    private readonly HashSet<string> observed = new HashSet<string>();
    private readonly List<PendingReaction> pending = new List<PendingReaction>();
    private bool cancelled, started, farewell, conceding;
    private double hoverUntil;
    private double lastReaction = double.NegativeInfinity, lastHover = double.NegativeInfinity;
    private sealed class PendingReaction
    {
        internal NpcReaction Trigger;
        internal int Index;
        internal string Detail;
        internal double Ready, Expires, IdleDelay;
        internal bool DelayAfterIdle, NpcLoses;
        internal string FollowUp;   // second emote of a rival farewell: same beat, so no gap/farewell gate
    }
    public NpcPresentation(NpcProfile profile, NpcPresentationSettings settings, string matchId, int playerId,
        Func<double> clock, Func<bool> squelched, Func<NpcPresentationEvent, bool> send, NpcMemory memory = null)
    {
        this.profile = profile; this.settings = settings ?? new NpcPresentationSettings(); this.matchId = matchId;
        this.playerId = playerId; this.clock = clock; this.squelched = squelched; this.send = send;
        memoryGreeting = NpcMemoryEmotes.Greeting(profile.Personality.Emotes, memory);
        beatRival = NpcMemoryEmotes.BeatRival(profile.Personality.Emotes, memory);
    }
    // npcLoses: a Concession reaction to the NPC's own concession (the player beat it), never the human's concession.
    public void Queue(NpcReaction reaction, int publicEventIndex, string detail = null, double delayMilliseconds = -1,
        bool npcLoses = false)
    {
        // Concession ends the conversation even when its farewell roll declines.
        if (reaction == NpcReaction.Concession) { pending.Clear(); conceding = true; }
        if (conceding && reaction != NpcReaction.Concession) return;
        var emotes = profile.Personality.Emotes;
        if (cancelled || !settings.Emotes || squelched() || reactions >= emotes.Budget
            || reaction == NpcReaction.ReplyToHumanEmote && replies >= 2
            || !emotes.Rules.Any(r => r.Trigger == reaction && (r.HumanEmote == null || r.HumanEmote == detail))) return;
        if (reaction == NpcReaction.MatchStart)
        {
            if (started) return;
            started = true;
        }
        string key = reaction + ":" + publicEventIndex + ":" + detail;
        if (!observed.Add(key)) return;
        // One pending reply prevents an emote burst from becoming a delayed conversation.
        if (reaction == NpcReaction.ReplyToHumanEmote && pending.Any(p => p.Trigger == reaction)) return;
        if (pending.Count >= 16) return;
        if (delayMilliseconds < 0)
            delayMilliseconds = 600 + 600 * MatchRecorder.PersonalityRoll(profile.Version, matchId, publicEventIndex,
                "reaction-delay:" + reaction + ":" + detail);
        pending.Add(new PendingReaction { Trigger = reaction, Index = publicEventIndex, Detail = detail,
            NpcLoses = npcLoses && reaction == NpcReaction.Concession,
            Ready = clock() + delayMilliseconds, IdleDelay = delayMilliseconds, DelayAfterIdle = true,
            Expires = reaction == NpcReaction.MatchStart ? double.PositiveInfinity
                : clock() + (reaction == NpcReaction.ReplyToHumanEmote ? delayMilliseconds + 5000 : 15000) });
    }
    // The second emote of a rival farewell is still waiting (NativeMatch holds the NPC's ConcedeTask for it, bounded).
    public bool FarewellFollowUpPending => !cancelled && pending.Any(p => p.FollowUp != null);
    public bool WaitingForReaction => clock() - lastReaction < 600 || pending.Any(p => p.Trigger != NpcReaction.MatchStart
        && p.Trigger != NpcReaction.ReplyToHumanEmote);
    public bool Tick(bool safe, bool humanTurn)
    {
        if (cancelled) return false;
        if (!settings.Emotes || squelched()) { pending.Clear(); return false; }
        pending.RemoveAll(p => p.Expires < clock() || humanTurn && p.Trigger != NpcReaction.MatchStart
            && p.Trigger != NpcReaction.ReplyToHumanEmote);
        if (!safe)
        {
            foreach (var item in pending) item.DelayAfterIdle = true;
            return false;
        }
        foreach (var item in pending.OrderByDescending(p => p.Trigger == NpcReaction.Concession || p.Trigger == NpcReaction.BotDoomed)
            .ThenBy(p => p.Ready).ToArray())
        {
            // Measure the beat from continuous client idle, never from the engine action timestamp.
            if (item.DelayAfterIdle) { item.Ready = clock() + item.IdleDelay; item.DelayAfterIdle = false; }
            if (clock() < item.Ready) continue;
            pending.Remove(item);
            if (item.FollowUp != null ? FollowUp(item) : React(item.Trigger, item.Index, item.Detail, item.NpcLoses)) return true;
        }
        return false;
    }
    private bool React(NpcReaction reaction, int publicEventIndex, string detail = null, bool npcLoses = false)
    {
        var p = profile.Personality.Emotes;
        if (cancelled || !settings.Emotes || squelched() || reactions >= p.Budget
            || clock() - lastReaction < p.MinGapMilliseconds
            || reaction == NpcReaction.ReplyToHumanEmote && replies >= 2
            || farewell && (reaction == NpcReaction.BotDoomed || reaction == NpcReaction.Concession)) return false;
        if (npcLoses && beatRival.Count > 0 && reaction == NpcReaction.Concession)
        {
            if (!SendMemory(beatRival[0], publicEventIndex)) return false;
            farewell = true;
            if (beatRival.Count > 1)
                pending.Add(new PendingReaction { Trigger = reaction, Index = publicEventIndex, FollowUp = beatRival[1],
                    IdleDelay = FollowUpMilliseconds, DelayAfterIdle = true, Ready = clock() + FollowUpMilliseconds,
                    Expires = clock() + 15000 });
            return true;
        }
        for (int i = 0; i < p.Rules.Count; i++)
        {
            var rule = p.Rules[i];
            if (rule.Trigger != reaction || rule.HumanEmote != null && rule.HumanEmote != detail
                || uses.TryGetValue(i, out int used) && used >= rule.MaxUses
                || rule.Mock && (mocks >= 2 || lastMockKind == (detail ?? reaction.ToString()))) continue;
            if (MatchRecorder.PersonalityRoll(profile.Version, matchId, publicEventIndex, "emote:" + reaction + ":" + i + ":" + detail) >= rule.Chance) continue;
            string emote = reaction == NpcReaction.MatchStart && memoryGreeting != null ? memoryGreeting : rule.Emote;
            if (!send(new NpcPresentationEvent(playerId, emote, publicEventIndex: publicEventIndex))) return false;
            uses[i] = uses.TryGetValue(i, out used) ? used + 1 : 1;
            reactions++; lastReaction = clock();
            if (reaction == NpcReaction.ReplyToHumanEmote) replies++;
            if (reaction == NpcReaction.BotDoomed || reaction == NpcReaction.Concession) farewell = true;
            if (rule.Mock) { mocks++; lastMockKind = detail ?? reaction.ToString(); }
            return true;
        }
        return false;
    }
    private bool FollowUp(PendingReaction item)
    {
        if (cancelled || !settings.Emotes || squelched() || reactions >= profile.Personality.Emotes.Budget) return false;
        return SendMemory(item.FollowUp, item.Index);
    }
    private bool SendMemory(string emote, int publicEventIndex)
    {
        if (!send(new NpcPresentationEvent(playerId, emote, publicEventIndex: publicEventIndex))) return false;
        reactions++; lastReaction = clock();
        return true;
    }
    public void Hover(IEnumerable<int> candidateSources, ISet<int> ownHandAndBoard, IEnumerable<int> ownHand = null) =>
        Hover(() => candidateSources, ownHandAndBoard, ownHand);
    // Lazy candidates: evaluated only once the hover gates open (the planner's leaders are ranked on demand, not per frame).
    public void Hover(Func<IEnumerable<int>> candidateSources, ISet<int> ownHandAndBoard, IEnumerable<int> ownHand = null)
    {
        if (cancelled || !settings.Hover || clock() - lastHover < 600) return;
        if (ownHand != null && clock() < hoverUntil) return;
        var cards = candidateSources().Where(id => id > 0 && ownHandAndBoard.Contains(id)).Distinct().Take(3).ToList();
        if (ownHand != null)
        {
            if (cards.Count < 2)
                cards = cards.Concat(ownHand.Where(id => id > 0 && ownHandAndBoard.Contains(id))).Distinct().Take(3).ToList();
        }
        int next = cards.FirstOrDefault();
        if (ownHand != null && cards.Count > 0) next = cards[(cards.IndexOf(hovered) + 1) % cards.Count];
        if (next == hovered) return;
        if (send(new NpcPresentationEvent(playerId, overCard: next)))
        {
            hovered = next; lastHover = clock();
            hoverUntil = lastHover + 700 + 800 * MatchRecorder.PersonalityRoll(profile.Version, matchId, hoverIndex++, "hover");
        }
    }
    public void ClearHover()
    {
        if (hovered == 0) return;
        send(new NpcPresentationEvent(playerId, overCard: 0)); hovered = 0;
    }
    public double ActedCardDwell(int source, ISet<int> ownHandAndBoard)
    {
        if (cancelled || !settings.Hover || source <= 0 || !ownHandAndBoard.Contains(source)) return 0;
        Hover(new[] { source }, ownHandAndBoard);
        return hovered == source ? Math.Max(0, 300 - (clock() - lastHover))
            : Math.Max(0, 600 - (clock() - lastHover)) + 300;
    }
    public void Cancel() { ClearHover(); pending.Clear(); cancelled = true; }
    public string HeroCardId(int entityId, int botHeroId, string cardId) =>
        settings.Skin && entityId == botHeroId && !string.IsNullOrEmpty(profile.HeroSkin) ? profile.HeroSkin : cardId;
    public int CardBack(int seat, int original) => settings.CardBack && seat == playerId
        ? Math.Max(0, settings.CardBackOverride ?? profile.CardBack) : original;

}
