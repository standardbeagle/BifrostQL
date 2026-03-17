-- SQLite Advanced sample seed data

-- Manufacturers (5)
INSERT INTO manufacturers (name, country, website) VALUES
('Bosch Sensortec', 'Germany', 'https://www.bosch-sensortec.com'),
('Honeywell', 'United States', 'https://www.honeywell.com'),
('Sensirion', 'Switzerland', 'https://www.sensirion.com'),
('TE Connectivity', 'Switzerland', 'https://www.te.com'),
('Omron', 'Japan', 'https://www.omron.com');

-- Sensor Models (8)
INSERT INTO sensor_models (manufacturer_id, model_name, model_code, specifications, measurement_unit, min_value, max_value) VALUES
(1, 'BME280 Temperature', 'BOSCH-BME280-T', '{"accuracy": "0.5C", "resolution": "0.01C", "response_time_ms": 1000}', 'celsius', -40.0, 85.0),
(1, 'BME280 Humidity', 'BOSCH-BME280-H', '{"accuracy": "3%RH", "resolution": "0.008%RH", "response_time_ms": 1000}', 'percent_rh', 0.0, 100.0),
(2, 'HIH-6130 Humidity', 'HW-HIH6130', '{"accuracy": "4%RH", "resolution": "0.04%RH", "response_time_ms": 6000}', 'percent_rh', 10.0, 90.0),
(3, 'SCD41 CO2', 'SEN-SCD41', '{"accuracy": "50ppm", "range": "400-5000ppm", "response_time_ms": 5000}', 'ppm', 400.0, 5000.0),
(3, 'SPS30 Particulate', 'SEN-SPS30', '{"accuracy": "10%", "particle_range": "0.3-10um", "channels": 5}', 'ug_m3', 0.0, 1000.0),
(4, 'MS5611 Pressure', 'TE-MS5611', '{"accuracy": "1.5mbar", "resolution": "0.012mbar", "response_time_ms": 10}', 'mbar', 10.0, 1200.0),
(5, 'D6T Thermal', 'OMRON-D6T-44L', '{"pixels": "4x4", "fov_deg": 90, "accuracy": "1.5C"}', 'celsius', -10.0, 200.0),
(2, 'ABP Pressure', 'HW-ABPMANN', '{"accuracy": "0.25%", "media": "dry_gas", "port": "axial"}', 'psi', 0.0, 60.0);

-- Locations (6)
INSERT INTO locations (name, building, floor, latitude, longitude, metadata) VALUES
('Main Lobby', 'HQ', 1, 47.6062, -122.3321, '{"zone": "public", "area_sqft": 1200, "hvac_zone": "A1"}'),
('Server Room Alpha', 'HQ', 2, 47.6062, -122.3321, '{"zone": "restricted", "cooling_capacity_kw": 50, "rack_count": 12}'),
('Warehouse Bay 1', 'Warehouse', 1, 47.6105, -122.3340, '{"zone": "storage", "ceiling_height_ft": 30, "dock_number": 1}'),
('Executive Floor', 'HQ', 5, 47.6062, -122.3321, '{"zone": "restricted", "area_sqft": 3000, "hvac_zone": "E1"}'),
('Lab 201', 'Research', 2, 47.6080, -122.3355, '{"zone": "cleanroom", "iso_class": 7, "air_changes_per_hour": 60}'),
('Parking Garage B1', 'Garage', -1, 47.6058, -122.3318, '{"zone": "public", "capacity": 200, "ventilation": "mechanical"}');

-- Sensors (15)
INSERT INTO sensors (serial_number, sensor_model_id, location_id, label, status, installed_at, last_reading_at, configuration, firmware_version) VALUES
('SN-2024-001', 1, 1, 'Lobby Temperature', 'active', '2024-01-15 09:00:00', '2024-10-15 14:30:00', '{"interval_sec": 60, "alerts_enabled": true}', '2.1.0'),
('SN-2024-002', 2, 1, 'Lobby Humidity', 'active', '2024-01-15 09:00:00', '2024-10-15 14:30:00', '{"interval_sec": 60, "alerts_enabled": true}', '2.1.0'),
('SN-2024-003', 1, 2, 'Server Room Temp A', 'active', '2024-02-01 10:00:00', '2024-10-15 14:30:00', '{"interval_sec": 30, "alerts_enabled": true, "critical_temp": 28}', '2.1.0'),
('SN-2024-004', 2, 2, 'Server Room Humidity A', 'active', '2024-02-01 10:00:00', '2024-10-15 14:30:00', '{"interval_sec": 30, "alerts_enabled": true}', '2.1.0'),
('SN-2024-005', 4, 4, 'Executive CO2', 'active', '2024-03-10 08:00:00', '2024-10-15 14:30:00', '{"interval_sec": 300, "alerts_enabled": true}', '1.3.2'),
('SN-2024-006', 5, 5, 'Lab Particulate', 'active', '2024-03-15 11:00:00', '2024-10-15 14:30:00', '{"interval_sec": 120, "alerts_enabled": true, "pm25_limit": 35}', '3.0.1'),
('SN-2024-007', 6, 3, 'Warehouse Pressure', 'active', '2024-04-01 09:00:00', '2024-10-15 14:30:00', '{"interval_sec": 600, "alerts_enabled": false}', '1.0.5'),
('SN-2024-008', 1, 3, 'Warehouse Temperature', 'active', '2024-04-01 09:00:00', '2024-10-15 14:30:00', '{"interval_sec": 300, "alerts_enabled": true}', '2.1.0'),
('SN-2024-009', 7, 2, 'Server Room Thermal', 'active', '2024-05-20 14:00:00', '2024-10-15 14:30:00', '{"interval_sec": 60, "alerts_enabled": true, "hotspot_threshold": 45}', '1.2.0'),
('SN-2024-010', 3, 5, 'Lab Humidity', 'active', '2024-03-15 11:00:00', '2024-10-15 14:30:00', '{"interval_sec": 120, "alerts_enabled": true}', '4.0.0'),
('SN-2024-011', 1, 4, 'Executive Temperature', 'maintenance', '2024-03-10 08:00:00', '2024-09-28 16:00:00', '{"interval_sec": 300, "alerts_enabled": false}', '2.0.3'),
('SN-2024-012', 4, 6, 'Garage CO2', 'active', '2024-06-01 10:00:00', '2024-10-15 14:30:00', '{"interval_sec": 180, "alerts_enabled": true, "ventilation_trigger_ppm": 1000}', '1.3.2'),
('SN-2024-013', 8, 5, 'Lab Pressure', 'active', '2024-06-15 09:00:00', '2024-10-15 14:30:00', '{"interval_sec": 60, "alerts_enabled": true}', '2.2.1'),
('SN-2024-014', 1, 6, 'Garage Temperature', 'decommissioned', '2024-01-20 08:00:00', '2024-07-15 10:00:00', '{"interval_sec": 600, "alerts_enabled": false}', '1.8.0'),
('SN-2024-015', 5, 2, 'Server Room Particulate', 'active', '2024-08-01 10:00:00', '2024-10-15 14:30:00', '{"interval_sec": 300, "alerts_enabled": true}', '3.0.1');

-- Sensor Readings (60 rows across various sensors, using epoch timestamps)
-- Note: recorded_at_text is auto-generated from recorded_at_epoch
INSERT INTO sensor_readings (reading_id, sensor_id, value, recorded_at_epoch, quality_score) VALUES
(1, 1, 21.3, 1728993600, 100),
(2, 1, 21.5, 1728997200, 100),
(3, 1, 22.1, 1729000800, 98),
(4, 1, 22.8, 1729004400, 100),
(5, 1, 22.4, 1729008000, 100),
(6, 2, 45.2, 1728993600, 100),
(7, 2, 46.1, 1728997200, 99),
(8, 2, 44.8, 1729000800, 100),
(9, 2, 43.5, 1729004400, 100),
(10, 2, 44.0, 1729008000, 100),
(11, 3, 23.1, 1728993600, 100),
(12, 3, 23.4, 1728994800, 100),
(13, 3, 24.2, 1728996000, 95),
(14, 3, 24.8, 1728997200, 100),
(15, 3, 25.1, 1728998400, 100),
(16, 3, 24.5, 1728999600, 100),
(17, 3, 23.8, 1729000800, 100),
(18, 3, 23.3, 1729002000, 100),
(19, 4, 38.5, 1728993600, 100),
(20, 4, 39.2, 1728997200, 100),
(21, 4, 40.1, 1729000800, 97),
(22, 4, 39.8, 1729004400, 100),
(23, 4, 38.9, 1729008000, 100),
(24, 5, 620.0, 1728993600, 100),
(25, 5, 580.0, 1728997200, 100),
(26, 5, 750.0, 1729000800, 100),
(27, 5, 890.0, 1729004400, 98),
(28, 5, 680.0, 1729008000, 100),
(29, 6, 8.5, 1728993600, 100),
(30, 6, 9.2, 1728997200, 100),
(31, 6, 7.8, 1729000800, 100),
(32, 6, 8.1, 1729004400, 100),
(33, 6, 12.3, 1729008000, 92),
(34, 7, 1013.2, 1728993600, 100),
(35, 7, 1013.5, 1729000800, 100),
(36, 7, 1012.8, 1729008000, 100),
(37, 8, 18.5, 1728993600, 100),
(38, 8, 19.2, 1728997200, 100),
(39, 8, 20.1, 1729000800, 100),
(40, 8, 19.8, 1729004400, 100),
(41, 8, 18.9, 1729008000, 100),
(42, 9, 32.5, 1728993600, 100),
(43, 9, 33.1, 1728997200, 100),
(44, 9, 35.8, 1729000800, 88),
(45, 9, 34.2, 1729004400, 100),
(46, 9, 33.0, 1729008000, 100),
(47, 10, 42.0, 1728993600, 100),
(48, 10, 43.5, 1728997200, 100),
(49, 10, 41.8, 1729000800, 100),
(50, 10, 42.3, 1729004400, 100),
(51, 12, 450.0, 1728993600, 100),
(52, 12, 520.0, 1728997200, 100),
(53, 12, 680.0, 1729000800, 96),
(54, 12, 890.0, 1729004400, 100),
(55, 12, 1050.0, 1729008000, 100),
(56, 13, 14.7, 1728993600, 100),
(57, 13, 14.8, 1728997200, 100),
(58, 13, 14.6, 1729000800, 100),
(59, 15, 3.2, 1728993600, 100),
(60, 15, 4.1, 1729008000, 100);

-- Alerts (12)
INSERT INTO alerts (sensor_id, severity, message, reading_value, threshold_value, acknowledged, created_at, acknowledged_at) VALUES
(3, 'warning', 'Temperature approaching upper threshold', 24.8, 25.0, 1, '2024-10-15 11:00:00', '2024-10-15 11:15:00'),
(3, 'critical', 'Temperature exceeded threshold', 25.1, 25.0, 1, '2024-10-15 11:20:00', '2024-10-15 11:25:00'),
(5, 'warning', 'CO2 level elevated in executive floor', 890.0, 800.0, 0, '2024-10-15 12:00:00', NULL),
(6, 'warning', 'Particulate spike detected in Lab 201', 12.3, 10.0, 0, '2024-10-15 14:00:00', NULL),
(9, 'critical', 'Thermal hotspot detected near rack 7', 35.8, 35.0, 1, '2024-10-15 10:00:00', '2024-10-15 10:05:00'),
(12, 'warning', 'Garage CO2 rising above normal', 680.0, 600.0, 1, '2024-10-15 10:00:00', '2024-10-15 10:30:00'),
(12, 'critical', 'Garage CO2 exceeded ventilation trigger', 1050.0, 1000.0, 0, '2024-10-15 14:00:00', NULL),
(11, 'info', 'Sensor taken offline for maintenance', NULL, NULL, 1, '2024-09-28 16:00:00', '2024-09-28 16:05:00'),
(4, 'warning', 'Server room humidity above recommended range', 40.1, 40.0, 1, '2024-10-15 10:00:00', '2024-10-15 10:10:00'),
(8, 'info', 'Warehouse temperature within seasonal norms', 20.1, NULL, 0, '2024-10-15 10:00:00', NULL),
(14, 'info', 'Sensor decommissioned - end of service life', NULL, NULL, 1, '2024-07-15 10:00:00', '2024-07-15 10:00:00'),
(15, 'warning', 'Particulate count higher than baseline', 4.1, 4.0, 0, '2024-10-15 14:00:00', NULL);

-- Sensor Settings (20 rows, uses UPSERT-friendly unique constraint on sensor_id + setting_key)
INSERT INTO sensor_settings (sensor_id, setting_key, setting_value, updated_at) VALUES
(1, 'alert_high', '26.0', '2024-01-15 09:00:00'),
(1, 'alert_low', '16.0', '2024-01-15 09:00:00'),
(1, 'sample_rate', '60', '2024-06-01 10:00:00'),
(2, 'alert_high', '60.0', '2024-01-15 09:00:00'),
(2, 'alert_low', '30.0', '2024-01-15 09:00:00'),
(3, 'alert_high', '25.0', '2024-02-01 10:00:00'),
(3, 'alert_low', '18.0', '2024-02-01 10:00:00'),
(3, 'sample_rate', '30', '2024-02-01 10:00:00'),
(3, 'critical_high', '28.0', '2024-02-01 10:00:00'),
(5, 'alert_high', '800.0', '2024-03-10 08:00:00'),
(5, 'alert_low', '350.0', '2024-03-10 08:00:00'),
(6, 'alert_high', '10.0', '2024-03-15 11:00:00'),
(6, 'pm25_limit', '35.0', '2024-03-15 11:00:00'),
(9, 'hotspot_threshold', '45.0', '2024-05-20 14:00:00'),
(9, 'alert_high', '40.0', '2024-05-20 14:00:00'),
(12, 'alert_high', '600.0', '2024-06-01 10:00:00'),
(12, 'ventilation_trigger', '1000.0', '2024-06-01 10:00:00'),
(13, 'alert_high', '15.5', '2024-06-15 09:00:00'),
(13, 'alert_low', '14.0', '2024-06-15 09:00:00'),
(15, 'alert_high', '4.0', '2024-08-01 10:00:00');

-- Maintenance Logs (8)
INSERT INTO maintenance_logs (sensor_id, performed_by, action, notes, parts_replaced, performed_at) VALUES
(3, 'Mike Torres', 'Calibration', 'Quarterly calibration check - within spec', NULL, '2024-04-15 09:00:00'),
(3, 'Mike Torres', 'Calibration', 'Quarterly calibration check - adjusted 0.2C offset', NULL, '2024-07-15 09:00:00'),
(11, 'Sarah Lin', 'Repair', 'Sensor reporting intermittent readings, replaced probe', '{"probe": "BME280-PROBE-R3", "cost_usd": 12.50}', '2024-09-28 14:00:00'),
(14, 'Sarah Lin', 'Decommission', 'End of service life after 3 years, replaced by SN-2024-012', NULL, '2024-07-15 10:00:00'),
(6, 'Mike Torres', 'Firmware Update', 'Updated from v2.8.0 to v3.0.1 - improved particle counting accuracy', NULL, '2024-06-20 11:00:00'),
(9, 'James Cho', 'Installation', 'Installed thermal sensor for server rack monitoring', NULL, '2024-05-20 14:00:00'),
(1, 'Mike Torres', 'Calibration', 'Annual calibration - within spec', NULL, '2025-01-15 09:00:00'),
(15, 'James Cho', 'Installation', 'Installed particulate sensor in server room per air quality initiative', NULL, '2024-08-01 10:00:00');
