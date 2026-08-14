using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.World;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Bridges the r208022 Character Info shop shortcuts to the legacy NPC-store protocol.
/// </summary>
public static class CurrencyShopManager
{
    // These valid, rarely used express-text IDs are consumed as private client/server signals.
    public const uint HonorShopSignal = 100;
    public const uint VocationShopSignal = 101;

    private const uint HonorMerchantNpcTemplateId = 7054;
    private const uint VocationMerchantNpcTemplateId = 9785;
    private const float VirtualMerchantScale = 0.001f;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly ConcurrentDictionary<uint, Npc> Sessions = new();

    /// <summary>
    /// Opens a currency store when <paramref name="signal"/> is one of the two private shortcut signals.
    /// </summary>
    /// <returns>True when the packet was a shop shortcut and should not be processed as an emote.</returns>
    public static bool TryOpen(Character character, uint signal)
    {
        var npcTemplateId = signal switch
        {
            HonorShopSignal => HonorMerchantNpcTemplateId,
            VocationShopSignal => VocationMerchantNpcTemplateId,
            _ => 0u
        };

        if (npcTemplateId == 0)
            return false;

        if (character?.ParentWorld == null)
            return true;

        Close(character.ObjId);

        var npc = NpcManager.Instance.Create(character.ParentWorld, 0, npcTemplateId);
        if (npc == null)
        {
            Logger.Error("Failed to create virtual currency merchant from NPC template {0}", npcTemplateId);
            return true;
        }

        if (npc.Ai != null)
            npc.Ai.ShouldTick = false;

        npc.OwnerId = character.Id;
        npc.ScaleOverride = VirtualMerchantScale;
        npc.Transform.ZoneId = character.Transform.ZoneId;
        npc.Transform.Local.SetPosition(
            character.Transform.World.Position.X,
            character.Transform.World.Position.Y,
            character.Transform.World.Position.Z,
            character.Transform.World.Rotation.X,
            character.Transform.World.Rotation.Y,
            character.Transform.World.Rotation.Z);

        character.ParentWorld.AddObject(npc);
        npc.AddVisibleObject(character);
        Sessions[character.ObjId] = npc;

        character.SendPacket(new SCNpcInteractionSkillListPacket(
            npc.ObjId,
            0,
            0,
            0,
            1,
            0,
            [SkillsEnum.UseStore]));

        TaskManager.Instance.Schedule(
            new CurrencyShopMerchantDespawnTask(character.ObjId, npc.ObjId),
            SessionLifetime);

        return true;
    }

    /// <summary>
    /// Removes a virtual merchant if it is still the active session for the character.
    /// </summary>
    public static void Close(uint characterObjId, uint expectedNpcObjId = 0)
    {
        if (!Sessions.TryGetValue(characterObjId, out var npc))
            return;

        if (expectedNpcObjId != 0 && npc.ObjId != expectedNpcObjId)
            return;

        if (!Sessions.TryRemove(characterObjId, out npc))
            return;

        ObjectIdManager.Instance.ReleaseId(npc.ObjId);
        npc.Delete();
    }
}
