BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123224234_InitialFinancialWriter'
)
BEGIN
    EXEC(N'-- Frozen DDL for 20261123224234_InitialFinancialWriter. Do not edit after application.
    CREATE PROCEDURE [dbo].[AcquireFinancialAccountLocks]
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

        -- SQL uniqueidentifier order, not CLR Guid.CompareTo. Lock sequentially on the PK.
        DECLARE @id uniqueidentifier, @locked uniqueidentifier;
        DECLARE account_locks CURSOR LOCAL FAST_FORWARD FOR SELECT [Id] FROM @ids ORDER BY [Id];
        OPEN account_locks;
        FETCH NEXT FROM account_locks INTO @id;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SELECT @locked = [Id]
            FROM [dbo].[Accounts] WITH (UPDLOCK, HOLDLOCK, ROWLOCK, INDEX([PK_Accounts]))
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
    WHERE [MigrationId] = N'20261123224234_InitialFinancialWriter'
)
BEGIN
    EXEC(N'
    CREATE PROCEDURE [dbo].[ReadLockedFinancialBalance]
        @UserId uniqueidentifier,
        @AccountId uniqueidentifier,
        @Currency varchar(3),
        @IsInitialized bit OUTPUT,
        @Balance decimal(28,2) OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        IF @@TRANCOUNT = 0 OR XACT_STATE() <> 1
            THROW 51011, ''Balance validation requires the caller account lock and transaction.'', 1;

        SET @IsInitialized = CASE WHEN EXISTS (
            SELECT 1 FROM [dbo].[FinancialOpeningVersions]
            WHERE [UserId] = @UserId AND [AccountId] = @AccountId AND [Currency] = @Currency AND [Revision] = 1
        ) THEN 1 ELSE 0 END;

        -- Every relevant batch must have its final posted outcome and audit.
        IF EXISTS (
            SELECT 1 FROM (
                SELECT [BatchId] FROM [dbo].[FinancialAccountMovements]
                WHERE [UserId] = @UserId AND [AccountId] = @AccountId
                UNION
                SELECT [BatchId] FROM [dbo].[FinancialOpeningVersions]
                WHERE [UserId] = @UserId AND [AccountId] = @AccountId
            ) related
            WHERE NOT EXISTS (
                SELECT 1 FROM [dbo].[FinancialPostingBatches] b
                JOIN [dbo].[FinancialRequestOutcomes] r
                  ON r.[UserId] = b.[UserId] AND r.[BatchId] = b.[Id]
                 AND r.[CommandType] = b.[CommandType] AND r.[OutcomeKind] = 1
                JOIN [dbo].[FinancialAuditEntries] a ON a.[UserId] = r.[UserId] AND a.[OutcomeId] = r.[Id]
                WHERE b.[Id] = related.[BatchId] AND b.[UserId] = @UserId
            )
        )
            THROW 51020, ''Incomplete financial history requires investigation.'', 1;

        IF EXISTS (
            SELECT 1 FROM [dbo].[FinancialOpeningVersions] v
            JOIN [dbo].[FinancialOperations] o ON o.[Id] = v.[OpeningOperationId] AND o.[UserId] = v.[UserId]
            WHERE v.[UserId] = @UserId AND v.[AccountId] = @AccountId
              AND (o.[DestinationAccountId] <> @AccountId OR o.[Currency] <> @Currency)
        )
            THROW 51020, ''The opening reference does not match the account.'', 1;

        IF @IsInitialized = 0 AND EXISTS (
            SELECT 1 FROM [dbo].[FinancialAccountMovements] WHERE [UserId] = @UserId AND [AccountId] = @AccountId
        )
            THROW 51020, ''Movements exist without financial initialization.'', 1;

        DECLARE @total decimal(38,2);
        SELECT @total = COALESCE(SUM([SignedAmount]), 0)
        FROM [dbo].[FinancialAccountMovements]
        WHERE [UserId] = @UserId AND [AccountId] = @AccountId AND [Currency] = @Currency;

        IF @total < 0 OR @total > CONVERT(decimal(28,2), ''99999999999999999999999999.99'')
            THROW 51020, ''The derived balance is outside the supported range.'', 1;
        -- Check decimal(38,2) before narrowing or sending it to CLR decimal.
        SET @Balance = CASE WHEN @IsInitialized = 1 THEN CONVERT(decimal(28,2), @total) ELSE NULL END;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123224234_InitialFinancialWriter'
)
BEGIN
    EXEC(N'
    CREATE PROCEDURE [dbo].[InitializeFinancialOpening]
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
            THROW 51011, ''The financial protocol cannot join an ambient transaction.'', 1;

        DECLARE @empty uniqueidentifier = ''00000000-0000-0000-0000-000000000000'';
        DECLARE @amount decimal(28,2) = TRY_CONVERT(decimal(28,2), @AmountText);
        DECLARE @trim nvarchar(64) = NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);

        -- Wide inputs prevent parameter narrowing from erasing invalid precision/text.
        IF @UserId IS NULL OR @UserId = @empty OR @AccountId IS NULL OR @AccountId = @empty
            OR @amount IS NULL OR @amount < 0
            OR @AmountText IS NULL OR DATALENGTH(@AmountText) NOT BETWEEN 8 AND 58
            OR DATALENGTH(CONVERT(nvarchar(40), @amount)) <> DATALENGTH(@AmountText)
            OR CONVERT(nvarchar(40), @amount) COLLATE Latin1_General_100_BIN2 <> @AmountText COLLATE Latin1_General_100_BIN2
            OR @Currency IS NULL OR DATALENGTH(@Currency) <> 6
            OR @Currency COLLATE Latin1_General_100_BIN2 NOT IN (N''DOP'', N''USD'')
            OR @Reason IS NULL OR DATALENGTH(@Reason) NOT BETWEEN 2 AND 400
            OR DATALENGTH(@Reason) <> DATALENGTH(TRIM(@trim FROM @Reason))
            OR @IdempotencyKey IS NULL OR DATALENGTH(@IdempotencyKey) NOT BETWEEN 2 AND 256
            THROW 51010, ''Invalid financial protocol input.'', 1;

        DECLARE @position int = 1, @character int;
        WHILE @position <= DATALENGTH(@IdempotencyKey) / 2
        BEGIN
            SET @character = UNICODE(SUBSTRING(@IdempotencyKey, @position, 1));
            IF NOT (@character BETWEEN 48 AND 57 OR @character BETWEEN 65 AND 90
                OR @character BETWEEN 97 AND 122 OR @character IN (45,95))
                THROW 51010, ''Invalid idempotency key.'', 1;
            SET @position += 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM [dbo].[AspNetUsers] WHERE [Id] = @UserId)
            THROW 51010, ''The trusted owner context is invalid.'', 1;

        DECLARE @key varchar(128) = CONVERT(varchar(128), @IdempotencyKey);
        DECLARE @ccy varchar(3) = CONVERT(varchar(3), @Currency);
        -- v1: UTF-16LE, no BOM. Fixed fields + a length-prefixed final Unicode reason.
        DECLARE @canonical nvarchar(max) = N''VF-OPEN'' + NCHAR(10) + N''1'' + NCHAR(10)
            + LOWER(CONVERT(nvarchar(36), @AccountId)) + NCHAR(10) + @AmountText + NCHAR(10)
            + @Currency + NCHAR(10) + CONVERT(nvarchar(10), DATALENGTH(@Reason)/2) + N'':'' + @Reason;
        DECLARE @fingerprint char(64) = CONVERT(char(64), HASHBYTES(''SHA2_256'', CONVERT(varbinary(max), @canonical)), 2);
        DECLARE @resource nvarchar(255) = N''VF:1:'' + CONVERT(nvarchar(36), @UserId) + N'':''
            + CONVERT(nvarchar(64), HASHBYTES(''SHA2_256'', CONVERT(varbinary(max), @IdempotencyKey)), 2);
        DECLARE @code varchar(64), @receipt nvarchar(max), @existingHash char(64), @existingVersion int;
        DECLARE @outcome uniqueidentifier, @lockResult int;

        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
        BEGIN TRY
            BEGIN TRANSACTION;
            EXEC @lockResult = sys.sp_getapplock @Resource=@resource, @LockMode=''Exclusive'',
                @LockOwner=''Transaction'', @LockTimeout=5000, @DbPrincipal=''public'';
            IF @lockResult < 0
                THROW 51030, ''The financial request lock could not be acquired.'', 1;

            SELECT @outcome=[Id], @existingHash=[PayloadFingerprint], @existingVersion=[FingerprintVersion],
                   @code=[ResultCode], @receipt=[ReceiptJson]
            FROM [dbo].[FinancialRequestOutcomes]
            WHERE [UserId]=@UserId AND [CommandType]=1 AND [IdempotencyKey]=@key;

            IF @outcome IS NOT NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM [dbo].[FinancialAuditEntries] WHERE [UserId]=@UserId AND [OutcomeId]=@outcome)
                    THROW 51020, ''The stored result has incomplete audit history.'', 1;
                IF @existingVersion <> 1 OR @existingHash COLLATE Latin1_General_100_BIN2 <> @fingerprint COLLATE Latin1_General_100_BIN2
                BEGIN
                    SET @code = ''idempotency.conflict'';
                    SET @receipt = N''{"receiptVersion":1,"failure":"idempotency.conflict"}'';
                END;
                COMMIT;
                SELECT @code AS [ResultCode], @receipt AS [ReceiptJson];
                RETURN;
            END;

            EXEC [dbo].[AcquireFinancialAccountLocks] @UserId, @AccountId;
            DECLARE @accountCurrency varchar(3), @status int;
            SELECT @accountCurrency=[Currency], @status=[Status]
            FROM [dbo].[Accounts] WITH (UPDLOCK, HOLDLOCK, ROWLOCK, INDEX([PK_Accounts]))
            WHERE [Id]=@AccountId AND [UserId]=@UserId;

            DECLARE @recorded datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), ''+00:00'');
            DECLARE @effective date = CONVERT(date, @recorded AT TIME ZONE ''SA Western Standard Time'');
            DECLARE @initialized bit, @balance decimal(28,2);
            SET @code = NULL;

            IF @accountCurrency IS NULL SET @code = ''account.not_found'';
            ELSE IF @status <> 1 SET @code = ''account.archived'';
            ELSE IF @accountCurrency <> @ccy SET @code = ''account.currency_mismatch'';
            ELSE
            BEGIN
                EXEC [dbo].[ReadLockedFinancialBalance] @UserId, @AccountId, @ccy, @initialized OUTPUT, @balance OUTPUT;
                IF @initialized = 1 SET @code = ''opening.already_initialized'';
            END;

            SET @outcome = NEWID();
            DECLARE @batch uniqueidentifier = NULL, @operation uniqueidentifier = NULL;
            DECLARE @outcomeKind int = 2;
            IF @code IS NULL
            BEGIN
                SET @batch = NEWID();
                SET @outcomeKind = 1;
                SET @code = ''opening.initialized'';
                INSERT INTO [dbo].[FinancialPostingBatches]
                    ([Id],[UserId],[CommandType],[RecordedAtUtc],[EffectiveDate],[AccountingTimeZone])
                VALUES (@batch,@UserId,1,@recorded,@effective,''America/Santo_Domingo'');

                IF @amount > 0
                BEGIN
                    SET @operation = NEWID();
                    INSERT INTO [dbo].[FinancialOperations]
                        ([Id],[UserId],[BatchId],[Kind],[Currency],[Amount],[SourceAccountId],[DestinationAccountId],
                         [OriginalOperationId],[OriginalOperationKind],[Reason])
                    VALUES (@operation,@UserId,@batch,1,@ccy,@amount,NULL,@AccountId,NULL,NULL,@Reason);
                END;

                INSERT INTO [dbo].[FinancialOpeningVersions]
                    ([UserId],[Currency],[AccountId],[Revision],[PreviousRevision],[BatchId],
                     [OpeningOperationId],[OpeningOperationKind],[Reason])
                VALUES (@UserId,@ccy,@AccountId,1,NULL,@batch,@operation,
                        CASE WHEN @operation IS NULL THEN NULL ELSE 1 END,@Reason);

                SET @receipt = (SELECT 1 AS [receiptVersion], @AccountId AS [accountId], @batch AS [batchId],
                    @operation AS [operationId], 1 AS [revision], @amount AS [openingAmount], @ccy AS [currency],
                    @recorded AS [recordedAtUtc], @effective AS [effectiveDate]
                    FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER);
            END
            ELSE
                SET @receipt = (SELECT 1 AS [receiptVersion], @code AS [failure] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

            INSERT INTO [dbo].[FinancialRequestOutcomes]
                ([Id],[UserId],[CommandType],[IdempotencyKey],[PayloadFingerprint],[FingerprintVersion],
                 [OutcomeKind],[BatchId],[ResultCode],[ReceiptJson],[RecordedAtUtc])
            VALUES (@outcome,@UserId,1,@key,@fingerprint,1,@outcomeKind,@batch,@code,@receipt,@recorded);

            DECLARE @summary nvarchar(max) = (SELECT @AccountId AS [accountId], @batch AS [batchId]
                FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER);
            INSERT INTO [dbo].[FinancialAuditEntries] ([Id],[UserId],[OutcomeId],[RecordedAtUtc],[Action],[SummaryJson])
            VALUES (NEWID(),@UserId,@outcome,@recorded,
                CASE WHEN @outcomeKind=1 THEN ''financial.opening.posted'' ELSE ''financial.opening.rejected'' END,@summary);

            IF @outcomeKind = 1
            BEGIN
                EXEC [dbo].[ReadLockedFinancialBalance] @UserId, @AccountId, @ccy, @initialized OUTPUT, @balance OUTPUT;
                IF @initialized <> 1 OR @balance IS NULL OR @balance <> @amount
                    THROW 51020, ''Opening final-batch validation failed.'', 1;
            END;

            COMMIT;
            SELECT @code AS [ResultCode], @receipt AS [ReceiptJson];
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK;
            THROW;
        END CATCH;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123224234_InitialFinancialWriter'
)
BEGIN
    EXEC(N'
    CREATE PROCEDURE [dbo].[GetDerivedFinancialBalance]
        @UserId uniqueidentifier,
        @AccountId uniqueidentifier
    AS
    BEGIN
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        SET LOCK_TIMEOUT 5000;
        IF @@TRANCOUNT <> 0
            THROW 51011, ''The financial read cannot join an ambient transaction.'', 1;
        IF @UserId IS NULL OR @AccountId IS NULL
           OR @UserId=''00000000-0000-0000-0000-000000000000''
           OR @AccountId=''00000000-0000-0000-0000-000000000000''
            THROW 51010, ''Invalid financial read input.'', 1;

        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
        BEGIN TRY
            BEGIN TRANSACTION;
            -- U locks serialize readers with financial writers even under RCSI.
            EXEC [dbo].[AcquireFinancialAccountLocks] @UserId, @AccountId;
            DECLARE @currency varchar(3), @initialized bit, @balance decimal(28,2);
            SELECT @currency=[Currency] FROM [dbo].[Accounts] WHERE [UserId]=@UserId AND [Id]=@AccountId;
            IF @currency IS NULL
            BEGIN
                COMMIT;
                SELECT CONVERT(varchar(64),''account.not_found'') AS [ResultCode],
                    CONVERT(varchar(3),NULL) AS [Currency], CONVERT(bit,NULL) AS [IsInitialized],
                    CONVERT(decimal(28,2),NULL) AS [Amount];
                RETURN;
            END;

            EXEC [dbo].[ReadLockedFinancialBalance] @UserId, @AccountId, @currency, @initialized OUTPUT, @balance OUTPUT;
            COMMIT;
            SELECT CONVERT(varchar(64),''balance.read'') AS [ResultCode], @currency AS [Currency],
                @initialized AS [IsInitialized], @balance AS [Amount];
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK;
            THROW;
        END CATCH;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123224234_InitialFinancialWriter'
)
BEGIN
    EXEC(N'
    CREATE ROLE [VaultFinanceFinancialRuntime] AUTHORIZATION [dbo];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[dbo].[FinancialPostingBatches] TO [VaultFinanceFinancialRuntime];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[dbo].[FinancialOperations] TO [VaultFinanceFinancialRuntime];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[dbo].[FinancialOpeningVersions] TO [VaultFinanceFinancialRuntime];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[dbo].[FinancialRequestOutcomes] TO [VaultFinanceFinancialRuntime];
    DENY SELECT, INSERT, UPDATE, DELETE ON OBJECT::[dbo].[FinancialAuditEntries] TO [VaultFinanceFinancialRuntime];
    DENY SELECT ON OBJECT::[dbo].[FinancialAccountMovements] TO [VaultFinanceFinancialRuntime];
    GRANT EXECUTE ON OBJECT::[dbo].[InitializeFinancialOpening] TO [VaultFinanceFinancialRuntime];
    GRANT EXECUTE ON OBJECT::[dbo].[GetDerivedFinancialBalance] TO [VaultFinanceFinancialRuntime];
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123224234_InitialFinancialWriter'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261123224234_InitialFinancialWriter', N'10.0.15');
END;

COMMIT;
GO

