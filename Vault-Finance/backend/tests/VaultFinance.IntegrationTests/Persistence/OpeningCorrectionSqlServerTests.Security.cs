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

public sealed partial class OpeningCorrectionSqlServerTests
{
    [SqlServerTheory]
    [InlineData("compensation")]
    [InlineData("replacement")]
    [InlineData("audit")]
    public async Task ForcedFault_RollsBackAllComponentsVersionResultAndAuditThenAllowsTheSameKey(string stage)
    {
        await OpenAsync(source, 100m); await EntryAsync(false, 80m, "expense");
        var before = await CountsAsync(); var history = await CaptureHistoryAsync();
        string trigger = "TR_VF_CorrectionFault_" + runId;
        await using var connection = await ConnectAsync(); int objectId;
        await using (var create = connection.CreateCommand())
        {
            string table = stage == "audit" ? "FinancialAuditEntries" : "FinancialOperations";
            string filter = stage == "audit" ? "Action='financial.opening_correction.posted'"
                : stage == "compensation" ? "Kind=5 AND OriginalOperationKind=1" : "Kind=1";
            create.CommandText = $"""
                CREATE TRIGGER [dbo].[{trigger}] ON dbo.[{table}] AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}' AND {filter})
                        THROW 51080,'Synthetic correction component failure.',1;
                END;
                """;
            await create.ExecuteNonQueryAsync(); create.CommandText = $"SELECT OBJECT_ID(N'dbo.{trigger}');";
            objectId = Convert.ToInt32(await create.ExecuteScalarAsync());
        }
        try
        {
            Assert.Equal(51080, (await Assert.ThrowsAsync<SqlException>(() => CorrectAsync(80m))).Number);
            Assert.Equal(before, await CountsAsync()); await AssertPreservedAsync(history);
            Assert.Equal(20m, await IndependentBalanceAsync(source.Id));
        }
        finally { await RemoveOwnedTriggerAsync(connection, trigger, objectId, ownerId.ToString("D")); }
        var receipt = (await CorrectAsync(80m)).Receipt!; Assert.NotNull(receipt); await AssertCorrectionBatchAsync(receipt);
        Assert.Equal(0m, await IndependentBalanceAsync(source.Id));
    }

    [SqlServerTheory]
    [InlineData("Synthetic correction")]
    [InlineData("Synthetic \"correction\"\n\u03b1\U0001F4B0")]
    public async Task Fingerprint_IncludesAccountAmountCurrencyExpectedRevisionAndFullNormalizedReason(string reason)
    {
        await OpenAsync(source, 100m);
        var result = await CorrectAsync(80m, reason: "  " + reason + "  "); Assert.NotNull(result.Receipt);
        var stored = await database.FinancialRequestOutcomes.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.CommandType == FinancialCommandType.CorrectOpening);
        string canonical = $"VF-CORRECT-OPEN\n1\n{source.Id:D}\n1\n80.00\nUSD\n{reason.Length}:{reason}";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))), stored.PayloadFingerprint);
        Assert.Equal(1, stored.FingerprintVersion); Assert.Equal(result, await CorrectAsync(80.00m, currency: " usd ", reason: reason));
    }

    [SqlServerTheory]
    [InlineData("negative")]
    [InlineData("precision")]
    [InlineData("range")]
    [InlineData("currency")]
    [InlineData("revision")]
    [InlineData("empty-id")]
    [InlineData("reason")]
    [InlineData("untrimmed")]
    [InlineData("key")]
    public async Task SqlBoundary_RevalidatesTheIntentBeforeReservingTheKey(string invalid)
    {
        var intent = new ValidatedOpeningCorrection(invalid == "empty-id" ? Guid.Empty : source.Id,
            invalid == "negative" ? -1m : invalid == "precision" ? 1.001m : invalid == "range" ? decimal.MaxValue : 1m,
            invalid == "currency" ? "EUR" : "USD", invalid == "revision" ? 0 : 1,
            invalid == "reason" ? null! : invalid == "untrimmed" ? " reason " : "Synthetic", invalid == "key" ? "bad key" : "key");
        Assert.Equal(FinancialFailure.InvalidInput, (await new SqlFinancialJournal(serviceContext).CorrectOpeningAsync(Owner(), intent, CancellationToken.None)).Failure);
        Assert.Equal(new Counts(), await CountsAsync());
    }

    [SqlServerFact]
    public async Task IncompleteOrTamperedCurrentOpening_FailsClosedWithoutCompensatingItAgain()
    {
        var root = await OpenAsync(source, 100m); await EntryAsync(true, 100m, "funds");
        // Administrative corruption probe only: runtime ordinary reversal rejects Opening.
        var posting = new FinancialPostingContext(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var original = FinancialOperation.CreateOpening(root.OperationId!.Value, ownerId, source, new(new Money(100m, "USD")),
            new(root.BatchId, root.RecordedAtUtc), "Synthetic opening");
        var reversal = FinancialOperation.CreateReversal(Guid.NewGuid(), ownerId, original, posting, "Synthetic corrupt cancellation");
        database.Add(FinancialPostingBatchRecord.FromDomain(ownerId, FinancialCommandType.Reverse, posting)); database.Add(FinancialOperationRecord.FromDomain(reversal));
        var outcome = new FinancialRequestOutcomeRecord(Guid.NewGuid(), ownerId, FinancialCommandType.Reverse, "corrupt", new string('A', 64), 1,
            FinancialOutcomeKind.Posted, posting.BatchId, "synthetic.corrupt", "{}", posting.RecordedAtUtc);
        database.Add(outcome); database.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), ownerId, outcome.Id, posting.RecordedAtUtc, "synthetic.corrupt", "{}"));
        await database.SaveChangesAsync(); var before = await CountsAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent, (await CorrectAsync(80m)).Failure); Assert.Equal(before, await CountsAsync());
    }

    [SqlServerFact]
    public async Task MalformedHistoricalZeroVersion_IsNotSilentlyAcceptedAsTheCurrentChain()
    {
        await OpenAsync(source, 0m);
        var posting = new FinancialPostingContext(Guid.NewGuid(), DateTimeOffset.UtcNow);
        database.Add(FinancialPostingBatchRecord.FromDomain(ownerId, FinancialCommandType.CorrectOpening, posting));
        database.Add(new FinancialOpeningVersionRecord(ownerId, "USD", source.Id, 2, 1, posting.BatchId, null, "Synthetic forbidden no-change"));
        var outcome = new FinancialRequestOutcomeRecord(Guid.NewGuid(), ownerId, FinancialCommandType.CorrectOpening, "corrupt", new string('A', 64), 1,
            FinancialOutcomeKind.Posted, posting.BatchId, "synthetic.corrupt", "{}", posting.RecordedAtUtc);
        database.Add(outcome); database.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), ownerId, outcome.Id, posting.RecordedAtUtc, "synthetic.corrupt", "{}"));
        await database.SaveChangesAsync(); var before = await CountsAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent, (await CorrectAsync(20m, revision: 2)).Failure); Assert.Equal(before, await CountsAsync());
    }

    [SqlServerFact]
    public async Task ClockRegression_RejectsCorrectionWithoutChangingTheChain()
    {
        await OpenAsync(source, 100m);
        var posting = new FinancialPostingContext(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1));
        database.Add(FinancialPostingBatchRecord.FromDomain(ownerId, FinancialCommandType.Income, posting));
        database.Add(FinancialOperationRecord.FromDomain(FinancialOperation.CreateIncome(Guid.NewGuid(), ownerId, source, new(new Money(1m, "USD")), posting)));
        var outcome = new FinancialRequestOutcomeRecord(Guid.NewGuid(), ownerId, FinancialCommandType.Income, "future", new string('A', 64), 1,
            FinancialOutcomeKind.Posted, posting.BatchId, "synthetic.clock", "{}", posting.RecordedAtUtc);
        database.Add(outcome); database.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), ownerId, outcome.Id, posting.RecordedAtUtc, "synthetic.clock", "{}"));
        await database.SaveChangesAsync(); var before = await CountsAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent, (await CorrectAsync(80m)).Failure); Assert.Equal(before, await CountsAsync());
    }

    [SqlServerFact]
    public async Task CommandAndOwnerScopes_AreIndependentAndInitializationCannotRestartAfterCorrection()
    {
        Assert.NotNull((await service.InitializeOpeningAsync(Owner(), new(source.Id, 100m, "USD", "Synthetic"), "shared")).Receipt);
        Assert.NotNull((await CorrectAsync(80m, "shared")).Receipt);
        Assert.Equal(FinancialFailure.AlreadyInitialized, (await service.InitializeOpeningAsync(Owner(), new(source.Id, 100m, "USD", "Synthetic"), "restart")).Failure);
        var other = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), other, "Synthetic other correction", "USD");
        database.Users.Add(User(other)); database.Add(account); await database.SaveChangesAsync();
        Assert.NotNull((await service.InitializeOpeningAsync(new(other), new(account.Id, 100m, "USD", "Synthetic"), "shared")).Receipt);
        Assert.NotNull((await service.CorrectOpeningAsync(new(other), new(account.Id, 80m, "USD", 1, "Synthetic correction"), "shared")).Receipt);
    }

    [SqlServerFact]
    public async Task RuntimeRole_CanCorrectButCannotWriteHistoryCallPrivateHelpersOrChooseHistoricalEffects()
    {
        await OpenAsync(source, 100m); string principal = "vf_correction_probe_" + runId;
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
                foreach (string table in new[] { "FinancialPostingBatches", "FinancialOperations", "FinancialOpeningVersions", "FinancialRequestOutcomes", "FinancialAuditEntries" })
                    foreach (string denied in new[] { $"SELECT COUNT(*) FROM dbo.{table};", $"DELETE FROM dbo.{table} WHERE 1=0;" })
                    { command.CommandText = denied; Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number); }
                foreach (string denied in new[]
                {
                    "UPDATE dbo.FinancialOpeningVersions SET Reason=Reason WHERE 1=0;",
                    "INSERT INTO dbo.FinancialOpeningVersions (UserId,Currency,AccountId,Revision,PreviousRevision,BatchId,Reason) SELECT @owner,'USD',@account,2,1,NEWID(),N'forbidden' WHERE 1=0;",
                    "EXEC dbo.ExecuteFinancialWrite 2,@owner,@account,N'80.00',N'USD',N'Synthetic',N'private',@ExpectedRevision=1;",
                    "DECLARE @r int,@id uniqueidentifier,@a decimal(28,2); EXEC dbo.ReadLockedFinancialOpening @owner,@account,'USD',@r OUTPUT,@id OUTPUT,@a OUTPUT;"
                })
                {
                    command.Parameters.Clear(); command.CommandText = denied;
                    command.Parameters.AddWithValue("@owner", ownerId); command.Parameters.AddWithValue("@account", source.Id);
                    Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                }
                command.CommandText = "EXEC dbo.CorrectFinancialOpening @owner,@account,N'80.00',N'USD',1,N'Synthetic',N'forged',@PreviousOpeningAmount=N'0.00';";
                Assert.Equal(8144, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                command.CommandText = "EXEC dbo.CorrectFinancialOpening @owner,@account,N'80.00',N'USD',1,N'Synthetic correction',N'runtime';";
                await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal("opening.corrected", reader.GetString(0));
            }
            finally { command.Parameters.Clear(); command.CommandText = "REVERT;"; await command.ExecuteNonQueryAsync(); }
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
        Assert.Equal(80m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(2, await database.FinancialOpeningVersions.CountAsync(x => x.UserId == ownerId && x.AccountId == source.Id));
    }
}
