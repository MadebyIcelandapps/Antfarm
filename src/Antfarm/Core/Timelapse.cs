using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Terraria.ModLoader;

namespace Antfarm.Core;

/// <summary>
/// A recording of the whole world, one frame every so often, for ever.
///
/// The point of running this for a year is being able to watch the year back.
/// A frame is the same byte picture the live map uses, gzipped, appended to a
/// single file. At about 30 KB a frame and one every fifteen minutes that is
/// roughly a gigabyte a year, on a box with thirty free.
///
/// Frames are stored already compressed and served that way too, so a scrub
/// through history costs the server no CPU at all: it seeks, reads, and writes
/// the bytes straight to the socket with Content-Encoding: gzip.
///
/// Capture runs on its own thread. Sampling a large world touches twenty
/// million tiles, which is far too much to do on the main thread; it would
/// stutter the game every quarter of an hour. The snapshot and mask are plain
/// arrays, so reading them from here is safe, and a torn read would at worst
/// misdraw one cell of one historical frame.
/// </summary>
public sealed class Timelapse
{
    private readonly AntfarmSystem _system;
    private readonly string _path;

    /// <summary>
    /// One line of statistics per frame, so scrubbing shows the numbers that
    /// were true then.
    ///
    /// The archive recorded only the map, so replaying last week's tunnels
    /// displayed this week's population underneath them. Half of the history
    /// was fiction.
    /// </summary>
    private string StatsPath => _path + ".stats";
    private readonly int _intervalSeconds;

    private readonly List<(long Offset, int Length, long Time)> _index = new();
    private readonly object _lock = new();

    private Thread _thread;
    private volatile bool _running;

    public int FrameCount { get { lock (_lock) return _index.Count; } }
    public int IntervalSeconds => _intervalSeconds;

    public long FirstTime { get { lock (_lock) return _index.Count > 0 ? _index[0].Time : 0; } }
    public long LastTime { get { lock (_lock) return _index.Count > 0 ? _index[^1].Time : 0; } }

    public Timelapse(AntfarmSystem system, string path, int intervalSeconds)
    {
        _system = system;
        _path = path;
        _intervalSeconds = Math.Max(60, intervalSeconds);
    }

    public void Start()
    {
        if (_running)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            ReadIndex();
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<Antfarm>()?.Logger.Warn("antfarm: timelapse index unreadable: " + ex.Message);
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { Name = "Antfarm timelapse", IsBackground = true, Priority = ThreadPriority.Lowest };
        _thread.Start();

        ModContent.GetInstance<Antfarm>()?.Logger.Info(
            $"antfarm: timelapse recording to {_path}, {_index.Count} existing frames, " +
            $"one every {_intervalSeconds}s");
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
    }

    /// <summary>
    /// Walk the file once at startup to learn where every frame begins. Cheap
    /// even with tens of thousands of frames, because it only reads the twelve
    /// byte record headers and seeks over the payloads.
    /// </summary>
    private void ReadIndex()
    {
        lock (_lock)
        {
            _index.Clear();

            if (!File.Exists(_path))
                return;

            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            while (fs.Position + 12 <= fs.Length)
            {
                int len = br.ReadInt32();
                long time = br.ReadInt64();

                if (len <= 0 || fs.Position + len > fs.Length)
                    break;                       // truncated tail, most likely a kill mid-write

                _index.Add((fs.Position, len, time));
                fs.Position += len;
            }
        }
    }

    private void Loop()
    {
        // One frame immediately, so a fresh world has a "before" picture.
        Capture();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        double next = _intervalSeconds;

        while (_running)
        {
            if (clock.Elapsed.TotalSeconds < next)
            {
                Thread.Sleep(500);
                continue;
            }

            next = clock.Elapsed.TotalSeconds + _intervalSeconds;

            try
            {
                Capture();
            }
            catch (Exception ex)
            {
                ModContent.GetInstance<Antfarm>()?.Logger.Warn("antfarm: timelapse capture failed: " + ex.Message);
            }
        }
    }

    private void Capture()
    {
        if (_system.Snapshot.Width <= 0)
            return;

        byte[] raw = MapRenderer.Build(_system.Snapshot, _system.Mask, 0, 0, 0, 0);

        byte[] packed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                gz.Write(raw, 0, raw.Length);
            packed = ms.ToArray();
        }

        string stats = SnapshotStats();

        lock (_lock)
        {
            File.AppendAllText(StatsPath, stats + Environment.NewLine);

            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs);

            long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bw.Write(packed.Length);
            bw.Write(time);
            bw.Write(packed);
            bw.Flush();

            long offset = fs.Position - packed.Length;
            _index.Add((offset, packed.Length, time));
        }
    }

    /// <summary>The gzipped frame bytes, ready to go straight to the socket.</summary>
    public byte[] Frame(int i)
    {
        long offset;
        int length;

        lock (_lock)
        {
            if (i < 0 || i >= _index.Count)
                return null;

            offset = _index[i].Offset;
            length = _index[i].Length;
        }

        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = offset;

            var buf = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = fs.Read(buf, read, length - read);
                if (n <= 0)
                    return null;
                read += n;
            }

            return buf;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The recorded statistics for a frame, or null if there are none.</summary>
    public string StatsFor(int i)
    {
        try
        {
            if (i < 0 || !File.Exists(StatsPath))
                return null;

            // Frames are appended in order, so line i belongs to frame i.
            int line = 0;
            foreach (string text in File.ReadLines(StatsPath))
            {
                if (line++ == i)
                    return text;
            }
        }
        catch { }

        return null;
    }

    /// <summary>A compact record of how things stood when this frame was taken.</summary>
    private string SnapshotStats()
    {
        var sb = new System.Text.StringBuilder(512);
        sb.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        long pop = 0, mined = 0, built = 0, stored = 0, kills = 0, lost = 0;
        var names = new List<string>();

        lock (_system.Tribes)
        {
            foreach (Sim.Tribe t in _system.Tribes)
            {
                pop += t.Villagers.Count;
                mined += t.TilesMined;
                built += t.BuiltTiles;
                stored += t.ItemsStored;
                kills += t.Kills;
                lost += t.Losses;
                names.Add($"{{\"n\":\"{t.Name}\",\"p\":{t.Villagers.Count},\"m\":{t.TilesMined},\"b\":{t.BuiltTiles}}}");
            }
        }

        sb.Append(",\"pop\":").Append(pop)
          .Append(",\"mined\":").Append(mined)
          .Append(",\"built\":").Append(built)
          .Append(",\"stored\":").Append(stored)
          .Append(",\"kills\":").Append(kills)
          .Append(",\"lost\":").Append(lost)
          .Append(",\"tribes\":[").Append(string.Join(",", names)).Append("]}");

        return sb.ToString();
    }

    public long TimeOf(int i)
    {
        lock (_lock)
            return i >= 0 && i < _index.Count ? _index[i].Time : 0;
    }
}
