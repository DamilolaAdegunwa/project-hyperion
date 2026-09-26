namespace Hyperion.Storage;

public interface IReplacer
{
    int Size { get; }
    bool Victim(out int frameId);
    void Pin(int frameId);
    void Unpin(int frameId);
}

/// <summary>
/// Clock-based page replacement policy (Second-Chance algorithm).
/// </summary>
public sealed class ClockReplacer : IReplacer
{
    private readonly int _capacity;
    private readonly bool[] _inReplacer;
    private readonly bool[] _refBit;
    private int _clockHand;
    private int _size;
    private readonly object _lock = new();

    public ClockReplacer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");

        _capacity = capacity;
        _inReplacer = new bool[capacity];
        _refBit = new bool[capacity];
        _clockHand = 0;
        _size = 0;
    }

    public int Size
    {
        get
        {
            lock (_lock)
            {
                return _size;
            }
        }
    }

    public bool Victim(out int frameId)
    {
        lock (_lock)
        {
            frameId = -1;
            if (_size == 0)
                return false;

            int scans = 0;
            while (scans < _capacity * 2)
            {
                int current = _clockHand;
                _clockHand = (_clockHand + 1) % _capacity;
                scans++;

                if (!_inReplacer[current])
                    continue;

                if (_refBit[current])
                {
                    _refBit[current] = false;
                }
                else
                {
                    _inReplacer[current] = false;
                    _size--;
                    frameId = current;
                    return true;
                }
            }

            return false;
        }
    }

    public void Pin(int frameId)
    {
        lock (_lock)
        {
            if (frameId < 0 || frameId >= _capacity)
                return;

            if (_inReplacer[frameId])
            {
                _inReplacer[frameId] = false;
                _refBit[frameId] = false;
                _size--;
            }
        }
    }

    public void Unpin(int frameId)
    {
        lock (_lock)
        {
            if (frameId < 0 || frameId >= _capacity)
                return;

            if (!_inReplacer[frameId])
            {
                _inReplacer[frameId] = true;
                _refBit[frameId] = true;
                _size++;
            }
        }
    }
}
