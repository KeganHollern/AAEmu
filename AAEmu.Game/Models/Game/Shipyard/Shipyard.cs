using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Shipyard;

public sealed partial class Shipyard : Unit
{
    public override float Scale => 1.0f;
    private readonly object _lock = new();
    private bool _isDirty;
    private int _allAction;
    private int _baseAction;
    private int _numAction;
    private int _currentStep;
    private int _hpBeforeDeath;
    private ShipyardsTemplate _template;

    public override UnitTypeFlag TypeFlag { get => UnitTypeFlag.Shipyard; }
    public override BaseUnitType BaseUnitType => BaseUnitType.Shipyard;
    public ShipyardData ShipyardData { get; set; }
    public ShipyardsTemplate Template
    {
        get => _template;
        set
        {
            _template = value;
            _allAction = _template.ShipyardSteps.Values.Sum(step => step.NumActions);
        }
    }

    public bool IsDirty { get => _isDirty; set => _isDirty = value; }
    public int AllAction { get => _allAction; set { _allAction = value; _isDirty = true; } }
    public int BaseAction
    {
        get => _baseAction;
        private set
        {
            _baseAction = value; _isDirty = true;
        }
    }
    public int CurrentAction => BaseAction + NumAction;
    public int NumAction
    {
        get => _numAction;
        private set
        {
            _numAction = value; _isDirty = true;
        }
    }
    public int CurrentStep
    {
        get => _currentStep;
        private set
        {
            _currentStep = value;
            _isDirty = true;
            ModelId = _currentStep == -1 ? Template.MainModelId : Template.ShipyardSteps[_currentStep].ModelId;
            BaseAction = 0;
            if (_currentStep <= 0) { return; }
            for (var i = 0; i < _currentStep; i++)
                BaseAction += Template.ShipyardSteps[i].NumActions;
        }
    }

    public Shipyard()
    {
        IsDirty = true;
    }

    public override void AddVisibleObject(Character character)
    {
        character.SendPacket(new SCUnitStatePacket(this));
        character.SendPacket(new SCShipyardStatePacket(ShipyardData));

        base.AddVisibleObject(character);
    }

    public override void RemoveVisibleObject(Character character)
    {
        base.RemoveVisibleObject(character);

        character.SendPacket(new SCUnitsRemovedPacket([ObjId]));
    }

    #region Attributes

    public int Str
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.Str);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var result = formula.Evaluate(parameters);
            var res = (int)result;
            return (int)CalculateWithBonuses(res, UnitAttribute.Str);
        }
    }

    public int Dex
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.Dex);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.Dex);
        }
    }

    public int Sta
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.Sta);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.Sta);
        }
    }

    public int Int
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.Int);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.Int);
        }
    }

    public int Spi
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.Spi);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.Spi);
        }
    }

    public int Fai
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.Fai);
            var parameters = new Dictionary<string, double> { ["level"] = Level };
            var res = (int)formula.Evaluate(parameters);
            return (int)CalculateWithBonuses(res, UnitAttribute.Fai);
        }
    }

    public override int MaxHp
    {
        get
        {
            var formula =
                FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.MaxHealth);
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
            return (int)CalculateWithBonuses(res, UnitAttribute.MaxHealth);
        }
    }

    public override int HpRegen
    {
        get
        {
            var formula =
                FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.HealthRegen);
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
            res += Spi / 10;
            return (int)CalculateWithBonuses(res, UnitAttribute.HealthRegen);
        }
    }

    public override int PersistentHpRegen
    {
        get
        {
            var formula = FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard,
                UnitFormulaKind.PersistentHealthRegen);
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
            res /= 5; // TODO ...
            return (int)CalculateWithBonuses(res, UnitAttribute.PersistentHealthRegen);
        }
    }

    public override int MaxMp
    {
        get
        {
            var formula =
                FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.MaxMana);
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
            return (int)CalculateWithBonuses(res, UnitAttribute.MaxMana);
        }
    }

    public override int MpRegen
    {
        get
        {
            var formula =
                FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard, UnitFormulaKind.ManaRegen);
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
            res += Spi / 10;
            return (int)CalculateWithBonuses(res, UnitAttribute.ManaRegen);
        }
    }

    public override int PersistentMpRegen
    {
        get
        {
            var formula =
                FormulaManager.Instance.GetUnitFormula(FormulaOwnerType.Shipyard,
                    UnitFormulaKind.PersistentManaRegen);
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
            res /= 5; // TODO ...
            return (int)CalculateWithBonuses(res, UnitAttribute.PersistentManaRegen);
        }
    }

    #endregion

    public override void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!Retiring && !Retired)
                base.ReduceCurrentHp(attacker, value, killReason);
        }
    }

    public override void PostUpdateCurrentHp(BaseUnit attacker, int oldHp, int newHp,
        KillReason killReason = KillReason.Damage)
    {
        _hpBeforeDeath = oldHp;
        try
        {
            base.PostUpdateCurrentHp(attacker, oldHp, newHp, killReason);
        }
        finally
        {
            _hpBeforeDeath = 0;
        }
    }

    public override void DoDie(BaseUnit killer, KillReason killReason)
    {
        var previousHp = _hpBeforeDeath;
        ShipyardManager.Instance.DestroyShipyard(this, () => base.DoDie(killer, killReason), () =>
        {
            if (previousHp <= 0)
                return;
            Hp = previousHp;
            BroadcastPacket(new SCUnitPointsPacket(ObjId, Hp, Mp), true);
        });
    }

    public void AddBuildAction()
    {
        if (CurrentStep == -1)
            return;

        lock (_lock)
        {
            var nextAction = NumAction + 1;
            if (Template.ShipyardSteps[CurrentStep].NumActions > nextAction)
                NumAction = nextAction;
            else
            {
                NumAction = 0;
                var nextStep = CurrentStep + 1;
                if (Template.ShipyardSteps.Count > nextStep)
                    CurrentStep = nextStep;
                else
                {
                    CurrentStep = -1;
                }
            }
        }
    }

    internal Action CaptureConstructionState()
    {
        var step = _currentStep;
        var baseAction = _baseAction;
        var numAction = _numAction;
        var modelId = ModelId;
        var dirty = _isDirty;
        var dataStep = ShipyardData.Step;
        var dataActions = ShipyardData.Actions;
        var ceremonyEnd = CeremonyEnd;
        var completionItemId = CompletionItemId;
        return () =>
        {
            lock (_lock)
            {
                _currentStep = step;
                _baseAction = baseAction;
                _numAction = numAction;
                ModelId = modelId;
                _isDirty = dirty;
                ShipyardData.Step = dataStep;
                ShipyardData.Actions = dataActions;
                CeremonyEnd = ceremonyEnd;
                CompletionItemId = completionItemId;
            }
        };
    }
}
