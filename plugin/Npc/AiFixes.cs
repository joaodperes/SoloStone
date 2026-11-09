using System;
using System.Collections.Generic;
using System.Linq;

// Immutable per-profile switches. Registry entries default ON; never depend on game information or RNG.
public sealed class AiFixes
{
    public static IReadOnlyList<string> Registered { get; } = Array.AsReadOnly(new[] { "selftest", "survival-scoring", "commit-useful-plays", "attack-allocation", "plan-reuse", "carried-cards", "recurring-value", "settle-fallback", "reuse-mistake-rate", "lethal-recheck", "face-tiebreak", "doomed-minions", "death-value", "death-measured", "death-chip", "death-held", "starved-replies", "replan-reserve", "trigger-value", "trigger-attack", "trigger-plays", "trigger-damage", "own-deck-value", "carried-reuse" });
    public IReadOnlyList<string> Disabled { get; }
    public AiFixes(IEnumerable<string> disabled = null)
    {
        var ids = (disabled ?? new string[0]).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        foreach (var id in ids) Validate(id);
        Disabled = Array.AsReadOnly(ids);
    }
    private static void Validate(string id)
    {
        if (!Registered.Contains(id, StringComparer.Ordinal)) throw new ArgumentException("Unknown AI fix: " + id);
    }
    public bool On(string id) { Validate(id); return !Disabled.Contains(id, StringComparer.Ordinal); }
    // Search-budget fixes for tiers without softmax noise only. Softmax picks among candidate values, so more replies on
    // capped searches would move a lower tier's mistake rate and strength (2026-11-02: shallow candidates tripled it).
    private static readonly string[] NoiselessOnly = { "starved-replies", "replan-reserve" };
    public AiFixes ForSoftmax(double temperature) => temperature <= 0 || NoiselessOnly.All(Disabled.Contains)
        ? this : new AiFixes(Disabled.Concat(NoiselessOnly));
}
