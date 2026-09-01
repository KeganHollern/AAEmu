using AAEmu.Commons.Utils;
using AAEmu.Game.Utils;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.Game.Core.Managers.Id;

public class MailIdManager() : IdManager("MailIdManager", FirstId, LastId, ObjTables, Exclude, true), IMailIdManager
{
    private static MailIdManager _instance;
    private const uint FirstId = 0x00002710; // 10000, no special reason
    private const uint LastId = 0xFFFFFFFF;
    private static readonly uint[] Exclude = [];
    // A committed claim can coexist with its read mail until the player deletes it, so duplicate IDs are valid.
    private static readonly string[,] ObjTables = { { "mails", "id" }, { "auction_mail_claims", "mail_id" } };

    public static MailIdManager Instance =>
        _instance ??= SingletonContainer.ServiceProvider?.GetService<MailIdManager>() ?? new MailIdManager();
}
