using AAEmu.Game.Core.Managers;

namespace AAEmu.Game.Models.Game.Housing;

public static class HouseTaxReceipt
{
    public static void Save(PersistenceSaveContext context, long mailId, House house, uint payerId,
        int quotedCopper, bool certificates, DateTime previousProtectionEnd, DateTime paidAt, uint lateFeePercent = 0)
    {
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = """
            INSERT INTO house_tax_receipts
            (mail_id, house_id, payer_id, quoted_copper, late_fee_percent, paid_in_certificates, protection_before, protection_after, paid_at)
            VALUES (@mail, @house, @payer, @amount, @fee, @certificates, @before, @after, @paid)
            """;
        command.Parameters.AddWithValue("@mail", mailId);
        command.Parameters.AddWithValue("@house", house.Id);
        command.Parameters.AddWithValue("@payer", payerId);
        command.Parameters.AddWithValue("@amount", quotedCopper);
        command.Parameters.AddWithValue("@certificates", certificates);
        command.Parameters.AddWithValue("@fee", lateFeePercent);
        command.Parameters.AddWithValue("@before", previousProtectionEnd);
        command.Parameters.AddWithValue("@after", house.ProtectionEndDate);
        command.Parameters.AddWithValue("@paid", paidAt);
        command.ExecuteNonQuery();
    }
}
