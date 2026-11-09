using System;
using System.Collections.Generic;
using System.Linq;

// WP4. Process-wide world state for the live plugin and the harness. Installs NpcWorldHooks; nothing else in the
// codebase mutates the hooks. Not installed => every seam behaves exactly as before (golden catalog only).
public static class NpcWorldRuntime
{
    public static NpcWorld World { get; private set; }
    public static IMatchmaker Matchmaker { get; private set; }
    public static NpcLegendField Legend { get; private set; }
    // The encounter of the match being started/played; set by LocalLadder.StartSelected, cleared on settle/abandon.
    public static NpcEncounter Active { get; private set; }
    // #37 Standard Casual queue; null when no Casual catalog loaded (Casual then plays legacy opponents, still unranked).
    public static CasualMatchmaker Casual { get; private set; }

    private static readonly object planGate = new object();
    private static readonly Dictionary<string, DeckPlan> plans = new Dictionary<string, DeckPlan>(StringComparer.Ordinal);
    private static Func<string, LocalDeck> deckResolver;
    private static bool clientSeams;

    // Client seam (set by InstallClientSeams): NPC id of the saved, unsettled match that plays this deck id, else null.
    // It pins that deck (served even if a later library un-admits it) and names it after the NPC while Active is unset.
    public static Func<string, string> PendingNpcForDeck { get; private set; }

    public static bool Installed { get { return World != null; } }

    public static void Install(NpcWorld world, IMatchmaker matchmaker)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (matchmaker == null) throw new ArgumentNullException(nameof(matchmaker));
        World = world; Matchmaker = matchmaker; Active = null; Casual = null;
        Legend = (matchmaker as LadderMatchmaker)?.Legend ?? new NpcLegendField(world);
        lock (planGate) plans.Clear();
        deckResolver = world.Catalogs.Decks.LocalDeckResolver(NameForDeck, true, IsReferencedDeck);
        NpcWorldHooks.ProfileForDeck = ProfileForDeck;
        NpcWorldHooks.PlanForId = PlanForId;
        NpcWorldHooks.DecoratePilot = DecoratePilot;
        NpcWorldHooks.MulliganFor = MulliganFor;
        // Lazy: built on first use by an inferred-prior brain only, so boot never depends on it.
        var population = new Lazy<IReadOnlyList<PopulationDeck>>(() => world.Catalogs.Decks.Population());
        NpcWorldHooks.Population = () => population.Value;
    }

    // The Casual catalog's decks must already be in World.Catalogs.Decks (CasualDeckCatalog.MergeInto): plans and
    // LocalDecks.Get resolve them there, served only while an encounter references them (admission "casual").
    public static void InstallCasual(CasualDeckCatalog catalog)
    {
        if (World == null) throw new InvalidOperationException("NpcWorldRuntime.Install must run first.");
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        var missing = catalog.Decks.FirstOrDefault(d => !ReferenceEquals(World.Catalogs.Decks.Find(d.Id), d));
        if (missing != null) throw new InvalidOperationException("Casual deck not merged into the world library: " + missing.Id);
        Casual = new CasualMatchmaker(World, catalog, Legend);
    }

    // Tests and failed boot: back to the golden-only behaviour.
    public static void Uninstall()
    {
        NpcWorldHooks.Reset();
        if (clientSeams)
        {
            LocalDecks.ExternalDecks = null;
            PlaytestQueue.WorldProfileResolver = null;
            clientSeams = false;
        }
        PendingNpcForDeck = null; DecorationLog = null;
        World = null; Matchmaker = null; Legend = null; Active = null; Casual = null; deckResolver = null;
        lock (planGate) plans.Clear();
    }

    // The live client's seams on top of Install's hooks: S5 (LocalDecks.ExternalDecks) and S10 (playtest world ids).
    // The harness sets LocalDecks.ExternalDecks itself and never calls this.
    public static void InstallClientSeams(Func<string, string> pendingNpcForDeck)
    {
        if (World == null) throw new InvalidOperationException("NpcWorldRuntime.Install must run first.");
        PendingNpcForDeck = pendingNpcForDeck;
        LocalDecks.ExternalDecks = DeckForId;
        PlaytestQueue.WorldProfileResolver = PlaytestProfile;
        clientSeams = true;
    }

    // Seam S11 body (plugin NpcWorldBoot.TryInstall): generate, install hooks and client seams, then check that an admitted
    // library deck resolves through LocalDecks.Get and builds exactly as the native side will use it. Any failure
    // uninstalls everything, so ladder play keeps LocalDecks.NextOpponent and playtests keep the golden ids.
    public static bool TryInstallClient(Func<NpcWorld> generate, Func<string, string> pendingNpcForDeck, Action<string> log,
        CasualDeckCatalog casual = null)
    {
        try
        {
            if (generate == null) throw new ArgumentNullException(nameof(generate));
            var world = generate();
            Install(world, new LadderMatchmaker(world));
            InstallClientSeams(pendingNpcForDeck);
            DecorationLog = RateLimited(log);
            // Prefer a library-only deck: golden decks also live in LocalDecks.NpcDecks and would not exercise the seam.
            var admitted = world.Catalogs.Decks.Admitted.ToList();
            var sample = admitted.FirstOrDefault(d => !LocalDecks.NpcDecks.Any(deck => deck.Id == d.Id)) ?? admitted.FirstOrDefault()
                ?? throw new InvalidOperationException("The NPC deck library has no admitted deck.");
            LocalDecks.Get(sample.Id).Build();
            if (casual != null) TryInstallCasual(casual, log);
            Say(log, "npc-world installed: " + Summary());
            return true;
        }
        catch (Exception failure)
        {
            try { Uninstall(); } catch (Exception) { }
            Say(log, "npc-world boot failed; using legacy opponents: " + failure);
            return false;
        }
    }

    // A broken Casual catalog never blocks ranked play: a deck that collided with a ranked id or does not build is dropped
    // alone; with none left, Casual falls back to legacy opponents (still unranked).
    internal static void TryInstallCasual(CasualDeckCatalog casual, Action<string> log)
    {
        try
        {
            InstallCasual(casual.Keep(deck => {
                if (!ReferenceEquals(World.Catalogs.Decks.Find(deck.Id), deck)) { Say(log, "npc-world casual deck dropped (id taken): " + deck.Id); return false; }
                try { NpcDeckLibrary.ToLocalDeck(deck, deck.Name).Build(); return true; }
                catch (Exception error) { Say(log, "npc-world casual deck dropped: " + deck.Id + ": " + error.Message); return false; }
            }));
        }
        catch (Exception failure) { Casual = null; Say(log, "npc-world casual queue unavailable: " + failure.Message); }
    }

    // One boot line: NPC count, admitted decks, Legend field size (match index 0).
    public static string Summary()
    {
        var world = World;
        if (world == null) return "not installed";
        var decks = world.Catalogs.Decks;
        return world.All.Count + " NPCs (" + world.All.Count(n => n.Notable) + " notables), decks admitted "
            + decks.Admitted.Count() + "/" + decks.Decks.Count + ", Legend field " + (Legend?.Field(0).Count ?? 0)
            + ", casual decks " + (Casual?.Catalog.Decks.Count ?? 0);
    }

    private static void Say(Action<string> log, string message)
    {
        try { log?.Invoke(message); } catch (Exception) { }
    }

    // Host log wrapper for the world seams (hook failures can repeat every frame or every lookup): each distinct message
    // is written at most perMessage times, then one "suppressed" line; after total lines one final cap line, then nothing.
    public static Action<string> RateLimited(Action<string> sink, int perMessage = 3, int total = 200)
    {
        if (sink == null) return null;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        int written = 0;
        return message =>
        {
            string line = null;
            lock (seen)
            {
                string key = message == null ? "" : message.Length > 200 ? message.Substring(0, 200) : message;
                int count;
                seen.TryGetValue(key, out count);
                seen[key] = ++count;
                if (written > total) return;
                if (written == total) line = "npc-world log cap reached (" + total + " lines); further messages suppressed";
                else if (count <= perMessage) line = message;
                else if (count == perMessage + 1) line = "(repeated; further copies suppressed) " + message;
                if (line != null) written++;
            }
            if (line != null) Say(sink, line);
        };
    }

    // Experimental Options switch (NativeDeckInferenceOption; null in the harness): the NPC reads the player's deck by fair
    // inference for matches begun while it returns true. Off, Begin keeps the encounter object itself: the shipping search.
    public static Func<bool> InferredReplyPrior { get; set; }

    public static void Begin(NpcEncounter encounter)
    {
        if (encounter == null) throw new ArgumentNullException(nameof(encounter));
        bool inferred = InferredReplyPrior?.Invoke() == true;
        // Broken inference lists must not refuse the match: it runs the shipping prior, and NPC_REPLY_PRIOR says so.
        try { if (InferredReplyPrior != null) DeckInference.NewMatch(inferred); }
        catch (Exception error)
        {
            inferred = false;
            Say(DecorationLog, "npc deck inference unavailable; class prior for this match: " + error.GetType().Name + ": " + error.Message);
        }
        Active = inferred ? encounter.WithReplyPrior(ReplyPrior.Inferred) : encounter;
    }

    public static void End() { Active = null; }

    // Legend number of a rating at the given match count; null while no world is installed.
    public static int? LegendNumber(double rating, int matchIndex) =>
        Legend == null ? (int?)null : Legend.LegendNumber(rating, matchIndex);

    // An NPC opponent's Legend number, counting a Legend player (playerRating) beside it; null while no world is installed.
    public static int? OpponentLegendNumber(double rating, int matchIndex, double? playerRating) =>
        Legend == null ? (int?)null : Legend.OpponentLegendNumber(rating, matchIndex, playerRating);

    // Hook bodies (NpcWorldHooks.*). Only the Active encounter's deck resolves to a world profile.
    internal static NpcProfile ProfileForDeck(string deckId)
    {
        var active = Active;
        return active != null && active.Profile != null && active.Deck != null && active.Deck.Id == deckId ? active.Profile : null;
    }

    // "auto:<deckId>" resolves to the derived plan (overlaid by the deck's hand-written plan when it has one);
    // a non-auto id that some library deck declares as its hand-written PlanId resolves to the derived plan too.
    internal static DeckPlan PlanForId(string planId)
    {
        var world = World;
        if (world == null || planId == null) return null;
        lock (planGate)
        {
            DeckPlan plan;
            if (plans.TryGetValue(planId, out plan)) return plan;
            NpcDeckEntry deck; bool auto = AutoDeckPlan.IsAutoPlanId(planId);
            if (auto) deck = world.Catalogs.Decks.Find(planId.Substring(AutoDeckPlan.Prefix.Length));
            else deck = world.Catalogs.Decks.Decks.FirstOrDefault(d => d.PlanId == planId);
            if (deck == null) return null;
            plan = AutoDeckPlan.Derive(deck);
            // Static catalog only (DeckPlans.All): ForId would call back into this hook.
            var handWritten = auto && deck.PlanId != null ? DeckPlans.All.SingleOrDefault(p => p.Id == deck.PlanId) : null;
            if (handWritten != null) plan = AutoDeckPlan.Overlay(plan, handWritten);
            plans[planId] = plan;
            return plan;
        }
    }

    // Wraps the golden pilot with the encounter's skill. Elite tiers (no mistakes, no softmax) keep the plain pilot.
    // SkilledPilot rebuilds its own variants (no-reply, no-lethal) from the inner GoldenPilot, so no extra pilot is built
    // here (a ReplyK=0 GoldenPilot throws in CardFrequencies). Live 2026-10-25: a decorator that cannot run must never
    // drop the NPC to the legacy LocalBot; GuardedSkilledPilot falls back to the undecorated pilot instead.
    internal static IGoldenPilot DecoratePilot(NpcProfile profile, IGoldenPilot pilot)
    {
        var active = Active;
        if (active == null || active.Skill == null || active.Profile == null || profile == null || profile.Id != active.Profile.Id) return pilot;
        var skill = active.Skill;
        if (skill.Mistakes.Count == 0 && skill.Temperature <= 0) return pilot;
        var inner = pilot as IFrameSlicedGoldenPilot;
        if (inner == null) return pilot;
        IFrameSlicedGoldenPilot skilled;
        try { skilled = new SkilledPilot(inner, null, skill, active.MatchSeed); }
        catch (Exception error)
        {
            Say(DecorationLog, "npc=" + profile.Id + " skill decorator unavailable; plain golden pilot: " + error.GetType().Name + ": " + error.Message);
            return pilot;
        }
        return new GuardedSkilledPilot(profile.Id, skilled, inner, DecorationLog);
    }

    // Host log for DecoratePilot fallbacks (the live plugin sets it at boot; null in the harness).
    public static Action<string> DecorationLog { get; set; }

    internal static MulliganPolicy MulliganFor(NpcProfile profile, DeckPlan plan)
    {
        var active = Active;
        if (active == null || active.Skill == null || active.Profile == null || profile == null || profile.Id != active.Profile.Id) return null;
        return active.Skill.Chance(MistakeKind.GreedyMulligan) > 0 ? new SkilledMulligan(plan, active.Skill, active.MatchSeed) : null;
    }

    // Milestone "Challengers: band" grants (IMilestoneContext.ChallengersFor): notable NPC ids of the reached band,
    // strongest first, at most ChallengersPerMilestone. Milestone ids: "floor:<league>-<n>" or "legend".
    public const int ChallengersPerMilestone = 3;
    public static IReadOnlyList<string> ChallengersFor(string milestoneId)
    {
        var world = World;
        LadderBand band;
        if (world == null || !TryBand(milestoneId, out band)) return Array.AsReadOnly(new string[0]);
        return Array.AsReadOnly(world.All.Where(npc => npc.Notable && NpcWorld.BandForRating(npc.HomeRating) == band)
            .OrderByDescending(npc => npc.HomeRating).ThenBy(npc => npc.Id, StringComparer.Ordinal)
            .Take(ChallengersPerMilestone).Select(npc => npc.Id).ToArray());
    }

    private static bool TryBand(string milestoneId, out LadderBand band)
    {
        band = LadderBand.Bronze;
        if (milestoneId == "legend") { band = LadderBand.Legend; return true; }
        const string prefix = "floor:";
        if (milestoneId == null || !milestoneId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int dash = milestoneId.IndexOf('-', prefix.Length);
        string league = dash < 0 ? milestoneId.Substring(prefix.Length) : milestoneId.Substring(prefix.Length, dash - prefix.Length);
        return Enum.TryParse(league, true, out band) && Enum.IsDefined(typeof(LadderBand), band) && band != LadderBand.Legend;
    }

    // LocalDecks.Get seam (LocalDecks.ExternalDecks): admitted library decks by id, plus the Active or pending match's deck
    // whatever its admission, named for that encounter's NPC (the native name plate reads LocalDeck.Opponent).
    public static LocalDeck DeckForId(string deckId)
    {
        var resolver = deckResolver;
        return resolver == null || deckId == null ? null : resolver(deckId);
    }

    private static bool IsReferencedDeck(string deckId)
    {
        var active = Active;
        if (active != null && active.Deck != null && active.Deck.Id == deckId) return true;
        return PendingNpc(deckId) != null;
    }

    private static string NameForDeck(string deckId)
    {
        var active = Active;
        if (active != null && active.Deck != null && active.Deck.Id == deckId && active.Npc != null) return active.Npc.DisplayName;
        string npcId = PendingNpc(deckId);
        var world = World;
        return npcId == null || world == null ? null : world.Find(npcId)?.DisplayName;
    }

    private static string PendingNpc(string deckId)
    {
        var pending = PendingNpcForDeck;
        if (pending == null) return null;
        try { return pending(deckId); } catch (Exception) { return null; }
    }

    // Seam S10 body (PlaytestQueue.WorldProfileResolver): a world NPC (npc.n.* / npc.g.*) plays a playtest with its first
    // admitted deck, its skill tier and its personality, built exactly as the matchmaker builds a ladder encounter.
    public static bool IsPlaytestWorldId(string id) => id != null
        && (id.StartsWith("npc.n.", StringComparison.Ordinal) || id.StartsWith("npc.g.", StringComparison.Ordinal));

    public static NpcProfile PlaytestProfile(string npcId)
    {
        var world = World;
        if (world == null || !IsPlaytestWorldId(npcId)) return null;
        var npc = world.Find(npcId);
        if (npc == null) return null;
        var deck = npc.DeckIds.Select(id => world.Catalogs.Decks.Find(id)).FirstOrDefault(d => d != null && d.Admitted);
        SkillProfile skill; PersonalityArchetype personality;
        if (deck == null || !TryTraits(world, npc, out skill, out personality)) return null;
        return NpcEncounterProfiles.Build(npc, deck, personality, skill);
    }

    // S7 playtest path: the encounter around the knob's own profile object (so NpcCatalog.ForDeck returns exactly it),
    // against the NPC's drifted rating at this match index. Null when the profile is not a world NPC's (golden ids).
    public static NpcEncounter PlaytestEncounter(NpcProfile profile, int matchIndex, int matchSeed)
    {
        var world = World;
        if (world == null || profile == null || !IsPlaytestWorldId(profile.Id)) return null;
        var npc = world.Find(profile.Id);
        var deck = world.Catalogs.Decks.Find(profile.DeckId);
        SkillProfile skill; PersonalityArchetype personality;
        if (npc == null || deck == null || !deck.Admitted || !npc.DeckIds.Contains(deck.Id) || !TryTraits(world, npc, out skill, out personality))
            return null;
        double rating = Legend != null ? Legend.DriftedRating(npc, Math.Max(0, matchIndex)) : npc.HomeRating;
        return new NpcEncounter(npc, deck, skill, personality, profile, rating, matchSeed & int.MaxValue, "playtest");
    }

    // Tier/personality lookup as in LadderMatchmaker.Choose (generation always assigns SkillId; the rating fallback
    // goes through NpcPopulationRules.SkillFor because SkillCatalog.ForRating may still be WP2's stub).
    private static bool TryTraits(NpcWorld world, NpcIdentity npc, out SkillProfile skill, out PersonalityArchetype personality)
    {
        skill = npc.SkillId != null ? world.Catalogs.Skills.Find(npc.SkillId) : NpcPopulationRules.SkillFor(world.Catalogs.Skills, npc.HomeRating);
        personality = world.Catalogs.Personalities.Find(npc.PersonalityId);
        return skill != null && personality != null;
    }
}
