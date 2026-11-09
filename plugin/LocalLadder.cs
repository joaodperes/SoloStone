using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SabberStoneCore.Enums;
using UnityEngine;

internal static class LocalLadder
{
    private static LocalProfile profile;
    private static LocalCollectionUI collectionUi;
    private static string screen = "Loading", error, terminalOutcome;
    private static LocalMatchRecord active, result;
    private static LocalRewardFacts terminalFacts;
    private static LocalPetXpCounts terminalPetXp;
    private static LocalAchievementMatchEvidence terminalAchievementEvidence;
    private static int terminalTurns;
    private static bool conceded, nativeFinished;
    private static PlaytestQueue playtest;
    private static string opponentLabel;   // world encounters: "<NPC name> (3-1)"; null = the legacy deck label
    public static string Screen => screen;
    internal static LocalProfile Profile => profile;
    public static string MatchId => active?.Id ?? "";
    internal static string RewardSaveError => screen == "Result" && result == null ? error : null;

    public static void Initialize(LocalProfile selectedProfile)
    {
        if (profile != null) throw new InvalidOperationException("A save is already bound.");
        profile = selectedProfile;
        collectionUi = new LocalCollectionUI(profile);
    }

    internal static void ResetStartup(LocalProfile selectedProfile)
    {
        if (!ReferenceEquals(profile, selectedProfile) || screen != "Loading") return;
        profile = null;
        collectionUi = null;
    }

    public static void Ready() { if (screen == "Loading") screen = "Menu"; }
    public static void NativeStarted() { screen = "Playing"; }

    // The engine game exists: make that durable so a crash from here on is abandoned, not cancelled. Best effort.
    internal static void EngineStarted(Action<string> log, int firstPlayer)
    {
        try { if (active != null && profile?.Pending?.Id == active.Id) { profile.MarkStarted(active.Id, firstPlayer); active.FirstPlayer = firstPlayer; } }
        catch (Exception failure) { log("LOCAL_MARK_STARTED_FAILED " + failure.Message); }
    }

    // The staged match never produced an engine game (stage timeout, failed engine start): clear its Pending before the
    // app quits so the next boot does not treat it as an interrupted match. Best effort; recovery also cancels it.
    internal static void CancelUnstartedStage(Action<string> log)
    {
        try
        {
            if (active == null || profile?.Pending?.Id != active.Id) return;
            profile.CancelUnstarted(active.Id);
            active = null;
            screen = "Menu";
        }
        catch (Exception failure) { log("LOCAL_CANCEL_UNSTARTED_FAILED " + failure.Message); }
    }
    public static void NativeFinished() { nativeFinished = true; }
    public static void MarkConcede() { conceded = true; }
    public static void Shutdown() { profile?.Dispose(); }

    internal static void RetryRewardSave() { if (screen == "Result" && result == null) error = null; }
    internal static void RewardPresentationComplete()
    {
        screen = "Menu"; active = null; result = null; terminalFacts = null; terminalPetXp = null; terminalOutcome = null;
        terminalAchievementEvidence = null;
        terminalTurns = 0; conceded = false; nativeFinished = false; error = null;
        opponentLabel = null; NpcWorldRuntime.End();
    }

    public static void Observe(NativeMatch match, Action<string> diagnostic = null)
    {
        if (match?.Game == null || match.Game.State != State.COMPLETE || active == null) return;
        if (terminalFacts == null)
        {
            var outcome = MatchOutcome.From(match.Game, conceded);
            terminalOutcome = outcome.Result;
            terminalTurns = outcome.Turns;
            conceded = outcome.Conceded;
            LocalRewardFacts facts = match.RewardFacts;
            // The match is over: no treat or like can still change the counters.
            terminalPetXp = LocalProfile.ClampPetXp(match.PetXpCounts, active.Pet, out bool petXpClamped);
            if (petXpClamped)
                try { diagnostic?.Invoke("WARNING LOCAL_PET_XP_CLAMPED match=" + active.Id + " small=" + match.PetXpCounts.Small
                    + " medium=" + match.PetXpCounts.Medium + " large=" + match.PetXpCounts.Large + " (pet XP never blocks settlement)"); } catch { }
            terminalFacts = new LocalRewardFacts {
                DistinctCardsPlayed = facts.DistinctCardsPlayed,
                EnemyMinionsDefeated = facts.EnemyMinionsDefeated,
                MinimumHealth = facts.MinimumHealth
            };
        }
        if (active.DebugScenario)
        {
            FinishDebugScenario();
            return;
        }
        if (result == null && error == null)
        {
            try
            {
                if (terminalOutcome == null) throw new InvalidOperationException("Engine has no terminal player result.");
                // World encounters settle against the NPC's drifted rating stored on the pending record (null for legacy
                // opponents: the player's own rating). An NPC concession is recorded only on a win the player did not concede.
                bool? npcConceded = terminalOutcome == "Won" && !conceded && match.OpponentConceded ? true : (bool?)null;
                if (active.AchievementContext != null && terminalAchievementEvidence == null)
                {
                    terminalAchievementEvidence = match.AchievementEvidence(active.AchievementContext);
                    if (terminalAchievementEvidence?.EvidenceInvalid == true)
                        try { diagnostic?.Invoke("WARNING LOCAL_ACHIEVEMENT_EVIDENCE_INVALID match=" + active.Id
                            + " (no achievements this match): " + terminalAchievementEvidence.InvalidReason); } catch { }
                }
                result = profile.Settle(active.Id, terminalOutcome, terminalTurns, conceded, terminalFacts, active.NpcRating,
                    npcConceded, terminalAchievementEvidence, terminalPetXp);
            }
            catch (Exception failure) { error = "Result could not be saved: " + failure.Message; try { diagnostic?.Invoke("LOCAL_SETTLEMENT_FAILED " + failure.GetType().Name + "\n" + failure.StackTrace); } catch { } }
        }
        if (nativeFinished && screen == "Playing") screen = "Result";
    }

    // A debug scenario match (DebugScenario.cs) ends without a result screen or any reward: once the native end-of-game screen
    // has been asked for (or after a bounded wait), Settle drops the pending record (its central debug gate; nothing else
    // changes) and the finished-match router returns to the deck picker, as after an acknowledged result.
    private static float scenarioDoneAt = -1;
    private static void FinishDebugScenario()
    {
        if (error != null) return;
        if (scenarioDoneAt < 0) scenarioDoneAt = Time.realtimeSinceStartup;
        if (!nativeFinished && Time.realtimeSinceStartup - scenarioDoneAt < 20) return;
        try
        {
            profile.Settle(active.Id, terminalOutcome ?? "Lost", terminalTurns, conceded, terminalFacts, active.NpcRating);
            Debug.Log("DEBUG_SCENARIO finished match=" + active.Id + " result=" + terminalOutcome + " (no progression)");
            RewardPresentationComplete();
        }
        catch (Exception failure)
        {
            // The gate's own commit failed: drop the scenario pending record the same way recovery does, so it can never
            // block new starts until a restart. Only if that fails too does the error stay up.
            Debug.LogWarning("DEBUG_SCENARIO settle failed (" + failure.Message + "); dropping the scenario pending record");
            try
            {
                if (profile.Pending?.DebugScenario == true) profile.RecoverInterrupted();
                error = null;
                RewardPresentationComplete();
            }
            catch (Exception second) { error = "Debug scenario could not be closed: " + second.Message; Debug.LogError(error); }
        }
        scenarioDoneAt = -1;
    }

    private static void Start()
    {
        try
        {
            bool personal = collectionUi.SelectedDeck.StartsWith("custom:", StringComparison.Ordinal);
            StartSelected(collectionUi.SelectedDeck, collectionUi.ResolveSelectedDeck(), personal);
        }
        catch (Exception failure) { error = "Match could not start: " + failure.Message; collectionUi.SetError(error); screen = "Menu"; }
    }

    internal static void StartSelected(string selected, LocalDeck resolved) => StartSelected(selected, resolved,
        selected != null && selected.StartsWith("custom:", StringComparison.Ordinal));

    // casual: Standard Casual (#37): a Casual NPC queue and no ladder accounting (LocalMatchRecord.Mode).
    // rematch (#28) is Ranked-only: it replays a Ranked match against the ladder gate, so rematch with casual refuses the start.
    internal static void StartSelected(string selected, LocalDeck resolved, bool personal, bool nativeMatchmaking = false,
        bool rematch = false, bool casual = false)
    {
        if (screen == "Starting" || screen == "Playing" || profile.Pending != null) return;
        try
        {
            // Every check that can refuse runs first: Begin commits Pending only for a match that is ready to stage.
            if (rematch && casual) throw new InvalidOperationException("Rematch unavailable: " + LocalRematch.CasualRefusal);
            IReadOnlyList<LocalDeckCard> entries = NativeUIProjection.ValidateStart(profile, selected, resolved, personal);
            // Presentation only: a mismatch shows Normal cards, never refuses.
            IReadOnlyList<LocalDeckCard> premiumEntries = personal
                ? NativeMatch.PremiumEntries(resolved.CardIds, entries, message => Debug.LogWarning(message)) : null;
            // A rematch names its opponent: the playtest knob stays queued for the next normal start.
            playtest = rematch ? null : PlaytestQueue.Read(log: message => Debug.LogWarning(message));
#if STANDARD_PROOF
            bool proof = StandardProofNative.IsProof(selected);
            LocalDeck opponent = proof ? LocalDecks.Get(StandardProofNative.Opponent(selected))
                : LocalDecks.NextOpponent(profile.RankedHistory.Select(match => match.Opponent));
            int seed = proof ? 0 : Guid.NewGuid().GetHashCode() & int.MaxValue;
#else
            LocalDeck opponent = LocalDecks.OpponentFor(selected, profile.RankedHistory.Select(match => match.Opponent));
            int seed = Guid.NewGuid().GetHashCode() & int.MaxValue;
#endif
#if STANDARD_PROOF
            if (proof) playtest = null;
#endif
            // Seam S7: world matchmaker for plain ladder play. The playtest knob still overrides it, proof and test-fixture
            // selections keep their fixed opponents, and a missing/failed world keeps today's LocalDecks.NextOpponent.
            NpcEncounter encounter = null;
            LocalDeck worldDeck = null;
            NpcWorldRuntime.End();
            opponentLabel = null;
            if (playtest != null)
            {
                seed = playtest.SelectSeed(seed);
                // A world-id knob (npc.n.* / npc.g.*) plays as an encounter around the knob's own profile, so the S1/S5
                // hooks resolve that profile and its library deck in NativeMatch. Begin before LocalDecks.Get names it.
                encounter = TryPlaytestEncounter(playtest.Profile, seed);
                if (encounter != null) { NpcWorldRuntime.Begin(encounter); opponentLabel = encounter.Npc.DisplayName; }
                opponent = playtest.Opponent(false);
            }
            else if (rematch) encounter = RematchEncounter(selected, out worldDeck);
            else
            {
#if STANDARD_PROOF
                if (!proof)
#endif
                    encounter = casual ? TryCasualEncounter(selected, out worldDeck) : TryWorldEncounter(selected, out worldDeck);
            }
            if (encounter != null)
            {
                if (playtest == null) opponent = worldDeck;   // resolved and validated inside TryWorldEncounter
                // Before PrepareLocalMatch: NativeMatch resolves the world NPC's profile and library deck through S1/S5.
                NpcWorldRuntime.Begin(encounter);
            }
            var prepared = ClientBridge.PrepareLocalMatch(selected, opponent.Id, seed, profile.Stars, personal ? resolved : null, premiumEntries);
            // Rivals-lite: the NPC's memory from SETTLED history only (no Pending exists yet: refused above). Every world
            // encounter, ladder or a playtest knob naming a world NPC id; fixed-deck playtests have no encounter and no memory.
            // Presentation only (name plate, emote choice), set before the match starts.
            if (encounter != null)
                prepared.Match.NpcMemory = NativeMatch.MemoryFrom(profile.NpcRecord(encounter.Npc.Id));
            var queued = playtest;
            // One-shot debug scenario (engine-source builds only; null elsewhere): armed on the pending record below so the
            // match can never settle. Bound read failures throw before BeginLaunch for ranked, casual, playtest and rematch.
            // The file is consumed once the match is staged (inside the launch callback).
            DebugScenario scenario = DebugScenario.Read(null, message => Debug.LogWarning(message), ClientBridge.ClientKnowsCard,
                DebugScenario.RunId, NativeTestControl.DiagnosticsEnabled);
            LocalAchievementPendingContext achievementContext = encounter != null && playtest == null && scenario == null && NativeMatch.SupportsAchievementEvidence
                ? profile.AchievementContextFor(resolved, personal, encounter.Npc.Id, encounter.Rating,
                    (casual ? CasualAchievementBand(encounter.Rating) : NpcWorld.BandForStarIndex(profile.Ladder.StarIndex)).ToString(), premiumEntries, personal ? profile.GetDraft(long.Parse(selected.Substring(7))).Sideboards : null) : null;
            // A launch that throws after Begin clears its unstarted Pending atomically: no ladder change, no trapped save.
            // Test-control lanes only: a one-shot pet XP pacing percent frozen into this match's pending record (PetPacingKnob).
            if (PetPacingKnob.Take(PetPacingKnob.Path, NativeTestControl.DiagnosticsEnabled, PetPacingKnob.RunId, message => Debug.LogWarning(message)) is int pacing)
                profile.ArmPetPacing(pacing);
            if (scenario != null) {
                profile.ArmDebugScenario();
                if (scenario.FirstPlayer.HasValue) profile.ArmFirstPlayer(scenario.FirstPlayer.Value);
            }
            try {
            profile.BeginLaunch(selected, opponent.Id, seed, encounter, achievementContext, casual ? CasualRules.Mode : null, record => {
                // Stage first: a refusal throws before any displayed state (a previous result, the active id) changes.
                ClientBridge.StartLocalMatch(record, prepared, queued, nativeMatchmaking, scenario);
                active = record;
                result = null;
                terminalFacts = null;
                terminalPetXp = null;
                terminalAchievementEvidence = null;
                terminalOutcome = null;
                conceded = false;
                nativeFinished = false;
                error = null;
                collectionUi.ClearError();
                screen = "Starting";
            });
            } finally { profile.DisarmDebugScenario(); }
        }
        catch (Exception failure)
        {
            // A refusal leaves the prior displayed result and active id untouched (the launch lambda stages first).
            NpcWorldRuntime.End();
            opponentLabel = null;
            error = "Match could not start: " + failure.Message;
            collectionUi.SetError(error);
            screen = "Menu";
        }
    }

    // Ladder selections only: test loaners and showcase decks keep their fixed opponents.
    // Showcase and loaner decks are ordinary player decks on the ladder, so they meet world NPCs too; only automated
    // test fixtures keep their fixed opponents.
    private static bool IsWorldSelection(string selected) =>
        !string.IsNullOrEmpty(selected) && !selected.StartsWith("test-", StringComparison.Ordinal);

    // Matchmaker inputs are the player's ladder state and match history only (opponent ids, deck ids, results):
    // never the selected deck, hand or collection.
    // Everything that can fail for a given pick (matchmaker, deck resolution, deck validity, id shape) happens here, so a bad
    // pick falls back to the legacy opponent instead of failing the same way on every retry.
    private static NpcEncounter TryWorldEncounter(string selected, out LocalDeck deck)
    {
        deck = null;
        var matchmaker = NpcWorldRuntime.Matchmaker;
        if (matchmaker == null || !IsWorldSelection(selected)) return null;
        try
        {
            // The ladder queue reads ranked history only; the head-to-head label (ResolveWorldDeck) counts every mode.
            var matches = profile.RankedHistory;
            int depth = (matchmaker as LadderMatchmaker)?.Rules.HistoryDepth ?? 20;
            var request = LocalRematch.Request(profile, matches, NpcWorldRuntime.World.Catalogs.Decks, depth, NpcWorldRuntime.Legend);
            NpcEncounter encounter = matchmaker.Next(request);
            deck = ResolveWorldDeck(encounter);
            return encounter;
        }
        catch (Exception failure)
        {
            Debug.LogWarning("NPC world matchmaker failed; using the legacy opponent: " + failure.Message);
            opponentLabel = null; deck = null;
            return null;
        }
    }

    // A ladder or rematch encounter's deck; the head-to-head label counts every mode (Ranked and Casual).
    private static LocalDeck ResolveWorldDeck(NpcEncounter encounter)
    {
        string deckId = encounter.Deck.Id;
        if (string.IsNullOrWhiteSpace(deckId) || deckId.Length > 64 || deckId.Any(char.IsControl))
            throw new InvalidOperationException("World deck id is not storable: " + deckId);
        LocalDeck resolved = LocalDecks.Get(deckId);   // the same lookup the native side uses (ExternalDecks seam)
        resolved.Build();                              // throws when the deck is not playable
        opponentLabel = OpponentLabel(encounter, profile.History);
        return resolved;
    }

    // #28: the deck picker's REMATCH button, asked every frame. Recomputed only when the selection, the match count or the
    // pending match changes (History deep-copies); NativeRematchUI forgets it whenever the picker is left, so deck edits,
    // cheats and world reboots (all outside the picker) are seen. The start itself always recomputes.
    private static string rematchSelected, rematchPending;
    private static int rematchMatches = -1;
    private static bool rematchCasual;
    private static NpcEncounter rematchOffer;
    internal static string RematchRefusal { get; private set; }
    internal static void ForgetRematchOffer() { rematchMatches = -1; rematchOffer = null; }
    internal static NpcEncounter RematchOffer(string selected)
    {
        if (profile == null || screen == "Starting" || screen == "Playing") return null;
        bool casual = NativeUIProjection.CasualPlayMode;   // the picker's Ranked/Casual switch: Rematch is Ranked-only (#37)
        if (rematchMatches == profile.MatchCount && rematchSelected == selected && rematchPending == profile.PendingMatchId
            && rematchCasual == casual) return rematchOffer;
        rematchMatches = profile.MatchCount; rematchSelected = selected; rematchPending = profile.PendingMatchId; rematchOffer = null;
        rematchCasual = casual;
        try
        {
            string refusal = LocalRematch.CasualRefusal;
            if (!casual) rematchOffer = LocalRematch.Encounter(profile, NpcWorldRuntime.Matchmaker as LadderMatchmaker, selected, out refusal);
            RematchRefusal = refusal;
        }
        catch (Exception failure) { RematchRefusal = "failed: " + failure.Message; Debug.LogWarning("NPC world rematch check failed: " + failure.Message); }
        return rematchOffer;
    }

    // A rematch never falls back to another opponent: a refusal (the offer went stale) refuses the start.
    private static NpcEncounter RematchEncounter(string selected, out LocalDeck deck)
    {
        deck = null;
        NpcEncounter encounter = LocalRematch.Encounter(profile, NpcWorldRuntime.Matchmaker as LadderMatchmaker, selected, out string refusal);
        if (encounter == null) throw new InvalidOperationException("Rematch unavailable: " + refusal);
        deck = ResolveWorldDeck(encounter);
        return encounter;
    }

    private static string OpponentLabel(NpcEncounter encounter, IReadOnlyList<LocalMatchRecord> matches)
    {
        var decks = NpcWorldRuntime.World.Catalogs.Decks;
        var record = HeadToHead.For(encounter.Npc.Id, matches.Select(match => LocalRematch.Past(match, decks)),
            matches.Where(match => match.NpcConceded == true).Select(match => match.NpcId));
        return encounter.Npc.DisplayName + (record.Games > 0 ? " (" + record.Label + ")" : "");
    }

    // Diamond/Legend-gated achievement feats stay Ranked-only: a Casual match queues them at most at Platinum.
    internal static LadderBand CasualAchievementBand(double npcRating)
    {
        var band = NpcWorld.BandForRating(npcRating);
        return band > LadderBand.Platinum ? LadderBand.Platinum : band;
    }

    // #37 Casual queue: inputs are the hidden Casual rating and Casual history only (never the ladder or the player's deck).
    // The encounter is Active before LocalDecks.Get: Casual decks are served only while an encounter references them.
    private static NpcEncounter TryCasualEncounter(string selected, out LocalDeck deck)
    {
        deck = null;
        var matchmaker = NpcWorldRuntime.Casual;
        if (matchmaker == null || !IsWorldSelection(selected)) return null;
        try
        {
            var casual = profile.CasualHistory;
            var decks = NpcWorldRuntime.World.Catalogs.Decks;
            var request = new CasualRequest(profile.NpcProfileSeed, casual.Count, profile.RankedMatchCount, profile.CasualRating,
                casual.Skip(Math.Max(0, casual.Count - matchmaker.Rules.HistoryDepth)).Select(match => LocalRematch.Past(match, decks)));
            NpcEncounter encounter = matchmaker.Next(request);
            string deckId = encounter.Deck.Id;
            if (string.IsNullOrWhiteSpace(deckId) || deckId.Length > 64 || deckId.Any(char.IsControl))
                throw new InvalidOperationException("Casual deck id is not storable: " + deckId);
            NpcWorldRuntime.Begin(encounter);
            LocalDeck resolved = LocalDecks.Get(deckId);
            resolved.Build();
            opponentLabel = OpponentLabel(encounter, profile.History);
            deck = resolved;
            return encounter;
        }
        catch (Exception failure)
        {
            Debug.LogWarning("Casual matchmaker failed; using the legacy opponent: " + failure.Message);
            NpcWorldRuntime.End();
            opponentLabel = null; deck = null;
            return null;
        }
    }

    // Golden knob ids keep today's legacy record (null). A world id the runtime cannot wrap falls back to the knob's deck
    // without an encounter (the golden catalog then has no profile for it), logged once per start.
    private static NpcEncounter TryPlaytestEncounter(NpcProfile knob, int seed)
    {
        if (knob == null || !NpcWorldRuntime.IsPlaytestWorldId(knob.Id)) return null;
        try
        {
            var encounter = NpcWorldRuntime.PlaytestEncounter(knob, profile.RankedMatchCount, seed);
            if (encounter == null) Debug.LogWarning("NPC world playtest: no encounter for " + knob.Id + "; playing its deck without the world profile");
            return encounter;
        }
        catch (Exception failure)
        {
            Debug.LogWarning("NPC world playtest encounter failed for " + knob.Id + ": " + failure.Message);
            return null;
        }
    }

    public static void WriteStatus(string reports)
    {
        if (profile == null) return;
        // Non-copying counts: History/Pending/CustomDecks/CustomDeck/Collection deep-copy or rebuild their data on every call.
        var decks = profile.VisibleDeckSummaries();
        var custom = decks.Count == 1 ? decks[0] : null;
        // Live 2026-10-28: a single bought deck holding a card outside the legacy draft UI's catalogue made this
        // diagnostic throw every 3 s, so the status (and deploy's stop preflight) went stale. Diagnostics never throw.
        string StatusDraftCardIds() { try { return collectionUi.DraftCardIds; } catch (Exception) { return "unavailable"; } }
        profile.OwnedCollectionTotals(out int ownedDistinct, out int ownedTotal);
        LocalPackResult pack = profile.LastPack;
        string visibleError = error ?? collectionUi.ActionError;
        File.WriteAllLines(Path.Combine(reports, "ladder-state.txt"), new[] {
            "runId=" + ClientBridge.DiagnosticRunId, "utc=" + DateTime.UtcNow.ToString("o"), "screen=" + screen, "matchId=" + MatchId,
            "rank=" + profile.Rank, "stars=" + profile.Stars, "gold=" + profile.Gold,
            "matches=" + profile.MatchCount, "pending=" + (profile.PendingMatchId ?? ""),
            "playtestOpponent=" + (playtest?.Profile.Id ?? ""),
            "deck=" + (active?.Deck ?? ""), "opponent=" + (active?.Opponent ?? ""),
            "npc=" + (NpcWorldRuntime.Active?.Npc.Id ?? ""), "npcRating=" + (NpcWorldRuntime.Active?.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? ""),
            "result=" + (result?.Result ?? ""), "reward=" + (result?.Gold ?? 0),
            "nativeFinished=" + nativeFinished,
            "packs=" + profile.UnopenedPacks,
            "ownedDistinct=" + ownedDistinct,
            "ownedTotal=" + ownedTotal,
            "selectedDeck=" + collectionUi.SelectedDeck,
            "customCount=" + decks.Count,
            "customSaved=" + (custom != null).ToString().ToLowerInvariant(),
            "customName=" + StatusValue(custom?.Name),
            "draftHero=" + collectionUi.DraftHero,
            "draftCount=" + collectionUi.DraftCount,
            "draftValid=" + collectionUi.DraftValid.ToString().ToLowerInvariant(),
            "screenPage=" + collectionUi.PageFor(screen),
            "screenFilter=" + collectionUi.FilterFor(screen),
            "visibleCardIds=" + collectionUi.VisibleCardIds(screen),
            "draftCardIds=" + StatusDraftCardIds(),
            "lastPackIds=" + (pack == null ? "" : string.Join(",", pack.Cards.Select(card => card.Id))),
            "lastPackDuplicateGold=" + (pack?.Cards.Sum(card => card.DuplicateGold) ?? 0),
            "actionBusy=" + collectionUi.ActionBusy.ToString().ToLowerInvariant(),
            "error=" + StatusValue(visibleError), "profileBound=true"
        });
    }

    public static void Draw()
    {
        if (profile == null || NativeMatchmaking.Visible
            || (NativeUIProbe.HideMenu && screen != "Playing" && screen != "Starting")) return;
        Matrix4x4 oldMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(ScreenWidth / 1280f, ScreenHeight / 720f, 1));
        GUI.depth = -1000;
        var label = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
        label.normal.textColor = Color.white;
        var title = new GUIStyle(label) { fontSize = 30 };
        var button = new GUIStyle(GUI.skin.button) { fontSize = 20, wordWrap = true };

        // The in-match rank/gold/opponent box and its Concede shortcut are a debug aid (native Concede and test-control
        // concede cover players and tooling); hidden unless the Diagnostics.MatchDebugOverlay config entry is on.
        if (screen == "Playing") { if (ShowMatchDebugOverlay()) DrawPlaying(label, button); }
        else if (screen == "Result") DrawResult(label, title, button);
        else if (screen == "Menu" || screen == "Shop" || screen == "Packs" || screen == "Collection" || screen == "DeckBuilder")
            collectionUi.Draw(screen, value => screen = value, Start, label, title, button);
        else DrawWait(label, title, button);

        GUI.matrix = oldMatrix;
    }

    // Set by OfflineProbe from BepInEx config (Diagnostics.MatchDebugOverlay, default false).
    internal static Func<bool> ShowMatchDebugOverlay = () => false;

    private static void DrawPlaying(GUIStyle label, GUIStyle button)
    {
        // Top right: the top-left corner is the native opponent name plate (NPC name), which this panel used to cover.
        Panel(new Rect(955, 12, 310, 70));
        GUI.Label(new Rect(968, 20, 285, 55), profile.Rank + "  |  " + profile.Gold + " gold\nLocal ladder - "
            + (opponentLabel ?? LocalDecks.Get(active.Opponent).Opponent), new GUIStyle(label) { fontSize = 15 });
        if (GUI.Button(new Rect(16, 655, 145, 43), "Concede", button)) ClientBridge.ConcedeLocal();
    }

    private static void DrawResult(GUIStyle label, GUIStyle title, GUIStyle button)
    {
        Panel(new Rect(150, 70, 980, 580));
        GUI.Label(new Rect(185, 100, 900, 52), result == null ? "SAVE PENDING"
            : result.Result == "Won" ? "VICTORY" : result.Result == "Tied" ? "DRAW" : "DEFEAT", title);
        if (LocalEconomy.IsRolled(result?.RewardVersion ?? active?.RewardVersion ?? 0)) DrawCurrentResult(label);
        else DrawV2Result(label);
        if (result != null)
        {
            if (GUI.Button(new Rect(185, 515, 425, 60), "Replay saved pack", button)) { screen = "Menu"; NativeUIProbe.Navigate(SceneMgr.Mode.PACKOPENING); }
            if (GUI.Button(new Rect(660, 515, 425, 60), "Continue to native picker", button)) { screen = "Menu"; NativeUIProbe.Navigate(SceneMgr.Mode.TOURNAMENT); }
        }
        else if (GUI.Button(new Rect(660, 515, 425, 60), "Retry save", button)) error = null;
        if (!string.IsNullOrEmpty(error))
        {
            var errorStyle = new GUIStyle(label) { fontSize = 16 };
            errorStyle.normal.textColor = new Color(1f, 0.4f, 0.4f);
            GUI.Label(new Rect(185, 590, 900, 42), error, errorStyle);
        }
    }

    // Rolled reward fallback: the native flow is primary, but this panel must never resurrect the removed habit copy.
    private static void DrawCurrentResult(GUIStyle label)
    {
        if (result == null)
        {
            GUI.Label(new Rect(185, 170, 900, 62), "Result not saved. Retry uses the same match evidence.", label);
            GUI.Label(new Rect(185, 250, 900, 42), "Reward and achievement receipts pending", label);
            return;
        }
        LocalRewardSummary summary = LocalRewardPresentation.Summary(result, profile);
        GUI.Label(new Rect(185, 170, 900, 300), summary.Body, label);
    }

    private static void DrawV2Result(GUIStyle label)
    {
        GUI.Label(new Rect(185, 170, 900, 62), result == null ? "Result not saved. Retry uses same match and reward facts."
            : "+" + result.Gold + " local gold" + (result.DustReward > 0 ? " · +" + result.DustReward + " dust" : "")
                + (result.BonusGold > 0 ? " · " + result.BonusGold + " bonus" : ""), label);

        bool gated = conceded || terminalTurns < 4;
        if (gated)
            GUI.Label(new Rect(185, 250, 900, 34), "No rewards: " + (conceded ? "conceded" : "fewer than four engine turns"), label);
        else
            GUI.Label(new Rect(185, 250, 900, 34), "Completion +25" + (terminalOutcome == "Won" ? " · Victory +25" : ""), label);

        LocalRewardFacts facts = result?.RewardFacts ?? terminalFacts ?? new LocalRewardFacts();
        DrawReason(new Rect(185, 294, 900, 34), !gated && facts.DistinctCardsPlayed >= 6,
            facts.DistinctCardsPlayed >= 6 ? "6 distinct cards played +10" : Math.Min(6, facts.DistinctCardsPlayed) + "/6 distinct cards played", label);
        DrawReason(new Rect(185, 334, 900, 34), !gated && facts.EnemyMinionsDefeated >= 5,
            facts.EnemyMinionsDefeated >= 5 ? "5 enemy minions defeated +10" : Math.Min(5, facts.EnemyMinionsDefeated) + "/5 enemy minions defeated", label);
        bool lowHealth = terminalOutcome == "Won" && facts.MinimumHealth >= 1 && facts.MinimumHealth <= 10;
        DrawReason(new Rect(185, 374, 900, 48), !gated && lowHealth,
            lowHealth ? "Won after observed 1-10 health +20" : "Minimum health: " + facts.MinimumHealth, label);
        GUI.Label(new Rect(185, 432, 900, 42), result == null ? "Pack reward pending"
            : result.PackReward ? "Bonus pack earned" : "No bonus pack", label);
    }

    private static void DrawReason(Rect rect, bool earned, string text, GUIStyle label)
    {
        var style = new GUIStyle(label);
        style.normal.textColor = earned ? new Color(0.45f, 0.9f, 0.55f) : new Color(0.65f, 0.68f, 0.72f);
        GUI.Label(rect, text, style);
    }

    private static void DrawWait(GUIStyle label, GUIStyle title, GUIStyle button)
    {
        Panel(new Rect(40, 20, 1200, 680));
        // A public build carries no "Hearthstone" in its labels (I2b).
        GUI.Label(new Rect(65, 34, 700, 45),
#if SOLOSTONE_RELEASE
            "SOLOSTONE",
#else
            "HEARTHSTONE / LOCAL",
#endif
            title);
        GUI.Label(new Rect(185, 250, 900, 100), screen == "Loading" ? "Loading local card definitions..." : "Loading native match...", label);
        if (GUI.Button(new Rect(1110, 30, 100, 42), "Exit", button)) Application.Quit();
    }

    private static string StatusValue(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
    private static int ScreenWidth => UnityEngine.Screen.width;
    private static int ScreenHeight => UnityEngine.Screen.height;

    private static void Panel(Rect rectangle)
    {
        Color old = GUI.color;
        GUI.color = new Color(0.035f, 0.045f, 0.065f, 0.98f);
        GUI.DrawTexture(rectangle, Texture2D.whiteTexture);
        GUI.color = old;
    }
}
