-- Issue #314. No current mail or asset rows change during this additive update.
CREATE TABLE IF NOT EXISTS `mail_lifecycle` (
  `mail_id` bigint NOT NULL,
  `outcome` tinyint unsigned NOT NULL COMMENT '1 returned, 2 archived with contents, 3 removed without contents',
  `transitioned_at` datetime(6) NOT NULL,
  `returned_mail_id` bigint NOT NULL DEFAULT 0,
  `actor_character_id` int unsigned NOT NULL DEFAULT 0 COMMENT '0 for expiry or character removal',
  `source_mail` json NOT NULL COMMENT 'Immutable versioned source snapshot, not claimable mail',
  PRIMARY KEY (`mail_id`),
  KEY `outcome_time` (`outcome`, `transitioned_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `mail_archive_items` (
  `item_id` bigint unsigned NOT NULL,
  `mail_id` bigint NOT NULL,
  `item_row` json NOT NULL COMMENT 'Exact items row snapshot. BLOB values use base64. Original row remains in items.',
  PRIMARY KEY (`item_id`),
  KEY `mail_id` (`mail_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
