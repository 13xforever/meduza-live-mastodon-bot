using System.Collections;

namespace MeduzaRepost;

public class TimeLimitedQueue: IList<DateTime>
{
    private readonly Queue<DateTime> queue = new();

    private void CleanQueue()
    {
        var cutoffTimestamp = DateTime.UtcNow - Config.PublicLimiterTimeSpan;
        while (queue.TryPeek(out var dt) && dt < cutoffTimestamp)
            queue.Dequeue();
    }

    public IEnumerator<DateTime> GetEnumerator()
    {
        lock (queue)
        {
            CleanQueue();
            return queue.GetEnumerator();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICollection<DateTime>.Add(DateTime item)
    {
        if (!TryAdd(item))
            throw new InvalidOperationException("Operation count per time slot exceeded the limit");
    }

    public bool TryAdd(DateTime item)
    {
        lock (queue)
        {
            CleanQueue();
            if (queue.Count < Config.PublicLimiterItemCount)
            {
                queue.Enqueue(item);
                return true;
            }
            Config.Log.Warn("Throttled post queue");
            return false;
        }
    }

    public void Clear()
    {
        lock (queue)
            queue.Clear();
    }

    public bool Contains(DateTime item)
    {
        throw new NotImplementedException();
    }

    public void CopyTo(DateTime[] array, int arrayIndex)
    {
        throw new NotImplementedException();
    }

    public bool Remove(DateTime item)
    {
        throw new NotImplementedException();
    }

    public int Count
    {
        get
        {
            lock (queue)
                return queue.Count;
        }
    }

    public bool IsReadOnly => false;
    
    public int IndexOf(DateTime item)
    {
        throw new NotImplementedException();
    }

    public void Insert(int index, DateTime item)
    {
        throw new NotImplementedException();
    }

    public void RemoveAt(int index)
    {
        throw new NotImplementedException();
    }

    public DateTime this[int index]
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }
}