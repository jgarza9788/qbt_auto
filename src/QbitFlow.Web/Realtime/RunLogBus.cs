using System.Collections.Concurrent;
using System.Threading.Channels;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Web.Realtime;

/// <summary>
/// In-process fan-out of run log lines: the pipeline runner publishes here as it works, the SSE
/// endpoint subscribes. Buffered so a client that connects mid-run still sees the tail.
/// </summary>
public sealed class RunLogBus : IRunLogPublisher
{
    private sealed class RunFeed
    {
        public readonly List<RunLogEntry> Buffer = [];
        public readonly List<Channel<RunLogEntry?>> Subscribers = [];
        public bool Completed;
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<Guid, RunFeed> _feeds = new();

    public void Publish(Guid runId, RunLogEntry entry)
    {
        var feed = _feeds.GetOrAdd(runId, _ => new RunFeed());
        lock (feed.Gate)
        {
            feed.Buffer.Add(entry);
            if (feed.Buffer.Count > 2000) feed.Buffer.RemoveRange(0, feed.Buffer.Count - 2000);
            foreach (var sub in feed.Subscribers) sub.Writer.TryWrite(entry);
        }
    }

    public void Complete(Guid runId)
    {
        if (!_feeds.TryGetValue(runId, out var feed)) return;
        lock (feed.Gate)
        {
            feed.Completed = true;
            foreach (var sub in feed.Subscribers) sub.Writer.TryWrite(null);   // sentinel
        }

        // let late subscribers still drain the buffer for a while, then drop
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => _feeds.TryRemove(runId, out RunFeed? _));
    }

    /// <summary>Replays the current buffer, then streams new lines until the run completes.</summary>
    public async IAsyncEnumerable<RunLogEntry> StreamAsync(
        Guid runId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var feed = _feeds.GetOrAdd(runId, _ => new RunFeed());
        var channel = Channel.CreateUnbounded<RunLogEntry?>();

        List<RunLogEntry> replay;
        bool alreadyDone;
        lock (feed.Gate)
        {
            replay = [.. feed.Buffer];
            alreadyDone = feed.Completed;
            feed.Subscribers.Add(channel);
        }

        try
        {
            foreach (var e in replay)
                yield return e;

            if (alreadyDone)
                yield break;

            await foreach (var e in channel.Reader.ReadAllAsync(ct))
            {
                if (e is null) yield break;   // completion sentinel
                yield return e;
            }
        }
        finally
        {
            lock (feed.Gate) feed.Subscribers.Remove(channel);
        }
    }
}
