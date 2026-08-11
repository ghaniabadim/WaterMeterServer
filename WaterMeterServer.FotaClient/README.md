# FOTA Test Client

Windows desktop client for testing the terminal side of the water-meter FOTA
protocol against a local or remote WorkerService.

## Parameters

- Server host or IP address
- TCP port
- Meter ID
- AES key in hexadecimal format
- Chunk size
- Overall timeout
- Simulation scenario

## Scenarios

- `Success`
- `CrcRetry`
- `Resume`
- `DownloadFailure`
- `InstallFailure`

## Run

Open:

`WaterMeterServer.FotaClient.exe`

The target meter must be registered and have an active
`FirmwareUpgradeRequest` in the server database.
