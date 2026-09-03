using System.Collections.Generic;
using Antfarm.Core;
using Terraria.ID;

namespace Antfarm.Sim;

/// <summary>
/// One building under construction, built in dependency order.
///
/// The previous version dumped every tile of a room into one flat queue and let
/// villagers grab whichever they reached first. Roofs got built before floors,
/// walls before the interior was hollowed out, and a tribe started its next
/// room long before the last was finished. Nothing ever completed, so nothing
/// ever looked like a building.
///
/// A building now advances through phases and cannot start one until the last
/// is finished, so it goes up the way a building actually goes up: hollow the
/// space, lay the floors, raise the walls, roof it, back it, then fit it out.
/// </summary>
public sealed class Building
{
    /// <summary>What a hall is, and the fallback for anything unsized.</summary>
    public const int DefaultWidth = 11;

    /// <summary>
    /// This building's footprint. It was a constant 11 for everything ever
    /// built, so a hundred storey tower came out as an eleven tile needle: a
    /// chimney, not a monument. Halls keep the old size; towers are sized by
    /// how much the tribe has built, and get wide enough to be worth looking
    /// at from across the map.
    /// </summary>
    public int Width = DefaultWidth;
    public const int StoreyHeight = 6;

    public int X;              // left edge
    public int GroundY;        // floor level of the lowest storey
    public int Storeys;

    /// <summary>
    /// Blocks actually laid into this building. A building that finishes with
    /// zero of them was never built, and must not be counted as one.
    /// </summary>
    public int BlocksLaid;

    /// <summary>How many phases of this building the stall net has given up on.</summary>
    public int Stalled;
    public bool Underground;

    public int Phase;
    public readonly List<BuildJob> Pending = new();

    /// <summary>Handed to a villager but not yet reported finished.</summary>
    public int Outstanding;

    public bool Done;

    public int Right => X + Width - 1;
    public int TopY => GroundY - (Storeys - 1) * (StoreyHeight - 1) - StoreyHeight + 1;

    public const int LastPhase = 5;

    public bool Overlaps(Building other)
        => other != null && X <= other.Right + 2 && other.X <= Right + 2;

    /// <summary>
    /// Fill <see cref="Pending"/> with everything phase <see cref="Phase"/>
    /// requires. Each phase depends on the one before it being complete.
    /// </summary>
    public void GeneratePhase(int block, SimContext ctx)
    {
        Pending.Clear();

        int wallItem = block;

        switch (Phase)
        {
            // 0. Hollow out the whole volume. Underground this is the only
            //    reason the inside of a hall is not solid stone.
            case 0:
                // Only queue tiles that are genuinely in the way. A surface
                // tower is mostly air, and queueing sixteen hundred clearing
                // jobs for empty sky meant phase 0 never finished and not one
                // block was ever laid.
                for (int s = 0; s < Storeys; s++)
                {
                    int floorY = GroundY - s * (StoreyHeight - 1);
                    for (int x = X; x <= Right; x++)
                        for (int y = floorY - StoreyHeight + 1; y <= floorY; y++)
                            if (ctx.Snapshot.IsSolid(x, y) && ctx.Built.Get(x, y) == 0)
                                Pending.Add(new BuildJob(x, y, 0, BuildKind.Clear));
                }
                break;

            // 1. Floors, bottom up, with a stairwell column left open so the
            //    storeys are actually connected.
            case 1:
                for (int s = 0; s < Storeys; s++)
                {
                    int floorY = GroundY - s * (StoreyHeight - 1);
                    for (int x = X; x <= Right; x++)
                    {
                        if (s > 0 && x == X + 1)
                            continue;               // stairwell

                        Pending.Add(new BuildJob(x, floorY, block, BuildKind.Block));
                    }
                }
                break;

            // 2. Side walls, with a doorway punched through the ground storey.
            case 2:
                for (int s = 0; s < Storeys; s++)
                {
                    int floorY = GroundY - s * (StoreyHeight - 1);
                    for (int y = floorY - StoreyHeight + 1; y < floorY; y++)
                    {
                        bool door = s == 0 && (y == floorY - 1 || y == floorY - 2);
                        if (!door)
                            Pending.Add(new BuildJob(X, y, block, BuildKind.Block));

                        Pending.Add(new BuildJob(Right, y, block, BuildKind.Block));

                        // Internal columns, so a wide building reads as bays
                        // and piers rather than one enormous hollow box. Two
                        // outer walls is all a cottage needs; a hundred tile
                        // frontage needs something holding the middle up.
                        for (int cx = X + 14; cx < Right - 2; cx += 14)
                            if (!(s == 0 && (y == floorY - 1 || y == floorY - 2)))
                                Pending.Add(new BuildJob(cx, y, block, BuildKind.Block));
                    }
                }
                break;

            // 3. Roof over the top storey.
            case 3:
            {
                int roofY = TopY;
                for (int x = X; x <= Right; x++)
                    Pending.Add(new BuildJob(x, roofY, block, BuildKind.Block));
                break;
            }

            // 4. Background wall through every interior, so it reads as indoors
            //    rather than as a shell you can see the cave through.
            case 4:
                // Skipped on anything large. Background wall is four rows per
                // storey across the full frontage, so a 120 wide 100 storey
                // tower is roughly 48,000 jobs of wallpaper that never shows
                // on the map and does not hold anything up. On a cottage it is
                // cheap and makes the inside read as indoors; on a monument it
                // is the difference between topping out and never finishing.
                if (Width <= 40)
                {
                    for (int s = 0; s < Storeys; s++)
                    {
                        int floorY = GroundY - s * (StoreyHeight - 1);
                        for (int x = X + 1; x <= Right - 1; x++)
                            for (int y = floorY - StoreyHeight + 2; y <= floorY - 1; y++)
                                Pending.Add(new BuildJob(x, y, wallItem, BuildKind.Wall));
                    }
                }
                break;

            // 5. Fit out: a platform under each upper floor so they can walk up
            //    into it, and torches on every storey.
            default:
                for (int s = 0; s < Storeys; s++)
                {
                    int floorY = GroundY - s * (StoreyHeight - 1);

                    if (s > 0)
                        Pending.Add(new BuildJob(X + 1, floorY, block, BuildKind.Platform));

                    Pending.Add(new BuildJob(X + 2, floorY - 1, ItemID.Torch, BuildKind.Torch));
                    Pending.Add(new BuildJob(Right - 2, floorY - 1, ItemID.Torch, BuildKind.Torch));
                }
                break;
        }
    }

    /// <summary>
    /// Is this job still worth doing, or has the world already satisfied it?
    /// Checked when handing work out, so a phase can finish even if some of its
    /// tiles were already correct.
    /// </summary>
    public static bool StillNeeded(SimContext ctx, in BuildJob job)
    {
        switch (job.Kind)
        {
            case BuildKind.Clear:
                // Never demolish masonry to hollow a room; it is probably ours.
                return ctx.Snapshot.IsSolid(job.X, job.Y) && ctx.Built.Get(job.X, job.Y) == 0;

            case BuildKind.Wall:
                return true;                        // walls go behind whatever is there

            default:
                return !ctx.Snapshot.IsSolid(job.X, job.Y);
        }
    }
}
