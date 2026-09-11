using AAEmu.Game.Models.Account;

namespace AAEmu.Game.Utils.Scripts;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandPermissionAttribute(GamePermission permission) : Attribute
{
    public GamePermission Permission { get; } = permission;
    public bool Enabled { get; set; } = true;
    public bool ShowInHelp { get; set; } = true;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CommandActionPermissionAttribute(GamePermission permission, params string[] actions) : Attribute
{
    public GamePermission Permission { get; } = permission;
    // An empty action list restricts every nonempty argument list, but allows the no-argument status view.
    public string[] Actions { get; } = actions;
}
