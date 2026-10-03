using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Tasks.Mate;
using Task = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.Game.Models.Game.Units;

public class MatePassengerInfo
{
    public uint _objId;
    public AttachUnitReason _reason;
}

internal enum MateLifecycleState
{
    Created,
    Spawning,
    Spawned,
    SpawnFailed,
    Removing,
    Removed
}

public sealed partial class Mate : Unit
{
    private readonly object _lifecycleLock = new();
    private MateLifecycleState _lifecycleState = MateLifecycleState.Created;
    private bool _worldSpawnAttempted;

    public override UnitTypeFlag TypeFlag { get => UnitTypeFlag.Mate; }
    public override BaseUnitType BaseUnitType => BaseUnitType.Mate;
    public NpcTemplate Template { get; set; }
    public uint OwnerObjId { get; set; }
    public Dictionary<AttachPointKind, MatePassengerInfo> Passengers { get; }
    public override float Scale => Template.Scale;
    /// <summary>
    /// The item that this summon is from
    /// </summary>
    public ulong ItemId { get; set; }
    public byte UserState { get; set; }
    public int Experience { get; set; }
    public int Mileage { get; set; }
    public uint SpawnDelayTime { get; set; }
    public bool IsTemporarySummon { get; set; }
    public bool DespawnOnCreatorDeath { get; set; }
    public List<uint> Skills { get; set; }
    public MateDb DbInfo { get; set; }
    public Task MateXpUpdateTask { get; set; }
    public bool IsMaxLevel => Level >= ExperienceManager.Instance.MaxMateLevel;

    #region Attributes

    [UnitAttribute(UnitAttribute.Str)]
    public int Str
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.Str);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var result = formula.Evaluate(parameters);
            var res = (int)result;
            //foreach (var item in Inventory.Equip)
            //    if (item is EquipItem equip)
            //        res += equip.Str;
            return (int)CalculateWithBonuses(res, UnitAttribute.Str);
        }
    }

    [UnitAttribute(UnitAttribute.Dex)]
    public int Dex
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.Dex);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            //foreach (var item in Inventory.Equip)
            //    if (item is EquipItem equip)
            //        res += equip.Dex;
            return (int)CalculateWithBonuses(res, UnitAttribute.Dex);
        }
    }

    [UnitAttribute(UnitAttribute.Sta)]
    public int Sta
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.Sta);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            //foreach (var item in Inventory.Equip)
            //    if (item is EquipItem equip)
            //        res += equip.Sta;
            return (int)CalculateWithBonuses(res, UnitAttribute.Sta);
        }
    }

    [UnitAttribute(UnitAttribute.Int)]
    public int Int
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.Int);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            //foreach (var item in Inventory.Equip)
            //    if (item is EquipItem equip)
            //        res += equip.Int;
            return (int)CalculateWithBonuses(res, UnitAttribute.Int);
        }
    }

    [UnitAttribute(UnitAttribute.Spi)]
    public int Spi
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.Spi);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            //foreach (var item in Inventory.Equip)
            //    if (item is EquipItem equip)
            //        res += equip.Spi;
            return (int)CalculateWithBonuses(res, UnitAttribute.Spi);
        }
    }

    [UnitAttribute(UnitAttribute.Fai)]
    public int Fai
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.Fai);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.Fai);
        }
    }

    [UnitAttribute(UnitAttribute.MaxHealth)]
    public override int MaxHp
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.MaxHealth);
            var mateKindVariable = FormulaManager.Instance.GetUnitVariable(formula.Id,
                UnitFormulaVariableType.MateKind, (uint)Template.MateKindId);

            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai,
                ["mate_kind"] = mateKindVariable
            };
            var res = Math.Truncate(formula.Evaluate(parameters));

            res = CalculateWithBonuses(res, UnitAttribute.MaxHealth);

            return (int)res;
        }
    }

    [UnitAttribute(UnitAttribute.HealthRegen)]
    public override int HpRegen
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.HealthRegen);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai,
                ["mate_kind"] = Template.MateKindId
            };
            var res = (int)formula.Evaluate(parameters);
            res += Spi / 10;
            return (int)CalculateWithBonuses(res, UnitAttribute.HealthRegen);
        }
    }

    [UnitAttribute(UnitAttribute.PersistentHealthRegen)]
    public override int PersistentHpRegen
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.PersistentHealthRegen);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai,
                ["mate_kind"] = Template.MateKindId
            };
            var res = (int)formula.Evaluate(parameters);
            res /= 5; // TODO ...
            return (int)CalculateWithBonuses(res, UnitAttribute.PersistentHealthRegen);
        }
    }

    [UnitAttribute(UnitAttribute.MaxMana)]
    public override int MaxMp
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.MaxMana);
            var mateKindVariable = FormulaManager.Instance.GetUnitVariable(formula.Id,
                UnitFormulaVariableType.MateKind, (uint)Template.MateKindId);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai,
                ["mate_kind"] = mateKindVariable
            };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.MaxMana);
        }
    }

    [UnitAttribute(UnitAttribute.ManaRegen)]
    public override int MpRegen
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.ManaRegen);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai,
                ["mate_kind"] = Template.MateKindId
            };
            var res = (int)formula.Evaluate(parameters);
            res += Spi / 10;
            return (int)CalculateWithBonuses(res, UnitAttribute.ManaRegen);
        }
    }

    [UnitAttribute(UnitAttribute.PersistentManaRegen)]
    public override int PersistentMpRegen
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.PersistentManaRegen);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai,
                ["mate_kind"] = Template.MateKindId
            };
            var res = (int)formula.Evaluate(parameters);
            res /= 5; // TODO ...
            return (int)CalculateWithBonuses(res, UnitAttribute.PersistentManaRegen);
        }
    }

    // [UnitAttribute(UnitAttribute.Dps)]
    public override float LevelDps
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.LevelDps);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai,
                ["ab_level"] = Level
            };

            var res = formula.Evaluate(parameters);
            return (float)res;
        }
    }

    [UnitAttribute(UnitAttribute.MeleeDpsInc)]
    public override int DpsInc
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.MeleeDpsInc);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai
            };
            var res = formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.MeleeDpsInc);
        }
    }

    [UnitAttribute(UnitAttribute.SpellDpsInc)]
    public override int MDpsInc
    {
        get
        {
            var formula =
                FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.SpellDpsInc);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai
            };
            var res = formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.SpellDpsInc);
        }
    }

    [UnitAttribute(UnitAttribute.Armor)]
    public override int Armor
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.Armor);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai
            };
            var res = Math.Truncate(formula.Evaluate(parameters));
            return (int)CalculateWithBonuses(res, UnitAttribute.Armor);
        }
    }

    [UnitAttribute(UnitAttribute.MagicResist)]
    public override int MagicResistance
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Mate, UnitFormulaKind.MagicResist);
            var parameters = new Dictionary<string, double>
            {
                ["level"] = Level,
                ["str"] = Str,
                ["dex"] = Dex,
                ["sta"] = Sta,
                ["int"] = Int,
                ["spi"] = Spi,
                ["fai"] = Fai
            };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.MagicResist);
        }
    }
    #endregion

    public Mate()
    {
        Skills = [];
        Passengers = [];
        Equipment = new MateEquipmentContainer(0, SlotType.EquipmentMate, false, this);

        // TODO: Spawn this with the correct amount of seats depending on the template
        // 2 seats by default
        Passengers.Add(AttachPointKind.Driver, new MatePassengerInfo { _objId = 0, _reason = 0 });
        Passengers.Add(AttachPointKind.Passenger0, new MatePassengerInfo { _objId = 0, _reason = 0 });
    }

    internal MateLifecycleState LifecycleState
    {
        get
        {
            lock (_lifecycleLock)
                return _lifecycleState;
        }
    }

    /// <summary>
    /// Runs the externally visible spawn sequence while excluding removal for this exact mate.
    /// A removal reserved before this method starts wins and prevents a late spawn with released IDs.
    /// </summary>
    internal bool TryRunSpawnLifecycle(Action beforeWorldSpawnAction, Action worldSpawnAction)
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleState != MateLifecycleState.Created)
                return false;

            _lifecycleState = MateLifecycleState.Spawning;
            try
            {
                beforeWorldSpawnAction();
                _worldSpawnAttempted = true;
                worldSpawnAction();
                if (_lifecycleState != MateLifecycleState.Spawning)
                    return false;

                _lifecycleState = MateLifecycleState.Spawned;
                return true;
            }
            catch
            {
                if (_lifecycleState != MateLifecycleState.Removed)
                    _lifecycleState = MateLifecycleState.SpawnFailed;
                throw;
            }
        }
    }

    /// <summary>
    /// Runs cleanup once for this exact mate and waits for an in-progress spawn to finish first.
    /// </summary>
    internal bool TryRunDespawnLifecycle(Action<bool> despawnAction)
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleState is MateLifecycleState.Removing or MateLifecycleState.Removed)
                return false;

            // A packet-send failure happens before the object enters the world and must not
            // call Delete(). A failure inside Spawn() may have partially registered the object,
            // so cleanup must attempt Delete() in that case.
            var shouldDeleteWorldObject = _worldSpawnAttempted;
            _lifecycleState = MateLifecycleState.Removing;
            lock (AttachmentSyncRoot)
                AttachmentsRetired = true;
            try
            {
                despawnAction(shouldDeleteWorldObject);
                return true;
            }
            finally
            {
                _lifecycleState = MateLifecycleState.Removed;
            }
        }
    }

    /// <summary>
    /// Update the Item Data if it was summoned by an item
    /// </summary>
    private void UpdateMateItemData()
    {
        if (ItemId > 0)
        {
            var item = ItemManager.Instance.GetItemByItemId(ItemId);
            if (item is SummonMate mateItem)
            {
                mateItem.DetailMateExp = Experience;
                mateItem.DetailLevel = Level;
                mateItem.IsDirty = true;
            }
        }
    }

    /// <summary>
    /// Adds exp to this Mate and checks for level ups
    /// </summary>
    /// <param name="expDelta">The change in experience.</param>
    /// <remarks>This method does nothing if <paramref name="expDelta"/> is negative or zero. Only a positive increase in experience can be applied.</remarks>
    public void AddExp(int expDelta)
    {
        if (expDelta <= 0)
            return;
        if (IsMaxLevel)
            return;

        expDelta = (int)Math.Round(AppConfiguration.Instance.World.ExpRate * expDelta);
        var newExperience = Experience + expDelta;
        var newLevel = ExperienceManager.Instance.GetLevelFromExp(newExperience, Level, out var overflow, true);
        var leveledUp = newLevel > Level;

        // Prevent overflow - cap the experience at the amount for the highest level
        if (newLevel >= ExperienceManager.Instance.MaxMateLevel)
        {
            newExperience -= overflow;
        }

        Experience = newExperience;
        Level = newLevel;

        UpdateMateItemData();
        DbInfo.Xp = Experience;
        DbInfo.Level = Level;

        var owner = WorldManager.Instance.GetCharacterByObjId(OwnerObjId);
        owner.SendPacket(new SCExpChangedPacket(ObjId, expDelta, false));

        if (leveledUp)
        {
            BroadcastPacket(new SCLevelChangedPacket(ObjId, Level), true);
            owner.Achievements?.UpdateMaximum(CharRecordKind.PetLevel, Template.Id, 0, Level);
            // Notify owner of the level up event
            owner.Events.OnMateLevelUp(this, new OnMateLevelUpArgs());
            //StartRegen();
        }
    }

    public override void AddVisibleObject(Character character)
    {
        base.AddVisibleObject(character);

        character.SendPacket(new SCUnitStatePacket(this));
        character.SendPacket(new SCMateStatePacket(ObjId));
        character.SendPacket(new SCUnitPointsPacket(ObjId, Hp, Mp));
        // TODO: Maybe let base handle this ?
        foreach (var ati in Passengers)
        {
            if (ati.Value._objId > 0)
            {
                var player = WorldManager.Instance.GetCharacterByObjId(ati.Value._objId);
                if (player != null)
                    character.SendPacket(new SCUnitAttachedPacket(player.ObjId, ati.Key, ati.Value._reason, ObjId));
            }
        }
    }

    public override void RemoveVisibleObject(Character character)
    {
        base.RemoveVisibleObject(character);

        character.SendPacket(new SCUnitsRemovedPacket([ObjId]));
    }

    public override int DoFallDamage(ushort fallVel)
    {
        // Death cleanup can detach passengers. Capture them before applying the impact.
        var riders = Passengers.Select(passenger =>
            (Seat: passenger.Key, ObjId: passenger.Value._objId)).ToList();
        var wasDowned = IsDowned;
        var fallDmg = base.DoFallDamage(fallVel);
        if ((fallDmg > 0 && Hp <= 0) || (!wasDowned && IsDowned))
        {
            // A lethal mount impact also reaches its riders, with their own immunity checks.
            for (var i = riders.Count - 1; i >= 0; i--)
            {
                var pos = riders[i].Seat;
                var rider = WorldManager.Instance.GetCharacterByObjId(riders[i].ObjId);
                if (rider != null)
                {
                    rider.DoFallDamage(fallVel);
                    if (rider.Hp <= 0)
                        rider.ParentWorld.MateManager.UnMountMate(rider, TlId, pos, AttachUnitReason.SlaveBinding);
                }
            }
        }

        return fallDmg;
    }

    protected override void RegenTick(TimeSpan delta)
    {
        if (!NeedsRegen)
        {
            return;
        }
        if (IsDead)
        {
            var riders = Passengers.ToList();
            for (var i = riders.Count - 1; i >= 0; i--)
            {
                var pos = riders[i].Key;
                var rider = WorldManager.Instance.GetCharacterByObjId(riders[i].Value._objId);
                rider?.ParentWorld.MateManager.UnMountMate(rider, TlId, pos, AttachUnitReason.None);
            }
            return;
        }

        var oldHp = Hp;

        if (IsInBattle)
        {
            Hp += PersistentHpRegen;
            Mp += PersistentMpRegen;
        }
        else
        {
            Hp += HpRegen;
            Mp += MpRegen;
        }

        Hp = Math.Min(Hp, MaxHp);
        Mp = Math.Min(Mp, MaxMp);
        BroadcastPacket(new SCUnitPointsPacket(ObjId, Hp, Mp), false);
        PostUpdateCurrentHp(this, oldHp, Hp, KillReason.Unknown);
    }

    public void StartUpdateXp(Character owner)
    {
        if (MateXpUpdateTask != null)
        {
            return;
        }
        if (IsMaxLevel)
            return;
        MateXpUpdateTask = new MateXpUpdateTask(owner, this);
        TaskManager.Instance.Schedule(MateXpUpdateTask, TimeSpan.FromSeconds(60));
        //Logger.Trace("[StartUpdateXp] The current timer has been started...");
    }

    public void StopUpdateXp()
    {
        MateXpUpdateTask?.Cancel();
        MateXpUpdateTask = null;
        //Logger.Trace("[StopUpdateXp] The current timer has been canceled...");
    }

    public override void OnZoneChange(uint lastZoneKey, uint newZoneKey)
    {
        base.OnZoneChange(lastZoneKey, newZoneKey); // Unit

        if (Passengers.Count <= 0)
        {
            return;
        }

        foreach (var (_, passengerInfo) in Passengers)
        {
            var passenger = WorldManager.Instance.GetCharacterByObjId(passengerInfo._objId);
            passenger?.OnZoneChange(lastZoneKey, newZoneKey);
        }
    }

    public override Character GetOwnerCharacter()
    {
        var ownerObject = OwnerObjId > 0 ? ParentWorld.GetGameObject(OwnerObjId) as BaseUnit : null;
        return ownerObject?.GetOwnerCharacter();
    }
}
