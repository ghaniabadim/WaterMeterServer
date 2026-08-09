# FOTA Terminal Simulator

This console application emulates the terminal side of the remote firmware
upgrade protocol:

`Handshake -> Report -> 430C -> 4304 -> 4305 -> (4305 + 4306 -> 4307)* -> 4304 -> End Frame`

## Server prerequisites

1. Register the simulator meter ID in the `devices` table.
2. Create an active `FirmwareUpgradeRequest` in the `Idle` state.
3. Ensure its firmware file path is readable by the WorkerService.
4. Ensure the request `ChunkSize` matches the simulator `--chunk-size`.
5. Clear pending device commands so the server enters the FOTA state directly.

## Run with PowerShell

```powershell
$env:Protocol__AesKey = '676F6C64636172643030323530323533'

dotnet run --project WaterMeterServer.FotaSimulator -- `
  --meter-id 000000000001 `
  --host 127.0.0.1 `
  --port 502 `
  --chunk-size 256 `
  --scenario success
```

## Run with Command Prompt (cmd.exe)

```cmd
set "Protocol__AesKey=676F6C64636172643030323530323533"

dotnet run --project WaterMeterServer.FotaSimulator -- --meter-id 000000000001 --host 127.0.0.1 --port 502 --chunk-size 256 --timeout 120 --scenario success
```

Alternatively, pass the key directly without setting an environment variable:

```cmd
dotnet run --project WaterMeterServer.FotaSimulator -- --meter-id 000000000001 --host 127.0.0.1 --port 502 --chunk-size 256 --timeout 120 --scenario success --key 676F6C64636172643030323530323533
```

Supported scenarios:

- `success`
- `crc-retry`
- `resume`
- `download-failure`
- `install-failure`

The `success`, `crc-retry`, and `resume` scenarios reconnect after download
completion and report `4304H` status `0x05` to complete installation.
