using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using System.Reflection;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class RiftPlotTests
{
    private static readonly FieldInfo WorldInstanceField = typeof(Singleton<WorldManager>)
        .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
    private object _previousWorldManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = WorldInstanceField.GetValue(null);
        WorldInstanceField.SetValue(null, new WorldManager(null, null, null, null, null));
    }

    [After(Test)]
    public void TearDown() => WorldInstanceField.SetValue(null, _previousWorldManager);

    [Test]
    public async Task UpdateTargetInfo_RetailCrimsonLaunchImpactSummon_PreservesSkyfallAndLanding()
    {
        // compact plot 143: 1241 -> 1249 -> 1242. The sibling branch uses the same selectors.
        var caster = new Npc { ObjId = 8052 };
        caster.Transform.Local.SetPosition(21382f, 12611f, 300f);
        var skill = new Skill(new SkillTemplate { Id = 15298, TargetRelation = SkillTargetRelation.Any });
        var state = new PlotState(caster, null, caster, null, null, skill);
        var launch = Node(caster, caster);
        launch.UpdateTargetInfo(new PlotEventTemplate
        {
            Id = 1241, SourceUpdateMethodId = 3, TargetUpdateMethodId = 7,
            TargetUpdateMethodParam3 = 15000
        }, state);
        var impact = Node(launch.Source, launch.Target);
        impact.UpdateTargetInfo(new PlotEventTemplate
        {
            Id = 1249, SourceUpdateMethodId = 4, TargetUpdateMethodId = 7,
            TargetUpdateMethodParam4 = 500000
        }, state);
        var summon = Node(impact.Source, impact.Target);
        summon.UpdateTargetInfo(new PlotEventTemplate
        {
            Id = 1242, SourceUpdateMethodId = 3, TargetUpdateMethodId = 4
        }, state);

        await Assert.That(launch.Target.Transform.World.Position.Z).IsEqualTo(300f);
        await Assert.That(impact.Source).IsSameReferenceAs(launch.Target);
        await Assert.That(impact.Target.Transform.World.Position.Z).IsEqualTo(223f);
        await Assert.That(impact.Target.Transform.World.Position.X).IsEqualTo(launch.Target.Transform.World.Position.X);
        await Assert.That(impact.Target.Transform.World.Position.Y).IsEqualTo(launch.Target.Transform.World.Position.Y);
        await Assert.That(summon.Target).IsSameReferenceAs(impact.Target);
        await Assert.That(summon.EffectedTargets.Single()).IsSameReferenceAs(impact.Target);
    }

    [Test]
    [Arguments(100000, 400f)]
    [Arguments(-100000, 200f)]
    public async Task ResolvePlotLandHeight_UnrelatedLargeOffset_IsNotAnImpact(int parameter, float expected)
    {
        await Assert.That(PlotTargetInfo.ResolvePlotLandHeight(300f, parameter, 100f)).IsEqualTo(expected);
    }

    private static PlotTargetInfo Node(BaseUnit source, BaseUnit target) => new(source, target, _ => 223f);
}
