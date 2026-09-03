using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Antfarm.Sim;

namespace Antfarm.Core;

/// <summary>
/// The only part of the mod that touches Terraria's main thread.
///
/// It does four jobs per tick, all of them cheap and all of them bounded:
///   1. Sweep a slice of the world into the snapshot the colony reads.
///   2. Apply a budgeted number of queued tile changes.
///   3. Move delivered material into real chests.
///   4. Draw the villagers that happen to be on screen.
///
/// Everything expensive, which is a thousand villagers thinking sixty times a
/// second, happens on SimThread instead.
/// </summary>
public class AntfarmSystem : ModSystem
{
    public static AntfarmSystem Instance;

    public readonly TileSnapshot Snapshot = new();
    public readonly MinedMask Mask = new();

    /// <summary>Tiles the tribes placed, so they stop quarrying their own walls.</summary>
    public readonly MinedMask BuiltMask = new();
    public readonly ConcurrentQueue<TileOp> Ops = new();
    public readonly List<Tribe> Tribes = new();
    public readonly EventLog Events = new();

    /// <summary>Colony thread ticks, surfaced for the observation window.</summary>
    public long SimTicks => _sim?.Ticks ?? 0;

    private SimContext _ctx;
    private SimThread _sim;
    private WebObserver _observer;

    /// <summary>The history recorder, so the year can be watched back.</summary>
    public Timelapse Recorder { get; private set; }

    private static readonly Color[] TribeColors =
    {
        new(214,  73,  73), new( 84, 168, 214), new(120, 196,  96), new(224, 176,  64),
        new(168,  96, 208), new( 96, 208, 192), new(224, 128, 176), new(150, 150, 160),
        new(198, 122,  64), new(112, 128, 224),
    };

    private static readonly string[] TribeNames =
    {
        "Ashfang", "Deepmarrow", "Saltcrown", "Gravelkin", "Emberhollow",
        "Thornmoor", "Ironvein", "Palebriar", "Duskloam", "Stonewake",
    };

    public override void Load() => Instance = this;
    public override void Unload() { StopSim(); Instance = null; }

    /// <summary>
    /// Deliberately does not seed anything.
    ///
    /// LoadWorldData can run AFTER OnWorldLoad. That is not a guess: on a
    /// freshly generated world the observed order was OnWorldLoad seeding ten
    /// tribes, then LoadWorldData clearing the list and restoring the empty one
    /// that had been saved moments earlier during worldgen. The colony started
    /// with zero tribes and looked, from the outside, like a mod that simply
    /// did nothing.
    ///
    /// So startup waits for the first world tick, by which point both hooks
    /// have run in whatever order they chose.
    /// </summary>
    public override void OnWorldLoad()
    {
        _pendingStart = true;
    }

    private bool _pendingStart;

    private void EnsureStarted()
    {
        _pendingStart = false;

        // A multiplayer client must never run the colony.
        //
        // There was no guard here at all, so joining the server started a
        // second, completely independent simulation inside the client: it
        // seeded its own ten tribes and began mining tiles the server had
        // never heard of, while the server's real villagers were never sent
        // to it. The result from the player's seat was an empty world with
        // terrain quietly fighting itself.
        //
        // The client's only job is to draw villagers the server tells it
        // about, so it keeps the tribe list for colours and nothing else.
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            Mod.Logger.Info("antfarm: client mode, rendering only, no local colony");
            return;
        }

        Snapshot.Rebuild();

        // Territory colouring survives a restart now. Without this the map went
        // fully grey on every start and slowly re-coloured over hours.
        if (Mask.Load(MaskPath("mined"), Main.maxTilesX, Main.maxTilesY))
            Mod.Logger.Info("antfarm: restored the dug-by-tribe map");
        else
            Mask.Rebuild(Main.maxTilesX, Main.maxTilesY);

        if (!BuiltMask.Load(MaskPath("built"), Main.maxTilesX, Main.maxTilesY))
            BuiltMask.Rebuild(Main.maxTilesX, Main.maxTilesY);

        // Seed on an empty list rather than a saved flag. If a world somehow
        // comes back with no tribes, the right answer is to repopulate it, not
        // to sit there permanently empty because a boolean said otherwise.
        // A world saved before tribes had settlements loads them with none at
        // all, which would then index an empty list and take the server down on
        // startup. Treat any tribe without a home as unseeded rather than
        // trusting the save to match the current format.
        bool homeless = false;
        foreach (Tribe t in Tribes)
            if (t.Settlements.Count == 0)
                homeless = true;

        if (homeless)
        {
            Mod.Logger.Warn($"antfarm: {Tribes.Count} tribes loaded with no settlements, re-seeding");
            Tribes.Clear();
        }

        if (Tribes.Count == 0)
            SeedTribes();

        RestoreChestPositions();
        SpawnWorkforce();
        StartSim();

        int pop = 0;
        foreach (Tribe t in Tribes)
            pop += t.Villagers.Count;

        Mod.Logger.Info($"antfarm: started, {Tribes.Count} tribes, {pop} villagers, sim thread running");

        if (AntfarmConfig.Instance is not { TimelapseEnabled: false })
        {
            string file = System.IO.Path.Combine(
                Main.SavePath, "Antfarm", $"timelapse-{Main.worldID}.bin");

            Recorder = new Timelapse(this, file, (AntfarmConfig.Instance?.TimelapseMinutes ?? 15) * 60);
            Recorder.Start();
        }

        if (AntfarmConfig.Instance is not { ObserverEnabled: false })
        {
            _observer = new WebObserver(this, AntfarmConfig.Instance?.ObserverPort ?? 7778);
            _observer.Start();
        }
    }

    public override void OnWorldUnload()
    {
        Mod.Logger.Info($"antfarm: world unload, discarding {Tribes.Count} tribes");
        _observer?.Stop();
        _observer = null;
        Recorder?.Stop();
        Recorder = null;
        StopSim();
        Tribes.Clear();
        Ops.Clear();
        _pendingStart = false;
    }

    // ------------------------------------------------------------------
    // Setup
    // ------------------------------------------------------------------

    private void SeedTribes()
    {
        int count = AntfarmConfig.Instance?.TribeCount ?? 10;

        // Spread them across the world, staying clear of the ocean at each end.
        int usable = Main.maxTilesX - 700;
        int spacing = usable / count;

        for (int i = 0; i < count; i++)
        {
            int x = 350 + spacing / 2 + spacing * i;
            int y = SurfaceAt(x);

            Color c = TribeColors[i % TribeColors.Length];

            // Every tribe gets a character, spread evenly so all five show up
            // in a ten tribe world rather than leaving it to chance.
            var trait = (TribeTrait)(i % 5);

            var tribe = new Tribe
            {
                Id = i,
                Name = TribeNames[i % TribeNames.Length],
                Trait = trait,
                ColorR = c.R,
                ColorG = c.G,
                ColorB = c.B,
            };

            tribe.Settlements.Add(new Settlement(x, y));
            Tribes.Add(tribe);
        }
    }

    /// <summary>
    /// First solid ground below the sky at this column.
    ///
    /// Naively taking the first solid tile lands tribes on floating islands.
    /// That happened in testing: Ashfang was seeded at y=97, on an island, and
    /// could never place a stockpile chest because there was no room under it.
    /// So a candidate only counts as ground if it is actually backed by more
    /// ground underneath rather than being a thin slab with sky below.
    /// </summary>
    private static int SurfaceAt(int x)
    {
        x = Utils.Clamp(x, 10, Main.maxTilesX - 10);

        int limit = Main.maxTilesY - 200;

        for (int y = 1; y < limit; y++)
        {
            Tile t = Main.tile[x, y];
            if (!t.HasTile || !Main.tileSolid[t.TileType])
                continue;

            int solid = 0;
            for (int d = 0; d < 24 && y + d < limit; d++)
            {
                Tile below = Main.tile[x, y + d];
                if (below.HasTile && Main.tileSolid[below.TileType])
                    solid++;
            }

            // A floating island is a thin crust over open air. Real ground is
            // mostly solid for a good depth below the surface.
            if (solid >= 16)
                return y - 1;
        }

        return (int)Main.worldSurface;
    }

    /// <summary>
    /// Rebuild each tribe's chest positions from the world, and mark them as
    /// masonry.
    ///
    /// Chest indices are saved but their coordinates are not, and the colony
    /// thread cannot read Main.chest. Without this a reloaded world has tribes
    /// that do not know where their own vaults are, and worse, treat them as
    /// ordinary rock: halls are planned around caches, so the clearing phase
    /// tries to mine out a chest, Terraria refuses to break one holding items,
    /// and construction stops permanently at phase 0.
    /// </summary>
    private void RestoreChestPositions()
    {
        int restored = 0;

        foreach (Tribe tribe in Tribes)
        {
            tribe.ChestSpots.Clear();

            foreach (int index in tribe.Chests)
            {
                if (index < 0 || index >= Main.chest.Length)
                    continue;

                Chest chest = Main.chest[index];
                if (chest == null)
                    continue;

                tribe.ChestSpots.Add((chest.x, chest.y + 1));
                restored++;

                for (int ix = chest.x; ix <= chest.x + 1; ix++)
                    for (int iy = chest.y; iy <= chest.y + 2; iy++)
                        BuiltMask.Set(ix, iy, tribe.Id);
            }
        }

        if (restored > 0)
            Mod.Logger.Info($"antfarm: restored {restored} chest positions and marked them as masonry");
    }

    private void SpawnWorkforce()
    {
        int per = AntfarmConfig.Instance?.VillagersPerTribe ?? 40;
        var namer = new System.Random(Main.worldID != 0 ? Main.worldID : 4242);

        foreach (Tribe tribe in Tribes)
        {
            tribe.Villagers.Clear();

            // A tribe that grew to 300 must come back as 300, not reset to the
            // starting headcount. Restarting used to quietly undo every birth.
            int want = tribe.SavedPopulation > 0 ? tribe.SavedPopulation : per;

            // Spread them across their settlements so an outpost is not empty.
            int sites = tribe.Settlements.Count;

            for (int i = 0; i < want; i++)
            {
                Settlement s = tribe.Settlements[sites == 0 ? 0 : i % sites];

                // Respawn from the tribe's saved gene pool, not from neutral.
                //
                // Villagers are recreated on every world load, so seeding them
                // as founders would silently erase every generation of drift on
                // each restart and evolution could never accumulate past one
                // session. The averages are saved; individuals are redrawn
                // around them.
                var genes = tribe.PoolGenes(namer);

                tribe.Villagers.Add(new Villager
                {
                    TribeId = tribe.Id,
                    Name = VillagerNames.Next(namer),
                    Genes = genes,
                    MaxHealth = genes.MaxHealth(60),
                    Health = genes.MaxHealth(60),
                    X = s.X * 16f + 8f + (i % 9) * 4f,
                    Y = s.Y * 16f - 24f,
                });
            }
        }
    }

    private void StartSim()
    {
        // OnWorldLoad can fire more than once in a session: rejoining a server
        // does it, and observed doing exactly that during testing, which left
        // two colony threads running against one world. Two threads is not just
        // wasted CPU, it is two sets of villagers fighting over the same dig
        // claims. Starting a sim always stops any previous one first.
        StopSim();

        int seed = Main.worldID != 0 ? Main.worldID : 12345;
        _ctx = new SimContext(Snapshot, Ops, Events, BuiltMask, seed);
        _sim = new SimThread(_ctx, Tribes) { TargetHz = AntfarmConfig.Instance?.SimHz ?? 60 };
        _sim.Start();
    }

    private void StopSim()
    {
        _sim?.Stop();
        _sim = null;
        _ctx = null;
    }

    // ------------------------------------------------------------------
    // Main thread tick
    // ------------------------------------------------------------------

    public override void PostUpdateWorld()
    {
        if (_pendingStart)
            EnsureStarted();

        if (_sim == null)
            return;

        _worldTicks++;

        // Keep the colony's picture of the world honest. A full sweep of a
        // large world completes every five seconds at this rate.
        Snapshot.Sweep(8);

        ApplyOps();
        SweepLitter();
        BanishTorchGod();
        KeepCapitalGround();
        FlushStockpiles();
        Raids();
        Defend();
        SyncVillagers();
        LogStatus();
    }

    private uint _lastSync;

    /// <summary>
    /// Push nearby villagers to every connected player, four times a second.
    /// Without this a player sees an empty world: the colony is entirely
    /// server side and the client has no idea any of it exists.
    /// </summary>
    private void SyncVillagers()
    {
        if (Main.netMode != NetmodeID.Server || !Netplay.HasClients)
            return;

        if (Main.GameUpdateCount - _lastSync < 15)
            return;

        _lastSync = Main.GameUpdateCount;
        VillagerSync.Broadcast(Mod, Tribes);

        // Say out loud what each player is being sent, and how far the nearest
        // villager actually is. "I see nobody" has two very different causes:
        // the sync is broken, or the colony is simply not near them.
        if (Main.GameUpdateCount - _lastSyncLog >= 600)
        {
            _lastSyncLog = Main.GameUpdateCount;

            int nearest = VillagerSync.NearestVillagerDistance(
                Tribes, VillagerSync.LastPlayerX, VillagerSync.LastPlayerY,
                out int nx, out int ny, out string who);

            Mod.Logger.Info(
                $"antfarm sync: player at ({VillagerSync.LastPlayerX},{VillagerSync.LastPlayerY}) " +
                $"sent={VillagerSync.LastSentCount} nearest={who} at ({nx},{ny}) {nearest} tiles away");
        }
    }

    private uint _lastSyncLog;

    /// <summary>
    /// Tribe identity for joining clients. They need names and colours to draw
    /// anything, and world data is not otherwise sent to them.
    /// </summary>
    public override void NetSend(System.IO.BinaryWriter writer)
    {
        writer.Write((byte)Tribes.Count);

        foreach (Tribe t in Tribes)
        {
            writer.Write((byte)t.Id);
            writer.Write(t.Name ?? "");
            writer.Write(t.ColorR);
            writer.Write(t.ColorG);
            writer.Write(t.ColorB);
            writer.Write(t.Undead);
        }
    }

    public override void NetReceive(System.IO.BinaryReader reader)
    {
        Tribes.Clear();

        int count = reader.ReadByte();
        for (int i = 0; i < count; i++)
        {
            var t = new Tribe
            {
                Id = reader.ReadByte(),
                Name = reader.ReadString(),
                ColorR = reader.ReadByte(),
                ColorG = reader.ReadByte(),
                ColorB = reader.ReadByte(),
                Undead = reader.ReadBoolean(),
            };

            Tribes.Add(t);
        }
    }

    private uint _lastRaid;
    private bool _wasNight;

    /// <summary>
    /// Send monsters at them after dark.
    ///
    /// Terraria's spawning is player-centric: it runs around each connected
    /// player, so a server with nobody online spawns nothing at all. That is
    /// why the defence code had never once fired and `lost` was a column of
    /// zeros. If the tribes are to have anything to defend against while you
    /// are asleep, the raids have to be sent deliberately.
    ///
    /// Deliberately modest. This is meant to cost them villagers and give
    /// walls, torches and soldiers a reason to exist, not to wipe them out.
    /// </summary>
    private void Raids()
    {
        if (Main.dayTime)
        {
            _wasNight = false;
            return;
        }

        if (!_wasNight)
        {
            _wasNight = true;
            Events.Add(EventKind.Battle, -1, "Night fell across the world");
        }

        if (Main.GameUpdateCount - _lastRaid < 60 * 4)
            return;

        _lastRaid = Main.GameUpdateCount;

        // Leave most of the NPC array free; the world still needs slots.
        int hostiles = 0;
        for (int i = 0; i < Main.maxNPCs; i++)
            if (Main.npc[i].active && !Main.npc[i].friendly && Main.npc[i].damage > 0)
                hostiles++;

        if (hostiles >= 70 || Tribes.Count == 0)
            return;

        Tribe tribe = Tribes[Main.rand.Next(Tribes.Count)];
        if (tribe.Settlements.Count == 0)
            return;

        Settlement site = tribe.Settlements[Main.rand.Next(tribe.Settlements.Count)];

        int sx = site.X + (Main.rand.NextBool() ? 1 : -1) * Main.rand.Next(35, 70);
        if (sx < 30 || sx > Main.maxTilesX - 30)
            return;

        int sy = site.Y - 6;
        while (sy < Main.maxTilesY - 60 && !Main.tile[sx, sy].HasTile)
            sy++;

        sy -= 3;
        if (sy < 20)
            return;

        int type = Main.rand.Next(4) switch
        {
            0 => NPCID.DemonEye,
            1 => NPCID.Zombie,
            2 => NPCID.Skeleton,
            _ => NPCID.Zombie,
        };

        int idx = NPC.NewNPC(new EntitySource_SpawnNPC(), sx * 16 + 8, sy * 16, type);
        if (idx < 0 || idx >= Main.maxNPCs)
            return;

        // One in eight nights brings something worth a headline.
        if (Main.rand.Next(8) == 0)
            Events.Add(EventKind.Battle, tribe.Id,
                $"{tribe.Name} is under attack near {site.X},{site.Y}");
    }

    private readonly Dictionary<long, List<Villager>> _grid = new();
    private uint _lastCombat;

    private const int CellTiles = 16;

    private static long CellKey(int tileX, int tileY)
        => ((long)(tileX / CellTiles) << 32) | (uint)(tileY / CellTiles);

    /// <summary>
    /// Villagers fight what comes at them.
    ///
    /// This has to run on the main thread because it touches Terraria's NPCs,
    /// so it must stay cheap. Checking every villager against every monster is
    /// O(n*m), and with thousands of villagers and two hundred NPCs that is
    /// millions of comparisons a second. Instead the villagers go into a coarse
    /// spatial grid once per pass and each monster only looks at the handful of
    /// cells around it.
    ///
    /// Runs four times a second rather than sixty. Combat resolution does not
    /// need frame precision, and the saving is the difference between this
    /// being free and it being the most expensive thing in the mod.
    /// </summary>
    private void Defend()
    {
        if (Main.GameUpdateCount - _lastCombat < 15)
            return;

        _lastCombat = Main.GameUpdateCount;

        foreach (List<Villager> bucket in _grid.Values)
            bucket.Clear();

        foreach (Tribe tribe in Tribes)
        {
            foreach (Villager v in tribe.Villagers)
            {
                v.Threatened = false;

                long key = CellKey(v.TileX, v.TileY);
                if (!_grid.TryGetValue(key, out List<Villager> bucket))
                    _grid[key] = bucket = new List<Villager>();

                bucket.Add(v);
            }
        }

        Border();

        for (int i = 0; i < Main.maxNPCs; i++)
        {
            NPC npc = Main.npc[i];

            // Hostile things only. Critters and town NPCs are left alone, and
            // so is anything that cannot actually hurt anyone.
            if (!npc.active || npc.friendly || npc.townNPC || npc.damage <= 0 || npc.lifeMax <= 5)
                continue;

            int nx = (int)(npc.Center.X / 16f);
            int ny = (int)(npc.Center.Y / 16f);

            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oy = -1; oy <= 1; oy++)
                {
                    long key = CellKey(nx + ox * CellTiles, ny + oy * CellTiles);

                    if (!_grid.TryGetValue(key, out List<Villager> bucket))
                        continue;

                    foreach (Villager v in bucket)
                        Skirmish(npc, v, TribeById(v.TribeId));
                }
            }
        }
    }

    /// <summary>
    /// Tribes meeting underground.
    ///
    /// Ten rivals have dug toward each other for hours with soldiers, weapons
    /// and territory, and have never once fought. When two tunnel networks meet
    /// the villagers at the seam now come to blows, and both tribes raise a
    /// threat there, which pulls their soldiers in. Their own excavation is the
    /// thing that brings them into contact, which is exactly right.
    ///
    /// Reuses the spatial grid built for monster combat, so this costs one pass
    /// over the buckets rather than comparing every villager to every other.
    /// </summary>
    private void Border()
    {
        foreach (List<Villager> bucket in _grid.Values)
        {
            if (bucket.Count < 2)
                continue;

            for (int a = 0; a < bucket.Count; a++)
            {
                Villager va = bucket[a];

                for (int b = a + 1; b < bucket.Count; b++)
                {
                    Villager vb = bucket[b];

                    if (va.TribeId == vb.TribeId)
                        continue;

                    float dx = vb.X - va.X;
                    float dy = vb.Y - va.Y;

                    if (dx * dx + dy * dy > 40f * 40f)
                        continue;

                    Tribe ta = TribeById(va.TribeId);
                    Tribe tb = TribeById(vb.TribeId);

                    ta?.RaiseThreat(va.TileX, va.TileY);
                    tb?.RaiseThreat(vb.TileX, vb.TileY);

                    Duel(va, ta, vb, tb);
                    Duel(vb, tb, va, ta);
                }
            }
        }
    }

    /// <summary>One villager swinging at another. Soldiers and metal decide it.</summary>
    private void Duel(Villager attacker, Tribe attackerTribe, Villager target, Tribe targetTribe)
    {
        if (attacker.AttackCooldown > 0 || !attacker.Alive || !target.Alive)
            return;

        attacker.AttackCooldown = 50;

        int damage = attacker.Soldier ? 12 : 4;
        damage += attacker.Weapon * 14;

        target.Health -= damage;
        target.Threatened = true;

        if (target.Health > 0 || targetTribe == null || attackerTribe == null)
            return;

        attacker.Kills++;
        attackerTribe.Kills++;

        Events.Add(EventKind.Battle, attackerTribe.Id,
            $"{attacker.Name} of {attackerTribe.Name} killed {target.Name} of " +
            $"{targetTribe.Name} at {target.TileX},{target.TileY}");
    }

    private void Skirmish(NPC npc, Villager v, Tribe tribe)
    {
        float dx = npc.Center.X - v.X;
        float dy = npc.Center.Y - v.Y;
        float distSq = dx * dx + dy * dy;

        // Roughly three tiles. Close enough to see it and to swing at it.
        if (distSq > 48f * 48f)
            return;

        v.Threatened = true;

        // Call for help. This is what turns a colony into an army: one villager
        // getting hit pulls every soldier within a hundred tiles toward the
        // spot, instead of them dying one at a time while the rest mine on
        // twenty tiles away.
        tribe?.RaiseThreat(v.TileX, v.TileY);

        if (v.AttackCooldown > 0)
            return;

        v.AttackCooldown = 45;

        // Bare hands are nearly useless; smelted weapons are not. This is what
        // the ore is finally for.
        int damage = v.Soldier ? 14 : 5;
        damage += v.Weapon * 16;

        int before = npc.life;
        npc.SimpleStrikeNPC(damage, dx > 0 ? 1 : -1, false, 0.4f, null, true);

        if (before > 0 && npc.life <= 0 && tribe != null)
        {
            v.Kills++;
            tribe.Kills++;
            Events.Add(EventKind.Battle, tribe.Id,
                $"{v.Name} of {tribe.Name} killed a {npc.TypeName}");
        }

        // And it hits back. Scaled well down: a villager should survive a slime
        // and lose badly to anything serious.
        v.Health -= 1 + npc.damage / 6;

        if (v.Health <= 0 && tribe != null)
            Events.Add(EventKind.Battle, tribe.Id,
                $"{v.Name} of {tribe.Name} was killed by a {npc.TypeName} at depth {v.TileY}, " +
                $"after digging {v.TilesDug} blocks");
    }

    private uint _lastLog;
    private long _everythingTicks;
    private long _worldTicks;

    private long _lastStallWorldTicks;

    /// <summary>
    /// Stall detector.
    ///
    /// Runs on every main loop iteration, whether or not the world is being
    /// updated. A colony that has died and a colony that is merely idle look
    /// identical from the outside, and this mod is meant to run unattended for
    /// months, so the two are made to look different here.
    ///
    /// The specific failure worth catching: the game is running, the colony
    /// thread is producing work, and nothing is applying it. That is silent,
    /// it looks exactly like a quiet world, and it is the state a dedicated
    /// server with no players connected sits in permanently.
    /// </summary>
    public override void PostUpdateEverything()
    {
        _everythingTicks++;

        // Once a minute is plenty. At ten second intervals this would write
        // millions of lines over a year long run.
        if (_everythingTicks % 3600 != 0)
            return;

        bool worldStalled = _worldTicks == _lastStallWorldTicks;
        _lastStallWorldTicks = _worldTicks;

        if (worldStalled && !Ops.IsEmpty)
        {
            Mod.Logger.Warn(
                $"antfarm STALLED: the colony has {Ops.Count} tile changes waiting but the world " +
                $"is not updating (loopTicks={_everythingTicks} worldTicks={_worldTicks} " +
                $"hasClients={Netplay.HasClients} dedServ={Main.dedServ}). " +
                "On a dedicated server this is normal with nobody connected.");
        }
    }

    /// <summary>
    /// Print what the colony has actually done, every ten seconds.
    ///
    /// This exists because "it did not crash" is not evidence that anything is
    /// happening. A stalled colony and a working one look identical from the
    /// outside, and the whole point of this mod is that it runs unattended for
    /// months. If the numbers below stop climbing, something is wrong, and that
    /// should be visible without attaching a debugger.
    /// </summary>
    private void LogStatus()
    {
        if (Main.GameUpdateCount - _lastLog < 600)
            return;

        _lastLog = Main.GameUpdateCount;

        long mined = 0, stored = 0;
        foreach (Tribe t in Tribes)
        {
            mined += t.TilesMined;
            stored += t.ItemsStored;
        }

        Mod.Logger.Info(
            $"colony: simTicks={_sim.Ticks} queued={Ops.Count} mined={mined} stored={stored} tribes={Tribes.Count}");

        foreach (Tribe t in Tribes)
        {
            t.CountTasks(out int idle, out int outbound, out int returning, out int building);

            Mod.Logger.Info(
                $"  {t.Name,-12} pop={t.Villagers.Count}/{t.PopulationCap} towns={t.Settlements.Count} " +
                $"rooms={t.Rooms} idle={idle} out={outbound} ret={returning} build={building} " +
                $"stock={t.BuildStockCount} built={t.BuiltTiles} stairs={t.StairsBuilt} " +
                $"done={t.BuildingsFinished} gaveup={t.BuildingsAbandoned} " +
                $"sky={t.SkyFolk} skymason={t.SkyMasons} masonY={t.MasonMedianDepth} capY={(t.Settlements.Count > 0 ? t.Settlements[0].Y : 0)} " +
                $"mined={t.TilesMined} unmapped={t.UnmappedMined} hauling={t.HaulingCount} " +
                $"deliveries={t.Deliveries} stored={t.ItemsStored} chests={t.Chests.Count}");
        }

        // What is actually flying around out there.
        //
        // A player standing in the tunnels reported being shot at and hearing
        // explosions. I guessed vanilla traps, added a disarm, and the counter
        // came back at zero to four across ten tribes, which disproved it. So
        // rather than guess a third time: census the live projectiles and the
        // live NPCs and let the world say what it is spawning.
        var projCount = new Dictionary<int, int>();
        int projTotal = 0;

        for (int i = 0; i < Main.maxProjectiles; i++)
        {
            Projectile pr = Main.projectile[i];
            if (pr == null || !pr.active)
                continue;

            projCount.TryGetValue(pr.type, out int n);
            projCount[pr.type] = n + 1;
            projTotal++;
        }

        if (projTotal > 0)
        {
            var top = new List<KeyValuePair<int, int>>(projCount);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sb = new System.Text.StringBuilder();
            sb.Append($"projectiles: {projTotal} live -");

            for (int i = 0; i < 6 && i < top.Count; i++)
                sb.Append($" {Lang.GetProjectileName(top[i].Key).Value}({top[i].Key})x{top[i].Value}");

            Mod.Logger.Info(sb.ToString());
        }
        else
        {
            Mod.Logger.Info("projectiles: none live");
        }

        var itemCount = new Dictionary<int, int>();
        int itemTotal = 0;

        for (int i = 0; i < Main.maxItems; i++)
        {
            Item it = Main.item[i];
            if (it == null || !it.active || it.type <= 0)
                continue;

            itemCount.TryGetValue(it.type, out int c);
            itemCount[it.type] = c + 1;
            itemTotal++;
        }

        if (itemTotal > 0)
        {
            var topI = new List<KeyValuePair<int, int>>(itemCount);
            topI.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sbi = new System.Text.StringBuilder();
            sbi.Append($"items: {itemTotal} loose -");

            for (int i = 0; i < 6 && i < topI.Count; i++)
                sbi.Append($" {Lang.GetItemNameValue(topI[i].Key)}x{topI[i].Value}");

            Mod.Logger.Info(sbi.ToString());
        }
        else
        {
            Mod.Logger.Info("items: none loose");
        }

        var npcCount = new Dictionary<int, int>();
        int npcTotal = 0;

        for (int i = 0; i < Main.maxNPCs; i++)
        {
            NPC n2 = Main.npc[i];
            if (n2 == null || !n2.active)
                continue;

            npcCount.TryGetValue(n2.type, out int c);
            npcCount[n2.type] = c + 1;
            npcTotal++;
        }

        if (npcTotal > 0)
        {
            var topN = new List<KeyValuePair<int, int>>(npcCount);
            topN.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sb = new System.Text.StringBuilder();
            sb.Append($"npcs: {npcTotal} live -");

            for (int i = 0; i < 6 && i < topN.Count; i++)
                sb.Append($" {Lang.GetNPCNameValue(topN[i].Key)}({topN[i].Key})x{topN[i].Value}");

            Mod.Logger.Info(sb.ToString());
        }

        // Sample workers from the least productive tribe. A tribe wide count
        // says they are all outbound; only a single villager's state says why
        // outbound never turns into digging.
        Tribe worst = null;
        foreach (Tribe t in Tribes)
            if (worst == null || t.TilesMined < worst.TilesMined)
                worst = t;

        if (worst != null)
            for (int i = 0; i < 3 && i < worst.Villagers.Count; i++)
                Mod.Logger.Info($"  sample {worst.Name}[{i}] {worst.Villagers[i].Describe()}");

        // Sample villagers actually holding a build job. Every tribe reports
        // handed == outstanding, so not one has ever been returned, and only
        // the villager's own state can say why.
        foreach (Tribe t in Tribes)
        {
            int shown = 0;

            foreach (Villager v in t.Villagers)
            {
                if (v.Task != VillagerTask.Building || shown >= 2)
                    continue;

                Mod.Logger.Info($"  BUILDER {t.Name}: {v.Describe()} kind={v.BuildWanted}");
                shown++;
            }
        }
    }

    private int _litterCursor;

    /// <summary>
    /// Sweep the tribes' litter off the floor.
    ///
    /// Mining already drops nothing, but Terraria drops attached decorations
    /// on its own when it re-frames a tile that has lost its support, and at
    /// several thousand tile operations a second that beats any per tile
    /// guard. The result was a world item cap of 400 sitting permanently full,
    /// 364 of them torches: a cavern full of glowing bobbing litter, which is
    /// what a player standing in the tunnels was seeing, and no room left for
    /// a real ore drop to ever appear.
    ///
    /// So the litter is swept. Only what the tribes shed - torches, platforms
    /// and the bulk blocks they build with - is taken. Ore, coins, stars and
    /// anything else a player might actually want are left where they fall,
    /// which is the whole reason this is a list and not a wipe.
    /// </summary>
    /// <summary>
    /// Send the Torch God home.
    ///
    /// Place enough torches close together underground and Terraria spawns an
    /// entity that fires a barrage of flaming projectiles at the nearest
    /// player. The tribes light every tunnel they dig, so they were summoning
    /// it on him over and over: this is the "being shot at by fireworks", and
    /// the explosions, and it took a projectile census to find because nothing
    /// in this mod fires anything. The torches are the point of the torches,
    /// so the event goes rather than the lighting.
    /// </summary>
    private void BanishTorchGod()
    {
        for (int i = 0; i < Main.maxProjectiles; i++)
        {
            Projectile pr = Main.projectile[i];

            if (pr == null || !pr.active || pr.type != ProjectileID.TorchGod)
                continue;

            pr.active = false;
            pr.Kill();
            NetMessage.SendData(MessageID.SyncProjectile, -1, -1, null, i);
        }

        // The event itself is left alone. Its API is not reachable from a mod
        // here, and it does not need to be: this runs every tick, so a
        // projectile it spawns is gone within one frame and the barrage never
        // reaches anybody.
    }

    private void SweepLitter()
    {
        for (int n = 0; n < 64; n++)
        {
            _litterCursor++;
            if (_litterCursor >= Main.maxItems)
                _litterCursor = 0;

            Item it = Main.item[_litterCursor];

            if (it == null || !it.active || it.type <= 0)
                continue;

            if (!IsLitter(it.type))
                continue;

            it.active = false;
            it.TurnToAir();
            NetMessage.SendData(MessageID.SyncItem, -1, -1, null, _litterCursor);
        }
    }

    private static bool IsLitter(int itemType) =>
        itemType == ItemID.Torch ||
        itemType == ItemID.WoodPlatform ||
        itemType == ItemID.StoneBlock ||
        itemType == ItemID.DirtBlock ||
        itemType == ItemID.Wood ||
        itemType == ItemID.Cobweb ||
        itemType == ItemID.SiltBlock ||
        itemType == ItemID.SandBlock ||
        itemType == ItemID.MudBlock ||
        itemType == ItemID.ClayBlock ||
        itemType == ItemID.SlushBlock ||
        itemType == ItemID.SnowBlock ||
        itemType == ItemID.IceBlock ||
        itemType == ItemID.AshBlock ||
        itemType == ItemID.GlowingMushroom ||
        itemType == ItemID.Rope ||
        // A loose chest is a cache that was knocked off its foundation, which
        // is a bug being cleaned up rather than treasure. There were 152 of
        // them on the floor, and with the cap no longer under pressure they
        // would have sat there for the life of the world.
        itemType == ItemID.Chest;

    private uint _groundTick;

    /// <summary>
    /// Keep solid ground under every capital, and make it unmineable.
    ///
    /// This is why nothing was ever built above ground, and it was never a
    /// decision any villager made. The tribes quarried the ground out from
    /// under their own settlements, so a newborn appeared over an open shaft
    /// and fell to the bottom of the world. A census found sky=0 for all ten
    /// tribes: of five and a half thousand villagers not one was near the
    /// surface, and every mason sat about 1,800 tiles below its own capital.
    /// A tower cannot be staffed by people who are physically unable to be
    /// there, so the towers were sited, walked through their phases on the
    /// stall net, and never received a single block.
    ///
    /// The slab goes in directly rather than through the op queue, because
    /// that queue is saturated by digging every single tick and CanQueue is
    /// therefore false almost always: the first attempt at this laid a floor
    /// through Queue and not one tile of it was ever placed. It is bounded
    /// work, ten tribes by eighty one tiles by four rows, and it is marked in
    /// BuiltMask so the tribes route around it like any of their own masonry.
    /// </summary>
    private void KeepCapitalGround()
    {
        if (Main.GameUpdateCount - _groundTick < 900)
            return;

        _groundTick = Main.GameUpdateCount;

        foreach (Tribe tribe in Tribes)
        {
            if (tribe.Settlements.Count == 0)
                continue;

            int cx = tribe.Settlements[0].X;
            int cy = tribe.Settlements[0].Y;

            for (int x = cx - 40; x <= cx + 40; x++)
            {
                if (x < 1 || x >= Main.maxTilesX - 1)
                    continue;

                for (int y = cy + 1; y <= cy + 4; y++)
                {
                    if (y < 1 || y >= Main.maxTilesY - 1)
                        continue;

                    if (!Main.tile[x, y].HasTile)
                    {
                        WorldGen.PlaceTile(x, y, TileID.GrayBrick, true, false, -1);
                        Snapshot.Set(x, y, Main.tile[x, y].HasTile);
                        NetMessage.SendTileSquare(-1, x, y, 1);
                    }

                    // Theirs now, so nobody digs the plaza out again.
                    BuiltMask.Set(x, y, tribe.Id);
                }
            }
        }
    }

    private void ApplyOps()
    {
        var cfg = AntfarmConfig.Instance;
        int budget = cfg?.TileOpsPerTick ?? 96;

        // Nothing is being rendered while the window is in the background, so
        // hand that time to the tribes instead. This is the difference between
        // "it built a bit while I was in Vaki" and "I came back to a city".
        if (!Main.instance.IsActive)
            budget *= cfg?.UnfocusedBudgetMultiplier ?? 8;

        for (int n = 0; n < budget; n++)
        {
            if (!Ops.TryDequeue(out TileOp op))
                break;

            Apply(op);
        }
    }

    /// <summary>
    /// Tiles that attack when broken. Wire and pressure plates are included
    /// because breaking the plate is itself a trigger.
    /// </summary>
    private static bool IsHazard(ushort type) =>
        type == TileID.Traps ||
        type == TileID.Explosives ||
        type == TileID.LandMine ||
        type == TileID.Boulder ||
        type == TileID.GeyserTrap ||
        type == TileID.PressurePlates;

    /// <summary>True if a chest is resting on this tile, or sits in it.</summary>
    private static bool Supports(int x, int y)
    {
        for (int dy = -1; dy <= 0; dy++)
        {
            int ty = y + dy;
            if (ty < 1 || ty >= Main.maxTilesY - 1)
                continue;

            for (int dx = 0; dx <= 1; dx++)
            {
                int tx = x - dx;
                if (tx < 1 || tx >= Main.maxTilesX - 1)
                    continue;

                Tile t = Main.tile[tx, ty];

                if (t.HasTile &&
                    (t.TileType == TileID.Containers || t.TileType == TileID.Containers2))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Remove torches hanging on a tile that is about to be mined, without
    /// letting them drop. They were going to be destroyed either way.
    /// </summary>
    private void ClearAttachedTorches(int x, int y)
    {
        Neighbour(x, y - 1);
        Neighbour(x, y + 1);
        Neighbour(x - 1, y);
        Neighbour(x + 1, y);
    }

    private void Neighbour(int x, int y)
    {
        if (x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1)
            return;

        Tile t = Main.tile[x, y];

        if (!t.HasTile || t.TileType != TileID.Torches)
            return;

        Main.tile[x, y].ClearTile();
        Snapshot.Set(x, y, false);
        NetMessage.SendTileSquare(-1, x, y, 1);
    }

    private void Apply(in TileOp op)
    {
        if (op.X < 1 || op.Y < 1 || op.X >= Main.maxTilesX - 1 || op.Y >= Main.maxTilesY - 1)
            return;

        switch (op.Kind)
        {
            case TileOpKind.Mine:
            {
                Tile t = Main.tile[op.X, op.Y];
                if (!t.HasTile)
                {
                    Snapshot.Set(op.X, op.Y, false);
                    return;
                }

                // Disarm hazards, do not set them off.
                //
                // Terraria's caverns are laced with dart traps, flame traps,
                // explosives, land mines and boulders, all wired to fire when
                // the tile is broken. At five thousand tiles a second the
                // tribes were detonating every one they met: continuous
                // explosions and a crossfire of burning darts across the whole
                // underground, which is what a player standing in it actually
                // sees and hears. KillTile is what triggers them, so hazards
                // are cleared straight out of the tile array instead. Nothing
                // fires, nothing drops, and the tunnel still gets dug.
                // Take attached torches off first, silently.
                //
                // Mining drops nothing, but a torch is attached to its host
                // tile, and Terraria pops it off as a separate item when the
                // host goes. The tribes light every tunnel they dig and then
                // dig away the walls they lit, so torches were falling loose
                // faster than the world could clean them up: a census found
                // 364 loose torches against a world item cap of 400. Hundreds
                // of glowing bobbing torch items in a dark cavern is what a
                // player standing in it sees, and it is why nothing else could
                // drop either, the cap being full of them.
                // Never mine a chest's floor out from under it.
                //
                // A chest is held up by the tile beneath it, and when that goes
                // the chest breaks and drops as an item with everything inside
                // it. The tribes were tunnelling under their own caches: two
                // dozen chests loose on the ground at once, each one a granary
                // that fell on the floor. The chest tile itself was already
                // protected; its foundation was not, which protected nothing.
                if (Supports(op.X, op.Y))
                    return;

                ClearAttachedTorches(op.X, op.Y);

                if (IsHazard(t.TileType))
                {
                    Main.tile[op.X, op.Y].ClearTile();
                    Snapshot.Set(op.X, op.Y, false);
                    Mask.Set(op.X, op.Y, op.TribeId);
                    NetMessage.SendTileSquare(-1, op.X, op.Y, 1);

                    Tribe disarmed = TribeById(op.TribeId);
                    if (disarmed != null)
                    {
                        disarmed.TilesMined++;
                        disarmed.HazardsDisarmed++;
                    }

                    return;
                }

                TileDrops.Resolve(op.X, op.Y, t, out int drop, out int stack);

                // noItem: nothing hits the floor. A year of digging would
                // otherwise bury the world in loose item entities.
                WorldGen.KillTile(op.X, op.Y, false, false, true);

                if (!Main.tile[op.X, op.Y].HasTile)
                {
                    Snapshot.Set(op.X, op.Y, false);

                    Mask.Set(op.X, op.Y, op.TribeId);

                    Tribe tribe = TribeById(op.TribeId);
                    if (tribe != null)
                    {
                        tribe.TilesMined++;
                        tribe.CreditMined(drop, stack);

                        // Worth a headline the first time a tribe turns up a
                        // seam of something valuable, and never again.
                        // Only real finds. IsWorthy defaults to true for
                        // anything unrecognised, so without this the feed
                        // proudly announced striking Wood and Rain Cloud.
                        if (drop > 0 && Materials.IsNotable(drop) && tribe.FirstStrike(drop))
                            Events.Add(EventKind.Strike, tribe.Id,
                                $"{tribe.Name} struck {Lang.GetItemNameValue(drop)} at depth {op.Y}");
                    }
                }
                break;
            }

            case TileOpKind.Place:
                WorldGen.PlaceTile(op.X, op.Y, op.Type, true, false, -1);
                Snapshot.Set(op.X, op.Y, Main.tile[op.X, op.Y].HasTile);

                // Remember who put it there, so nobody mines it back out.
                if (Main.tile[op.X, op.Y].HasTile)
                    BuiltMask.Set(op.X, op.Y, op.TribeId);
                break;

            case TileOpKind.PlacePlatform:
                WorldGen.PlaceTile(op.X, op.Y, TileID.Platforms, true, false, -1);
                break;

            case TileOpKind.PlaceWall:
                WorldGen.PlaceWall(op.X, op.Y, op.Type, true);
                break;
        }

        if (Main.netMode != NetmodeID.SinglePlayer)
            NetMessage.SendTileSquare(-1, op.X, op.Y, 1);
    }

    private Tribe TribeById(int id)
    {
        for (int i = 0; i < Tribes.Count; i++)
            if (Tribes[i].Id == id)
                return Tribes[i];
        return null;
    }

    // ------------------------------------------------------------------
    // Stockpiles
    // ------------------------------------------------------------------

    private void FlushStockpiles()
    {
        // One tribe per tick. There is no hurry, and this keeps the cost flat
        // no matter how many tribes exist.
        if (Tribes.Count == 0)
            return;

        Tribe tribe = Tribes[(int)(Main.GameUpdateCount % (uint)Tribes.Count)];

        BuildCaches(tribe);
        List<KeyValuePair<int, int>> delivered = tribe.DrainLedger();
        if (delivered == null)
            return;

        foreach (var entry in delivered)
        {
            int leftover = StoreInVault(tribe, entry.Key, entry.Value);
            if (leftover > 0)
                tribe.ReturnToLedger(entry.Key, leftover);
        }
    }

    /// <summary>
    /// Push material into the tribe's chests, adding a chest when they fill.
    /// Returns however much would not fit, so nothing is silently destroyed.
    /// </summary>
    private int StoreInVault(Tribe tribe, int itemType, int count)
    {
        if (itemType <= 0 || count <= 0)
            return 0;

        if (tribe.Chests.Count == 0 && !AddChest(tribe))
            return count;

        for (int pass = 0; pass < 2; pass++)
        {
            foreach (int chestIndex in tribe.Chests)
            {
                Chest chest = Main.chest[chestIndex];
                if (chest == null)
                    continue;

                for (int slot = 0; slot < chest.item.Length && count > 0; slot++)
                {
                    Item item = chest.item[slot];

                    if (item == null || item.IsAir)
                    {
                        item = new Item();
                        item.SetDefaults(itemType);
                        item.stack = count > item.maxStack ? item.maxStack : count;
                        count -= item.stack;
                        tribe.ItemsStored += item.stack;
                        chest.item[slot] = item;
                    }
                    else if (item.type == itemType && item.stack < item.maxStack)
                    {
                        int room = item.maxStack - item.stack;
                        int moved = count < room ? count : room;
                        item.stack += moved;
                        count -= moved;
                        tribe.ItemsStored += moved;
                    }
                }

                if (count <= 0)
                    return 0;
            }

            // Everything full. Dig out another chest and try once more. This is
            // how hoarding turns into architecture rather than a silent cap.
            if (pass == 0 && !AddChest(tribe))
                break;
        }

        return count;
    }

    /// <summary>
    /// Add a chest to the tribe's vault.
    ///
    /// An earlier version tried exactly one cell and gave up. Four of ten
    /// tribes could never place a chest there, so they mined forever, delivered
    /// forever, and stored nothing: the goods bounced straight back into the
    /// ledger on every attempt. Nothing looked broken. So this now prepares the
    /// pocket properly and tries a spread of spots before admitting defeat.
    /// </summary>
    private bool AddChest(Tribe tribe)
    {
        int row = tribe.Chests.Count;
        int y = tribe.HomeY - 1 - (row / 12) * 3;

        for (int attempt = 0; attempt < 24; attempt++)
        {
            // Alternate outward either side of home rather than marching in one
            // direction into whatever happens to be there.
            int spread = 2 + attempt;
            int x = tribe.HomeX + (attempt % 2 == 0 ? spread : -spread);

            if (TryPlaceChest(tribe, x, y, out int index))
            {
                tribe.RegisterChest(index, x, y);

                if (Main.netMode != NetmodeID.SinglePlayer)
                    NetMessage.SendTileSquare(-1, x, y, 3);

                return true;
            }
        }

        Mod.Logger.Warn(
            $"antfarm: {tribe.Name} could not place a vault chest anywhere near " +
            $"({tribe.HomeX},{y}) after 24 attempts; stockpile is stalled");

        return false;
    }

    /// <summary>
    /// Build the caches the colony asked for, out at the dig front.
    ///
    /// This is what stops the haul home growing without limit as the tribe
    /// burrows. A hauler that finds no chest within sixty tiles asks for one
    /// where it stands, and storage follows the work instead of the workers
    /// walking further every trip until the economy stops.
    /// </summary>
    private void BuildCaches(Tribe tribe)
    {
        for (int n = 0; n < 2; n++)
        {
            (int X, int Y) spot;

            lock (tribe.CacheRequests)
            {
                if (tribe.CacheRequests.Count == 0)
                    return;

                spot = tribe.CacheRequests.Dequeue();
            }

            // Somebody may have already put one there while this was queued.
            if (tribe.NearestChest(spot.X, spot.Y, out _, out _, out int near) && near < 40)
                continue;

            for (int attempt = 0; attempt < 12; attempt++)
            {
                int x = spot.X + (attempt % 2 == 0 ? attempt / 2 : -(attempt / 2));
                int y = spot.Y;

                if (!TryPlaceChest(tribe, x, y, out int index))
                    continue;

                tribe.RegisterChest(index, x, y);

                if (Main.netMode != NetmodeID.SinglePlayer)
                    NetMessage.SendTileSquare(-1, x, y, 3);

                // A cache is the seed of an underground room: queue a hall
                // around it so the storage network becomes a city rather than
                // chests in bare tunnels.
                tribe.QueueSite(x, y, underground: true);

                Events.Add(EventKind.Colony, tribe.Id,
                    $"{tribe.Name} opened a cache at {x},{y}");
                break;
            }
        }
    }

    private bool TryPlaceChest(Tribe tribe, int x, int y, out int index)
    {
        index = -1;

        if (x < 10 || x > Main.maxTilesX - 12 || y < 10 || y > Main.maxTilesY - 12)
            return false;

        // A chest occupies a 2x2 starting at (x, y-1) and needs solid ground
        // directly beneath both of its columns.
        for (int ix = x; ix <= x + 1; ix++)
        {
            for (int iy = y - 1; iy <= y; iy++)
            {
                if (Main.tile[ix, iy].HasTile)
                    WorldGen.KillTile(ix, iy, false, false, true);

                // Liquid in the cavity makes PlaceChest refuse.
                Main.tile[ix, iy].LiquidAmount = 0;
                Snapshot.Set(ix, iy, false);
            }

            Tile floor = Main.tile[ix, y + 1];
            if (!floor.HasTile || !Main.tileSolid[floor.TileType])
            {
                if (floor.HasTile)
                    WorldGen.KillTile(ix, y + 1, false, false, true);

                WorldGen.PlaceTile(ix, y + 1, TileID.GrayBrick, true, true, -1);
                Snapshot.Set(ix, y + 1, true);
            }
        }

        index = WorldGen.PlaceChest(x, y, TileID.Containers, false, 0);

        if (index < 0)
            return false;

        // Mark the chest and the brick under it as tribe masonry.
        //
        // Without this the colony treats its own vault as raw stone. Halls are
        // planned around caches, so the clearing phase tried to mine the chest
        // out, and Terraria refuses to break a chest holding items. Every tribe
        // that reached that point froze at phase 0 with one or two impossible
        // jobs left, for ever, and never placed a single block. It also stops
        // miners quarrying away their own stockpile.
        for (int ix = x; ix <= x + 1; ix++)
        {
            for (int iy = y - 1; iy <= y + 1; iy++)
                BuiltMask.Set(ix, iy, tribe.Id);

            Snapshot.Set(ix, y - 1, true);
            Snapshot.Set(ix, y, true);
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    /// <summary>
    /// The roll of the dead has to survive a restart, or a wiped out tribe
    /// would come back as strangers rather than as itself. Capped at the two
    /// hundred most recent, since that is far more than a rising ever raises.
    /// </summary>
    private static List<TagCompound> SaveDead(Tribe t)
    {
        var list = new List<TagCompound>();

        lock (t.Dead)
        {
            int from = t.Dead.Count > 200 ? t.Dead.Count - 200 : 0;

            for (int i = from; i < t.Dead.Count; i++)
            {
                Fallen f = t.Dead[i];
                list.Add(new TagCompound
                {
                    ["n"] = f.Name ?? "",
                    ["d"] = f.TilesDug,
                    ["k"] = f.Kills,
                    ["y"] = f.DeepestY,
                    ["s"] = f.WasSoldier,
                    ["w"] = f.Weapon,
                });
            }
        }

        return list;
    }

    private static string MaskPath(string kind) =>
        System.IO.Path.Combine(Main.SavePath, "Antfarm", $"{kind}-{Main.worldID}.bin");

    public override void SaveWorldData(TagCompound tag)
    {
        // Written alongside the world rather than inside it: a byte per tile is
        // far too much for a TagCompound, but gzips small on disk.
        Mask.Save(MaskPath("mined"));
        BuiltMask.Save(MaskPath("built"));

        var list = new List<TagCompound>();

        foreach (Tribe t in Tribes)
        {
            var stockTypes = new List<int>();
            var stockCounts = new List<int>();
            var oreTypes = new List<int>();
            var oreCounts = new List<int>();
            t.ExportStock(stockTypes, stockCounts, oreTypes, oreCounts);

            var sx = new List<int>();
            var sy = new List<int>();
            var rooms = new List<int>();

            foreach (Settlement s in t.Settlements)
            {
                sx.Add(s.X);
                sy.Add(s.Y);
                rooms.Add(s.Slot);
            }

            list.Add(new TagCompound
            {
                ["id"] = t.Id,
                ["name"] = t.Name,
                ["sx"] = sx,
                ["sy"] = sy,
                ["rooms"] = rooms,
                ["reach"] = t.Reach,
                ["r"] = (int)t.ColorR,
                ["g"] = (int)t.ColorG,
                ["b"] = (int)t.ColorB,
                ["chests"] = t.Chests,
                ["pop"] = t.Villagers.Count,
                ["mined"] = t.TilesMined,
                ["stored"] = t.ItemsStored,
                ["built"] = t.BuiltTiles,
                ["losses"] = t.Losses,
                ["trait"] = (int)t.Trait,
                ["gv"] = t.GeneVigour,
                ["gc"] = t.GeneCapacity,
                ["gt"] = t.GeneToughness,
                ["gb"] = t.GeneBoldness,
                ["gw"] = t.GeneWander,
                ["births"] = t.Births,
                ["stockT"] = stockTypes,
                ["stockN"] = stockCounts,
                ["oreT"] = oreTypes,
                ["oreN"] = oreCounts,
                ["bars"] = t.Bars,
                ["undead"] = t.Undead,
                ["dead"] = SaveDead(t),
            });
        }

        tag["tribes"] = list;
    }

    public override void LoadWorldData(TagCompound tag)
    {
        Tribes.Clear();

        if (!tag.ContainsKey("tribes"))
            return;

        foreach (TagCompound t in tag.GetList<TagCompound>("tribes"))
        {
            var tribe = new Tribe
            {
                Id = t.GetInt("id"),
                Name = t.GetString("name"),
                Reach = t.GetInt("reach"),
                ColorR = (byte)t.GetInt("r"),
                ColorG = (byte)t.GetInt("g"),
                ColorB = (byte)t.GetInt("b"),

                // Lifetime totals survive a restart now. Losing them meant the
                // scoreboard reset to zero every reboot while the tunnels the
                // numbers described were still sitting in the world.
                TilesMined = t.GetLong("mined"),
                ItemsStored = t.GetLong("stored"),
                BuiltTiles = t.GetLong("built"),
                Losses = t.GetLong("losses"),
                SavedPopulation = t.GetInt("pop"),
                // A world saved before traits existed has no key here, and
                // GetInt would hand back 0 for every tribe, making all ten
                // Delvers. Fall back to the same spread used at seeding.
                Trait = t.ContainsKey("trait")
                    ? (TribeTrait)t.GetInt("trait")
                    : (TribeTrait)(t.GetInt("id") % 5),
                Undead = t.GetBool("undead"),
                GeneVigour = t.GetInt("gv"),
                GeneCapacity = t.GetInt("gc"),
                GeneToughness = t.GetInt("gt"),
                GeneBoldness = t.GetInt("gb"),
                GeneWander = t.GetInt("gw"),
                Births = t.GetLong("births"),
            };

            if (t.ContainsKey("dead"))
                foreach (TagCompound d in t.GetList<TagCompound>("dead"))
                    tribe.Dead.Add(new Fallen(
                        d.GetString("n"), d.GetLong("d"), d.GetInt("k"),
                        d.GetInt("y"), d.GetBool("s"), d.GetInt("w")));

            var sx = t.GetList<int>("sx");
            var sy = t.GetList<int>("sy");
            var rooms = t.GetList<int>("rooms");

            for (int i = 0; i < sx.Count && i < sy.Count; i++)
                tribe.Settlements.Add(new Settlement(sx[i], sy[i])
                {
                    // Saved under the old name; it is the layout cursor, not a
                    // housing count. Housing is derived from blocks placed now.
                    Slot = i < rooms.Count ? rooms[i] : 0,
                });

            tribe.Bars = t.GetLong("bars");

            if (t.ContainsKey("stockT"))
                tribe.ImportStock(
                    t.GetList<int>("stockT"), t.GetList<int>("stockN"),
                    t.GetList<int>("oreT"), t.GetList<int>("oreN"));

            tribe.Chests.AddRange(t.GetList<int>("chests"));
            Tribes.Add(tribe);
        }
    }

    // ------------------------------------------------------------------
    // Drawing
    // ------------------------------------------------------------------

    private Color ColourOf(int tribeId)
    {
        foreach (Tribe t in Tribes)
            if (t.Id == tribeId)
                return new Color(t.ColorR, t.ColorG, t.ColorB);

        return TribeColors[tribeId % TribeColors.Length];
    }

    /// <summary>
    /// One villager. Deliberately readable at a glance rather than pretty:
    /// body, darker head showing facing, and a bright cap on soldiers so an
    /// armed mob arriving at a threat looks like an armed mob arriving.
    /// </summary>
    /// <summary>
    /// One villager per tribe, drawn as an actual person.
    ///
    /// They were three coloured rectangles: a body, a smaller head, and a bar
    /// for a helmet. In a lit room that reads as a crude little figure. In an
    /// unlit cavern, which is where these tribes actually live, it reads as a
    /// pale ghost blob, and a few hundred of them at ten different tribe
    /// colours reads as fireworks going off in your face.
    ///
    /// Terraria already ships a townsfolk sprite for every one of these, drawn
    /// in exactly the right style and already animated, so there is no reason
    /// to invent art. Each tribe gets its own townsperson, which does the job
    /// the colours were doing: you can tell at a glance whose tunnel you are
    /// standing in. The tribe colour survives as a small badge over the head,
    /// because ten NPC types is identity but not a legend.
    /// </summary>
    private static readonly int[] TribeFolk =
    {
        NPCID.Guide, NPCID.Merchant, NPCID.Dryad, NPCID.Demolitionist,
        NPCID.ArmsDealer, NPCID.Clothier, NPCID.Painter, NPCID.Stylist,
        NPCID.GoblinTinkerer, NPCID.Cyborg,
    };

    private static void DrawOne(Texture2D pixel, float x, float y, Color body, int tribeId,
                                bool facingRight, bool soldier, bool undead)
    {
        int tileX = (int)(x / 16f);
        int tileY = (int)(y / 16f);

        Color lit = Lighting.GetColor(tileX, tileY);

        // Never fully black. Underground the tribes are the only thing worth
        // looking at, and true lighting made them invisible in their own
        // unlit tunnels.
        int floor = undead ? 90 : 70;
        int r = System.Math.Max(lit.R, floor);
        int g = System.Math.Max(lit.G, floor);
        int b = System.Math.Max(lit.B, floor);
        var light = new Color(r, g, b);

        int npcId = undead
            ? NPCID.Zombie
            : TribeFolk[((tribeId % TribeFolk.Length) + TribeFolk.Length) % TribeFolk.Length];

        Texture2D sprite = FolkTexture(npcId);

        float screenX = x - Main.screenPosition.X;
        float screenY = y - Main.screenPosition.Y;

        if (sprite == null)
        {
            // The vanilla texture is not loaded yet. Draw the old block rather
            // than nothing, so a villager is never invisible.
            var fallback = new Rectangle(
                (int)(screenX - Villager.Width * 0.5f),
                (int)(screenY - Villager.Height * 0.5f),
                (int)Villager.Width, (int)Villager.Height);

            Main.spriteBatch.Draw(pixel, fallback, Multiply(body, light));
        }
        else
        {
            int frames = Main.npcFrameCount[npcId];
            if (frames < 1)
                frames = 1;

            int frameH = sprite.Height / frames;

            // Animate off position rather than a stored counter: a walking
            // villager cycles, a still one holds a pose, and the ghosts synced
            // from the server carry no animation state of their own.
            int frame = (int)(x / 7f) % frames;
            if (frame < 0)
                frame += frames;

            var src = new Rectangle(0, frame * frameH, sprite.Width, frameH);

            // Fit the sprite to the body the simulation actually uses, so the
            // drawing and the collision agree about how big a villager is.
            float scale = (Villager.Height + 8f) / frameH;

            Main.spriteBatch.Draw(
                sprite,
                new Vector2(screenX, screenY),
                src,
                light,
                0f,
                new Vector2(sprite.Width * 0.5f, frameH * 0.5f),
                scale,
                facingRight ? SpriteEffects.None : SpriteEffects.FlipHorizontally,
                0f);
        }

        // Tribe badge. Small, over the head, full strength so it stays legible
        // in the dark: this is the only thing that says which tribe this is.
        var badge = new Rectangle(
            (int)(screenX - 2f),
            (int)(screenY - Villager.Height * 0.5f - 7f),
            4, 3);

        Main.spriteBatch.Draw(pixel, badge, Multiply(body, light));

        if (soldier)
            Main.spriteBatch.Draw(pixel,
                new Rectangle(badge.X - 2, badge.Y - 3, 8, 2),
                Multiply(new Color(230, 220, 160), light));
    }

    private static Color Multiply(Color c, Color light) =>
        new Color(c.R * light.R / 255, c.G * light.G / 255, c.B * light.B / 255);

    /// <summary>
    /// The vanilla townsfolk texture, loaded on demand.
    ///
    /// Asking for a texture that has not been loaded yet throws on the drawing
    /// thread, so this loads it once and hands back null until it is ready
    /// rather than taking the frame down.
    /// </summary>
    private static Texture2D FolkTexture(int npcId)
    {
        try
        {
            if (TextureAssets.Npc == null || npcId < 0 || npcId >= TextureAssets.Npc.Length)
                return null;

            if (TextureAssets.Npc[npcId] == null || !TextureAssets.Npc[npcId].IsLoaded)
            {
                Main.instance.LoadNPC(npcId);
                return null;
            }

            return TextureAssets.Npc[npcId].Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Put the truth on the player's screen.
    ///
    /// "I see nobody" has three completely different causes and my logs could
    /// not tell them apart: the server sending nothing, packets not arriving,
    /// or villagers arriving and failing to draw. I asserted the third could
    /// not be happening without ever having seen a villager render on a client
    /// even once. This says which it is, in the one place that matters.
    /// </summary>
    private void DrawDiagnostic()
    {
        if (Main.netMode != NetmodeID.MultiplayerClient)
            return;

        int held;
        lock (VillagerSync.Ghosts)
            held = VillagerSync.Ghosts.Count;

        string text =
            $"ANTFARM  packets={VillagerSync.PacketsReceived}  " +
            $"lastPacket={VillagerSync.LastReceivedCount} villagers  " +
            $"held={held}  drawnThisFrame={_drawnLastFrame}  " +
            $"tribesKnown={Tribes.Count}";

        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            Main.DefaultSamplerState, DepthStencilState.None, Main.Rasterizer, null, Matrix.Identity);

        Terraria.Utils.DrawBorderString(Main.spriteBatch, text, new Vector2(12f, 90f),
            held > 0 ? Color.LightGreen : Color.Orange);

        Main.spriteBatch.End();
    }

    private int _drawnLastFrame;

    public override void PostDrawTiles()
    {
        // A client has no local colony, so _sim is always null there. Gating on
        // it meant a joined player could never see a villager even once the
        // server started sending them.
        if (Main.dedServ)
            return;

        if (AntfarmConfig.Instance is { DrawVillagers: false })
            return;

        if (_sim == null && Main.netMode != NetmodeID.MultiplayerClient)
            return;

        Texture2D pixel = TextureAssets.MagicPixel.Value;

        Main.spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            Main.DefaultSamplerState,
            DepthStencilState.None,
            Main.Rasterizer,
            null,
            Main.GameViewMatrix.TransformationMatrix);

        // Only what is actually on screen. This is the whole reason a thousand
        // physically present villagers is affordable.
        float padding = 64f;
        float left = Main.screenPosition.X - padding;
        float top = Main.screenPosition.Y - padding;
        float right = left + Main.screenWidth + padding * 2f;
        float bottom = top + Main.screenHeight + padding * 2f;

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int drawn = 0;

            lock (VillagerSync.Ghosts)
            {
                double now = Main.gameTimeCache?.TotalGameTime.TotalSeconds ?? 0;

                foreach (GhostVillager g in VillagerSync.Ghosts)
                {
                    // Draw where it is between the last two reports, not where
                    // the last packet put it.
                    g.Sample(now, out float gx, out float gy);

                    if (gx < left || gx > right || gy < top || gy > bottom)
                        continue;

                    DrawOne(pixel, gx, gy, ColourOf(g.TribeId), g.TribeId, g.FacingRight, g.Soldier, g.Undead);
                    drawn++;
                }
            }

            _drawnLastFrame = drawn;
        }
        else
        {
            lock (Tribes)
            {
                foreach (Tribe tribe in Tribes)
                {
                    var body = new Color(tribe.ColorR, tribe.ColorG, tribe.ColorB);

                    foreach (Villager v in tribe.Villagers)
                    {
                        if (v.X < left || v.X > right || v.Y < top || v.Y > bottom)
                            continue;

                        DrawOne(pixel, v.X, v.Y, body, tribe.Id, v.FacingRight, v.Soldier, v.Undead);
                    }
                }
            }
        }

        Main.spriteBatch.End();

        DrawDiagnostic();
    }
}
