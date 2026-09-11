using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Core.Managers;

public interface ICommandAuditStore
{
    void Start(CommandAuditEntry entry);
    void Complete(CommandAuditEntry entry);
}
