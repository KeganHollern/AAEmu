using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Gimmicks;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.NpcGroup;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.SkillControllers;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Static;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.Game.Utils;
using static AAEmu.Game.Models.Game.Units.Buffs;

namespace AAEmu.Game.Models.Game.Units;

public class Unit : BaseUnit, IUnit
{
    public virtual UnitTypeFlag TypeFlag { get => UnitTypeFlag.None; }
    public virtual BaseUnitType BaseUnitType { get; set; } = BaseUnitType.Invalid;

    public virtual UnitEvents Events { get; }
    public uint ModelId { get; set; }
    public uint ActiveMountSkillBuffId { get; internal set; }
    internal object AttachmentSyncRoot { get; } = new();
    // Access under AttachmentSyncRoot. Retirement precedes passenger cleanup.
    internal bool AttachmentsRetired { get; set; }
    private TurretAimState _turretAim = new(0f, 0f);
    internal TurretAimState TurretAim
    {
        get => Volatile.Read(ref _turretAim);
        set => Volatile.Write(ref _turretAim, value);
    }
    public GameStanceType CollisionStance { get; set; } = GameStanceType.Combat;
    private SkillController _activeSkillController;
    public SkillController ActiveSkillController
    {
        get => _activeSkillController;
        set
        {
            if (ReferenceEquals(_activeSkillController, value))
                return;
            FallMovement.Reset();
            _activeSkillController = value;
        }
    }

    // Set after a knockback/impulse so AI movement is suppressed until expiry,
    // giving the displacement animation time to play on clients.
    public DateTime? DisplacedUntil { get; set; }

    public override float ModelSize
    {
        get
        {
            return (ModelManager.Instance.GetActorModel(ModelId)?.Radius ?? 0) * Scale;
        }
    }

    public virtual float BaseMoveSpeed
    {
        get
        {
            return 1f;
        }
    }

    public byte Level { get; set; }

    private int _deathHandled;
    public virtual int Hp
    {
        get;
        set
        {
            if (field <= 0 && value > 0)
                Interlocked.Exchange(ref _deathHandled, 0);
            field = value;
        }
    }

    public int Hpp
    {
        get
        {
            if (MaxHp <= 0)
                return 0;
            return Math.Clamp((int)Math.Ceiling(Hp * 100f / MaxHp), 0, 100);
        }
    }

    public DateTime LastCombatActivity { get; set; }

    protected bool _isUnderWater;

    public virtual bool IsUnderWater
    {
        get => _isUnderWater;
        set => _isUnderWater = value;
    }

    /// <summary>
    /// List of values in the range of 0 -> 100
    /// </summary>
    protected List<int> HpTriggerPointsPercent { get; set; } = [];

    #region Attributes

    [UnitAttribute(UnitAttribute.ExpMul)]
    public double ExperienceMultiplier => ExperienceModifierGameData.Instance.Clamp(UnitAttribute.ExpMul,
        CalculateWithBonuses(100d, UnitAttribute.ExpMul)) / 100d;

    [UnitAttribute(UnitAttribute.ExpByLaborPowerMul)]
    public double LaborExperienceMultiplier => ExperienceModifierGameData.Instance.Clamp(UnitAttribute.ExpByLaborPowerMul,
        CalculateWithBonuses(100d, UnitAttribute.ExpByLaborPowerMul)) / 100d;

    [UnitAttribute(UnitAttribute.MoveSpeedMul)]
    public virtual float MoveSpeedMul
    {
        get
        {
            // Percent movement modifiers apply to the normal-speed baseline too.
            var value = CalculateBonuses(1000d, UnitAttribute.MoveSpeedMul);
            var bonus = UnitAttributeLimitsGameData.Instance.Clamp(UnitAttribute.MoveSpeedMul, value - 1000d);
            return (float)(1000d + bonus) / 1000f;
        }
    }
    [UnitAttribute(UnitAttribute.GlobalCooldownMul)]
    public virtual float GlobalCooldownMul { get; set; } = 100f;
    [UnitAttribute(UnitAttribute.MaxHealth)]
    public virtual int MaxHp { get; set; }
    [UnitAttribute(UnitAttribute.HealthRegen)]
    public virtual int HpRegen { get; set; }
    [UnitAttribute(UnitAttribute.PersistentHealthRegen)]
    public virtual int PersistentHpRegen { get; set; } = 30;
    public int Mp { get; set; }
    [UnitAttribute(UnitAttribute.MaxMana)]
    public virtual int MaxMp { get; set; }
    [UnitAttribute(UnitAttribute.ManaRegen)]
    public virtual int MpRegen { get; set; }
    [UnitAttribute(UnitAttribute.PersistentManaRegen)]
    public virtual int PersistentMpRegen { get; set; } = 30;
    [UnitAttribute(UnitAttribute.CastingTimeMul)]
    public virtual float CastTimeMul { get; set; } = 1f;
    public virtual float LevelDps { get; set; }
    [UnitAttribute(UnitAttribute.MainhandDps)]
    public virtual int Dps { get; set; }
    [UnitAttribute(UnitAttribute.MeleeDpsInc)]
    public virtual int DpsInc { get; set; }
    [UnitAttribute(UnitAttribute.OffhandDps)]
    public virtual int OffhandDps { get; set; }
    [UnitAttribute(UnitAttribute.RangedDps)]
    public virtual int RangedDps { get; set; }
    [UnitAttribute(UnitAttribute.RangedDpsInc)]
    public virtual int RangedDpsInc { get; set; }
    [UnitAttribute(UnitAttribute.SpellDps)]
    public virtual int MDps { get; set; }
    [UnitAttribute(UnitAttribute.SpellDpsInc)]
    public virtual int MDpsInc { get; set; }
    [UnitAttribute(UnitAttribute.HealDps)]
    public virtual int HDps { get; set; }
    [UnitAttribute(UnitAttribute.HealDpsInc)]
    public virtual int HDpsInc { get; set; }
    [UnitAttribute(UnitAttribute.MeleeAntiMissMul)]
    public virtual float MeleeAccuracy { get; set; } = 100f;
    [UnitAttribute(UnitAttribute.MeleeCritical)]
    public virtual float MeleeCritical { get; set; }
    [UnitAttribute(UnitAttribute.MeleeCriticalBonus)]
    public virtual float MeleeCriticalBonus { get; set; }
    [UnitAttribute(UnitAttribute.MeleeCriticalMul)]
    public virtual float MeleeCriticalMul { get; set; } = 1f;
    [UnitAttribute(UnitAttribute.RangedAntiMiss)]
    public virtual float RangedAccuracy { get; set; } = 100f;
    [UnitAttribute(UnitAttribute.RangedCritical)]
    public virtual float RangedCritical { get; set; }
    [UnitAttribute(UnitAttribute.RangedCriticalBonus)]
    public virtual float RangedCriticalBonus { get; set; }
    [UnitAttribute(UnitAttribute.RangedCriticalMul)]
    public virtual float RangedCriticalMul { get; set; } = 1f;
    [UnitAttribute(UnitAttribute.SpellAntiMiss)]
    public virtual float SpellAccuracy { get; set; } = 100f;
    [UnitAttribute(UnitAttribute.SpellCritical)]
    public virtual float SpellCritical { get; set; }
    [UnitAttribute(UnitAttribute.SpellCriticalBonus)]
    public virtual float SpellCriticalBonus { get; set; }
    [UnitAttribute(UnitAttribute.SpellCriticalMul)]
    public virtual float SpellCriticalMul { get; set; } = 1f;
    [UnitAttribute(UnitAttribute.HealCritical)]
    public virtual float HealCritical { get; set; }
    [UnitAttribute(UnitAttribute.HealCriticalBonus)]
    public virtual float HealCriticalBonus { get; set; }
    [UnitAttribute(UnitAttribute.HealCriticalMul)]
    public virtual float HealCriticalMul { get; set; }
    [UnitAttribute(UnitAttribute.Armor)]
    public virtual int Armor { get; set; }
    [UnitAttribute(UnitAttribute.MagicResist)]
    public virtual int MagicResistance { get; set; }
    [UnitAttribute(UnitAttribute.IgnoreArmor)]
    public virtual int DefensePenetration { get; set; }
    [UnitAttribute(UnitAttribute.MagicPenetration)]
    public virtual int MagicPenetration { get; set; }
    [UnitAttribute(UnitAttribute.Dodge)]
    public virtual float DodgeRate { get; set; }
    [UnitAttribute(UnitAttribute.MeleeParry)]
    public virtual float MeleeParryRate { get; set; }
    [UnitAttribute(UnitAttribute.RangedParry)]
    public virtual float RangedParryRate { get; set; }
    [UnitAttribute(UnitAttribute.Block)]
    public virtual float BlockRate { get; set; }
    [UnitAttribute(UnitAttribute.BattleResist)]
    public virtual int BattleResist { get; set; }
    [UnitAttribute(UnitAttribute.BullsEye)]
    public virtual int BullsEye { get; set; }
    [UnitAttribute(UnitAttribute.Flexibility)]
    public virtual int Flexibility { get; set; }
    [UnitAttribute(UnitAttribute.Facets)]
    public virtual int Facets { get; set; }
    [UnitAttribute(UnitAttribute.MeleeDamageMul)]
    public virtual float MeleeDamageMul { get; set; } = 1.0f;
    [UnitAttribute(UnitAttribute.RangedDamageMul)]
    public virtual float RangedDamageMul { get; set; } = 1.0f;
    [UnitAttribute(UnitAttribute.SpellDamageMul)]
    public virtual float SpellDamageMul { get; set; } = 1.0f;

    [UnitAttribute(UnitAttribute.IncomingHealMul)]
    public virtual float IncomingHealMul { get; set; } = 1.0f;
    [UnitAttribute(UnitAttribute.HealMul)]
    public virtual float HealMul { get; set; } = 1.0f;
    [UnitAttribute(UnitAttribute.IncomingDamageMul)]
    public virtual float IncomingDamageMul { get; set; } = 1f;
    [UnitAttribute(UnitAttribute.IncomingMeleeDamageMul)]
    public virtual float IncomingMeleeDamageMul { get; set; } = 1f;
    [UnitAttribute(UnitAttribute.IncomingRangedDamageMul)]
    public virtual float IncomingRangedDamageMul { get; set; } = 1f;
    [UnitAttribute(UnitAttribute.IncomingSpellDamageMul)]
    public virtual float IncomingSpellDamageMul { get; set; } = 1f;
    [UnitAttribute(UnitAttribute.AggroMul)]
    public float AggroMul
    {
        get => (float)CalculateWithBonuses(100d, UnitAttribute.AggroMul);
    }
    [UnitAttribute(UnitAttribute.IncomingAggroMul)]

    #endregion Attributes

    public float IncomingAggroMul
    {
        get => (float)CalculateWithBonuses(100d, UnitAttribute.IncomingAggroMul);
    }
    public BaseUnit CurrentTarget { get; set; }
    public BaseUnit CurrentInteractionObject { get; set; }
    public virtual byte RaceGender => 0;
    public UnitCustomModelParams ModelParams { get; set; } = new();
    public byte ActiveWeapon { get; set; }
    public bool IdleStatus { get; set; }
    public bool ForceAttack { get; set; }
    public bool Invisible { get; set; }
    public uint OwnerId { get; set; }
    private SkillTask _skillTask;
    public SkillTask SkillTask
    {
        get => Volatile.Read(ref _skillTask);
        set
        {
            var previous = Interlocked.Exchange(ref _skillTask, value);
            if (!ReferenceEquals(previous, value))
                previous?.CastWindow?.TryCancel();
        }
    }
    public SkillTask AutoAttackTask { get; set; }
    public DateTime GlobalCooldown { get; set; }
    internal uint GlobalCooldownDurationMilliseconds { get; set; }
    public bool IsGlobalCooldownDone => GlobalCooldown > DateTime.UtcNow;
    public object GcdLock { get; set; }
    public DateTime SkillLastUsed { get; set; }
    public PlotState ActivePlotState { get; set; }
    public Dictionary<uint, List<Bonus>> Bonuses { get; set; }
    public Dictionary<uint, List<DynamicBonus>> DynamicBonuses { get; set; }
    public UnitCooldowns Cooldowns { get; set; }
    public virtual Expedition Expedition { get; set; }

    private int _isInBattle;
    public bool IsInBattle
    {
        get => Volatile.Read(ref _isInBattle) != 0;
        set
        {
            var state = value ? 1 : 0;
            if (Interlocked.Exchange(ref _isInBattle, state) == state)
                return;
            if (value)
                LastCombatActivity = DateTime.UtcNow;
            else
                BroadcastPacket(new SCCombatClearedPacket(ObjId), true);

            var world = ParentWorld;
            if (world == null)
                return;

            if (value)
                world.Events.OnUnitCombatStart(world, new OnUnitCombatStartArgs { Npc = this });
            else
                world.Events.OnUnitCombatEnd(world, new OnUnitCombatEndArgs { Npc = this });
        }
    }

    public bool IsInDuel { get; set; }
    public bool IsInPatrol { get; set; } // so as not to run the route a second time
    public int SummarizeDamage { get; set; }
    public bool IsAutoAttack { get; set; }
    public ushort TlId { get; set; }
    public ItemContainer Equipment { get; set; }
    public GameConnection Connection { get; set; }

    /// <summary>
    /// Unit巡逻
    /// Unit patrol
    /// 指明Unit巡逻路线及速度、是否正在执行巡逻等行为
    /// Indicates the route and speed of the Unit patrol, whether it is performing patrols, etc.
    /// </summary>
    public Patrol Patrol { get; set; }
    public Simulation Simulation { get; set; }

    public UnitProcs Procs { get; protected set; }
    private readonly HashSet<uint> _equipmentSetBuffs = [];

    public ConcurrentDictionary<uint, Aggro> AggroTable { get; } = [];

    public Unit()
    {
        Events = new UnitEvents();
        GcdLock = new object();
        Bonuses = [];
        DynamicBonuses = [];
        IsInBattle = false;
        Equipment = new EquipmentContainer(0, SlotType.Equipment, false, this);
        ChargeLock = new object();
        Cooldowns = new UnitCooldowns();
        Procs = new UnitProcs(this);
        CharacterTagging = new Tagging(this); //Adding because Tagging works differently than Aggro
    }

    public void SetPosition(float x, float y, float z, sbyte rotationX, sbyte rotationY, sbyte rotationZ)
    {
        SetPosition(x, y, z, (float)MathUtil.ConvertDirectionToRadian(rotationX), (float)MathUtil.ConvertDirectionToRadian(rotationY), (float)MathUtil.ConvertDirectionToRadian(rotationZ));
    }

    public override void SetPosition(float x, float y, float z, float rotationX, float rotationY, float rotationZ)
    {
        var moved = !Transform.World.Position.X.Equals(x) || !Transform.World.Position.Y.Equals(y) || !Transform.World.Position.Z.Equals(z);
        if (moved)
        {
            Events.OnMovement(this, new OnMovementArgs());
        }
        base.SetPosition(x, y, z, rotationX, rotationY, rotationZ);

        // Characters handle underwater/breath in Character.SetPosition.
        // Avoid double-updating IsUnderWater (and packet spam) for players.
        if (this is Character)
            return;

        var worldDrownThreshold = WorldManager.Instance.GetWorld(Transform.InstanceId)?.Template.OceanLevel - 2f ?? 98f;
        if (!IsUnderWater && Transform.World.Position.Z < worldDrownThreshold)
            IsUnderWater = true;
        else if (IsUnderWater && Transform.World.Position.Z > worldDrownThreshold)
            IsUnderWater = false;
    }

    public bool CheckMovedPosition(Vector3 oldPosition)
    {
        var moved = !Transform.World.Position.X.Equals(oldPosition.X) || !Transform.World.Position.Y.Equals(oldPosition.Y) || !Transform.World.Position.Z.Equals(oldPosition.Z);
        if (moved)
        {
            Events.OnMovement(this, new OnMovementArgs());
        }
        if (DisabledSetPosition)
            return moved;

        WorldManager.Instance.AddVisibleObject(this);
        // base.SetPosition(x, y, z, rotationX, rotationY, rotationZ);
        return moved;
    }

    /// <summary>
    /// Make unit take value amount of damage, calls PostReduceCurrentHp() at the end
    /// </summary>
    /// <param name="attacker"></param>
    /// <param name="value"></param>
    /// <param name="killReason"></param>
    public virtual void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage)
    {
        if (killReason != KillReason.Gm && value > 0 && PeaceProtection.PreventsAttack(attacker, this))
            return;

        if (Hp <= 0)
            return;

        var oldHp = Hp;

        var absorptionEffects = Buffs.GetAbsorptionEffects().ToList();
        if (absorptionEffects.Count > 0)
        {
            // Handle damage absorb
            foreach (var absorptionEffect in absorptionEffects)
            {
                value = absorptionEffect.ConsumeCharge(value);
            }
        }

        Hp = GetHealthAfterDamage(attacker, value, killReason);
        SkillCastReactions.OnDamage(this, oldHp - Hp, DateTime.UtcNow);

        BroadcastPacket(new SCUnitPointsPacket(ObjId, Hp, Hp > 0 ? Mp : 0), true);

        PostUpdateCurrentHp(attacker, oldHp, Hp, killReason);
    }

    protected virtual int GetMinimumHealthAfterDamage(BaseUnit attacker, KillReason killReason) => 0;

    protected virtual int GetHealthAfterDamage(BaseUnit attacker, int damage, KillReason killReason)
    {
        return Math.Max(Hp - damage, GetMinimumHealthAfterDamage(attacker, killReason));
    }

    /// <summary>
    /// Called at the end of ReduceCurrentHp() and can be overriden and handles things like death
    /// </summary>
    /// <param name="attackerBase"></param>
    /// <param name="oldHpValue"></param>
    /// <param name="newHpValue"></param>
    /// <param name="killReason"></param>
    public virtual void PostUpdateCurrentHp(BaseUnit attackerBase, int oldHpValue, int newHpValue, KillReason killReason = KillReason.Damage)
    {
        // If Hp triggers are set up, do the calculations for them
        if (HpTriggerPointsPercent.Count > 0)
        {
            var oldHpP = (int)Math.Round(oldHpValue * 100f / MaxHp);
            var newHpP = (int)Math.Round(newHpValue * 100f / MaxHp);

            if (oldHpP > newHpP)
            {
                // Took damage, check downwards
                foreach (var triggerValue in HpTriggerPointsPercent)
                {
                    if (oldHpP > triggerValue && newHpP <= triggerValue)
                    {
                        DoHpChangeTrigger(triggerValue, true, oldHpValue, newHpValue);
                        break;
                    }
                }
            }

            if (oldHpP < newHpP)
            {
                // Healed, check upwards
                foreach (var triggerValue in HpTriggerPointsPercent)
                {
                    if (oldHpP < triggerValue && newHpP >= triggerValue)
                    {
                        DoHpChangeTrigger(triggerValue, false, oldHpValue, newHpValue);
                        break;
                    }
                }
            }
        }

        if (Hp > 0 || oldHpValue <= 0 || newHpValue > 0)
            return;

        DoDie(attackerBase, killReason);
    }

    protected virtual void DoHpChangeTrigger(int triggerValue, bool tookDamage, int oldHpValue, int newHpValue)
    {
        // Do nothing by default
    }

    public virtual void ReduceCurrentMp(BaseUnit unit, int value)
    {
        if (Hp == 0)
        {
            return;
        }

        Mp = Math.Max(Mp - value, 0);
        //if (Mp == 0)
        //{
        //    StopRegen();
        //}

        //else
        //StartRegen();
        BroadcastPacket(new SCUnitPointsPacket(ObjId, Hp, Mp), true);
    }

    public virtual void DoDie(BaseUnit killer, KillReason killReason)
    {
        if (!TryBeginDeath())
            return;

        CompleteDeath(killer, killReason);
    }

    protected bool TryBeginDeath()
    {
        return Interlocked.Exchange(ref _deathHandled, 1) == 0;
    }

    protected void CompleteDeath(BaseUnit killer, KillReason killReason)
    {
        FallMovement.Reset();
        InterruptSkills();

        IsInBattle = false;
        var killerUnit = killer as Unit;
        var killerCharacter = killer as Character;
        var thisCharacter = this as Character;

        Events.OnDeath(this, new OnDeathArgs { Killer = killerUnit, Victim = this });
        var world = ParentWorld;
        world?.Events.OnUnitKilled(world, new OnUnitKilledArgs { Killer = killerUnit, Victim = this });
        killerUnit?.Events.OnKill(this, new OnKillArgs { Killer = killerUnit, Victim = this, Target = this });

        Buffs.RemoveEffectsOnDeath();

        var lostExp = 0;
        byte durabilityLoss = 0;
        var resurrectWaitTime = 0;
        // If this unit is a player and NOT killed by another player, then calculate xp loss.
        // Only in main_world
        if (thisCharacter is not null)
        {
            resurrectWaitTime = thisCharacter.RezWaitDuration;
            lostExp = thisCharacter.LastExpLoss;
            durabilityLoss = thisCharacter.LastDurabilityLoss;
        }

        if (this is Npc { Spawner: not null } thisNpc)
        {
            resurrectWaitTime = thisNpc.Spawner.RespawnTime;
        }
        
        killer.BroadcastPacket(new SCUnitDeathPacket(ObjId, killReason, resurrectWaitTime, lostExp, durabilityLoss, killerUnit), true);
        if (killer == this)
        {
            switch (this)
            {
                case Mate mate:
                    DespawnMate(WorldManager.Instance.GetCharacterByObjId(mate.OwnerObjId));
                    break;
                case Character character:
                    DespawnMate(character);
                    break;
            }
            return;
        }

        // Generate the loot for this Npc
        LootingContainer.GenerateLoot(killer);

        // Cleanup targeting and aggro packets
        if (CurrentTarget != null)
        {
            killer.SendPacketToPlayers([this, killer], new SCAiAggroPacket(killer.ObjId, 0));

            if (killerUnit is not null)
            {
                killerUnit.SummarizeDamage = 0;
                if (killerUnit.CurrentTarget is Unit unitTarget)
                {
                    unitTarget.IsInBattle = false;
                }

                killerUnit.IsInBattle = false;
            }
            //killer.StartRegen();
            killer.BroadcastPacket(new SCTargetChangedPacket(killer.ObjId, 0), true);

            if (thisCharacter is not null)
            {
                StopAutoSkill(thisCharacter);
                thisCharacter.IsInBattle = false; // we need the character to be "not in battle"
                DespawnMate(thisCharacter);
            }

            if (killerCharacter?.CurrentTarget == thisCharacter && thisCharacter is not null)
            {
                StopAutoSkill(killerCharacter);
                killerCharacter.IsInBattle = false; // we need the character to be "not in battle"
            }

            killerUnit?.CurrentTarget = null;
        }
    }

    private static void DespawnMate(Character character)
    {
        // if we died sitting on a horse
        if (character.Hp > 0) { return; }

        character.ParentWorld.MateManager.RemoveActiveMatesOnOwnerDeath(character);
    }

    public void StopAutoSkill(Unit unit)
    {
        if (unit.AutoAttackTask is null || unit is not Character character)
        {
            return;
        }

        character.AutoAttackTask.Cancelled = true;
        // await character.AutoAttackTask.Cancel();
        /*
        character.AutoAttackTask = null;
        character.IsAutoAttack = false; // turned off auto attack
        character.BroadcastPacket(new SCSkillEndedPacket(character.TlId), true);
        character.BroadcastPacket(new SCSkillStoppedPacket(character.ObjId, character.SkillId), true);
        TlIdManager.Instance.ReleaseId(character.TlId);
        */
    }

    public void StartAutoSkill(Skill skill)
    {
        if (this is not Character character || AutoAttackTask is not null)
        {
            return;
        }

        var newTask = new UseAutoAttackSkillTask(skill, character);
        character.AutoAttackTask = newTask;
        var attackDelayTimes = SkillManager.GetAttackDelay(skill.Template, character);

        TaskManager.Instance.Schedule(character.AutoAttackTask, TimeSpan.FromMilliseconds(attackDelayTimes),
            TimeSpan.FromMilliseconds(attackDelayTimes), -1);
        /*
        await character.AutoAttackTask.Cancel();
        character.AutoAttackTask = null;
        character.IsAutoAttack = false; // turned off auto attack
        character.BroadcastPacket(new SCSkillEndedPacket(character.TlId), true);
        character.BroadcastPacket(new SCSkillStoppedPacket(character.ObjId, character.SkillId), true);
        TlIdManager.Instance.ReleaseId(character.TlId);
        */
    }

    public void SetInvisible(bool value)
    {
        Invisible = value;
        BroadcastPacket(new SCUnitInvisiblePacket(ObjId, Invisible), true);
    }

    public void SetGeoDataMode(bool value)
    {
        AppConfiguration.Instance.World.GeoDataMode = value;
    }
    public void SetGodMode(bool value)
    {
        AppConfiguration.Instance.World.GodMode = value;
    }
    public void SetGrowthRate(float value)
    {
        AppConfiguration.Instance.World.GrowthRate = value;
    }
    public void SetLootRate(float value)
    {
        AppConfiguration.Instance.World.LootRate = value;
    }
    public void SetVocationRate(float value)
    {
        AppConfiguration.Instance.World.VocationRate = value;
    }
    public void SetHonorRate(float value)
    {
        AppConfiguration.Instance.World.HonorRate = value;
    }
    public void SetExpRate(float value)
    {
        AppConfiguration.Instance.World.ExpRate = value;
    }
    public void SetAutoSaveInterval(float value)
    {
        AppConfiguration.Instance.World.AutoSaveInterval = value;
    }
    public void SetLogoutMessage(string value)
    {
        AppConfiguration.Instance.World.LogoutMessage = value;
    }
    public void SetMotdMessage(string value)
    {
        AppConfiguration.Instance.World.MOTD = value;
    }
    public void SetCriminalState(bool criminalState, BaseUnit attackedTarget)
    {
        if (criminalState)
        {
            // Don't trigger Retribution (purple) when target is a Npc (except for player portals)
            if (attackedTarget is Npc && attackedTarget is not Portal)
                return;

            var buff = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Retribution);
            var casterObj = new SkillCasterUnit(ObjId);
            Buffs.AddBuff(new Buff(this, this, casterObj, buff, null, DateTime.UtcNow));
        }
        else
        {
            Buffs.RemoveBuff((uint)BuffConstants.Retribution);
        }
    }

    public void SetForceAttack(bool value)
    {
        ForceAttack = value;
        if (ForceAttack)
        {
            var buff = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Bloodlust);
            var casterObj = new SkillCasterUnit(ObjId);
            Buffs.AddBuff(new Buff(this, this, casterObj, buff, null, DateTime.UtcNow));
        }
        else
        {
            Buffs.RemoveBuff((uint)BuffConstants.Bloodlust);
        }
        BroadcastPacket(new SCForceAttackSetPacket(ObjId, ForceAttack), true);
    }

    public override void AddBonus(uint bonusIndex, Bonus bonus)
    {
        var bonuses = Bonuses.TryGetValue(bonusIndex, out var bonusList) ? bonusList : [];
        bonuses.Add(bonus);
        Bonuses[bonusIndex] = bonuses;
    }

    public override void RemoveBonus(uint bonusIndex, UnitAttribute attribute)
    {
        if (!Bonuses.TryGetValue(bonusIndex, out var bonuses))
        {
            return;
        }

        foreach (var bonus in new List<Bonus>(bonuses))
        {
            if (bonus.Template != null && bonus.Template.Attribute == attribute)
            {
                bonuses.Remove(bonus);
            }
        }
    }

    public List<Bonus> GetBonuses(UnitAttribute attribute)
    {
        var result = new List<Bonus>();
        if (Bonuses == null)
        {
            return result;
        }
        foreach (var bonuses in new List<List<Bonus>>(Bonuses.Values))
        {
            foreach (var bonus in new List<Bonus>(bonuses))
            {
                if (bonus.Template != null && bonus.Template.Attribute == attribute)
                {
                    result.Add(bonus);
                }
            }
        }
        return result;
    }

    public override void AddDynamicBonus(uint bonusIndex, DynamicBonus bonus)
    {
        var bonuses = DynamicBonuses.TryGetValue(bonusIndex, out var bonusList) ? bonusList : [];
        bonuses.Add(bonus);
        DynamicBonuses[bonusIndex] = bonuses;
    }

    public override void RemoveDynamicBonus(uint bonusIndex, UnitAttribute attribute)
    {
        if (!DynamicBonuses.TryGetValue(bonusIndex, out var bonuses))
        {
            return;
        }

        foreach (var bonus in new List<DynamicBonus>(bonuses))
        {
            if (bonus.Template != null && bonus.Template.Attribute == attribute)
            {
                bonuses.Remove(bonus);
            }
        }

        if (bonuses.Count == 0)
        {
            DynamicBonuses.Remove(bonusIndex);
        }
    }

    public List<DynamicBonus> GetDynamicBonuses(UnitAttribute attribute)
    {
        var result = new List<DynamicBonus>();
        if (DynamicBonuses == null)
        {
            return result;
        }
        foreach (var bonuses in new List<List<DynamicBonus>>(DynamicBonuses.Values))
        {
            foreach (var bonus in new List<DynamicBonus>(bonuses))
            {
                if (bonus.Template != null && bonus.Template.Attribute == attribute)
                {
                    result.Add(bonus);
                }
            }
        }
        return result;
    }

    public double CalculateWithBonuses(double value, UnitAttribute attr)
    {
        return UnitAttributeLimitsGameData.Instance.Clamp(attr, CalculateBonuses(value, attr));
    }

    protected double CalculateBonuses(double value, UnitAttribute attr)
    {
        // Apply static and dynamic flat values, then one combined Percent multiplier.
        // Dynamic bonuses are evaluated on the fly from their source buff so that time-varying
        // modifiers (LinearFunc dynamic_unit_modifiers) reflect the current elapsed time rather
        // than a value snapshotted at buff Start.
        var bonuses = GetBonuses(attr);
        var dynamicBonuses = GetDynamicBonuses(attr);

        // Static flat values
        foreach (var bonus in bonuses)
        {
            if (bonus.Template.ModifierType != UnitModifierType.Value)
                continue;
            value += bonus.Value;
        }

        // Dynamic flat values
        foreach (var dynamicBonus in dynamicBonuses)
        {
            if (dynamicBonus.Template.ModifierType != UnitModifierType.Value)
                continue;
            if (dynamicBonus.Evaluate(out var dynValue))
                value += dynValue;
        }

        var percent = 100d;

        // Static percent values
        foreach (var bonus in bonuses)
        {
            if (bonus.Template.ModifierType != UnitModifierType.Percent)
                continue;
            percent += bonus.Value;
        }

        // Dynamic percent values
        foreach (var dynamicBonus in dynamicBonuses)
        {
            if (dynamicBonus.Template.ModifierType != UnitModifierType.Percent)
                continue;
            if (dynamicBonus.Evaluate(out var dynValue))
                percent += dynValue;
        }

        return value * percent / 100d;
    }

    public void SendPacket(GamePacket packet)
    {
        Connection?.SendPacket(packet);
    }

    public void SendErrorMessage(ErrorMessageType type)
    {
        SendPacket(new SCErrorMsgPacket(type, 0, true));
    }

    public virtual int GetAbLevel(AbilityType type)
    {
        return Level;
    }

    public string GetAttribute(UnitAttribute attr)
    {
        var props = GetType().GetProperties()
            .Where(o => (o.GetCustomAttributes(typeof(UnitAttributeAttribute), true) as IEnumerable<UnitAttributeAttribute>)
                .Any(a => a.Attributes.Contains(attr)));

        if (props.Any())
            return props.ElementAt(0).GetValue(this).ToString();
        else
            return "NotFound";
    }

    public T GetAttribute<T>(UnitAttribute attr, T defaultVal)
    {
        var props = GetType().GetProperties()
            .Where(o => (o.GetCustomAttributes(typeof(UnitAttributeAttribute), true) as IEnumerable<UnitAttributeAttribute>)
                .Any(a => a.Attributes.Contains(attr)));

        if (props.Any())
        {
            var ElementValue = props.ElementAt(0).GetValue(this);
            if (ElementValue is T ret)
                return ret;
        }
        return defaultVal;
    }

    public string GetAttribute(uint attr) => GetAttribute((UnitAttribute)attr);

    //Uncomment if you need this
    /*
    public string GetAttribute(string attr)
    {
        if (Enum.TryParse(typeof(UnitAttribute), attr, true, out var result))
        {
            return GetAttribute((UnitAttribute)result);
        }
        return "FailedParse";
    }
    */

    public override void InterruptSkills()
    {
        ActivePlotState?.RequestCancellation();
        if (SkillTask == null)
            return;
        switch (SkillTask)
        {
            case EndChannelingTask ect:
                ect.Skill.Stop(this, ect._channelDoodad);
                break;
            default:
                SkillTask.Skill.Stop(this);
                break;
        }
    }

    public bool IsDead
    {
        get
        {
            return Hp <= 0;
        }
    }

    public bool NeedsRegen
    {
        get
        {
            return Hp < MaxHp || Mp < MaxMp;
        }
    }

    // TODO: Implement this to grab actual loot info
    public virtual bool HasLootLeft { get; set; } = false;
    public virtual ModelPostureType ModelPostureType { get => ModelPostureType.None; }
    public Gimmick Gimmick { get; set; }

    /// <summary>
    /// Tagging works differently to Aggro and has its own system 
    /// </summary>
    public Tagging CharacterTagging { get; set; }
    public virtual void OnSkillEnd(Skill skill)
    {

    }

    internal FallMovement FallMovement { get; } = new();

    internal bool HasFallDamageImmunity => Buffs.HasEffectsMatchingCondition(buff =>
        buff.InUse && !buff.IsEnded() && buff.Template.FallDamageImmune);

    internal void ObserveFallMovement(bool reportedLanding, double? time = null)
    {
        var controlledFall = Buffs.HasEffectsMatchingCondition(buff => buff.InUse && !buff.IsEnded() &&
            (buff.Template.FallDamageImmune || buff.Template.Gliding));
        if (DisabledSetPosition || IsDead || controlledFall || ActiveSkillController != null ||
            DisplacedUntil > DateTime.UtcNow || Transform.Parent != null || Transform.StickyParent != null ||
            this is Character { IsRiding: true } || ParentWorld?.IsWater(Transform.World.Position) == true)
        {
            FallMovement.Reset();
            return;
        }

        var position = Transform.World.Position;
        var grounded = ParentWorld?.TryGetHeight(position.X, position.Y, out var height) == true &&
            position.Z <= height + FallMovement.PositionTolerance;
        var impact = FallMovement.Observe(position.Z, time ?? FallMovement.Now, grounded, reportedLanding);
        if (impact > 0)
            DoFallDamage(impact);
    }

    /// <summary>
    /// Applies an impact derived from accepted movement. The value uses the native
    /// actor.fallVel encoding (0..65535 represents 0..128 metres per second).
    /// </summary>
    public virtual int DoFallDamage(ushort fallVel)
    {
        if (Hp <= 0 || MaxHp <= 0 || HasFallDamageImmunity ||
            ParentWorld?.IsWater(Transform.World.Position) == true)
            return 0;

        // Preserve the established server damage curve and lethal threshold. The
        // movement observer, not the optional client value, supplies the impact.
        var multiplier = CalculateWithBonuses(0d, UnitAttribute.FallDamageMul) / 100d;
        var baseDamage = Math.Clamp(MaxHp * ((fallVel - 8600) / 15000d), 0, MaxHp);
        var damage = (int)Math.Clamp(baseDamage * Math.Max(0, 1 + multiplier), 0, MaxHp);
        if (damage == 0 && fallVel < 32000)
            return 0;

        var oldHp = Hp;
        var minimumHp = Math.Max(1, MaxHp / 20);
        var availableHp = Math.Max(0, Hp - minimumHp);
        if (fallVel >= 32000)
        {
            ReduceCurrentHp(this, Hp, KillReason.Fall);
        }
        else if (damage < availableHp)
        {
            ReduceCurrentHp(this, damage, KillReason.Fall);
        }
        else
        {
            var duration = 500 * (damage / minimumHp);
            var template = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.FallStun);
            if (template != null)
                Buffs.AddBuff(new Buff(this, this, new SkillCasterUnit(ObjId), template, null, DateTime.UtcNow), 0, duration);
            if (availableHp > 0)
                ReduceCurrentHp(this, availableHp, KillReason.Fall);
        }

        var dealt = Math.Max(0, oldHp - Hp);
        if (dealt > 0)
            BroadcastPacket(new SCEnvDamagePacket(EnvSource.Falling, ObjId, (uint)dealt), true);
        return dealt;
    }

    /// <summary>
    /// Set the faction of the owner
    /// </summary>
    /// <param name="factionId"></param>
    public virtual void SetFaction(FactionsEnum factionId)
    {
        // Keep origin faction data temporarily for arena players
        OriginFaction = Faction;
        var player = this as Character;

        // change the faction for the character
        player?.OriginFactionName = player.FactionName;

        Logger.Info($"SetFaction: npc={TemplateId}:{ObjId}, factionId={factionId}");

        if (Faction.Id == factionId)
        {
            Logger.Info($"SetFaction: faction has already been established factionId={factionId}");
        }
        else
        {
            var oldFactionId = Faction?.Id ?? 0;
            // BroadcastPacket(new SCUnitFactionChangedPacket(ObjId, Name, Faction?.Id ?? 0, factionId, false), true);
            var faction = FactionManager.Instance.GetFaction(factionId);
            if (player != null)
                ChatManager.Instance.UserChannels.ChangeFaction(player, faction);
            else
                Faction = faction;
            BroadcastPacket(new SCUnitFactionChangedPacket(ObjId, Name, oldFactionId, Faction.Id, false), true);
            if (Faction.Id == FactionsEnum.Pirate)
            {
                Buffs.AddBuff((uint)BuffConstants.Contemptuous, this);
            }
            else
            {
                Buffs.RemoveBuff((uint)BuffConstants.Contemptuous);
            }
        }

        // TODO added for quest Id=2486
        if (this is not Npc npc) { return; }

        // Npc attacks the character
        var characters = WorldManager.GetAround<Character>(npc, 5.0f);
        foreach (var character in characters.Where(CanAttack))
        {
            Logger.Info($"SetFaction: npc={TemplateId}:{ObjId} attack the character={character.Name}:{character.TemplateId}:{character.ObjId}");
            npc.Ai.Owner.AddUnitAggro(AggroKind.Damage, character, 1);
            npc.Ai.OnAggroTargetChanged();
            //npc.Ai.GoToCombat();
        }
    }

    public virtual SkillResult UseSkill(uint skillId, IUnit target)
    {
        var skill = new Skill(SkillManager.Instance.GetSkillTemplate(skillId));

        var caster = SkillCaster.GetByType(SkillCasterType.Unit);
        caster.ObjId = ObjId;

        var sct = SkillCastTarget.GetByType(SkillCastTargetType.Unit);
        sct.ObjId = target.ObjId;

        return skill.Use(this, caster, sct, null, true, out _);
    }

    public virtual SkillResult UseSkill(uint skillId, Doodad target)
    {
        var skill = new Skill(SkillManager.Instance.GetSkillTemplate(skillId));

        var caster = SkillCaster.GetByType(SkillCasterType.Unit);
        caster.ObjId = ObjId;

        var sct = SkillCastTarget.GetByType(SkillCastTargetType.Doodad);
        sct.ObjId = target.ObjId;

        return skill.Use(this, caster, sct, null, true, out _);
    }

    public static void ModelPosture(PacketStream stream, Unit unit, uint animActionId, bool activateAnimation)
    {
        var npc = unit as Npc;

        stream.Write((byte)unit.ModelPostureType);
        stream.Write(unit.HasLootLeft); // isLooted

        switch (unit.ModelPostureType)
        {
            case ModelPostureType.HouseState: // build
                for (var i = 0; i < 2; i++)
                {
                    stream.Write(true); // door
                }

                for (var i = 0; i < 6; i++)
                {
                    stream.Write(true); // window
                }

                break;
            case ModelPostureType.ActorModelState: // npc
                // Logger.Debug($"Using AnimActionId={animActionId} for NPC TemplateId: {npc?.TemplateId}, ObjId:{npc?.ObjId}");
                stream.Write(animActionId); // Animation override
                stream.Write(activateAnimation); // activate
                break;
            case ModelPostureType.FarmfieldState:
                stream.Write(0u); // type(id)
                stream.Write(0f); // growRate
                stream.Write(0); // randomSeed
                stream.Write(false); // isWithered
                stream.Write(false); // isHarvested
                break;
            case ModelPostureType.TurretState: // slave
                var aim = unit.TurretAim;
                stream.Write(aim.Pitch);
                stream.Write(aim.Yaw);
                break;
        }
    }

    public WeaponWieldKind GetWeaponWieldKind()
    {
        var item = Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        if (item != null && item.Template is WeaponTemplate weapon)
        {
            var slotId = (EquipmentItemSlotType)weapon.HoldableTemplate.SlotTypeId;
            if (slotId == EquipmentItemSlotType.TwoHanded)
                return WeaponWieldKind.TwoHanded;
            else if (slotId == EquipmentItemSlotType.OneHanded || slotId == EquipmentItemSlotType.Mainhand)
            {
                var item2 = Equipment.GetItemBySlot((int)EquipmentItemSlot.Offhand);
                if (item2 != null && item2.Template is WeaponTemplate weapon2)
                {
                    var slotId2 = (EquipmentItemSlotType)weapon2.HoldableTemplate.SlotTypeId;
                    if (slotId2 == EquipmentItemSlotType.OneHanded || slotId2 == EquipmentItemSlotType.Offhand)
                        return WeaponWieldKind.DuelWielded;
                    else
                        return WeaponWieldKind.OneHanded;
                }
                else
                    return WeaponWieldKind.OneHanded;
            }
        }

        return WeaponWieldKind.None;
    }

    public void UpdateGearBonuses(Item itemAdded, Item itemRemoved)
    {
        Bonuses[GearBonusesIndex] = [];

        foreach (var item in Equipment.Items)
        {
            if (item is not EquipItem ei)
                continue;
            
            if (!ei.IsNotDestroyed)
                continue;

            // Mods on the gear Itself
            foreach (var template in ItemManager.Instance.GetUnitModifiers(item.TemplateId))
                AddBonus(GearBonusesIndex, new Bonus { Template = template, Value = template.Value });

            // Mods from equipped Gems
            foreach (var gem in ei.GemIds)
                foreach (var template in ItemManager.Instance.GetUnitModifiers(gem))
                    AddBonus(GearBonusesIndex, new Bonus { Template = template, Value = template.Value });
        }

        // Apply Equipment Effects
        ApplyEquipEffects(itemAdded, itemRemoved);

        // Compute gear buff
        ApplyWeaponWieldBuff();
        ApplyArmorGradeBuff(itemAdded, itemRemoved);
        ApplyEquipItemSetBonuses();
        Procs.RefreshEquipment();
    }

    private void ApplyWeaponWieldBuff()
    {
        Buffs.RemoveBuff((uint)BuffConstants.EquipDualwield);
        Buffs.RemoveBuff((uint)BuffConstants.EquipShield);
        Buffs.RemoveBuff((uint)BuffConstants.EquipTwoHanded);

        BuffTemplate buffTemplate = null;
        switch (GetWeaponWieldKind())
        {
            case WeaponWieldKind.None:
            case WeaponWieldKind.OneHanded:
                var item = Equipment.GetItemBySlot((int)EquipmentItemSlot.Offhand);
                if (item != null && item.Template is WeaponTemplate weapon)
                {
                    var slotId = (EquipmentItemSlotType)weapon.HoldableTemplate.SlotTypeId;
                    if (slotId == EquipmentItemSlotType.Shield)
                        buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.EquipShield);
                }
                break;
            case WeaponWieldKind.TwoHanded:
                buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.EquipTwoHanded);
                break;
            case WeaponWieldKind.DuelWielded:
                buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.EquipDualwield);
                break;
        }

        if (buffTemplate != null)
        {
            var effect = new Buff(this, this, new SkillCasterUnit(ObjId), buffTemplate, null, DateTime.UtcNow);
            Buffs.AddBuff(effect);
        }
    }

    private void ApplyEquipItemSetBonuses()
    {
        var desired = new Dictionary<uint, uint>();
        var equipped = Equipment.Items.OfType<EquipItem>()
            .Where(item => item.IsNotDestroyed && ReferenceEquals(item._holdingContainer, Equipment));
        foreach (var group in equipped.Where(item => item.Template is EquipItemTemplate { EquipItemSetId: > 0 })
                     .GroupBy(item => ((EquipItemTemplate)item.Template).EquipItemSetId))
        {
            var set = ItemManager.Instance.GetEquippedItemSet(group.Key);
            if (set == null)
                continue;
            var count = group.Count();
            var level = (uint)group.Min(item => item.Template.Level);
            foreach (var bonus in set.Bonuses.Where(bonus => bonus.BuffId != 0 && count >= bonus.NumPieces))
                desired[bonus.BuffId] = level;
        }

        foreach (var buffId in _equipmentSetBuffs.Where(buffId => !desired.ContainsKey(buffId)))
            Buffs.RemoveBuff(buffId);
        _equipmentSetBuffs.Clear();
        foreach (var (buffId, level) in desired)
        {
            _equipmentSetBuffs.Add(buffId);
            if (Buffs.CheckBuff(buffId))
                continue;
            var template = SkillManager.Instance.GetBuffTemplate(buffId);
            if (template != null)
                Buffs.AddBuff(new Buff(this, this, new SkillCasterUnit(ObjId), template, null, DateTime.UtcNow)
                {
                    AbLevel = level
                });
        }
    }

    private void ApplyArmorGradeBuff(Item itemAdded, Item itemRemoved)
    {
        if ((itemAdded != null || itemRemoved != null) && itemAdded is not Items.Armor && itemRemoved is not Items.Armor)
            return;

        if (itemAdded is EquipItem { MaxDurability: > 0, Durability: <= 0 })
        {
            // Destroyed item, ignore
            return;
        }

        // Clear any existing armor grade buffs
        Buffs.RemoveBuffs((uint)BuffConstants.ArmorBuffTag, 10);

        // Get armor pieces by kind
        var armorPieces = new Dictionary<ArmorType, List<Armor>>();
        foreach (var item in Equipment.Items)
        {
            if (item is not Armor armor)
                continue;

            if (item.Template is not ArmorTemplate armorTemplate)
                continue;

            if (armorTemplate.SlotTemplate.SlotTypeId == (ulong)EquipmentItemSlotType.Back)
                continue;

            if (!armorPieces.ContainsKey((ArmorType)armorTemplate.KindTemplate.TypeId))
                armorPieces.Add((ArmorType)armorTemplate.KindTemplate.TypeId, []);
            armorPieces[(ArmorType)armorTemplate.KindTemplate.TypeId].Add(armor);
        }

        if (armorPieces.Count == 0)
            return;
        // Get kind with most pieces
        var piecesOfKind = armorPieces.First();
        foreach (var piecesByKind in armorPieces)
        {
            if (piecesByKind.Value.Count > piecesOfKind.Value.Count) piecesOfKind = piecesByKind;
        }

        var piecesToAccountForBuff = piecesOfKind.Value;

        if (piecesToAccountForBuff.Count < 4)
            return;

        var finalArmorTemplate = piecesToAccountForBuff.First().Template as ArmorTemplate;
        if (finalArmorTemplate == null)
            return;

        if (piecesToAccountForBuff.Count == 7)
        {
            BuffTemplate buffTemplate = null;
            switch ((ArmorType)finalArmorTemplate.WearableTemplate.TypeId)
            {
                case ArmorType.Cloth:
                    buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Cloth7P);
                    break;
                case ArmorType.Leather:
                    buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Leather7P);
                    break;
                case ArmorType.Metal:
                    buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Plate7P);
                    break;
            }

            if (buffTemplate != null)
                Buffs.AddBuff(new Buff(this, this, new SkillCasterUnit(), buffTemplate, null, DateTime.UtcNow));
        }
        else
        {
            BuffTemplate buffTemplate = null;
            switch ((ArmorType)finalArmorTemplate.WearableTemplate.TypeId)
            {
                case ArmorType.Cloth:
                    buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Cloth4P);
                    break;
                case ArmorType.Leather:
                    buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Leather4P);
                    break;
                case ArmorType.Metal:
                    buffTemplate = SkillManager.Instance.GetBuffTemplate((uint)BuffConstants.Plate4P);
                    break;
            }

            if (buffTemplate != null)
                Buffs.AddBuff(new Buff(this, this, new SkillCasterUnit(), buffTemplate, null, DateTime.UtcNow));
        }

        // Get only pieces >= arcane
        var piecesAboveArcane = piecesToAccountForBuff.Where(p => p.Grade >= (int)ItemGrade.Arcane).ToList();
        if (piecesAboveArcane.Count < 4)
            return;

        var totalLevel = piecesAboveArcane.Sum(a => a.Template.Level);

        // This const was calculated by hand, it might make no sense.
        var abLevel = totalLevel * 0.40670554f;
        var gradeBuffAbLevel = abLevel * abLevel / 15 + 30;
        var lowestGrade = piecesAboveArcane.Min(a => a.Grade);

        // Apply buff 
        if (piecesAboveArcane.First().Template is ArmorTemplate armorTemp)
        {
            var type = armorTemp.WearableTemplate.TypeId;
            var armorGradeBuff =
                ItemManager.Instance.GetArmorGradeBuff((ArmorType)type, (ItemGrade)lowestGrade);
            var buffTemplate = SkillManager.Instance.GetBuffTemplate(armorGradeBuff.BuffId);

            var newEffect =
                new Buff(this, this, new SkillCasterUnit(), buffTemplate, null, DateTime.UtcNow)
                {
                    AbLevel = (uint)gradeBuffAbLevel
                };

            Buffs.AddBuff(newEffect);
        }
    }

    private void ApplyEquipEffects(Item itemAdded, Item itemRemoved)
    {
        if (itemRemoved != null)
        {
            // Static Item Buffs
            var itemRemovedBuff = ItemGameData.Instance.GetItemBuff(itemRemoved.TemplateId, itemRemoved.Grade) ??
                                  SkillManager.Instance.GetBuffTemplate(itemRemoved.Template?.BuffId ?? 0);
            if (itemRemovedBuff != null) // remove previous buff
            {
                if (Buffs.CheckBuff(itemRemovedBuff.Id))
                {
                    Buffs.RemoveBuff(itemRemovedBuff.Id);
                }
            }

            // Charged Item Buffs
            if (itemRemoved.Template is EquipItemTemplate equipItemTemplate &&
                equipItemTemplate.RechargeBuffId > 0 &&
                Buffs.CheckBuff(equipItemTemplate.RechargeBuffId))
                Buffs.RemoveBuff(equipItemTemplate.RechargeBuffId);
        }

        if (itemAdded != null)
        {
            if (itemAdded is EquipItem { MaxDurability: > 0, Durability: <= 0 })
            {
                // Destroyed item, ignore
            }
            else
            {
                // Static Buffs
                var itemAddedBuff = ItemGameData.Instance.GetItemBuff(itemAdded.TemplateId, itemAdded.Grade) ??
                                    SkillManager.Instance.GetBuffTemplate(itemAdded.Template.BuffId);
                if (itemAddedBuff != null) // add buff from equipped item
                {
                    var newEffect =
                        new Buff(this, this, new SkillCasterUnit(), itemAddedBuff, null, DateTime.UtcNow)
                        {
                            AbLevel = (uint)itemAdded.Template.Level
                        };

                    Buffs.AddBuff(newEffect);
                }

                // Charged Item Buffs
                if (itemAdded is EquipItem equipItem && equipItem.Template is EquipItemTemplate equipItemTemplate &&
                    equipItemTemplate.RechargeBuffId > 0)
                {
                    var addChargeBuff = false;
                    var checkExpireTime = equipItemTemplate.BindType.HasFlag(ItemBindType.BindOnUnpack)
                        ? equipItem.UnpackTime
                        : equipItem.ChargeStartTime;
                    checkExpireTime = checkExpireTime.AddMinutes(equipItemTemplate.ChargeLifetime);

                    // Check against timer
                    if (equipItemTemplate.ChargeLifetime > 0 && checkExpireTime > DateTime.UtcNow)
                        addChargeBuff = true;

                    // Check against charge counter
                    if (equipItemTemplate.ChargeCount > 0 && equipItem.ChargeCount > 0)
                        addChargeBuff = true;

                    // If this item is Bind on unwrap, don't start the buff if it's not unwrapped
                    if (equipItemTemplate.BindType.HasFlag(ItemBindType.BindOnUnpack) &&
                        equipItem.HasFlag(ItemFlag.Unpacked) == false)
                        addChargeBuff = false;

                    if (addChargeBuff)
                    {
                        var itemAddedChargedBuff =
                            SkillManager.Instance.GetBuffTemplate(equipItemTemplate.RechargeBuffId);
                        var newEffect =
                            new Buff(this, this, new SkillCasterUnit(), itemAddedChargedBuff, null, DateTime.UtcNow)
                            {
                                AbLevel = (uint)itemAdded.Template.Level
                            };
                        Buffs.AddBuff(newEffect);
                    }
                }

                // Unit_Modifiers from items
            }
        }

        if (itemAdded == null && itemRemoved == null) // This is the first load check to apply buffs for equipped items. 
        {
            Buffs.RemoveBuffs((uint)BuffConstants.EquipmentBuffTag, 20);
            foreach (var item in Equipment.Items)
            {
                // Static Buffs
                if (item.Template.BuffId != 0)
                {
                    var buffTemplate = ItemGameData.Instance.GetItemBuff(item?.TemplateId ?? 0, item?.Grade ?? 0) ??
                                       SkillManager.Instance.GetBuffTemplate(item?.Template.BuffId ?? 0);
                    var newEffect =
                        new Buff(this, this, new SkillCasterUnit(), buffTemplate, null, DateTime.UtcNow)
                        {
                            AbLevel = (uint)item.Template.Level
                        };

                    Buffs.AddBuff(newEffect);
                }

                // Charged Item Buffs
                if (item is EquipItem equipItem && equipItem.Template is EquipItemTemplate equipItemTemplate &&
                    equipItemTemplate.RechargeBuffId > 0)
                {
                    var addChargeBuff = false;
                    var checkExpireTime = equipItemTemplate.BindType.HasFlag(ItemBindType.BindOnUnpack)
                        ? equipItem.UnpackTime
                        : equipItem.ChargeStartTime;
                    checkExpireTime = checkExpireTime.AddMinutes(equipItemTemplate.ChargeLifetime);

                    // Check against timer
                    if (equipItemTemplate.ChargeLifetime > 0 && checkExpireTime > DateTime.UtcNow)
                        addChargeBuff = true;

                    // Check against charge counter
                    if (equipItemTemplate.ChargeCount > 0 && equipItem.ChargeCount > 0)
                        addChargeBuff = true;

                    // If this item is Bind on unwrap, don't start the buff if it's not unwrapped
                    if (equipItemTemplate.BindType.HasFlag(ItemBindType.BindOnUnpack) && equipItem.HasFlag(ItemFlag.Unpacked) == false)
                        addChargeBuff = false;

                    if (addChargeBuff)
                    {
                        var itemAddedChargedBuff = SkillManager.Instance.GetBuffTemplate(equipItemTemplate.RechargeBuffId);
                        var newEffect =
                            new Buff(this, this, new SkillCasterUnit(), itemAddedChargedBuff, null, DateTime.UtcNow)
                            {
                                AbLevel = (uint)item.Template.Level
                            };
                        Buffs.AddBuff(newEffect);
                    }
                }
            }
        }
    }

    public override void OnZoneChange(uint lastZoneKey, uint newZoneKey)
    {
        // We switched zone keys, we need to do some checks
        var lastZone = ZoneManager.Instance.GetZoneByKey(lastZoneKey);
        var newZone = ZoneManager.Instance.GetZoneByKey(newZoneKey);
        var lastZoneGroupId = (short)(lastZone?.GroupId ?? 0);
        var newZoneGroupId = (short)(newZone?.GroupId ?? 0);
        if (lastZoneGroupId == newZoneGroupId)
            return;

        // Handle Zone Buffs
        if (lastZone != null)
        {
            // Remove the old zone buff if needed
            var lastZoneGroup = ZoneManager.Instance.GetZoneGroupById(lastZone.GroupId);
            if (lastZoneGroup != null && lastZoneGroup.BuffId != 0)
            {
                // Remove the applied buff from last zoneGroup
                Buffs.RemoveBuff(lastZoneGroup.BuffId);
            }
        }
        if (newZone != null)
        {
            // Apply the new zone buff if needed
            var newZoneGroup = ZoneManager.Instance.GetZoneGroupById(newZone.GroupId);
            if (newZoneGroup != null && newZoneGroup.BuffId != 0)
            {
                // Add buff from new zoneGroup
                var buffTemplate = SkillManager.Instance.GetBuffTemplate(newZoneGroup.BuffId);
                if (buffTemplate != null)
                {
                    var casterObj = new SkillCasterUnit(ObjId);
                    var newZoneBuff = new Buff(this, this, casterObj, buffTemplate, null, DateTime.UtcNow);
                    Buffs.AddBuff(newZoneBuff);
                }
            }
        }
    }

    private readonly Dictionary<uint, int> _triggerCounts = new();

    public void IncrementTriggerCount(uint buffId)
    {
        if (!_triggerCounts.TryAdd(buffId, 1))
        {
            _triggerCounts[buffId]++;
        }
    }

    public void DecrementTriggerCount(uint buffId)
    {
        if (_triggerCounts.ContainsKey(buffId) && _triggerCounts[buffId] > 0)
        {
            _triggerCounts[buffId]--;
        }
    }

    public int GetTriggerCount(uint buffId)
    {
        return _triggerCounts.GetValueOrDefault(buffId, 0);
    }

    /// <summary>
    /// Handle is still in combat related things
    /// </summary>
    /// <param name="delta"></param>
    protected virtual void CombatTick(TimeSpan delta)
    {
        // TODO: Make it so you can also become out of combat if you are not on any aggro lists
        if (IsInBattle && LastCombatActivity.AddSeconds(WorldManager.DefaultCombatTimeout) < DateTime.UtcNow)
        {
            IsInBattle = false;
        }
    }

    /// <summary>
    /// Call regeneration function of the unit
    /// </summary>
    /// <param name="delta"></param>
    protected virtual void RegenTick(TimeSpan delta)
    {
        // Do nothing
    }

    /// <summary>
    /// Tick called for Units in active player regions about once per second
    /// </summary>
    /// <param name="delta"></param>
    public virtual void OnActiveRegionTick(TimeSpan delta)
    {
        CombatTick(delta);
        RegenTick(delta);
    }

    /// <summary>
    /// Adds aggro
    /// </summary>
    /// <param name="kind"></param>
    /// <param name="unit"></param>
    /// <param name="amount"></param>
    /// <returns>Returns true if it's initial aggro</returns>
    public bool AddUnitAggro(AggroKind kind, Unit unit, int amount)
    {
        return AddUnitAggroCore(kind, unit, amount, false);
    }

    public bool TryAddAssistanceAggro(Unit unit)
    {
        return AddUnitAggroCore(AggroKind.Damage, unit, 1, true);
    }

    private bool AddUnitAggroCore(AggroKind kind, Unit unit, int amount, bool initialOnly, bool requireCurrentAggro = false)
    {
        if (unit == null)
            return false;
        if (Buffs.CheckBuffTag((uint)TagsEnum.NoFight) || Buffs.CheckBuffTag((uint)TagsEnum.Returning) ||
            (unit.Buffs?.CheckBuffTag((uint)TagsEnum.NoFight) ?? false) ||
            (unit.Buffs?.CheckBuffTag((uint)TagsEnum.Returning) ?? false))
        {
            ClearAggroOfUnit(unit);
            return false;
        }

        var npc = this as Npc;
        Aggro aggro;
        bool added;
        int damageDelta;
        int healDelta;
        lock (AggroTable)
        {
            if (npc != null && (npc.Despawned || npc.CombatRetired || npc.IsDead || npc.Hp <= 0 ||
                npc.Ai?.GetCurrentBehavior() is ReturnStateBehavior or DeadBehavior))
                return false;
            if (requireCurrentAggro && AggroTable.IsEmpty)
                return false;
            if (initialOnly && (npc == null || IsInBattle || !AggroTable.IsEmpty ||
                !ReferenceEquals(npc.Ai?.Owner, npc) || unit.Hp <= 0 || unit.IsDead ||
                unit is Npc { Despawned: true } or Npc { CombatRetired: true } ||
                !ReferenceEquals(ParentWorld, unit.ParentWorld) ||
                !ReferenceEquals(ParentWorld?.GetUnit(ObjId), this) ||
                !ReferenceEquals(ParentWorld?.GetUnit(unit.ObjId), unit) ||
                npc.Ai?.GetCurrentBehavior() is ReturnStateBehavior or DeadBehavior))
                return false;

            // Actual damage/taunt input owns tagging. Shared copies below do not.
            if (kind == AggroKind.Damage)
                CharacterTagging.AddTagger(unit, amount);

            amount = (int)(amount * (unit.AggroMul / 100.0f));
            amount = (int)(amount * (IncomingAggroMul / 100.0f));
            added = !TryGetCurrentAggro(unit, out aggro);
            if (added)
            {
                aggro = new Aggro(unit);
                AggroTable[unit.ObjId] = aggro;
                UnitEvents.UpdateSubscription(ref unit.Events.OnHealed, OnAbuserHealed, true);
                UnitEvents.UpdateSubscription(ref unit.Events.OnDeath, OnAbuserDied, true);
            }
            var previousDamage = aggro.DamageAggro;
            var previousHeal = aggro.HealAggro;
            aggro.AddAggro(kind, amount);
            damageDelta = aggro.DamageAggro - previousDamage;
            healDelta = aggro.HealAggro - previousHeal;
        }

        // No group operation or quest/AI callback runs under the local mutation lock.
        npc?.GroupInstance?.AddSharedThreat(npc, unit, aggro, damageDelta, healDelta);
        CompleteAggroUpdate(unit, aggro, added, true);
        // Preserve the legacy return value for normal callers. Assistance needs a
        // distinct success result for its atomic initial-entry operation.
        return initialOnly || !added;
    }

    internal Action SetSharedAggro(Unit unit, int damage, int heal, bool sourceEffect = false, NpcGroupInstance group = null)
    {
        Aggro aggro;
        bool added;
        lock (AggroTable)
        {
            if (this is not Npc npc || npc.Despawned || npc.CombatRetired || npc.IsDead || npc.Hp <= 0 ||
                !ReferenceEquals(npc.ParentWorld?.GetUnit(npc.ObjId), npc) ||
                unit.Hp <= 0 || unit.IsDead || unit is Npc { Despawned: true } or Npc { CombatRetired: true } ||
                !ReferenceEquals(ParentWorld, unit.ParentWorld) ||
                !ReferenceEquals(ParentWorld?.GetUnit(unit.ObjId), unit) ||
                !ReferenceEquals(npc.Ai?.Owner, npc) ||
                npc.Ai?.GetCurrentBehavior() is ReturnStateBehavior or DeadBehavior ||
                (group != null && (group.IsRetired || !ReferenceEquals(npc.GroupInstance, group))))
                return null;

            added = !TryGetCurrentAggro(unit, out aggro);
            if (added)
            {
                aggro = new Aggro(unit);
                AggroTable[unit.ObjId] = aggro;
                UnitEvents.UpdateSubscription(ref unit.Events.OnHealed, OnAbuserHealed, true);
                UnitEvents.UpdateSubscription(ref unit.Events.OnDeath, OnAbuserDied, true);
            }
            // The source already applied modifiers and the healing threat factor.
            aggro.SetAmounts(damage, heal);
        }
        return () => CompleteAggroUpdate(unit, aggro, added, sourceEffect);
    }

    // Caller holds the local aggro lock. Object IDs can be reused after removal.
    private bool TryGetCurrentAggro(Unit unit, out Aggro aggro)
    {
        if (!AggroTable.TryGetValue(unit.ObjId, out aggro))
            return false;
        if (ReferenceEquals(aggro.Owner, unit))
            return true;
        AggroTable.TryRemove(unit.ObjId, out _);
        UnitEvents.UpdateSubscription(ref aggro.Owner.Events.OnHealed, OnAbuserHealed, false);
        UnitEvents.UpdateSubscription(ref aggro.Owner.Events.OnDeath, OnAbuserDied, false);
        if (aggro.Owner is Character oldPlayer)
        {
            lock (oldPlayer.IsInAggroListOf)
                oldPlayer.IsInAggroListOf.Remove(ObjId);
        }
        aggro = null;
        return false;
    }

    private void CompleteAggroUpdate(Unit unit, Aggro aggro, bool added, bool sourceEffect)
    {
        var npc = this as Npc;
        if (npc != null && (npc.Despawned || npc.CombatRetired || npc.IsDead || npc.Hp <= 0))
            return;
        if (!AggroTable.TryGetValue(unit.ObjId, out var current) || !ReferenceEquals(current, aggro))
            return;

        var player = unit as Character;
        if (added)
        {
            if (sourceEffect && npc?.Template.EngageCombatGiveQuestId > 0 && player != null &&
                !player.Quests.IsQuestComplete(npc.Template.EngageCombatGiveQuestId) &&
                !player.Quests.HasQuest(npc.Template.EngageCombatGiveQuestId))
                player.Quests.AddQuestFromNpc(npc.Template.EngageCombatGiveQuestId, npc.ObjId);

            // A quest callback can remove its giver. Do not restore combat after that cleanup.
            if (npc is { Despawned: true } or { CombatRetired: true } || !AggroTable.TryGetValue(unit.ObjId, out current) ||
                !ReferenceEquals(current, aggro))
                return;
            unit.SendPacketToPlayers([this, unit], new SCCombatFirstHitPacket(ObjId, unit.ObjId, 0));
            if (unit.Hp > 0 && !unit.IsInBattle)
            {
                unit.IsInBattle = true;
                unit.LastCombatActivity = DateTime.UtcNow;
            }
        }

        if (player == null)
            return;
        lock (AggroTable)
        {
            if (IsDead || Hp <= 0 || npc is { Despawned: true } or { CombatRetired: true } ||
                !AggroTable.TryGetValue(unit.ObjId, out current) || !ReferenceEquals(current, aggro))
                return;
            if (aggro.TotalAggro > 0)
            {
                lock (player.IsInAggroListOf)
                    player.IsInAggroListOf.TryAdd(ObjId, this);
            }
        }
        if (sourceEffect && npc != null)
            QuestManager.Instance.DoOnAggroEvents(player, npc);
    }

    public void ClearAggroOfUnit(Unit unit)
    {
        if (unit is null)
            return;

        bool removed;
        lock (AggroTable)
        {
            // A late death callback from an old object must not clear its replacement.
            if (AggroTable.TryGetValue(unit.ObjId, out var current) && !ReferenceEquals(current.Owner, unit))
                return;
            removed = AggroTable.TryRemove(unit.ObjId, out var aggro);
            if (removed)
            {
                // Use the retained owner if this target already left the world registry.
                UnitEvents.UpdateSubscription(ref aggro.Owner.Events.OnHealed, OnAbuserHealed, false);
                UnitEvents.UpdateSubscription(ref aggro.Owner.Events.OnDeath, OnAbuserDied, false);
            }
            if (unit is Character targetPlayer)
            {
                lock (targetPlayer.IsInAggroListOf)
                    targetPlayer.IsInAggroListOf.Remove(ObjId);
                if (this is Character thisPlayer)
                {
                    thisPlayer.AssaultOn.Remove(targetPlayer.Id);
                    targetPlayer.AssaultedBy.Remove(thisPlayer.Id);
                }
            }
        }

        var npc = this as Npc;
        npc?.GroupInstance?.PruneSharedThreat();
        if (removed)
            npc?.CheckIfEmptyAggroToReturn(unit);
    }

    public void OnAbuserHealed(object sender, OnHealedArgs args)
    {
        if (this is Npc npc && npc.GroupInstance?.HandleSharedHealing(npc, args) == true)
            return;
        AddUnitAggroCore(AggroKind.Heal, args.Healer, args.HealAmount, false, true);
    }

    public void OnAbuserDied(object sender, OnDeathArgs args)
    {
        ClearAggroOfUnit(args.Victim);
    }

    public virtual void ClearAllAggro()
    {
        // Adding for tagging
        CharacterTagging.ClearAllTaggers();

        foreach (var table in AggroTable)
        {
            var unit = table.Value.Owner?.ParentWorld.GetUnit(table.Key);
            if (unit != null)
            {
                UnitEvents.UpdateSubscription(ref unit.Events.OnHealed, OnAbuserHealed, false);
                UnitEvents.UpdateSubscription(ref unit.Events.OnDeath, OnAbuserDied, false);
            }
        }
    }
}
