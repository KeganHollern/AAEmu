using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public class TestHeight : ICommand
{
    public string[] CommandNames { get; set; } = ["testheightvisualizer", "test_height_visualizer"];
    private const float TargetX = 22500f;
    private const float TargetY = 18500f;
    private const float TargetZ = 10f;

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "(target) [testpos||mark||line]";
    }

    public string GetCommandHelpText()
    {
        return "Gets your or target's current height and that of the supposed floor (using heightmap data)\n" +
               "testpos will move you near Freedich underwater\r" +
               "mark creates a grid of pillar doodads used for measuring the floor at 2m intervals (exact heightmap points)\r" +
               "line creates a cross of pillar doodads used for measuring the floor at 1m intervals (for in-between points)";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        var targetPlayer = character;
        var firstarg = 0;
        if (args.Length > 0)
        {
            targetPlayer = WorldManager.Instance.GetTargetOrSelf(character, args[0], out firstarg);
        }

        if (args.Length > firstarg && args[firstarg] == "testpos")
        {
            targetPlayer.DisabledSetPosition = true;
            targetPlayer.SendPacket(new SCTeleportUnitPacket(0, 0, TargetX, TargetY, TargetZ, 0f));
            targetPlayer.SendMessage(
                $"[Move] |cFFFFFFFF{targetPlayer.Name}|r moved to X: {TargetX}, Y: {TargetY}, Z: {TargetZ}");
        }
        else if (args.Length > firstarg && args[firstarg] == "mark")
        {
            // Place markers
            var rX = (int)Math.Floor(targetPlayer.Transform.World.Position.X);
            rX = rX - rX % 2;
            var rY = (int)Math.Floor(targetPlayer.Transform.World.Position.Y);
            rY = rY - rY % 2;
            uint unitId = 5622; // Pillar
            var unavailableMarkers = 0;
            for (var y = rY - 10; y <= rY + 10; y += 2)
            {
                for (var x = rX - 10; x <= rX + 10; x += 2)
                {
                    if (!DoodadManager.Instance.Exist(unitId))
                    {
                        return;
                    }

                    var markerPosition = targetPlayer.Transform.CloneAsSpawnPosition();
                    markerPosition.X = x;
                    markerPosition.Y = y;
                    if (!TryResolveMarkerPosition(targetPlayer.ParentWorld.Template, markerPosition, out _))
                    {
                        unavailableMarkers++;
                        continue;
                    }

                    var doodadSpawner = new DoodadSpawner
                    {
                        ParentWorld = targetPlayer.ParentWorld, Id = 0, UnitId = unitId, Position = markerPosition
                    };
                    doodadSpawner.Position.Yaw = 0;
                    doodadSpawner.Position.Pitch = 0;
                    doodadSpawner.Position.Roll = 0;
                    doodadSpawner.Spawn(0);
                }
            }

            ReportSkippedMarkers(messageOutput, unavailableMarkers);
        }
        else if (args.Length > firstarg && args[firstarg] == "line")
        {
            // Place markers
            var rXX = (int)Math.Floor(targetPlayer.Transform.World.Position.X);
            rXX = rXX - rXX % 2;
            var rYY = (int)Math.Floor(targetPlayer.Transform.World.Position.Y);
            rYY = rYY - rYY % 2;

            float rX = rXX;
            float rY = rYY;
            uint unitId = 5622; // Pillar
            var unavailableMarkers = 0;
            for (var x = rX - 10f; x <= rX + 10f; x += 1f)
            {
                if (!DoodadManager.Instance.Exist(unitId))
                {
                    return;
                }

                var markerPosition = targetPlayer.Transform.CloneAsSpawnPosition();
                markerPosition.X = x;
                markerPosition.Y = rY;
                if (!TryResolveMarkerPosition(targetPlayer.ParentWorld.Template, markerPosition, out _))
                {
                    unavailableMarkers++;
                    continue;
                }

                var doodadSpawner = new DoodadSpawner
                {
                    ParentWorld = targetPlayer.ParentWorld, Id = 0, UnitId = unitId, Position = markerPosition
                };
                doodadSpawner.Position.Yaw = 0;
                doodadSpawner.Position.Pitch = 0;
                doodadSpawner.Position.Roll = 0;
                doodadSpawner.Spawn(0);
            }

            for (var y = rY - 10f; y <= rY + 10f; y += 1f)
            {
                if (!DoodadManager.Instance.Exist(unitId))
                {
                    return;
                }

                var markerPosition = targetPlayer.Transform.CloneAsSpawnPosition();
                markerPosition.X = rX;
                markerPosition.Y = y;
                if (!TryResolveMarkerPosition(targetPlayer.ParentWorld.Template, markerPosition, out _))
                {
                    unavailableMarkers++;
                    continue;
                }

                var doodadSpawner = new DoodadSpawner
                {
                    ParentWorld = targetPlayer.ParentWorld, Id = 0, UnitId = unitId, Position = markerPosition
                };
                doodadSpawner.Position.Yaw = 0;
                doodadSpawner.Position.Pitch = 0;
                doodadSpawner.Position.Roll = 0;
                doodadSpawner.Spawn(0);
            }

            ReportSkippedMarkers(messageOutput, unavailableMarkers);
        }
        else
        {
            // Show info
            var world = targetPlayer.ParentWorld.Template;
            var surface = CommandSurfaceResult.Resolve(world, targetPlayer.Transform.World.Position);
            var terrainDelta = surface.TerrainHeight.HasValue
                ? CommandSurfaceResult.Format(surface.QueryPosition.Z - surface.TerrainHeight.Value)
                : "n/a";
            CommandManager.SendNormalText(this, messageOutput,
                $"{targetPlayer.Name} Z-Pos: {CommandSurfaceResult.Format(surface.QueryPosition.Z)} - " +
                $"{surface.FormatHeights()} terrainDelta={terrainDelta}");
            CommandManager.SendNormalText(this, messageOutput,
                $"|cFFFFFFFF{targetPlayer.Name}|r X: |cFFFFFFFF{targetPlayer.Transform.World.Position.X:F1}|r  Y: |cFFFFFFFF{targetPlayer.Transform.World.Position.Y:F1}|r  Z: |cFFFFFFFF{targetPlayer.Transform.World.Position.Z:F1}|r ");

            var borderLeft = (int)Math.Floor(targetPlayer.Transform.World.Position.X);
            borderLeft = borderLeft - borderLeft % 2;
            var borderRight =
                borderLeft +
                2; // we're using a divider of 2 of the heightmaps in memory, so we need to compensate with that in mind (instead of 1)
            var borderBottom = (int)Math.Floor(targetPlayer.Transform.World.Position.Y);
            borderBottom = borderBottom - borderBottom % 2;
            var borderTop = borderBottom + 2;

            // Get heights for these points
            var heightTL = FormatRawHeight(world, borderLeft, borderTop); // 10
            var heightTR = FormatRawHeight(world, borderRight, borderTop); // 6
            var heightBL = FormatRawHeight(world, borderLeft, borderBottom); // 14
            var heightBR = FormatRawHeight(world, borderRight, borderBottom); // 16
            CommandManager.SendNormalText(this, messageOutput, $"TL @ {borderLeft}x{borderTop} = {heightTL}");
            CommandManager.SendNormalText(this, messageOutput, $"TR @ {borderRight}x{borderTop} = {heightTR}");
            CommandManager.SendNormalText(this, messageOutput, $"BL @ {borderLeft}x{borderBottom} = {heightBL}");
            CommandManager.SendNormalText(this, messageOutput, $"BR @ {borderRight}x{borderBottom} = {heightBR}");
        }
    }

    internal static bool TryResolveMarkerPosition(WorldTemplate template, WorldSpawnPosition markerPosition,
        out CommandSurfaceResult surface)
    {
        surface = CommandSurfaceResult.Resolve(template, markerPosition.AsPositionVector());
        if (!surface.TryGetSelectedGroundHeight(out var height))
            return false;

        markerPosition.Z = height;
        return true;
    }

    private static string FormatRawHeight(WorldTemplate template, int x, int y) =>
        template.TryGetRawHeightMapHeight(x, y, out var height) ? CommandSurfaceResult.Format(height) : "n/a";

    private void ReportSkippedMarkers(IMessageOutput messageOutput, int unavailableMarkers)
    {
        if (unavailableMarkers > 0)
            CommandManager.SendErrorText(this, messageOutput,
                $"Skipped {unavailableMarkers} marker(s) because selected ground was unavailable.");
    }
}
