-- Persist achievement record amounts and one completion state per character.
CREATE TABLE IF NOT EXISTS `character_achievement_records` (
  `character_id` INT UNSIGNED NOT NULL COMMENT 'Character who owns this achievement record',
  `record_id` INT UNSIGNED NOT NULL COMMENT 'char_records.id from compact.sqlite3',
  `amount` INT UNSIGNED NOT NULL DEFAULT 0 COMMENT 'Persistent achievement record amount',
  PRIMARY KEY (`character_id`, `record_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Persistent achievement record amounts by character';

CREATE TABLE IF NOT EXISTS `character_achievements` (
  `character_id` INT UNSIGNED NOT NULL COMMENT 'Character who completed this achievement',
  `achievement_id` INT UNSIGNED NOT NULL COMMENT 'achievements.id from compact.sqlite3',
  `completed_at` DATETIME(3) NOT NULL COMMENT 'UTC completion time',
  `reward_status` TINYINT UNSIGNED NOT NULL DEFAULT 0 COMMENT '0=pending, 1=inventory, 2=mail',
  PRIMARY KEY (`character_id`, `achievement_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Completed achievements and reward state by character';
