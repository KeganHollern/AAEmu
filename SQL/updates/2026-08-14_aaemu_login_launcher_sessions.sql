SET @has_unique_username = (
  SELECT COUNT(*)
  FROM information_schema.statistics
  WHERE table_schema = DATABASE()
    AND table_name = 'users'
    AND index_name = 'uq_users_username'
    AND non_unique = 0
    AND column_name = 'username'
);
SET @has_legacy_username_index = (
  SELECT COUNT(*)
  FROM information_schema.statistics
  WHERE table_schema = DATABASE()
    AND table_name = 'users'
    AND index_name = 'username'
);
SET @username_index_ddl = CASE
  WHEN @has_unique_username > 0 THEN 'SELECT 1'
  WHEN @has_legacy_username_index > 0 THEN
    'ALTER TABLE `users` DROP INDEX `username`, ADD UNIQUE KEY `uq_users_username` (`username`)'
  ELSE 'ALTER TABLE `users` ADD UNIQUE KEY `uq_users_username` (`username`)'
END;
PREPARE aaemu_launcher_username_index FROM @username_index_ddl;
EXECUTE aaemu_launcher_username_index;
DEALLOCATE PREPARE aaemu_launcher_username_index;

CREATE TABLE IF NOT EXISTS `launcher_sessions` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `user_id` int unsigned NOT NULL,
  `access_token_hash` binary(32) NOT NULL,
  `refresh_token_hash` binary(32) NOT NULL,
  `access_expires_at` bigint unsigned NOT NULL,
  `refresh_expires_at` bigint unsigned NOT NULL,
  `created_at` bigint unsigned NOT NULL,
  `updated_at` bigint unsigned NOT NULL,
  `revoked_at` bigint unsigned DEFAULT NULL,

  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_launcher_sessions_access_token_hash` (`access_token_hash`),
  UNIQUE KEY `uq_launcher_sessions_refresh_token_hash` (`refresh_token_hash`),
  KEY `idx_launcher_sessions_user_id` (`user_id`),
  KEY `idx_launcher_sessions_refresh_expires_at` (`refresh_expires_at`),
  CONSTRAINT `fk_launcher_sessions_user_id` FOREIGN KEY (`user_id`)
    REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8 ROW_FORMAT=DYNAMIC COMMENT='Revocable native launcher sessions';

CREATE TABLE IF NOT EXISTS `launcher_launch_tickets` (
  `ticket_hash` binary(32) NOT NULL,
  `session_id` bigint unsigned NOT NULL,
  `username` varchar(32) NOT NULL,
  `expires_at` bigint unsigned NOT NULL,
  `created_at` bigint unsigned NOT NULL,

  PRIMARY KEY (`ticket_hash`),
  UNIQUE KEY `uq_launcher_launch_tickets_session_id` (`session_id`),
  KEY `idx_launcher_launch_tickets_expires_at` (`expires_at`),
  CONSTRAINT `fk_launcher_launch_tickets_session_id` FOREIGN KEY (`session_id`)
    REFERENCES `launcher_sessions` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8 ROW_FORMAT=DYNAMIC COMMENT='Single-use native launcher game tickets';
