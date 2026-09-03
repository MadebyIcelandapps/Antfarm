namespace Antfarm.Sim;

/// <summary>
/// What a tribe is like.
///
/// Ten colonies running identical code produce ten identical histories, and
/// after a year the map is texture with no story in it. A trait is only a set
/// of weights on behaviour that already exists, so it costs almost nothing,
/// but it makes each tribe legible: you should be able to look at the map and
/// know which one dug that, without checking the table.
/// </summary>
public enum TribeTrait : byte
{
    /// <summary>Goes down. Deeper targets, wider reach, mines faster.</summary>
    Delver,

    /// <summary>Goes up. More of the tribe on the tools, more rooms, more housing.</summary>
    Builder,

    /// <summary>Goes out. Splits into new settlements far sooner.</summary>
    Expander,

    /// <summary>Goes armed. More soldiers, hits harder, tougher.</summary>
    Warlike,

    /// <summary>Goes rich. Carries more, smelts more, fills more chests.</summary>
    Hoarder,
}

public static class TribeTraits
{
    public static string Describe(TribeTrait t) => t switch
    {
        TribeTrait.Delver => "delvers",
        TribeTrait.Builder => "builders",
        TribeTrait.Expander => "expanders",
        TribeTrait.Warlike => "warlike",
        TribeTrait.Hoarder => "hoarders",
        _ => "",
    };
}

/// <summary>
/// One of the dead, kept on the roll.
///
/// A wiped out tribe does not disappear: it rises. So a villager's name and
/// record have to outlive them, or the thing that comes back out of the ground
/// would be a stranger wearing the tribe's colours rather than Halla, who dug
/// four thousand blocks before something killed her.
/// </summary>
public readonly struct Fallen
{
    public readonly string Name;
    public readonly long TilesDug;
    public readonly int Kills;
    public readonly int DeepestY;
    public readonly bool WasSoldier;
    public readonly int Weapon;

    public Fallen(string name, long tilesDug, int kills, int deepestY, bool wasSoldier, int weapon)
    {
        Name = name;
        TilesDug = tilesDug;
        Kills = kills;
        DeepestY = deepestY;
        WasSoldier = wasSoldier;
        Weapon = weapon;
    }
}
