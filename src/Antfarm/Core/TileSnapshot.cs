using Terraria;

namespace Antfarm.Core;

/// <summary>
/// A thread safe, read only picture of which tiles are solid.
///
/// Why this exists: the colony brain runs on its own thread so it keeps full
/// speed while the game window is in the background. Terraria's Main.tile array
/// is not thread safe, and reading it from another thread while the main thread
/// writes to it will eventually tear or crash. Over an afternoon you would
/// probably get away with it. Over a year you absolutely would not.
///
/// So the sim thread never touches Main.tile. It reads this instead: one bit
/// per tile, packed into ulongs. A large world is 8400 x 2400, which is about
/// 2.5 MB of bits. Cheap enough to hold forever.
///
/// Coherency is maintained two ways:
///   1. Every tile op we apply updates the bit immediately, so the sim sees its
///      own work straight away.
///   2. A slow rolling rescan sweeps the whole world every few seconds to pick
///      up changes we did not cause: the player digging, grass growing, sand
///      falling, bombs, other mods.
///
/// A stale bit is harmless. The worst case is a villager walking into a block
/// that just appeared, or briefly trying to mine air. Both self correct on the
/// next tick.
/// </summary>
public sealed class TileSnapshot
{
    private ulong[] _bits;
    private int _width;
    private int _height;

    // Rolling rescan cursor, in tile columns.
    private int _sweepX;

    public int Width => _width;
    public int Height => _height;

    /// <summary>Full rebuild. Main thread only. Called once on world load.</summary>
    public void Rebuild()
    {
        _width = Main.maxTilesX;
        _height = Main.maxTilesY;

        long count = (long)_width * _height;
        _bits = new ulong[(count + 63) / 64];
        _sweepX = 0;

        for (int x = 0; x < _width; x++)
            RescanColumn(x);
    }

    /// <summary>
    /// Rescan a slice of the world. Main thread only. Called every tick with a
    /// small number of columns so a full sweep completes every few seconds
    /// without ever costing a visible spike.
    /// </summary>
    public void Sweep(int columns)
    {
        if (_bits == null)
            return;

        for (int n = 0; n < columns; n++)
        {
            RescanColumn(_sweepX);
            _sweepX++;
            if (_sweepX >= _width)
                _sweepX = 0;
        }
    }

    private void RescanColumn(int x)
    {
        for (int y = 0; y < _height; y++)
        {
            Tile t = Main.tile[x, y];
            bool solid = t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
            SetBit(x, y, solid);
        }
    }

    /// <summary>Update one tile. Main thread only, called as ops are applied.</summary>
    public void Set(int x, int y, bool solid)
    {
        if (InBounds(x, y))
            SetBit(x, y, solid);
    }

    /// <summary>
    /// Is this tile solid? Safe to call from the sim thread.
    /// Out of bounds reads as solid so villagers treat the world edge as wall
    /// rather than walking into an exception.
    /// </summary>
    public bool IsSolid(int x, int y)
    {
        if (_bits == null || !InBounds(x, y))
            return true;

        long idx = (long)x * _height + y;
        return (_bits[idx >> 6] & (1UL << (int)(idx & 63))) != 0;
    }

    public bool IsOpen(int x, int y) => !IsSolid(x, y);

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height;

    private void SetBit(int x, int y, bool value)
    {
        long idx = (long)x * _height + y;
        ulong mask = 1UL << (int)(idx & 63);

        if (value)
            _bits[idx >> 6] |= mask;
        else
            _bits[idx >> 6] &= ~mask;
    }
}
