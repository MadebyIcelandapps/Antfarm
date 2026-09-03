using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Antfarm.Sim;
using Terraria.ModLoader;

namespace Antfarm.Core;

/// <summary>
/// The colony's own heartbeat.
///
/// This is the piece that makes "leave it running in the background for a year"
/// actually true. Terraria drops its update rate when the window loses focus,
/// so a simulation living inside PostUpdateWorld would crawl the moment you
/// alt-tab. This thread has its own clock and does not care whether anyone is
/// looking.
///
/// It only ever reads the TileSnapshot and writes to the op queue. It must
/// never touch Main.tile, Main.chest, Main.rand or anything else in Terraria.
/// </summary>
public sealed class SimThread
{
    private Thread _thread;
    private volatile bool _running;

    private readonly SimContext _ctx;
    private readonly List<Tribe> _tribes;

    public int TargetHz = 60;

    /// <summary>Ticks completed. Useful for proving it is still alive after a week.</summary>
    public long Ticks;

    public SimThread(SimContext ctx, List<Tribe> tribes)
    {
        _ctx = ctx;
        _tribes = tribes;
    }

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _thread = new Thread(Loop)
        {
            Name = "Antfarm colony",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double next = 0;

        while (_running)
        {
            double period = 1000.0 / Math.Max(1, TargetHz);
            double now = clock.Elapsed.TotalMilliseconds;

            if (now < next)
            {
                Thread.Sleep(1);
                continue;
            }

            // If we fall a long way behind, do not try to catch up by running
            // a thousand ticks at once. Drop the debt and carry on.
            next = now + period;
            if (now - next > 1000)
                next = now;

            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // A crash on this thread must never take the game down, and
                // must never fail silently either.
                ModContent.GetInstance<Antfarm>()?.Logger.Error("Antfarm colony thread: " + ex);
                Thread.Sleep(250);
            }

            Ticks++;

            // Heartbeat from the colony thread itself. This is deliberately
            // separate from the main thread's status line: if this one climbs
            // and that one does not, the brain is alive and the game is not
            // applying its work, which is a completely different fault to the
            // brain having died. Without both, the two look identical.
            if (Ticks % 3600 == 0)
            {
                ModContent.GetInstance<Antfarm>()?.Logger.Info(
                    $"antfarm sim thread alive: ticks={Ticks} queued={_ctx.Ops.Count}");
            }
        }
    }

    private void Tick()
    {
        // Back off when the main thread has not kept up. Without this the sim
        // races ahead and the queue grows without bound.
        if (!_ctx.CanQueue)
            return;

        lock (_tribes)
        {
            foreach (Tribe tribe in _tribes)
            {
                tribe.Tick(_ctx);

                List<Villager> villagers = tribe.Villagers;
                for (int i = villagers.Count - 1; i >= 0; i--)
                {
                    Villager v = villagers[i];

                    if (!v.Alive)
                    {
                        villagers.RemoveAt(i);
                        tribe.Losses++;

                        // Onto the roll of the dead, name and record intact,
                        // because a wiped out tribe rises from exactly this
                        // list rather than as a fresh set of strangers.
                        tribe.RecordFallen(v);

                        // Only the ones who did enough to be worth a line. A
                        // feed full of "someone died" is noise.
                        if (v.TilesDug > 200 || v.Kills > 0)
                            _ctx.Events.Add(EventKind.Battle, tribe.Id,
                                $"{v.Name} of {tribe.Name} died at depth {v.DeepestY}, " +
                                $"having dug {v.TilesDug} blocks and killed {v.Kills}");
                        continue;
                    }

                    v.Update(_ctx, tribe);
                }
            }
        }
    }
}
