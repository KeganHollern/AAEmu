using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.AI.v2.Params.Almighty;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units.Movements;

namespace AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;

public class SpawningBehavior : BaseCombatBehavior
{
    private bool _enter;

    public override void Enter()
    {
        Ai.Owner.CurrentGameStance = GameStanceType.Relaxed;
        Ai.Owner.CurrentAlertness = MoveTypeAlertness.Idle;
        // TODO 
        var _aiParams = Ai.Owner.Template.AiParams as AlmightyNpcAiParams;
        if (_aiParams != null && _aiParams.AlertToAttack && _aiParams.AlertDuration == 0)
        {
            CheckAggression();
        }
        _enter = true;
    }

    public override void Tick(TimeSpan delta)
    {
        if (!_enter)
            return; // not initialized yet Enter()

        // TODO: This follows the game's way of doing it. This will need code later, obviously
        Ai.GoToRunCommandSet();
    }

    public override void Exit()
    {
        _enter = false;
    }
}
