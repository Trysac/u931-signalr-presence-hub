# Project record

## Objective

Track user presence and room membership across server instances through a pluggable backplane, with sequence-checked deltas, snapshot resync and heartbeat expiry.

## Scope

- In-scope: The implementation of `PresenceNode` and state convergence logic, the `IBackplane` interface and `InMemoryBackplane` implementation, and the public API documented in `README.md`.
- Out-of-scope: ASP.NET Core SignalR hub adapters, Redis/Azure SignalR backplane implementations, persistent storage, authentication and authorization, and external I/O services.

## Timeline

| Date | Event |
|---|---|
| 2026 | Repository created from the factory utility template. |

## Effort metrics

| Metric | Value |
|---|---|
| Tokens (input / output) | 932126 / 29814 |
| Hours (human) | pending measurement |
| Cost (USD) | 0.6569 |

## Factory run u931 (measured from API usage)

| Date (UTC) | Phase | Model | Batch | Input | Output | Cache write | Cache read | Cost (USD) |
|---|---|---|---|---|---|---|---|---|
| 2026-11-17 | generation | google/gemini-4-argon | yes | 5710 | 20665 | 0 | 0 | 0.4361 |
| 2026-11-17 | fix-1 | google/gemini-3.8-flash | no | 83990 | 6618 | 0 | 371543 | 0.1157 |
| 2026-11-17 | audit | google/gemini-3.8-flash | no | 89359 | 876 | 0 | 360217 | 0.0973 |
| 2026-11-17 | docs | google/gemini-3.1-flash-lite | no | 21307 | 1655 | 0 | 0 | 0.0078 |

Total: $0.6569. Prices: vertex line, as measured by its adapter. Providers serve the current version of each model id, so the date tells which version answered; versions, launch stages and price sources per line are in the factory's model register (docs/MODELS.md).
