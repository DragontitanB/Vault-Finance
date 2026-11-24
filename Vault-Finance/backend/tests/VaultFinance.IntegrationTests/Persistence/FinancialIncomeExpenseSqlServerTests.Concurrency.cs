using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Application.Accounts;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialIncomeExpenseSqlServerTests
{
    [SqlServerTheory]
    [InlineData("overspend")]
    [InlineData("sufficient")]
    [InlineData("income-expense")]
    [InlineData("same-income")]
    [InlineData("same-expense")]
    [InlineData("changed-payload")]
    public async Task ConcurrentEntries_ContendOnDistinctSqlSessionsAndMatchASerialResult(string scenario)
    {
        decimal initial=scenario=="sufficient" ? 20000m : scenario=="income-expense" ? 0m : 10000m;
        await OpenAsync(initial);
        await using var blocker=await ConnectAsync();
        await using var transaction=(SqlTransaction)await blocker.BeginTransactionAsync();
        await using(var command=blocker.CreateCommand())
        {
            command.Transaction=transaction;
            command.CommandText="EXEC dbo.AcquireFinancialAccountLocks @owner,@account;";
            command.Parameters.AddWithValue("@owner",ownerId); command.Parameters.AddWithValue("@account",account.Id);
            await command.ExecuteNonQueryAsync();
        }
        bool firstIncome=scenario is "income-expense" or "same-income" or "changed-payload";
        bool secondIncome=scenario is "same-income" or "changed-payload";
        bool sameKey=scenario is "same-income" or "same-expense" or "changed-payload";
        var first=PostAsync(firstIncome,7000m,"race");
        var second=PostAsync(secondIncome,scenario=="changed-payload" ? 6000m : 7000m,sameKey ? "race" : "race-2");
        var both=Task.WhenAll(first,second);
        try
        {
            var sessions=await WaitForBlockedSessionsAsync(2);
            Assert.Equal(2,sessions.Distinct().Count());
            Assert.False(both.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync();
            await both;
        }

        var results=await both;
        decimal expected;
        int posted,rejected;
        if(scenario=="overspend")
        {
            Assert.Single(results,x=>x.Receipt is not null);
            Assert.Equal(FinancialFailure.InsufficientFunds,Assert.Single(results,x=>x.Receipt is null).Failure);
            expected=3000m; posted=1; rejected=1;
        }
        else if(scenario=="sufficient")
        {
            Assert.All(results,x=>Assert.NotNull(x.Receipt));
            expected=6000m; posted=2; rejected=0;
        }
        else if(scenario=="income-expense")
        {
            Assert.NotNull(results[0].Receipt);
            if(results[1].Receipt is not null) {expected=0m; posted=2; rejected=0;}
            else {Assert.Equal(FinancialFailure.InsufficientFunds,results[1].Failure); expected=7000m; posted=1; rejected=1;}
        }
        else if(scenario=="changed-payload")
        {
            var receipt=Assert.Single(results,x=>x.Receipt is not null).Receipt!;
            Assert.Equal(FinancialFailure.IdempotencyConflict,Assert.Single(results,x=>x.Receipt is null).Failure);
            expected=initial+receipt.Amount; posted=1; rejected=0;
        }
        else
        {
            Assert.NotNull(results[0].Receipt);
            Assert.Equal(results[0],results[1]);
            expected=initial+(firstIncome ? 7000m : -7000m); posted=1; rejected=0;
        }
        int openingOperations=initial>0 ? 1 : 0;
        Assert.Equal((1+posted,openingOperations+posted,1,1+posted+rejected,1+posted+rejected,openingOperations+posted),await CountsAsync());
        Assert.Equal(expected,await IndependentBalanceAsync());
        Assert.Equal(expected,(await service.GetBalanceAsync(Owner(),account.Id)).Balance!.Amount);
    }

    [SqlServerTheory]
    [InlineData(true,false)]
    [InlineData(false,false)]
    [InlineData(true,true)]
    [InlineData(false,true)]
    public async Task ArchiveRace_UsesTheExistingEfPathAndHasAControlledSerializableOrder(bool income,bool operationFirst)
    {
        await OpenAsync(10000m);
        await using var archiveScope=fixture.Provider.CreateAsyncScope();
        var archiveContext=archiveScope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        archiveContext.Database.SetConnectionString(connectionString);
        var archiveService=new AccountService(archiveScope.ServiceProvider.GetRequiredService<IAccountStore>());
        FinancialEntryOutcome result;

        if(!operationFirst)
        {
            await using var archiveTransaction=await archiveContext.Database.BeginTransactionAsync();
            Assert.Null((await archiveService.ArchiveAsync(ownerId,account.Id,CancellationToken.None)).Failure);
            var waiting=PostAsync(income,1000m);
            try {await WaitForBlockedSessionsAsync(1); Assert.False(waiting.IsCompleted);}
            finally {await archiveTransaction.CommitAsync();}
            result=await waiting;
            Assert.Equal(FinancialFailure.Archived,result.Failure);
        }
        else
        {
            string trigger="TR_VF_EntryGate_"+runId;
            string resource="VaultFinance.TestEntryGate."+runId;
            await using var gate=await ConnectAsync();
            await using(var hold=gate.CreateCommand())
            {
                hold.CommandText="DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=1000; SELECT @r;";
                hold.Parameters.AddWithValue("@resource",resource);
                Assert.True(Convert.ToInt32(await hold.ExecuteScalarAsync())>=0);
            }
            int objectId;
            await using(var create=gate.CreateCommand())
            {
                create.CommandText=$"""
                    CREATE TRIGGER [dbo].[{trigger}] ON dbo.FinancialAuditEntries AFTER INSERT AS
                    BEGIN
                        SET NOCOUNT ON;
                        IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}' AND Action IN ('financial.income.posted','financial.expense.posted'))
                        BEGIN
                            DECLARE @r int;
                            EXEC @r=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=5000;
                            IF @r<0 THROW 51080,'Synthetic entry gate timed out.',1;
                        END;
                    END;
                    """;
                await create.ExecuteNonQueryAsync();
                create.CommandText=$"SELECT OBJECT_ID(N'dbo.{trigger}');";
                objectId=Convert.ToInt32(await create.ExecuteScalarAsync());
            }
            Task<FinancialEntryOutcome>? entry=null;
            Task<AccountOutcome>? archive=null;
            try
            {
                entry=PostAsync(income,1000m);
                await WaitForBlockedSessionsAsync(1);
                archive=archiveService.ArchiveAsync(ownerId,account.Id,CancellationToken.None);
                var sessions=await WaitForBlockedSessionsAsync(2);
                Assert.Equal(2,sessions.Distinct().Count());
            }
            finally
            {
                await using var release=gate.CreateCommand();
                release.CommandText="EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';";
                release.Parameters.AddWithValue("@resource",resource);
                await release.ExecuteNonQueryAsync();
                try
                {
                    if(entry is not null) await entry;
                    if(archive is not null) await archive;
                }
                finally {await RemoveOwnedTriggerAsync(gate,trigger,objectId,resource);}
            }
            result=await entry!;
            Assert.NotNull(result.Receipt);
            Assert.Null((await archive!).Failure);
        }
        Assert.Equal("Archived",(await archiveService.GetAsync(ownerId,account.Id,CancellationToken.None)).Account!.Status);
        decimal expected=result.Receipt is null ? 10000m : income ? 11000m : 9000m;
        Assert.Equal(expected,await IndependentBalanceAsync());
        Assert.Equal(expected,(await service.GetBalanceAsync(Owner(),account.Id)).Balance!.Amount);
        Assert.Equal(operationFirst ? (2,2,1,2,2,2) : (1,1,1,2,2,1),await CountsAsync());
    }

    private async Task<int[]> WaitForBlockedSessionsAsync(int count)
    {
        await using var observer=await ConnectAsync();
        await using var command=observer.CreateCommand();
        command.CommandText="""
            SELECT CONVERT(int,r.session_id)
            FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
            WHERE s.program_name=@application AND r.wait_type LIKE N'LCK_M_%';
            """;
        command.Parameters.AddWithValue("@application",applicationName);
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(3))
        {
            var sessions=new List<int>();
            await using(var reader=await command.ExecuteReaderAsync())
                while(await reader.ReadAsync()) sessions.Add(reader.GetInt32(0));
            if(sessions.Count>=count) return sessions.ToArray();
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Expected independent SQL sessions did not reach real lock contention.");
    }

    private static async Task RemoveOwnedTriggerAsync(SqlConnection connection,string name,int id,string marker)
    {
        await using var command=connection.CreateCommand();
        command.CommandText=$"""
            IF DB_NAME()<>N'VaultFinanceJournalTests' OR OBJECT_ID(N'dbo.{name}')<>@id
               OR NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=@id AND CHARINDEX(@marker,definition)>0)
                THROW 51081,'Test trigger ownership changed; cleanup refused.',1;
            DROP TRIGGER [dbo].[{name}];
            """;
        command.Parameters.AddWithValue("@id",id); command.Parameters.AddWithValue("@marker",marker);
        await command.ExecuteNonQueryAsync();
    }
}
