#if !SOLOSTONE_RELEASE
using System.Linq;
using MiniJSON;

// Read-only live evidence for pet reactions: the native client's own decision log (played, cached,
// blocked by cooldown/fatigue, mood) plus each in-match pet's loaded event config and timers.
internal static class PetReactionTelemetry
{
    internal static JsonNode Read(int after)
    {
        NativeTestControl.AssertMainThread();
        var lines = PetData_Test.Get().GetLogLines();
        int start = System.Math.Min(System.Math.Max(after, 0), lines.Count);
        var pets = new System.Collections.Generic.List<object>();
        var manager = PetGameplayManager.Get();
        for (int player = 1; manager != null && player <= 2; player++)
        {
            if (!manager.TryGetPet(player, out var pet) || pet == null) continue;
            var config = pet.PetEventConfig;
            var timing = pet.PetInteractionConfig;
            pets.Add(new JsonNode {
                { "playerId", player }, { "friendly", pet.IsFriendly(false) }, { "petId", pet.PetId }, { "variantId", pet.PetVariantId },
                { "visible", pet.PetObject != null }, { "currentEvent", pet.CurrentEvent.ToString() }, { "fatigueTimer", (double)pet.FatigueTimer },
                { "cooldowns", TestControlProtocol.List(pet.AnimationCooldownTimers.Select(t => (object)(t.behavior?.ActionAnim.ToString() ?? ""))) },
                { "eventCacheTimer", timing == null ? -1d : (double)timing.eventCacheTimer },
                { "cooldownTime", timing == null ? -1d : (double)timing.cooldownTime },
                { "fatigueTime", timing == null ? -1d : (double)timing.fatigueTime },
                { "actions", config == null ? null : TestControlProtocol.List(config.editorPetActions.Select(a => (object)new JsonNode {
                    { "event", a.EventType.ToString() }, { "anim", a.ActionAnim.ToString() }, { "priority", a.Priority },
                    { "happiness", (double)a.HappinessImpact }, { "excitement", (double)a.ExcitementImpact } })) },
                { "sequences", config == null ? null : TestControlProtocol.List(config.editorPetSequences.Select(a => (object)new JsonNode {
                    { "event", a.EventType.ToString() }, { "anim", a.ActionAnim.ToString() }, { "priority", a.Priority } })) },
            });
        }
        return new JsonNode {
            { "next", lines.Count },
            { "log", TestControlProtocol.List(lines.Skip(start).Select(line => (object)new JsonNode {
                { "time", line.logTime }, { "type", line.logType.ToString() }, { "text", line.log } })) },
            { "pets", TestControlProtocol.List(pets) },
        };
    }
}
#endif
