namespace AAEmu.Game.Core.Managers;

public interface IAccessLevelManager : ILoadable
{
    int GetLevel(params string[] commandPath);
}
