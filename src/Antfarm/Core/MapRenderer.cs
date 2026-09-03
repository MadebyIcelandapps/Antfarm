using System;

namespace Antfarm.Core;

/// <summary>
/// Turns the world into the compact byte picture the page paints.
///
/// One byte per downsampled cell: 0 open, 1 untouched rock, 2 + tribeId dug by
/// that tribe. A twenty byte header carries the region actually rendered, so a
/// viewer never has to trust that it got the region it asked for.
///
/// Shared by the live map and the timelapse recorder, because a recorded frame
/// and a live frame must be byte identical: the page has one painter, and a
/// second copy of this logic would drift from it.
/// </summary>
public static class MapRenderer
{
    public const int HeaderBytes = 20;

    /// <summary>Roughly how many pixels wide a rendered region should come out.</summary>
    public const int TargetWidth = 1100;

    /// <summary>Where the built-tile colours start, one per tribe.</summary>
    public const int BuiltBase = 12;

    public static byte[] Build(TileSnapshot snap, MinedMask mask, MinedMask built,
                               int regionX, int regionY, int regionW, int regionH)
    {
        int worldW = snap.Width;
        int worldH = snap.Height;

        if (worldW <= 0 || worldH <= 0)
            return new byte[HeaderBytes];

        if (regionW <= 0 || regionH <= 0)
        {
            regionX = 0;
            regionY = 0;
            regionW = worldW;
            regionH = worldH;
        }

        regionW = Math.Clamp(regionW, 16, worldW);
        regionH = Math.Clamp(regionH, 16, worldH);
        regionX = Math.Clamp(regionX, 0, worldW - regionW);
        regionY = Math.Clamp(regionY, 0, worldH - regionH);

        int step = Math.Max(1, (int)Math.Ceiling(regionW / (double)TargetWidth));
        int vw = regionW / step;
        int vh = regionH / step;

        if (vw <= 0 || vh <= 0)
            return new byte[HeaderBytes];

        var buf = new byte[HeaderBytes + vw * vh];
        var tally = new int[32];        // votes per tribe within one cell
        WriteInt(buf, 0, vw);
        WriteInt(buf, 4, vh);
        WriteInt(buf, 8, regionX);
        WriteInt(buf, 12, regionY);
        WriteInt(buf, 16, step);

        for (int vy = 0; vy < vh; vy++)
        {
            int rowBase = HeaderBytes + vy * vw;
            int y0 = regionY + vy * step;

            for (int vx = 0; vx < vw; vx++)
            {
                int x0 = regionX + vx * step;

                // Majority, not first-hit.
                //
                // The old rule was "if any tile in this block was dug by a
                // tribe, colour the whole block". At one pixel per 8x8 tiles a
                // single dug tile painted sixty-four tiles' worth of colour, so
                // territory looked enormous zoomed out and sparse zoomed in.
                // Colours appeared and vanished as you zoomed, because the two
                // scales genuinely disagreed about what was there.
                //
                // Counting and taking the majority makes every zoom level agree
                // with every other, and with the world.
                int open = 0, solid = 0;
                int bestTribe = -1, bestCount = 0;

                // Masonry beats excavation for the cell's colour.
                //
                // The map drew MinedMask alone, which is what a tribe has DUG.
                // A tower is placed, not dug, so a hundred storey tower left
                // no mark whatsoever and the map showed ten colonies growing
                // only downward and sideways. That was the map's blind spot,
                // not the tribes': the one thing this panel exists to show was
                // the one thing it could not draw.
                int bestBuilt = -1;

                // Bound the work on big blocks: sampling every other tile is
                // plenty to find a majority and keeps a full world redraw cheap.
                int stride = step > 4 ? 2 : 1;

                for (int dy = 0; dy < step; dy += stride)
                {
                    for (int dx = 0; dx < step; dx += stride)
                    {
                        int x = x0 + dx;
                        int y = y0 + dy;

                        if (built != null)
                        {
                            byte bm = built.Get(x, y);
                            if (bm != 0)
                                bestBuilt = bm - 1;
                        }

                        byte m = mask.Get(x, y);

                        if (m != 0)
                        {
                            int id = m - 1;
                            int count = ++tally[id & 31];

                            if (count > bestCount)
                            {
                                bestCount = count;
                                bestTribe = id;
                            }
                        }
                        else if (snap.IsOpen(x, y))
                        {
                            open++;
                        }
                        else
                        {
                            solid++;
                        }
                    }
                }

                // Reset only what was touched; clearing 32 entries per cell
                // would cost more than the sampling itself.
                if (bestTribe >= 0)
                    for (int dy = 0; dy < step; dy += stride)
                        for (int dx = 0; dx < step; dx += stride)
                        {
                            byte m = mask.Get(x0 + dx, y0 + dy);
                            if (m != 0)
                                tally[(m - 1) & 31] = 0;
                        }

                // Territory is drawn thicker than true scale, on purpose.
                //
                // A tunnel is one or two tiles wide, so in an 8x8 block it is
                // about an eighth of the tiles and can never win a majority. A
                // majority rule rendered the tribes at 0.025% of the map:
                // stable, and blank. Every map ever drawn has this problem with
                // thin features and solves it the same way, which is why roads
                // are wider than scale on a road atlas.
                //
                // So any tribe work in a block colours it. Rock against hollow
                // is still decided by majority, which is what stopped the
                // coastline flickering. The zoom label tells you the scale, and
                // zooming in shows the true width.
                buf[rowBase + vx] =
                    bestBuilt >= 0 ? (byte)(BuiltBase + bestBuilt)
                    : bestTribe >= 0 ? (byte)(2 + bestTribe)
                    : open >= solid ? (byte)0
                    : (byte)1;

            }
        }

        return buf;
    }

    private static void WriteInt(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}
