-- NULL leaves existing owned and manually placed houses outside the tax-ruin lifecycle.
DROP PROCEDURE IF EXISTS `migrate_tax_demolition_20261125`;
CREATE PROCEDURE `migrate_tax_demolition_20261125`()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'housings' AND COLUMN_NAME = 'demolition_hp'
    ) THEN
        ALTER TABLE `housings` ADD COLUMN `demolition_hp` INT UNSIGNED DEFAULT NULL;
    END IF;
END;
CALL `migrate_tax_demolition_20261125`();
DROP PROCEDURE `migrate_tax_demolition_20261125`;
