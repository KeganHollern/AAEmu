CREATE TABLE IF NOT EXISTS `arena_match_honor` (
  `match_id` char(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `character_id` int unsigned NOT NULL,
  `battlefield_id` int unsigned NOT NULL,
  `honor` int unsigned NOT NULL,
  `claimed` tinyint(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (`match_id`, `character_id`),
  KEY `arena_honor_claim` (`character_id`, `claimed`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
