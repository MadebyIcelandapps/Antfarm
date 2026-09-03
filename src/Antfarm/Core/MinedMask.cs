namespace Antfarm.Core;

/// <summary>
/// Who dug what: one byte per tile, 0 for untouched and tribe id + 1 otherwise.
///
/// The snapshot already knows which tiles are open, but natural caves are open
/// too, so a view built from it alone cannot tell a tribe's work from terrain
/// that was always there. This is what makes the observation map readable: you
/// watch ten coloured networks spread through grey rock.
///
/// Costs one byte per tile, so 5 MB on a small world and 20 MB on a large one.
/// Held in memory only and not saved: on a restart the colouring starts blank
/// and refills as they keep digging. Worth revisiting if the history turns out
/// to matter more than the twenty megabytes.
/// </summary>
public sealed class MinedMask
{
    private byte[] _by;
    private int _width;
    private int _height;

    public int Width => _width;
    public int Height => _height;

    public void Rebuild(int width, int height)
    {
        _width = width;
        _height = height;
        _by = new byte[(long)width * height];
    }

    public void Set(int x, int y, int tribeId)
    {
        if (_by == null || x < 0 || y < 0 || x >= _width || y >= _height)
            return;

        _by[(long)x * _height + y] = (byte)(tribeId + 1);
    }

    /// <summary>0 when untouched, otherwise tribe id + 1.</summary>
    public byte Get(int x, int y)
    {
        if (_by == null || x < 0 || y < 0 || x >= _width || y >= _height)
            return 0;

        return _by[(long)x * _height + y];
    }
}
