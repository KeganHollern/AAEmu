using System.Numerics;
using AAEmu.Game.Models.CryEngine.Readers;

namespace AAEmu.Game.Models.CryEngine.Entities;

public class NodeDescriptor(NetMissionReader netMission)
{
    public NetMissionReader NetMission { get; } = netMission;
    public int Id { get; set; }
    public Vector3 Dir { get; set; } = Vector3.Zero;
    public Vector3 Up { get; set; } = Vector3.UnitZ;
    public Vector3 Pos { get; set; } = Vector3.Zero;
    public int Index { get; set; }
    public int[] Obstacle { get; set; } = [];
    public BaiNavigationType NavigationType { get; set; }
    public byte Flags { get; set; }
    public byte Padding { get; set; }

    public bool Equals(NodeDescriptor other)
    {
        if (this == other)
            return true;

        if (other == null)
            return false;

        return Id == other.Id &&
               Index == other.Index &&
               NavigationType == other.NavigationType &&
               Flags == other.Flags &&
               Padding == other.Padding &&
               Dir.Equals(other.Dir) &&
               Up.Equals(other.Up) &&
               Pos.Equals(other.Pos) &&
               Obstacle.SequenceEqual(other.Obstacle);
    }

    public override string ToString()
    {
        return $"Pos: {Pos}, Id: {Id}, Index: {Index}, NavigationType: {NavigationType}";
    }
}
