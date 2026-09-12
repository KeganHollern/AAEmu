using AAEmu.Game.Utils.DB;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.S2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Stream;

namespace AAEmu.Game.Core.Managers.Stream;

public partial class UccManager
{
    private readonly Dictionary<StreamConnection, PendingUpload> _pendingUploads = [];
    internal Dictionary<uint, uint> Patterns { get; } = [];
    internal Func<Doodad, DoodadFuncStampMaker> ResolveStampMaker { get; set; } = FindStampMaker;
    internal Func<Character, Ucc, bool> CommitPurchase { get; set; } = (character, ucc) =>
        SaveManager.Instance.TryCommitEconomy([character], context =>
        {
            using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            ucc.Save(command);
        });

    private sealed record PendingUpload(Character Character, Doodad Printer, DoodadFuncStampMaker Maker,
        DefaultUcc Ucc, UccUploadHandle Handle);

    private void LoadPatterns()
    {
        using var connection = SQLite.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind_id FROM emblem_patterns";
        using var reader = command.ExecuteReader();
        Patterns.Clear();
        while (reader.Read())
            Patterns.Add(Convert.ToUInt32(reader["id"]), Convert.ToUInt32(reader["kind_id"]));
    }

    private static DoodadFuncStampMaker FindStampMaker(Doodad printer)
    {
        var function = printer.CurrentFuncs.FirstOrDefault(func => func.FuncType == nameof(DoodadFuncStampMaker));
        return function == null ? null : DoodadManager.Instance.GetFuncTemplate(function.FuncId, function.FuncType) as DoodadFuncStampMaker;
    }

    public bool StartUpload(StreamConnection connection, uint printerId, int expectedSize, DefaultUcc ucc)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_pendingUploads.ContainsKey(connection))
            {
                connection.GameConnection?.ActiveChar?.SendErrorMessage(ErrorMessageType.UccOnUpload);
                return false;
            }
            var character = connection.GameConnection?.ActiveChar;
            if (printerId == 0 || character == null || !character.IsOnline || !ReferenceEquals(character.Connection, connection.GameConnection) ||
                !ServiceInteraction.CanUseDoodad(character, nameof(DoodadFuncStampMaker), printerId))
                return FailUpload(connection, ErrorMessageType.TooFarAway);
            var printer = (Doodad)character.CurrentInteractionObject;
            var maker = ResolveStampMaker(printer);
            var custom = ucc is CustomUcc;
            if (maker == null || maker.ItemId == 0 || maker.ConsumeMoney < 0 || maker.ConsumeCount <= 0 ||
                (custom && maker.ConsumeItemId == 0) || ucc == null ||
                (custom ? expectedSize is < UccUploadHandle.MinimumDdsSize or > UccUploadHandle.MaximumDdsSize : expectedSize != 0) ||
                !ValidPatterns(ucc))
                return FailUpload(connection, ErrorMessageType.UccInvalidData);

            ucc.UploaderId = character.Id;
            var handle = custom ? new UccUploadHandle(expectedSize, (CustomUcc)ucc) : null;
            _pendingUploads.Add(connection, new PendingUpload(character, printer, maker, ucc, handle));
            connection.SendPacket(new TCEmblemStreamRecvStatusPacket(custom ? EmblemStreamStatus.Continue : EmblemStreamStatus.Start));
            return true;
        }
    }

    internal bool ValidPatterns(DefaultUcc ucc) =>
        (ucc.Pattern1 == 0 || Patterns.TryGetValue(ucc.Pattern1, out var background) && background is >= 1 and <= 3) &&
        (ucc.Pattern2 == 0 ? ucc is CustomUcc : Patterns.TryGetValue(ucc.Pattern2, out var foreground) && foreground == 4) &&
        new[] { ucc.Color1R, ucc.Color1G, ucc.Color1B, ucc.Color2R, ucc.Color2G, ucc.Color2B,
            ucc.Color3R, ucc.Color3G, ucc.Color3B }.All(color => color <= byte.MaxValue);

    public bool UploadPart(StreamConnection connection, UccPart part)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!_pendingUploads.TryGetValue(connection, out var pending) || pending.Handle == null ||
                !CurrentUpload(connection, pending) || !pending.Handle.TryAddPart(part))
                return FailUpload(connection, ErrorMessageType.UccInvalidData);
            // The client sends CTEmblemStreamUploadStatus(0) after the last acknowledged part.
            connection.SendPacket(new TCEmblemStreamRecvStatusPacket(EmblemStreamStatus.Continue));
            return true;
        }
    }

    private bool CurrentUpload(StreamConnection connection, PendingUpload pending) =>
        ReferenceEquals(connection.GameConnection?.ActiveChar, pending.Character) && pending.Character.IsOnline &&
        ReferenceEquals(pending.Character.Connection, connection.GameConnection) &&
        ReferenceEquals(pending.Character.CurrentInteractionObject, pending.Printer) &&
        ServiceInteraction.CanUseDoodad(pending.Character, nameof(DoodadFuncStampMaker), pending.Printer.ObjId) &&
        ReferenceEquals(ResolveStampMaker(pending.Printer), pending.Maker);

    public bool ConfirmDefaultUcc(StreamConnection connection, byte status)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!_pendingUploads.Remove(connection, out var pending) || status != 0 || !CurrentUpload(connection, pending) ||
                pending.Handle != null && !pending.Handle.TryFinalizeUpload())
                return FailUpload(connection, ErrorMessageType.UccInvalidData);
            var character = pending.Character;
            var maker = pending.Maker;
            using var inventory = new InventoryMutation(ItemTaskType.GainItemWithUcc);
            if (pending.Handle == null)
            {
                if (!inventory.TryChangeMoney(character, -maker.ConsumeMoney))
                    return FailUpload(connection, ErrorMessageType.NotEnoughMoney);
            }
            else
            {
                var needed = maker.ConsumeCount;
                foreach (var item in character.Inventory.Bag.Items.Where(item => item.TemplateId == maker.ConsumeItemId)
                             .OrderBy(item => item.Slot).ToArray())
                {
                    var count = Math.Min(needed, item.Count - TradeReservation.GetReservedCount(item));
                    if (count <= 0)
                        continue;
                    if (!inventory.TryConsume(character.Inventory.Bag, item, count))
                        return FailUpload(connection, ErrorMessageType.NotEnoughItem);
                    needed -= count;
                    if (needed == 0)
                        break;
                }
                if (needed != 0)
                    return FailUpload(connection, ErrorMessageType.NotEnoughItem);
            }

            var created = ItemManager.Instance.Create(maker.ItemId, 1, 0, true);
            if (!inventory.TryAddCreated(created, character.Inventory.Bag))
                return FailUpload(connection, ErrorMessageType.BagFull);
            var id = uccIdManager.GetNextId();
            if (id == 0)
                return FailUpload(connection, ErrorMessageType.UccUploadFailed);
            var ucc = pending.Ucc;
            ucc.Id = id;
            ucc.Modified = DateTime.UtcNow;
            created.UccId = id;
            _uccs.TryAdd(id, ucc);
            try
            {
                if (!CommitPurchase(character, ucc))
                {
                    _uccs.TryRemove(id, out _);
                    uccIdManager.ReleaseId(id);
                    return FailUpload(connection, ErrorMessageType.UccUploadFailed);
                }
                inventory.Complete();
                connection.SendPacket(new TCEmblemStreamRecvStatusPacket(EmblemStreamStatus.End));
                return true;
            }
            catch
            {
                // A commit exception has an uncertain result. A notification exception follows a committed purchase.
                inventory.PreservePreparedState();
                throw;
            }
        }
    }

    public bool FailUpload(StreamConnection connection, ErrorMessageType error)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            _pendingUploads.Remove(connection);
            connection.GameConnection?.ActiveChar?.SendErrorMessage(error);
            connection.SendPacket(new TCEmblemStreamRecvStatusPacket(EmblemStreamStatus.Failed));
            return false;
        }
    }

    public void RemoveConnection(StreamConnection connection)
    {
        lock (SaveManager.PersistenceSyncRoot)
            _pendingUploads.Remove(connection);
        lock (s_lockObject)
            _downloadQueue?.Remove(connection.Id);
    }
}
