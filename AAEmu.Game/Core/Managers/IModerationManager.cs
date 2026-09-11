using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers;

public interface IModerationManager
{
    Task<ModerationResult> ModerateAsync(Character actor, ModerationTarget target, ModerationAction action,
        ulong durationSeconds, string reason);
    Task<bool> RefreshAccountAsync(uint accountId);
    Task RefreshLiveAccountsAsync();
    bool TryAdmit(uint accountId, Action admit);
    bool CanChat(uint accountId);
    bool TryKick(Character actor, ModerationTarget target, string reason, out string error);
    void CompleteRequest(ModerationResult result);
    void ApplyState(ModerationState state);
    bool TryResolveTarget(string value, out ModerationTarget target);
}
