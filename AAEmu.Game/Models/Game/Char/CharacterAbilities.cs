using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Skills;
using MySql.Data.MySqlClient;

namespace AAEmu.Game.Models.Game.Char;

public class CharacterAbilities
{
    public Dictionary<AbilityType, Ability> Abilities { get; set; }
    public Character Owner { get; set; }

    public CharacterAbilities(Character owner)
    {
        Owner = owner;
        Abilities = [];
        for (var i = 1; i < 11; i++)
        {
            var id = (AbilityType)i;
            Abilities[id] = new Ability(id);
        }
    }

    public IEnumerable<Ability> Values => Abilities.Values;

    public void SetAbility(AbilityType id, byte order)
    {
        Abilities[id].Order = order;
    }

    public List<AbilityType> GetActiveAbilities()
    {
        var list = new List<AbilityType>();
        if (Owner.Ability1 != AbilityType.None)
            list.Add(Owner.Ability1);
        if (Owner.Ability2 != AbilityType.None)
            list.Add(Owner.Ability2);
        if (Owner.Ability3 != AbilityType.None)
            list.Add(Owner.Ability3);
        return list;
    }

    public void AddExp(AbilityType type, int exp)
    {
        lock (Owner.StorePurchaseSyncRoot)
            AddExpLocked(type, exp);
    }

    private void AddExpLocked(AbilityType type, int exp)
    {
        // TODO SCAbilityExpChangedPacket
        if (type != AbilityType.None)
        {
            Abilities[type].Exp += exp;
            Owner.Achievements?.UpdateAbilityLevel(type, GetAbilityLevel(type));
        }
    }

    public void AddActiveExp(int exp)
    {
        lock (Owner.StorePurchaseSyncRoot)
            AddActiveExpLocked(exp);
    }

    private void AddActiveExpLocked(int exp)
    {
        // TODO SCExpChangedPacket
        var maxLevelExp = ExperienceManager.Instance.GetExpForLevel(ExperienceManager.Instance.MaxPlayerLevel);
        if (Owner.Ability1 != AbilityType.None)
        {
            Abilities[Owner.Ability1].Exp = Math.Min(Abilities[Owner.Ability1].Exp + exp, maxLevelExp);
            Owner.Achievements?.UpdateAbilityLevel(Owner.Ability1, GetAbilityLevel(Owner.Ability1));
        }
        if (Owner.Ability2 != AbilityType.None)
        {
            Abilities[Owner.Ability2].Exp = Math.Min(Abilities[Owner.Ability2].Exp + exp, maxLevelExp);
            Owner.Achievements?.UpdateAbilityLevel(Owner.Ability2, GetAbilityLevel(Owner.Ability2));
        }
        if (Owner.Ability3 != AbilityType.None)
        {
            Abilities[Owner.Ability3].Exp = Math.Min(Abilities[Owner.Ability3].Exp + exp, maxLevelExp);
            Owner.Achievements?.UpdateAbilityLevel(Owner.Ability3, GetAbilityLevel(Owner.Ability3));
        }
    }

    public void Swap(AbilityType oldAbilityId, AbilityType abilityId)
    {
        lock (Owner.StorePurchaseSyncRoot)
            SwapLocked(oldAbilityId, abilityId);
    }

    private void SwapLocked(AbilityType oldAbilityId, AbilityType abilityId)
    {
        Owner.Skills.Reset(oldAbilityId);
        var changed = false;
        if (Owner.Ability1 == oldAbilityId)
        {
            Owner.Ability1 = abilityId;
            Abilities[abilityId].Order = 0;
            changed = true;
        }
        else if (Owner.Ability2 == oldAbilityId)
        {
            Owner.Ability2 = abilityId;
            Abilities[abilityId].Order = 1;
            changed = true;

            //This sets are current ability level to match ability1 since its suppost to be in sync
            if (oldAbilityId == AbilityType.None)
            {
                Abilities[Owner.Ability2].Exp = Abilities[Owner.Ability1].Exp;
            }
        }
        else if (Owner.Ability3 == oldAbilityId)
        {
            Owner.Ability3 = abilityId;
            Abilities[abilityId].Order = 2;
            changed = true;

            if (oldAbilityId == AbilityType.None)
            {
                Abilities[Owner.Ability3].Exp = Abilities[Owner.Ability1].Exp;

                //every unchosen ability is default level 10 besides are selected ones since spillover exp can unsync character exp with skill exp
                var c = GetActiveAbilities();
                for (var i = 1; i < Abilities.Count; i++)
                {
                    var id = (AbilityType)i;
                    if (!c.Contains(Abilities[id].Id))
                    {
                        Abilities[id].Exp = 42000;
                    }
                }
            }
        }

        if (oldAbilityId != AbilityType.None)
            Abilities[oldAbilityId].Order = 255;

        foreach (var ability in Abilities.Values)
            Owner.Achievements?.UpdateAbilityLevel(ability.Id, GetAbilityLevel(ability.Id));

        if (changed && oldAbilityId != abilityId)
            Owner.Achievements?.Increment(CharRecordKind.AbilityChange, 0, 0);

        Owner.BroadcastPacket(new SCAbilitySwappedPacket(Owner.ObjId, oldAbilityId, abilityId), true);
    }

    public void Load(MySqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM abilities WHERE `owner` = @owner";
            command.Parameters.AddWithValue("@owner", Owner.Id);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var ability = new Ability
                    {
                        Id = (AbilityType)reader.GetByte("id"),
                        Exp = reader.GetInt32("exp")
                    };
                    if (ability.Id == Owner.Ability1)
                        ability.Order = 0;
                    if (ability.Id == Owner.Ability2)
                        ability.Order = 1;
                    if (ability.Id == Owner.Ability3)
                        ability.Order = 2;
                    Abilities[ability.Id] = ability;
                }
            }
        }
    }

    public void Save(MySqlConnection connection, MySqlTransaction transaction)
    {
        foreach (var ability in Abilities.Values)
        {
            using (var command = connection.CreateCommand())
            {
                command.Connection = connection;
                command.Transaction = transaction;

                command.CommandText = "REPLACE INTO abilities(`id`,`exp`,`owner`) VALUES (@id, @exp, @owner)";
                command.Parameters.AddWithValue("@id", (byte)ability.Id);
                command.Parameters.AddWithValue("@exp", ability.Exp);
                command.Parameters.AddWithValue("@owner", Owner.Id);
                command.ExecuteNonQuery();
            }
        }
    }

    public byte GetAbilityLevel(AbilityType abilityType)
    {
        return Abilities.TryGetValue(abilityType, out var ability) ? ExperienceManager.Instance.GetLevelFromExp(ability.Exp, out _) : (byte)0;
    }
}
