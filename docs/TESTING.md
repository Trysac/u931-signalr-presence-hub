# Testing

## Running

```sh
dotnet test Library.sln
```

## Coverage

Tests are located in `tests/` and use xUnit and FsCheck. They cover:

- **Convergence**: Property-based tests (FsCheck) verify that sequences of operations on multiple nodes result in consistent `PresenceSnapshot` state.
- **State Transitions**: Unit tests verify that `Connect`, `Disconnect`, `Join`, and `Leave` broadcast appropriate deltas and emit correct `PresenceEvent` objects.
- **Fault Tolerance**: Tests verify that sequence gaps trigger a `sync_request`, that duplicate messages are ignored, and that `InMemoryBackplane` fault hooks (`Hold`, `DropNext`, `DuplicateNext`) operate correctly.
- **Expiration**: Tests verify that nodes correctly expire remote replicas and raise appropriate events after the configured `InstanceTimeout` is reached, using the injected clock.
- **Validation**: Tests verify that `PresenceException` is thrown for invalid names, capacity limits, and duplicate operations.
