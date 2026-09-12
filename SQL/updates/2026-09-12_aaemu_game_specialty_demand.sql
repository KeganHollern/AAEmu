CREATE TABLE IF NOT EXISTS `specialty_demand` (
  `item_id` int unsigned NOT NULL,
  `zone_group_id` int unsigned NOT NULL,
  `ratio` decimal(12,6) NOT NULL,
  `pending_sales` int unsigned NOT NULL DEFAULT 0,
  `consume_at` datetime(6) NOT NULL,
  `regenerate_at` datetime(6) NOT NULL,
  PRIMARY KEY (`item_id`, `zone_group_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
