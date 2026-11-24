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

public sealed partial class FinancialIncomeExpenseSqlServerTests
{
    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuditFailure_RollsBackTheEntryOutcomeAndAuditAndAllowsSameKeyRetry(bool income)
    {
        await OpenAsync(100m);
        var before=await CountsAsync();
        string trigger="TR_VF_EntryFault_"+runId;
        int objectId;
        await using var connection=await ConnectAsync();
        await using(var create=connection.CreateCommand())
        {
            create.CommandText=$"""
                CREATE TRIGGER [dbo].[{trigger}] ON dbo.FinancialAuditEntries AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}'
                        AND Action IN ('financial.income.posted','financial.expense.posted'))
                        THROW 51080,'Synthetic entry audit failure.',1;
                END;
                """;
            await create.ExecuteNonQueryAsync();
            create.CommandText=$"SELECT OBJECT_ID(N'dbo.{trigger}');";
            objectId=Convert.ToInt32(await create.ExecuteScalarAsync());
        }
        try
        {
            Assert.Equal(51080,(await Assert.ThrowsAsync<SqlException>(()=>PostAsync(income,1m))).Number);
            Assert.Equal(before,await CountsAsync());
            Assert.Equal(100m,await IndependentBalanceAsync());
        }
        finally {await RemoveOwnedTriggerAsync(connection,trigger,objectId,ownerId.ToString("D"));}
        Assert.NotNull((await PostAsync(income,1m)).Receipt);
        Assert.Equal(income ? 101m : 99m,await IndependentBalanceAsync());
        Assert.Equal((2,2,1,2,2,2),await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanonicalFingerprint_IncludesCommandAndFullNormalizedPayload(bool income)
    {
        await OpenAsync(100m);
        const string reason="Synthetic \"entry\"\nα";
        var result=await PostAsync(income,1.2300m,reason:" "+reason+" ");
        Assert.NotNull(result.Receipt);
        var stored=await database.FinancialRequestOutcomes.AsNoTracking()
            .SingleAsync(x=>x.UserId==ownerId && x.CommandType==(income ? FinancialCommandType.Income : FinancialCommandType.Expense));
        string header=income ? "VF-INCOME" : "VF-EXPENSE";
        string canonical=$"{header}\n1\n{account.Id:D}\n1.23\nUSD\n{reason.Length}:{reason}";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))),stored.PayloadFingerprint);
        Assert.Equal(1,stored.FingerprintVersion);
        Assert.Equal(result,await PostAsync(income,1.23m,currency:" usd ",reason:reason));
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordingClockRegression_FailsClosedWithoutCreatingAnotherResult(bool income)
    {
        await OpenAsync(100m);
        // Administrative history probe only; the executable commands expose no client timestamp.
        var posting=new FinancialPostingContext(Guid.NewGuid(),DateTimeOffset.UtcNow.AddDays(1));
        var operation=FinancialOperation.CreateIncome(Guid.NewGuid(),ownerId,account,new(new Money(1m,"USD")),posting,"Synthetic future clock");
        database.Add(FinancialPostingBatchRecord.FromDomain(ownerId,FinancialCommandType.Income,posting));
        database.Add(FinancialOperationRecord.FromDomain(operation));
        var outcome=new FinancialRequestOutcomeRecord(Guid.NewGuid(),ownerId,FinancialCommandType.Income,
            "future-probe",new string('A',64),1,FinancialOutcomeKind.Posted,posting.BatchId,"synthetic.clock","{}",posting.RecordedAtUtc);
        database.Add(outcome);
        database.Add(new FinancialAuditEntryRecord(Guid.NewGuid(),ownerId,outcome.Id,posting.RecordedAtUtc,"synthetic.clock","{}"));
        await database.SaveChangesAsync();
        var before=await CountsAsync();
        Assert.Equal(FinancialFailure.JournalInconsistent,(await PostAsync(income,1m)).Failure);
        Assert.Equal(before,await CountsAsync());
    }

    [SqlServerFact]
    public async Task RuntimeRole_AllowsIncomeAndExpenseButDeniesDirectDmlAndInternalDispatcher()
    {
        await OpenAsync(100m);
        string principal="vf_entry_probe_"+runId;
        byte[] sid;
        await using var administrator=await ConnectAsync();
        await using(var create=administrator.CreateCommand())
        {
            create.CommandText=$"""
                CREATE USER [{principal}] WITHOUT LOGIN;
                ALTER ROLE VaultFinanceFinancialRuntime ADD MEMBER [{principal}];
                SELECT sid FROM sys.database_principals WHERE name=N'{principal}';
                """;
            sid=(byte[])(await create.ExecuteScalarAsync())!;
        }
        try
        {
            await using var runtime=await ConnectAsync();
            await using var command=runtime.CreateCommand();
            command.CommandText=$"EXECUTE AS USER=N'{principal}';";
            await command.ExecuteNonQueryAsync();
            try
            {
                foreach(string denied in new[]
                {
                    "SELECT COUNT(*) FROM dbo.FinancialOperations;",
                    "INSERT INTO dbo.FinancialOperations (Id,UserId,BatchId,Kind,Currency,Amount,DestinationAccountId) SELECT NEWID(),@owner,NEWID(),2,'USD',1,@account WHERE 1=0;",
                    "UPDATE dbo.FinancialOperations SET Amount=Amount WHERE 1=0;",
                    "DELETE FROM dbo.FinancialRequestOutcomes WHERE 1=0;",
                    "EXEC dbo.ExecuteFinancialWrite 3,@owner,@account,N'1.00',N'USD',NULL,N'key';"
                })
                {
                    command.Parameters.Clear(); command.CommandText=denied;
                    command.Parameters.AddWithValue("@owner",ownerId); command.Parameters.AddWithValue("@account",account.Id);
                    Assert.Equal(229,(await Assert.ThrowsAsync<SqlException>(()=>command.ExecuteNonQueryAsync())).Number);
                }
                foreach(bool income in new[]{true,false})
                {
                    command.Parameters.Clear();
                    command.CommandType=CommandType.StoredProcedure;
                    command.CommandText=income ? "dbo.RecordFinancialIncome" : "dbo.RecordFinancialExpense";
                    command.Parameters.AddWithValue("@UserId",ownerId);
                    command.Parameters.AddWithValue("@AccountId",account.Id);
                    command.Parameters.Add("@AmountText",SqlDbType.NVarChar,-1).Value="1.00";
                    command.Parameters.Add("@Currency",SqlDbType.NVarChar,-1).Value="USD";
                    command.Parameters.Add("@Reason",SqlDbType.NVarChar,-1).Value=DBNull.Value;
                    command.Parameters.Add("@IdempotencyKey",SqlDbType.NVarChar,-1).Value="runtime-entry";
                    await using var reader=await command.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(income ? "income.posted" : "expense.posted",reader.GetString(0));
                }
            }
            finally
            {
                command.CommandType=CommandType.Text;
                command.Parameters.Clear(); command.CommandText="REVERT;";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var drop=administrator.CreateCommand();
            drop.CommandText=$"""
                IF DB_NAME()<>N'VaultFinanceJournalTests'
                   OR NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{principal}' AND sid=@sid AND type='S')
                    THROW 51081,'Test principal ownership changed; cleanup refused.',1;
                ALTER ROLE VaultFinanceFinancialRuntime DROP MEMBER [{principal}];
                DROP USER [{principal}];
                """;
            drop.Parameters.Add("@sid",SqlDbType.VarBinary,85).Value=sid;
            await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal(100m,await IndependentBalanceAsync());
        Assert.Equal((3,3,1,3,3,3),await CountsAsync());
    }
}
