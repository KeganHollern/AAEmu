using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncExitIndun : DoodadFuncTemplate
{
    // doodad_funcs
    public uint ReturnPointId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Info("DoodadFuncExitIndun, ReturnPointId: {0}", ReturnPointId);

        if (caster is Character character)
        {
            if (ReturnPointId == 0 && character.MainWorldPosition != null)
            {
                // aaemu-cluster#92 (#102): remember which dungeon is being left before the character
                // is moved back to the main world (which changes ParentWorld).
                var dungeon = character.ParentWorld?.DungeonInstance;
                if (IndunManager.Instance.RequestLeaveInstance(character) && dungeon != null)
                {
                    // Stamp the empty timestamp when this was the last player out so the grace sweep
                    // in IndunManager can reclaim the instance later. Do NOT destroy it immediately;
                    // players may re-enter after a wipe/repair trip.
                    dungeon.MarkEmptyIfNoPlayers();
                }
            }
            else
            {
                // TODO in db not have a entries, but we can change this xD
                Logger.Info("DoodadFuncExitIndun, Not have return point!");
                character.SendErrorMessage(ErrorMessageType.InvalidReturnPosInstance); // ошибка, не можете выйти сейчас из данжона
                //character.SendErrorMessage(ErrorMessageType.TryLaterInstance); // ошибка данжона, пробуй еще раз
                //character.SendErrorMessage(ErrorMessageType.InvalidStateInstance); // данжон уже загружен
                //character.SendErrorMessage(ErrorMessageType.ProhibitedInInstance); // нельзя это сделать внутри данжона
                //character.SendErrorMessage(ErrorMessageType.InstanceVisitLimit); // Ты израсходовал лимит на вход в данжон. Пробуй позже.
            }
        }
    }
}
