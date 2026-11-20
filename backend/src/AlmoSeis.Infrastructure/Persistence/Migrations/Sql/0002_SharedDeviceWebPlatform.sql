-- GERADO por backend/tools/Sync-DdlMigration.ps1 a partir de docs/05-ddl.md. NÃƒO EDITE Ã€ MÃƒO.
ALTER TABLE shared_devices DROP CONSTRAINT shared_devices_platform_check;
ALTER TABLE shared_devices ADD CONSTRAINT shared_devices_platform_check CHECK (platform IN ('ANDROID', 'IOS', 'WEB'));
