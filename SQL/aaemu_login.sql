CREATE DATABASE IF NOT EXISTS `aaemu_login`;
USE `aaemu_login`;
-- ----------------------------------------------------------------------------------------------
-- Make sure to remove the above two lines if you want use your own DB/Schema names during import
-- This script is idempotent. It can be run multiple times without causing errors, and does not
-- clear data from existing tables.
-- ----------------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS `users` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `username` varchar(32) NOT NULL,
  `password` text COMMENT 'Hashed password of the user',
  `korea_challenge_hash` varchar(120) DEFAULT NULL COMMENT 'sha256_crypt $5$ hash used as AES-256 key for Korea challenge-response auth (V2).',
  `email` varchar(128) NOT NULL,
  `last_login` bigint unsigned NOT NULL DEFAULT '0',
  `last_ip` varchar(128) NOT NULL,
  `created_at` bigint unsigned NOT NULL DEFAULT '0',
  `updated_at` bigint unsigned NOT NULL DEFAULT '0',
  `banned` int NOT NULL DEFAULT '0',
  `ban_reason` int NOT NULL DEFAULT '0',
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_users_username` (`username`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 ROW_FORMAT=DYNAMIC COMMENT='Account login information';


CREATE TABLE IF NOT EXISTS `user_2fa` (
  `user_id` int unsigned NOT NULL,
  `enabled_methods` tinyint unsigned NOT NULL DEFAULT '0' COMMENT 'Bitmask: 1=OTP, 2=PcCert, 4=ARS',

  -- OTP (TOTP)
  `otp_secret` varchar(64) DEFAULT NULL COMMENT 'Base32-encoded TOTP secret',
  `otp_verified` tinyint(1) NOT NULL DEFAULT '0',

  -- PcCert (PIN-based)
  `cert_pin_hash` text DEFAULT NULL COMMENT 'Hashed PIN',

  -- ARS (phone callback)
  `ars_phone_number` varchar(20) DEFAULT NULL,
  `ars_phone_verified` tinyint(1) NOT NULL DEFAULT '0',

  `created_at` bigint unsigned NOT NULL DEFAULT '0',
  `updated_at` bigint unsigned NOT NULL DEFAULT '0',

  PRIMARY KEY (`user_id`),
  CONSTRAINT `fk_user_2fa_user_id` FOREIGN KEY (`user_id`)
    REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8 ROW_FORMAT=DYNAMIC COMMENT='Two-factor authentication settings for Korea auth';


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
