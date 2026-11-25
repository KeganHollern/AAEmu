-- A prior district visit includes all of its destinations.
DROP PROCEDURE IF EXISTS `migrate_portal_destinations_20261125`;
CREATE PROCEDURE `migrate_portal_destinations_20261125`()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'portal_visited_district' AND COLUMN_NAME = 'omitted_portal_ids'
    ) THEN
        ALTER TABLE `portal_visited_district` ADD COLUMN `omitted_portal_ids` JSON DEFAULT NULL;
    END IF;
END;
CALL `migrate_portal_destinations_20261125`();
DROP PROCEDURE `migrate_portal_destinations_20261125`;
