-- Skill cooldowns persisted across sessions.
-- Without this table, every relog reset all skill/GCD cooldowns.
CREATE TABLE IF NOT EXISTS `character_cooldowns` (
  `character_id` INT UNSIGNED NOT NULL COMMENT 'Character who owns this cooldown',
  `skill_id`     INT UNSIGNED NOT NULL COMMENT 'SkillTemplate.Id the cooldown applies to',
  `expires_at`   DATETIME NOT NULL COMMENT 'UTC time when the cooldown ends',
  PRIMARY KEY (`character_id`, `skill_id`),
  INDEX `idx_character_id` (`character_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Skill cooldowns persisted across player sessions';
