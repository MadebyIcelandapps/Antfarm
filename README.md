# Antfarm

A tModLoader mod. Ten rival tribes are seeded into a world and never stop.

They mine, haul, smelt ore into bars, arm soldiers, build lit halls and towers,
found outposts, fight monsters, fight each other, die, and get replaced by
children who inherited their parents' traits. They keep doing it while you play,
while you are alt-tabbed, and on a dedicated server with nobody logged in at all.

It is meant to be left running. Over hours the world becomes a honeycomb of
tunnels; over days it becomes ten distinguishable civilisations.

![status](https://img.shields.io/badge/tModLoader-1.4.4-blue)

---

## What it actually does

**Villagers are not vanilla NPCs.** Terraria allocates a fixed array of 200 NPC
slots shared with every slime and boss in the world, which would give ten tribes
fifteen villagers each. These are a custom lightweight entity instead: roughly
two percent of the cost of an NPC, no cap, and simulated on their own thread.
Worlds with 12,000 of them run fine.

**The colony has its own heartbeat.** Terraria throttles its update rate when
the window loses focus, so a simulation living in the game loop would crawl the
moment you alt-tab. The colony runs on a dedicated thread with its own clock. It
never touches `Main.tile` (which is not thread safe); it reads a packed bitmap
of the world and emits tile operations that the main thread applies under a
budget. When the window is unfocused the budget goes *up*, because there is no
rendering to pay for.

**A dedicated server keeps working with nobody online.** Terraria's server loop
only updates the world when a client is connected, so an empty server is frozen.
Antfarm drives the skipped update itself, so the world genuinely runs 24/7.

**Villagers evolve.** Five heritable genes (vigour, capacity, toughness,
boldness, wander) drive dig speed, pack size, health and nerve. A newborn copies
a living parent with a small mutation. Selection needs no extra machinery:
villagers who die young do not become parents, so whatever kept the survivors
alive spreads. Gene averages are saved, so drift accumulates across restarts.

**Storage follows the work.** Haulers deliver to the nearest chest, and when
there is none within 60 tiles the tribe opens a cache where the villager stands.
Chests propagate along the dig front, so the round trip stays short however deep
the colony goes, and each cache seeds an underground hall around itself.

---

## Requirements

- **tModLoader 1.4.4** (tested against `v2026.07.3.0`)
- Terraria, obviously
- For a dedicated server: .NET 8 runtime

---

## Quick start (Windows)

Double-click **`launcher/Play Antfarm.bat`**.

It finds tModLoader through Steam, starts a local server, waits for the colony
to come up, **opens the live view panel in your browser**, and joins you to the
world. Leave the panel open: it keeps working whether or not Terraria is
running.

It will tell you what to do if tModLoader is missing or the mod is not built
yet. First run generates a world, which takes a few minutes.

---

## Installing

### From source

1. Clone into tModLoader's `ModSources` folder:

   ```bash
   git clone https://github.com/<you>/terraria-antfarm.git
   ```

   Copy or symlink `src/Antfarm` into
   `<tModLoader save directory>/ModSources/Antfarm`.

2. Launch tModLoader, go to **Workshop → Develop Mods**, and build Antfarm.

Or build headlessly:

```bash
dotnet tModLoader.dll -server -build "<path>/ModSources/Antfarm" -savedirectory "<save dir>"
```

Run that from the tModLoader install directory. It produces `Antfarm.tmod` in
`<save dir>/tModLoader/Mods/`.

### Enabling

Enable it in the mod list, or write `["Antfarm"]` into
`<save dir>/tModLoader/Mods/enabled.json`.

---

## Running a dedicated server

```bash
dotnet tModLoader.dll -server \
  -savedirectory /opt/antfarm/saves \
  -world /opt/antfarm/saves/tModLoader/Worlds/Antfarm.wld \
  -autocreate 3 -worldname Antfarm -difficulty 0 \
  -players 8 -port 7777 -noupnp
```

A systemd unit, if you want it to survive reboots. The memory ceiling and low
priority matter if anything else shares the box:

```ini
[Unit]
Description=Antfarm
After=network-online.target

[Service]
WorkingDirectory=/opt/antfarm/tml
ExecStart=/opt/antfarm/dotnet/dotnet /opt/antfarm/tml/tModLoader.dll -server \
  -savedirectory /opt/antfarm/saves \
  -world /opt/antfarm/saves/tModLoader/Worlds/Antfarm.wld \
  -autocreate 3 -worldname Antfarm -players 8 -port 7777 -noupnp
StandardInput=null
Restart=always
RestartSec=15
MemoryMax=2G
Nice=10
CPUWeight=20

[Install]
WantedBy=multi-user.target
```

---

## The observation panel

The mod serves a live map on **`http://localhost:7778/`**, so you can watch the
world without Terraria open at all.

- Whole world as one image: grey untouched rock, dark air and caves, each
  tribe's excavation in its own colour
- Scroll to zoom, drag to pan. Zooming requests a smaller region from the
  server, so it shows real extra detail down to individual tiles
- Live per-tribe table: population, roles, materials, gene averages, haul
  distance
- Event feed: ore strikes, caches opened, settlements founded, deaths, battles
- Hall of fame: the villagers with the most blocks dug, living and dead
- **Timelapse**: a whole-world frame every 15 minutes, with a scrubber and a
  play button. Playback samples down to ~300 frames so a year plays in about
  forty seconds. Roughly 30 KB a frame, about 1 GB a year

It binds **loopback only**, so it is never exposed to the internet by itself.
To publish it, put a reverse proxy in front. A Cloudflare Tunnel ingress rule
is two lines:

```yaml
ingress:
  - hostname: antfarm.example.com
    service: http://127.0.0.1:7778
```

### Endpoints

| Path | Returns |
|---|---|
| `/` | the page |
| `/stats` | per-tribe JSON |
| `/events` | recent events |
| `/legends` | hall of fame |
| `/map.bin?x&y&w&h` | map region, one byte per cell, gzipped |
| `/timelapse` | frame count and interval |
| `/timelapse/frame?i=N` | one historical frame |

---

## Commands

Both are server-side, so they work when you are connected to a dedicated server.

| Command | Effect |
|---|---|
| `/tribes` | every tribe, its population, and the coordinates and distance of its nearest villager |
| `/goto [tribe]` | teleport to the nearest villager, or to a named tribe |

An 8,400 tile world is large enough that finding a colony by walking is not
realistic, hence `/goto`.

---

## Configuration

In **Settings → Mod Configuration → Antfarm**:

| Setting | Default | Notes |
|---|---|---|
| Number of tribes | 10 | |
| Villagers per tribe | 40 | starting population; they grow from there |
| Tile changes per tick | 96 | the most important number. Terraria applies tile, light and liquid updates on the main thread, so this is what protects your framerate |
| Background speed multiplier | 8 | applied while the window is unfocused |
| Simulation rate | 60 Hz | how often the colony thinks, independent of framerate |
| Keep building with nobody online | on | dedicated servers |
| Observation window | on, port 7778 | |
| Timelapse | on, every 15 min | |

---

## Players are spectators

Everyone on the server is immortal and can fly (hold jump). The tribes quarry
the surface into pits and shafts that are genuinely impossible to walk across,
and dying to a fall while trying to look at a tower is not the game.

---

## Known limitations

- **The world is finite.** 20 million tiles, and a large colony can honeycomb a
  whole world in a couple of days. Late-game behaviour for a full world
  (demolition, rebuilding, fighting over what is left) is not written yet.
- **Settlements are founded at a point and outposts follow the workforce, but
  the original capital never moves.** Chest anchoring makes this survivable;
  it is not solved.
- **No building has been observed completing all five construction phases.**
  Blocks are placed and phases advance; the final fit-out is unproven.
- Villagers render as coloured rectangles rather than sprites.
- Tribe-vs-tribe combat is implemented but only fires once two tribes' tunnels
  actually meet, which takes hours.

---

## How it is put together

```
Core/
  AntfarmSystem   the only thing touching Terraria's main thread
  SimThread       the colony's own clock, 60 Hz, independent of framerate
  TileSnapshot    packed bitmap of the world; what the colony reads
  TileOp          a requested tile change, applied under budget
  MapRenderer     world to bytes, shared by the live map and the recorder
  WebObserver     raw socket HTTP server on loopback
  Timelapse       append-only gzipped frame archive
  VillagerSync    streams nearby villagers to connected players
  HeadlessTicker  drives the world update an empty server skips
Sim/
  Tribe           settlements, stockpiles, roles, construction, expansion
  Villager        one individual: physics, jobs, genes, health
  Genes           what a villager inherits
  Building        a structure built in dependency order
  Architect       what a room is made of
```

The rule the whole thing hangs on: **the colony thread only ever reads the
snapshot and writes to the operation queue.** It never touches `Main.tile`,
`Main.chest` or `Main.rand`. Everything that must touch Terraria happens on the
main thread, under a budget.

---

## Licence

MIT.
