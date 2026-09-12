-- Historical SKU quantities are not immutable. Keep every legacy quantity unknown.
DROP PROCEDURE IF EXISTS `migrate_ics_sale_quantity_20260912`;
CREATE PROCEDURE `migrate_ics_sale_quantity_20260912`()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'audit_ics_sales' AND COLUMN_NAME = 'item_count'
    ) THEN
        ALTER TABLE `audit_ics_sales` ADD COLUMN `item_count` INT UNSIGNED NULL
            COMMENT 'Immutable sold quantity, NULL for legacy sales' AFTER `sku`;
    END IF;
END;
CALL `migrate_ics_sale_quantity_20260912`();
DROP PROCEDURE `migrate_ics_sale_quantity_20260912`;
