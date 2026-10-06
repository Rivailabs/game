using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AstraKingdoms.Server.Storage;

/// <summary>
/// Writes per-round match checkpoints in the background so command handling never waits for the
/// single SQLite writer. Only the newest snapshot of a match is kept (older ones are coalesced).
/// <para>
/// Writes that must be durable before the service continues (creation, suspension, settlement) go
/// through <see cref="WriteNow"/>, which first discards the match's pending checkpoint and waits for
/// an in-flight one, so an older checkpoint can never overwrite a newer direct write.
/// </para>
/// </summary>
public sealed class CheckpointWriter : IDisposable
{
    private readonly IMatchRepository _repo;
    private readonly ILogger<CheckpointWriter> _log;
    private readonly ConcurrentDictionary<string, StoredMatch> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly Channel<string> _signal = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;

    public CheckpointWriter(IMatchRepository repo, ILogger<CheckpointWriter> log)
    {
        _repo = repo;
        _log = log;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Queues a snapshot (a copy taken by the caller); a later one for the same match replaces it.</summary>
    public void Enqueue(StoredMatch snapshot)
    {
        _pending[snapshot.MatchId] = snapshot;
        _signal.Writer.TryWrite(snapshot.MatchId);
    }

    /// <summary>Writes synchronously after cancelling any queued or in-flight checkpoint of the match.</summary>
    public void WriteNow(StoredMatch match, Action<StoredMatch> write)
    {
        lock (LockFor(match.MatchId))
        {
            _pending.TryRemove(match.MatchId, out _);
            write(match);
        }
    }

    /// <summary>Number of checkpoints not yet written (readiness and tests).</summary>
    public int Backlog => _pending.Count;

    private object LockFor(string matchId) => _locks.GetOrAdd(matchId, _ => new object());

    private async Task RunAsync()
    {
        await foreach (string id in _signal.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (LockFor(id))
            {
                if (!_pending.TryRemove(id, out StoredMatch snapshot)) continue;
                try
                {
                    _repo.Save(snapshot);
                }
                catch (Exception e)
                {
                    // A lost checkpoint is recoverable (the next round or settlement writes again); log and continue.
                    _log.LogError("Checkpoint of match {MatchId} failed: {Error}", id, e.Message);
                }
            }
        }
    }

    /// <summary>Writes whatever is still queued (shutdown).</summary>
    public void Flush()
    {
        foreach (string id in _pending.Keys.ToList())
        {
            lock (LockFor(id))
            {
                if (_pending.TryRemove(id, out StoredMatch snapshot)) _repo.Save(snapshot);
            }
        }
    }

    public void Dispose()
    {
        _signal.Writer.TryComplete();
        Flush();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }
}
