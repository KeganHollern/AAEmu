-- Independent r208022 shared cooldowns retain their actual duration and UTC expiry.
-- The Game updater creates this table before character cooldowns load.
CREATE TABLE IF NOT EXISTS `character_cooldown_tags` (
  `character_id` INT UNSIGNED NOT NULL COMMENT 'Character who owns this shared cooldown',
  `tag_id` INT UNSIGNED NOT NULL COMMENT 'Authored cooldown tag ID',
  `duration_ms` INT UNSIGNED NOT NULL DEFAULT 0 COMMENT 'Total cooldown duration in milliseconds',
  `expires_at` DATETIME(3) NOT NULL COMMENT 'UTC time when the cooldown ends',
  PRIMARY KEY (`character_id`, `tag_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Shared cooldown tags persisted across player sessions';
