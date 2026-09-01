-- Preserve auction mail claim identity and the result needed to replay a lost acknowledgement.
CREATE TABLE IF NOT EXISTS `auction_mail_claims` (
  `mail_id` INT UNSIGNED NOT NULL COMMENT 'Auction mail ID retained as the idempotency source',
  `claim_type` TINYINT UNSIGNED NOT NULL COMMENT '1=auction buy item, 2=auction sale money',
  `receiver_id` INT UNSIGNED NOT NULL COMMENT 'Character that received the auction mail',
  `item_id` BIGINT UNSIGNED NULL COMMENT 'Claimed item ID for an auction buy',
  `item_count` INT UNSIGNED NULL COMMENT 'Claimed item count for an auction buy',
  `item_slot_type` TINYINT UNSIGNED NULL COMMENT 'Original item slot type used by the claim acknowledgement',
  `item_slot` SMALLINT UNSIGNED NULL COMMENT 'Original item slot used by the claim acknowledgement',
  `money_amount` BIGINT UNSIGNED NULL COMMENT 'Claimed copper amount for an auction sale',
  `created_at` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) COMMENT 'UTC claim commit time',
  PRIMARY KEY (`mail_id`, `claim_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Durable idempotency ledger for auction mail claims';
