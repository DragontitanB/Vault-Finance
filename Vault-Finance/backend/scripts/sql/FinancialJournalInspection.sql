BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124181610_FinancialJournalInspection'
)
BEGIN
    EXEC(N'-- Frozen read-only journal inspection. No writer or global isolation setting is changed.
    CREATE PROCEDURE [dbo].[CaptureFinancialJournalInspection]
        @UserId uniqueidentifier,@MaximumRows int=20000
    AS
    BEGIN
        SET NOCOUNT ON;
        -- Read validators can reject corrupt history without dooming this read-only capture.
        -- Unexpected SQL errors still roll back and invalidate the entire capture.
        SET XACT_ABORT OFF;
        SET LOCK_TIMEOUT 5000;
        IF @@TRANCOUNT<>0 OR @UserId IS NULL OR @UserId=''00000000-0000-0000-0000-000000000000''
           OR @MaximumRows IS NULL OR @MaximumRows NOT BETWEEN 1 AND 20000
            THROW 51010, ''Invalid diagnostic capture context.'', 1;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @discard bigint;
            -- Acquire actual retained OBJECT S locks before exporting ANY payload.
            -- Early counts only acquire locks; they are not treated as one coherent snapshot.
            SELECT @discard=COUNT_BIG(*) FROM dbo.Accounts WITH (TABLOCK,HOLDLOCK) WHERE UserId=@UserId;
            SELECT @discard=COUNT_BIG(*) FROM dbo.FinancialPostingBatches WITH (TABLOCK,HOLDLOCK) WHERE UserId=@UserId;
            SELECT @discard=COUNT_BIG(*) FROM dbo.FinancialOperations WITH (TABLOCK,HOLDLOCK) WHERE UserId=@UserId;
            SELECT @discard=COUNT_BIG(*) FROM dbo.FinancialOpeningVersions WITH (TABLOCK,HOLDLOCK) WHERE UserId=@UserId;
            SELECT @discard=COUNT_BIG(*) FROM dbo.FinancialRequestOutcomes WITH (TABLOCK,HOLDLOCK) WHERE UserId=@UserId;
            SELECT @discard=COUNT_BIG(*) FROM dbo.FinancialAuditEntries WITH (TABLOCK,HOLDLOCK) WHERE UserId=@UserId;

            DECLARE @inspection uniqueidentifier=NEWID(),@captured datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),''+00:00'');
            DECLARE @rows bigint=(SELECT COUNT_BIG(*) FROM dbo.Accounts WHERE UserId=@UserId)*2
                +(SELECT COUNT_BIG(*) FROM dbo.FinancialPostingBatches WHERE UserId=@UserId)
                +(SELECT COUNT_BIG(*) FROM dbo.FinancialOperations WHERE UserId=@UserId)
                +(SELECT COUNT_BIG(*) FROM dbo.FinancialOpeningVersions WHERE UserId=@UserId)
                +(SELECT COUNT_BIG(*) FROM dbo.FinancialRequestOutcomes WHERE UserId=@UserId)
                +(SELECT COUNT_BIG(*) FROM dbo.FinancialAuditEntries WHERE UserId=@UserId)
                +(SELECT COUNT_BIG(*) FROM dbo.FinancialAccountMovements WHERE UserId=@UserId);
            IF @rows>@MaximumRows THROW 51040, ''Diagnostic capture exceeds the bounded inspection size.'', 1;
            SELECT @inspection AS InspectionId,@UserId AS OwnerId,@captured AS CapturedAtUtc,
                CONVERT(varchar(32),''shared-table-locks-v1'') AS ConsistencyMode,@rows AS [RowCount];

            SELECT Id,UserId,Currency,Status FROM dbo.Accounts WHERE UserId=@UserId ORDER BY Id;
            SELECT Id,UserId,Sequence,CommandType,RecordedAtUtc,EffectiveDate,AccountingTimeZone FROM dbo.FinancialPostingBatches WHERE UserId=@UserId ORDER BY Sequence;
            SELECT Id,UserId,BatchId,Kind,Currency,Amount,SourceAccountId,DestinationAccountId,OriginalOperationId,OriginalOperationKind,Reason FROM dbo.FinancialOperations WHERE UserId=@UserId ORDER BY Id;
            SELECT UserId,Currency,AccountId,Revision,PreviousRevision,BatchId,OpeningOperationId,OpeningOperationKind,Reason FROM dbo.FinancialOpeningVersions WHERE UserId=@UserId ORDER BY AccountId,Revision;
            SELECT Id,UserId,CommandType,IdempotencyKey,PayloadFingerprint,FingerprintVersion,OutcomeKind,BatchId,ResultCode,ReceiptJson,RecordedAtUtc FROM dbo.FinancialRequestOutcomes WHERE UserId=@UserId ORDER BY Id;
            SELECT Id,UserId,OutcomeId,RecordedAtUtc,Action,SummaryJson FROM dbo.FinancialAuditEntries WHERE UserId=@UserId ORDER BY Id;
            SELECT OperationId,UserId,AccountId,Currency,BatchId,BatchSequence,RecordedAtUtc,EffectiveDate,Kind,SignedAmount FROM dbo.FinancialAccountMovements WHERE UserId=@UserId ORDER BY OperationId,AccountId;

            DECLARE @account uniqueidentifier,@currency varchar(3),@initialized bit,@balance decimal(28,2);
            DECLARE normal_balances CURSOR LOCAL FAST_FORWARD FOR SELECT Id,Currency FROM dbo.Accounts WHERE UserId=@UserId ORDER BY Id;
            OPEN normal_balances; FETCH NEXT FROM normal_balances INTO @account,@currency;
            WHILE @@FETCH_STATUS=0
            BEGIN
                SET @initialized=NULL; SET @balance=NULL;
                BEGIN TRY
                    -- This is the unchanged usual balance validator, not the independent reconstruction.
                    -- The six retained table S locks exclude all relevant DML more broadly than its account anchor.
                    EXEC dbo.ReadLockedFinancialBalance @UserId,@account,@currency,@initialized OUTPUT,@balance OUTPUT;
                    SELECT @account AS AccountId,@currency AS Currency,@initialized AS IsInitialized,@balance AS Amount,
                        CONVERT(varchar(64),NULL) AS FailureCode;
                END TRY
                BEGIN CATCH
                    IF XACT_STATE()<>1 OR ERROR_NUMBER() NOT IN (51020,8115) THROW;
                    SELECT @account AS AccountId,@currency AS Currency,CONVERT(bit,NULL) AS IsInitialized,
                        CONVERT(decimal(28,2),NULL) AS Amount,CONVERT(varchar(64),''journal.inconsistent'') AS FailureCode;
                END CATCH;
                FETCH NEXT FROM normal_balances INTO @account,@currency;
            END;
            CLOSE normal_balances; DEALLOCATE normal_balances;
            COMMIT;
            -- A partial result stream is never a successful snapshot. The adapter requires and drains this marker.
            SELECT @inspection AS InspectionId,@UserId AS OwnerId,CONVERT(bit,1) AS IsComplete,@rows AS [RowCount];
        END TRY
        BEGIN CATCH
            IF XACT_STATE()<>0 ROLLBACK;
            THROW;
        END CATCH;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124181610_FinancialJournalInspection'
)
BEGIN
    EXEC(N'
    CREATE ROLE [VaultFinanceJournalDiagnostics] AUTHORIZATION [dbo];
    GRANT EXECUTE ON OBJECT::dbo.CaptureFinancialJournalInspection TO [VaultFinanceJournalDiagnostics];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Accounts TO [VaultFinanceJournalDiagnostics];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.FinancialPostingBatches TO [VaultFinanceJournalDiagnostics];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.FinancialOperations TO [VaultFinanceJournalDiagnostics];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.FinancialOpeningVersions TO [VaultFinanceJournalDiagnostics];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.FinancialRequestOutcomes TO [VaultFinanceJournalDiagnostics];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.FinancialAuditEntries TO [VaultFinanceJournalDiagnostics];
    DENY SELECT ON OBJECT::dbo.FinancialAccountMovements TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.InitializeFinancialOpening TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.RecordFinancialIncome TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.RecordFinancialExpense TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.RecordFinancialTransfer TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.RecordFinancialReversal TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.CorrectFinancialOpening TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.ExecuteFinancialWrite TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.AcquireFinancialAccountLocks TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.ReadLockedFinancialOpening TO [VaultFinanceJournalDiagnostics];
    DENY EXECUTE ON OBJECT::dbo.ReadLockedFinancialBalance TO [VaultFinanceJournalDiagnostics];
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124181610_FinancialJournalInspection'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261124181610_FinancialJournalInspection', N'10.0.15');
END;

COMMIT;
GO

