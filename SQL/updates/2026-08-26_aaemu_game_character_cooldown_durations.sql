-- Retain total skill cooldown duration for the r208022 login cooldown snapshot.
-- Existing rows use 0 because their original duration cannot be recovered. The server
-- falls back to their remaining duration and writes the exact total on the next cast.
-- Apply this update before starting a Game binary that reads duration_ms.
ALTER TABLE `character_cooldowns`
  ADD COLUMN `duration_ms` INT UNSIGNED NOT NULL DEFAULT 0
    COMMENT 'Total cooldown duration in milliseconds'
    AFTER `skill_id`,
  MODIFY COLUMN `expires_at` DATETIME(3) NOT NULL
    COMMENT 'UTC time when the cooldown ends';
