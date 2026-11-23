BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    ALTER TABLE [Accounts] ADD CONSTRAINT [AK_Accounts_UserId_Currency_Id] UNIQUE ([UserId], [Currency], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE TABLE [FinancialPostingBatches] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Sequence] bigint NOT NULL IDENTITY,
        [CommandType] int NOT NULL,
        [RecordedAtUtc] datetimeoffset(7) NOT NULL,
        [EffectiveDate] date NOT NULL,
        [AccountingTimeZone] varchar(64) NOT NULL,
        CONSTRAINT [PK_FinancialPostingBatches] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [AK_FinancialPostingBatches_UserId_CommandType_Id] UNIQUE ([UserId], [CommandType], [Id]),
        CONSTRAINT [AK_FinancialPostingBatches_UserId_Id] UNIQUE ([UserId], [Id]),
        CONSTRAINT [CK_FinancialPostingBatches_Command] CHECK ([CommandType] IN (1,2,3,4,5,6)),
        CONSTRAINT [CK_FinancialPostingBatches_Ids] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_FinancialPostingBatches_Utc] CHECK (DATEPART(TZOFFSET, [RecordedAtUtc]) = 0 AND [RecordedAtUtc] > CONVERT(datetimeoffset(7), '0001-01-01T00:00:00+00:00')),
        CONSTRAINT [CK_FinancialPostingBatches_Zone] CHECK ([AccountingTimeZone] COLLATE Latin1_General_100_BIN2 = 'America/Santo_Domingo' AND DATALENGTH([AccountingTimeZone]) = 21),
        CONSTRAINT [FK_FinancialPostingBatches_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE TABLE [FinancialOperations] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [BatchId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [Currency] varchar(3) NOT NULL,
        [Amount] decimal(28,2) NULL,
        [SourceAccountId] uniqueidentifier NULL,
        [DestinationAccountId] uniqueidentifier NULL,
        [OriginalOperationId] uniqueidentifier NULL,
        [OriginalOperationKind] int NULL,
        [Reason] nvarchar(200) NULL,
        CONSTRAINT [PK_FinancialOperations] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_FinancialOperations_UserId_Currency_BatchId_Id_Kind] UNIQUE ([UserId], [Currency], [BatchId], [Id], [Kind]),
        CONSTRAINT [AK_FinancialOperations_UserId_Currency_Id_Kind] UNIQUE ([UserId], [Currency], [Id], [Kind]),
        CONSTRAINT [CK_FinancialOperations_Currency] CHECK ([Currency] COLLATE Latin1_General_100_BIN2 IN ('DOP','USD') AND DATALENGTH([Currency]) = 3),
        CONSTRAINT [CK_FinancialOperations_Ids] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000' AND [BatchId] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_FinancialOperations_Reason] CHECK ([Reason] IS NULL OR (DATALENGTH([Reason]) BETWEEN 2 AND 400 AND DATALENGTH([Reason]) = DATALENGTH(TRIM(NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32) + NCHAR(133) + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195) + NCHAR(8196) + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201) + NCHAR(8202) + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288) FROM [Reason])))),
        CONSTRAINT [CK_FinancialOperations_Shape] CHECK ((
      [Kind] IN (1,2,3,4) AND [Amount] IS NOT NULL AND [Amount] > 0
      AND [OriginalOperationId] IS NULL AND [OriginalOperationKind] IS NULL
      AND (
        ([Kind] IN (1,2) AND [SourceAccountId] IS NULL AND [DestinationAccountId] IS NOT NULL)
        OR ([Kind] = 3 AND [SourceAccountId] IS NOT NULL AND [DestinationAccountId] IS NULL)
        OR ([Kind] = 4 AND [SourceAccountId] IS NOT NULL AND [DestinationAccountId] IS NOT NULL AND [SourceAccountId] <> [DestinationAccountId])
      )
      AND ([Kind] <> 1 OR [Reason] IS NOT NULL)
    )
    OR (
      [Kind] = 5 AND [Amount] IS NULL AND [SourceAccountId] IS NULL AND [DestinationAccountId] IS NULL
      AND [OriginalOperationId] IS NOT NULL AND [OriginalOperationId] <> [Id]
      AND [OriginalOperationKind] IS NOT NULL AND [OriginalOperationKind] IN (1,2,3,4)
      AND [Reason] IS NOT NULL
    )),
        CONSTRAINT [FK_FinancialOperations_Accounts_UserId_Currency_DestinationAccountId] FOREIGN KEY ([UserId], [Currency], [DestinationAccountId]) REFERENCES [Accounts] ([UserId], [Currency], [Id]),
        CONSTRAINT [FK_FinancialOperations_Accounts_UserId_Currency_SourceAccountId] FOREIGN KEY ([UserId], [Currency], [SourceAccountId]) REFERENCES [Accounts] ([UserId], [Currency], [Id]),
        CONSTRAINT [FK_FinancialOperations_FinancialOperations_UserId_Currency_OriginalOperationId_OriginalOperationKind] FOREIGN KEY ([UserId], [Currency], [OriginalOperationId], [OriginalOperationKind]) REFERENCES [FinancialOperations] ([UserId], [Currency], [Id], [Kind]),
        CONSTRAINT [FK_FinancialOperations_FinancialPostingBatches_UserId_BatchId] FOREIGN KEY ([UserId], [BatchId]) REFERENCES [FinancialPostingBatches] ([UserId], [Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE TABLE [FinancialRequestOutcomes] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CommandType] int NOT NULL,
        [IdempotencyKey] varchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [PayloadFingerprint] char(64) NOT NULL,
        [FingerprintVersion] int NOT NULL,
        [OutcomeKind] int NOT NULL,
        [BatchId] uniqueidentifier NULL,
        [ResultCode] varchar(64) NOT NULL,
        [ReceiptJson] nvarchar(max) NOT NULL,
        [RecordedAtUtc] datetimeoffset(7) NOT NULL,
        CONSTRAINT [PK_FinancialRequestOutcomes] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_FinancialRequestOutcomes_UserId_Id] UNIQUE ([UserId], [Id]),
        CONSTRAINT [CK_FinancialRequestOutcomes_Code] CHECK (DATALENGTH([ResultCode]) BETWEEN 1 AND 64 AND [ResultCode] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Za-z0-9_.-]%'),
        CONSTRAINT [CK_FinancialRequestOutcomes_Command] CHECK ([CommandType] IN (1,2,3,4,5,6)),
        CONSTRAINT [CK_FinancialRequestOutcomes_Fingerprint] CHECK (DATALENGTH([PayloadFingerprint]) = 64 AND [PayloadFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%' AND [FingerprintVersion] > 0),
        CONSTRAINT [CK_FinancialRequestOutcomes_Ids] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_FinancialRequestOutcomes_Key] CHECK (DATALENGTH([IdempotencyKey]) BETWEEN 1 AND 128 AND [IdempotencyKey] NOT LIKE '%[^A-Za-z0-9_-]%' COLLATE Latin1_General_100_BIN2),
        CONSTRAINT [CK_FinancialRequestOutcomes_Receipt] CHECK (ISJSON([ReceiptJson]) = 1 AND DATALENGTH([ReceiptJson]) <= 32768),
        CONSTRAINT [CK_FinancialRequestOutcomes_Result] CHECK (([OutcomeKind] = 1 AND [BatchId] IS NOT NULL) OR ([OutcomeKind] = 2 AND [BatchId] IS NULL)),
        CONSTRAINT [CK_FinancialRequestOutcomes_Utc] CHECK (DATEPART(TZOFFSET, [RecordedAtUtc]) = 0 AND [RecordedAtUtc] > CONVERT(datetimeoffset(7), '0001-01-01T00:00:00+00:00')),
        CONSTRAINT [FK_FinancialRequestOutcomes_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]),
        CONSTRAINT [FK_FinancialRequestOutcomes_FinancialPostingBatches_UserId_CommandType_BatchId] FOREIGN KEY ([UserId], [CommandType], [BatchId]) REFERENCES [FinancialPostingBatches] ([UserId], [CommandType], [Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE TABLE [FinancialOpeningVersions] (
        [UserId] uniqueidentifier NOT NULL,
        [Currency] varchar(3) NOT NULL,
        [AccountId] uniqueidentifier NOT NULL,
        [Revision] int NOT NULL,
        [PreviousRevision] int NULL,
        [BatchId] uniqueidentifier NOT NULL,
        [OpeningOperationId] uniqueidentifier NULL,
        [OpeningOperationKind] int NULL,
        [Reason] nvarchar(200) NOT NULL,
        CONSTRAINT [PK_FinancialOpeningVersions] PRIMARY KEY ([UserId], [Currency], [AccountId], [Revision]),
        CONSTRAINT [CK_FinancialOpeningVersions_Currency] CHECK ([Currency] COLLATE Latin1_General_100_BIN2 IN ('DOP','USD') AND DATALENGTH([Currency]) = 3),
        CONSTRAINT [CK_FinancialOpeningVersions_Opening] CHECK (([OpeningOperationId] IS NULL AND [OpeningOperationKind] IS NULL)
    OR ([OpeningOperationId] IS NOT NULL AND [OpeningOperationKind] IS NOT NULL AND [OpeningOperationKind] = 1)),
        CONSTRAINT [CK_FinancialOpeningVersions_Reason] CHECK (DATALENGTH([Reason]) BETWEEN 2 AND 400 AND DATALENGTH([Reason]) = DATALENGTH(TRIM(NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32) + NCHAR(133) + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195) + NCHAR(8196) + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201) + NCHAR(8202) + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288) FROM [Reason]))),
        CONSTRAINT [CK_FinancialOpeningVersions_Revision] CHECK (([Revision] = 1 AND [PreviousRevision] IS NULL)
    OR ([Revision] > 1 AND [PreviousRevision] IS NOT NULL AND [PreviousRevision] = [Revision] - 1)),
        CONSTRAINT [FK_FinancialOpeningVersions_Accounts_UserId_Currency_AccountId] FOREIGN KEY ([UserId], [Currency], [AccountId]) REFERENCES [Accounts] ([UserId], [Currency], [Id]),
        CONSTRAINT [FK_FinancialOpeningVersions_FinancialOpeningVersions_UserId_Currency_AccountId_PreviousRevision] FOREIGN KEY ([UserId], [Currency], [AccountId], [PreviousRevision]) REFERENCES [FinancialOpeningVersions] ([UserId], [Currency], [AccountId], [Revision]),
        CONSTRAINT [FK_FinancialOpeningVersions_FinancialOperations_UserId_Currency_BatchId_OpeningOperationId_OpeningOperationKind] FOREIGN KEY ([UserId], [Currency], [BatchId], [OpeningOperationId], [OpeningOperationKind]) REFERENCES [FinancialOperations] ([UserId], [Currency], [BatchId], [Id], [Kind]),
        CONSTRAINT [FK_FinancialOpeningVersions_FinancialPostingBatches_UserId_BatchId] FOREIGN KEY ([UserId], [BatchId]) REFERENCES [FinancialPostingBatches] ([UserId], [Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE TABLE [FinancialAuditEntries] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [OutcomeId] uniqueidentifier NOT NULL,
        [RecordedAtUtc] datetimeoffset(7) NOT NULL,
        [Action] varchar(64) NOT NULL,
        [SummaryJson] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_FinancialAuditEntries] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_FinancialAuditEntries_Action] CHECK (DATALENGTH([Action]) BETWEEN 1 AND 64 AND [Action] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Za-z0-9_.-]%'),
        CONSTRAINT [CK_FinancialAuditEntries_Ids] CHECK ([Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_FinancialAuditEntries_Summary] CHECK (ISJSON([SummaryJson]) = 1 AND DATALENGTH([SummaryJson]) <= 8192),
        CONSTRAINT [CK_FinancialAuditEntries_Utc] CHECK (DATEPART(TZOFFSET, [RecordedAtUtc]) = 0 AND [RecordedAtUtc] > CONVERT(datetimeoffset(7), '0001-01-01T00:00:00+00:00')),
        CONSTRAINT [FK_FinancialAuditEntries_FinancialRequestOutcomes_UserId_OutcomeId] FOREIGN KEY ([UserId], [OutcomeId]) REFERENCES [FinancialRequestOutcomes] ([UserId], [Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FinancialAuditEntries_UserId_OutcomeId] ON [FinancialAuditEntries] ([UserId], [OutcomeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialOpeningVersions_UserId_BatchId] ON [FinancialOpeningVersions] ([UserId], [BatchId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialOpeningVersions_UserId_Currency_AccountId_PreviousRevision] ON [FinancialOpeningVersions] ([UserId], [Currency], [AccountId], [PreviousRevision]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialOpeningVersions_UserId_Currency_BatchId_OpeningOperationId_OpeningOperationKind] ON [FinancialOpeningVersions] ([UserId], [Currency], [BatchId], [OpeningOperationId], [OpeningOperationKind]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_FinancialOpeningVersions_UserId_OpeningOperationId] ON [FinancialOpeningVersions] ([UserId], [OpeningOperationId]) WHERE [OpeningOperationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialOperations_UserId_BatchId] ON [FinancialOperations] ([UserId], [BatchId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialOperations_UserId_Currency_DestinationAccountId_BatchId] ON [FinancialOperations] ([UserId], [Currency], [DestinationAccountId], [BatchId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialOperations_UserId_Currency_OriginalOperationId_OriginalOperationKind] ON [FinancialOperations] ([UserId], [Currency], [OriginalOperationId], [OriginalOperationKind]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialOperations_UserId_Currency_SourceAccountId_BatchId] ON [FinancialOperations] ([UserId], [Currency], [SourceAccountId], [BatchId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_FinancialOperations_UserId_OriginalOperationId] ON [FinancialOperations] ([UserId], [OriginalOperationId]) WHERE [OriginalOperationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_FinancialPostingBatches_Sequence] ON [FinancialPostingBatches] ([Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialPostingBatches_UserId_EffectiveDate_Sequence] ON [FinancialPostingBatches] ([UserId], [EffectiveDate], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_FinancialRequestOutcomes_UserId_BatchId] ON [FinancialRequestOutcomes] ([UserId], [BatchId]) WHERE [BatchId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE INDEX [IX_FinancialRequestOutcomes_UserId_CommandType_BatchId] ON [FinancialRequestOutcomes] ([UserId], [CommandType], [BatchId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FinancialRequestOutcomes_UserId_CommandType_IdempotencyKey] ON [FinancialRequestOutcomes] ([UserId], [CommandType], [IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_FinancialPostingBatches_AppendOnly]
    ON [dbo].[FinancialPostingBatches]
    INSTEAD OF UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 51001, ''Financial journal history is append-only.'', 1;
    END;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_FinancialOperations_AppendOnly]
    ON [dbo].[FinancialOperations]
    INSTEAD OF UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 51001, ''Financial journal history is append-only.'', 1;
    END;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_FinancialOpeningVersions_AppendOnly]
    ON [dbo].[FinancialOpeningVersions]
    INSTEAD OF UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 51001, ''Financial journal history is append-only.'', 1;
    END;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_FinancialRequestOutcomes_AppendOnly]
    ON [dbo].[FinancialRequestOutcomes]
    INSTEAD OF UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 51001, ''Financial journal history is append-only.'', 1;
    END;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_FinancialAuditEntries_AppendOnly]
    ON [dbo].[FinancialAuditEntries]
    INSTEAD OF UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 51001, ''Financial journal history is append-only.'', 1;
    END;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    EXEC(N'CREATE VIEW [dbo].[FinancialAccountMovements]
    AS
    WITH OrdinaryEffects AS (
        SELECT [Id] AS [OperationId], [UserId], [DestinationAccountId] AS [AccountId],
               [Currency], [BatchId], [Kind], [Amount] AS [SignedAmount]
        FROM [dbo].[FinancialOperations]
        WHERE [Kind] IN (1,2,4)
        UNION ALL
        SELECT [Id], [UserId], [SourceAccountId], [Currency], [BatchId], [Kind],
               CAST(-[Amount] AS decimal(28,2))
        FROM [dbo].[FinancialOperations]
        WHERE [Kind] IN (3,4)
    ),
    AllEffects AS (
        SELECT [OperationId], [UserId], [AccountId], [Currency], [BatchId], [Kind], [SignedAmount]
        FROM OrdinaryEffects
        UNION ALL
        SELECT r.[Id], r.[UserId], e.[AccountId], r.[Currency], r.[BatchId], r.[Kind],
               CAST(-e.[SignedAmount] AS decimal(28,2))
        FROM [dbo].[FinancialOperations] r
        JOIN OrdinaryEffects e ON e.[OperationId] = r.[OriginalOperationId] AND e.[UserId] = r.[UserId]
        WHERE r.[Kind] = 5
    )
    SELECT e.[OperationId], e.[UserId], e.[AccountId], e.[Currency], e.[BatchId],
           b.[Sequence] AS [BatchSequence], b.[RecordedAtUtc], b.[EffectiveDate], e.[Kind], e.[SignedAmount]
    FROM AllEffects e
    JOIN [dbo].[FinancialPostingBatches] b ON b.[Id] = e.[BatchId] AND b.[UserId] = e.[UserId];');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261123215517_FinancialJournalPersistence'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261123215517_FinancialJournalPersistence', N'10.0.15');
END;

COMMIT;
GO

