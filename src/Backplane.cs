namespace Trysac.SignalrPresenceHub;

/// <summary>Payload for a single delta operation (<c>connect</c>, <c>disconnect</c>, <c>join</c>, or <c>leave</c>).</summary>
/// <param name="Operation">Operation kind: <c>connect</c>, <c>disconnect</c>, <c>join</c>, or <c>leave</c>.</param>
/// <param name="ConnectionId">Target connection identifier.</param>
/// <param name="UserId">User identifier for <c>connect</c>; empty string for other operations.</param>
/// <param name="Room">Room name for <c>join</c> and <c>leave</c>; empty string for other operations.</param>
public sealed record DeltaPayload(
    string Operation,
    string ConnectionId,
    string UserId,
    string Room);

/// <summary>Immutable snapshot of a single connection and its joined rooms.</summary>
/// <param name="ConnectionId">Connection identifier.</param>
/// <param name="UserId">User identifier owning the connection.</param>
/// <param name="Rooms">Ordinal-sorted list of rooms joined by this connection.</param>
public sealed record ConnectionSnapshot(
    string ConnectionId,
    string UserId,
    IReadOnlyList<string> Rooms);

/// <summary>Payload for a <c>snapshot</c> message containing all connections owned by the sending instance.</summary>
/// <param name="Connections">Active connections on the sending instance.</param>
public sealed record SnapshotPayload(
    IReadOnlyList<ConnectionSnapshot> Connections);

/// <summary>Payload for a <c>sync_request</c> message.</summary>
/// <param name="TargetInstanceId">Optional target instance identifier, or <c>null</c> to request snapshots from all instances.</param>
public sealed record SyncRequestPayload(
    string? TargetInstanceId);

/// <summary>Envelope published across an <see cref="IBackplane"/> between <see cref="PresenceNode"/> instances.</summary>
/// <param name="Kind">Message kind: <c>delta</c>, <c>heartbeat</c>, <c>sync_request</c>, or <c>snapshot</c>.</param>
/// <param name="InstanceId">Identifier of the sending <see cref="PresenceNode"/> instance.</param>
/// <param name="Sequence">Monotonically increasing sequence number of the sender's deltas.</param>
/// <param name="Payload">Message-specific payload (<see cref="DeltaPayload"/>, <see cref="SnapshotPayload"/>, <see cref="SyncRequestPayload"/>, or <c>null</c>).</param>
public sealed record BackplaneMessage(
    string Kind,
    string InstanceId,
    long Sequence,
    object? Payload = null);

/// <summary>Pluggable pub/sub transport used by <see cref="PresenceNode"/> instances to exchange messages.</summary>
public interface IBackplane
{
    /// <summary>Publishes a message to subscribers on the backplane.</summary>
    /// <param name="message">The message to publish.</param>
    void Publish(BackplaneMessage message);

    /// <summary>Registers a callback to receive published messages.</summary>
    /// <param name="handler">Callback invoked for each delivered message.</param>
    /// <returns>A disposable subscription token that unregisters the handler when disposed.</returns>
    IDisposable Subscribe(Action<BackplaneMessage> handler);
}

/// <summary>In-memory synchronous <see cref="IBackplane"/> implementation with deterministic fault-injection hooks for tests.</summary>
public sealed class InMemoryBackplane : IBackplane
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Queue<(BackplaneMessage Message, int? SenderSubscriptionId)> _heldMessages = [];
    private readonly List<BackplaneMessage> _publishedLog = [];
    private readonly AsyncLocal<int?> _activeSubscriberId = new();
    private int _nextSubscriptionId = 1;
    private bool _duplicateNext;
    private bool _dropNext;
    private bool _holding;

    /// <summary>Gets a snapshot of all messages passed to <see cref="Publish"/> in publish order (for testing and diagnostics).</summary>
    public IReadOnlyList<BackplaneMessage> PublishedMessages
    {
        get
        {
            lock (_gate)
            {
                return [.. _publishedLog];
            }
        }
    }

    /// <summary>Causes the next non-sync/heartbeat delta message (or next published message if not a delta) to be delivered twice to every recipient.</summary>
    public void DuplicateNext()
    {
        lock (_gate)
        {
            _duplicateNext = true;
        }
    }

    /// <summary>Causes the next published message to be silently dropped instead of delivered.</summary>
    public void DropNext()
    {
        lock (_gate)
        {
            _dropNext = true;
        }
    }

    /// <summary>Holds all subsequently published messages in an internal queue until <see cref="Release"/> is called.</summary>
    public void Hold()
    {
        lock (_gate)
        {
            _holding = true;
        }
    }

    /// <summary>Stops holding messages and synchronously delivers all queued messages in publish order.</summary>
    public void Release()
    {
        List<(BackplaneMessage Message, int? SenderId, List<Subscription> Targets)> batch = [];
        lock (_gate)
        {
            _holding = false;
            while (_heldMessages.Count > 0)
            {
                var item = _heldMessages.Dequeue();
                batch.Add((item.Message, item.SenderSubscriptionId, [.. _subscriptions]));
            }
        }

        foreach (var (message, senderId, targets) in batch)
        {
            DeliverToSubscribers(message, senderId, targets, duplicate: false);
        }
    }

    /// <inheritdoc />
    public void Publish(BackplaneMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        List<Subscription> targets;
        bool duplicate = false;
        int? senderId = _activeSubscriberId.Value;

        lock (_gate)
        {
            if (_publishedLog.Count >= 10_000)
            {
                _publishedLog.RemoveAt(0);
            }

            _publishedLog.Add(message);

            if (_dropNext)
            {
                _dropNext = false;
                return;
            }

            if (_duplicateNext)
            {
                _duplicateNext = false;
                duplicate = true;
            }

            if (_holding)
            {
                _heldMessages.Enqueue((message, senderId));
                if (duplicate)
                {
                    _heldMessages.Enqueue((message, senderId));
                }

                return;
            }

            targets = [.. _subscriptions];
        }

        DeliverToSubscribers(message, senderId, targets, duplicate);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<BackplaneMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            var sub = new Subscription(this, _nextSubscriptionId++, handler);
            _subscriptions.Add(sub);
            return sub;
        }
    }

    private void DeliverToSubscribers(BackplaneMessage message, int? senderId, List<Subscription> targets, bool duplicate)
    {
        int deliveries = duplicate ? 2 : 1;
        for (int pass = 0; pass < deliveries; pass++)
        {
            foreach (var sub in targets)
            {
                if (sub.IsDisposed)
                {
                    continue;
                }

                if (senderId.HasValue && sub.Id == senderId.Value)
                {
                    continue;
                }

                int? previous = _activeSubscriberId.Value;
                _activeSubscriberId.Value = sub.Id;
                try
                {
                    sub.Handler(message);
                }
                finally
                {
                    _activeSubscriberId.Value = previous;
                }
            }
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription(InMemoryBackplane owner, int id, Action<BackplaneMessage> handler) : IDisposable
    {
        private int _disposed;

        public int Id { get; } = id;

        public Action<BackplaneMessage> Handler { get; } = handler;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(this);
            }
        }
    }
}
