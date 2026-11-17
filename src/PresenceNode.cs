namespace Trysac.SignalrPresenceHub;

/// <summary>Tracks local connections and remote instance replicas over an <see cref="IBackplane"/> to provide a convergent view of user presence and room membership.</summary>
public sealed class PresenceNode : IDisposable
{
    private const int MaxNameLength = 128;
    private const int MaxRemoteInstances = 1_024;
    private const int MaxConnectionsPerReplica = 50_000;

    private readonly object _lock = new();
    private readonly string _instanceId;
    private readonly IBackplane _backplane;
    private readonly PresenceOptions _options;
    private readonly Dictionary<string, ConnectionRecord> _localConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstanceReplica> _replicas = new(StringComparer.Ordinal);

    private IDisposable? _subscription;
    private bool _started;
    private bool _disposed;
    private long _sequence;
    private DateTimeOffset? _lastHeartbeatSentAt;

    /// <summary>Raised outside the internal lock whenever a user transitions online/offline or joins/leaves a room.</summary>
    public event Action<PresenceEvent>? OnEvent;

    /// <summary>Gets the identifier of this node instance.</summary>
    public string InstanceId => _instanceId;

    /// <summary>Initializes a new instance of the <see cref="PresenceNode"/> class.</summary>
    /// <param name="instanceId">Unique instance identifier (1 to 128 non-control characters).</param>
    /// <param name="backplane">Backplane used to publish and subscribe to presence messages.</param>
    /// <param name="options">Heartbeat, timeout, clock, and capacity options.</param>
    /// <exception cref="ArgumentNullException">Thrown if any reference argument is null.</exception>
    /// <exception cref="PresenceException">Thrown with <c>invalid_name</c> if <paramref name="instanceId"/> is invalid.</exception>
    public PresenceNode(string instanceId, IBackplane backplane, PresenceOptions options)
    {
        ValidateName(instanceId, nameof(instanceId));
        ArgumentNullException.ThrowIfNull(backplane);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Clock);

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "HeartbeatInterval must be positive.");
        }

        if (options.InstanceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "InstanceTimeout must be positive.");
        }

        if (options.MaxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConnections must be positive.");
        }

        if (options.MaxRoomsPerConnection <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRoomsPerConnection must be positive.");
        }

        _instanceId = instanceId;
        _backplane = backplane;
        _options = options;
    }

    /// <summary>Subscribes this node to the backplane and broadcasts a <c>sync_request</c> to discover existing instances.</summary>
    public void Start()
    {
        bool shouldRequestSync = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _subscription = _backplane.Subscribe(OnBackplaneMessage);
                _started = true;
                _lastHeartbeatSentAt = _options.Clock();
                shouldRequestSync = true;
            }
        }

        if (shouldRequestSync)
        {
            _backplane.Publish(new BackplaneMessage(
                Kind: "sync_request",
                InstanceId: _instanceId,
                Sequence: 0,
                Payload: new SyncRequestPayload(TargetInstanceId: null)));
        }
    }

    /// <summary>Registers a new local connection for <paramref name="userId"/> and broadcasts a <c>connect</c> delta.</summary>
    /// <param name="connectionId">Unique connection identifier (1..128 chars, no control chars).</param>
    /// <param name="userId">User identifier (1..128 chars, no control chars).</param>
    public void Connect(string connectionId, string userId)
    {
        ValidateName(connectionId, nameof(connectionId));
        ValidateName(userId, nameof(userId));

        List<PresenceEvent> events = [];
        BackplaneMessage deltaMessage;

        lock (_lock)
        {
            EnsureStarted();

            if (_localConnections.ContainsKey(connectionId))
            {
                throw new PresenceException("duplicate_connection", "Connection ID is already registered on this node.");
            }

            if (_localConnections.Count >= _options.MaxConnections)
            {
                throw new PresenceException("capacity", "Node has reached its maximum connection capacity.");
            }

            bool wasOnline = IsUserOnlineUnlocked(userId);
            _localConnections[connectionId] = new ConnectionRecord(connectionId, userId);

            if (!wasOnline)
            {
                events.Add(new PresenceEvent("user_online", string.Empty, userId, "connected"));
            }

            long seq = ++_sequence;
            deltaMessage = new BackplaneMessage(
                Kind: "delta",
                InstanceId: _instanceId,
                Sequence: seq,
                Payload: new DeltaPayload("connect", connectionId, userId, string.Empty));
        }

        RaiseEvents(events);
        _backplane.Publish(deltaMessage);
    }

    /// <summary>Removes a local connection and all of its room memberships, broadcasting a <c>disconnect</c> delta.</summary>
    /// <param name="connectionId">Connection identifier to remove.</param>
    public void Disconnect(string connectionId)
    {
        ValidateName(connectionId, nameof(connectionId));

        List<PresenceEvent> events = [];
        BackplaneMessage deltaMessage;

        lock (_lock)
        {
            EnsureStarted();

            if (!_localConnections.TryGetValue(connectionId, out var record))
            {
                throw new PresenceException("unknown_connection", "Connection ID is not registered on this node.");
            }

            var rooms = record.Rooms.OrderBy(r => r, StringComparer.Ordinal).ToList();
            string userId = record.UserId;

            _localConnections.Remove(connectionId);

            foreach (var room in rooms)
            {
                if (!IsUserInRoomUnlocked(userId, room))
                {
                    events.Add(new PresenceEvent("user_left", room, userId, "disconnected"));
                }
            }

            if (!IsUserOnlineUnlocked(userId))
            {
                events.Add(new PresenceEvent("user_offline", string.Empty, userId, "disconnected"));
            }

            long seq = ++_sequence;
            deltaMessage = new BackplaneMessage(
                Kind: "delta",
                InstanceId: _instanceId,
                Sequence: seq,
                Payload: new DeltaPayload("disconnect", connectionId, string.Empty, string.Empty));
        }

        RaiseEvents(events);
        _backplane.Publish(deltaMessage);
    }

    /// <summary>Adds a local connection to <paramref name="room"/> and broadcasts a <c>join</c> delta if not already in the room.</summary>
    /// <param name="connectionId">Local connection identifier.</param>
    /// <param name="room">Room name (1..128 chars, no control chars).</param>
    public void Join(string connectionId, string room)
    {
        ValidateName(connectionId, nameof(connectionId));
        ValidateName(room, nameof(room));

        List<PresenceEvent> events = [];
        BackplaneMessage? deltaMessage = null;

        lock (_lock)
        {
            EnsureStarted();

            if (!_localConnections.TryGetValue(connectionId, out var record))
            {
                throw new PresenceException("unknown_connection", "Connection ID is not registered on this node.");
            }

            if (record.Rooms.Contains(room))
            {
                return;
            }

            if (record.Rooms.Count >= _options.MaxRoomsPerConnection)
            {
                throw new PresenceException("too_many_rooms", "Connection has reached the maximum number of joined rooms.");
            }

            bool wasUserInRoom = IsUserInRoomUnlocked(record.UserId, room);
            record.Rooms.Add(room);

            if (!wasUserInRoom)
            {
                events.Add(new PresenceEvent("user_joined", room, record.UserId, "connected"));
            }

            long seq = ++_sequence;
            deltaMessage = new BackplaneMessage(
                Kind: "delta",
                InstanceId: _instanceId,
                Sequence: seq,
                Payload: new DeltaPayload("join", connectionId, string.Empty, room));
        }

        RaiseEvents(events);
        if (deltaMessage is not null)
        {
            _backplane.Publish(deltaMessage);
        }
    }

    /// <summary>Removes a local connection from <paramref name="room"/> and broadcasts a <c>leave</c> delta if the connection was in the room.</summary>
    /// <param name="connectionId">Local connection identifier.</param>
    /// <param name="room">Room name (1..128 chars, no control chars).</param>
    public void Leave(string connectionId, string room)
    {
        ValidateName(connectionId, nameof(connectionId));
        ValidateName(room, nameof(room));

        List<PresenceEvent> events = [];
        BackplaneMessage? deltaMessage = null;

        lock (_lock)
        {
            EnsureStarted();

            if (!_localConnections.TryGetValue(connectionId, out var record))
            {
                throw new PresenceException("unknown_connection", "Connection ID is not registered on this node.");
            }

            if (!record.Rooms.Remove(room))
            {
                return;
            }

            if (!IsUserInRoomUnlocked(record.UserId, room))
            {
                events.Add(new PresenceEvent("user_left", room, record.UserId, "left"));
            }

            long seq = ++_sequence;
            deltaMessage = new BackplaneMessage(
                Kind: "delta",
                InstanceId: _instanceId,
                Sequence: seq,
                Payload: new DeltaPayload("leave", connectionId, string.Empty, room));
        }

        RaiseEvents(events);
        if (deltaMessage is not null)
        {
            _backplane.Publish(deltaMessage);
        }
    }

    /// <summary>Publishes a heartbeat if <see cref="PresenceOptions.HeartbeatInterval"/> has elapsed and expires remote instance replicas silent for longer than <see cref="PresenceOptions.InstanceTimeout"/>.</summary>
    public void Tick()
    {
        List<PresenceEvent> events = [];
        BackplaneMessage? heartbeatMessage = null;

        lock (_lock)
        {
            EnsureStarted();

            DateTimeOffset now = _options.Clock();

            if (_lastHeartbeatSentAt is null || (now - _lastHeartbeatSentAt.Value) >= _options.HeartbeatInterval)
            {
                _lastHeartbeatSentAt = now;
                heartbeatMessage = new BackplaneMessage(
                    Kind: "heartbeat",
                    InstanceId: _instanceId,
                    Sequence: _sequence,
                    Payload: null);
            }

            var expiredInstances = _replicas
                .Where(kvp => (now - kvp.Value.LastSeen) > _options.InstanceTimeout)
                .Select(kvp => kvp.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (expiredInstances.Count > 0)
            {
                var affectedUsers = new SortedSet<string>(StringComparer.Ordinal);
                var affectedUserRooms = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

                foreach (var instanceId in expiredInstances)
                {
                    var replica = _replicas[instanceId];
                    foreach (var conn in replica.Connections.Values)
                    {
                        affectedUsers.Add(conn.UserId);
                        if (!affectedUserRooms.TryGetValue(conn.UserId, out var userRooms))
                        {
                            userRooms = new SortedSet<string>(StringComparer.Ordinal);
                            affectedUserRooms[conn.UserId] = userRooms;
                        }

                        foreach (var room in conn.Rooms)
                        {
                            userRooms.Add(room);
                        }
                    }

                    _replicas.Remove(instanceId);
                }

                foreach (var userId in affectedUsers)
                {
                    if (affectedUserRooms.TryGetValue(userId, out var rooms))
                    {
                        foreach (var room in rooms)
                        {
                            if (!IsUserInRoomUnlocked(userId, room))
                            {
                                events.Add(new PresenceEvent("user_left", room, userId, "instance_expired"));
                            }
                        }
                    }

                    if (!IsUserOnlineUnlocked(userId))
                    {
                        events.Add(new PresenceEvent("user_offline", string.Empty, userId, "instance_expired"));
                    }
                }
            }
        }

        if (heartbeatMessage is not null)
        {
            _backplane.Publish(heartbeatMessage);
        }

        RaiseEvents(events);
    }

    /// <summary>Returns <c>true</c> if <paramref name="userId"/> holds at least one active connection on this node or any non-expired remote instance.</summary>
    /// <param name="userId">User identifier to query.</param>
    /// <returns><c>true</c> if the user is online; otherwise <c>false</c>.</returns>
    public bool IsOnline(string userId)
    {
        ValidateName(userId, nameof(userId));

        lock (_lock)
        {
            return IsUserOnlineUnlocked(userId);
        }
    }

    /// <summary>Returns the distinct user identifiers with at least one connection in <paramref name="room"/>, ordered with <see cref="StringComparer.Ordinal"/>.</summary>
    /// <param name="room">Room name to query.</param>
    /// <returns>Ordinal-sorted distinct user identifiers in the room.</returns>
    public IReadOnlyList<string> UsersIn(string room)
    {
        ValidateName(room, nameof(room));

        lock (_lock)
        {
            var users = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var conn in _localConnections.Values)
            {
                if (conn.Rooms.Contains(room))
                {
                    users.Add(conn.UserId);
                }
            }

            foreach (var replica in _replicas.Values)
            {
                foreach (var conn in replica.Connections.Values)
                {
                    if (conn.Rooms.Contains(room))
                    {
                        users.Add(conn.UserId);
                    }
                }
            }

            return [.. users];
        }
    }

    /// <summary>Computes an immutable snapshot of all online users and room memberships currently known to this node.</summary>
    /// <returns>A value-equatable <see cref="PresenceSnapshot"/>.</returns>
    public PresenceSnapshot Snapshot()
    {
        lock (_lock)
        {
            var onlineUsers = new SortedSet<string>(StringComparer.Ordinal);
            var rooms = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

            AccumulateConnections(_localConnections.Values, onlineUsers, rooms);
            foreach (var replica in _replicas.Values)
            {
                AccumulateConnections(replica.Connections.Values, onlineUsers, rooms);
            }

            var roomDict = new Dictionary<string, IReadOnlyList<string>>(rooms.Count, StringComparer.Ordinal);
            foreach (var kvp in rooms)
            {
                if (kvp.Value.Count > 0)
                {
                    roomDict[kvp.Key] = [.. kvp.Value];
                }
            }

            return new PresenceSnapshot([.. onlineUsers], roomDict);
        }
    }

    /// <summary>Unsubscribes from the backplane and releases resources held by this node.</summary>
    public void Dispose()
    {
        IDisposable? sub;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sub = _subscription;
            _subscription = null;
        }

        sub?.Dispose();
    }

    private static void AccumulateConnections(
        IEnumerable<ConnectionRecord> connections,
        SortedSet<string> onlineUsers,
        SortedDictionary<string, SortedSet<string>> rooms)
    {
        foreach (var conn in connections)
        {
            onlineUsers.Add(conn.UserId);
            foreach (var room in conn.Rooms)
            {
                if (!rooms.TryGetValue(room, out var members))
                {
                    members = new SortedSet<string>(StringComparer.Ordinal);
                    rooms[room] = members;
                }

                members.Add(conn.UserId);
            }
        }
    }

    private void OnBackplaneMessage(BackplaneMessage message)
    {
        if (message is null || string.Equals(message.InstanceId, _instanceId, StringComparison.Ordinal))
        {
            return;
        }

        if (!IsValidName(message.InstanceId))
        {
            return;
        }

        switch (message.Kind)
        {
            case "delta":
                HandleDelta(message);
                break;
            case "heartbeat":
                HandleHeartbeat(message);
                break;
            case "sync_request":
                HandleSyncRequest(message);
                break;
            case "snapshot":
                HandleSnapshot(message);
                break;
        }
    }

    private void HandleDelta(BackplaneMessage message)
    {
        if (message.Payload is not DeltaPayload delta || message.Sequence <= 0)
        {
            return;
        }

        List<PresenceEvent> events = [];
        BackplaneMessage? syncRequest = null;

        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }

            DateTimeOffset now = _options.Clock();
            if (!_replicas.TryGetValue(message.InstanceId, out var replica))
            {
                if (!TryCreateReplica(message.InstanceId, now, out replica))
                {
                    return;
                }
            }

            replica.LastSeen = now;

            if (message.Sequence <= replica.LastSequence)
            {
                return;
            }

            if (message.Sequence > replica.LastSequence + 1)
            {
                syncRequest = new BackplaneMessage(
                    Kind: "sync_request",
                    InstanceId: _instanceId,
                    Sequence: _sequence,
                    Payload: new SyncRequestPayload(TargetInstanceId: message.InstanceId));
            }
            else
            {
                ApplyDeltaUnlocked(replica, delta, events);
                replica.LastSequence = message.Sequence;
            }
        }

        RaiseEvents(events);
        if (syncRequest is not null)
        {
            _backplane.Publish(syncRequest);
        }
    }

    private void ApplyDeltaUnlocked(InstanceReplica replica, DeltaPayload delta, List<PresenceEvent> events)
    {
        switch (delta.Operation)
        {
            case "connect":
                if (!IsValidName(delta.ConnectionId) || !IsValidName(delta.UserId))
                {
                    return;
                }

                if (replica.Connections.ContainsKey(delta.ConnectionId) || replica.Connections.Count >= MaxConnectionsPerReplica)
                {
                    return;
                }

                bool wasOnline = IsUserOnlineUnlocked(delta.UserId);
                replica.Connections[delta.ConnectionId] = new ConnectionRecord(delta.ConnectionId, delta.UserId);
                if (!wasOnline)
                {
                    events.Add(new PresenceEvent("user_online", string.Empty, delta.UserId, "connected"));
                }

                break;

            case "disconnect":
                if (!IsValidName(delta.ConnectionId))
                {
                    return;
                }

                if (!replica.Connections.TryGetValue(delta.ConnectionId, out var removed))
                {
                    return;
                }

                var rooms = removed.Rooms.OrderBy(r => r, StringComparer.Ordinal).ToList();
                string userId = removed.UserId;
                replica.Connections.Remove(delta.ConnectionId);

                foreach (var room in rooms)
                {
                    if (!IsUserInRoomUnlocked(userId, room))
                    {
                        events.Add(new PresenceEvent("user_left", room, userId, "disconnected"));
                    }
                }

                if (!IsUserOnlineUnlocked(userId))
                {
                    events.Add(new PresenceEvent("user_offline", string.Empty, userId, "disconnected"));
                }

                break;

            case "join":
                if (!IsValidName(delta.ConnectionId) || !IsValidName(delta.Room))
                {
                    return;
                }

                if (!replica.Connections.TryGetValue(delta.ConnectionId, out var joiningConn))
                {
                    return;
                }

                if (joiningConn.Rooms.Contains(delta.Room) || joiningConn.Rooms.Count >= _options.MaxRoomsPerConnection)
                {
                    return;
                }

                bool wasInRoom = IsUserInRoomUnlocked(joiningConn.UserId, delta.Room);
                joiningConn.Rooms.Add(delta.Room);
                if (!wasInRoom)
                {
                    events.Add(new PresenceEvent("user_joined", delta.Room, joiningConn.UserId, "connected"));
                }

                break;

            case "leave":
                if (!IsValidName(delta.ConnectionId) || !IsValidName(delta.Room))
                {
                    return;
                }

                if (!replica.Connections.TryGetValue(delta.ConnectionId, out var leavingConn))
                {
                    return;
                }

                if (!leavingConn.Rooms.Remove(delta.Room))
                {
                    return;
                }

                if (!IsUserInRoomUnlocked(leavingConn.UserId, delta.Room))
                {
                    events.Add(new PresenceEvent("user_left", delta.Room, leavingConn.UserId, "left"));
                }

                break;
        }
    }

    private void HandleHeartbeat(BackplaneMessage message)
    {
        BackplaneMessage? syncRequest = null;

        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }

            DateTimeOffset now = _options.Clock();
            if (!_replicas.TryGetValue(message.InstanceId, out var replica))
            {
                if (!TryCreateReplica(message.InstanceId, now, out replica))
                {
                    return;
                }
            }

            replica.LastSeen = now;

            if (message.Sequence > replica.LastSequence)
            {
                syncRequest = new BackplaneMessage(
                    Kind: "sync_request",
                    InstanceId: _instanceId,
                    Sequence: _sequence,
                    Payload: new SyncRequestPayload(TargetInstanceId: message.InstanceId));
            }
        }

        if (syncRequest is not null)
        {
            _backplane.Publish(syncRequest);
        }
    }

    private void HandleSyncRequest(BackplaneMessage message)
    {
        if (message.Payload is SyncRequestPayload req &&
            req.TargetInstanceId is not null &&
            !string.Equals(req.TargetInstanceId, _instanceId, StringComparison.Ordinal))
        {
            return;
        }

        BackplaneMessage? snapshotMessage = null;

        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }

            DateTimeOffset now = _options.Clock();
            if (_replicas.TryGetValue(message.InstanceId, out var senderReplica))
            {
                senderReplica.LastSeen = now;
            }

            var connections = _localConnections.Values
                .OrderBy(c => c.ConnectionId, StringComparer.Ordinal)
                .Select(c => new ConnectionSnapshot(
                    c.ConnectionId,
                    c.UserId,
                    c.Rooms.OrderBy(r => r, StringComparer.Ordinal).ToArray()))
                .ToArray();

            snapshotMessage = new BackplaneMessage(
                Kind: "snapshot",
                InstanceId: _instanceId,
                Sequence: _sequence,
                Payload: new SnapshotPayload(connections));
        }

        _backplane.Publish(snapshotMessage);
    }

    private void HandleSnapshot(BackplaneMessage message)
    {
        if (message.Payload is not SnapshotPayload snapshotPayload || message.Sequence < 0)
        {
            return;
        }

        List<PresenceEvent> events = [];

        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }

            DateTimeOffset now = _options.Clock();
            if (!_replicas.TryGetValue(message.InstanceId, out var replica))
            {
                if (!TryCreateReplica(message.InstanceId, now, out replica))
                {
                    return;
                }
            }

            replica.LastSeen = now;

            if (message.Sequence < replica.LastSequence)
            {
                return;
            }

            var beforeUsers = GetAllOnlineUsersUnlocked();
            var beforeRooms = GetAllRoomsWithMembersUnlocked();

            replica.Connections.Clear();
            int count = 0;
            foreach (var connSnap in snapshotPayload.Connections)
            {
                if (count >= MaxConnectionsPerReplica)
                {
                    break;
                }

                if (!IsValidName(connSnap.ConnectionId) || !IsValidName(connSnap.UserId))
                {
                    continue;
                }

                var record = new ConnectionRecord(connSnap.ConnectionId, connSnap.UserId);
                foreach (var room in connSnap.Rooms)
                {
                    if (record.Rooms.Count >= _options.MaxRoomsPerConnection)
                    {
                        break;
                    }

                    if (IsValidName(room))
                    {
                        record.Rooms.Add(room);
                    }
                }

                replica.Connections[connSnap.ConnectionId] = record;
                count++;
            }

            replica.LastSequence = message.Sequence;

            var afterUsers = GetAllOnlineUsersUnlocked();
            var afterRooms = GetAllRoomsWithMembersUnlocked();

            foreach (var user in afterUsers)
            {
                if (!beforeUsers.Contains(user))
                {
                    events.Add(new PresenceEvent("user_online", string.Empty, user, "connected"));
                }
            }

            foreach (var kvp in afterRooms)
            {
                beforeRooms.TryGetValue(kvp.Key, out var prevMembers);
                foreach (var user in kvp.Value)
                {
                    if (prevMembers is null || !prevMembers.Contains(user))
                    {
                        events.Add(new PresenceEvent("user_joined", kvp.Key, user, "connected"));
                    }
                }
            }

            foreach (var kvp in beforeRooms)
            {
                afterRooms.TryGetValue(kvp.Key, out var currentMembers);
                foreach (var user in kvp.Value)
                {
                    if (currentMembers is null || !currentMembers.Contains(user))
                    {
                        events.Add(new PresenceEvent("user_left", kvp.Key, user, "disconnected"));
                    }
                }
            }

            foreach (var user in beforeUsers)
            {
                if (!afterUsers.Contains(user))
                {
                    events.Add(new PresenceEvent("user_offline", string.Empty, user, "disconnected"));
                }
            }
        }

        RaiseEvents(events);
    }

    private bool TryCreateReplica(string instanceId, DateTimeOffset now, out InstanceReplica replica)
    {
        if (_replicas.Count >= MaxRemoteInstances)
        {
            var oldestKey = _replicas
                .OrderBy(kvp => kvp.Value.LastSeen)
                .Select(kvp => kvp.Key)
                .FirstOrDefault();
            if (oldestKey is not null)
            {
                _replicas.Remove(oldestKey);
            }
        }

        replica = new InstanceReplica(now);
        _replicas[instanceId] = replica;
        return true;
    }

    private SortedSet<string> GetAllOnlineUsersUnlocked()
    {
        var users = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var conn in _localConnections.Values)
        {
            users.Add(conn.UserId);
        }

        foreach (var replica in _replicas.Values)
        {
            foreach (var conn in replica.Connections.Values)
            {
                users.Add(conn.UserId);
            }
        }

        return users;
    }

    private SortedDictionary<string, SortedSet<string>> GetAllRoomsWithMembersUnlocked()
    {
        var rooms = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        AccumulateRooms(_localConnections.Values, rooms);
        foreach (var replica in _replicas.Values)
        {
            AccumulateRooms(replica.Connections.Values, rooms);
        }

        return rooms;
    }

    private static void AccumulateRooms(
        IEnumerable<ConnectionRecord> connections,
        SortedDictionary<string, SortedSet<string>> rooms)
    {
        foreach (var conn in connections)
        {
            foreach (var room in conn.Rooms)
            {
                if (!rooms.TryGetValue(room, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    rooms[room] = set;
                }

                set.Add(conn.UserId);
            }
        }
    }

    private bool IsUserOnlineUnlocked(string userId)
    {
        foreach (var conn in _localConnections.Values)
        {
            if (string.Equals(conn.UserId, userId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var replica in _replicas.Values)
        {
            foreach (var conn in replica.Connections.Values)
            {
                if (string.Equals(conn.UserId, userId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsUserInRoomUnlocked(string userId, string room)
    {
        foreach (var conn in _localConnections.Values)
        {
            if (string.Equals(conn.UserId, userId, StringComparison.Ordinal) && conn.Rooms.Contains(room))
            {
                return true;
            }
        }

        foreach (var replica in _replicas.Values)
        {
            foreach (var conn in replica.Connections.Values)
            {
                if (string.Equals(conn.UserId, userId, StringComparison.Ordinal) && conn.Rooms.Contains(room))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new PresenceException("not_started", "PresenceNode.Start() must be called before performing operations.");
        }
    }

    private void RaiseEvents(List<PresenceEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        var handler = OnEvent;
        if (handler is null)
        {
            return;
        }

        foreach (var evt in events)
        {
            handler(evt);
        }
    }

    private static void ValidateName(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (!IsValidName(value))
        {
            throw new PresenceException("invalid_name", $"Parameter '{paramName}' must be 1 to 128 non-control characters.");
        }
    }

    private static bool IsValidName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxNameLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ConnectionRecord(string connectionId, string userId)
    {
        public string ConnectionId { get; } = connectionId;

        public string UserId { get; } = userId;

        public HashSet<string> Rooms { get; } = new(StringComparer.Ordinal);
    }

    private sealed class InstanceReplica(DateTimeOffset lastSeen)
    {
        public long LastSequence { get; set; }

        public DateTimeOffset LastSeen { get; set; } = lastSeen;

        public Dictionary<string, ConnectionRecord> Connections { get; } = new(StringComparer.Ordinal);
    }
}
