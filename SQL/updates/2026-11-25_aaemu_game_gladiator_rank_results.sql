CREATE TABLE IF NOT EXISTS `gladiator_rank_results` (
  `match_id` char(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `completed_at` datetime(6) NOT NULL,
  `corps1_character_id` int unsigned NOT NULL,
  `corps1_score` int NOT NULL,
  `corps1_rank_points` int NOT NULL,
  `corps2_character_id` int unsigned NOT NULL,
  `corps2_score` int NOT NULL,
  `corps2_rank_points` int NOT NULL,
  PRIMARY KEY (`match_id`),
  KEY `gladiator_corps1_character` (`corps1_character_id`, `completed_at`),
  KEY `gladiator_corps2_character` (`corps2_character_id`, `completed_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
