using Antfarm.Core;
using Terraria.ID;

namespace Antfarm.Sim;

/// <summary>
/// What a villager does for a living.
///
/// Construction used to be an opportunistic job offered to whoever happened to
/// be idle nearby. Almost nobody ever was: the tribe roams 420 tiles wide,
/// eight settlements and 300 tiles deep, so "nearby" was a moving target and
/// the rejection counter read far=3520 against handed=1. A trade fixes it.
/// A mason walks to the building once and stays until it is finished.
/// </summary>
public enum VillagerRole : byte
{
    Miner,      // digs, carries, delivers
    Mason,      // builds, and does nothing else
    Soldier,    // fights, and holds the settlement
}

public enum VillagerTask : byte
{
    Idle,
    Outbound,   // heading to a dig face, tunnelling through whatever is in the way
    Returning,  // tunnelling back to the settlement with a full load
    Depositing,
    Building,   // carrying rubble to a construction site and placing it
    Fighting,   // rallying to a threat against the tribe
    Fleeing,    // not a soldier, and something is killing people nearby
}

/// <summary>
/// One villager. Not a vanilla NPC, on purpose.
///
/// Terraria allocates a fixed array of 200 NPC slots at startup and shares it
/// with every slime, bat and boss in the world. Ten tribes would get fifteen
/// each. A vanilla NPC also carries loot tables, buff slots, immunity frames
/// and netcode that a digging villager will never use.
///
/// So this is a plain object with a position, a job, and enough health to be
/// worth defending. It costs a fraction of an NPC, there is no cap on how many
/// can exist, and it is simulated on the colony thread rather than the render
/// thread.
/// </summary>
public sealed class Villager
{
    public const float TileSize = 16f;
    public const float Width = 10f;
    public const float Height = 20f;

    public float X, Y;
    public float VelX, VelY;
    public int TribeId;
    public bool OnGround;
    public bool FacingRight = true;

    public VillagerTask Task;

    /// <summary>Who this is. Cheap, because they were already individual objects.</summary>
    public string Name = "";

    public long TilesDug;
    public int Kills;
    public int DeepestY;

    /// <summary>The villager's trade. Set by the tribe, and rarely changed.</summary>
    public VillagerRole Role;

    /// <summary>Soldiers rally to threats. Everyone else runs from them.</summary>
    public bool Soldier => Role == VillagerRole.Soldier;

    /// <summary>0 is bare hands. Higher tiers come from smelted bars.</summary>
    public int Weapon;

    /// <summary>Came back up out of the ground. Keeps its name and its record.</summary>
    public bool Undead;

    /// <summary>What this villager inherited. Drives digging, carrying, health and nerve.</summary>
    public Genes Genes = Genes.Founder();

    public int MaxHealth = 60;
    public int Health = 60;

    /// <summary>Ticks left before this villager can swing again.</summary>
    public int AttackCooldown;

    /// <summary>Set by the main thread when something hostile is close.</summary>
    public bool Threatened;

    /// <summary>Bracing against a shaft wall this tick, so gravity does not apply.</summary>
    public bool Climbing;

    // Where it is headed. For Outbound this is the dig face; for Building it is
    // the block to place.
    public int TargetX, TargetY;

    // What it is placing, when Building, and what sort of work it is.
    public int BuildItem;
    public BuildKind BuildWanted;

    // How much it is carrying home. What that material actually is gets
    // resolved on the main thread when the dig lands, because only the main
    // thread may read a tile's type.
    public int CarriedCount;

    /// <summary>
    /// Tuned against the dig rate, not picked. At 60 a villager mined for two
    /// and a half minutes before heading home, wandering further out the whole
    /// time, so the carried pool grew into the hundreds and deliveries sat at
    /// zero. At 24 a round trip closes often enough to keep construction fed.
    /// Hoarder tribes carry more.
    /// </summary>
    // Behaviour comes from the villager's own genes, scaled off the tribe's
    // baseline. Two siblings in the same tribe are no longer identical.
    private int CarryCapacity => Genes.CarryCapacity(_owner?.CarryCapacity ?? 24);
    private int DigTicks => Genes.DigTicks(_owner?.DigTicks ?? 16);

    private int _digTimer;
    private int _digCooldown;
    private int _stuckTimer;
    private int _lastDigX = -1, _lastDigY = -1;

    /// <summary>Closest we have got to the current goal, for detecting no progress.</summary>
    private int _bestDist = int.MaxValue;

    // Height is tracked apart from distance because climbing is the only part
    // of moving that can fail outright. See the staircase in MoveToward.
    private int _bestY = int.MaxValue;
    private int _climbTimer;
    private VillagerTask _lastTask = VillagerTask.Idle;

    public int TileX => (int)(X / TileSize);
    public int TileY => (int)(Y / TileSize);

    public bool Alive => Health > 0;

    private const float MoveSpeed = 1.6f;
    private const float Gravity = 0.32f;
    private const float MaxFall = 9f;

    /// <summary>One villager's full state, for working out why nothing is happening.</summary>
    public string Describe() =>
        $"task={Task} pos=({TileX},{TileY}) tgt=({TargetX},{TargetY}) " +
        $"d=({TargetX - TileX},{TargetY - TileY}) dig={_digTimer} cd={_digCooldown} " +
        $"stuck={_stuckTimer} ground={OnGround} vel=({VelX:0.0},{VelY:0.0}) " +
        $"carry={CarriedCount} hp={Health}";

    private Tribe _owner;

    public void Update(SimContext ctx, Tribe tribe)
    {
        // Held so the deeper helpers can reach the tribe without threading it
        // through every call. Reassigned every tick, so it is never stale.
        _owner = tribe;

        if (_digCooldown > 0)
            _digCooldown--;

        if (AttackCooldown > 0)
            AttackCooldown--;

        // Re-established each tick by whoever is moving; gravity resumes the
        // moment nothing is bracing.
        Climbing = false;

        // A new task means a new goal, so the closest-approach record from the
        // last one must not carry over and instantly look like a stall.
        if (Task != _lastTask)
        {
            _lastTask = Task;
            _bestDist = int.MaxValue;
            _bestY = int.MaxValue;
            _climbTimer = 0;
            _stuckTimer = 0;
        }

        // A threat overrides whatever anyone was doing. Soldiers converge on it
        // and everyone else runs, which is what makes an attack look like an
        // attack rather than a slow leak in the population counter.
        if (tribe.ThreatActive && Task != VillagerTask.Fighting && Task != VillagerTask.Fleeing)
        {
            int tdx = tribe.ThreatX - TileX;
            int tdy = tribe.ThreatY - TileY;
            int near = tdx * tdx + tdy * tdy;

            // The dead do not run. They also do not much care who is winning.
            if ((Soldier || Undead) && near <= 140 * 140)
                Task = VillagerTask.Fighting;
            else if (!Soldier && !Undead && near <= Genes.FleeRangeSq())
                Task = VillagerTask.Fleeing;
        }

        switch (Task)
        {
            case VillagerTask.Idle:
                TakeJob(ctx, tribe);
                break;

            case VillagerTask.Fighting:
                if (!tribe.ThreatActive)
                {
                    Task = VillagerTask.Idle;
                    break;
                }

                // Stand and fight once close; the main thread resolves blows.
                if (!MoveToward(ctx, tribe.ThreatX, tribe.ThreatY, dig: true))
                    Wedged(ctx, tribe);
                break;

            case VillagerTask.Fleeing:
                if (!tribe.ThreatActive || tribe.NearStockpile(TileX, TileY))
                {
                    Task = VillagerTask.Idle;
                    break;
                }

                MoveToward(ctx, tribe.HomeX, tribe.HomeY, dig: true);
                break;

            case VillagerTask.Outbound:
                StepOutbound(ctx, tribe);
                break;

            case VillagerTask.Returning:
                StepReturning(ctx, tribe);
                break;

            case VillagerTask.Building:
                StepBuilding(ctx, tribe);
                break;

            case VillagerTask.Depositing:
                tribe.Deposit(CarriedCount);
                CarriedCount = 0;
                Task = VillagerTask.Idle;
                break;
        }

        Physics(ctx);
    }

    private void TakeJob(SimContext ctx, Tribe tribe)
    {
        // Everyone works their own trade. A mason never picks up a pick, which
        // is what stops construction competing with mining for the same bodies.
        if (Role == VillagerRole.Mason)
        {
            // Get to the site before asking for work there.
            //
            // Without this a mason on the far side of the world took a job it
            // could never reach, timed out, handed it back and took it again,
            // for ever. Travel is its own act: walk to the building first, then
            // become eligible for jobs on it.
            if (tribe.SiteX != 0 && !NearSite(tribe))
            {
                TargetX = tribe.SiteX;
                TargetY = tribe.SiteY;
                BuildItem = TravelMarker;
                BuildWanted = BuildKind.Clear;
                Task = VillagerTask.Building;
                _stuckTimer = 0;
                return;
            }

            if (tribe.TryTakeBuildJob(ctx, TileX, TileY, out BuildJob job))
            {
                TargetX = job.X;
                TargetY = job.Y;
                BuildItem = job.Item;
                BuildWanted = job.Kind;
                Task = VillagerTask.Building;
                _stuckTimer = 0;
                return;
            }

            return;   // at the site with nothing to do; wait here
        }

        if (Role == VillagerRole.Soldier && !tribe.ThreatActive && CarriedCount == 0)
        {
            // Off duty: hold the nearest settlement instead of mining.
            tribe.NearestSettlementTo(TileX, TileY, out int hx, out int hy);

            if (!tribe.NearStockpile(TileX, TileY))
            {
                TargetX = hx;
                TargetY = hy;
                Task = VillagerTask.Returning;
            }

            return;
        }

        if (tribe.TryTakeDigTarget(ctx, TileX, TileY, out int dx2, out int dy2))
        {
            TargetX = dx2;
            TargetY = dy2;
            Task = VillagerTask.Outbound;
            _stuckTimer = 0;
        }
    }

    private void StepOutbound(SimContext ctx, Tribe tribe)
    {
        // Full load, or hurt badly enough to want to be home. Either way, go.
        if (CarriedCount >= CarryCapacity || Health < MaxHealth / 3)
        {
            tribe.ReleaseDigTarget(TargetX, TargetY);
            Task = CarriedCount > 0 ? VillagerTask.Returning : VillagerTask.Idle;
            _stuckTimer = 0;
            return;
        }

        if (MoveToward(ctx, TargetX, TargetY, dig: true))
        {
            // Reached the face. Take another one rather than walking home with
            // three blocks. Returning after every target meant villagers spent
            // their lives commuting: 186 tiles mined produced 6 deliveries,
            // and the carried pool just kept growing because almost nothing
            // ever arrived. Now they work until the pack is full.
            tribe.ReleaseDigTarget(TargetX, TargetY);
            Task = VillagerTask.Idle;
            _stuckTimer = 0;
            return;
        }

        Wedged(ctx, tribe);
    }

    /// <summary>
    /// Tunnel home rather than retrace the outbound route.
    ///
    /// The first version dropped a breadcrumb on every tile and walked the list
    /// backwards. Trails reached eight hundred entries, which is about four
    /// minutes of walking, and the stuck timer gave up after sixty seconds. So
    /// villagers mined constantly and almost never delivered: hauling climbed
    /// into the hundreds while deliveries sat at zero.
    ///
    /// They are miners. Digging a straight line home is both faster and more
    /// in character, and it carves the arterial tunnels between the dig face
    /// and the settlement that make the map worth looking at.
    /// </summary>
    /// <summary>
    /// Haul to the nearest chest, and if there is not one, make one here.
    ///
    /// This used to head for the nearest settlement, which is a point fixed at
    /// founding. The colony digs away from it for hours, so the walk home grew
    /// without limit until villagers spent their entire lives in transit and
    /// the economy stopped. Chests follow the dig front instead, so a round
    /// trip stays roughly the same length however deep the tribe goes.
    /// </summary>
    private void StepReturning(SimContext ctx, Tribe tribe)
    {
        const int CacheRange = 60;

        if (!tribe.NearestChest(TileX, TileY, out int cx, out int cy, out int dist) || dist > CacheRange)
        {
            // The dig front has outrun the tribe's storage. Drop a cache here
            // and let the tribe build a chest on it.
            tribe.RequestCache(TileX, TileY);
            Task = VillagerTask.Depositing;
            _stuckTimer = 0;
            return;
        }

        int dx = cx - TileX;
        int dy = cy - TileY;

        if (dx * dx + dy * dy <= 8 * 8 || MoveToward(ctx, cx, cy, dig: true))
        {
            Task = VillagerTask.Depositing;
            _stuckTimer = 0;
            return;
        }

        // A hauler that genuinely cannot reach even a nearby chest caches where
        // it stands rather than being stuck for ever holding the load.
        _stuckTimer++;
        if (_stuckTimer > 60 * 20)
        {
            _stuckTimer = 0;
            tribe.RequestCache(TileX, TileY);
            Task = VillagerTask.Depositing;
        }
    }

    /// <summary>Carry out the job in hand: dig it, place it, wall it or light it.</summary>
    private void DoBuild(SimContext ctx, Tribe tribe)
    {
        switch (BuildWanted)
        {
            case BuildKind.Clear:
                ctx.Queue(TileOp.Mine(TargetX, TargetY, TribeId));
                break;

            case BuildKind.Wall:
                if (tribe.ConsumeBuildItem(BuildItem))
                    ctx.Queue(TileOp.Wall(TargetX, TargetY,
                        Architect.WallTileFor(BuildItem), TribeId));
                break;

            case BuildKind.Platform:
                if (tribe.ConsumeBuildItem(BuildItem))
                    ctx.Queue(TileOp.Platform(TargetX, TargetY, TileID.Platforms, TribeId));
                break;

            case BuildKind.Torch:
                if (tribe.ConsumeBuildItem(BuildItem))
                    ctx.Queue(TileOp.Place(TargetX, TargetY, TileID.Torches, TribeId));
                break;

            default:
                if (tribe.ConsumeBuildItem(BuildItem))
                {
                    ushort tile = Materials.TileFor(BuildItem);
                    if (tile != 0)
                        ctx.Queue(TileOp.Place(TargetX, TargetY, tile, TribeId));
                }
                break;
        }
    }

    /// <summary>Marks a "walk to the building site" instruction rather than a real job.</summary>
    public const int TravelMarker = -1;

    /// <summary>Close enough to the site to be given work on it.</summary>
    private bool NearSite(Tribe tribe)
    {
        int dx = tribe.SiteX - TileX;
        int dy = tribe.SiteY - TileY;
        return dx > -45 && dx < 45 && dy > -25 && dy < 25;
    }

    private void StepBuilding(SimContext ctx, Tribe tribe)
    {
        // Travelling to the site, not building yet.
        if (BuildItem == TravelMarker)
        {
            if (NearSite(tribe) || MoveToward(ctx, TargetX, TargetY, dig: true))
            {
                Task = VillagerTask.Idle;
                return;
            }

            // A mason that genuinely cannot reach the site goes back to the
            // pick rather than walking into a wall for the rest of its life.
            _stuckTimer++;
            if (_stuckTimer > 60 * 25)
            {
                _stuckTimer = 0;
                Role = VillagerRole.Miner;
                Task = VillagerTask.Idle;
            }

            return;
        }

        int dx = TargetX - TileX;
        int dy = TargetY - TileY;

        // Close enough to place it.
        if (dx * dx + dy * dy <= 25)
        {
            if (_digCooldown == 0)
            {
                DoBuild(ctx, tribe);
                _digCooldown = 4;

                // Take the next piece of the same building rather than going
                // idle and being re-dispatched. Every tile of a room is next to
                // the last one, so returning to the job queue between each
                // block meant construction was almost entirely walking: five
                // hundred blocks placed against three and a half million mined.
                if (tribe.TryTakeBuildJob(ctx, TileX, TileY, out BuildJob next))
                {
                    TargetX = next.X;
                    TargetY = next.Y;
                    BuildItem = next.Item;
                    BuildWanted = next.Kind;
                    _stuckTimer = 0;
                }
                else
                {
                    Task = VillagerTask.Idle;
                }
            }

            return;
        }

        if (MoveToward(ctx, TargetX, TargetY, dig: true))
            return;

        // Give the job back quickly rather than blocking the whole phase.
        // Give the job back unfinished rather than losing it.
        _stuckTimer++;
        if (_stuckTimer > 60 * 12)
        {
            _stuckTimer = 0;
            Task = VillagerTask.Idle;
        }
    }

    private void Wedged(SimContext ctx, Tribe tribe)
    {
        _stuckTimer++;
        if (_stuckTimer <= 60 * 8)
            return;

        _stuckTimer = 0;
        tribe.ReleaseDigTarget(TargetX, TargetY);
        Task = CarriedCount > 0 ? VillagerTask.Returning : VillagerTask.Idle;
    }

    /// <summary>
    /// Head for a tile, chewing through anything solid in the way. Returns true
    /// once standing on it. Shared by every task, so outbound, hauling and
    /// construction all move and dig identically.
    /// </summary>
    private bool MoveToward(SimContext ctx, int goalX, int goalY, bool dig)
    {
        int tx = TileX, ty = TileY;
        int dx = goalX - tx, dy = goalY - ty;

        if (dx == 0 && dy == 0)
            return true;

        int stepX = System.Math.Sign(dx);
        int stepY = System.Math.Sign(dy);

        // The body is 20px tall, so it straddles two tile rows. Collision uses
        // both; the dig decision must use both as well. When those two
        // disagreed, villagers stood against walls they never considered
        // digging and froze there permanently.
        int headY = (int)((Y - Height * 0.5f) / TileSize);
        int feetY = (int)((Y + Height * 0.5f - 1f) / TileSize);

        bool digging = false;

        // Always make horizontal progress when there is any to make. Gating
        // this on |dx| >= |dy| meant a target mostly above left VelX at zero,
        // and the villager just pogo-sticked on the spot forever.
        if (stepX != 0)
        {
            int nx = tx + stepX;
            bool blocked = false;

            for (int y = headY; y <= feetY; y++)
            {
                if (!ctx.Snapshot.IsSolid(nx, y))
                    continue;

                blocked = true;

                // Only counts as digging if the dig was actually accepted.
                // Setting this unconditionally froze villagers solid: Dig
                // refuses to touch the tribe's own masonry, but the caller
                // marked it as digging anyway, so the villager never walked
                // and never tried to climb either. Facing one of your own
                // walls became a permanent full stop, and settlements are
                // made of exactly those walls.
                if (dig && Dig(ctx, nx, y))
                    digging = true;

                break;
            }

            if (!blocked)
                WalkToward(nx);
            else if (!digging)
                VelX = 0f;              // cannot pass here; try going over it
        }

        // Then deal with height: dig through the ceiling or floor if it blocks
        // us, otherwise hop, but only from the ground so we cannot pogo.
        if (!digging && stepY != 0)
        {
            int ny = stepY > 0 ? feetY + 1 : headY - 1;

            if (ctx.Snapshot.IsSolid(tx, ny))
            {
                if (dig && Dig(ctx, tx, ny))
                    digging = true;
            }
            else if (stepY < 0)
            {
                // Getting back up a shaft they dug themselves.
                //
                // Digging down carves a one tile vertical shaft. Climbing out
                // of it, the tile above is open so there is nothing to dig, and
                // a hop clears about three tiles before gravity wins. Villagers
                // sat at the bottom of their own mines forever: thirty haulers
                // in the returning state and not one delivery.
                //
                // So if there is a wall to brace against, they climb it. Ants
                // do not fall down their own tunnels.
                bool braced = ctx.Snapshot.IsSolid(tx - 1, ty) ||
                              ctx.Snapshot.IsSolid(tx + 1, ty) ||
                              ctx.Snapshot.IsSolid(tx - 1, ty + 1) ||
                              ctx.Snapshot.IsSolid(tx + 1, ty + 1);

                if (braced)
                {
                    Climbing = true;
                    VelY = -3.4f;
                }
                else if (OnGround)
                {
                    VelY = -6.2f;
                }

                // If hopping is not gaining height, build a step.
                //
                // Descending is free and ascending is not, so a tribe that digs
                // for a week ends up at the bottom of its own quarry with every
                // job far above it. This is the escape, and it used to be gated
                // on _stuckTimer, which measures straight line distance to the
                // target and is the wrong instrument entirely: a villager
                // bouncing under a ledge sets a new closest approach on almost
                // every hop, so the timer reset before it ever reached ninety
                // and the staircase was never built. Four tribes sat frozen
                // with every villager outbound, zero tiles mined and stuck
                // timers stuck in the forties.
                //
                // Height is its own measurement. If the highest row reached has
                // not improved in forty ticks, hopping has failed, whatever the
                // distance is doing, and it is time to lay a step.
                if (ty < _bestY)
                {
                    _bestY = ty;
                    _climbTimer = 0;
                }
                else
                {
                    _climbTimer++;
                }

                if (_climbTimer > 40 && _owner != null && ctx.CanQueue)
                {
                    int stairX = FacingRight ? tx + 1 : tx - 1;

                    // One across and one up per step, laid repeatedly, is a
                    // staircase. Rock in the way is quarried rather than
                    // treated as a dead end: with the target directly overhead
                    // dx is zero, so the horizontal branch above never runs and
                    // nothing else in this function would ever clear it.
                    if (ctx.Snapshot.IsOpen(stairX, ty) && ctx.Snapshot.IsOpen(stairX, ty - 1))
                    {
                        ctx.Queue(TileOp.Platform(stairX, ty, TileID.Platforms, TribeId));
                        _owner.StairsBuilt++;
                        _climbTimer = 0;
                        _bestY = int.MaxValue;
                    }
                    else if (dig)
                    {
                        Dig(ctx, stairX, ty);
                        Dig(ctx, stairX, ty - 1);
                        _climbTimer = 20;
                    }
                }
            }
        }

        // Progress means getting closer, not merely moving.
        //
        // This used to reset whenever the villager's tile changed, which sounds
        // equivalent and is not: a villager bobbing between y=271 and y=272
        // under an unreachable target changes tile every single tick, so the
        // stuck timer reset every tick and the escape hatch never fired. They
        // hovered under overhead targets forever and mining flatlined at zero
        // while the tribe carried on hauling and building.
        int dist = dx * dx + dy * dy;

        if (dist < _bestDist)
        {
            _bestDist = dist;
            _stuckTimer = 0;
        }

        return false;
    }

    /// <summary>True if the dig was accepted, false if this tile is off limits.</summary>
    private bool Dig(SimContext ctx, int x, int y)
    {
        // Masonry is not raw material. Villagers route around their own walls
        // rather than through them, so buildings survive long enough to be
        // buildings. A villager properly wedged for four seconds may break out,
        // because being entombed by your own architecture is the worse outcome.
        if (ctx.Built.Get(x, y) != 0 && _stuckTimer < 60 * 4)
            return false;

        // Engaged, just between swings.
        if (_digCooldown > 0)
            return true;

        if (x != _lastDigX || y != _lastDigY)
        {
            _lastDigX = x;
            _lastDigY = y;
            _digTimer = 0;
        }

        _digTimer++;
        if (_digTimer < DigTicks)
            return true;

        _digTimer = 0;
        _stuckTimer = 0;

        ctx.Queue(TileOp.Mine(x, y, TribeId));

        TilesDug++;
        if (y > DeepestY)
            DeepestY = y;

        // Miners carry torches and light as they go. Lighting used to be a
        // construction job, which meant a builder had to walk out to every
        // tunnel; across millions of tiles the world stayed pitch black.
        if (TilesDug % 5 == 0 && _owner != null && _owner.TakeTorch())
            ctx.Queue(TileOp.Place(x, y, TileID.Torches, TribeId));

        // The op is applied by the main thread, not now. Wait a few ticks
        // before deciding whether it worked, rather than spamming the queue
        // with the same request sixty times a second.
        _digCooldown = 12;

        if (CarriedCount < CarryCapacity)
            CarriedCount++;

        return true;
    }

    private void WalkToward(int tileX)
    {
        float targetPx = tileX * TileSize + TileSize * 0.5f;

        if (targetPx > X + 2f)
        {
            VelX = MoveSpeed;
            FacingRight = true;
        }
        else if (targetPx < X - 2f)
        {
            VelX = -MoveSpeed;
            FacingRight = false;
        }
        else
        {
            VelX = 0f;
        }
    }

    /// <summary>
    /// Deliberately simple: gravity, horizontal move, one tile step up, and
    /// axis separated collision against the snapshot. It is not Terraria's
    /// physics and does not need to be. It needs to look like a small person
    /// walking through a tunnel and never fall through the floor.
    /// </summary>
    private void Physics(SimContext ctx)
    {
        if (!Climbing)
        {
            VelY += Gravity;
            if (VelY > MaxFall)
                VelY = MaxFall;
        }

        float newX = X + VelX;
        if (Blocked(ctx, newX, Y))
        {
            if (OnGround && !Blocked(ctx, newX, Y - TileSize))
            {
                Y -= TileSize;
                X = newX;
            }
            else
            {
                VelX = 0f;
            }
        }
        else
        {
            X = newX;
        }

        float newY = Y + VelY;
        if (Blocked(ctx, X, newY))
        {
            if (VelY > 0f)
            {
                OnGround = true;
                Y = (float)System.Math.Floor((newY + Height * 0.5f) / TileSize) * TileSize - Height * 0.5f - 0.01f;
            }
            VelY = 0f;
        }
        else
        {
            Y = newY;
            OnGround = false;
        }
    }

    private bool Blocked(SimContext ctx, float px, float py)
    {
        int left = (int)((px - Width * 0.5f) / TileSize);
        int right = (int)((px + Width * 0.5f) / TileSize);
        int top = (int)((py - Height * 0.5f) / TileSize);
        int bottom = (int)((py + Height * 0.5f) / TileSize);

        for (int x = left; x <= right; x++)
            for (int y = top; y <= bottom; y++)
                if (ctx.Snapshot.IsSolid(x, y))
                    return true;

        return false;
    }
}
