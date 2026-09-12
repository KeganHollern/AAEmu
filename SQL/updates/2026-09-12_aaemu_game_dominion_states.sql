CREATE TABLE IF NOT EXISTS `dominion_states` (
  `zone_group_id` smallint unsigned NOT NULL,
  `siege_zone_id` int unsigned NOT NULL,
  `owner_expedition_id` int unsigned NOT NULL DEFAULT 0,
  `tax_rate` int NOT NULL DEFAULT 0,
  `house_tax_balance` bigint NOT NULL DEFAULT 0,
  PRIMARY KEY (`zone_group_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
