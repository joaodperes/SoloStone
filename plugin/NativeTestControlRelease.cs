#if SOLOSTONE_RELEASE
using System;
using System.Collections.Generic;
using BepInEx.Logging;

// A public build has no test control: no endpoint, no flag file, no exporters (I2e). Gameplay code still calls these hooks,
// so they exist here as no-ops, and the one that real turn timing needs (TurnInputReady) forwards to TurnInput.
internal static class NativeTestControl
{
    internal static void Initialize(OfflineLaunchContext launch, ManualLogSource log) { }
    internal static void Update() { }
    internal static void Shutdown() { }
    internal static void AssertMainThread() { }
    internal static bool DiagnosticsEnabled => false;
    internal static string SemanticNonce => null;
    internal static string AttributeTrace(string record) => record;
    internal static bool TurnInputReady(NativeMatch match, bool forTimer = false) => TurnInput.Ready(match, forTimer);
    internal static void PacketInjected() { }
    internal static void ObserveChoices(int id, List<int> picks) { }
    internal static void ObserveSend(string kind, int id = 0, int index = 0, int target = 0, int sub = -1, int position = 0) { }
    internal static void EngineAccepted() { }
    internal static void SendFailed(Exception error) { }
    internal static void SendRejected(TurnExpiredException error) { }
}
#endif
