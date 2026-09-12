-- Retain distinct paid bot reports across restart. No historical reports are inferred.
-- Character saves use REPLACE, so these stable IDs must not use cascading foreign keys.
CREATE TABLE IF NOT EXISTS `bot_reports` (
  `reported_id` int unsigned NOT NULL,
  `reporter_id` int unsigned NOT NULL,
  PRIMARY KEY (`reported_id`, `reporter_id`),
  KEY `idx_bot_reports_reporter` (`reporter_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
