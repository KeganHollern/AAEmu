using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj.Funcs;

[NotInParallel]
public sealed class DoodadFuncRatioRespawnTests
{
    private const uint TargetTemplateId = 3085;
    private static readonly FieldInfo s_managerInstanceField =
        typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    private DoodadManager _previousManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousManager = (DoodadManager)s_managerInstanceField.GetValue(null);
        var manager = new DoodadManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);
        SetPrivateField(manager, "_templates", new Dictionary<uint, DoodadTemplate>
        {
            [TargetTemplateId] = new DoodadTemplate { Id = TargetTemplateId }
        });
        s_managerInstanceField.SetValue(null, manager);
    }

    [After(Test)]
    public void TearDown()
    {
        s_managerInstanceField.SetValue(null, _previousManager);
    }

    [Test]
    public async Task Use_RatioHit_ReplacesDoodadThroughSameSpawner()
    {
        var spawner = new RecordingDoodadSpawner
        {
            Id = 10,
            UnitId = 2768,
            SpawnResult = new Doodad { TemplateId = TargetTemplateId }
        };
        var owner = CreateOwner(spawner, 1000, 5000);
        var function = new DoodadFuncRatioRespawn { Ratio = 2000, SpawnDoodadId = TargetTemplateId };

        var stopped = function.Use(null, owner);

        await Assert.That(stopped).IsTrue();
        await Assert.That(spawner.Despawned).IsSameReferenceAs(owner);
        await Assert.That(spawner.SpawnCalls).IsEqualTo(1);
        await Assert.That(spawner.SelectedTemplateId).IsEqualTo(TargetTemplateId);
        await Assert.That(spawner.Calls.SequenceEqual(["despawn", "spawn"])).IsTrue();
    }

    [Test]
    public async Task Use_RatioMiss_KeepsCurrentDoodad()
    {
        var spawner = new RecordingDoodadSpawner { Id = 10, UnitId = 2768 };
        var owner = CreateOwner(spawner, 3000, 5000);
        var function = new DoodadFuncRatioRespawn { Ratio = 2000, SpawnDoodadId = TargetTemplateId };

        var stopped = function.Use(null, owner);

        await Assert.That(stopped).IsFalse();
        await Assert.That(spawner.Calls).IsEmpty();
        await Assert.That(owner.CumulativePhaseRatio).IsEqualTo(3000);
    }

    [Test]
    public async Task Use_MissingSpawner_KeepsCurrentDoodad()
    {
        var owner = CreateOwner(null, 1000, 5000);
        var function = new DoodadFuncRatioRespawn { Ratio = 2000, SpawnDoodadId = TargetTemplateId };

        var stopped = function.Use(null, owner);

        await Assert.That(stopped).IsFalse();
        await Assert.That(owner.CumulativePhaseRatio).IsEqualTo(3000);
    }

    [Test]
    public async Task Use_UnknownTarget_KeepsCurrentDoodad()
    {
        var spawner = new RecordingDoodadSpawner { Id = 10, UnitId = 2768 };
        var owner = CreateOwner(spawner, 1000, 5000);
        var function = new DoodadFuncRatioRespawn { Ratio = 2000, SpawnDoodadId = 999999 };

        var stopped = function.Use(null, owner);

        await Assert.That(stopped).IsFalse();
        await Assert.That(spawner.Calls).IsEmpty();
        await Assert.That(owner.CumulativePhaseRatio).IsEqualTo(3000);
    }

    private static Doodad CreateOwner(DoodadSpawner spawner, int phaseRatio, int cumulativeRatio)
    {
        var owner = new Doodad
        {
            TemplateId = 2768,
            Template = new DoodadTemplate { Id = 2768 },
            Spawner = spawner,
            CumulativePhaseRatio = cumulativeRatio
        };
        typeof(Doodad).GetProperty(nameof(Doodad.PhaseRatio))!.SetValue(owner, phaseRatio);
        return owner;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private sealed class RecordingDoodadSpawner : DoodadSpawner
    {
        public List<string> Calls { get; } = [];
        public Doodad Despawned { get; private set; }
        public int SpawnCalls { get; private set; }
        public uint SelectedTemplateId { get; private set; }
        public Doodad SpawnResult { get; init; }

        public override void Despawn(Doodad doodad)
        {
            Calls.Add("despawn");
            Despawned = doodad;
        }

        public override Doodad Spawn(uint objId)
        {
            Calls.Add("spawn");
            SpawnCalls++;
            SelectedTemplateId = RespawnDoodadTemplateId;
            RespawnDoodadTemplateId = 0;
            return SpawnResult;
        }
    }
}
