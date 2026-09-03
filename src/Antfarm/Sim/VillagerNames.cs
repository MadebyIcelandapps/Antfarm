using System;

namespace Antfarm.Sim;

/// <summary>
/// Names for villagers, in the same register as the tribe names.
///
/// A colony of 500 anonymous dots is a statistic. One of them being Halla,
/// who dug four thousand blocks and died to a goblin at depth 1180, is a
/// story, and after a year of running that is the difference between numbers
/// going up and something worth reading.
/// </summary>
public static class VillagerNames
{
    private static readonly string[] Heads =
    {
        "Hal", "Bryn", "Sig", "Vald", "Ing", "Thor", "Ase", "Grim", "Orm", "Sten",
        "Ulf", "Rag", "Skad", "Frey", "Hjal", "Kol", "Rune", "Sval", "Tyr", "Yr",
        "Ald", "Brand", "Dag", "Eir", "Fenn", "Gorm", "Hild", "Jor", "Ket", "Lif",
    };

    private static readonly string[] Tails =
    {
        "la", "dis", "mar", "unn", "vor", "ir", "gar", "ny", "eth", "rik",
        "sten", "run", "vald", "borg", "grim", "a", "i", "or", "ulf", "heim",
    };

    private static readonly string[] Epithets =
    {
        "the Deep", "Stonebiter", "the Patient", "Longtunnel", "the Quiet",
        "Ironhand", "the First", "Farwander", "Gravemaker", "the Bold",
        "Emberhand", "the Lost", "Hollowborn", "Saltbeard", "the Steady",
    };

    public static string Next(Random rand)
    {
        string name = Heads[rand.Next(Heads.Length)] + Tails[rand.Next(Tails.Length)];

        // A small minority get a title, so the ones who do stand out.
        if (rand.Next(14) == 0)
            name += " " + Epithets[rand.Next(Epithets.Length)];

        return name;
    }
}
