# Trysac.SignalrPresenceHub

Track user presence and room membership across server instances through a pluggable backplane, with sequence-checked deltas, snapshot resync and heartbeat expiry.

## Problem

Chat and collaboration applications scaled to multiple SignalR instances frequently report stale user presence status, showing users as online after an instance crashes or offline while they remain connected to another node. This occurs because standard backplanes only forward messages and do not reconcile internal application state. `Trysac.SignalrPresenceHub` maintains a convergent presence view across all nodes using an `IBackplane` interface, utilizing sequence-checked deltas, atomic snapshots, and heartbeat-based expiry to ensure system-wide consistency.

## Installation

Add the `Trysac.SignalrPresenceHub` package via NuGet.

## Usage

```csharp
using Trysac.SignalrPresenceHub;

var backplane = new InMemoryBackplane();
var options = new PresenceOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), () => DateTimeOffset.UtcNow);
using var nodeA = new PresenceNode("a", backplane, options);
using var nodeB = new PresenceNode("b", backplane, options);
nodeA.Start();
nodeB.Start();
nodeA.Connect("c1", "ana");
nodeA.Join("c1", "lobby");
```

## Public API

### Core Classes
- `PresenceNode(string instanceId, IBackplane backplane, PresenceOptions options)`
  - `void Start()`: Initializes subscription and broadcasts a sync request.
  - `void Connect(string connectionId, string userId)`: Registers a connection and broadcasts state.
  - `void Disconnect(string connectionId)`: Removes a connection and broadcasts state.
  - `void Join(string connectionId, string room)`: Adds connection to room and broadcasts state.
  - `void Leave(string connectionId, string room)`: Removes connection from room and broadcasts state.
  - `void Tick()`: Sends heartbeats and expires stale remote instances.
  - `bool IsOnline(string userId)`: Checks if user has active connections globally.
  - `IReadOnlyList<string> UsersIn(string room)`: Returns sorted list of users in a room.
  - `PresenceSnapshot Snapshot()`: Returns an immutable snapshot of all connections and rooms.
  - `event Action<PresenceEvent>? OnEvent`: Invoked on state transitions.
- `IBackplane`: Interface for pub/sub transport.
- `InMemoryBackplane : IBackplane`: Test-oriented implementation with `DuplicateNext()`, `DropNext()`, `Hold()`, and `Release()` for fault injection.

### Options & Types
- `PresenceOptions(TimeSpan HeartbeatInterval, TimeSpan InstanceTimeout, Func<DateTimeOffset> Clock, int MaxConnections = 10_000, int MaxRoomsPerConnection = 100)`
- `PresenceEvent(string Kind, string Room, string UserId, string Reason)`
- `PresenceException`: Thrown for `duplicate_connection`, `unknown_connection`, `invalid_name`, `too_many_rooms`, `capacity`, `not_started`.

## Why not SignalR Groups?

SignalR groups are transport-only mechanisms designed for message broadcasting. They cannot be queried to list active members, they do not reconcile state across server restarts or crashes, and they lack visibility into user presence, making them unsuitable for building collaborative presence features.

## Security

See [docs/SECURITY.md](docs/SECURITY.md) for details on the threat model and input validation limits.
