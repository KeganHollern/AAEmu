using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.InstantGame;

public partial class InstantGame
{
    private void AddScore(InstantCorps corps, InstantGameTeamMember scorer, int score)
    {
        scorer.Score += score;
        var points = new SCInstantGameAddPointPacket(_zoneInstanceId, corps, score,
            _corps1Result.Score, _corps2Result.Score, scorer.Character.Name);

        var target = _battlefield.RuleSet.VictoryScore;
        if (target > 0 && scorer.Corps.Score >= target)
            _ = FinishGame(BattlefieldEndingReason.Score, points);
        else
            BroadcastPacket(points);
    }

    public void OnKill(object sender, OnKillArgs args)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (args.Killer is not Character killer)
                return;

            if (_finished || _announcedAt is not { } announcement)
                return;
            var elapsed = _clock.GetElapsedTime(announcement) - StartCountdown;
            if (elapsed < TimeSpan.Zero)
                return;
            if (elapsed >= PlayingTime)
            {
                _ = EndGame();
                return;
            }

            if (killer.CurrentInstantGame != this || !_members.TryGetValue(killer, out var memberKiller))
                return;
            if (args.Victim is Npc npc)
            {
                if (_battlefield.RuleSet.TopNpcKillsHonor > 0 && npc.ParentWorld == _world &&
                    killer.ParentWorld == _world && killer.GetRelationStateTo(npc) == RelationState.Hostile)
                    memberKiller.EnemyNpcKills++;
                return;
            }
            if (args.Victim is not Character victim || victim.CurrentInstantGame != this ||
                !_members.TryGetValue(victim, out var memberVictim) ||
                memberKiller.Corps == memberVictim.Corps)
                return;
            memberKiller.Kills++;
            memberKiller.Killstreak++;

            memberVictim.Deaths++;
            memberVictim.Killstreak = 0;

            BroadcastPacket(new SCInstantGameKillPacket(_zoneInstanceId, killer, victim, _characterCorps[killer], _characterCorps[victim], (sbyte)memberKiller.Killstreak, memberKiller.Corps.TotalKills, memberVictim.Corps.TotalDeaths));
            killer.SendPacket(new SCInstantGameKillstreakPacket(_zoneInstanceId, (sbyte)memberKiller.Killstreak, 0, true));
            var corps = _characterCorps[killer];
            if (_battlefield.RuleSet.EnemyPlayerKillScore != 0)
                AddScore(corps, memberKiller, _battlefield.RuleSet.EnemyPlayerKillScore);

            var assistScore = _battlefield.RuleSet.EnemyPlayerDamageAssistScore;
            if (_battlefield.Id == (uint)InstantGameType.DrillCamp && assistScore != 0)
            {
                var assistants = victim.CollectAssists(killer, damageOnly: true);
                foreach (var teammate in _members.Values)
                {
                    if (teammate.Corps != memberKiller.Corps || !teammate.Character.IsOnline ||
                        teammate.Character.CurrentInstantGame != this ||
                        !assistants.Contains(teammate.Character.Id))
                        continue;

                    teammate.Assists++;
                    AddScore(corps, teammate, assistScore);
                }
            }

            if (!_finished)
                _ = ResetAfterKillDelay(killer, victim, memberKiller, memberVictim, _characterCorps[victim]);
        }
    }

    private async Task ResetAfterKillDelay(Character killer, Character victim, InstantGameTeamMember expectedKiller,
        InstantGameTeamMember expectedVictim, InstantCorps victimCorps)
    {
        await Task.Delay(_battlefield.RuleSet.ResurrectionDelay, _clock);
        ResetAfterKill(killer, victim, expectedKiller, expectedVictim, victimCorps);
    }

    private bool CanResetMember(Character character, InstantGameTeamMember expected)
    {
        if (character.CurrentInstantGame != this || !_members.TryGetValue(character, out var current) || current != expected)
            return false;
        if (PrisonerAccess.CanEnter(character, true))
            return true;
        CancelAdmission(character);
        return false;
    }

    internal void ResetAfterKill(Character killer, Character victim, InstantGameTeamMember expectedKiller,
        InstantGameTeamMember expectedVictim, InstantCorps victimCorps)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_finished)
                return;

            if (CanResetMember(victim, expectedVictim))
            {
                var spawn = victimCorps == InstantCorps.Corps1 ? _battlefield.Spawns.Corps1Spawn : _battlefield.Spawns.Corps2Spawn;
                victim.BroadcastPacket(new SCCharacterResurrectedPacket(victim.ObjId, spawn.X, spawn.Y, spawn.Z, spawn.RotationZ), true);
                victim.ResetAllSkillCooldowns(false);
                victim.Buffs.RemoveAllEffects();
                victim.Hp = victim.MaxHp;
                victim.Mp = victim.MaxMp;
                victim.MoveToRespawn(new Vector3(spawn.X, spawn.Y, spawn.Z));
                victim.BroadcastPacket(new SCUnitPointsPacket(victim.ObjId, victim.Hp, victim.Mp), true);
            }

            if (CanResetMember(killer, expectedKiller) && _battlefield.Id == (uint)InstantGameType.Gladiator)
            {
                var spawn = victimCorps == InstantCorps.Corps1 ? _battlefield.Spawns.Corps2Spawn : _battlefield.Spawns.Corps1Spawn;
                if (killer.Hp == 0)
                    killer.BroadcastPacket(new SCCharacterResurrectedPacket(killer.ObjId, spawn.X, spawn.Y, spawn.Z, spawn.RotationZ), true);
                else
                {
                    killer.Mates?.PrepareForTeleport();
                    killer.SendPacket(new SCTeleportUnitPacket(0, 0, spawn.X, spawn.Y, spawn.Z, spawn.RotationZ));
                }
                killer.Hp = killer.MaxHp;
                killer.Mp = killer.MaxMp;
                killer.MoveToRespawn(new Vector3(spawn.X, spawn.Y, spawn.Z));
                killer.BroadcastPacket(new SCUnitPointsPacket(killer.ObjId, killer.Hp, killer.Mp), true);
                killer.Buffs.RemoveAllEffects();
                killer.ResetAllSkillCooldowns(false);
            }
        }
    }

}
