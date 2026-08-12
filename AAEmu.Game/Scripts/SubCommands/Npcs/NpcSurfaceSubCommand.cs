using System.Drawing;
using System.Globalization;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Npcs;

public class NpcSurfaceSubCommand : SubCommandBase
{
    public NpcSurfaceSubCommand()
    {
        Title = "[Npc Surface]";
        Description = "Compare an NPC's runtime height with its authored, home, terrain, and legacy geodata heights";
        CallPrefix = $"{CommandManager.CommandPrefix}npc surface";
        AddParameter(new StringSubCommandParameter("target", "target", true, "target", "id"));
        AddParameter(new NumericSubCommandParameter<uint>("ObjId", "object id", false));
    }

    public override void Execute(ICharacter character, string triggerArgument, IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        Npc npc;
        if (parameters.TryGetValue("ObjId", out var npcObjId))
        {
            npc = ((Character)character).ParentWorld.GetNpc(npcObjId);
            if (npc is null)
            {
                SendColorMessage(messageOutput, Color.Red, $"Npc with objId {npcObjId} does not exist");
                return;
            }
        }
        else
        {
            npc = ((Character)character).CurrentTarget as Npc;
            if (npc is null)
            {
                SendColorMessage(messageOutput, Color.Red, "You need to target a Npc first");
                return;
            }
        }

        var localPosition = npc.Transform.Local.Position;
        var worldPosition = npc.Transform.World.Position;
        var instanceId = npc.Transform.InstanceId;
        var zoneId = npc.Transform.ZoneId;
        var template = npc.ParentWorld?.Template;
        float? terrainZ = template is not null && template.TryGetHeight(worldPosition.X, worldPosition.Y, out var sampledTerrainZ)
            ? sampledTerrainZ
            : null;
        float? geoDataZ = template?.GeoData is not null && template.GeoData.TryGetHeight(worldPosition, out var sampledGeoDataZ)
            ? sampledGeoDataZ
            : null;

        foreach (var line in BuildReport(npc, localPosition, worldPosition, instanceId, zoneId, terrainZ, geoDataZ))
            SendMessage(messageOutput, line);
    }

    internal static string[] BuildReport(Npc npc, Vector3 localPosition, Vector3 worldPosition, uint instanceId, uint zoneId,
        float? terrainZ, float? geoDataZ)
    {
        var ai = npc.Ai;
        var behavior = ai?.GetCurrentBehavior()?.GetType().Name ?? (ai is null ? "no-ai" : "none");
        var spawner = npc.Spawner;
        var spawnerId = spawner is null ? "n/a" : $"{spawner.Id}/{spawner.SpawnerId}";
        var authoredZ = spawner?.Position?.Z;
        float? homeZ = ai is null ? null : ai.HomePosition.Z;
        float? idleZ = ai is null ? null : ai.IdlePosition.Z;

        return
        [
            $"obj={npc.ObjId} template={npc.TemplateId} spawner={spawnerId} instance={instanceId} zone={zoneId} canFly={npc.CanFly} behavior={behavior}",
            $"packetLocal={FormatVector(localPosition)} queryWorld={FormatVector(worldPosition)}",
            $"{FormatHeight("authored", authoredZ, worldPosition.Z)} {FormatHeight("home", homeZ, worldPosition.Z)} {FormatHeight("idle", idleZ, worldPosition.Z)}",
            $"{FormatHeight("terrain", terrainZ, worldPosition.Z)} {FormatHeight("legacyGeo", geoDataZ, worldPosition.Z)} geoMinusTerrain={FormatDifference(geoDataZ, terrainZ)}"
        ];
    }

    private static string FormatVector(Vector3 position) =>
        $"({Format(position.X)},{Format(position.Y)},{Format(position.Z)})";

    private static string FormatHeight(string name, float? value, float worldZ) => value.HasValue
        ? $"{name}={Format(value.Value)} dZ={Format(worldZ - value.Value)}"
        : $"{name}=n/a dZ=n/a";

    private static string FormatDifference(float? left, float? right) => left.HasValue && right.HasValue
        ? Format(left.Value - right.Value)
        : "n/a";

    private static string Format(float value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
