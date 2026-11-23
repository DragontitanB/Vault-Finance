using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.FinancialAccounts;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialOpeningSqlServerTests
{
    [SqlServerTheory]
    [InlineData("incomplete")]
    [InlineData("negative")]
    [InlineData("financial-overflow")]
    [InlineData("clr-overflow")]
    public async Task Balance_FailsClosedOnAdministrativeCorruptionBeforeNarrowingTheAggregate(string scenario)
    {
        Assert.NotNull((await OpenAsync(0m)).Receipt);
        int count = scenario == "clr-overflow" ? 800 : scenario == "financial-overflow" ? 2 : 1;
        await using var connection = await ConnectAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Administrative corruption probe only. No executable Income/Expense use case is introduced.
        command.CommandText = """
            DECLARE @seed table (n int, batch uniqueidentifier, operation uniqueidentifier, outcome uniqueidentifier);
            INSERT INTO @seed
            SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY a.n,b.n,c.n), NEWID(), NEWID(), NEWID()
            FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(n)
            CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(n)
            CROSS JOIN (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(n);
            DECLARE @utc datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
            INSERT INTO dbo.FinancialPostingBatches
                (Id,UserId,CommandType,RecordedAtUtc,EffectiveDate,AccountingTimeZone)
            SELECT batch,@owner,CASE WHEN @negative=1 THEN 4 ELSE 3 END,@utc,
                CONVERT(date,@utc AT TIME ZONE 'SA Western Standard Time'),'America/Santo_Domingo' FROM @seed;
            INSERT INTO dbo.FinancialOperations
                (Id,UserId,BatchId,Kind,Currency,Amount,SourceAccountId,DestinationAccountId,OriginalOperationId,OriginalOperationKind,Reason)
            SELECT operation,@owner,batch,CASE WHEN @negative=1 THEN 3 ELSE 2 END,'USD',
                CASE WHEN @negative=1 THEN CONVERT(decimal(28,2),1) ELSE CONVERT(decimal(28,2),'99999999999999999999999999.99') END,
                CASE WHEN @negative=1 THEN @account ELSE NULL END,
                CASE WHEN @negative=0 THEN @account ELSE NULL END,NULL,NULL,N'Synthetic integrity probe' FROM @seed;
            IF @incomplete=0
            BEGIN
                INSERT INTO dbo.FinancialRequestOutcomes
                    (Id,UserId,CommandType,IdempotencyKey,PayloadFingerprint,FingerprintVersion,OutcomeKind,BatchId,ResultCode,ReceiptJson,RecordedAtUtc)
                SELECT outcome,@owner,CASE WHEN @negative=1 THEN 4 ELSE 3 END,CONVERT(varchar(36),outcome),
                    REPLICATE('A',64),1,1,batch,'synthetic.probe',N'{}',@utc FROM @seed;
                INSERT INTO dbo.FinancialAuditEntries (Id,UserId,OutcomeId,RecordedAtUtc,Action,SummaryJson)
                SELECT NEWID(),@owner,outcome,@utc,'synthetic.probe',N'{}' FROM @seed;
            END;
            """;
        command.Parameters.AddWithValue("@owner", ownerId);
        command.Parameters.AddWithValue("@account", account.Id);
        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@negative", scenario == "negative");
        command.Parameters.AddWithValue("@incomplete", scenario == "incomplete");
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        var before = await CountsAsync();

        Assert.Equal(FinancialFailure.JournalInconsistent, (await service.GetBalanceAsync(Owner(), account.Id)).Failure);
        Assert.Equal(FinancialFailure.JournalInconsistent, (await OpenAsync(1m, "investigate")).Failure);
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(1, await database.FinancialOpeningVersions.CountAsync(row => row.UserId == ownerId));
    }

    [SqlServerFact]
    public async Task Balance_RejectsAnOpeningVersionPointingAtAnotherOwnedAccount()
    {
        await using var connection = await ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @batch uniqueidentifier=NEWID(), @op uniqueidentifier=NEWID(), @result uniqueidentifier=NEWID();
            DECLARE @utc datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');
            BEGIN TRANSACTION;
            INSERT INTO dbo.FinancialPostingBatches (Id,UserId,CommandType,RecordedAtUtc,EffectiveDate,AccountingTimeZone)
            VALUES (@batch,@owner,1,@utc,CONVERT(date,@utc AT TIME ZONE 'SA Western Standard Time'),'America/Santo_Domingo');
            INSERT INTO dbo.FinancialOperations
                (Id,UserId,BatchId,Kind,Currency,Amount,SourceAccountId,DestinationAccountId,OriginalOperationId,OriginalOperationKind,Reason)
            VALUES (@op,@owner,@batch,1,'USD',1,NULL,@other,NULL,NULL,N'Synthetic wrong reference');
            INSERT INTO dbo.FinancialOpeningVersions
                (UserId,Currency,AccountId,Revision,PreviousRevision,BatchId,OpeningOperationId,OpeningOperationKind,Reason)
            VALUES (@owner,'USD',@account,1,NULL,@batch,@op,1,N'Synthetic wrong reference');
            INSERT INTO dbo.FinancialRequestOutcomes
                (Id,UserId,CommandType,IdempotencyKey,PayloadFingerprint,FingerprintVersion,OutcomeKind,BatchId,ResultCode,ReceiptJson,RecordedAtUtc)
            VALUES (@result,@owner,1,CONVERT(varchar(36),@result),REPLICATE('A',64),1,1,@batch,'synthetic.probe',N'{}',@utc);
            INSERT INTO dbo.FinancialAuditEntries (Id,UserId,OutcomeId,RecordedAtUtc,Action,SummaryJson)
            VALUES (NEWID(),@owner,@result,@utc,'synthetic.probe',N'{}');
            COMMIT;
            """;
        command.Parameters.AddWithValue("@owner", ownerId);
        command.Parameters.AddWithValue("@account", account.Id);
        command.Parameters.AddWithValue("@other", secondAccount.Id);
        await command.ExecuteNonQueryAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent, (await service.GetBalanceAsync(Owner(), account.Id)).Failure);
        Assert.Equal(FinancialFailure.JournalInconsistent, (await service.GetBalanceAsync(Owner(), secondAccount.Id)).Failure);
    }
}
