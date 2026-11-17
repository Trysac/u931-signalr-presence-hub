# Architecture

## Purpose

`Trysac.SignalrPresenceHub` is a small, dependency-free .NET library: Track user presence and room membership across server instances through a pluggable backplane, with sequence-checked deltas, snapshot resync and heartbeat expiry

## Shape

```text
caller code --> namespace Trysac.SignalrPresenceHub (src/)
                    |
                    +-- static classes with pure functions; no I/O, no global mutable state
                    +-- internal helpers (optional)
```

- **Public API**: every `public` type and member of the assembly. `internal` types may change
  without a major version.
- **Errors**: invalid input throws an `ArgumentException` subclass of the library (such as
  `BlankInputException`) documented with `<exception>`; null arguments throw
  `ArgumentNullException`. The library never writes to the console or exits.
- **Side effects**: none. If a feature needs I/O, accept a `Stream`, a `TextReader` or a small
  interface instead of opening resources, and record the choice in an ADR.

## Quality contract

`scripts/Check/` is the single definition of the gates; `fabrica.json` tells the factory and CI how
to run it. See [TESTING.md](TESTING.md).

## Decisions

Architecture decisions are recorded in [decisions/](decisions/) as numbered ADRs.
