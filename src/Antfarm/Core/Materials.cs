using Terraria.ID;

namespace Antfarm.Core;

/// <summary>
/// What a tribe does with a thing it dug up.
///
/// Two piles, and the split is what makes the colony work. Ore and gems are
/// worth keeping, so they go into chests. Dirt, stone, mud and sand are not
/// worth a chest slot but they are exactly what you build with, so they become
/// construction stock instead.
///
/// Before this split every block a tribe mined was queued for a chest, which
/// meant a tribe filled forty slots with dirt and then had nowhere to put the
/// gold it found underneath.
/// </summary>
public static class Materials
{
    /// <summary>
    /// Worth a chest slot. Only genuine finds: ore, bars and gems.
    ///
    /// This used to default to true for anything unrecognised, on the theory
    /// that a rare drop should never be binned. That was exactly backwards. A
    /// hand written list can never cover every tile in the game, so any tribe
    /// digging terrain the list missed filed its entire haul as treasure. Two
    /// tribes ended up sat on seventeen hundred items of chest loot with zero
    /// build stock, which meant no rooms, no housing, and a population frozen
    /// at its starting figure for ever while their neighbours tripled.
    ///
    /// Bulk is the safe default, because the cost of miscategorising is wildly
    /// asymmetric: a gem treated as rubble is one wasted block, but a common
    /// rock treated as treasure sterilises a tribe permanently.
    /// </summary>
    public static bool IsWorthy(int itemType) => IsNotable(itemType) || itemType == ItemID.IronBar;

    /// <summary>Actually worth announcing: ore or a gem, not a stray log.</summary>
    public static bool IsNotable(int itemType)
    {
        if (IsOre(itemType))
            return true;

        switch (itemType)
        {
            case ItemID.Amethyst:
            case ItemID.Topaz:
            case ItemID.Sapphire:
            case ItemID.Emerald:
            case ItemID.Ruby:
            case ItemID.Diamond:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Ore, as opposed to gems. Only ore can go in a furnace.</summary>
    public static bool IsOre(int itemType)
    {
        switch (itemType)
        {
            case ItemID.CopperOre:
            case ItemID.TinOre:
            case ItemID.IronOre:
            case ItemID.LeadOre:
            case ItemID.SilverOre:
            case ItemID.TungstenOre:
            case ItemID.GoldOre:
            case ItemID.PlatinumOre:
            case ItemID.DemoniteOre:
            case ItemID.CrimtaneOre:
            case ItemID.Meteorite:
            case ItemID.Hellstone:
            case ItemID.CobaltOre:
            case ItemID.PalladiumOre:
            case ItemID.MythrilOre:
            case ItemID.OrichalcumOre:
            case ItemID.AdamantiteOre:
            case ItemID.TitaniumOre:
            case ItemID.ChlorophyteOre:
            case ItemID.LunarOre:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Which tile a stockpiled building item places as. Returns 0 when the item
    /// is not something we know how to build with.
    /// </summary>
    public static ushort TileFor(int itemType)
    {
        return itemType switch
        {
            ItemID.DirtBlock => TileID.Dirt,
            ItemID.StoneBlock => TileID.Stone,
            ItemID.ClayBlock => TileID.ClayBlock,
            ItemID.SandBlock => TileID.Sand,
            ItemID.HardenedSand => TileID.HardenedSand,
            ItemID.Sandstone => TileID.Sandstone,
            ItemID.MudBlock => TileID.Mud,
            ItemID.SnowBlock => TileID.SnowBlock,
            ItemID.IceBlock => TileID.IceBlock,
            ItemID.AshBlock => TileID.Ash,
            ItemID.Granite => TileID.Granite,
            ItemID.Marble => TileID.Marble,
            ItemID.EbonstoneBlock => TileID.Ebonstone,
            ItemID.CrimstoneBlock => TileID.Crimstone,
            ItemID.PearlstoneBlock => TileID.Pearlstone,
            ItemID.IronBrick => TileID.IronBrick,
            ItemID.Torch => TileID.Torches,
            _ => 0,
        };
    }
}
