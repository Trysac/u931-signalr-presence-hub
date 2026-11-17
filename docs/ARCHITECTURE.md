# Architecture

## Overview

`Trysac.SignalrPresenceHub` implements a convergent multi-instance presence and room membership state machine without depending on ASP.NET Core or external transport packages.

## State Model

- Each `PresenceNode` owns its local connections (`_localConnections`) and maintains an per-instance replica (`_replicas[instanceId]`) for every remote node.
- A user is considered online when at least one connection belonging to that user exists on either the local node or any active remote replica.
- Room membership is tracked per connection. `UsersIn(room)` returns the ordinal-sorted distinct users who hold at least one connection in `room`.
- Events (`OnEvent`) fire only on user-level state transitions (`user_online`, `user_offline`, `user_joined`, `user_left`) and never expose connection identifiers.

## Replication Protocol

Nodes communicate over `IBackplane` using `BackplaneMessage` envelopes:

1. **`delta`**: Every local mutation increments the node's monotonic sequence counter (starting at 1) and broadcasts a `DeltaPayload` (`connect`, `disconnect`, `join`, `leave`).
   - Receivers ignore duplicate deltas (`Sequence <= LastSequence`).
   - If `Sequence > LastSequence + 1`, the receiver detects a gap and publishes a targeted `sync_request`.
2. **`sync_request`**: Sent when a node starts (`Start()`) or when a sequence gap or ahead-of-replica heartbeat is observed.
3. **`snapshot`**: The owner responds to a `sync_request` with its complete local connection table and current sequence number. Receivers replace their replica for that instance and emit only the net user-level transition events.
4. **`heartbeat`**: Sent by `Tick()` whenever `HeartbeatInterval` has elapsed. Remote replicas whose `LastSeen` timestamp is older than `InstanceTimeout` are removed during `Tick()`, emitting `user_left` and `user_offline` events with reason `instance_expired`.

## Concurrency

Each `PresenceNode` serializes state transitions under a single private lock (`_lock`) and invokes `OnEvent` subscribers and `IBackplane.Publish` calls outside the lock to prevent deadlocks.
