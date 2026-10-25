IF COL_LENGTH(N'[__SCHEMA__].[Image]', 'ImageInfoState') IS NULL
    ALTER TABLE [__SCHEMA__].[Image]
        ADD ImageInfoState TINYINT NOT NULL CONSTRAINT DF_Image_ImageInfoState DEFAULT (0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Image]') AND name=N'IX_Image_MetadataPriority')
    EXEC(N'CREATE INDEX IX_Image_MetadataPriority ON [__SCHEMA__].[Image](ImageInfoState,Priority DESC,ImageId)
        INCLUDE (SourceWiki) WHERE ImageInfoState=0');

IF OBJECT_ID(N'[__SCHEMA__].[ImageWorkSetting]', N'U') IS NULL
    CREATE TABLE [__SCHEMA__].[ImageWorkSetting] (
        Kind TINYINT NOT NULL CONSTRAINT PK_ImageWorkSetting PRIMARY KEY,
        Enabled BIT NOT NULL CONSTRAINT DF_ImageWorkSetting_Enabled DEFAULT (0),
        MinimumPriority REAL NOT NULL CONSTRAINT DF_ImageWorkSetting_MinimumPriority DEFAULT (0),
        Concurrency TINYINT NOT NULL CONSTRAINT DF_ImageWorkSetting_Concurrency DEFAULT (1),
        PacingMilliseconds INT NOT NULL CONSTRAINT DF_ImageWorkSetting_Pacing DEFAULT (1000),
        CONSTRAINT CK_ImageWorkSetting_Kind CHECK (Kind BETWEEN 1 AND 3),
        CONSTRAINT CK_ImageWorkSetting_Concurrency CHECK (Concurrency BETWEEN 1 AND 16),
        CONSTRAINT CK_ImageWorkSetting_Pacing CHECK (PacingMilliseconds BETWEEN 0 AND 60000)
    );

INSERT INTO [__SCHEMA__].[ImageWorkSetting](Kind)
SELECT k.Kind FROM (VALUES (1),(2),(3)) k(Kind)
WHERE NOT EXISTS (SELECT 1 FROM [__SCHEMA__].[ImageWorkSetting] s WHERE s.Kind=k.Kind);

IF OBJECT_ID(N'[__SCHEMA__].[ImageWorkQueue]', N'U') IS NULL
BEGIN
    CREATE TABLE [__SCHEMA__].[ImageWorkQueue] (
        ImageId INT NOT NULL,
        Kind TINYINT NOT NULL,
        Priority REAL NOT NULL,
        State TINYINT NOT NULL CONSTRAINT DF_ImageWorkQueue_State DEFAULT (0),
        DueAt DATETIME2(3) NOT NULL CONSTRAINT DF_ImageWorkQueue_Due DEFAULT SYSUTCDATETIME(),
        LeaseId UNIQUEIDENTIFIER NULL,
        LeaseUntil DATETIME2(3) NULL,
        AttemptNumber INT NOT NULL CONSTRAINT DF_ImageWorkQueue_Attempt DEFAULT (0),
        LastError VARCHAR(1024) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        CONSTRAINT PK_ImageWorkQueue PRIMARY KEY CLUSTERED (ImageId,Kind),
        CONSTRAINT FK_ImageWorkQueue_Image FOREIGN KEY (ImageId) REFERENCES [__SCHEMA__].[Image](ImageId),
        CONSTRAINT CK_ImageWorkQueue_State CHECK (State BETWEEN 0 AND 3)
    );
    CREATE INDEX IX_ImageWorkQueue_Claim ON [__SCHEMA__].[ImageWorkQueue](Kind,Priority DESC,ImageId)
        INCLUDE (AttemptNumber) WHERE State=0;
    CREATE INDEX IX_ImageWorkQueue_Due ON [__SCHEMA__].[ImageWorkQueue](State,DueAt,Kind,ImageId)
        WHERE State=1;
    CREATE INDEX IX_ImageWorkQueue_Lease ON [__SCHEMA__].[ImageWorkQueue](State,LeaseUntil,Kind,ImageId)
        WHERE State=2;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[ImageWorkQueue]') AND name=N'IX_ImageWorkQueue_Kind')
    CREATE INDEX IX_ImageWorkQueue_Kind ON [__SCHEMA__].[ImageWorkQueue](Kind,State);

IF OBJECT_ID(N'[__SCHEMA__].[ImageWorkAttempt]', N'U') IS NULL
BEGIN
    CREATE TABLE [__SCHEMA__].[ImageWorkAttempt] (
        ImageId INT NOT NULL,
        Kind TINYINT NOT NULL,
        AttemptNumber INT NOT NULL,
        LeaseId UNIQUEIDENTIFIER NOT NULL,
        StartedAt DATETIME2(3) NOT NULL CONSTRAINT DF_ImageWorkAttempt_Started DEFAULT SYSUTCDATETIME(),
        FinishedAt DATETIME2(3) NULL,
        Outcome TINYINT NOT NULL CONSTRAINT DF_ImageWorkAttempt_Outcome DEFAULT (0),
        Error VARCHAR(1024) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        CONSTRAINT PK_ImageWorkAttempt PRIMARY KEY CLUSTERED (ImageId,Kind,AttemptNumber)
    );
    CREATE INDEX IX_ImageWorkAttempt_Active ON [__SCHEMA__].[ImageWorkAttempt](Kind,StartedAt)
        INCLUDE (ImageId,LeaseId) WHERE FinishedAt IS NULL;
END;
