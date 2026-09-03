using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Utils;

using GameTeam = AAEmu.Game.Models.Game.Team.Team;

namespace AAEmu.Game.Models.Game.Quests;

internal static class QuestTeamShareEligibility
{
    internal static bool IsEligibleMember(
        ICharacter sourcePlayer,
        ICharacter teamMember,
        Transform rangeOrigin = null,
        GameTeam sourceTeam = null)
    {
        if (sourcePlayer == null || teamMember == null || teamMember.Id == sourcePlayer.Id)
            return false;

        sourceTeam ??= TeamManager.Instance.GetTeamByObjId(sourcePlayer.ObjId);
        if (sourceTeam == null || sourceTeam.Members.All(member => member?.Character?.Id != teamMember.Id))
            return false;

        rangeOrigin ??= sourcePlayer.Transform;
        if (sourcePlayer.Transform.WorldId != rangeOrigin.WorldId ||
            sourcePlayer.Transform.InstanceId != rangeOrigin.InstanceId)
            return false;

        return IsEligibleRecipient(teamMember, rangeOrigin);
    }

    internal static bool IsEligibleRecipient(ICharacter recipient, Transform rangeOrigin)
    {
        if (recipient is not Character { IsOnline: true } || rangeOrigin == null)
            return false;

        if (recipient.Transform.WorldId != rangeOrigin.WorldId ||
            recipient.Transform.InstanceId != rangeOrigin.InstanceId)
            return false;

        return MathUtil.CalculateDistance(rangeOrigin.World.Position, recipient.Transform.World.Position, true) <=
               AppConfiguration.Instance.World.QuestTeamShareRange;
    }
}
