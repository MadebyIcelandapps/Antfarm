using System.Collections.Generic;

namespace Antfarm.Core;

public enum EventKind : byte
{
    Colony,     // founded a settlement, grew, built something notable
    Strike,     // found something worth having
    Battle,     // fighting, losses, victories
    Tech,       // smelting, new materials unlocked
}

public readonly struct ColonyEvent
{
    public readonly long Id;
    public readonly EventKind Kind;
    public readonly int TribeId;
    public readonly string Text;

    public ColonyEvent(long id, EventKind kind, int tribeId, string text)
    {
        Id = id;
        Kind = kind;
        TribeId = tribeId;
        Text = text;
    }
}

/// <summary>
/// The colony's news feed.
///
/// Until now everything had to be inferred from counters moving. A tribe
/// striking gold, losing twelve villagers to a raid, or founding a town looked
/// identical from the outside: a number changed. This records the moments so
/// the site can show what actually happened.
///
/// Fixed size ring buffer, because this runs for months and an unbounded list
/// of every event since January is a memory leak with extra steps.
/// </summary>
public sealed class EventLog
{
    private readonly ColonyEvent[] _ring;
    private readonly object _lock = new();
    private long _nextId;
    private int _head;
    private int _count;

    public EventLog(int capacity = 400)
    {
        _ring = new ColonyEvent[capacity];
    }

    public void Add(EventKind kind, int tribeId, string text)
    {
        lock (_lock)
        {
            _ring[_head] = new ColonyEvent(_nextId++, kind, tribeId, text);
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length)
                _count++;
        }
    }

    /// <summary>Most recent first, newest <paramref name="max"/> entries.</summary>
    public List<ColonyEvent> Recent(int max)
    {
        var outp = new List<ColonyEvent>(max);

        lock (_lock)
        {
            for (int i = 0; i < _count && outp.Count < max; i++)
            {
                int idx = (_head - 1 - i + _ring.Length * 2) % _ring.Length;
                outp.Add(_ring[idx]);
            }
        }

        return outp;
    }
}
