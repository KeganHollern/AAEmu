using System.Buffers.Binary;
using System.Numerics;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Items;

public class SummonSlave : Item
{
    // r208022 repair-kit descriptions and the native summon check both use 600 seconds.
    internal const int RepairDurationSeconds = 600;
    private DateTime _repairStartTime;
    private byte[] _locationDetails = new byte[16];
    public override ItemDetailType DetailType => ItemDetailType.Slave;
    public override uint DetailBytesLength => 29;

    public byte SlaveType { get; set; } // Not sure about this, captures show 2 here
    public uint SlaveDbId { get; set; }
    public byte IsDestroyed { get; set; }

    public DateTime RepairStartTime
    {
        get => _repairStartTime;
        set
        {
            _repairStartTime = value;
            if (value > DateTime.MinValue)
                IsDestroyed = 0;
        }
    }

    // r208022 stores only packed world X and Y in the final 16 detail bytes.
    public Vector3 SummonLocation
    {
        get => new(Helpers.ConvertLongX(BinaryPrimitives.ReadInt64LittleEndian(_locationDetails)),
            Helpers.ConvertLongY(BinaryPrimitives.ReadInt64LittleEndian(_locationDetails.AsSpan(8))), 0);
        set
        {
            BinaryPrimitives.WriteInt64LittleEndian(_locationDetails, Helpers.ConvertLongX(value.X));
            BinaryPrimitives.WriteInt64LittleEndian(_locationDetails.AsSpan(8), Helpers.ConvertLongY(value.Y));
        }
    }

    internal bool HasSummonLocation => BinaryPrimitives.ReadInt64LittleEndian(_locationDetails) != 0 &&
        BinaryPrimitives.ReadInt64LittleEndian(_locationDetails.AsSpan(8)) != 0;

    internal void ClearSummonLocation() => Array.Clear(_locationDetails);

    internal ErrorMessageType GetSpawnError(DateTime now, out uint secondsRemaining)
    {
        secondsRemaining = 0;
        if (IsDestroyed != 0)
            return ErrorMessageType.SlaveSpawnErrorDestroyed;
        if (RepairStartTime == DateTime.MinValue)
            return ErrorMessageType.NoErrorMessage;

        // The native gate uses time64 seconds and accepts only elapsed > 600.
        var elapsed = AAEmu.Commons.Utils.Helpers.UnixTime(now) - AAEmu.Commons.Utils.Helpers.UnixTime(RepairStartTime);
        if (elapsed > RepairDurationSeconds)
            return ErrorMessageType.NoErrorMessage;
        secondsRemaining = (uint)Math.Clamp(RepairDurationSeconds - elapsed, 0, uint.MaxValue);
        return ErrorMessageType.SlaveSpawnErrorNeedRepairTime;
    }

    public SummonSlave()
    {
        //
    }

    public SummonSlave(ulong id, ItemTemplate template, int count) : base(id, template, count)
    {
        //
    }

    public override void ReadDetails(PacketStream stream)
    {
        if (stream.LeftBytes < DetailBytesLength)
            return;
        SlaveType = stream.ReadByte(); // Type? (2 = slave?)
        SlaveDbId = stream.ReadBc(); // DbId
        IsDestroyed = stream.ReadByte();
        var repairTime = stream.ReadInt64();
        _repairStartTime = repairTime == 0 ? DateTime.MinValue : AAEmu.Commons.Utils.Helpers.UnixTime(repairTime);
        // Keep exact packed values on load. The native range gate reads both i64 coordinates.
        _locationDetails = stream.ReadBytes(16);
    }

    public override void WriteDetails(PacketStream stream)
    {
        stream.Write(SlaveType);
        stream.WriteBc(SlaveDbId);
        stream.Write(IsDestroyed);

        stream.Write(RepairStartTime);
        stream.Write(_locationDetails);
    }

    public override void OnManuallyDestroyingItem()
    {
        var owner = WorldManager.Instance.GetCharacterById((uint)OwnerId);
        if (owner == null)
            return;

        if (!owner.ParentWorld.SlaveManager.OnDeleteSlaveItem(this))
            Logger.Warn($"Failed to delete Slave attached to Item Id: {Id}, Type: {TemplateId}");
    }

    public override bool CanDestroy()
    {
        // TODO: Always allow expired items to be removed regardless if summoned or not 
        var owner = WorldManager.Instance.GetCharacterById((uint)OwnerId);
        if (owner != null)
        {
            var checkSlave = owner.ParentWorld.SlaveManager.GetActiveSlaveByOwnerObjId(owner.ObjId);
            if (checkSlave?.Id == SlaveDbId)
            {
                owner.SendErrorMessage(ErrorMessageType.SlaveSpawnItemLocked);
                return false;
            }
        }

        return true;
    }
}
