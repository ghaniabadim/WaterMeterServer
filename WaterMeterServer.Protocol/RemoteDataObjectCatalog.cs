namespace WaterMeterServer.Protocol;

public sealed record RemoteDataObjectDefinition(
    ushort Id,
    string Name,
    string Direction,
    string Domain);

/// <summary>
/// Remote Data IDs that are part of the current electromagnetic-meter release.
/// Local communication and unsupported catalog entries are intentionally excluded.
/// </summary>
public static class RemoteDataObjectCatalog
{
    public static IReadOnlyList<RemoteDataObjectDefinition> Current { get; } =
    [
        new(0x70F2, "RealTimeData", "report/read", "telemetry"),
        new(0xB05C, "DailyFrozenData", "report/read", "operations"),
        new(0xB070, "AlarmStatus", "report", "alarms"),
        new(0x70EE, "TerminalEvents", "report", "alarms"),
        new(0x4304, "UpgradeStatus", "report", "fota"),
        new(0x4305, "FirmwareInfo", "report", "fota"),
        new(0x4306, "FirmwareSegment", "report", "fota"),
        new(0x4307, "FirmwareData", "distribution/report", "fota"),
        new(0x430C, "FirmwareUpgradeRequest", "distribution", "fota")
    ];

    public static bool Contains(ushort id) => Current.Any(x => x.Id == id);
}
