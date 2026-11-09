# The Deep Delve — experimental roguelike dungeon

This is a first playable vertical slice of a Kobolds-inspired solo dungeon with Slay the Spire-style branching. It does not restore Blizzard's original Dungeon Run boss mechanics.

## Start an expedition

1. Build the fork against a locally owned, supported Hearthstone client, following the existing BUILDING.md.
2. Open a profile, enter the Hub or deck picker, and press **F8**.
3. Choose a class starter deck and a reachable room.
4. Battles use the offline native match engine and existing NPC decks. Win a fight to choose one of three card buckets, or reroll.
5. Traverse branching rooms across three acts. You can visit events, merchants, treasure caches and camps.

**Important:** This branch has not yet been verified inside a running Hearthstone Unity client, and it is not a packaged release. Keep a backup of the existing client and saves.

## Implemented experimental systems

- Three acts, six floors per act, 48 nodes with deterministic generation, explicit route edges and a boss at the end of each act.
- A separate per-profile dungeon-run.xml checkpoint, atomic saves, recovery for interrupted attempts and a completed-run archive.
- Three starting lives, up to five; dungeon-only gold; rerolls; battle loss and retry; three-card bucket draft rewards; a 70-card deck cap.
- 55 existing supported NPC deck references in a staging encounter catalog, 165 three-card buckets for 11 class heroes, 16 two-option narrative events, and 12 out-of-battle treasure effects.
- Temporary unowned dungeon decks and matches are isolated from ordinary ranked stars, account gold/dust, achievement progression and inventory. Dungeon matches use SoloStone's debug-scenario settlement gate.
- A functional F8 IMGUI overlay for map, combat, drafting, events, rest sites, merchants and treasure rewards.

## Not implemented yet

- Unique Kobolds boss powers and hero powers, original Dungeon Run adventure scenes, combat-altering treasures and a dedicated dungeon NPC pilot.
- A polished graphical path map (the present map is a functional scrollable grid of route nodes), rich animations and custom artwork.
- Automatic loading of Mercenaries voice lines. No Blizzard audio, images or other proprietary client assets are redistributed; the first step is inspecting verified asset IDs inside the player's own locally installed client, with silent fallback where missing.
- A full native end-to-end playtest. The new combat adapter can require further version-specific changes.

## Contents and code

- plugin/Roguelike/DungeonRunCore.cs — deterministic maps, pure room transitions and validation.
- data/roguelike/dungeon-content-v1.json — class-specific draft buckets, event narratives, treasures and encounter metadata.
- plugin/Roguelike/DungeonContent.cs — embedded catalog loader and supported-card filtering.
- plugin/Roguelike/DungeonMenu.cs — opt-in map, actions and persistence.
- plugin/LocalLadder.cs and plugin/ClientBridge.cs — non-ranked native dungeon fight boundary.
- installer/SoloStone.Launcher.Tests/RoguelikeTests.cs — deterministic rules and XML roundtrip regression tests.
- tools/roguelike-tests/ — standalone, dependency-free test runner for the pure rules engine.

## Test locally

With .NET SDK 10.0.402:

    dotnet run --project tools/roguelike-tests/Roguelike.Tests.csproj -c Release

Or as part of the existing launcher tests:

    dotnet run --project installer/SoloStone.Launcher.Tests -c Release -- --roguelike

A GitHub Actions workflow runs the pure core tests on changes. These tests do not verify Harmony hooks or UI assets.

## Next work

1. Verify fight start, win, loss, concede and scene return inside the supported Hearthstone client; explicitly test temporary decks at 30/33/36/50/70 cards.
2. Replace default NPC opponents with encounter-specific templates, escalating AI and novel boss effects.
3. Improve buckets with archetype and curve weighting, limiting poor combinations; add short 10–15-card starters after the client deck-size behavior is verified.
4. Build actual line-drawn maps, additional events, shops selling cards and combat-affecting treasure abilities.
5. Add client-local voice cue lookup behind availability checks and game-asset licensing review, without exporting or bundling copyrighted game media.

## Security and upstream compatibility

SoloStone remains an isolated, entirely offline, unofficial mod requiring a lawfully obtained Hearthstone client. This feature changes no firewall policy, authentication, server communications or Blizzard-owned game files.
