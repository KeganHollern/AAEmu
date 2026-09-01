CREATE TABLE IF NOT EXISTS `account_daily_login_claims` (
  `account_id` INT NOT NULL,
  `reward_date` DATE NOT NULL,
  `credits_amount` INT NOT NULL,
  `loyalty_amount` INT NOT NULL,
  `claimed_at` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`account_id`, `reward_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Durable idempotency ledger for daily account login rewards';

-- Legacy reset rewards did not update last_login, so seed every existing account at cutover.
-- This prevents a legacy and transactional reward from both being granted on the rollout day.
INSERT IGNORE INTO `account_daily_login_claims`
    (`account_id`, `reward_date`, `credits_amount`, `loyalty_amount`, `claimed_at`)
SELECT `account_id`, UTC_DATE(), 0, 0, UTC_TIMESTAMP(3)
FROM `accounts`;
