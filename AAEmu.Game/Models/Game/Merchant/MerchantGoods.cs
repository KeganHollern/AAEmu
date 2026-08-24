namespace AAEmu.Game.Models.Game.Merchant;

public class MerchantGoods(uint id, byte kind = 0)
{
    public uint Id { get; set; } = id;
    public byte Kind { get; set; } = kind;
    public List<MerchantGoodsItem> Items { get; set; } = [];

    public AAEmu.Game.Models.Game.Items.ShopCurrencyType? Currency => Kind switch
    {
        0 => AAEmu.Game.Models.Game.Items.ShopCurrencyType.Money,
        1 => AAEmu.Game.Models.Game.Items.ShopCurrencyType.Honor,
        3 => AAEmu.Game.Models.Game.Items.ShopCurrencyType.VocationBadges,
        _ => null
    };

    // NOTE: If there is ever a case where one itemTemplate is sold at multiple grades, then this code needs a rework
    public bool SellsItem(uint itemTemplateId)
    {
        foreach (var i in Items)
            if (i.ItemTemplateId == itemTemplateId)
                return true;
        return false;
    }

    public bool TryGetItem(uint itemTemplateId, out MerchantGoodsItem item)
    {
        item = Items.FirstOrDefault(candidate => candidate.ItemTemplateId == itemTemplateId);
        return item != null;
    }

    public void AddItemToStock(uint itemTemplateId, byte itemGrade)
    {
        if (SellsItem(itemTemplateId))
            return;
        var newItem = new MerchantGoodsItem { ItemTemplateId = itemTemplateId, Grade = itemGrade };

        Items.Add(newItem);
    }
}

public class MerchantGoodsItem
{
    public uint ItemTemplateId;
    public byte Grade;
}
