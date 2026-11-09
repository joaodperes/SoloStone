using System;
using System.Collections.Concurrent;
using System.Threading;

// One process-wide disk thread. Producers only copy/enqueue; no disk work holds a producer lock.
internal sealed class BackgroundDiskWriter
{
    internal const int DefaultCapacity = 256;
    internal const int ShutdownMilliseconds = 250;
    private const int MaximumWriters = 32;
    private const int MaximumPendingBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<int, BackgroundDiskWriter> Writers = new ConcurrentDictionary<int, BackgroundDiskWriter>();
    private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
    private static int nextId, writerCount;
    private static readonly Thread Worker = StartWorker();
    private readonly ConcurrentQueue<Work> queue = new ConcurrentQueue<Work>();
    private readonly Action<string> finish;
    private readonly Action<Exception> failure;
    private readonly int id, capacity;
    private int pending, pendingBytes;
    private long submitted, completed;
    private volatile bool closing, finished;
    private volatile string stopReason;
    internal Exception Error { get; private set; }
    internal bool Accepting => !closing;
    internal string StopReason => stopReason;
    private sealed class Work { internal Action Write; internal int Bytes; }

    internal BackgroundDiskWriter(Action<string> finish, Action<Exception> failure, int capacity = DefaultCapacity)
    {
        this.finish = finish; this.failure = failure; this.capacity = capacity;
        if (Interlocked.Increment(ref writerCount) > MaximumWriters)
        {
            Interlocked.Decrement(ref writerCount);
            throw new InvalidOperationException("Too many pending disk writers.");
        }
        id = Interlocked.Increment(ref nextId);
        Writers.TryAdd(id, this);
    }
    private static Thread StartWorker()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Match journal/FX disk writer" };
        thread.Start(); return thread;
    }
    private static void Run()
    {
        while (true)
        {
            Wake.WaitOne();
            bool worked;
            do
            {
                worked = false;
                foreach (var writer in Writers.Values) worked |= writer.DrainOne();
            } while (worked);
        }
    }
    // A sink has one producer (Unity). Capacity includes its in-flight write, even on a stalled disk.
    internal bool TryEnqueue(Action write, int bytes = 0)
    {
        int count = Interlocked.Increment(ref pending);
        if (closing) { Interlocked.Decrement(ref pending); Wake.Set(); return false; }
        int total = Interlocked.Add(ref pendingBytes, bytes);
        if (count > capacity || total > MaximumPendingBytes)
        {
            Interlocked.Add(ref pendingBytes, -bytes);
            Interlocked.Decrement(ref pending);
            Complete("queue-overflow");
            return false;
        }
        Interlocked.Increment(ref submitted);
        queue.Enqueue(new Work { Write = write, Bytes = bytes });
        Wake.Set(); return true;
    }
    // Optional diagnostics (match event rows): never at the price of the writer. Refuses quietly when the queue is more than half
    // full or pending bytes are high, instead of overflowing and closing the whole sink like TryEnqueue.
    internal bool TryEnqueueOptional(Action write, int bytes = 0)
    {
        if (closing || Volatile.Read(ref pending) >= capacity / 2 || Volatile.Read(ref pendingBytes) + bytes > MaximumPendingBytes / 2) return false;
        return TryEnqueue(write, bytes);
    }
    internal void Complete(string reason = null)
    {
        if (reason != null) Interlocked.CompareExchange(ref stopReason, reason, null);
        closing = true; Wake.Set();
    }
    internal bool WaitForIdle(int milliseconds = ShutdownMilliseconds)
    {
        long target = Interlocked.Read(ref submitted);
        return SpinWait.SpinUntil(() => finished || (Interlocked.Read(ref completed) >= target && !closing), milliseconds);
    }
    internal bool WaitForClose(int milliseconds = ShutdownMilliseconds) => SpinWait.SpinUntil(() => finished, milliseconds);
    private bool DrainOne()
    {
        bool worked = queue.TryDequeue(out var work);
        if (worked)
        {
            try { if (Error == null) work.Write(); }
            catch (Exception error)
            {
                Error = error; closing = true;
                try { failure?.Invoke(error); } catch { }
            }
            finally
            {
                Interlocked.Add(ref pendingBytes, -work.Bytes);
                Interlocked.Decrement(ref pending);
                Interlocked.Increment(ref completed);
            }
        }
        if (closing && Volatile.Read(ref pending) == 0 && !finished)
        {
            try { finish?.Invoke(stopReason); }
            catch (Exception error)
            {
                Error = error;
                try { failure?.Invoke(error); } catch { }
            }
            finally
            {
                Writers.TryRemove(id, out _);
                Interlocked.Decrement(ref writerCount);
                finished = true;
            }
        }
        return worked;
    }
}
