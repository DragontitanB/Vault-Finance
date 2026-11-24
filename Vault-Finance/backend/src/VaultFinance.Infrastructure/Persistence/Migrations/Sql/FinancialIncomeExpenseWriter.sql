-- Frozen DDL for the Income/Expense increment. Applied migrations remain unchanged.
CREATE PROCEDURE [dbo].[ExecuteFinancialWrite]
    @CommandType int,
    @UserId uniqueidentifier,
    @AccountId uniqueidentifier,
    @AmountText nvarchar(max),
    @Currency nvarchar(max),
    @Reason nvarchar(max),
    @IdempotencyKey nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET LOCK_TIMEOUT 5000;
    IF @@TRANCOUNT <> 0
        THROW 51011, 'The financial protocol cannot join an ambient transaction.', 1;

    DECLARE @empty uniqueidentifier='00000000-0000-0000-0000-000000000000';
    DECLARE @amount decimal(28,2)=TRY_CONVERT(decimal(28,2),@AmountText);
    DECLARE @trim nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);

    IF @CommandType IS NULL OR @CommandType NOT IN (1,3,4)
       OR @UserId IS NULL OR @UserId=@empty OR @AccountId IS NULL OR @AccountId=@empty
       OR @amount IS NULL OR @amount<0 OR (@CommandType<>1 AND @amount=0)
       OR @AmountText IS NULL OR DATALENGTH(@AmountText) NOT BETWEEN 8 AND 58
       OR DATALENGTH(CONVERT(nvarchar(40),@amount))<>DATALENGTH(@AmountText)
       OR CONVERT(nvarchar(40),@amount) COLLATE Latin1_General_100_BIN2<>@AmountText COLLATE Latin1_General_100_BIN2
       OR @Currency IS NULL OR DATALENGTH(@Currency)<>6
       OR @Currency COLLATE Latin1_General_100_BIN2 NOT IN (N'DOP',N'USD')
       OR (@CommandType=1 AND @Reason IS NULL)
       OR (@Reason IS NOT NULL AND (DATALENGTH(@Reason) NOT BETWEEN 2 AND 400
           OR DATALENGTH(@Reason)<>DATALENGTH(TRIM(@trim FROM @Reason))))
       OR @IdempotencyKey IS NULL OR DATALENGTH(@IdempotencyKey) NOT BETWEEN 2 AND 256
        THROW 51010, 'Invalid financial protocol input.', 1;

    DECLARE @position int=1,@character int;
    WHILE @position<=DATALENGTH(@IdempotencyKey)/2
    BEGIN
        SET @character=UNICODE(SUBSTRING(@IdempotencyKey,@position,1));
        IF NOT (@character BETWEEN 48 AND 57 OR @character BETWEEN 65 AND 90
             OR @character BETWEEN 97 AND 122 OR @character IN (45,95))
            THROW 51010, 'Invalid idempotency key.', 1;
        SET @position+=1;
    END;
    IF NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers WHERE Id=@UserId)
        THROW 51010, 'The trusted owner context is invalid.', 1;

    DECLARE @key varchar(128)=CONVERT(varchar(128),@IdempotencyKey),@ccy varchar(3)=CONVERT(varchar(3),@Currency);
    DECLARE @header nvarchar(16)=CASE @CommandType WHEN 1 THEN N'VF-OPEN' WHEN 3 THEN N'VF-INCOME' ELSE N'VF-EXPENSE' END;
    DECLARE @name varchar(16)=CASE @CommandType WHEN 1 THEN 'opening' WHEN 3 THEN 'income' ELSE 'expense' END;
    DECLARE @kind int=CASE @CommandType WHEN 1 THEN 1 WHEN 3 THEN 2 ELSE 3 END;
    DECLARE @reasonTail nvarchar(max)=CASE WHEN @Reason IS NULL THEN N'-1:'
        ELSE CONVERT(nvarchar(10),DATALENGTH(@Reason)/2)+N':'+@Reason END;
    -- Opening v1 bytes stay identical to InitialFinancialWriter. New types have distinct headers.
    DECLARE @canonical nvarchar(max)=@header+NCHAR(10)+N'1'+NCHAR(10)
        +LOWER(CONVERT(nvarchar(36),@AccountId))+NCHAR(10)+@AmountText+NCHAR(10)+@Currency+NCHAR(10)+@reasonTail;
    DECLARE @fingerprint char(64)=CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@canonical)),2);
    DECLARE @resource nvarchar(255)=N'VF:'+CONVERT(nvarchar(10),@CommandType)+N':'
        +CONVERT(nvarchar(36),@UserId)+N':'
        +CONVERT(nvarchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@IdempotencyKey)),2);
    DECLARE @code varchar(64),@receipt nvarchar(max),@existingHash char(64),@existingVersion int;
    DECLARE @outcome uniqueidentifier,@lockResult int;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

    BEGIN TRY
        BEGIN TRANSACTION;
        EXEC @lockResult=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',
            @LockOwner='Transaction',@LockTimeout=5000,@DbPrincipal='public';
        IF @lockResult<0
            THROW 51030, 'The financial request lock could not be acquired.', 1;

        SELECT @outcome=Id,@existingHash=PayloadFingerprint,@existingVersion=FingerprintVersion,
               @code=ResultCode,@receipt=ReceiptJson
        FROM dbo.FinancialRequestOutcomes
        WHERE UserId=@UserId AND CommandType=@CommandType AND IdempotencyKey=@key;
        IF @outcome IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.FinancialAuditEntries WHERE UserId=@UserId AND OutcomeId=@outcome)
                THROW 51020, 'The stored result has incomplete audit history.', 1;
            IF @existingVersion<>1 OR @existingHash COLLATE Latin1_General_100_BIN2<>@fingerprint COLLATE Latin1_General_100_BIN2
            BEGIN
                SET @code='idempotency.conflict';
                SET @receipt=N'{"receiptVersion":1,"failure":"idempotency.conflict"}';
            END;
            COMMIT;
            SELECT @code AS ResultCode,@receipt AS ReceiptJson;
            RETURN;
        END;

        EXEC dbo.AcquireFinancialAccountLocks @UserId,@AccountId;
        DECLARE @accountCurrency varchar(3),@status int,@initialized bit,@balance decimal(28,2);
        SELECT @accountCurrency=Currency,@status=Status
        FROM dbo.Accounts WITH (UPDLOCK,HOLDLOCK,ROWLOCK,INDEX(PK_Accounts))
        WHERE Id=@AccountId AND UserId=@UserId;
        SET @code=NULL;
        IF @accountCurrency IS NULL SET @code='account.not_found';
        ELSE IF @status<>1 SET @code='account.archived';
        ELSE IF @accountCurrency<>@ccy SET @code='account.currency_mismatch';
        ELSE
        BEGIN
            EXEC dbo.ReadLockedFinancialBalance @UserId,@AccountId,@ccy,@initialized OUTPUT,@balance OUTPUT;
            IF @CommandType=1 AND @initialized=1 SET @code='opening.already_initialized';
            ELSE IF @CommandType<>1 AND @initialized=0 SET @code='account.uninitialized';
        END;

        DECLARE @recorded datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00');
        DECLARE @effective date=CONVERT(date,@recorded AT TIME ZONE 'SA Western Standard Time');
        DECLARE @expected decimal(29,2);
        IF @code IS NULL
        BEGIN
            IF @CommandType=1 SET @expected=@amount;
            ELSE
            BEGIN
                IF @balance IS NULL
                    THROW 51020, 'An initialized account has no valid balance.', 1;
                -- Two bounded decimal(28,2) operands produce exact decimal(29,2), never decimal(38,2) arithmetic.
                SET @expected=CASE WHEN @CommandType=3 THEN @balance+@amount ELSE @balance-@amount END;
                IF @expected<0 SET @code='funds.insufficient';
                ELSE IF @expected>CONVERT(decimal(28,2),'99999999999999999999999999.99')
                    SET @code='balance.limit_exceeded';

                DECLARE @lastUtc datetimeoffset(7),@lastDate date;
                SELECT @lastUtc=MAX(b.RecordedAtUtc),@lastDate=MAX(b.EffectiveDate)
                FROM dbo.FinancialPostingBatches b
                JOIN (
                    SELECT BatchId FROM dbo.FinancialAccountMovements WHERE UserId=@UserId AND AccountId=@AccountId
                    UNION
                    SELECT BatchId FROM dbo.FinancialOpeningVersions WHERE UserId=@UserId AND AccountId=@AccountId
                ) related ON related.BatchId=b.Id
                WHERE b.UserId=@UserId;
                IF @code IS NULL AND (@lastUtc>@recorded OR @lastDate>@effective)
                    THROW 51020, 'Financial recording time or accounting date regressed.', 1;
            END;
        END;

        SET @outcome=NEWID();
        DECLARE @batch uniqueidentifier=NULL,@operation uniqueidentifier=NULL,@outcomeKind int=2;
        IF @code IS NULL
        BEGIN
            SET @batch=NEWID();
            SET @outcomeKind=1;
            SET @code=CASE WHEN @CommandType=1 THEN 'opening.initialized' ELSE @name+'.posted' END;
            INSERT INTO dbo.FinancialPostingBatches (Id,UserId,CommandType,RecordedAtUtc,EffectiveDate,AccountingTimeZone)
            VALUES (@batch,@UserId,@CommandType,@recorded,@effective,'America/Santo_Domingo');

            IF @amount>0
            BEGIN
                SET @operation=NEWID();
                INSERT INTO dbo.FinancialOperations
                    (Id,UserId,BatchId,Kind,Currency,Amount,SourceAccountId,DestinationAccountId,
                     OriginalOperationId,OriginalOperationKind,Reason)
                VALUES (@operation,@UserId,@batch,@kind,@ccy,@amount,
                    CASE WHEN @CommandType=4 THEN @AccountId ELSE NULL END,
                    CASE WHEN @CommandType<>4 THEN @AccountId ELSE NULL END,NULL,NULL,@Reason);
            END;
            IF @CommandType=1
            BEGIN
                INSERT INTO dbo.FinancialOpeningVersions
                    (UserId,Currency,AccountId,Revision,PreviousRevision,BatchId,OpeningOperationId,OpeningOperationKind,Reason)
                VALUES (@UserId,@ccy,@AccountId,1,NULL,@batch,@operation,CASE WHEN @operation IS NULL THEN NULL ELSE 1 END,@Reason);
                SET @receipt=(SELECT 1 AS receiptVersion,@AccountId AS accountId,@batch AS batchId,@operation AS operationId,
                    1 AS revision,@amount AS openingAmount,@ccy AS currency,@recorded AS recordedAtUtc,@effective AS effectiveDate
                    FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
            END
            ELSE
                SET @receipt=(SELECT 1 AS receiptVersion,@AccountId AS accountId,@batch AS batchId,@operation AS operationId,
                    @kind AS kind,@amount AS amount,@ccy AS currency,@recorded AS recordedAtUtc,@effective AS effectiveDate
                    FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
        END
        ELSE
            SET @receipt=(SELECT 1 AS receiptVersion,@code AS failure FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);

        INSERT INTO dbo.FinancialRequestOutcomes
            (Id,UserId,CommandType,IdempotencyKey,PayloadFingerprint,FingerprintVersion,OutcomeKind,BatchId,ResultCode,ReceiptJson,RecordedAtUtc)
        VALUES (@outcome,@UserId,@CommandType,@key,@fingerprint,1,@outcomeKind,@batch,@code,@receipt,@recorded);
        DECLARE @summary nvarchar(max)=(SELECT @AccountId AS accountId,@batch AS batchId
            FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
        INSERT INTO dbo.FinancialAuditEntries (Id,UserId,OutcomeId,RecordedAtUtc,Action,SummaryJson)
        VALUES (NEWID(),@UserId,@outcome,@recorded,'financial.'+@name+CASE WHEN @outcomeKind=1 THEN '.posted' ELSE '.rejected' END,@summary);

        IF @outcomeKind=1
        BEGIN
            EXEC dbo.ReadLockedFinancialBalance @UserId,@AccountId,@ccy,@initialized OUTPUT,@balance OUTPUT;
            IF @initialized<>1 OR @balance IS NULL OR @balance<>@expected
                THROW 51020, 'Financial final-batch validation failed.', 1;
        END;
        COMMIT;
        SELECT @code AS ResultCode,@receipt AS ReceiptJson;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO

CREATE OR ALTER PROCEDURE [dbo].[InitializeFinancialOpening]
    @UserId uniqueidentifier,@AccountId uniqueidentifier,@AmountText nvarchar(max),
    @Currency nvarchar(max),@Reason nvarchar(max),@IdempotencyKey nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.ExecuteFinancialWrite 1,@UserId,@AccountId,@AmountText,@Currency,@Reason,@IdempotencyKey;
END;
GO

CREATE PROCEDURE [dbo].[RecordFinancialIncome]
    @UserId uniqueidentifier,@AccountId uniqueidentifier,@AmountText nvarchar(max),
    @Currency nvarchar(max),@Reason nvarchar(max)=NULL,@IdempotencyKey nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.ExecuteFinancialWrite 3,@UserId,@AccountId,@AmountText,@Currency,@Reason,@IdempotencyKey;
END;
GO

CREATE PROCEDURE [dbo].[RecordFinancialExpense]
    @UserId uniqueidentifier,@AccountId uniqueidentifier,@AmountText nvarchar(max),
    @Currency nvarchar(max),@Reason nvarchar(max)=NULL,@IdempotencyKey nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.ExecuteFinancialWrite 4,@UserId,@AccountId,@AmountText,@Currency,@Reason,@IdempotencyKey;
END;
GO

GRANT EXECUTE ON OBJECT::[dbo].[RecordFinancialIncome] TO [VaultFinanceFinancialRuntime];
GRANT EXECUTE ON OBJECT::[dbo].[RecordFinancialExpense] TO [VaultFinanceFinancialRuntime];
GO
