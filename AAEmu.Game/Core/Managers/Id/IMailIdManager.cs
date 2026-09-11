namespace AAEmu.Game.Core.Managers.Id;

public interface IMailIdManager : IIdManager
{
    void RetainId(uint mailId);
}
