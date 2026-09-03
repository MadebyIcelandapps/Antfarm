using System.Collections.Generic;
using Antfarm.Core;
using Terraria.ID;

namespace Antfarm.Sim;

/// <summary>
/// One of the ten rivals. Owns settlements, a workforce, two stockpiles and a
/// growing hole in the ground.
/// </summary>
public sealed class Tribe
{
    public int Id;
    public string Name;

    public byte ColorR, ColorG, ColorB;

    /// <summary>What this tribe is like. Set once at world generation.</summary>
    public TribeTrait Trait;

    /// <summary>
    /// This tribe was wiped out and came back up out of the ground. It keeps
    /// its name, its dead keep theirs, and it carries on working.
    /// </summary>
    public bool Undead;

    /// <summary>The roll of the dead, and the roster an undead tribe rises from.</summary>
    public readonly List<Fallen> Dead = new();

    private int _riseTimer;

    // Traits are only weights on behaviour that already exists.
    public int DigTicks => Trait == TribeTrait.Delver ? 11 : 16;
    public int CarryCapacity => Trait == TribeTrait.Hoarder ? 40 : 24;
    /// <summary>One in this many villagers may be building. Lower means more masons.</summary>
    public int BuilderShare => Trait == TribeTrait.Builder ? 2 : 4;
    public int SoldierShare => Trait == TribeTrait.Warlike ? 3 : 6;
    public int SettlerThreshold => Trait == TribeTrait.Expander ? 90 : SettlersPerSettlement;
    public int ReachCap => Trait == TribeTrait.Delver ? 420 : 260;

    public readonly List<Settlement> Settlements = new();

    /// <summary>The capital. Where the vault is, and where haulers head by default.</summary>
    public int HomeX => Settlements.Count > 0 ? Settlements[0].X : 0;
    public int HomeY => Settlements.Count > 0 ? Settlements[0].Y : 0;

    /// <summary>
    /// How far the tribe reaches for work. Grows whenever it runs out of
    /// frontier. A tribe cannot run out of work, only out of nearby work, and
    /// its answer to that is to want more world.
    /// </summary>
    public int Reach = 40;

    public readonly List<Villager> Villagers = new();
    public readonly List<int> Chests = new();

    /// <summary>
    /// Where the tribe's chests physically are.
    ///
    /// The colony thread cannot read Main.chest, so it needs its own copy of
    /// the positions. This is the anchor everything else now hangs off: a
    /// settlement is a point that never moves, and by hour six the tribe is
    /// eight hundred tiles away from it, which is what broke the last world.
    /// Chests follow the dig front instead.
    /// </summary>
    public readonly List<(int X, int Y)> ChestSpots = new();

    /// <summary>
    /// Sites waiting to be built, in order. A cache dropped at the dig front
    /// queues a hall around itself, so the underground storage network grows
    /// into an actual city instead of chests sitting in bare tunnels.
    /// </summary>
    private readonly Queue<(int X, int Y, bool Underground)> _siteQueue = new();

    public void QueueSite(int x, int y, bool underground)
    {
        lock (_lock)
        {
            if (_siteQueue.Count < 24)
                _siteQueue.Enqueue((x, y, underground));
        }
    }

    public int SitesWaiting { get { lock (_lock) return _siteQueue.Count; } }

    /// <summary>Places the colony has asked for a new cache. Drained by the main thread.</summary>
    public readonly Queue<(int X, int Y)> CacheRequests = new();

    /// <summary>Rolling gauge: how far the average villager is from a chest.</summary>
    public int HaulDistance;

    public long TilesMined;
    public long ItemsStored;
    public long UnmappedMined;
    public long Deliveries;
    public long BuiltTiles;
    public long Losses;

    /// <summary>Population read back from the world file, so growth is not undone by a restart.</summary>
    public int SavedPopulation;

    /// <summary>Carried but not yet delivered.</summary>
    private readonly Dictionary<int, int> _hauling = new();

    /// <summary>Delivered and worth a chest slot, waiting to be written into one.</summary>
    private readonly Dictionary<int, int> _ledger = new();

    /// <summary>Delivered bulk terrain. Not treasure, but it is what they build with.</summary>
    private readonly Dictionary<int, int> _buildStock = new();

    /// <summary>Delivered ore, waiting for the furnace.</summary>
    private readonly Dictionary<int, int> _smeltQueue = new();

    private readonly object _lock = new();
    private readonly HashSet<long> _claimed = new();

    /// <summary>Shell tiles waiting for a villager, and which are already taken.</summary>
    private readonly Queue<BuildJob> _buildJobs = new();
    private readonly HashSet<long> _buildClaimed = new();

    private int _growthTimer;

    /// <summary>Villagers currently holding a build job, so most stay on the tools underground.</summary>
    private int _builders;

    // --- war ---------------------------------------------------------

    /// <summary>Where the tribe believes it is under attack, and for how much longer.</summary>
    public int ThreatX, ThreatY;
    public int ThreatTicks;
    public bool ThreatActive => ThreatTicks > 0;

    public int Soldiers;
    public int Armed;
    public long Kills;

    // --- industry ----------------------------------------------------

    /// <summary>Smelted metal. Arms soldiers and, as brick, puts up better buildings.</summary>
    public long Bars;
    public long Smelted;
    public long TorchesPlaced;
    public int RoadsBuilt;

    /// <summary>Steps laid by villagers who could not otherwise get anywhere.</summary>
    public long StairsBuilt;
    public long Births;

    /// <summary>Tribe average of each gene, so drift is visible while it happens.</summary>
    public int GeneVigour, GeneCapacity, GeneToughness, GeneBoldness, GeneWander;

    /// <summary>
    /// Average the tribe's genes. This is the whole point of evolution being
    /// visible rather than merely happening: over days these numbers should
    /// pull apart between tribes living in different worlds.
    /// </summary>
    /// <summary>
    /// Draw an individual from the tribe's saved gene pool, scattered around
    /// the averages so a reload produces a population rather than clones.
    /// </summary>
    public Genes PoolGenes(System.Random rand)
    {
        if (GeneVigour == 0)
            return Genes.Founder();

        var g = new Genes
        {
            Vigour = Clamp(GeneVigour, rand),
            Capacity = Clamp(GeneCapacity, rand),
            Toughness = Clamp(GeneToughness, rand),
            Boldness = Clamp(GeneBoldness, rand),
            Wander = Clamp(GeneWander, rand),
        };

        return g;
    }

    private static byte Clamp(int mean, System.Random rand)
    {
        int v = mean + rand.Next(-8, 9);
        return (byte)(v < 12 ? 12 : v > 243 ? 243 : v);
    }

    private void MeasureGenes()
    {
        int n = Villagers.Count;
        if (n == 0)
            return;

        long v = 0, c = 0, t = 0, b = 0, w = 0;

        foreach (Villager x in Villagers)
        {
            v += x.Genes.Vigour;
            c += x.Genes.Capacity;
            t += x.Genes.Toughness;
            b += x.Genes.Boldness;
            w += x.Genes.Wander;
        }

        GeneVigour = (int)(v / n);
        GeneCapacity = (int)(c / n);
        GeneToughness = (int)(t / n);
        GeneBoldness = (int)(b / n);
        GeneWander = (int)(w / n);
    }

    /// <summary>Places a villager dug that are worth lighting.</summary>
    private readonly Queue<(int X, int Y)> _torchSpots = new();

    // The highway currently being laid toward the newest settlement.
    private int _roadX, _roadY, _roadDir, _roadRemaining, _roadBlock;

    /// <summary>
    /// Something is attacking us here. Any villager taking a hit raises this,
    /// which is what makes them defend each other rather than dying one at a
    /// time while the rest carry on mining twenty tiles away.
    /// </summary>
    public void RaiseThreat(int tileX, int tileY)
    {
        ThreatX = tileX;
        ThreatY = tileY;
        ThreatTicks = 60 * 25;
    }

    /// <summary>Villagers report tunnels worth lighting as they dig them.</summary>
    public void SuggestTorch(int x, int y)
    {
        lock (_lock)
        {
            if (_torchSpots.Count < 400)
                _torchSpots.Enqueue((x, y));
        }
    }

    /// <summary>Hand a miner a torch off the tribe's stock, if there is one.</summary>
    public bool TakeTorch()
    {
        lock (_lock)
        {
            if (!_buildStock.TryGetValue(ItemID.Torch, out int have) || have <= 0)
                return false;

            if (have == 1)
                _buildStock.Remove(ItemID.Torch);
            else
                _buildStock[ItemID.Torch] = have - 1;

            TorchesPlaced++;
            return true;
        }
    }

    public int HaulingCount
    {
        get { lock (_lock) { int n = 0; foreach (int v in _hauling.Values) n += v; return n; } }
    }

    /// <summary>
    /// The material economy, for saving. Build stock, ore awaiting the furnace
    /// and carried loads all live only in memory otherwise, so every restart
    /// wipes a tribe back to nothing and it reports "no materials" until it has
    /// mined its way back. Across many restarts it can never accumulate at all.
    /// </summary>
    public void ExportStock(List<int> types, List<int> counts, List<int> oreTypes, List<int> oreCounts)
    {
        lock (_lock)
        {
            foreach (var kv in _buildStock) { types.Add(kv.Key); counts.Add(kv.Value); }
            foreach (var kv in _smeltQueue) { oreTypes.Add(kv.Key); oreCounts.Add(kv.Value); }
        }
    }

    public void ImportStock(IList<int> types, IList<int> counts, IList<int> oreTypes, IList<int> oreCounts)
    {
        lock (_lock)
        {
            for (int i = 0; i < types.Count && i < counts.Count; i++)
                _buildStock[types[i]] = counts[i];

            for (int i = 0; i < oreTypes.Count && i < oreCounts.Count; i++)
                _smeltQueue[oreTypes[i]] = oreCounts[i];
        }
    }

    public int BuildStockCount
    {
        get { lock (_lock) { int n = 0; foreach (int v in _buildStock.Values) n += v; return n; } }
    }

    /// <summary>
    /// Housing, derived from blocks actually placed rather than rooms planned.
    /// A shell is roughly thirty blocks, so this cannot outrun reality.
    /// </summary>
    public int Rooms => (int)(BuiltTiles / 30);

    /// <summary>
    /// Housing sets the ceiling. Build more rooms, support more villagers.
    ///
    /// Three parts, and the third exists because of a nasty feedback loop.
    /// The base is the starting headcount, since an earlier version used 20 and
    /// every tribe opened already over its cap. Rooms are the real driver.
    ///
    /// But rooms need build stock, stock needs deliveries, and a tribe whose
    /// haulers were fighting bad terrain never delivered, so it never built,
    /// so its cap stayed at exactly its starting population and it could never
    /// grow again. Some tribes were permanently sterile while their neighbours
    /// compounded. Excavation therefore also counts: carving living space out
    /// of solid rock houses people too, and it gives every tribe a floor to
    /// climb from.
    ///
    /// The excavation term was worth at most 60, which was far too small next
    /// to six per room, and that turned the whole thing into a poverty trap.
    /// Growth needed rooms, rooms needed stock, stock needed miners, and miners
    /// needed growth. Nine of ten tribes sat at exactly their cap (43/43, 42/42)
    /// and could never take the first step, while the one tribe that broke out
    /// early compounded away from everyone: 438 villagers against 44, and
    /// fourteen times the tonnage. Mining alone now has to be a real path up.
    /// </summary>
    public int PopulationCap =>
        40 + Rooms * 6 + (int)(TilesMined / 50 < 500 ? TilesMined / 50 : 500);

    public void CountTasks(out int idle, out int outbound, out int returning, out int building)
    {
        idle = outbound = returning = building = 0;

        List<Villager> vs = Villagers;
        for (int i = 0; i < vs.Count; i++)
        {
            switch (vs[i].Task)
            {
                case VillagerTask.Idle: idle++; break;
                case VillagerTask.Outbound: outbound++; break;
                case VillagerTask.Returning:
                case VillagerTask.Depositing: returning++; break;
                case VillagerTask.Building: building++; break;
            }
        }
    }

    /// <summary>The closest place to drop a load. Not necessarily the capital.</summary>
    /// <summary>
    /// The closest chest, and how far away it is. Returns false when the tribe
    /// has none at all yet.
    /// </summary>
    public bool NearestChest(int x, int y, out int cx, out int cy, out int dist)
    {
        cx = cy = 0;
        dist = int.MaxValue;

        lock (_lock)
        {
            foreach (var c in ChestSpots)
            {
                int dx = c.X - x, dy = c.Y - y;
                int d = dx * dx + dy * dy;

                if (d < dist)
                {
                    dist = d;
                    cx = c.X;
                    cy = c.Y;
                }
            }
        }

        if (dist == int.MaxValue)
            return false;

        dist = (int)System.Math.Sqrt(dist);
        return true;
    }

    /// <summary>
    /// Ask for a cache here. A hauler that cannot find a chest nearby is the
    /// signal that the dig front has outrun the tribe's storage, so storage
    /// follows it rather than the hauler walking further every trip.
    /// </summary>
    public void RequestCache(int x, int y)
    {
        lock (_lock)
        {
            if (CacheRequests.Count < 32)
                CacheRequests.Enqueue((x, y));
        }
    }

    /// <summary>Main thread: a chest now exists here.</summary>
    public void RegisterChest(int index, int x, int y)
    {
        lock (_lock)
        {
            Chests.Add(index);
            ChestSpots.Add((x, y));
        }
    }

    public void NearestSettlementTo(int x, int y, out int sx, out int sy)
    {
        Settlement s = Nearest(x, y);
        sx = s?.X ?? HomeX;
        sy = s?.Y ?? HomeY;
    }

    public bool NearStockpile(int tileX, int tileY)
    {
        foreach (Settlement s in Settlements)
        {
            int dx = tileX - s.X;
            int dy = tileY - s.Y;
            // Generous on purpose. Requiring haulers to reach the exact home
            // tile made the last leg of every round trip the longest part of
            // it, and the settlement is a district rather than a doorstep.
            if (dx * dx + dy * dy <= 30 * 30)
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Digging
    // ------------------------------------------------------------------

    public bool TryTakeDigTarget(SimContext ctx, int fromX, int fromY, out int tx, out int ty)
    {
        // Work radiates from whichever settlement is nearest, so an outpost
        // digs its own patch rather than everyone commuting to the capital.
        Settlement origin = Nearest(fromX, fromY) ?? (Settlements.Count > 0 ? Settlements[0] : null);

        if (origin == null)
        {
            tx = ty = 0;
            return false;
        }

        for (int attempt = 0; attempt < 48; attempt++)
        {
            // Mostly dig where you already are.
            //
            // Sourcing every target from the settlement meant villagers walked
            // hundreds of tiles to a face, mined one pack's worth, and walked
            // back. Digging is 26 ticks a block, so 400 villagers should manage
            // hundreds of tiles a second; measured throughput was two, because
            // they were almost never actually at a face.
            //
            // Working close to where they stand keeps them cutting, and it is
            // what produces dense tunnel networks instead of long thin spokes.
            bool local = attempt < 36;

            int cx = local ? fromX : origin.X;
            int cy = local ? fromY : origin.Y;
            int radius = local ? 24 : Reach;

            int x = cx + ctx.Rand.Next(-radius, radius + 1);

            // Barely ever pick a face high overhead. With no pathfinder, a
            // target twenty tiles straight up in open air is unreachable: the
            // villager hovers under it, digs nothing, and only a stall timer
            // ever frees it. Down and sideways is both reachable and what an
            // ant would do anyway.
            int y = cy + ctx.Rand.Next(local ? -4 : -Reach / 4, radius + 1);

            if (!ctx.Snapshot.InBounds(x, y) || y < 40 || y > ctx.Snapshot.Height - 40)
                continue;

            // Never accept work outside the tribe's territory, even when the
            // target was chosen relative to the villager. Picking each new face
            // near wherever you happen to stand is a random walk, so villagers
            // drifted steadily away from home and ended up spending their whole
            // lives hauling: nearly every worker sat in the returning state and
            // mining ground down to a trickle.
            // Local work must stay within reach of storage.
            //
            // Picking each new face near wherever you stand is a random walk
            // with no limit, and over hours it carries a tribe clean off the
            // map: Ashfang's villagers ended up 6,084 tiles from their nearest
            // chest, inside another tribe's territory, unable to deliver
            // anything ever again. Bounding to the settlement was the original
            // mistake, because settlements never move. Bounding to the nearest
            // chest works because storage follows the dig front: push past it
            // and a hauler opens a cache, which extends the boundary.
            if (local && ChestSpots.Count > 0 &&
                NearestChest(x, y, out _, out _, out int toStore) && toStore > 150)
                continue;

            // Only the settlement-centred fallback is bounded by territory.
            //
            // Applying it to local targets too was fatal: a villager 415 tiles
            // below its settlement had every tile beside its own feet rejected
            // for being "outside tribe territory", fell through to a surface
            // target, and spent the rest of its life climbing toward something
            // 386 tiles overhead. Mining across the whole world dropped from
            // 900 tiles a second to zero. A tile next to a villager who is
            // standing on it is territory the tribe holds, by definition.
            if (!local)
            {
                int ox = x - origin.X;
                int oy = y - origin.Y;
                if (ox * ox + oy * oy > Reach * Reach)
                    continue;
            }

            if (!ctx.Snapshot.IsSolid(x, y))
                continue;

            // Never quarry your own masonry.
            if (ctx.Built.Get(x, y) != 0)
                continue;

            bool frontier =
                ctx.Snapshot.IsOpen(x - 1, y) || ctx.Snapshot.IsOpen(x + 1, y) ||
                ctx.Snapshot.IsOpen(x, y - 1) || ctx.Snapshot.IsOpen(x, y + 1);

            if (!frontier && attempt < 32)
                continue;

            long key = Key(x, y);
            lock (_lock)
            {
                if (_claimed.Contains(key))
                    continue;
                _claimed.Add(key);
            }

            tx = x;
            ty = y;
            return true;
        }

        // Territory grows when they genuinely run out of frontier, but stays
        // bounded: past a few hundred tiles the haul home costs more than the
        // ore is worth, and expansion is what founding a new settlement is for.
        if (Reach < ReachCap)
            Reach += 8;

        tx = ty = 0;
        return false;
    }

    public void ReleaseDigTarget(int x, int y)
    {
        lock (_lock)
            _claimed.Remove(Key(x, y));
    }

    private Settlement Nearest(int x, int y)
    {
        Settlement best = null;
        long bestDist = long.MaxValue;

        foreach (Settlement s in Settlements)
        {
            long dx = x - s.X, dy = y - s.Y;
            long d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = s; }
        }

        return best;
    }

    // ------------------------------------------------------------------
    // Materials
    // ------------------------------------------------------------------

    private readonly HashSet<int> _seenOre = new();

    /// <summary>True the first time this tribe ever turns up a given material.</summary>
    public bool FirstStrike(int itemType)
    {
        lock (_lock)
            return _seenOre.Add(itemType);
    }

    /// <summary>Main thread: a dig landed, and this is what came out of it.</summary>
    public void CreditMined(int itemType, int count)
    {
        if (itemType <= 0 || count <= 0)
            return;

        lock (_lock)
        {
            _hauling.TryGetValue(itemType, out int have);
            _hauling[itemType] = have + count;
        }
    }

    /// <summary>
    /// A villager reached a settlement carrying this much. Split it: ore and
    /// gems become chest contents, bulk terrain becomes construction stock.
    ///
    /// Before the split every block went to a chest, so a tribe filled forty
    /// slots with dirt and then had nowhere to put the gold underneath.
    /// </summary>
    public void Deposit(int count)
    {
        if (count <= 0)
            return;

        lock (_lock)
        {
            int remaining = count;
            var types = new List<int>(_hauling.Keys);

            foreach (int type in types)
            {
                if (remaining <= 0)
                    break;

                int available = _hauling[type];
                int moved = available < remaining ? available : remaining;

                _hauling[type] = available - moved;
                if (_hauling[type] <= 0)
                    _hauling.Remove(type);

                // Three destinations, not two. Ore goes to the furnace queue,
                // because the main thread empties the ledger into chests every
                // tick and the smelter, running every twenty seconds, never
                // once saw any ore to melt.
                Dictionary<int, int> pile =
                    Materials.IsOre(type) ? _smeltQueue :
                    Materials.IsWorthy(type) ? _ledger : _buildStock;

                // Rubble that cannot be laid as a block is still rubble. Grass,
                // cobweb, vines and every unrecognised tile were piling up as
                // build stock that no builder could use, so tribes sat on ten
                // thousand units of "materials" and reported having none.
                int stored = type;
                if (pile == _buildStock && Materials.TileFor(type) == 0)
                    stored = ItemID.StoneBlock;
                pile.TryGetValue(stored, out int have);
                pile[stored] = have + moved;

                remaining -= moved;
            }

            Deliveries++;
        }
    }

    /// <summary>Main thread: take everything worth a chest slot.</summary>
    public List<KeyValuePair<int, int>> DrainLedger()
    {
        lock (_lock)
        {
            if (_ledger.Count == 0)
                return null;

            var snapshot = new List<KeyValuePair<int, int>>(_ledger);
            _ledger.Clear();
            return snapshot;
        }
    }

    public void ReturnToLedger(int itemType, int count)
    {
        if (count <= 0)
            return;

        lock (_lock)
        {
            _ledger.TryGetValue(itemType, out int have);
            _ledger[itemType] = have + count;
        }
    }

    // ------------------------------------------------------------------
    // Building
    // ------------------------------------------------------------------

    public bool ConsumeBuildItem(int itemType)
    {
        lock (_lock)
        {
            if (!_buildStock.TryGetValue(itemType, out int have) || have <= 0)
                return false;

            if (have == 1)
                _buildStock.Remove(itemType);
            else
                _buildStock[itemType] = have - 1;

            BuiltTiles++;

            // Counted separately so "are they lighting the tunnels" is a
            // question with an answer, rather than being buried inside the
            // total block count.
            if (itemType == ItemID.Torch)
                TorchesPlaced++;

            return true;
        }
    }

    /// <summary>The building currently going up. One at a time, to completion.</summary>
    private Building _current;

    /// <summary>Footprints already used, so the next building is not put on top of the last.</summary>
    private readonly List<Building> _built = new();

    public int BuildingsFinished;
    private int _lastPending = -1;
    private int _phaseStallTicks;
    public string BuildingStatus { get; private set; } = "idle";

    /// <summary>Where the crew should be standing. Zero when there is no site.</summary>
    public int SiteX, SiteY;

    /// <summary>
    /// Move the building along, judged against the world rather than against
    /// bookkeeping.
    ///
    /// Every earlier version tracked handed-out jobs, claims and an outstanding
    /// counter, and every one of them deadlocked: a mason that wandered off,
    /// died or got yanked to a fight left the counter permanently wrong and the
    /// phase never ended. Fifteen deploys placed zero blocks between them.
    ///
    /// A job is now finished when the tile actually looks right, which no
    /// amount of villager misbehaviour can corrupt. Jobs are never removed on
    /// hand-out, so two masons doing the same tile is merely wasteful, and a
    /// mason that vanishes costs nothing at all.
    /// </summary>
    public void AdvanceBuilding(SimContext ctx)
    {
        lock (_lock)
        {
            int block = BestBlockLocked();

            if (block == 0)
            {
                BuildingStatus = "no materials";
                return;
            }

            if (_current == null)
            {
                _current = ChooseSiteLocked(ctx, HomeX, HomeY);

                if (_current == null)
                {
                    BuildingStatus = "no site";
                    return;
                }

                _current.Phase = 0;
                _current.GeneratePhase(block, ctx);
            }

            // Drop everything the world already satisfies.
            for (int i = _current.Pending.Count - 1; i >= 0; i--)
                if (!Building.StillNeeded(ctx, _current.Pending[i]))
                    _current.Pending.RemoveAt(i);

            // Safety net: a phase that makes no progress for two minutes is
            // being blocked by something nobody can do, so move on rather than
            // stopping the tribe's construction for ever. One indestructible
            // tile used to freeze a tribe permanently at phase 0.
            if (_current.Pending.Count != _lastPending)
            {
                _lastPending = _current.Pending.Count;
                _phaseStallTicks = 0;
            }
            else if (++_phaseStallTicks > 6)
            {
                _phaseStallTicks = 0;
                _current.Pending.Clear();

                ctx.Events.Add(EventKind.Colony, Id,
                    $"{Name} gave up on part of a building it could not finish");
            }

            if (_current.Pending.Count == 0)
            {
                if (_current.Phase >= Building.LastPhase)
                {
                    _built.Add(_current);
                    BuildingsFinished++;

                    if (_built.Count > 64)
                        _built.RemoveRange(0, 32);

                    _current = null;
                    SiteX = SiteY = 0;
                    BuildingStatus = "finished one";
                    return;
                }

                _current.Phase++;
                _current.GeneratePhase(block, ctx);
            }

            SiteX = _current.X + Building.Width / 2;
            SiteY = _current.GroundY;

            BuildingStatus =
                $"phase {_current.Phase} left={_current.Pending.Count} " +
                $"at {SiteX},{SiteY}" + (_current.Underground ? " hall" : " tower");
        }
    }

    /// <summary>
    /// The nearest piece of work on the current building. Nothing is reserved
    /// and nothing is removed: duplicated effort is harmless and self correcting,
    /// whereas reservation was the thing that kept jamming.
    /// </summary>
    public bool TryTakeBuildJob(SimContext ctx, int fromX, int fromY, out BuildJob taken)
    {
        taken = default;

        lock (_lock)
        {
            if (_current == null || _current.Pending.Count == 0)
                return false;

            int bestDist = int.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < _current.Pending.Count; i++)
            {
                BuildJob j = _current.Pending[i];

                int dx = j.X - fromX;
                int dy = j.Y - fromY;
                int d = dx * dx + dy * dy;

                if (d < bestDist)
                {
                    bestDist = d;
                    bestIndex = i;
                }
            }

            // Out of arm's reach: the mason should walk to the site first.
            if (bestIndex < 0 || bestDist > 70 * 70)
                return false;

            taken = _current.Pending[bestIndex];
            return true;
        }
    }

    private int BestBlockLocked()
    {
        int block = 0, best = 0;

        foreach (var kv in _buildStock)
            if (kv.Key != ItemID.Torch && kv.Value > best && Materials.TileFor(kv.Key) != 0)
            {
                best = kv.Value;
                block = kv.Key;
            }

        return block;
    }

    /// <summary>
    /// Find somewhere to put the next building: near the capital, on ground
    /// that is level enough to stand on, clear of anything already standing.
    /// </summary>
    private Building ChooseSiteLocked(SimContext ctx, int fromX, int fromY)
    {
        if (Settlements.Count == 0)
            return null;

        // A queued site wins: those are halls around caches out at the dig
        // front, which is where the tribe actually lives.
        if (_siteQueue.Count > 0)
        {
            var want = _siteQueue.Dequeue();

            var hall = new Building
            {
                X = want.X - Building.Width / 2,
                GroundY = want.Y,
                Storeys = want.Underground ? 2 + ctx.Rand.Next(3) : 3 + ctx.Rand.Next(5),
                Underground = want.Underground,
            };

            bool taken = false;
            foreach (Building other in _built)
                if (hall.Overlaps(other))
                {
                    taken = true;
                    break;
                }

            if (!taken)
                return hall;
        }

        Settlement site = Nearest(fromX, fromY) ?? Settlements[0];

        for (int attempt = 0; attempt < 24; attempt++)
        {
            int x = site.X - 60 + ctx.Rand.Next(120);

            int ground = attempt < 18
                ? LevelGround(ctx, x, Building.Width)
                : SurfaceY(ctx, x + Building.Width / 2);

            if (ground < 0)
                continue;

            var b = new Building
            {
                X = x,
                GroundY = ground,
                Storeys = 3 + ctx.Rand.Next(6),
                Underground = false,
            };

            bool clash = false;
            foreach (Building other in _built)
                if (b.Overlaps(other))
                {
                    clash = true;
                    break;
                }

            if (!clash)
                return b;
        }

        return null;
    }

    /// <summary>
    /// The ground level across a span, or -1 if it is too uneven to build on.
    /// Stops towers being planted half in a cliff and half in mid air.
    /// </summary>
    private static int LevelGround(SimContext ctx, int x0, int width)
    {
        int first = SurfaceY(ctx, x0);
        if (first < 0)
            return -1;

        for (int x = x0 + 1; x < x0 + width; x++)
        {
            int y = SurfaceY(ctx, x);
            if (y < 0 || y < first - 6 || y > first + 6)
                return -1;
        }

        return first;
    }

    private static int SurfaceY(SimContext ctx, int x)
    {
        int limit = ctx.Snapshot.Height - 80;

        for (int y = 60; y < limit; y++)
        {
            if (!ctx.Snapshot.IsSolid(x, y))
                continue;

            int solid = 0;
            for (int d = 0; d < 6 && y + d < limit; d++)
                if (ctx.Snapshot.IsSolid(x, y + d))
                    solid++;

            if (solid >= 4)
                return y;
        }

        return -1;
    }

    private void Enqueue(int x, int y, int item)
        => Enqueue(new BuildJob(x, y, item, BuildKind.Block));

    private void Enqueue(BuildJob job)
    {
        if (job.X < 5 || job.Y < 5)
            return;

        _buildJobs.Enqueue(job);
    }

    // ------------------------------------------------------------------
    // Population and expansion
    // ------------------------------------------------------------------

    /// <summary>
    /// Grow toward the housing cap, and found a new settlement once this one is
    /// built out. Called on the colony thread once per tick.
    /// </summary>
    public void Tick(SimContext ctx)
    {
        // Threats expire on their own, so a tribe returns to work after a raid
        // instead of standing guard over an empty tunnel forever.
        if (ThreatTicks > 0)
            ThreatTicks--;

        CheckRising(ctx);

        _growthTimer++;
        if (_growthTimer < 60 * 20)
            return;

        _growthTimer = 0;

        AssignRoles();
        MeasureHaulDistance();
        MeasureGenes();
        AdvanceBuilding(ctx);
        Smelt(ctx);
        Arm(ctx);
        MakeTorches();
        PlanTorches(ctx);

        // A birth costs stockpiled food-equivalent: they must be productive to
        // grow, so a starving or stalled tribe stops expanding on its own.
        // The dead do not breed. An undead tribe grows only by raising more of
        // its own fallen, which is what makes it unkillable but never larger.
        // Births do not compete with builders for materials.
        //
        // They used to: a birth needed 60 stock and spent 20. That put a
        // hard ceiling on the pile at 60, because every time a tribe crept
        // over the line a child ate the difference. Buildings need more than
        // that to start, so eight of ten tribes were pinned in a 0-60 band
        // for good, mining thousands of tiles and never laying a block.
        // Duskloam escaped only because its income dwarfed the drain, and it
        // then looked like the one tribe with a work ethic.
        //
        // Tuning the two numbers cannot fix this; any gate above any cost
        // recreates the same band. So the pools are separate now. Population
        // is limited by PopulationCap, which is rooms and excavation and is
        // already paid for in real digging. Materials limit building. Neither
        // can starve the other.
        if (!Undead && Villagers.Count < PopulationCap)
        {
            Settlement home = Settlements.Count > 0 ? Settlements[0] : null;
            if (home != null)
            {
                // Selection, in one line: parents are drawn from the living, so
                // whatever kept them alive is what gets copied forward. Weighted
                // toward the productive, because a villager that dug more was
                // demonstrably better at being one.
                Genes inherited = Genes.Founder();

                if (Villagers.Count > 0)
                {
                    Villager parent = Villagers[ctx.Rand.Next(Villagers.Count)];

                    for (int tries = 0; tries < 3; tries++)
                    {
                        Villager other = Villagers[ctx.Rand.Next(Villagers.Count)];
                        if (other.TilesDug > parent.TilesDug)
                            parent = other;
                    }

                    inherited = parent.Genes.Breed(ctx.Rand);
                }

                var child = new Villager
                {
                    TribeId = Id,
                    Name = VillagerNames.Next(ctx.Rand),
                    Genes = inherited,
                    X = home.X * 16f + 8f,
                    Y = home.Y * 16f - 24f,
                };

                child.MaxHealth = inherited.MaxHealth(60);
                child.Health = child.MaxHealth;
                Villagers.Add(child);
                Births++;
            }
        }

        // At 150 to a settlement the place is full and a party sets off to
        // found the next one, then 150 again for the one after that.
        if (Settlements.Count < MaxSettlements &&
            Villagers.Count >= SettlerThreshold * Settlements.Count)
        {
            // Prefer putting it where the workforce already is; fall back to
            // the old surface expansion only if nobody is out of range.
            if (!FoundOutpostAtWorkforce(ctx))
                FoundSettlement(ctx);
        }
    }

    /// <summary>A villager died. Keep the name and the record, because it may be needed again.</summary>
    public void RecordFallen(Villager v)
    {
        lock (_lock)
        {
            Dead.Add(new Fallen(v.Name, v.TilesDug, v.Kills, v.DeepestY, v.Soldier, v.Weapon));

            // Bounded, because this runs for months. The oldest dead are the
            // ones a risen tribe is least likely to need.
            if (Dead.Count > 600)
                Dead.RemoveRange(0, 200);
        }
    }

    /// <summary>
    /// Wiped out, and then not.
    ///
    /// A tribe that hits zero does not disappear from the world. After a while
    /// it claws its way back out of the ground with the same name and the same
    /// people, carrying whatever they had achieved when they died. An undead
    /// tribe cannot breed, so it never grows on its own, but it also cannot be
    /// permanently killed: every time it is wiped out it simply rises again.
    /// </summary>
    private void CheckRising(SimContext ctx)
    {
        if (Villagers.Count > 0)
        {
            _riseTimer = 0;
            return;
        }

        if (Dead.Count == 0)
            return;

        _riseTimer++;
        if (_riseTimer < 60 * 45)
            return;

        _riseTimer = 0;

        bool first = !Undead;
        Undead = true;

        // Sickly, so their territory reads as undead on the map without any
        // extra data: the colour the map already paints them with just changes.
        ColorR = (byte)(90 + ColorR / 6);
        ColorG = (byte)(130 + ColorG / 5);
        ColorB = (byte)(90 + ColorB / 6);

        Settlement home = Settlements.Count > 0 ? Settlements[0] : null;
        if (home == null)
            return;

        int raise;
        lock (_lock)
        {
            raise = Dead.Count < 40 ? Dead.Count : 40;

            for (int i = 0; i < raise; i++)
            {
                Fallen f = Dead[Dead.Count - 1 - i];

                Villagers.Add(new Villager
                {
                    TribeId = Id,
                    Name = f.Name,
                    TilesDug = f.TilesDug,
                    Kills = f.Kills,
                    DeepestY = f.DeepestY,
                    Role = f.WasSoldier ? VillagerRole.Soldier : VillagerRole.Miner,
                    Weapon = f.Weapon,
                    Undead = true,
                    MaxHealth = f.WasSoldier ? 140 : 80,
                    Health = f.WasSoldier ? 140 : 80,
                    X = home.X * 16f + 8f,
                    Y = home.Y * 16f - 24f,
                });
            }

            Dead.RemoveRange(Dead.Count - raise, raise);
        }

        ctx.Events.Add(EventKind.Battle, Id, first
            ? $"{Name} was wiped out. Forty five seconds later, {raise} of them rose from the ground and went back to work"
            : $"{Name} fell again, and {raise} more of the dead got back up");
    }

    /// <summary>
    /// Ore into bars.
    ///
    /// Until now ore went into a chest and sat there for ever, which made the
    /// whole point of mining a trophy cabinet. Smelting turns it into two
    /// things a tribe can actually use: weapons for its soldiers, and brick to
    /// build with once it has outgrown piling dirt on dirt.
    /// </summary>
    private void Smelt(SimContext ctx)
    {
        lock (_lock)
        {
            int budget = 40;

            foreach (int type in new List<int>(_smeltQueue.Keys))
            {
                if (budget <= 0)
                    break;

                int have = _smeltQueue[type];
                int take = have < budget ? have : budget;
                take -= take % 4;              // four ore to a pour
                if (take <= 0)
                    continue;

                _smeltQueue[type] = have - take;
                if (_smeltQueue[type] <= 0)
                    _smeltQueue.Remove(type);

                budget -= take;

                int made = take / 4;
                Bars += made;
                Smelted += take;

                // Half the pour becomes building brick, half stays as bars for
                // the armoury.
                _buildStock.TryGetValue(ItemID.IronBrick, out int brick);
                _buildStock[ItemID.IronBrick] = brick + made;

                // And bars into the vault, so the chests hold something that
                // reflects the tribe's industry rather than raw rock.
                _ledger.TryGetValue(ItemID.IronBar, out int bar);
                _ledger[ItemID.IronBar] = bar + made;
            }

            if (Smelted > 0 && _firstSmelt)
            {
                _firstSmelt = false;
                ctx.Events.Add(EventKind.Tech, Id, $"{Name} lit its first furnace and poured metal");
            }
        }
    }

    private bool _firstSmelt = true;

    public int Miners, Masons;

    /// <summary>
    /// Keep the workforce to its quotas. Builders being a share of whoever
    /// happened to be idle meant construction and mining fought over the same
    /// bodies and construction always lost.
    /// </summary>
    /// <summary>
    /// How far the average villager is from a chest.
    ///
    /// This is the gauge that would have caught the last world dying six hours
    /// early. When storage stops following the dig front this number climbs,
    /// and every downstream symptom, stalled hauling, empty stock, no
    /// construction, follows from it. Sampled rather than exhaustive, because
    /// it runs against thousands of villagers.
    /// </summary>
    private void MeasureHaulDistance()
    {
        if (Villagers.Count == 0 || ChestSpots.Count == 0)
            return;

        long total = 0;
        int sampled = 0;
        int step = Villagers.Count / 40 + 1;

        for (int i = 0; i < Villagers.Count; i += step)
        {
            Villager v = Villagers[i];

            if (NearestChest(v.TileX, v.TileY, out _, out _, out int d))
            {
                total += d;
                sampled++;
            }
        }

        if (sampled > 0)
            HaulDistance = (int)(total / sampled);
    }

    /// <summary>
    /// Found the next outpost where the people actually are.
    ///
    /// New settlements used to appear a few hundred tiles sideways at the
    /// surface, which is the one place a tribe that has burrowed for hours is
    /// guaranteed not to be. Averaging the position of villagers who are far
    /// from any existing settlement puts the outpost in the middle of the work.
    /// </summary>
    private bool FoundOutpostAtWorkforce(SimContext ctx)
    {
        long sx = 0, sy = 0;
        int n = 0;

        foreach (Villager v in Villagers)
        {
            Settlement near = Nearest(v.TileX, v.TileY);
            if (near == null)
                continue;

            int dx = v.TileX - near.X;
            int dy = v.TileY - near.Y;

            if (dx * dx + dy * dy < 150 * 150)
                continue;                       // already served

            sx += v.TileX;
            sy += v.TileY;
            n++;
        }

        if (n < 30)
            return false;

        int cx = (int)(sx / n);
        int cy = (int)(sy / n);

        if (!ctx.Snapshot.InBounds(cx, cy) || cy > ctx.Snapshot.Height - 80)
            return false;

        Settlements.Add(new Settlement(cx, cy));
        Reach += 40;

        ctx.Events.Add(EventKind.Colony, Id,
            $"{Name} founded an outpost at {cx},{cy}, where {n} of its people were already working");

        return true;
    }

    private void AssignRoles()
    {
        int n = Villagers.Count;
        if (n == 0)
            return;

        int wantMason = 1 + n * (Trait == TribeTrait.Builder ? 45 : 30) / 100;
        int wantSoldier = 1 + n * (Trait == TribeTrait.Warlike ? 25 : 12) / 100;

        int masons = 0, soldiers = 0;

        foreach (Villager v in Villagers)
        {
            if (v.Role == VillagerRole.Mason) masons++;
            else if (v.Role == VillagerRole.Soldier) soldiers++;
        }

        foreach (Villager v in Villagers)
        {
            if (masons < wantMason && v.Role == VillagerRole.Miner)
            {
                v.Role = VillagerRole.Mason;
                masons++;
            }
            else if (soldiers < wantSoldier && v.Role == VillagerRole.Miner)
            {
                v.Role = VillagerRole.Soldier;
                v.MaxHealth = 110;
                v.Health = 110;
                soldiers++;
            }
            else if (masons > wantMason && v.Role == VillagerRole.Mason)
            {
                v.Role = VillagerRole.Miner;
                masons--;
            }
        }

        Masons = masons;
        Soldiers = soldiers;
        Miners = n - masons - soldiers;
    }

    /// <summary>
    /// Turn some of the workforce into a standing army, and put weapons in
    /// their hands. Unarmed villagers still fight; armed ones win.
    /// </summary>
    /// <summary>
    /// Put weapons in soldiers' hands. Who IS a soldier is now decided by
    /// AssignRoles; this only equips them.
    /// </summary>
    private void Arm(SimContext ctx)
    {
        int armed = 0;

        foreach (Villager v in Villagers)
        {
            if (v.Role != VillagerRole.Soldier)
                continue;

            if (v.Weapon == 0 && Bars >= 3)
            {
                Bars -= 3;
                v.Weapon = 1;

                if (Armed == 0)
                    ctx.Events.Add(EventKind.Tech, Id, $"{Name} armed its first warriors");
            }

            if (v.Weapon > 0)
                armed++;
        }

        Armed = armed;
    }

    /// <summary>
    /// Queue lighting work directly, rather than waiting for the room queue to
    /// run dry.
    ///
    /// Torch planning used to live inside the room planner, which only runs
    /// when every queued wall tile has been placed. With builders capped at a
    /// third of the tribe that almost never happened, and the whole colony
    /// placed one torch between them.
    /// </summary>
    private void PlanTorches(SimContext ctx)
    {
        lock (_lock)
        {
            if (!_buildStock.TryGetValue(ItemID.Torch, out int torches) || torches <= 0)
                return;

            int want = torches < 20 ? torches : 20;

            for (int i = 0; i < want && _torchSpots.Count > 0; i++)
            {
                var spot = _torchSpots.Dequeue();

                // A torch needs something to hang on.
                if (ctx.Snapshot.IsOpen(spot.X, spot.Y) &&
                    (ctx.Snapshot.IsSolid(spot.X, spot.Y + 1) ||
                     ctx.Snapshot.IsSolid(spot.X - 1, spot.Y) ||
                     ctx.Snapshot.IsSolid(spot.X + 1, spot.Y)))
                    Enqueue(spot.X, spot.Y, ItemID.Torch);
            }
        }
    }

    /// <summary>Bulk material into torches, so their tunnels stop being pitch dark.</summary>
    private void MakeTorches()
    {
        lock (_lock)
        {
            int spare = 0;
            foreach (var kv in _buildStock)
                if (kv.Key != ItemID.Torch)
                    spare += kv.Value;

            if (spare < 20)
                return;

            // A quarter of spare stock becomes torches, at four torches to the
            // unit. Sized against demand, not plucked: every villager lights a
            // torch every five blocks, so a tribe of 1,400 miners burns through
            // them far faster than the old twelve-units-per-twenty-seconds
            // could ever supply. That is why the world stayed dark.
            // A tenth, not a quarter. Torches were consuming more than twice
            // the material that construction did, which is a strange set of
            // priorities for a colony with nowhere to live.
            int budget = spare / 10;

            foreach (int type in new List<int>(_buildStock.Keys))
            {
                if (budget <= 0)
                    break;

                if (type == ItemID.Torch || Materials.TileFor(type) == 0)
                    continue;

                int take = _buildStock[type] < budget ? _buildStock[type] : budget;
                if (take <= 0)
                    continue;

                _buildStock[type] -= take;
                if (_buildStock[type] <= 0)
                    _buildStock.Remove(type);

                budget -= take;

                _buildStock.TryGetValue(ItemID.Torch, out int t);
                _buildStock[ItemID.Torch] = t + take * 4;
            }
        }
    }

    public const int SettlersPerSettlement = 150;
    public const int MaxSettlements = 8;

    /// <summary>
    /// Send a party out to start again somewhere else. They walk: the new site
    /// is just a place they now consider home, and because work radiates from
    /// the nearest settlement they immediately start digging it out.
    /// </summary>
    private void FoundSettlement(SimContext ctx)
    {
        Settlement from = Settlements[Settlements.Count - 1];
        int dir = ctx.Rand.Next(2) == 0 ? -1 : 1;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            int nx = from.X + dir * ctx.Rand.Next(220, 420);
            int ny = from.Y;

            if (nx < 60 || nx > ctx.Snapshot.Width - 60)
            {
                dir = -dir;
                continue;
            }

            // Drop to whatever the ground is over there rather than hanging the
            // settlement in mid air over a chasm.
            int y = ny;
            while (y < ctx.Snapshot.Height - 60 && ctx.Snapshot.IsOpen(nx, y))
                y++;

            if (y >= ctx.Snapshot.Height - 60)
            {
                dir = -dir;
                continue;
            }

            var founded = new Settlement(nx, y - 1);
            Settlements.Add(founded);
            Reach += 40;

            // Lay a road back to the town they came from.
            _roadDir = nx > from.X ? -1 : 1;
            _roadX = nx;
            _roadY = founded.Y + 1;
            _roadRemaining = System.Math.Abs(nx - from.X);
            _roadBlock = ItemID.StoneBlock;

            ctx.Events.Add(EventKind.Colony, Id,
                $"{Name} sent settlers {System.Math.Abs(nx - from.X)} tiles away and founded a new town");
            return;
        }
    }

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
