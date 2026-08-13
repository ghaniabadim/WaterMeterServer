CREATE TABLE IF NOT EXISTS communication_logs (
    id BIGSERIAL PRIMARY KEY,
    meter_id VARCHAR(128),
    connection_id VARCHAR(128) NOT NULL,
    direction VARCHAR(16) NOT NULL,
    raw_data BYTEA NOT NULL,
    frame_type SMALLINT,
    protocol_version SMALLINT,
    control_code SMALLINT,
    mid SMALLINT,
    session_id BIGINT,
    frame_number INTEGER,
    request_sequence INTEGER,
    function_code SMALLINT,
    result VARCHAR(32) NOT NULL DEFAULT 'Received',
    error_reason VARCHAR(512),
    processing_duration_ms BIGINT,
    timestamp TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_communication_logs_timestamp
    ON communication_logs (timestamp);
CREATE INDEX IF NOT EXISTS ix_communication_logs_meter_timestamp
    ON communication_logs (meter_id, timestamp);

CREATE TABLE IF NOT EXISTS system_event_logs (
    id BIGSERIAL PRIMARY KEY,
    level SMALLINT NOT NULL,
    category VARCHAR(64) NOT NULL,
    message VARCHAR(2048) NOT NULL,
    exception_details TEXT,
    timestamp TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_system_event_logs_timestamp
    ON system_event_logs (timestamp);
CREATE INDEX IF NOT EXISTS ix_system_event_logs_category_level_timestamp
    ON system_event_logs (category, level, timestamp);

CREATE TABLE IF NOT EXISTS water_usage_records (
    id BIGSERIAL PRIMARY KEY,
    device_id BIGINT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    record_object_id INTEGER NOT NULL,
    record_index INTEGER NOT NULL,
    received_at TIMESTAMPTZ NOT NULL,
    record_time TIMESTAMPTZ NULL,
    raw_data BYTEA NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_water_usage_records_device_object_time
    ON water_usage_records (device_id, record_object_id, record_time);

CREATE TABLE IF NOT EXISTS firmware_chunk_logs (
    id BIGSERIAL PRIMARY KEY,
    firmware_upgrade_request_id BIGINT NOT NULL REFERENCES firmware_upgrade_requests(id) ON DELETE CASCADE,
    device_id BIGINT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    session_id BIGINT NOT NULL,
    mid SMALLINT NOT NULL,
    request_sequence INTEGER NOT NULL,
    offset INTEGER NOT NULL,
    length INTEGER NOT NULL,
    sha256 VARCHAR(64),
    retry_count INTEGER NOT NULL DEFAULT 0,
    result SMALLINT NOT NULL,
    error_message TEXT,
    started_at TIMESTAMPTZ NOT NULL,
    finished_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_firmware_chunk_logs_request_offset
    ON firmware_chunk_logs (firmware_upgrade_request_id, offset);

ALTER TABLE IF EXISTS firmware_upgrade_logs
    ALTER COLUMN "FirmwareVersionId" DROP NOT NULL;

CREATE TABLE IF NOT EXISTS hourly_water_usages (
    "Id" BIGSERIAL PRIMARY KEY,
    "DeviceId" BIGINT NOT NULL REFERENCES devices("Id") ON DELETE CASCADE,
    "HourStart" TIMESTAMPTZ NOT NULL,
    "FirstReadingAt" TIMESTAMPTZ NOT NULL,
    "LastReadingAt" TIMESTAMPTZ NOT NULL,
    "InitialPositiveCumulative" DOUBLE PRECISION NOT NULL,
    "FinalPositiveCumulative" DOUBLE PRECISION NOT NULL,
    "InitialReverseCumulative" DOUBLE PRECISION NOT NULL,
    "FinalReverseCumulative" DOUBLE PRECISION NOT NULL,
    "PositiveUsage" DOUBLE PRECISION NOT NULL,
    "ReverseUsage" DOUBLE PRECISION NOT NULL,
    "NetUsage" DOUBLE PRECISION NOT NULL,
    "SampleCount" INTEGER NOT NULL,
    "UpdatedAt" TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_hourly_water_usages_device_hour
    ON hourly_water_usages ("DeviceId", "HourStart");

CREATE TABLE IF NOT EXISTS daily_water_usages (
    "Id" BIGSERIAL PRIMARY KEY,
    "DeviceId" BIGINT NOT NULL REFERENCES devices("Id") ON DELETE CASCADE,
    "UsageDate" DATE NOT NULL,
    "FirstReadingAt" TIMESTAMPTZ NOT NULL,
    "LastReadingAt" TIMESTAMPTZ NOT NULL,
    "InitialPositiveCumulative" DOUBLE PRECISION NOT NULL,
    "FinalPositiveCumulative" DOUBLE PRECISION NOT NULL,
    "InitialReverseCumulative" DOUBLE PRECISION NOT NULL,
    "FinalReverseCumulative" DOUBLE PRECISION NOT NULL,
    "PositiveUsage" DOUBLE PRECISION NOT NULL,
    "ReverseUsage" DOUBLE PRECISION NOT NULL,
    "NetUsage" DOUBLE PRECISION NOT NULL,
    "SampleCount" INTEGER NOT NULL,
    "UpdatedAt" TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_daily_water_usages_device_date
    ON daily_water_usages ("DeviceId", "UsageDate");

CREATE TABLE IF NOT EXISTS monthly_water_usages (
    "Id" BIGSERIAL PRIMARY KEY,
    "DeviceId" BIGINT NOT NULL REFERENCES devices("Id") ON DELETE CASCADE,
    "UsageYear" INTEGER NOT NULL,
    "UsageMonth" INTEGER NOT NULL,
    "FirstReadingAt" TIMESTAMPTZ NOT NULL,
    "LastReadingAt" TIMESTAMPTZ NOT NULL,
    "InitialPositiveCumulative" DOUBLE PRECISION NOT NULL,
    "FinalPositiveCumulative" DOUBLE PRECISION NOT NULL,
    "InitialReverseCumulative" DOUBLE PRECISION NOT NULL,
    "FinalReverseCumulative" DOUBLE PRECISION NOT NULL,
    "PositiveUsage" DOUBLE PRECISION NOT NULL,
    "ReverseUsage" DOUBLE PRECISION NOT NULL,
    "NetUsage" DOUBLE PRECISION NOT NULL,
    "SampleCount" INTEGER NOT NULL,
    "UpdatedAt" TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_monthly_water_usages_device_month
    ON monthly_water_usages ("DeviceId", "UsageYear", "UsageMonth");
