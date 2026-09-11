-- Stop old Game writers and back up the account/character permission fields before this schema change.
-- A Recreate deployment followed by the normal startup updater can provide the writer stop.
-- MySQL DDL commits independently. This procedure supports a retry after each DDL boundary.
-- Do not promote character-only access to an account without an operator decision.
DROP PROCEDURE IF EXISTS `migrate_account_roles_20260910`;
CREATE PROCEDURE `migrate_account_roles_20260910`()
BEGIN
    DECLARE has_account_access INT DEFAULT 0;
    DECLARE has_character_access INT DEFAULT 0;
    DECLARE has_account_role INT DEFAULT 0;
    DECLARE violations BIGINT DEFAULT 0;

    SELECT COUNT(*) INTO has_account_access
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'accounts' AND COLUMN_NAME = 'access_level';
    SELECT COUNT(*) INTO has_character_access
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'characters' AND COLUMN_NAME = 'access_level';
    SELECT COUNT(*) INTO has_account_role
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'accounts' AND COLUMN_NAME = 'role';

    IF has_account_access = 0 AND has_account_role = 0 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Account role migration: no source role column';
    END IF;

    IF has_character_access > 0 THEN
        IF has_account_access = 0 THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Account role migration: character access remains without account access';
        END IF;

        SELECT COUNT(*) INTO violations
        FROM `characters` AS c
        LEFT JOIN `accounts` AS a ON a.`account_id` = c.`account_id`
        WHERE c.`account_id` = 0 OR a.`account_id` IS NULL OR c.`access_level` > a.`access_level`;
        IF violations > 0 THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Account role migration: review orphan characters or character-only access';
        END IF;
    END IF;

    IF has_account_role = 0 THEN
        ALTER TABLE `accounts` ADD COLUMN `role` TINYINT UNSIGNED NOT NULL DEFAULT 0 AFTER `account_id`;
    END IF;

    IF has_account_access > 0 THEN
        UPDATE `accounts`
        SET `role` = CASE
            WHEN `account_id` <= 0 THEN 0
            WHEN `access_level` >= 100 THEN 2
            WHEN `access_level` >= 50 THEN 1
            ELSE 0
        END;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.TABLE_CONSTRAINTS
        WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'accounts' AND CONSTRAINT_NAME = 'chk_accounts_role'
    ) THEN
        ALTER TABLE `accounts` ADD CONSTRAINT `chk_accounts_role` CHECK (`role` BETWEEN 0 AND 2);
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.TABLE_CONSTRAINTS
        WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'accounts' AND CONSTRAINT_NAME = 'chk_accounts_zero_role'
    ) THEN
        ALTER TABLE `accounts` ADD CONSTRAINT `chk_accounts_zero_role` CHECK (`account_id` <> 0 OR `role` = 0);
    END IF;

    -- Drop the character column first. A retry still has the account source until the final DDL succeeds.
    IF has_character_access > 0 THEN
        ALTER TABLE `characters` DROP COLUMN `access_level`;
    END IF;
    IF has_account_access > 0 THEN
        ALTER TABLE `accounts` DROP COLUMN `access_level`;
    END IF;
END;
CALL `migrate_account_roles_20260910`();
DROP PROCEDURE `migrate_account_roles_20260910`;
