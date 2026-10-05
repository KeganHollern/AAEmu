using System.Numerics;

using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

public sealed partial class AggroSpecialEffectsTests
{
    [Test]
    [Arguments(0)]
    [Arguments(10)]
    public async Task AggroReset_ClearsItsOwnThreatAndSelectionThenUsesNormalReturn(int distance)
    {
        var affected = CreateNpc();
        var other = CreateNpc();
        var first = CreateCharacter();
        var second = CreateCharacter();
        var ai = new ResetProbeAi { Owner = affected };
        affected.Ai = ai;
        ai.Start();
        ai.GoToCombat();
        affected.Transform.Local.SetPosition(new Vector3(distance, 0, 0));
        affected.AddUnitAggro(AggroKind.Damage, first, 50);
        affected.AddUnitAggro(AggroKind.Heal, second, 100);
        other.AddUnitAggro(AggroKind.Heal, first, 100);
        affected.CurrentTarget = first;
        affected.CurrentAggroTarget = first;
        affected.IsInBattle = true;

        new AggroReset().Execute(affected, null, affected, null, null, null, null, DateTime.UtcNow, 0, 1, 0, 0);

        await Assert.That(affected.AggroTable).IsEmpty();
        await Assert.That(affected.CurrentTarget).IsNull();
        await Assert.That(affected.CurrentAggroTarget).IsNull();
        await Assert.That(affected.CharacterTagging.Tagger).IsNull();
        await Assert.That(affected.IsInBattle).IsFalse();
        await Assert.That(first.IsInAggroListOf.ContainsKey(affected.ObjId)).IsFalse();
        await Assert.That(first.IsInAggroListOf.ContainsKey(other.ObjId)).IsTrue();
        await Assert.That(second.IsInAggroListOf).IsEmpty();
        await Assert.That(other.AggroTable[first.ObjId].HealAggro).IsEqualTo(60);
        await Assert.That(ai.GetCurrentBehavior()).IsSameReferenceAs(ai.Return);
        await Assert.That(ai.Return.Entries).IsEqualTo(1);
        await Assert.That(affected.Hp).IsEqualTo(100);
    }

    [Test]
    public async Task AggroReset_UnknownArgumentsDoNotGuessAnotherResetRule()
    {
        var npc = CreateNpc();
        var player = CreateCharacter();
        npc.AddUnitAggro(AggroKind.Heal, player, 100);
        npc.CurrentTarget = player;

        Execute(new AggroReset(), npc, npc);

        await Assert.That(npc.AggroTable[player.ObjId].HealAggro).IsEqualTo(60);
        await Assert.That(npc.CurrentTarget).IsSameReferenceAs(player);
    }

    private sealed class ResetProbeAi : NpcAi
    {
        public ResetProbeBehavior Return { get; } = new();

        protected override void Build()
        {
            AddBehavior(BehaviorKind.Attack, new ResetProbeBehavior())
                .AddTransition(TransitionEvent.OnNoAggroTarget, BehaviorKind.ReturnState);
            AddBehavior(BehaviorKind.ReturnState, Return);
        }
    }

    private sealed class ResetProbeBehavior : Behavior
    {
        public int Entries { get; private set; }
        public override void Enter() => Entries++;
        public override void Tick(TimeSpan delta) { }
        public override void Exit() { }
    }
}
