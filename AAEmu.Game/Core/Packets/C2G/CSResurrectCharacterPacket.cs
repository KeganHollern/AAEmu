using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSResurrectCharacterPacket() : GamePacket(CSOffsets.CSResurrectCharacterPacket, 1)
{
    /// <summary>Duration of the post-revive Respawn-Cooldown debuff in milliseconds (5 min).</summary>
    private const int RespawnCooldownDurationMs = 300_000;

    public override void Read(PacketStream stream)
    {
        if (stream.Count - stream.Pos != 1 || Connection.ActiveChar is not { } character)
            return;
        var flag = stream.ReadByte();
        if (flag > 1)
            return;
        lock (character.StorePurchaseSyncRoot)
            Resurrect(character, flag == 1);
    }

    private static void Resurrect(Character character, bool inPlace)
    {
        if (character.Hp > 0)
            return;
        Character.ResurrectionOffer offer = null;
        if (inPlace && !character.TryTakeResurrectionOffer(out offer))
            return;
        character.ClearResurrectionOffer();

        Logger.Debug("ResurrectCharacter, InPlace: {0}", inPlace);

        var portal = new Portal();

        // поищем сначала "UnitId": 502, "Title": "Temple Priestess",
        // Inside dungeons or other instances, just respawn at the nearest Priestess
        if (inPlace)
        {
            portal.X = offer.Position.X;
            portal.Y = offer.Position.Y;
            portal.Z = offer.Position.Z;
            portal.ZRot = offer.Rotation;
        }
        else if (character.Transform.InstanceId != WorldManager.DefaultInstanceId)
        {
            var npcs = character.ParentWorld.GetAllNpcs();
            foreach (var npc in npcs.Where(npc => npc.TemplateId == 502))
            {
                portal.WorldId = character.Transform.WorldId;
                portal.ZoneId = npc.Transform.ZoneId;
                portal.X = npc.Transform.World.Position.X + Random.Shared.Next(1, 3);
                portal.Y = npc.Transform.World.Position.Y + Random.Shared.Next(1, 3);
                portal.Z = npc.Transform.World.Position.Z;
                portal.ZRot = npc.Transform.World.Rotation.Z;
                portal.Yaw = npc.Transform.World.Rotation.Z;
                break;
            }
        }
        else
        {
            // Check if the current zone is at War and if it has special respawn areas for factions
            var usePortalId = 0u;
            var currentZone = ZoneManager.Instance.GetZoneByKey(character.Transform.ZoneId);
            if (currentZone != null)
            {
                var conflictData = ZoneManager.Instance.GetConflicts().FirstOrDefault(c => c.ZoneGroupId == currentZone.GroupId);
                if (conflictData?.CurrentZoneState == ZoneConflictType.War)
                {
                    switch (character.Faction.MotherId)
                    {
                        case FactionsEnum.NuiaAlliance:
                            usePortalId = conflictData.NuiaReturnPointId;
                            break;
                        case FactionsEnum.HaranyaAlliance:
                            usePortalId = conflictData.HariharaReturnPointId;
                            break;
                    }
                }
            }

            // Try to get a faction specific respawn
            if (usePortalId > 0)
            {
                portal = PortalManager.Instance.GetRespawnById(usePortalId);
            }

            // Find the closest return portal (in the world) for the player if none has been found yet
            if (usePortalId == 0 || portal == null)
            {
                portal = PortalManager.Instance.GetClosestReturnPortal(character);
            }
        }

        if (inPlace)
        {
            character.Hp = offer.GetHealth(character.MaxHp);
            character.Mp = offer.GetMana(character.MaxMp);
            character.RestorePriestExperience(offer);
            character.PostUpdateCurrentHp(character, 0, character.Hp, KillReason.Unknown);
        }
        else
        {
            character.Hp = (int)(character.MaxHp * 0.1);
            character.Mp = (int)(character.MaxMp * 0.1);
            character.PostUpdateCurrentHp(character, 0, character.Hp, KillReason.Unknown);
        }

        if (portal != null && (inPlace || portal.X != 0))
        {
            character.BroadcastPacket(
                new SCCharacterResurrectedPacket(
                    character.ObjId,
                    portal.X,
                    portal.Y,
                    portal.Z,
                    portal.ZRot
                ),
                true
            );
        }
        else
        {
            character.BroadcastPacket(
                new SCCharacterResurrectedPacket(
                    character.ObjId,
                    character.Transform.World.Position.X,
                    character.Transform.World.Position.Y,
                    character.Transform.World.Position.Z,
                    0
                ),
                true
            );
        }

        character.BroadcastPacket(
            new SCUnitPointsPacket(
                character.ObjId,
                character.Hp,
                character.Mp
            ),
            true
        );

        // Route death-debuffs based on death context (set by Character.DoDie).
        ApplyRevivalDebuffs(character, inPlace);

        // Retail grants a short protection shield after any resurrection
        // (Beginner's Blessing, 6s); it falls off early on skill use or when
        // interacting with nearby objects.
        ApplyBuff(character, new SkillCasterUnit(character.ObjId),
            (uint)BuffConstants.BeginnersBlessing);

        character.IsUnderWater = false;
        //character.StartRegen();
        character.Breath = character.LungCapacity;
    }

    /// <summary>
    /// Apply post-revive debuffs based on the death context:
    ///   inPlace (player-res) → no debuffs at all
    ///   DiedInPvpWarZone     → Leech + 5 min Respawn-CD
    ///   DiedInPvp            → 5 min Respawn-CD only (no Weakened Body)
    ///   PvE death            → Weakened Body + 5 min Respawn-CD
    /// </summary>
    private static void ApplyRevivalDebuffs(Character character, bool inPlace)
    {
        if (inPlace)
        {
            // Player-resurrected (e.g. by another player's resurrect skill): no debuffs.
            character.DiedInPvpWarZone = false;
            character.DiedInPvp = false;
            return;
        }

        var casterObj = new SkillCasterUnit(character.ObjId);

        if (character.DiedInPvpWarZone)
        {
            // PvP death in War zone → Leech + Respawn-CD
            character.DiedInPvpWarZone = false;
            character.DiedInPvp = false;
            ApplyBuff(character, casterObj, (uint)BuffConstants.WarZoneLeech);
            ApplyBuff(character, casterObj, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs);
        }
        else if (character.DiedInPvp)
        {
            // PvP death outside War zone → Respawn-CD only (no Weakened Body)
            character.DiedInPvp = false;
            ApplyBuff(character, casterObj, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs);
        }
        else
        {
            // PvE death → Weakened Body + Respawn-CD
            ApplyBuff(character, casterObj, (uint)BuffConstants.WeakenedBody);
            ApplyBuff(character, casterObj, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs);
        }
    }

    private static void ApplyBuff(Character character, SkillCasterUnit casterObj, uint buffId, int forcedDurationMs = 0)
    {
        var template = SkillManager.Instance.GetBuffTemplate(buffId);
        if (template == null)
            return;

        var buff = new Buff(character, character, casterObj, template, null, DateTime.UtcNow);
        if (forcedDurationMs > 0)
            character.Buffs.AddBuff(buff, forcedDuration: forcedDurationMs);
        else
            character.Buffs.AddBuff(buff);
    }
}
