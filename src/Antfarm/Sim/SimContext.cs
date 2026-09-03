using System;
using System.Collections.Concurrent;
using Antfarm.Core;

namespace Antfarm.Sim;

/// <summary>
/// Everything the sim thread is allowed to touch. If it is not on here, the
/// colony brain must not reach for it, because it is not thread safe.
/// </summary>
public sealed class SimContext
{
    public readonly TileSnapshot Snapshot;
    public readonly ConcurrentQueue<TileOp> Ops;
    public readonly Random Rand;

    /// <summary>The news feed. Thread safe, so the colony can post to it directly.</summary>
    public readonly EventLog Events;

    /// <summary>
    /// Tiles a tribe placed. Villagers will not choose these as dig faces, so
    /// a colony stops demolishing the walls it just put up. Without it every
    /// structure was quarried away by its own builders within minutes, which
    /// is why the settlements never looked like buildings.
    /// </summary>
    public readonly MinedMask Built;

    /// <summary>
    /// Ops already queued but not yet applied. Used to stop the sim racing
    /// ahead and queueing ten thousand operations while the main thread is
    /// still chewing through the first hundred, which is how a background
    /// simulation quietly eats all your memory over a long session.
    /// </summary>
    public int PendingOpLimit = 20000;

    public SimContext(TileSnapshot snapshot, ConcurrentQueue<TileOp> ops, EventLog events,
                      MinedMask built, int seed)
    {
        Snapshot = snapshot;
        Ops = ops;
        Events = events;
        Built = built;
        Rand = new Random(seed);
    }

    public bool CanQueue => Ops.Count < PendingOpLimit;

    public void Queue(in TileOp op)
    {
        if (CanQueue)
            Ops.Enqueue(op);
    }
}
