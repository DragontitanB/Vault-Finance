using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialTransferSqlServerTests
{
    [SqlServerFact]
    public async Task AuditFailure_RollsBackBothEffectsAndEveryRecordThenAllowsTheSameKey()
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        var before = await CountsAsync();
        string trigger = "TR_VF_TransferFault_" + runId;
        await using var connection = await ConnectAsync();
        int objectId;
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TRIGGER [dbo].[{trigger}] ON dbo.FinancialAuditEntries AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}' AND Action='financial.transfer.posted')
                        THROW 51080,'Synthetic transfer audit failure.',1;
                END;
                """;
            await create.ExecuteNonQueryAsync();
            create.CommandText = $"SELECT OBJECT_ID(N'dbo.{trigger}');";
            objectId = Convert.ToInt32(await create.ExecuteScalarAsync());
        }
        try
        {
            Assert.Equal(51080, (await Assert.ThrowsAsync<SqlException>(() => TransferAsync(10m))).Number);
            Assert.Equal(before, await CountsAsync());
            Assert.Equal(100m, await IndependentBalanceAsync(source.Id));
            Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
            Assert.Equal(0, await TransferOperationCountAsync());
        }
        finally { await RemoveOwnedTriggerAsync(connection, trigger, objectId, ownerId.ToString("D")); }
        Assert.NotNull((await TransferAsync(10m)).Receipt);
        Assert.Equal(before with { Batches = before.Batches + 1, Operations = before.Operations + 1,
            Outcomes = before.Outcomes + 1, Audits = before.Audits + 1, Effects = before.Effects + 2 }, await CountsAsync());
        Assert.Equal(90m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(10m, await IndependentBalanceAsync(destination.Id));
    }

    [SqlServerTheory]
    [InlineData(null)]
    [InlineData("Synthetic \"transfer\"\n\u03b1")]
    public async Task CanonicalFingerprint_IncludesBothOrderedParticipantsAndNormalizedFullPayload(string? reason)
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        var result = await TransferAsync(1.2300m, reason: reason is null ? " \t " : " " + reason + " ");
        Assert.NotNull(result.Receipt);
        var stored = await database.FinancialRequestOutcomes.AsNoTracking()
            .SingleAsync(x => x.UserId == ownerId && x.CommandType == FinancialCommandType.Transfer);
        string tail = reason is null ? "-1:" : $"{reason.Length}:{reason}";
        string canonical = $"VF-TRANSFER\n1\n{source.Id:D}\n{destination.Id:D}\n1.23\nUSD\n{tail}";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))), stored.PayloadFingerprint);
        Assert.Equal(1, stored.FingerprintVersion);
        Assert.Equal(result, await TransferAsync(1.23m, currency: " usd ", reason: reason));
        Assert.Equal(FinancialFailure.IdempotencyConflict, (await TransferAsync(1.23m, from: destination.Id, to: source.Id, reason: reason)).Failure);
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClockRegression_OnEitherAccountFailsClosedWithoutReservingTheKey(bool onSource)
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        var account = onSource ? source : destination;
        // Administrative integrity probe only; the application command exposes no client clock.
        var posting = new FinancialPostingContext(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1));
        var operation = FinancialOperation.CreateIncome(Guid.NewGuid(), ownerId, account, new(new Money(1m, "USD")), posting, "Synthetic future clock");
        database.Add(FinancialPostingBatchRecord.FromDomain(ownerId, FinancialCommandType.Income, posting));
        database.Add(FinancialOperationRecord.FromDomain(operation));
        var outcome = new FinancialRequestOutcomeRecord(Guid.NewGuid(), ownerId, FinancialCommandType.Income,
            "future-probe", new string('A', 64), 1, FinancialOutcomeKind.Posted, posting.BatchId, "synthetic.clock", "{}", posting.RecordedAtUtc);
        database.Add(outcome);
        database.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), ownerId, outcome.Id, posting.RecordedAtUtc, "synthetic.clock", "{}"));
        await database.SaveChangesAsync();
        var before = await CountsAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent, (await TransferAsync(1m)).Failure);
        Assert.Equal(before, await CountsAsync());
    }

    [SqlServerFact]
    public async Task RuntimeRole_CanExecuteTransferButCannotWriteHistoryOrCallPrivateDispatcher()
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        string principal = "vf_transfer_probe_" + runId;
        await using var administrator = await ConnectAsync();
        byte[] sid;
        await using (var create = administrator.CreateCommand())
        {
            create.CommandText = $"""
                CREATE USER [{principal}] WITHOUT LOGIN;
                ALTER ROLE VaultFinanceFinancialRuntime ADD MEMBER [{principal}];
                SELECT sid FROM sys.database_principals WHERE name=N'{principal}';
                """;
            sid = (byte[])(await create.ExecuteScalarAsync())!;
        }
        try
        {
            await using var runtime = await ConnectAsync();
            await using var command = runtime.CreateCommand();
            command.CommandText = $"EXECUTE AS USER=N'{principal}';";
            await command.ExecuteNonQueryAsync();
            try
            {
                foreach (string denied in new[]
                {
                    "SELECT COUNT(*) FROM dbo.FinancialOperations;",
                    "INSERT INTO dbo.FinancialOperations (Id,UserId,BatchId,Kind,Currency,Amount,SourceAccountId,DestinationAccountId) SELECT NEWID(),@owner,NEWID(),4,'USD',1,@source,@destination WHERE 1=0;",
                    "UPDATE dbo.FinancialOperations SET Amount=Amount WHERE 1=0;",
                    "DELETE FROM dbo.FinancialRequestOutcomes WHERE 1=0;",
                    "EXEC dbo.ExecuteFinancialWrite 5,@owner,@source,N'1.00',N'USD',NULL,N'key',@destination;",
                    "EXEC dbo.AcquireFinancialAccountLocks @owner,@source,@destination;",
                    "DECLARE @i bit,@b decimal(28,2); EXEC dbo.ReadLockedFinancialBalance @owner,@source,'USD',@i OUTPUT,@b OUTPUT;"
                })
                {
                    command.Parameters.Clear(); command.CommandText = denied;
                    command.Parameters.AddWithValue("@owner", ownerId);
                    command.Parameters.AddWithValue("@source", source.Id); command.Parameters.AddWithValue("@destination", destination.Id);
                    Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                }
                command.Parameters.Clear(); command.CommandType = CommandType.StoredProcedure;
                command.CommandText = "dbo.RecordFinancialTransfer";
                command.Parameters.AddWithValue("@UserId", ownerId);
                command.Parameters.AddWithValue("@SourceAccountId", source.Id); command.Parameters.AddWithValue("@DestinationAccountId", destination.Id);
                command.Parameters.Add("@AmountText", SqlDbType.NVarChar, -1).Value = "10.00";
                command.Parameters.Add("@Currency", SqlDbType.NVarChar, -1).Value = "USD";
                command.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, -1).Value = "runtime-transfer";
                await using (var reader = await command.ExecuteReaderAsync())
                { Assert.True(await reader.ReadAsync()); Assert.Equal("transfer.posted", reader.GetString(0)); }
                foreach (var id in new[] { source.Id, destination.Id })
                {
                    command.Parameters.Clear(); command.CommandText = "dbo.GetDerivedFinancialBalance";
                    command.Parameters.AddWithValue("@UserId", ownerId); command.Parameters.AddWithValue("@AccountId", id);
                    await using var reader = await command.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync()); Assert.Equal("balance.read", reader.GetString(0));
                    Assert.Equal(id == source.Id ? 90m : 10m, reader.GetDecimal(3));
                }
            }
            finally
            {
                command.CommandType = CommandType.Text; command.Parameters.Clear(); command.CommandText = "REVERT;";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var drop = administrator.CreateCommand();
            drop.CommandText = $"""
                IF DB_NAME()<>N'VaultFinanceJournalTests'
                    OR NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{principal}' AND sid=@sid AND type='S')
                    THROW 51081,'Test principal ownership changed; cleanup refused.',1;
                ALTER ROLE VaultFinanceFinancialRuntime DROP MEMBER [{principal}];
                DROP USER [{principal}];
                """;
            drop.Parameters.Add("@sid", SqlDbType.VarBinary, 85).Value = sid; await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal(90m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(10m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(1, await TransferOperationCountAsync());
    }
}
