namespace AAEmu.Commons.Network;

public abstract class PacketBase<T> : PacketMarshaler
{
    public ushort TypeId { get; }

    public T Connection { protected get; set; }
    public virtual PacketLogLevel LogLevel => PacketLogLevel.Trace;

    protected PacketBase(ushort typeId)
    {
        TypeId = typeId;
    }

    protected static bool IsLogLevelEnabled(PacketLogLevel logLevel)
    {
        return logLevel switch
        {
            PacketLogLevel.Trace => Logger.IsTraceEnabled,
            PacketLogLevel.Debug => Logger.IsDebugEnabled,
            PacketLogLevel.Info => Logger.IsInfoEnabled,
            PacketLogLevel.Warning => Logger.IsWarnEnabled,
            PacketLogLevel.Error => Logger.IsErrorEnabled,
            PacketLogLevel.Fatal => Logger.IsFatalEnabled,
            _ => false
        };
    }

    protected static void LogPacket(PacketLogLevel logLevel, string message)
    {
        switch (logLevel)
        {
            case PacketLogLevel.Trace:
                Logger.Trace(message);
                break;
            case PacketLogLevel.Debug:
                Logger.Debug(message);
                break;
            case PacketLogLevel.Info:
                Logger.Info(message);
                break;
            case PacketLogLevel.Warning:
                Logger.Warn(message);
                break;
            case PacketLogLevel.Error:
                Logger.Error(message);
                break;
            case PacketLogLevel.Fatal:
                Logger.Fatal(message);
                break;
        }
    }

    public abstract PacketStream Encode();
    public abstract PacketBase<T> Decode(PacketStream ps);
}
