namespace Antfarm.Core;

public enum TileOpKind : byte
{
    Mine,
    Place,
    PlaceWall,
    PlacePlatform,
}

/// <summary>
/// One requested change to the world, produced by the sim thread and applied by
/// the main thread under budget. Kept as a struct so a queue of thousands of
/// them costs nothing to hold and produces no garbage for the collector, which
/// matters when the thing is expected to run for a year.
/// </summary>
public readonly struct TileOp
{
    public readonly int X;
    public readonly int Y;
    public readonly TileOpKind Kind;
    public readonly ushort Type;
    public readonly int TribeId;

    public TileOp(TileOpKind kind, int x, int y, ushort type, int tribeId)
    {
        Kind = kind;
        X = x;
        Y = y;
        Type = type;
        TribeId = tribeId;
    }

    public static TileOp Mine(int x, int y, int tribeId)
        => new(TileOpKind.Mine, x, y, 0, tribeId);

    public static TileOp Place(int x, int y, ushort type, int tribeId)
        => new(TileOpKind.Place, x, y, type, tribeId);

    public static TileOp Wall(int x, int y, ushort type, int tribeId)
        => new(TileOpKind.PlaceWall, x, y, type, tribeId);

    public static TileOp Platform(int x, int y, ushort type, int tribeId)
        => new(TileOpKind.PlacePlatform, x, y, type, tribeId);
}
