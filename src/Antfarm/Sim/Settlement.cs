namespace Antfarm.Sim;

/// <summary>
/// One place a tribe lives. A tribe starts with one and founds more as it
/// outgrows them, which is what turns a single hole in the ground into a
/// spreading civilisation.
/// </summary>
public sealed class Settlement
{
    public int X;
    public int Y;

    /// <summary>
    /// Where the next room goes in the layout. Always advances, even when a
    /// planned room turns out to be inside solid rock and gets discarded, so
    /// the planner moves on instead of re-planning the same doomed slot.
    ///
    /// This is deliberately NOT the housing figure. It used to be both, and
    /// because it advanced on planning rather than on building, one tribe
    /// claimed 145,114 rooms off 344 placed blocks, which inflated its
    /// population ceiling to 870,784.
    /// </summary>
    public int Slot;

    /// <summary>Shell tiles placed toward the room currently under construction.</summary>
    public int RoomProgress;

    public Settlement(int x, int y)
    {
        X = x;
        Y = y;
    }
}
