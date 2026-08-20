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

-- Attachments: real PNG photos and PDF documents stored as blobs.
INSERT INTO attachments (sensor_id, file_name, mime_type, content, uploaded_at) VALUES
(1, 'sensor-1-install-photo.png', 'image/png', X'89504e470d0a1a0a0000000d49484452000000600000004008020000006a56e559000000884944415478daedd8b10900200c44d1db4c7037d7713a077081807d7c9032d5ab3e97b94e7919bbbcdffe03081020408000016a0b04e2f10f081020408000016afb0f424903020408102040f620a1a8a4010102040810207b905054d2800001020408903d081c20408000010204c81ea4a40101020408102040f620250d081020408000016af17f014381e1d2b8c6d8360000000049454e44ae426082', '2024-06-10 09:00:00'),
(2, 'sensor-2-install-photo.png', 'image/png', X'89504e470d0a1a0a0000000d49484452000000600000004008020000006a56e559000000884944415478daedd8310d00200c44d133801b9ce00075c8c44013f6f2928e9ddef473996795973dcafbed3f80000102040810a0b640201eff80000102040810a0b6ff209434204080000102640f128a4a1a102040800001b2070945250d081020408000d983c00102040810204080ec414a1a10204080000102640f52d280000102040810a016ff174e4c691e657214650000000049454e44ae426082', '2024-06-11 09:00:00'),
(3, 'sensor-3-thermal-cam.png', 'image/png', X'89504e470d0a1a0a0000000d49484452000000600000004008020000006a56e559000000864944415478daedd8410d00200c04c1338502aca20e1518682aa04cd2675ff3da5cee5ee59da4bcdffe03081020408000011a0b04a2ff0704081020408000cd0502a1a4010102040810207b905054d2800001020408903d48282a69408000010204c81e040e10204080000102640f52d280000102040810207b909206040810204080008df87fdd944969e76f00470000000049454e44ae426082', '2024-06-12 09:00:00'),
(5, 'sensor-5-mount-detail.png', 'image/png', X'89504e470d0a1a0a0000000d49484452000000600000004008020000006a56e559000000874944415478daedd8a10d00200c44d133accc663836c3b040c300e52595554ffd5cce1ee5ad99f27efb0f20408000010204a82d1088f73f20408000010204a82f1008250d081020408000d98384a29206040810204080ec4142514903020408102040f6207080000102040810207b90920604081020408000d983943420408000010204a8c5ff058a38d5ffe35255750000000049454e44ae426082', '2024-06-13 09:00:00'),
(1, 'calibration-certificate.pdf', 'application/pdf', X'255044462d312e340a312030206f626a3c3c2f547970652f436174616c6f672f50616765732032203020523e3e656e646f626a0a322030206f626a3c3c2f547970652f50616765732f4b6964735b33203020525d2f436f756e7420313e3e656e646f626a0a332030206f626a3c3c2f547970652f506167652f506172656e742032203020522f4d65646961426f785b30203020333030203132305d2f5265736f75726365733c3c2f466f6e743c3c2f46312035203020523e3e3e3e2f436f6e74656e74732034203020523e3e656e646f626a0a342030206f626a3c3c2f4c656e6774682035393e3e73747265616d0a4254202f4631203134205466203234203630205464202843616c6962726174696f6e2063657274696669636174653a20504153532920546a2045540a656e6473747265616d656e646f626a0a352030206f626a3c3c2f547970652f466f6e742f537562747970652f54797065312f42617365466f6e742f48656c7665746963613e3e656e646f626a0a787265660a3020360a303030303030303030302036353533352066200a30303030303030303039203030303030206e200a30303030303030303532203030303030206e200a30303030303030313031203030303030206e200a30303030303030323131203030303030206e200a30303030303030333135203030303030206e200a747261696c65723c3c2f53697a6520362f526f6f742031203020523e3e0a7374617274787265660a3337360a2525454f46', '2024-06-14 09:00:00'),
(4, 'installation-manual.pdf', 'application/pdf', X'255044462d312e340a312030206f626a3c3c2f547970652f436174616c6f672f50616765732032203020523e3e656e646f626a0a322030206f626a3c3c2f547970652f50616765732f4b6964735b33203020525d2f436f756e7420313e3e656e646f626a0a332030206f626a3c3c2f547970652f506167652f506172656e742032203020522f4d65646961426f785b30203020333030203132305d2f5265736f75726365733c3c2f466f6e743c3c2f46312035203020523e3e3e3e2f436f6e74656e74732034203020523e3e656e646f626a0a342030206f626a3c3c2f4c656e6774682035373e3e73747265616d0a4254202f46312031342054662032342036302054642028496e7374616c6c6174696f6e206d616e75616c20657863657270742920546a2045540a656e6473747265616d656e646f626a0a352030206f626a3c3c2f547970652f466f6e742f537562747970652f54797065312f42617365466f6e742f48656c7665746963613e3e656e646f626a0a787265660a3020360a303030303030303030302036353533352066200a30303030303030303039203030303030206e200a30303030303030303532203030303030206e200a30303030303030313031203030303030206e200a30303030303030323131203030303030206e200a30303030303030333133203030303030206e200a747261696c65723c3c2f53697a6520362f526f6f742031203020523e3e0a7374617274787265660a3337340a2525454f46', '2024-06-15 09:00:00');
