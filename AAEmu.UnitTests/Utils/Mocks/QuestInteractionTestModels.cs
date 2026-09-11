using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Utils.Mocks;

internal sealed class QuestInteractionTestModels : IDisposable
{
    private static readonly FieldInfo InstanceField = typeof(Singleton<ModelManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object _previous = InstanceField.GetValue(null);
    private static readonly FieldInfo SkillsInstanceField = typeof(Singleton<SkillManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object _previousSkills = SkillsInstanceField.GetValue(null);

    public QuestInteractionTestModels()
    {
        var manager = new ModelManager();
        typeof(ModelManager).GetField("_modelTypes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<uint, ModelType>
            {
                [1] = new() { Id = 1, SubType = "ActorModel", SubId = 1 }
            });
        typeof(ModelManager).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<string, Dictionary<uint, Model>>
            {
                ["ActorModel"] = new() { [1] = new ActorModel { Id = 1, Height = 1, Radius = 0.5f } }
            });
        InstanceField.SetValue(null, manager);
        var skills = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        typeof(SkillManager).GetField("_taggedBuffs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, List<uint>>());
        SkillsInstanceField.SetValue(null, skills);
    }

    public static Region CreateRegion(WorldInstance world)
    {
        var region = new Region(world, 0, 0, 0);
        typeof(Region).GetField("_neighbors", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(region, new[] { region });
        return region;
    }

    public void Dispose()
    {
        SkillsInstanceField.SetValue(null, _previousSkills);
        InstanceField.SetValue(null, _previous);
    }
}
