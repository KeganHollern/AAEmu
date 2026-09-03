# `World.json` settings (AAEmu.Game)

This document describes the settings in `AAEmu.Game/Configurations/World.json`.

## Where to configure

- **File**: `AAEmu.Game/Configurations/World.json`
- **Section**: `World`

Example:

```json
{
  "World": {
    "MOTD": "Welcome to AAEmu!",
    "TargetPhysicsTps": 25.0
  }
}
```

## Parameters

### `MOTD`
- **Type**: `string`
- **Description**: Message of the Day (shown in chat on login).

### `LogoutMessage`
- **Type**: `string`
- **Description**: Message shown when leaving the game.

### `AutoSaveInterval`
- **Type**: `number`
- **Description**: Auto-save interval (minutes).

### `ExpRate`
- **Type**: `number`
- **Description**: Server-side EXP multiplier.

### `HonorRate`
- **Type**: `number`
- **Description**: Server-side honor points multiplier.

### `VocationRate`
- **Type**: `number`
- **Description**: Server-side vocation badge / vocation points multiplier.

### `LootRate`
- **Type**: `number`
- **Description**: Loot dice multiplier (not all loot types are affected).

### `GoldLootMultiplier`
- **Type**: `number`
- **Description**: Gold multiplier for gold obtained from loot.

### `GrowthRate`
- **Type**: `number`
- **Description**: Growth rate multiplier for doodads (growth steps, not simple timers).

### `DaysForTaxPayment`
- **Type**: `number`
- **Description**: Number of days one tax payment covers (default: 7).

### `MaxTaxPrepaymentPeriods`
- **Type**: `number`
- **Description**: Maximum number of optional tax periods offered consecutively after the current bill is paid (default: 5). Set to `0` to disable tax prepayment mails.

### `IgnoreFallDamageAccessLevel`
- **Type**: `number`
- **Description**: Minimum access level that ignores fall damage (dev/testing).

### `GodMode`
- **Type**: `boolean`
- **Description**: When `true`, players take no damage.

### `GeoDataMode`
- **Type**: `boolean`
- **Description**: Enables loading GeoData/NavMesh (dungeons/navigation).

### `PreLoadTerrain`
- **Type**: `boolean`
- **Description**: When `true`, preloads terrain data (slower startup, lower runtime spikes, higher memory usage).

### `MaxInstances`
- **Type**: `number`
- **Description**: Maximum number of instances (including system instances).

### `TargetPhysicsTps`
- **Type**: `number`
- **Description**: Target physics TPS (tick rate for physics threads).

## World clock

### `Time.Mode`
- **Type**: `string`
- **Allowed values**:
  - **`Accelerated`**: Derives the game-day phase from the UTC epoch and the configured day length.
  - **`TimeZone`**: Matches the wall clock in `Time.TimeZoneId`.
- **Default**: `Accelerated`

Both modes derive time from an authoritative clock. Server restarts and delayed ticks do not reset the game time.
World clock setting changes take effect after a Game service restart.

### `Time.TimeZoneId`
- **Type**: `string`
- **Description**: IANA or Windows time-zone ID for `TimeZone` mode.
- **Default**: `UTC`
- **Example**: `America/Chicago` follows Central Standard Time and Central Daylight Time.

The `/time set` command is disabled in `TimeZone` mode because the wall clock controls game time.

### `Time.AcceleratedDayLengthMinutes`
- **Type**: `number`
- **Description**: Real minutes for one complete game day in `Accelerated` mode.
- **Default**: `240.0`

The `/time set` command changes the accelerated phase until the Game service restarts.
Use the UTC-derived phase for restart-safe accelerated time.

Example:

```json
{
  "World": {
    "Time": {
      "Mode": "TimeZone",
      "TimeZoneId": "America/Chicago"
    }
  }
}
```

### `ActabilityRate`
- **Type**: `number`
- **Description**: Server-side actability points multiplier.

### `QuestTeamShareRange`

- **Type**: `number`
- **Default**: `200.0`
- **Description**: Maximum 3D distance in meters from a team-shared quest event, including monster credit expanded by `TagShareEnabled`. The NPC or victim position is used when available; otherwise the originating player's position is used. Only online members in the same world instance and within this range receive shared credit.

## Ship wind

### `WindModel`
- **Type**: `string`
- **Path**: `World.WindModel`
- **Allowed values**:
  - **`Official`**: retail-like wind model.
    - wind does **not** change with time of day;
    - a **+15%** max speed bonus applies only when sailing within **±15°** of the **North↔South** axis (both directions);
    - outside the cone, the bonus is **0%**.
  - **`Realistic`**: more realistic model.
    - wind direction rotates smoothly over the day (and sail rig profile logic applies).

Examples:

```json
{
  "World": {
    "WindModel": "Official"
  }
}
```

```json
{
  "World": {
    "WindModel": "Realistic"
  }
}
```
