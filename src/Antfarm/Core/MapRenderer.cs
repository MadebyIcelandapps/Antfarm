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

    public static byte[] Build(TileSnapshot snap, MinedMask mask,
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

                int tribe = -1;
                bool open = false;

                // Tribe work wins the cell, so a one tile tunnel still shows
                // after downsampling. That is the whole point of the view.
                for (int dy = 0; dy < step && tribe < 0; dy++)
                {
                    for (int dx = 0; dx < step; dx++)
                    {
                        int x = x0 + dx;
                        int y = y0 + dy;

                        byte m = mask.Get(x, y);
                        if (m != 0)
                        {
                            tribe = m - 1;
                            break;
                        }

                        if (!open && snap.IsOpen(x, y))
                            open = true;
                    }
                }

                buf[rowBase + vx] = tribe >= 0 ? (byte)(2 + tribe) : (open ? (byte)0 : (byte)1);
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
