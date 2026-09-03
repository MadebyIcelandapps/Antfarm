using System;

namespace Antfarm.Sim;

/// <summary>
/// What a villager inherits.
///
/// The tribe personalities I wrote by hand are fixed weights: a Delver digs
/// faster for ever, whatever the world does to it. This is the version that
/// makes itself. A newborn takes its parent's tendencies with a small random
/// nudge, and villagers who die young simply do not become parents, so
/// whatever kept the survivors alive spreads without anybody designing it.
///
/// Five bytes, neutral at 128. A tribe in hard rock should drift toward
/// patience and endurance; one under constant raids toward toughness and
/// caution; one with a short haul toward big packs. Nobody chooses that, and
/// after a year of running the ten tribes should be measurably different
/// animals rather than ten copies with different colours.
/// </summary>
public struct Genes
{
    /// <summary>Higher digs faster. Costs nothing, so it is under pure positive selection.</summary>
    public byte Vigour;

    /// <summary>Bigger pack, but a longer round trip before delivering anything.</summary>
    public byte Capacity;

    /// <summary>More health. Matters only where things are trying to kill you.</summary>
    public byte Toughness;

    /// <summary>Willingness to be near danger. Low flees early, high stands and fights.</summary>
    public byte Boldness;

    /// <summary>How far from home a villager will accept work.</summary>
    public byte Wander;

    public const byte Neutral = 128;

    public static Genes Founder() => new()
    {
        Vigour = Neutral,
        Capacity = Neutral,
        Toughness = Neutral,
        Boldness = Neutral,
        Wander = Neutral,
    };

    /// <summary>
    /// A child of this villager. Each gene drifts by a few points, so change is
    /// gradual and a single lucky birth cannot swing a tribe.
    /// </summary>
    public Genes Breed(Random rand)
    {
        return new Genes
        {
            Vigour = Drift(Vigour, rand),
            Capacity = Drift(Capacity, rand),
            Toughness = Drift(Toughness, rand),
            Boldness = Drift(Boldness, rand),
            Wander = Drift(Wander, rand),
        };
    }

    private static byte Drift(byte value, Random rand)
    {
        int next = value + rand.Next(-9, 10);
        return (byte)(next < 12 ? 12 : next > 243 ? 243 : next);
    }

    // --- how a gene turns into behaviour -----------------------------

    /// <summary>Ticks to break one block. Lower is faster.</summary>
    public int DigTicks(int baseline) =>
        Math.Max(5, baseline * 2 * Neutral / (Neutral + Vigour));

    public int CarryCapacity(int baseline) =>
        Math.Max(6, baseline * Capacity / Neutral);

    public int MaxHealth(int baseline) =>
        Math.Max(20, baseline * Toughness / Neutral);

    /// <summary>Squared tile distance at which a non-soldier bolts for home.</summary>
    public int FleeRangeSq() =>
        (int)Math.Pow(70 - Boldness * 40 / 255, 2);

    public int WanderRadius(int baseline) =>
        Math.Max(8, baseline * Wander / Neutral);
}
