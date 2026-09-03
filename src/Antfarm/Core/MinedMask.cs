using System;
using System.IO;
using System.IO.Compression;

namespace Antfarm.Core;

/// <summary>
/// Who dug what: one byte per tile, 0 for untouched and tribe id + 1 otherwise.
///
/// The snapshot already knows which tiles are open, but natural caves are open
/// too, so a view built from it alone cannot tell a tribe's work from terrain
/// that was always there. This is what makes the observation map readable: you
/// watch ten coloured networks spread through grey rock.
///
/// Costs one byte per tile, so 5 MB on a small world and 20 MB on a large one.
/// Held in memory only and not saved: on a restart the colouring starts blank
/// and refills as they keep digging. Worth revisiting if the history turns out
/// to matter more than the twenty megabytes.
/// </summary>
public sealed class MinedMask
{
    private byte[] _by;
    private int _width;
    private int _height;

    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Write the mask beside the world.
    ///
    /// It is a byte per tile, 20 MB on a large world, but almost all zeros, so
    /// it gzips to a fraction of that. Keeping it only in memory meant every
    /// restart erased which tribe had dug what, the whole map went grey, and it
    /// re-coloured from scratch over the following hours. With twenty-odd
    /// restarts in an evening the territory view never looked stable.
    /// </summary>
    public void Save(string path)
    {
        if (_by == null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write(_width);
            bw.Write(_height);
            bw.Flush();

            using var gz = new GZipStream(fs, CompressionLevel.Fastest, true);
            gz.Write(_by, 0, _by.Length);
        }
        catch { /* a lost mask costs colour, never correctness */ }
    }

    /// <summary>Read it back. Silently does nothing if it is missing or the world changed size.</summary>
    public bool Load(string path, int width, int height)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (br.ReadInt32() != width || br.ReadInt32() != height)
                return false;

            var buf = new byte[(long)width * height];

            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < buf.Length)
                {
                    int n = gz.Read(buf, read, buf.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
            }

            _width = width;
            _height = height;
            _by = buf;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Rebuild(int width, int height)
    {
        _width = width;
        _height = height;
        _by = new byte[(long)width * height];
    }

    public void Set(int x, int y, int tribeId)
    {
        if (_by == null || x < 0 || y < 0 || x >= _width || y >= _height)
            return;

        _by[(long)x * _height + y] = (byte)(tribeId + 1);
    }

    /// <summary>0 when untouched, otherwise tribe id + 1.</summary>
    public byte Get(int x, int y)
    {
        if (_by == null || x < 0 || y < 0 || x >= _width || y >= _height)
            return 0;

        return _by[(long)x * _height + y];
    }
}
