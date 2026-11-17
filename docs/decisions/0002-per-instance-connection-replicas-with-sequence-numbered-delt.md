# Per-instance connection replicas with sequence-numbered deltas and snapshot resynchronization

Status: accepted (generated, pending human review)

## Context

Multi-instance real-time applications using SignalR need an accurate view of online users and room members across server nodes. Forwarding raw connection events over a pub/sub backplane drifts when packets are duplicated, dropped, reordered, or when an instance crashes without sending disconnect messages.

## Decision

Partition presence state by owning instance ID. Each PresenceNode owns its local connections, numbers outgoing deltas sequentially from 1, ignores duplicate sequence numbers, requests a full instance snapshot upon detecting a sequence gap or startup, and expires remote instance replicas when heartbeats are not received within InstanceTimeout.

## Consequences

Presence state converges deterministically after packet duplication, packet loss, late node startup, and silent node failures without requiring distributed locks or external consensus.
