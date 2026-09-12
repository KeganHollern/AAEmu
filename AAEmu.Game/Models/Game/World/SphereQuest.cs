using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.World;

public enum AreaSphereTriggerCondition
{
    None = 0,
    TriggerOnceAtAll = 1,
    TriggerOnceInRuntime = 2,
    TriggerEveryNTimeAfter = 3
}

/// <summary>
/// Sphere location data
/// </summary>
public class SphereQuest
{
    private Spheres.Spheres _dbSphere;

    public Spheres.Spheres DbSphere
    {
        get
        {
            if (_dbSphere == null)
            {
                var sphereId = SphereGameData.Instance.GetSphereIdFromQuest(QuestId);
                _dbSphere = SphereGameData.Instance.GetSphere(sphereId);
            }
            return _dbSphere;
        }
        // set => _dbSphere = value;
    }

    public uint ZoneId { get; set; }
    public string WorldId { get; set; }
    public uint QuestId { get; set; }
    public uint ComponentId { get; set; }
    public float Radius { get; set; }
    public Vector3 Xyz { get; set; } = Vector3.Zero;

    public bool Contains(Vector3 pos)
    {
        return MathUtil.CalculateDistance(Xyz, pos, true) <= Radius;
    }
}

/// <summary>
/// Per user sphere trigger data
/// </summary>
public class SphereQuestTrigger
{
    private bool _inside;

    /// <summary>
    /// Sphere data to check against
    /// </summary>
    public SphereQuest Sphere { get; set; }

    /// <summary>
    /// Owner of this SphereQuestTrigger
    /// </summary>
    public ICharacter Owner { get; set; }

    /// <summary>
    /// Related Quest object
    /// </summary>
    public Quest Quest { get; set; }

    /// <summary>
    /// If set, the nearest NPC with this template Id is used as a center point for the check
    /// </summary>
    public uint NpcTemplate { get; set; }

    /// <summary>
    /// Sphere requirements for this act, which can differ between components of one quest.
    /// </summary>
    public uint SphereId { get; set; }

    /// <summary>
    /// Last location of the Owner
    /// </summary>
    public Vector3 LastCheckLocation { get; set; } = Vector3.Zero;

    /// <summary>
    /// Minimum time needed between checks
    /// </summary>
    public int TickRate { get; set; }

    /// <summary>
    /// Last check time
    /// </summary>
    private DateTime LastTick { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Handle the tick for this SphereQuestTrigger
    /// </summary>
    /// <param name="delta"></param>
    public void Tick(TimeSpan delta)
    {
        var now = DateTime.UtcNow;
        if (TickRate > 0 && (now - LastTick).TotalMilliseconds < TickRate)
            return;
        if (Owner?.Transform?.World == null)
            return;

        var position = Owner.Transform.World.Position;
        var dbSphere = SphereId != 0 ? SphereGameData.Instance.GetSphere(SphereId) : Sphere.DbSphere;
        var triggerActive = dbSphere == null || UnitRequirementsGameData.Instance.CanTriggerSphere(dbSphere, (BaseUnit)Owner);
        var newInside = false;
        if (triggerActive)
        {
            if (NpcTemplate == 0)
            {
                newInside = Sphere.Contains(position);
            }
            else
            {
                var character = (Character)Owner;
                var npcsNear = WorldManager.GetAround<Npc>(character, Sphere.Radius, false);
                newInside = npcsNear.Any(npc => npc.TemplateId == NpcTemplate &&
                    npc.ParentWorld == character.ParentWorld &&
                    MathUtil.CalculateDistance(npc.Transform.World.Position, position, true) <= Sphere.Radius);
            }
        }

        // Keep the previous result. Rechecking an old position against a moving
        // NPC loses transitions, and the world origin is not an initial sample.
        var oldInside = _inside;
        var oldPosition = LastCheckLocation;
        _inside = newInside;
        LastCheckLocation = position;
        LastTick = now;

        if (!oldInside && newInside)
            QuestManager.Instance.DoOnEnterSphereEvents(Owner, Sphere, oldPosition);
        else if (oldInside && !newInside)
            QuestManager.Instance.DoOnExitSphereEvents(Owner, Sphere, oldPosition);
    }
}

/// <summary>
/// Global quest starter triggers
/// </summary>
public class SphereQuestStarter
{
    public SphereQuest Sphere { get; set; }
    public uint QuestTemplateId { get; set; }
    public uint SphereId { get; set; }
    public Region Region { get; set; }
}
