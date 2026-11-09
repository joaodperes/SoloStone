using System;
using System.IO;
using System.Reflection;

// Plugin-only (not linked into tests/harness). Loads data/npc/*.json from embedded resources with the strict
// Unity-Mono-safe EmbeddedJson parser and installs NpcWorldRuntime. Called once from ClientBridge.Initialize (seam S11).
internal static class NpcWorldBoot
{
    internal static string Read(string resourceName)
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("Missing embedded resource: " + resourceName))
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    internal static NpcWorldCatalogs LoadCatalogs() => LoadCatalogs(null);

    // casual: its decks join the ranked library (admission "casual": never generated, matched or inferred on the ladder).
    internal static NpcWorldCatalogs LoadCatalogs(CasualDeckCatalog casual)
    {
        NpcWorldJson.Parser = EmbeddedJson.Parse;
        var ranked = NpcDeckLibrary.Parse(Read(NpcDeckLibrary.ResourceName));
        return new NpcWorldCatalogs(casual == null ? ranked : casual.MergeInto(ranked),
            SkillCatalog.Parse(Read(SkillCatalog.ResourceName)), PersonalityCatalog.Parse(Read(PersonalityCatalog.ResourceName)),
            NpcNotables.Parse(Read(NpcNotables.ResourceName)), NpcNameTables.Parse(Read(NpcNameTables.ResourceName)),
            NpcCosmeticPool.Parse(Read(NpcCosmeticPool.ResourceName)));
    }

    // Seam S11 startup entry (ClientBridge.Initialize, after SelectedProfile.Initialize). Points every world diagnostics sink
    // at the plugin log (rate limited), then generates and installs the world with its client seams. A failed boot logs
    // once and leaves every hook null, so ladder play keeps today's opponents (LocalDecks.NextOpponent).
    internal static bool TryInstall(Action<string> log)
    {
        var limited = NpcWorldRuntime.RateLimited(log);
        NpcCatalog.ProfileHookFailed = error => limited?.Invoke("NpcCatalog.ForDeck hook failed: " + error.GetType().Name + ": " + error.Message);
        NpcPopulationRules.Log = limited;
        DeckPlans.SeamLog = limited;
#if ENGINE_SOURCE
        FairBotState.HistoryLimitLog = limited;
#endif
        LocalDecks.ExternalDecksLog = limited;
        CasualDeckCatalog casual = null;
        try { NpcWorldJson.Parser = EmbeddedJson.Parse; casual = CasualDeckCatalog.Parse(Read(CasualDeckCatalog.ResourceName)); }
        catch (Exception failure) { limited?.Invoke("npc-world casual catalog unavailable: " + failure.Message); }
        return NpcWorldRuntime.TryInstallClient(() => NpcWorld.Generate(LoadCatalogs(casual)), PendingNpcForDeck, log, casual);
    }

    // The saved, unsettled match's NPC when it plays this deck (pins and names the deck while NpcWorldRuntime.Active is
    // unset). Normal builds settle an interrupted pending match as Abandoned at profile open, before this boot runs.
    private static string PendingNpcForDeck(string deckId)
    {
        var pending = LocalLadder.Profile?.Pending;
        return pending != null && pending.NpcId != null && (pending.NpcDeckId ?? pending.Opponent) == deckId ? pending.NpcId : null;
    }
}
