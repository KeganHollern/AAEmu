namespace AAEmu.Game.Models.CryEngine.Entities;

[Flags]
public enum BaiNavigationType : ushort
{
    Unset = 1 << 0,
    Triangular = 1 << 1,
    WaypointHuman = 1 << 2,
    Waypoint3DSurface = 1 << 3,
    Flight = 1 << 4,
    Volume = 1 << 5,
    Road = 1 << 6,
    SmartObject = 1 << 7,
    Free2D = 1 << 8,
    CustomNavigation = 1 << 9
}
