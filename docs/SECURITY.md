# Security notes

## Threat model

`Trysac.SignalrPresenceHub` is a library focusing on state convergence. The following threats are defended:

- **Malformed input**: All public methods validate input (IDs and room names) against length (1-128 chars) and content (no control characters). `PresenceException` is thrown for invalid data.
- **State corruption**: Sequence numbers prevent processing reordered deltas. Heartbeat timeouts prevent stale state from zombie instances.
- **Unintended exposure**: Events carry only semantic data (UserId, Room, Kind, Reason), never low-level connection identifiers.

Threats not defended by the library:
- **Backplane security**: The library assumes the provided `IBackplane` implementation is secure. A compromised backplane can inject arbitrary messages.
- **Runtime environment**: Secure configuration of the host environment, memory access, and dependencies are outside the scope of this library.

## Limits

- **MaxNameLength**: 128 characters.
- **MaxRemoteInstances**: 1,024 instances tracked per node.
- **MaxConnectionsPerReplica**: 50,000 connections tracked per remote instance.
