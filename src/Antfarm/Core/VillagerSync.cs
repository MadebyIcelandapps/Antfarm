using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Antfarm.Sim;

namespace Antfarm.Core;

/// <summary>
/// One villager as a client sees it: enough to draw, and nothing else.
/// </summary>
public struct GhostVillager
{
    public float X, Y;
    public byte TribeId;
    public bool FacingRight;
    public bool Undead;
    public bool Soldier;
}

/// <summary>
/// Sends villagers to players so they can actually see the colony.
///
/// The colony lives entirely on the server. Without this the client had no
/// idea any villager existed, which is why joining the server showed an empty
/// world full of tunnels that nothing had apparently dug.
///
/// Only villagers near each player are sent, because there are twelve thousand
/// of them and a player can see about sixty tiles. Position is quantised to
/// halves of a tile: a villager is a small coloured rectangle, so sub-pixel
/// precision would be bytes spent on nothing.
/// </summary>
public static class VillagerSync
{
    public const byte PacketVillagers = 1;

    /// <summary>
    /// How far around a player villagers are worth sending, in tiles.
    ///
    /// 140 was far too small for a world 8400 wide whose tribes range hundreds
    /// of tiles from their capitals. A player standing in a mined out region
    /// was told about nobody at all, correctly, and concluded the mod was
    /// broken. This is well beyond what fits on screen, but villagers are ten
    /// bytes each and being able to see a crowd approaching is the point.
    /// </summary>
    private const int RangeTiles = 420;

    /// <summary>Hard cap per packet, so a crowded settlement cannot flood the link.</summary>
    private const int MaxPerPacket = 600;

    /// <summary>What the client currently believes is on screen.</summary>
    public static readonly List<GhostVillager> Ghosts = new();

    public static void Broadcast(Mod mod, List<Tribe> tribes)
    {
        for (int p = 0; p < Main.maxPlayers; p++)
        {
            Player player = Main.player[p];
            if (player == null || !player.active)
                continue;

            SendTo(mod, tribes, p, (int)(player.Center.X / 16f), (int)(player.Center.Y / 16f));
        }
    }

    private static void SendTo(Mod mod, List<Tribe> tribes, int toClient, int px, int py)
    {
        var near = new List<Villager>(128);

        lock (tribes)
        {
            foreach (Tribe t in tribes)
            {
                foreach (Villager v in t.Villagers)
                {
                    int dx = v.TileX - px;
                    int dy = v.TileY - py;

                    if (dx * dx + dy * dy > RangeTiles * RangeTiles)
                        continue;

                    near.Add(v);
                    if (near.Count >= MaxPerPacket)
                        break;
                }

                if (near.Count >= MaxPerPacket)
                    break;
            }
        }

        ModPacket packet = mod.GetPacket();
        packet.Write(PacketVillagers);
        packet.Write((ushort)near.Count);

        foreach (Villager v in near)
        {
            packet.Write(v.X);
            packet.Write(v.Y);
            packet.Write((byte)v.TribeId);

            byte flags = 0;
            if (v.FacingRight) flags |= 1;
            if (v.Undead) flags |= 2;
            if (v.Soldier) flags |= 4;
            packet.Write(flags);
        }

        packet.Send(toClient);

        // Whether anyone is actually in range is the first thing to know when
        // a player reports seeing nobody: the colony may simply be elsewhere.
        LastSentCount = near.Count;
        LastPlayerX = px;
        LastPlayerY = py;
    }

    public static int LastSentCount;
    public static int LastPlayerX, LastPlayerY;

    /// <summary>Tiles to the nearest villager of any tribe, and where it is.</summary>
    public static int NearestVillagerDistance(List<Tribe> tribes, int px, int py,
                                              out int nx, out int ny, out string who)
    {
        long best = long.MaxValue;
        nx = ny = 0;
        who = "nobody";

        lock (tribes)
        {
            foreach (Tribe t in tribes)
                foreach (Villager v in t.Villagers)
                {
                    long dx = v.TileX - px;
                    long dy = v.TileY - py;
                    long d = dx * dx + dy * dy;

                    if (d >= best)
                        continue;

                    best = d;
                    nx = v.TileX;
                    ny = v.TileY;
                    who = t.Name;
                }
        }

        return best == long.MaxValue ? -1 : (int)System.Math.Sqrt(best);
    }

    /// <summary>Packets the client has actually received, and when the last one landed.</summary>
    public static int PacketsReceived;
    public static int LastReceivedCount;
    public static double LastReceivedAt;

    public static void Receive(BinaryReader reader)
    {
        int count = reader.ReadUInt16();

        PacketsReceived++;
        LastReceivedCount = count;
        LastReceivedAt = Main.gameTimeCache?.TotalGameTime.TotalSeconds ?? 0;

        lock (Ghosts)
        {
            Ghosts.Clear();

            for (int i = 0; i < count; i++)
            {
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                byte tribe = reader.ReadByte();
                byte flags = reader.ReadByte();

                Ghosts.Add(new GhostVillager
                {
                    X = x,
                    Y = y,
                    TribeId = tribe,
                    FacingRight = (flags & 1) != 0,
                    Undead = (flags & 2) != 0,
                    Soldier = (flags & 4) != 0,
                });
            }
        }
    }
}
