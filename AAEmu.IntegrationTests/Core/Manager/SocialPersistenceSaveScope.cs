using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using Moq;

namespace AAEmu.IntegrationTests.Core.Manager;

internal sealed class SocialPersistenceSaveScope : IDisposable
{
    private static readonly FieldInfo s_instance = typeof(Singleton<SaveManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object _previous;

    public SaveManager Save { get; } = new(Mock.Of<ITaskManager>(), Mock.Of<IHousingManager>(),
        Mock.Of<IMailManager>(), Mock.Of<IItemManager>(), Mock.Of<IAuctionManager>(),
        Mock.Of<ICrimeManager>(), Mock.Of<IWorldManager>(), Mock.Of<IZoneManager>());

    public SocialPersistenceSaveScope()
    {
        _previous = s_instance.GetValue(null);
        s_instance.SetValue(null, Save);
    }

    public void Dispose() => s_instance.SetValue(null, _previous);
}
