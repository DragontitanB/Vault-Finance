using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialReversalSqlServerTests
{
    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public async Task AuditFailure_RollsBackTheWholeReversalAndAllowsTheSameKey(FinancialOperationKind kind)
    {
        var original = await PrepareAsync(kind); var before = await CountsAsync();
        string originalBefore = await OriginalSnapshotAsync(original);
        decimal a = await IndependentBalanceAsync(source.Id), b = await IndependentBalanceAsync(destination.Id);
        string trigger = "TR_VF_ReversalFault_" + runId;
        await using var connection = await ConnectAsync(); int objectId;
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TRIGGER [dbo].[{trigger}] ON dbo.FinancialAuditEntries AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}' AND Action='financial.reversal.posted')
                        THROW 51080,'Synthetic reversal audit failure.',1;
                END;
                """;
            await create.ExecuteNonQueryAsync(); create.CommandText = $"SELECT OBJECT_ID(N'dbo.{trigger}');";
            objectId = Convert.ToInt32(await create.ExecuteScalarAsync());
        }
        try
        {
            Assert.Equal(51080, (await Assert.ThrowsAsync<SqlException>(() => ReverseAsync(original))).Number);
            Assert.Equal(before, await CountsAsync()); Assert.Equal(0, await ReversalCountAsync(original));
            Assert.Equal(a, await IndependentBalanceAsync(source.Id)); Assert.Equal(b, await IndependentBalanceAsync(destination.Id));
            Assert.Equal(originalBefore, await OriginalSnapshotAsync(original));
        }
        finally { await RemoveOwnedTriggerAsync(connection, trigger, objectId, ownerId.ToString("D")); }
        Assert.NotNull((await ReverseAsync(original)).Receipt); Assert.Equal(1, await ReversalCountAsync(original));
        Assert.Equal(100m, await IndependentBalanceAsync(source.Id)); Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
    }

    [SqlServerTheory]
    [InlineData("Synthetic reason")]
    [InlineData("Synthetic \"correction\"\n\u03b1\U0001F4B0")]
    public async Task Fingerprint_ContainsOnlyTheOriginalAndRequiredNormalizedReason(string reason)
    {
        var original = await PrepareAsync(FinancialOperationKind.Transfer);
        var result = await ReverseAsync(original, reason: "  " + reason + "  "); Assert.NotNull(result.Receipt);
        var stored = await database.FinancialRequestOutcomes.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.CommandType == FinancialCommandType.Reverse);
        string canonical = $"VF-REVERSE\n1\n{original:D}\n{reason.Length}:{reason}";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))), stored.PayloadFingerprint);
        Assert.Equal(1, stored.FingerprintVersion);
        Assert.Equal(result, await ReverseAsync(original, reason: reason));
    }

    [SqlServerTheory]
    [InlineData("empty-id")]
    [InlineData("missing-reason")]
    [InlineData("whitespace-reason")]
    [InlineData("untrimmed-reason")]
    [InlineData("long-reason")]
    [InlineData("bad-key")]
    [InlineData("long-key")]
    public async Task SqlBoundary_RejectsMalformedIntentBeforeReservingTheKey(string invalid)
    {
        var intent = new ValidatedFinancialReversal(invalid == "empty-id" ? Guid.Empty : Guid.NewGuid(),
            invalid == "missing-reason" ? null! : invalid == "whitespace-reason" ? " \t " : invalid == "untrimmed-reason" ? " reason " : invalid == "long-reason" ? new string('r', 201) : "Synthetic",
            invalid == "bad-key" ? "bad key" : invalid == "long-key" ? new string('k', 129) : "key");
        Assert.Equal(FinancialFailure.InvalidInput, (await new SqlFinancialJournal(serviceContext).RecordReversalAsync(Owner(), intent, CancellationToken.None)).Failure);
        Assert.Equal(new Counts(), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClockRegression_OnEitherTransferParticipantFailsClosed(bool onSource)
    {
        var original = await PrepareAsync(FinancialOperationKind.Transfer);
        var account = onSource ? source : destination;
        var posting = new FinancialPostingContext(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1));
        var operation = FinancialOperation.CreateIncome(Guid.NewGuid(), ownerId, account, new(new Money(1m, "USD")), posting);
        database.Add(FinancialPostingBatchRecord.FromDomain(ownerId, FinancialCommandType.Income, posting));
        database.Add(FinancialOperationRecord.FromDomain(operation));
        var outcome = new FinancialRequestOutcomeRecord(Guid.NewGuid(), ownerId, FinancialCommandType.Income, "future-probe", new string('A', 64), 1,
            FinancialOutcomeKind.Posted, posting.BatchId, "synthetic.clock", "{}", posting.RecordedAtUtc);
        database.Add(outcome); database.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), ownerId, outcome.Id, posting.RecordedAtUtc, "synthetic.clock", "{}"));
        await database.SaveChangesAsync(); var before = await CountsAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent, (await ReverseAsync(original)).Failure);
        Assert.Equal(before, await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_MustHaveCompleteCompatibleConfirmationHistory(bool wrongCommand)
    {
        await OpenAsync(source, 100m);
        var posting = new FinancialPostingContext(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var operation = FinancialOperation.CreateIncome(Guid.NewGuid(), ownerId, source, new(new Money(20m, "USD")), posting);
        var command = wrongCommand ? FinancialCommandType.Expense : FinancialCommandType.Income;
        database.Add(FinancialPostingBatchRecord.FromDomain(ownerId, command, posting));
        database.Add(FinancialOperationRecord.FromDomain(operation));
        if (wrongCommand)
        {
            var outcome = new FinancialRequestOutcomeRecord(Guid.NewGuid(), ownerId, command, "malformed-original", new string('A', 64), 1,
                FinancialOutcomeKind.Posted, posting.BatchId, "synthetic.original", "{}", posting.RecordedAtUtc);
            database.Add(outcome); database.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), ownerId, outcome.Id, posting.RecordedAtUtc, "synthetic.original", "{}"));
        }
        await database.SaveChangesAsync(); var before = await CountsAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent, (await ReverseAsync(operation.Id)).Failure);
        Assert.Equal(before, await CountsAsync());
    }

    [SqlServerFact]
    public async Task IdempotencyScope_SeparatesReverseFromOrdinaryCommandsAndOtherOwners()
    {
        var original = await PrepareAsync(FinancialOperationKind.Income);
        Assert.NotNull((await ReverseAsync(original, "original")).Receipt);
        var other = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), other, "Synthetic other owner", "USD");
        database.Users.Add(User(other)); database.Add(account); await database.SaveChangesAsync();
        Assert.NotNull((await service.InitializeOpeningAsync(new(other), new(account.Id, 0m, "USD", "Synthetic"), "open")).Receipt);
        var otherOriginal = (await service.RecordIncomeAsync(new(other), new(account.Id, 20m, "USD"), "original")).Receipt!.OperationId;
        Assert.NotNull((await service.RecordReversalAsync(new(other), new(otherOriginal, "Synthetic reversal"), "original")).Receipt);
        Assert.Equal(FinancialFailure.IdempotencyConflict, (await service.RecordReversalAsync(new(other), new(original, "Synthetic reversal"), "original")).Failure);
        Assert.Equal(FinancialFailure.NotFound, (await service.RecordReversalAsync(new(other), new(original, "Synthetic reversal"), "foreign")).Failure);
    }

    [SqlServerFact]
    public async Task RuntimeRole_ExecutesOnlyTheFixedReversalBoundaryAndCannotWriteOrSupplyEffects()
    {
        var original = await PrepareAsync(FinancialOperationKind.Transfer);
        string principal = "vf_reversal_probe_" + runId;
        await using var administrator = await ConnectAsync(); byte[] sid;
        await using (var create = administrator.CreateCommand())
        {
            create.CommandText = $"CREATE USER [{principal}] WITHOUT LOGIN; ALTER ROLE VaultFinanceFinancialRuntime ADD MEMBER [{principal}]; SELECT sid FROM sys.database_principals WHERE name=N'{principal}';";
            sid = (byte[])(await create.ExecuteScalarAsync())!;
        }
        try
        {
            await using var runtime = await ConnectAsync(); await using var command = runtime.CreateCommand();
            command.CommandText = $"EXECUTE AS USER=N'{principal}';"; await command.ExecuteNonQueryAsync();
            try
            {
                foreach (string table in new[] { "FinancialOperations", "FinancialPostingBatches", "FinancialOpeningVersions", "FinancialRequestOutcomes", "FinancialAuditEntries" })
                {
                    foreach (string denied in new[] { $"SELECT COUNT(*) FROM dbo.{table};", $"DELETE FROM dbo.{table} WHERE 1=0;" })
                    {
                        command.CommandText = denied;
                        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                    }
                }
                foreach (string denied in new[]
                {
                    "UPDATE dbo.FinancialOperations SET Reason=Reason WHERE 1=0;",
                    "INSERT INTO dbo.FinancialOperations (Id,UserId,BatchId,Kind,Currency,OriginalOperationId,OriginalOperationKind,Reason) SELECT NEWID(),@owner,NEWID(),5,'USD',@original,4,N'forbidden' WHERE 1=0;",
                    "EXEC dbo.ExecuteFinancialWrite @CommandType=6,@UserId=@owner,@AccountId=NULL,@AmountText=NULL,@Currency=NULL,@Reason=N'Synthetic',@IdempotencyKey=N'private',@OriginalOperationId=@original;"
                })
                {
                    command.CommandText = denied; command.Parameters.Clear();
                    command.Parameters.AddWithValue("@owner", ownerId); command.Parameters.AddWithValue("@original", original);
                    Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                }
                command.CommandText = "EXEC dbo.RecordFinancialReversal @owner,@original,N'Synthetic',N'forged',@AmountText=N'1.00';";
                Assert.Equal(8144, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                command.CommandText = "EXEC dbo.RecordFinancialReversal @owner,@original,N'Synthetic reversal',N'runtime-reversal';";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync()); Assert.Equal("reversal.posted", reader.GetString(0));
            }
            finally { command.CommandType = CommandType.Text; command.Parameters.Clear(); command.CommandText = "REVERT;"; await command.ExecuteNonQueryAsync(); }
        }
        finally
        {
            await using var drop = administrator.CreateCommand();
            drop.CommandText = $"""
                IF DB_NAME()<>N'VaultFinanceJournalTests'
                    OR NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{principal}' AND sid=@sid AND type='S')
                    THROW 51081,'Test principal ownership changed; cleanup refused.',1;
                ALTER ROLE VaultFinanceFinancialRuntime DROP MEMBER [{principal}]; DROP USER [{principal}];
                """;
            drop.Parameters.Add("@sid", SqlDbType.VarBinary, 85).Value = sid; await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await ReversalCountAsync(original));
        Assert.Equal(100m, await IndependentBalanceAsync(source.Id)); Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
    }
}
