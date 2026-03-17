CREATE TABLE manufacturers (
    manufacturer_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    country TEXT NOT NULL,
    website TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE sensor_models (
    sensor_model_id INTEGER PRIMARY KEY AUTOINCREMENT,
    manufacturer_id INTEGER NOT NULL REFERENCES manufacturers(manufacturer_id) ON DELETE CASCADE,
    model_name TEXT NOT NULL,
    model_code TEXT NOT NULL UNIQUE,
    specifications TEXT,
    measurement_unit TEXT NOT NULL,
    min_value REAL,
    max_value REAL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE locations (
    location_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    building TEXT,
    floor INTEGER,
    latitude REAL,
    longitude REAL,
    metadata TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE sensors (
    sensor_id INTEGER PRIMARY KEY AUTOINCREMENT,
    serial_number TEXT NOT NULL UNIQUE,
    sensor_model_id INTEGER NOT NULL REFERENCES sensor_models(sensor_model_id) ON DELETE CASCADE,
    location_id INTEGER REFERENCES locations(location_id) ON DELETE SET NULL,
    label TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'active',
    installed_at TEXT NOT NULL DEFAULT (datetime('now')),
    last_reading_at TEXT,
    configuration TEXT,
    firmware_version TEXT
);

CREATE TABLE sensor_readings (
    reading_id INTEGER PRIMARY KEY,
    sensor_id INTEGER NOT NULL,
    value REAL NOT NULL,
    recorded_at_epoch INTEGER NOT NULL DEFAULT (unixepoch()),
    recorded_at_text TEXT NOT NULL GENERATED ALWAYS AS (datetime(recorded_at_epoch, 'unixepoch')) STORED,
    quality_score INTEGER NOT NULL DEFAULT 100
) STRICT;

CREATE TABLE alerts (
    alert_id INTEGER PRIMARY KEY AUTOINCREMENT,
    sensor_id INTEGER NOT NULL REFERENCES sensors(sensor_id) ON DELETE CASCADE,
    severity TEXT NOT NULL DEFAULT 'warning',
    message TEXT NOT NULL,
    reading_value REAL,
    threshold_value REAL,
    acknowledged INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    acknowledged_at TEXT
);

CREATE TABLE sensor_settings (
    sensor_setting_id INTEGER PRIMARY KEY AUTOINCREMENT,
    sensor_id INTEGER NOT NULL REFERENCES sensors(sensor_id) ON DELETE CASCADE,
    setting_key TEXT NOT NULL,
    setting_value TEXT NOT NULL,
    updated_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(sensor_id, setting_key)
);

CREATE TABLE maintenance_logs (
    maintenance_log_id INTEGER PRIMARY KEY AUTOINCREMENT,
    sensor_id INTEGER NOT NULL REFERENCES sensors(sensor_id) ON DELETE CASCADE,
    performed_by TEXT NOT NULL,
    action TEXT NOT NULL,
    notes TEXT,
    parts_replaced TEXT,
    performed_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE VIEW sensor_reading_stats AS
WITH ranked_readings AS (
    SELECT
        sr.sensor_id,
        s.label AS sensor_label,
        sr.value,
        sr.recorded_at_text,
        sr.quality_score,
        ROW_NUMBER() OVER (PARTITION BY sr.sensor_id ORDER BY sr.recorded_at_epoch DESC) AS reading_rank,
        AVG(sr.value) OVER (PARTITION BY sr.sensor_id) AS avg_value,
        MIN(sr.value) OVER (PARTITION BY sr.sensor_id) AS min_value,
        MAX(sr.value) OVER (PARTITION BY sr.sensor_id) AS max_value,
        COUNT(*) OVER (PARTITION BY sr.sensor_id) AS total_readings
    FROM sensor_readings sr
    INNER JOIN sensors s ON s.sensor_id = sr.sensor_id
)
SELECT
    sensor_id,
    sensor_label,
    value AS latest_value,
    recorded_at_text AS latest_reading_at,
    quality_score AS latest_quality,
    avg_value,
    min_value,
    max_value,
    total_readings
FROM ranked_readings
WHERE reading_rank = 1;
