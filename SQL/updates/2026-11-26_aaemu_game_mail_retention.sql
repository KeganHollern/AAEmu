-- A NULL deadline keeps the legacy expiry for previously delivered letters.
DROP PROCEDURE IF EXISTS `migrate_mail_retention_20261126`;
CREATE PROCEDURE `migrate_mail_retention_20261126`()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mails' AND COLUMN_NAME = 'expires_at'
    ) THEN
        ALTER TABLE `mails` ADD COLUMN `expires_at` DATETIME(6) NULL DEFAULT NULL;
    END IF;
END;
CALL `migrate_mail_retention_20261126`();
DROP PROCEDURE `migrate_mail_retention_20261126`;
