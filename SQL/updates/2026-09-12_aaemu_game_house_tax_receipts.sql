CREATE TABLE IF NOT EXISTS `house_tax_receipts` (
  `mail_id` bigint NOT NULL,
  `house_id` int unsigned NOT NULL,
  `payer_id` int unsigned NOT NULL,
  `quoted_copper` int unsigned NOT NULL,
  `late_fee_percent` int unsigned NOT NULL DEFAULT 0,
  `paid_in_certificates` tinyint(1) NOT NULL,
  `protection_before` datetime(6) NOT NULL,
  `protection_after` datetime(6) NOT NULL,
  `paid_at` datetime(6) NOT NULL,
  PRIMARY KEY (`mail_id`),
  KEY `house_tax_receipts_house` (`house_id`, `paid_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
