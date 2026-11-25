using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Crime;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.InstantGame;

public partial class InstantGame
{
    private void AddScore(InstantCorps corps, InstantGameTeamMember scorer, int score)
    {
        scorer.Score += score;
        BroadcastPacket(new SCInstantGameAddPointPacket(_zoneInstanceId, corps, score,
            _corps1Result.Score, _corps2Result.Score, scorer.Character.Name));

        var target = _battlefield.RuleSet.VictoryScore;
        if (target > 0 && scorer.Corps.Score >= target)
            _ = FinishGame(BattlefieldEndingReason.Score);
    }

    public void OnKill(object sender, OnKillArgs args)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (args.Killer is not Character killer || args.Victim is not Character victim)
                return;

            if (_finished || _startedAt is not { } start)
                return;
            if (_clock.GetElapsedTime(start) >= PlayingTime)
            {
                _ = EndGame();
                return;
            }

            if (killer.CurrentInstantGame != this || victim.CurrentInstantGame != this ||
                !_members.TryGetValue(killer, out var memberKiller) || !_members.TryGetValue(victim, out var memberVictim) ||
                memberKiller.Corps == memberVictim.Corps)
                return;
            memberKiller.Kills++;
            memberKiller.Killstreak++;

            memberVictim.Deaths++;
            memberVictim.Killstreak = 0;

            BroadcastPacket(new SCInstantGameKillPacket(_zoneInstanceId, killer, victim, _characterCorps[killer], _characterCorps[victim], (sbyte)memberKiller.Killstreak, memberKiller.Corps.TotalKills, memberVictim.Corps.TotalKills));
            killer.SendPacket(new SCInstantGameKillstreakPacket(_zoneInstanceId, (sbyte)memberKiller.Killstreak, 0, true));
            if (_battlefield.RuleSet.EnemyPlayerKillScore != 0)
                AddScore(_characterCorps[killer], memberKiller, _battlefield.RuleSet.EnemyPlayerKillScore);

            if (!_finished)
                _ = ResetAfterKillDelay(killer, victim, memberKiller, memberVictim, _characterCorps[victim]);
        }
    }

    private async Task ResetAfterKillDelay(Character killer, Character victim, InstantGameTeamMember expectedKiller,
        InstantGameTeamMember expectedVictim, InstantCorps victimCorps)
    {
        await Task.Delay(TimeSpan.FromSeconds(6), _clock);
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
                killer.BroadcastPacket(new SCUnitPointsPacket(killer.ObjId, killer.Hp, killer.Mp), true);
                killer.Buffs.RemoveAllEffects();
                killer.ResetAllSkillCooldowns(false);
            }
        }
    }

}
