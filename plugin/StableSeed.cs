using System;
using System.Collections.Generic;
using System.Linq;

// SplitMix64, with ordered signed integers encoded as their two's-complement bits.
// Derive mixes the root, then each component; the low signed 32 bits seed the engine.
public static class StableSeed
{
#if MATCH_TESTS
    // Deterministic legacy fixtures explicitly keep their original seat; coin-flip tests clear this override.
    internal static int? TestFirstPlayer = 1;
#endif
    public static int MatchFirstPlayer(int seed)
    {
#if MATCH_TESTS
        if (TestFirstPlayer.HasValue) return TestFirstPlayer.Value;
#endif
        return new Random(Derive(seed, 0x53454154 /* SEAT */)).Next(2) + 1;
    }

    public static int ApplyFirstPlayerRules(int rolled, IEnumerable<string> player1Cards, IEnumerable<string> player2Cards)
    {
        if (rolled != 1 && rolled != 2) throw new ArgumentOutOfRangeException(nameof(rolled));
        // Pinned engine tags the ordinary seat before Aya's rule. Resolve first so it cannot tag both players FIRST_PLAYER.
        bool firstAya = player1Cards.Contains("JAIL_504"), secondAya = player2Cards.Contains("JAIL_504");
        return firstAya == secondAya ? rolled : firstAya ? 2 : 1;
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
    public static int Derive(long root, params long[] components)
    {
        unchecked
        {
            ulong value = Mix((ulong)root);
            foreach (long component in components) value = Mix(value ^ (ulong)component);
            return (int)value;
        }
    }
    public static int Index(int seed, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        // Rejection avoids modulo bias. Each decision gets an independent policy seed.
        ulong state = unchecked((ulong)(long)seed), bound = (ulong)count;
        ulong threshold = unchecked(0UL - bound) % bound;
        do
        {
            ulong value = Mix(state);
            state = unchecked(state + 0x9E3779B97F4A7C15UL);
            if (value >= threshold) return (int)(value % bound);
        } while (true);
    }
}
