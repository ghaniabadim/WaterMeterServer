# Phase 0 — Remote TCP Baseline

Date: 2026-08-22

## Scope

This release supports remote communication only:

- Transport: TCP
- Protocol: Iranian Electromagnetic Water Meter Protocol V0.3
- Security: AES-128 ECB with PKCS7 padding
- Integrity: CRC16/CCITT-FALSE style calculation from Appendix C
- Device family in scope: electromagnetic water meter

The following are explicitly out of scope for this release:

- Infrared communication
- RS485 communication
- Local communication framing
- Local XOR-with-`A5` processing
- UDP ingestion
- Ultrasonic meter protocol

## Current runtime baseline

The current executable hosts:

- TCP listener on the configured meter port (default `502`)
- HTTP API on the configured API port (default `5080`)
- PostgreSQL persistence through EF Core/Npgsql
- Background telemetry, communication-log, and command-timeout workers
- Remote handshake, session validation, telemetry reporting, commands, records, and FOTA flows

## Protocol baseline

The current implementation contains:

- Handshake request/response handling
- Session ID and terminal frame-number tracking
- MID propagation
- Reporting, distribution, end-frame and resume responses
- Read/write data-object builders
- Read-records-by-time and read-recent-records builders
- AES encryption/decryption and CRC16 validation
- FOTA object handling for `4304H`, `4305H`, `4306H`, `4307H`, and `430CH`

The primary electromagnetic data objects currently handled in the application are:

- `70F2H` — real-time telemetry
- `B05CH` — daily frozen data
- `B070H` — alarm status
- `70EEH` — event records

## Verification result

Command:

```text
dotnet test WaterMeterServer.Protocol.Tests/WaterMeterServer.Protocol.Tests.csproj --no-restore
```

Result on 2026-08-22:

```text
Passed: 9
Failed: 0
Skipped: 0
```

The test suite currently covers official FOTA vectors, frame parsing, CRC/data-length rejection, reported-object parsing, and firmware chunk construction.

## Phase 0 exit criteria

- [x] Remote-only scope recorded
- [x] Local communication excluded from implementation scope
- [x] Current runtime and protocol capabilities recorded
- [x] Current test baseline captured
- [ ] Appendix B handshake/reporting vectors added to automated tests
- [ ] Required Remote Data ID catalog approved
- [ ] Session/reconnect behavior test cases added

The unchecked items are inputs for Phase 1 and do not change the current runtime behavior.
