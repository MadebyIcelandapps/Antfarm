using System;
using System.Reflection;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace Antfarm.Core;

/// <summary>
/// What a tile turns into when a villager breaks it.
///
/// Villagers mine with the drop suppressed, so material never litters the floor
/// as loose items. Instead the tribe is credited directly and the villager
/// carries it home. That keeps a year long dig from filling the world with
/// thousands of item entities, which would tank the game long before the
/// tunnels ever got interesting.
///
/// The mapping is Terraria's own. An earlier version used a hand written table
/// of tile ids, which looked complete and was not: a tribe seeded on a cloud
/// island mined two hundred tiles and stockpiled nothing, because Cloud was not
/// in the table. Anything hand maintained here drifts the same way.
///
/// The game's resolver is internal, so it is bound once as a delegate rather
/// than reflected per tile. If a future tModLoader renames it, the binding
/// fails loudly in the log once and the fallback table takes over, which is a
/// degraded mod rather than a crashed one.
/// </summary>
public static class TileDrops
{
    private delegate void GetItemDropsDelegate(
        int x, int y, Tile tileCache,
        out int dropItem, out int dropItemStack,
        out int secondaryItem, out int secondaryItemStack,
        bool includeLargeObjectDrops);

    private static GetItemDropsDelegate _getItemDrops;
    private static bool _bindAttempted;

    private static void Bind()
    {
        _bindAttempted = true;

        try
        {
            MethodInfo mi = typeof(WorldGen).GetMethod(
                "KillTile_GetItemDrops",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (mi == null)
            {
                ModContent.GetInstance<Antfarm>()?.Logger.Warn(
                    "antfarm: WorldGen.KillTile_GetItemDrops not found; " +
                    "falling back to the built in drop table, so exotic tiles will not be stockpiled");
                return;
            }

            _getItemDrops = (GetItemDropsDelegate)Delegate.CreateDelegate(typeof(GetItemDropsDelegate), mi);
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<Antfarm>()?.Logger.Warn("antfarm: could not bind tile drop resolver: " + ex.Message);
        }
    }

    public static void Resolve(int x, int y, Tile tile, out int itemType, out int stack)
    {
        if (!_bindAttempted)
            Bind();

        if (_getItemDrops != null)
        {
            _getItemDrops(x, y, tile, out itemType, out stack, out int _, out int _, false);

            if (itemType > 0)
            {
                if (stack < 1)
                    stack = 1;
                return;
            }
        }

        itemType = Fallback(tile.TileType);
        stack = itemType > 0 ? 1 : 0;
    }

    private static int Fallback(ushort tileType) => tileType switch
    {
        TileID.Dirt => ItemID.DirtBlock,
        TileID.Stone => ItemID.StoneBlock,
        TileID.Grass => ItemID.DirtBlock,
        TileID.Mud => ItemID.MudBlock,
        TileID.ClayBlock => ItemID.ClayBlock,
        TileID.Sand => ItemID.SandBlock,
        TileID.HardenedSand => ItemID.HardenedSand,
        TileID.Sandstone => ItemID.Sandstone,
        TileID.SnowBlock => ItemID.SnowBlock,
        TileID.IceBlock => ItemID.IceBlock,
        TileID.Silt => ItemID.SiltBlock,
        TileID.Slush => ItemID.SlushBlock,
        TileID.Ash => ItemID.AshBlock,
        TileID.Cloud => ItemID.Cloud,
        TileID.RainCloud => ItemID.RainCloud,
        TileID.Granite => ItemID.Granite,
        TileID.Marble => ItemID.Marble,
        _ => 0,
    };
}
