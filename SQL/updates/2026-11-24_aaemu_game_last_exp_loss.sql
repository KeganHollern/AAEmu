-- Older balances contain only the priest share. The exact loss is unknown.
DROP PROCEDURE IF EXISTS `migrate_last_exp_loss_20261124`;
CREATE PROCEDURE `migrate_last_exp_loss_20261124`()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'characters' AND COLUMN_NAME = 'last_exp_loss'
    ) THEN
        ALTER TABLE `characters` ADD COLUMN `last_exp_loss` INT NOT NULL DEFAULT 0;
    END IF;
END;
CALL `migrate_last_exp_loss_20261124`();
DROP PROCEDURE `migrate_last_exp_loss_20261124`;
