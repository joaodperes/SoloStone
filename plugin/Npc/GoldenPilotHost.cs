// Hosts (NativeMatch, harness) read the fair-view factory and public inputs through this, never by casting to
// GoldenPilot: decorators such as SkilledPilot (plugin/Npc/World) forward their primary search's values.
public interface IGoldenPilotHost
{
    DriverOptions Options { get; }
    GoldenPublicInputs PublicInputs { get; }
}
