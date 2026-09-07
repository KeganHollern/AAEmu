using AAEmu.Commons.Utils;
using AAEmu.Game.Utils;

using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.Game.Core.Managers.Id;

public class ItemIdManager() : IdManager(
    "ItemIdManager",
    FirstId,
    LastId,
    ObjTables,
    Exclude,
    true,
    RetainedObjTables), IItemIdManager
{
    private static ItemIdManager _instance;
    private const uint FirstId = 0x01000000;
    private const uint LastId = 0xFFFFFFFF;
    private static readonly uint[] Exclude = [];
    // A claimed attachment can retain its source ID in the receipt after it has merged into another stack.
    private static readonly string[,] ObjTables = { { "items", "id" }, { "auction_mail_claims", "item_id" } };
    private static readonly string[,] RetainedObjTables = { { "auction_mail_claims", "item_id" } };

    public static ItemIdManager Instance =>
        _instance ??= SingletonContainer.ServiceProvider?.GetService<ItemIdManager>() ?? new ItemIdManager();
}
