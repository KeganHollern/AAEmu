using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Quests.Acts;

public class QuestActObjZoneKill(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public override bool CountsAsAnObjective => true;
    public override int Count => Math.Max(CountNpc, CountPlayerKill);
    public int CountPlayerKill { get; set; }
    public int CountNpc { get; set; }
    /// <summary>
    /// ZoneGroupId
    /// </summary>
    public uint ZoneId { get; set; }
    public bool TeamShare { get; set; }
    public bool UseAlias { get; set; }
    public uint QuestActObjAliasId { get; set; }
    public int LvlMin { get; set; }
    public int LvlMax { get; set; }
    public bool IsParty { get; set; } // Always the same as TeamShare by the looks of it
    public int LvlMinNpc { get; set; }
    public int LvlMaxNpc { get; set; }
    public FactionsEnum PcFactionId { get; set; }
    public bool PcFactionExclusive { get; set; }
    public FactionsEnum NpcFactionId { get; set; }
    public bool NpcFactionExclusive { get; set; }

    /// <summary>
    /// Checks if either the NPC or PK kill quota has been met
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    /// <returns></returns>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        Logger.Debug($"{QuestActTemplateName}({DetailId}).RunAct: Quest: {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), Zone {ZoneId}, Npc kills x {CountNpc} (Faction {NpcFactionId} Ex {NpcFactionExclusive}, Lv{LvlMinNpc}~{LvlMaxNpc}), PK x {CountPlayerKill} (Faction {PcFactionId} Ex {PcFactionExclusive}, Lv{LvlMin}~{LvlMax}), TeamShare {TeamShare}, IsParty {IsParty}");
        return (CountNpc > 0 && currentObjectiveCount >= CountNpc) || (CountPlayerKill > 0 && currentObjectiveCount >= CountPlayerKill);
    }

    public override void InitializeAction(Quest quest, QuestAct questAct)
    {
        base.InitializeAction(quest, questAct);
        quest.Owner.Events.OnZoneKill += questAct.OnZoneKill;
    }

    public override void FinalizeAction(Quest quest, QuestAct questAct)
    {
        quest.Owner.Events.OnZoneKill -= questAct.OnZoneKill;
        base.FinalizeAction(quest, questAct);
    }

    public override void OnZoneKill(QuestAct questAct, object sender, OnZoneKillArgs args)
    {
        if (questAct.Id != ActId)
            return;

        var player = questAct.QuestComponent.Parent.Parent.Owner;

        // If Party kills is not allowed, only allow kills from self
        if (!IsParty && args.Killer.Id != player.Id)
            return;

        // Ignore if victim is the killer (e.g. death from fall-damage)
        // TODO: Verify if DoT debuff effects apply the killer setting correctly
        if (args.Killer.ObjId == args.Victim.ObjId)
            return;

        var victimPc = args.Victim as Character;
        var victimNpc = args.Victim as Npc;

        var valid = false;

        if (CountNpc > 0 && victimNpc != null)
        {
            // NPC kills
            valid = MatchesKillTarget(victimNpc, NpcFactionId, NpcFactionExclusive, LvlMinNpc, LvlMaxNpc);
        }

        if (CountPlayerKill > 0 && victimPc != null)
        {
            // Player kills
            valid = MatchesKillTarget(victimPc, PcFactionId, PcFactionExclusive, LvlMin, LvlMax);
        }

        if (valid)
        {
            // TODO: Check if this would actually need 2 objective counters or not
            AddObjective(questAct, 1);

            // Handle Team sharing (if needed)
            if (TeamShare)
            {
                // Delegate also to other team members
                var myTeam = TeamManager.Instance.GetTeamByObjId(player.ObjId);
                if (myTeam != null)
                {
                    foreach (var teamMember in myTeam.Members)
                    {
                        if (teamMember == null)
                            continue;
                        // Skip self
                        if (teamMember.Character.Id == player.Id)
                            continue;

                        // TODO: Range check?

                        // Directly call OnZoneKill on team members to avoid loops/duplicates
                        teamMember.Character.Events.OnZoneKill(sender, args);
                    }
                }
            }
        }
    }

    private static bool MatchesKillTarget(Unit victim, FactionsEnum factionId, bool factionExclusive, int levelMin, int levelMax)
    {
        var matchesFaction = factionId == FactionsEnum.Invalid ||
                             (victim.Faction != null && (factionExclusive
                                 ? victim.Faction.Id != factionId
                                 : victim.Faction.Id == factionId));
        var matchesLevel = (levelMin <= 0 || victim.Level >= levelMin) &&
                           (levelMax <= 0 || victim.Level <= levelMax);
        return matchesFaction && matchesLevel;
    }
}
