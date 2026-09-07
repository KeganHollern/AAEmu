CREATE TABLE IF NOT EXISTS `zone_conflict_states` (
    `zone_group_id` SMALLINT UNSIGNED NOT NULL,
    `state` TINYINT UNSIGNED NOT NULL,
    `kill_count` INT UNSIGNED NOT NULL DEFAULT 0,
    `next_state_time` DATETIME(6) NULL,
    PRIMARY KEY (`zone_group_id`)
) ENGINE=InnoDB DEFAULT COLLATE='utf8mb4_general_ci';
