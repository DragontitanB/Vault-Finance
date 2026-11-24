BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124152847_FinancialTransferWriter'
)
BEGIN
    EXEC(N'-- Frozen transfer increment. No prior migration resource is edited.
    CREATE OR ALTER PROCEDURE [dbo].[AcquireFinancialAccountLocks]
        @UserId uniqueidentifier,
        @FirstAccountId uniqueidentifier,
        @SecondAccountId uniqueidentifier = NULL
    AS
    BEGIN
        SET NOCOUNT ON;
        IF @@TRANCOUNT = 0 OR XACT_STATE() <> 1
            THROW 51011, ''Account locks require an active committable transaction.'', 1;

        DECLARE @ids table ([Id] uniqueidentifier NOT NULL PRIMARY KEY);
        INSERT INTO @ids VALUES (@FirstAccountId);
        IF @SecondAccountId IS NOT NULL AND @SecondAccountId <> @FirstAccountId
            INSERT INTO @ids VALUES (@SecondAccountId);

        -- The cursor sorts a private table of identifiers, NOT Account rows.
        -- Each loop issues a separate singleton PK seek and completes before the next lookup.
        -- FORCESEEK prevents a scan from acquiring unrelated account U locks out of order.
        DECLARE @id uniqueidentifier, @locked uniqueidentifier;
        DECLARE account_locks CURSOR LOCAL FAST_FORWARD FOR SELECT [Id] FROM @ids ORDER BY [Id];
        OPEN account_locks;
        FETCH NEXT FROM account_locks INTO @id;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SELECT @locked = [Id]
            FROM [dbo].[Accounts] WITH (UPDLOCK, HOLDLOCK, ROWLOCK, FORCESEEK([PK_Accounts]([Id])))
            WHERE [Id] = @id AND [UserId] = @UserId;
            FETCH NEXT FROM account_locks INTO @id;
        END;
        CLOSE account_locks;
        DEALLOCATE account_locks;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124152847_FinancialTransferWriter'
)
BEGIN
    EXEC(N'
    -- One shared transaction owner. Legacy fingerprints/receipts remain byte-compatible.
    CREATE OR ALTER PROCEDURE [dbo].[ExecuteFinancialWrite]
        @CommandType int,
        @UserId uniqueidentifier,
        @AccountId uniqueidentifier,
        @AmountText nvarchar(max),
        @Currency nvarchar(max),
        @Reason nvarchar(max),
        @IdempotencyKey nvarchar(max),
        @DestinationAccountId uniqueidentifier=NULL
    AS
    BEGIN
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        SET LOCK_TIMEOUT 5000;
        IF @@TRANCOUNT <> 0
            THROW 51011, ''The financial protocol cannot join an ambient transaction.'', 1;

        DECLARE @empty uniqueidentifier=''00000000-0000-0000-0000-000000000000'';
        DECLARE @amount decimal(28,2)=TRY_CONVERT(decimal(28,2),@AmountText);
        DECLARE @trim nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);

        IF @CommandType IS NULL OR @CommandType NOT IN (1,3,4,5)
           OR @UserId IS NULL OR @UserId=@empty OR @AccountId IS NULL OR @AccountId=@empty
           OR (@CommandType=5 AND (@DestinationAccountId IS NULL OR @DestinationAccountId=@empty OR @DestinationAccountId=@AccountId))
           OR (@CommandType<>5 AND @DestinationAccountId IS NOT NULL)
           OR @amount IS NULL OR @amount<0 OR (@CommandType<>1 AND @amount=0)
           OR @AmountText IS NULL OR DATALENGTH(@AmountText) NOT BETWEEN 8 AND 58
           OR DATALENGTH(CONVERT(nvarchar(40),@amount))<>DATALENGTH(@AmountText)
           OR CONVERT(nvarchar(40),@amount) COLLATE Latin1_General_100_BIN2<>@AmountText COLLATE Latin1_General_100_BIN2
           OR @Currency IS NULL OR DATALENGTH(@Currency)<>6
           OR @Currency COLLATE Latin1_General_100_BIN2 NOT IN (N''DOP'',N''USD'')
           OR (@CommandType=1 AND @Reason IS NULL)
           OR (@Reason IS NOT NULL AND (DATALENGTH(@Reason) NOT BETWEEN 2 AND 400
               OR DATALENGTH(@Reason)<>DATALENGTH(TRIM(@trim FROM @Reason))))
           OR @IdempotencyKey IS NULL OR DATALENGTH(@IdempotencyKey) NOT BETWEEN 2 AND 256
            THROW 51010, ''Invalid financial protocol input.'', 1;

        DECLARE @position int=1,@character int;
        WHILE @position<=DATALENGTH(@IdempotencyKey)/2
        BEGIN
            SET @character=UNICODE(SUBSTRING(@IdempotencyKey,@position,1));
            IF NOT (@character BETWEEN 48 AND 57 OR @character BETWEEN 65 AND 90
                 OR @character BETWEEN 97 AND 122 OR @character IN (45,95))
                THROW 51010, ''Invalid idempotency key.'', 1;
            SET @position+=1;
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers WHERE Id=@UserId)
            THROW 51010, ''The trusted owner context is invalid.'', 1;

        DECLARE @key varchar(128)=CONVERT(varchar(128),@IdempotencyKey),@ccy varchar(3)=CONVERT(varchar(3),@Currency);
        DECLARE @header nvarchar(16)=CASE @CommandType WHEN 1 THEN N''VF-OPEN'' WHEN 3 THEN N''VF-INCOME'' WHEN 4 THEN N''VF-EXPENSE'' ELSE N''VF-TRANSFER'' END;
        DECLARE @name varchar(16)=CASE @CommandType WHEN 1 THEN ''opening'' WHEN 3 THEN ''income'' WHEN 4 THEN ''expense'' ELSE ''transfer'' END;
        DECLARE @kind int=CASE @CommandType WHEN 1 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 3 ELSE 4 END;
        DECLARE @reasonTail nvarchar(max)=CASE WHEN @Reason IS NULL THEN N''-1:''
            ELSE CONVERT(nvarchar(10),DATALENGTH(@Reason)/2)+N'':''+@Reason END;
        -- Opening v1 bytes stay identical to InitialFinancialWriter. New types have distinct headers.
        DECLARE @canonical nvarchar(max)=@header+NCHAR(10)+N''1''+NCHAR(10)
            +LOWER(CONVERT(nvarchar(36),@AccountId))+NCHAR(10)
            +CASE WHEN @CommandType=5 THEN LOWER(CONVERT(nvarchar(36),@DestinationAccountId))+NCHAR(10) ELSE N'''' END
            +@AmountText+NCHAR(10)+@Currency+NCHAR(10)+@reasonTail;
        DECLARE @fingerprint char(64)=CONVERT(char(64),HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@canonical)),2);
        DECLARE @resource nvarchar(255)=N''VF:''+CONVERT(nvarchar(10),@CommandType)+N'':''
            +CONVERT(nvarchar(36),@UserId)+N'':''
            +CONVERT(nvarchar(64),HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@IdempotencyKey)),2);
        DECLARE @code varchar(64),@receipt nvarchar(max),@existingHash char(64),@existingVersion int;
        DECLARE @outcome uniqueidentifier,@lockResult int;
        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

        BEGIN TRY
            BEGIN TRANSACTION;
            EXEC @lockResult=sys.sp_getapplock @Resource=@resource,@LockMode=''Exclusive'',
                @LockOwner=''Transaction'',@LockTimeout=5000,@DbPrincipal=''public'';
            IF @lockResult<0
                THROW 51030, ''The financial request lock could not be acquired.'', 1;

            SELECT @outcome=Id,@existingHash=PayloadFingerprint,@existingVersion=FingerprintVersion,
                   @code=ResultCode,@receipt=ReceiptJson
            FROM dbo.FinancialRequestOutcomes
            WHERE UserId=@UserId AND CommandType=@CommandType AND IdempotencyKey=@key;
            IF @outcome IS NOT NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.FinancialAuditEntries WHERE UserId=@UserId AND OutcomeId=@outcome)
                    THROW 51020, ''The stored result has incomplete audit history.'', 1;
                IF @existingVersion<>1 OR @existingHash COLLATE Latin1_General_100_BIN2<>@fingerprint COLLATE Latin1_General_100_BIN2
                BEGIN
                    SET @code=''idempotency.conflict'';
                    SET @receipt=N''{"receiptVersion":1,"failure":"idempotency.conflict"}'';
                END;
                COMMIT;
                SELECT @code AS ResultCode,@receipt AS ReceiptJson;
                RETURN;
            END;

            EXEC dbo.AcquireFinancialAccountLocks @UserId,@AccountId,@DestinationAccountId;
            DECLARE @accountCurrency varchar(3),@status int,@initialized bit,@balance decimal(28,2);
            DECLARE @destinationCurrency varchar(3),@destinationStatus int,@destinationInitialized bit,@destinationBalance decimal(28,2);
            SELECT @accountCurrency=Currency,@status=Status
            FROM dbo.Accounts WITH (UPDLOCK,HOLDLOCK,ROWLOCK,FORCESEEK(PK_Accounts(Id)))
            WHERE Id=@AccountId AND UserId=@UserId;
            IF @CommandType=5
                SELECT @destinationCurrency=Currency,@destinationStatus=Status
                FROM dbo.Accounts WITH (UPDLOCK,HOLDLOCK,ROWLOCK,FORCESEEK(PK_Accounts(Id)))
                WHERE Id=@DestinationAccountId AND UserId=@UserId;
            SET @code=NULL;
            -- Resolve both owned participants before exposing any other account-state rejection.
            IF @accountCurrency IS NULL OR (@CommandType=5 AND @destinationCurrency IS NULL) SET @code=''account.not_found'';
            ELSE IF @status<>1 OR (@CommandType=5 AND @destinationStatus<>1) SET @code=''account.archived'';
            ELSE IF @accountCurrency<>@ccy OR (@CommandType=5 AND @destinationCurrency<>@ccy) SET @code=''account.currency_mismatch'';
            ELSE
            BEGIN
                EXEC dbo.ReadLockedFinancialBalance @UserId,@AccountId,@ccy,@initialized OUTPUT,@balance OUTPUT;
                IF @CommandType=5
                    EXEC dbo.ReadLockedFinancialBalance @UserId,@DestinationAccountId,@ccy,@destinationInitialized OUTPUT,@destinationBalance OUTPUT;
                IF @CommandType=1 AND @initialized=1 SET @code=''opening.already_initialized'';
                ELSE IF @CommandType<>1 AND (@initialized=0 OR (@CommandType=5 AND @destinationInitialized=0)) SET @code=''account.uninitialized'';
            END;

            DECLARE @recorded datetimeoffset(7)=TODATETIMEOFFSET(SYSUTCDATETIME(),''+00:00'');
            DECLARE @effective date=CONVERT(date,@recorded AT TIME ZONE ''SA Western Standard Time'');
            DECLARE @expected decimal(29,2),@destinationExpected decimal(29,2),@combinedBefore decimal(29,2);
            IF @code IS NULL
            BEGIN
                IF @CommandType=1 SET @expected=@amount;
                ELSE
                BEGIN
                    IF @balance IS NULL
                        THROW 51020, ''An initialized account has no valid balance.'', 1;
                    -- Two bounded decimal(28,2) operands produce exact decimal(29,2), never decimal(38,2) arithmetic.
                    SET @expected=CASE WHEN @CommandType=3 THEN @balance+@amount ELSE @balance-@amount END;
                    IF @CommandType=5
                    BEGIN
                        IF @destinationBalance IS NULL
                            THROW 51020, ''An initialized destination has no valid balance.'', 1;
                        SET @destinationExpected=@destinationBalance+@amount;
                        SET @combinedBefore=@balance+@destinationBalance;
                        -- decimal(29,2) + decimal(29,2) stays exact in decimal(30,2).
                        IF @expected+@destinationExpected<>@combinedBefore
                            THROW 51020, ''Transfer conservation validation failed.'', 1;
                    END;
                    IF @expected<0 SET @code=''funds.insufficient'';
                    ELSE IF @expected>CONVERT(decimal(28,2),''99999999999999999999999999.99'')
                         OR (@CommandType=5 AND @destinationExpected>CONVERT(decimal(28,2),''99999999999999999999999999.99''))
                        SET @code=''balance.limit_exceeded'';

                    DECLARE @lastUtc datetimeoffset(7),@lastDate date;
                    SELECT @lastUtc=MAX(b.RecordedAtUtc),@lastDate=MAX(b.EffectiveDate)
                    FROM dbo.FinancialPostingBatches b
                    JOIN (
                        SELECT BatchId FROM dbo.FinancialAccountMovements WHERE UserId=@UserId AND AccountId IN (@AccountId,@DestinationAccountId)
                        UNION
                        SELECT BatchId FROM dbo.FinancialOpeningVersions WHERE UserId=@UserId AND AccountId IN (@AccountId,@DestinationAccountId)
                    ) related ON related.BatchId=b.Id
                    WHERE b.UserId=@UserId;
                    IF @code IS NULL AND (@lastUtc>@recorded OR @lastDate>@effective)
                        THROW 51020, ''Financial recording time or accounting date regressed.'', 1;
                END;
            END;

            SET @outcome=NEWID();
            DECLARE @batch uniqueidentifier=NULL,@operation uniqueidentifier=NULL,@outcomeKind int=2;
            IF @code IS NULL
            BEGIN
                SET @batch=NEWID();
                SET @outcomeKind=1;
                SET @code=CASE WHEN @CommandType=1 THEN ''opening.initialized'' ELSE @name+''.posted'' END;
                INSERT INTO dbo.FinancialPostingBatches (Id,UserId,CommandType,RecordedAtUtc,EffectiveDate,AccountingTimeZone)
                VALUES (@batch,@UserId,@CommandType,@recorded,@effective,''America/Santo_Domingo'');

                IF @amount>0
                BEGIN
                    SET @operation=NEWID();
                    INSERT INTO dbo.FinancialOperations
                        (Id,UserId,BatchId,Kind,Currency,Amount,SourceAccountId,DestinationAccountId,
                         OriginalOperationId,OriginalOperationKind,Reason)
                    VALUES (@operation,@UserId,@batch,@kind,@ccy,@amount,
                        CASE WHEN @CommandType IN (4,5) THEN @AccountId ELSE NULL END,
                        CASE WHEN @CommandType=5 THEN @DestinationAccountId WHEN @CommandType=4 THEN NULL ELSE @AccountId END,NULL,NULL,@Reason);
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
                ELSE IF @CommandType=5
                    SET @receipt=(SELECT 1 AS receiptVersion,@AccountId AS sourceAccountId,@DestinationAccountId AS destinationAccountId,
                        @batch AS batchId,@operation AS operationId,@kind AS kind,@amount AS amount,@ccy AS currency,
                        @recorded AS recordedAtUtc,@effective AS effectiveDate
                        FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
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
            DECLARE @summary nvarchar(max);
            IF @CommandType=5
                SET @summary=(SELECT @AccountId AS sourceAccountId,@DestinationAccountId AS destinationAccountId,@batch AS batchId
                    FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
            ELSE
                SET @summary=(SELECT @AccountId AS accountId,@batch AS batchId
                    FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
            INSERT INTO dbo.FinancialAuditEntries (Id,UserId,OutcomeId,RecordedAtUtc,Action,SummaryJson)
            VALUES (NEWID(),@UserId,@outcome,@recorded,''financial.''+@name+CASE WHEN @outcomeKind=1 THEN ''.posted'' ELSE ''.rejected'' END,@summary);

            IF @outcomeKind=1
            BEGIN
                EXEC dbo.ReadLockedFinancialBalance @UserId,@AccountId,@ccy,@initialized OUTPUT,@balance OUTPUT;
                IF @initialized<>1 OR @balance IS NULL OR @balance<>@expected
                    THROW 51020, ''Financial final-batch validation failed.'', 1;
                IF @CommandType=5
                BEGIN
                    EXEC dbo.ReadLockedFinancialBalance @UserId,@DestinationAccountId,@ccy,@destinationInitialized OUTPUT,@destinationBalance OUTPUT;
                    IF @destinationInitialized<>1 OR @destinationBalance IS NULL OR @destinationBalance<>@destinationExpected
                        THROW 51020, ''Transfer destination final-batch validation failed.'', 1;
                    IF @balance+@destinationBalance<>@combinedBefore
                        THROW 51020, ''Transfer final conservation validation failed.'', 1;
                END;
            END;
            COMMIT;
            SELECT @code AS ResultCode,@receipt AS ReceiptJson;
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
    WHERE [MigrationId] = N'20261124152847_FinancialTransferWriter'
)
BEGIN
    EXEC(N'
    CREATE PROCEDURE [dbo].[RecordFinancialTransfer]
        @UserId uniqueidentifier,@SourceAccountId uniqueidentifier,@DestinationAccountId uniqueidentifier,
        @AmountText nvarchar(max),@Currency nvarchar(max),@Reason nvarchar(max)=NULL,@IdempotencyKey nvarchar(max)
    AS
    BEGIN
        SET NOCOUNT ON;
        EXEC dbo.ExecuteFinancialWrite @CommandType=5,@UserId=@UserId,@AccountId=@SourceAccountId,
            @AmountText=@AmountText,@Currency=@Currency,@Reason=@Reason,@IdempotencyKey=@IdempotencyKey,
            @DestinationAccountId=@DestinationAccountId;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124152847_FinancialTransferWriter'
)
BEGIN
    EXEC(N'
    GRANT EXECUTE ON OBJECT::[dbo].[RecordFinancialTransfer] TO [VaultFinanceFinancialRuntime];
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124152847_FinancialTransferWriter'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261124152847_FinancialTransferWriter', N'10.0.15');
END;

COMMIT;
GO

