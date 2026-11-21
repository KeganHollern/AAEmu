using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimeCatcherToken_CheckpointPersistsOnlyABelowLimitCraft(bool capped)
    {
        const uint coinsId = 30214;
        const uint tokenId = 30217;
        using var graph = new SendGraph();
        var player = graph.Sender;
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
        templates[coinsId] = new ItemTemplate { Id = coinsId, MaxCount = 1000, FixedGrade = -1 };
        templates[tokenId] = new ItemTemplate { Id = tokenId, MaxCount = 1, PickupLimit = 1, FixedGrade = -1 };
        var coins = graph.AddItem(0);
        coins.TemplateId = coinsId;
        coins.Template = templates[coinsId];
        coins.Count = 200;
        if (capped)
        {
            var token = graph.AddItem(1);
            token.TemplateId = tokenId;
            token.Template = templates[tokenId];
            token.Count = 1;
        }

        var itemIds = (IItemIdManager)typeof(ItemManager)
            .GetField("<itemIdManager>P", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
        Mock.Get(itemIds).Setup(manager => manager.GetNextId()).Returns(player.Id + 90);
        player.InitializeLaborCache(4276, DateTime.UtcNow);
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},4276) ON DUPLICATE KEY UPDATE labor=4276");
        Assert.True(graph.Save.TryCommitEconomy([player]));

        var recipe = new Craft { Id = 6078, SkillId = 23347,
            CraftMaterials = [new CraftMaterial { ItemId = coinsId, Amount = 200 }],
            CraftProducts = [new CraftProduct { ItemId = tokenId, Amount = 1 }] };
        var craft = player.Craft = new CharacterCraft(player) { IsCrafting = true };
        typeof(CharacterCraft).GetProperty("CurrentCraft", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(craft, recipe);
        typeof(CharacterCraft).GetProperty("Count", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(craft, 1);
        var skill = new Skill(new SkillTemplate { Id = recipe.SkillId });
        var debugInfo = AppConfiguration.Instance.DebugInfo;
        try
        {
            AppConfiguration.Instance.DebugInfo = false;
            craft.EndCraft(skill);
        }
        finally
        {
            AppConfiguration.Instance.DebugInfo = debugInfo;
        }

        Assert.Equal(capped, skill.Cancelled);
        Assert.True(graph.Save.TryCommitEconomy([player]));
        Assert.Equal(capped ? 200 : 0, Scalar($"SELECT COALESCE(SUM(count), 0) FROM items WHERE owner={player.Id} AND template_id={coinsId}"));
        Assert.Equal(1, Scalar($"SELECT COALESCE(SUM(count), 0) FROM items WHERE owner={player.Id} AND template_id={tokenId}"));
        Assert.Equal(4276, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
    }
}
