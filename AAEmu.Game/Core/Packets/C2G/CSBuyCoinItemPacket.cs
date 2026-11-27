using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSBuyCoinItemPacket() : GamePacket(CSOffsets.CSBuyCoinItemPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var objId = stream.ReadBc();
        var id = stream.ReadUInt32();

        Logger.Trace("BuyCoinItem, objId: {0}, id: {1}", objId, id);
        var doodad = Connection.ActiveChar.ParentWorld.GetDoodad(objId);
        if (doodad == null)
        {
            Logger.Warn("BuyCoinItem, no such doodad objId: {0}", objId);
            return;
        }
        var doodadFunc = DoodadManager.Instance.GetFunc(id);
        if (doodadFunc == null)
        {
            Logger.Warn("BuyCoinItem, no doodadFunc for objId: {0}, id: {1}", objId, id);
            return;
        }
        var funcTemplate = DoodadManager.Instance.GetFuncTemplate(doodadFunc.FuncId, doodadFunc.FuncType);
        if (funcTemplate == null)
        {
            Logger.Warn("BuyCoinItem, no funcTemplate found with doodad func for objId: {0}, id: {1} funcId: {2}, funcType: {3}", objId, id, doodadFunc.FuncId, doodadFunc.FuncType);
            return;
        }
        var character = Connection.ActiveChar;
        if (doodadFunc.SkillId == DoodadFuncPurchase.Buy && funcTemplate is DoodadFuncPurchase { UsesItemPayment: true } purchase)
        {
            lock (character.StorePurchaseSyncRoot)
            {
                if (doodad.Despawn == DateTime.MinValue && doodad.CurrentFuncs.Contains(doodadFunc) &&
                    ServiceInteraction.CanReach(character, doodad))
                    purchase.CompleteDisplayPurchase(character, doodad, doodadFunc);
            }
            return;
        }

        funcTemplate.Use(character, doodad, 0);
    }
}
