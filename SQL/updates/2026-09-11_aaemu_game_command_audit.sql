CREATE TABLE IF NOT EXISTS `command_audit` (
    `request_id` char(36) NOT NULL,
    `started_at` datetime(6) NOT NULL,
    `completed_at` datetime(6) DEFAULT NULL,
    `actor_account_id` int unsigned NOT NULL,
    `actor_character_id` int unsigned NOT NULL,
    `actor_role` varchar(16) NOT NULL,
    `source` varchar(24) NOT NULL,
    `remote_address` varchar(64) NOT NULL,
    `command_name` varchar(128) NOT NULL,
    `arguments` json NOT NULL,
    `targets` json NOT NULL,
    `result` varchar(32) NOT NULL,
    `detail` varchar(1024) NOT NULL DEFAULT '',
    PRIMARY KEY (`request_id`),
    KEY `ix_command_audit_actor_time` (`actor_account_id`, `started_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
