using System.Collections.Generic;
using Terraria.ID;

namespace Antfarm.Sim;

public enum BuildKind : byte
{
    /// <summary>A solid block: floors, walls, roofs.</summary>
    Block,

    /// <summary>Background wall. Not solid, but it is what makes a room read as indoors.</summary>
    Wall,

    /// <summary>Walkable platform: storey floors you can pass through.</summary>
    Platform,

    /// <summary>Dig this out. Interiors are solid rock until somebody removes them.</summary>
    Clear,

    /// <summary>Light.</summary>
    Torch,
}

public readonly struct BuildJob
{
    public readonly int X;
    public readonly int Y;
    public readonly int Item;
    public readonly BuildKind Kind;

    public BuildJob(int x, int y, int item, BuildKind kind)
    {
        X = x;
        Y = y;
        Item = item;
        Kind = kind;
    }
}

/// <summary>
/// Lays out actual buildings.
///
/// The first version queued only a hollow rectangle of blocks: no interior was
/// ever dug out, no background wall was placed, there was no door and no way
/// between storeys. Underground that produced a brick outline around solid
/// rock, which is why the settlements did not read as buildings.
///
/// A room here is cleared, walled, floored, doored, lit, and connected to the
/// storey above, and the same generator serves a surface house and an
/// underground hall because the only real difference is what is around it.
/// </summary>
public static class Architect
{
    public const int Width = 11;
    public const int StoreyHeight = 6;

    /// <summary>
    /// Emit one storey of a building whose ground floor sits at
    /// <paramref name="baseY"/>. Storeys stack upward.
    /// </summary>
    public static void PlanStorey(List<BuildJob> jobs, int x0, int baseY, int storey,
                                  int block, bool lit)
    {
        // Background walls are paid for with the building block itself.
        int wallItem = block;

        // Each storey shares the floor below it, hence StoreyHeight - 1.
        int floorY = baseY - storey * (StoreyHeight - 1);
        int roofY = floorY - StoreyHeight + 1;

        int left = x0;
        int right = x0 + Width - 1;

        // The stairwell: one column left open through every floor so villagers
        // can climb between storeys. They brace against the wall beside it.
        int stairX = left + 1;

        // 1. Hollow it out. Without this an underground hall is a brick outline
        //    around untouched stone and nobody can get inside it.
        for (int x = left + 1; x <= right - 1; x++)
            for (int y = roofY + 1; y <= floorY - 1; y++)
                jobs.Add(new BuildJob(x, y, 0, BuildKind.Clear));

        // 2. Background wall across the interior, so it looks like a room.
        for (int x = left + 1; x <= right - 1; x++)
            for (int y = roofY + 1; y <= floorY - 1; y++)
                jobs.Add(new BuildJob(x, y, wallItem, BuildKind.Wall));

        // 3. Floor, with the stairwell left open.
        for (int x = left; x <= right; x++)
        {
            if (x == stairX && storey > 0)
                continue;

            jobs.Add(new BuildJob(x, floorY, block, BuildKind.Block));
        }

        // 4. Side walls, with a doorway punched through the ground storey.
        for (int y = roofY; y <= floorY - 1; y++)
        {
            bool doorway = storey == 0 && (y == floorY - 1 || y == floorY - 2);

            if (!doorway)
                jobs.Add(new BuildJob(left, y, block, BuildKind.Block));

            jobs.Add(new BuildJob(right, y, block, BuildKind.Block));
        }

        // 5. Roof. Only the top storey gets a solid one; below that the next
        //    storey's floor serves, and a platform lets them walk up onto it.
        for (int x = left + 1; x <= right - 1; x++)
            jobs.Add(new BuildJob(x, roofY, block, BuildKind.Platform));

        // 6. Light it. Two torches a storey, off the walls.
        if (lit)
        {
            jobs.Add(new BuildJob(left + 2, floorY - 1, ItemID.Torch, BuildKind.Torch));
            jobs.Add(new BuildJob(right - 2, floorY - 1, ItemID.Torch, BuildKind.Torch));
        }
    }

    /// <summary>
    /// The background wall a given building block backs onto.
    ///
    /// Walls are paid for with the same block the tribe is already building
    /// with, rather than a separate wall item. A tribe's stock is dirt and
    /// stone, so requiring actual Stone Wall items would have meant no tribe
    /// could ever afford a single background wall.
    ///
    /// Grey brick is the default on purpose: it reads as something built
    /// rather than as more cave, which is the whole point of putting a wall
    /// behind a room.
    /// </summary>
    public static ushort WallTileFor(int blockItem) => blockItem switch
    {
        ItemID.DirtBlock => WallID.Dirt,
        ItemID.MudBlock => WallID.MudUnsafe,
        ItemID.SandBlock => WallID.HardenedSand,
        ItemID.Sandstone => WallID.HardenedSand,
        ItemID.HardenedSand => WallID.HardenedSand,
        ItemID.Granite => WallID.Granite,
        ItemID.Marble => WallID.Marble,
        ItemID.IronBrick => WallID.IronBrick,
        ItemID.EbonstoneBlock => WallID.EbonstoneBrick,
        ItemID.CrimstoneBlock => WallID.CrimstoneBrick,
        _ => WallID.GrayBrick,
    };
}
