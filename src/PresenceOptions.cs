namespace Trysac.SignalrPresenceHub;

/// <summary>Configuration options for a <see cref="PresenceNode"/>.</summary>
/// <param name="HeartbeatInterval">Minimum interval between outgoing heartbeat messages during <see cref="PresenceNode.Tick"/>.</param>
/// <param name="InstanceTimeout">Maximum silence allowed from a remote instance before its replica is expired during <see cref="PresenceNode.Tick"/>.</param>
/// <param name="Clock">Deterministic or system clock supplying the current <see cref="DateTimeOffset"/>.</param>
/// <param name="MaxConnections">Maximum local connections allowed on the node (default 10,000).</param>
/// <param name="MaxRoomsPerConnection">Maximum distinct rooms a single connection may join (default 100).</param>
public sealed record PresenceOptions(
    TimeSpan HeartbeatInterval,
    TimeSpan InstanceTimeout,
    Func<DateTimeOffset> Clock,
    int MaxConnections = 10_000,
    int MaxRoomsPerConnection = 100);
