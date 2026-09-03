using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Antfarm.Sim;
using Terraria.ModLoader;

namespace Antfarm.Core;

/// <summary>
/// A little web page you can leave open in Firefox and watch the world get
/// eaten, without Terraria being open at all.
///
/// It reads the same thread safe snapshot the colony brain reads, never the
/// game's tile array, so it can serve requests from its own thread while the
/// world runs. The map is downsampled to about a thousand pixels wide and sent
/// as one byte per cell, gzipped, and painted into a canvas by the page. That
/// avoids needing a PNG encoder or a graphics device, neither of which a
/// dedicated server has.
/// </summary>
public sealed class WebObserver
{
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Thread> _threads = new();
    private volatile bool _running;

    private readonly AntfarmSystem _system;
    private readonly int _port;

    public WebObserver(AntfarmSystem system, int port)
    {
        _system = system;
        _port = port;
    }

    public string Url => $"http://localhost:{_port}/";

    /// <summary>
    /// Listens on both loopback addresses and speaks HTTP directly.
    ///
    /// This used to be an HttpListener and it failed in two ways at once behind
    /// a reverse proxy. It bound IPv6 loopback only, so a proxy dialling
    /// 127.0.0.1 got connection refused, and its "localhost" prefix rejected
    /// every request whose Host header was the public hostname, answering 400.
    /// The wildcard prefix that would accept those needs administrator rights
    /// on Windows, which would break running this on a home PC.
    ///
    /// A raw socket has none of those problems: it binds exactly where told, it
    /// does not care what the Host header says, and it needs no privileges.
    /// Both loopbacks are bound so a browser on the same machine reaches it
    /// whether "localhost" resolves to ::1 or 127.0.0.1. Neither is reachable
    /// from outside the machine, so publishing it stays the proxy's decision.
    /// </summary>
    public void Start()
    {
        if (_running)
            return;

        _running = true;

        Bind(IPAddress.Loopback);
        Bind(IPAddress.IPv6Loopback);

        if (_listeners.Count == 0)
        {
            _running = false;
            ModContent.GetInstance<Antfarm>()?.Logger.Warn(
                $"antfarm: observation window could not bind port {_port} on either loopback");
            return;
        }

        ModContent.GetInstance<Antfarm>()?.Logger.Info(
            $"antfarm: observation window at {Url} ({_listeners.Count} loopback binding(s))");
    }

    private void Bind(IPAddress address)
    {
        TcpListener listener = null;

        try
        {
            listener = new TcpListener(address, _port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
        }
        catch (Exception ex)
        {
            // One family missing is fine as long as the other bound. A box with
            // IPv6 disabled is normal, and so is one with only IPv6 loopback.
            ModContent.GetInstance<Antfarm>()?.Logger.Info(
                $"antfarm: observer could not bind {address}:{_port} ({ex.Message})");
            try { listener?.Stop(); } catch { }
            return;
        }

        _listeners.Add(listener);

        var thread = new Thread(() => Loop(listener))
        {
            Name = $"Antfarm observer {address}",
            IsBackground = true,
        };

        _threads.Add(thread);
        thread.Start();
    }

    public void Stop()
    {
        _running = false;

        foreach (TcpListener l in _listeners)
        {
            try { l.Stop(); } catch { /* shutting down anyway */ }
        }

        _listeners.Clear();

        foreach (Thread t in _threads)
            t.Join(1000);

        _threads.Clear();
    }

    private void Loop(TcpListener listener)
    {
        while (_running)
        {
            TcpClient client;

            try
            {
                client = listener.AcceptTcpClient();
            }
            catch
            {
                if (!_running)
                    return;
                continue;
            }

            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 15000;
                    Handle(client.GetStream());
                }
                catch (Exception ex)
                {
                    ModContent.GetInstance<Antfarm>()?.Logger.Debug(
                        "antfarm: observer request failed: " + ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Read one request, answer it, close. No keep alive, because the page
    /// makes two small requests every two seconds and simplicity is worth more
    /// here than the handful of microseconds a persistent connection saves.
    /// </summary>
    private void Handle(NetworkStream stream)
    {
        string requestLine = null;
        bool gzip = false;

        // Request line, then headers until a blank line.
        for (int lineNo = 0; lineNo < 100; lineNo++)
        {
            string line = ReadLine(stream);

            if (line == null)
                return;

            if (lineNo == 0)
            {
                requestLine = line;
                continue;
            }

            if (line.Length == 0)
                break;

            if (line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                line.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                gzip = true;
        }

        if (requestLine == null)
            return;

        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            Send(stream, 405, "text/plain", Encoding.UTF8.GetBytes("method not allowed"), false);
            return;
        }

        string target = parts[1];
        string path = target;
        string query = "";

        int q = target.IndexOf('?');
        if (q >= 0)
        {
            path = target.Substring(0, q);
            query = target.Substring(q + 1);
        }

        Route(stream, path, query, gzip);
    }

    private static string ReadLine(NetworkStream stream)
    {
        var sb = new StringBuilder(128);

        while (sb.Length < 8192)
        {
            int b = stream.ReadByte();

            if (b < 0)
                return sb.Length > 0 ? sb.ToString() : null;

            if (b == '\n')
                return sb.ToString().TrimEnd('\r');

            sb.Append((char)b);
        }

        return sb.ToString();
    }

    private void Route(NetworkStream stream, string path, string query, bool gzip)
    {
        switch (path)
        {
            case "/":
            case "/index.html":
                Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ObserverPage.Html), gzip);
                break;

            case "/stats":
                Send(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildStats()), gzip);
                break;

            case "/events":
                Send(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildEvents()), gzip);
                break;

            case "/legends":
                Send(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildLegends()), gzip);
                break;

            case "/timelapse":
                Send(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildTimelapseInfo()), gzip);
                break;

            case "/timelapse/frame":
            {
                Timelapse tl = _system.Recorder;
                byte[] frame = tl?.Frame(ParseInt(Query(query, "i"), -1));

                if (frame == null)
                {
                    Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("no such frame"), false);
                    break;
                }

                // Frames are stored gzipped and shipped exactly as they sit on
                // disk. Scrubbing through a year of history therefore costs the
                // server a seek and a write, and no compression work at all.
                SendStoredGzip(stream, "application/octet-stream", frame);
                break;
            }

            case "/map.bin":
                Send(stream, 200, "application/octet-stream", BuildMap(
                    ParseInt(Query(query, "x"), 0),
                    ParseInt(Query(query, "y"), 0),
                    ParseInt(Query(query, "w"), 0),
                    ParseInt(Query(query, "h"), 0)), gzip);
                break;

            default:
                Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found"), false);
                break;
        }
    }

    private static string Query(string query, string key)
    {
        foreach (string pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair.Substring(0, eq) == key)
                return pair.Substring(eq + 1);
        }

        return null;
    }

    private static void Send(NetworkStream stream, int status, string contentType, byte[] payload, bool gzip)
    {
        string encoding = "";

        if (gzip && payload.Length > 1024)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
                gz.Write(payload, 0, payload.Length);

            payload = ms.ToArray();
            encoding = "Content-Encoding: gzip\r\n";
        }

        string head =
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Error")}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            encoding +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    // ------------------------------------------------------------------
    // Payloads
    // ------------------------------------------------------------------

    /// <summary>
    /// One byte per downsampled cell: 0 open, 1 solid, 2 + tribeId dug by that
    /// tribe. Tribe work takes priority in a cell so a one tile tunnel is still
    /// visible after downsampling, which is the whole point of the view.
    /// </summary>
    private static int ParseInt(string s, int fallback)
        => int.TryParse(s, out int v) ? v : fallback;

    /// <summary>
    /// Zooming asks for a smaller region, which lowers the downsample step and
    /// shows real extra detail rather than magnifying the same pixels. At full
    /// zoom the step reaches 1 and you are looking at individual tiles.
    /// </summary>
    private byte[] BuildMap(int regionX, int regionY, int regionW, int regionH)
        => MapRenderer.Build(_system.Snapshot, _system.Mask, regionX, regionY, regionW, regionH);

    private string BuildStats()
    {
        var sb = new StringBuilder(1024);
        sb.Append("{\"tribes\":[");

        List<Tribe> tribes = _system.Tribes;

        lock (tribes)
        {
            for (int i = 0; i < tribes.Count; i++)
            {
                Tribe t = tribes[i];

                if (i > 0)
                    sb.Append(',');

                sb.Append("{\"id\":").Append(t.Id)
                  .Append(",\"name\":\"").Append(Escape(t.Name)).Append('"')
                  .Append(",\"colour\":\"#")
                  .Append(t.ColorR.ToString("x2"))
                  .Append(t.ColorG.ToString("x2"))
                  .Append(t.ColorB.ToString("x2")).Append('"')
                  .Append(",\"x\":").Append(t.HomeX)
                  .Append(",\"y\":").Append(t.HomeY)
                  .Append(",\"reach\":").Append(t.Reach)
                  .Append(",\"settlements\":").Append(t.Settlements.Count)
                  .Append(",\"rooms\":").Append(t.Rooms)
                  .Append(",\"cap\":").Append(t.PopulationCap)
                  .Append(",\"stock\":").Append(t.BuildStockCount)
                  .Append(",\"built\":").Append(t.BuiltTiles)
                  .Append(",\"losses\":").Append(t.Losses)
                  .Append(",\"soldiers\":").Append(t.Soldiers)
                  .Append(",\"masons\":").Append(t.Masons)
                  .Append(",\"haul\":").Append(t.HaulDistance)
                  .Append(",\"births\":").Append(t.Births)
                  .Append(",\"genes\":\"")
                  .Append(t.GeneVigour).Append('/')
                  .Append(t.GeneCapacity).Append('/')
                  .Append(t.GeneToughness).Append('/')
                  .Append(t.GeneBoldness).Append('/')
                  .Append(t.GeneWander).Append('"')
                  .Append(",\"miners\":").Append(t.Miners)
                  .Append(",\"armed\":").Append(t.Armed)
                  .Append(",\"bars\":").Append(t.Bars)
                  .Append(",\"kills\":").Append(t.Kills)
                  .Append(",\"roads\":").Append(t.RoadsBuilt)
                  .Append(",\"torches\":").Append(t.TorchesPlaced)
                  .Append(",\"buildings\":").Append(t.BuildingsFinished)
                  .Append(",\"site\":\"").Append(Escape(t.BuildingStatus)).Append('"')
                  .Append(",\"trait\":\"").Append(TribeTraits.Describe(t.Trait)).Append('"')
                  .Append(",\"undead\":").Append(t.Undead ? "true" : "false")
                  .Append(",\"dead\":").Append(t.Dead.Count)
                  .Append(",\"threat\":").Append(t.ThreatActive ? "true" : "false")
                  .Append(",\"mined\":").Append(t.TilesMined)
                  .Append(",\"stored\":").Append(t.ItemsStored)
                  .Append(",\"hauling\":").Append(t.HaulingCount)
                  .Append(",\"deliveries\":").Append(t.Deliveries)
                  .Append(",\"chests\":").Append(t.Chests.Count)
                  .Append(",\"villagers\":").Append(t.Villagers.Count)
                  .Append('}');
            }
        }

        sb.Append("],\"headlessTicks\":").Append(HeadlessTicker.DrivenTicks);
        sb.Append(",\"queued\":").Append(_system.Ops.Count);
        sb.Append(",\"simTicks\":").Append(_system.SimTicks);
        sb.Append(",\"worldW\":").Append(_system.Snapshot.Width);
        sb.Append(",\"worldH\":").Append(_system.Snapshot.Height);
        sb.Append('}');

        return sb.ToString();
    }

    /// <summary>
    /// The hall of fame, living and dead together.
    ///
    /// This costs nothing to produce because every villager has been carrying
    /// its own record all along; it just had nowhere to be read. It is also
    /// where the naming work finally pays off, because "Halla of Ironvein,
    /// 4,300 blocks, died at depth 1,180" is the thing worth coming back for.
    /// </summary>
    private string BuildLegends()
    {
        var rows = new List<(string Name, string Tribe, string Colour, long Dug, int Kills, int Depth, bool Alive, bool Undead)>();

        List<Tribe> tribes = _system.Tribes;

        lock (tribes)
        {
            foreach (Tribe t in tribes)
            {
                string colour = "#" + t.ColorR.ToString("x2") + t.ColorG.ToString("x2") + t.ColorB.ToString("x2");

                foreach (Villager v in t.Villagers)
                    rows.Add((v.Name, t.Name, colour, v.TilesDug, v.Kills, v.DeepestY, true, v.Undead));

                lock (t.Dead)
                    foreach (Fallen f in t.Dead)
                        rows.Add((f.Name, t.Name, colour, f.TilesDug, f.Kills, f.DeepestY, false, false));
            }
        }

        rows.Sort((a, b) => b.Dug.CompareTo(a.Dug));

        var sb = new StringBuilder(2048);
        sb.Append("{\"legends\":[");

        int n = 0;
        foreach (var r in rows)
        {
            if (n >= 20)
                break;

            if (n > 0)
                sb.Append(',');

            sb.Append("{\"name\":\"").Append(Escape(r.Name)).Append('"')
              .Append(",\"tribe\":\"").Append(Escape(r.Tribe)).Append('"')
              .Append(",\"colour\":\"").Append(r.Colour).Append('"')
              .Append(",\"dug\":").Append(r.Dug)
              .Append(",\"kills\":").Append(r.Kills)
              .Append(",\"depth\":").Append(r.Depth)
              .Append(",\"alive\":").Append(r.Alive ? "true" : "false")
              .Append(",\"undead\":").Append(r.Undead ? "true" : "false")
              .Append('}');

            n++;
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private string BuildTimelapseInfo()
    {
        Timelapse tl = _system.Recorder;

        if (tl == null)
            return "{\"frames\":0}";

        return "{\"frames\":" + tl.FrameCount +
               ",\"intervalSec\":" + tl.IntervalSeconds +
               ",\"first\":" + tl.FirstTime +
               ",\"last\":" + tl.LastTime + "}";
    }

    /// <summary>Write bytes that are already gzip, telling the client so.</summary>
    private static void SendStoredGzip(NetworkStream stream, string contentType, byte[] payload)
    {
        string head =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Content-Encoding: gzip\r\n" +
            "Cache-Control: public, max-age=31536000, immutable\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private string BuildEvents()
    {
        var sb = new StringBuilder(2048);
        sb.Append("{\"events\":[");

        List<ColonyEvent> events = _system.Events.Recent(60);

        for (int i = 0; i < events.Count; i++)
        {
            ColonyEvent e = events[i];

            if (i > 0)
                sb.Append(',');

            sb.Append("{\"id\":").Append(e.Id)
              .Append(",\"kind\":\"").Append(e.Kind.ToString().ToLowerInvariant()).Append('"')
              .Append(",\"tribe\":").Append(e.TribeId)
              .Append(",\"text\":\"").Append(Escape(e.Text)).Append('"')
              .Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static string Escape(string s)
        => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
