# Phase 1 — Remote Protocol V0.3

Date: 2026-08-22

## Implemented

- Added a remote-only Data ID catalog for the current electromagnetic-meter release.
- Added official remote handshake-vector parsing coverage.
- Added reconnect coverage proving that the previous Session ID is invalidated.
- Added duplicate/replay and frame-number rollback coverage.
- Hardened `SessionManager.GenerateSessionId` so a new session removes stale reverse mappings and MID state.

## Current remote catalog

The catalog currently covers:

- `70F2H` — real-time telemetry
- `B05CH` — daily frozen data
- `B070H` — alarm status
- `70EEH` — terminal events
- `4304H` — upgrade status
- `4305H` — firmware information
- `4306H` — firmware segmentation
- `4307H` — firmware data
- `430CH` — firmware upgrade request

Local communication entries are intentionally not included.

## Verification

```text
dotnet test WaterMeterServer.Protocol.Tests/WaterMeterServer.Protocol.Tests.csproj --no-restore
Passed: 13
Failed: 0
Skipped: 0
```

## Remaining Phase 1 work

- Appendix B handshake request, handshake response, and periodic-reporting first-frame vectors are now parsed as automated contract tests.
- Added explicit response builders for negative read/write/record responses.
- Added a 30-minute inactivity TTL policy and deterministic expiry coverage.
- Validate every catalog entry against the approved Remote column in the Data ID workbook.
