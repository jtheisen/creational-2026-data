IF OBJECT_ID(N'[__SCHEMA__].[ImageAdoptionCheckpoint]', N'U') IS NULL
    CREATE TABLE [__SCHEMA__].[ImageAdoptionCheckpoint] (
        PartitionName VARCHAR(64) NOT NULL CONSTRAINT PK_ImageAdoptionCheckpoint PRIMARY KEY,
        LastRowKey VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        Processed BIGINT NOT NULL,
        Matched BIGINT NOT NULL,
        Copied BIGINT NOT NULL,
        Quarantined BIGINT NOT NULL,
        UpdatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_ImageAdoptionCheckpoint_Updated DEFAULT SYSUTCDATETIME()
    );

IF OBJECT_ID(N'[__SCHEMA__].[ImageAdoptionIssue]', N'U') IS NULL
    CREATE TABLE [__SCHEMA__].[ImageAdoptionIssue] (
        PartitionName VARCHAR(64) NOT NULL,
        LegacyRowKey VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        Reason VARCHAR(64) NOT NULL,
        Title VARCHAR(1536) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
        NotedAt DATETIME2(3) NOT NULL CONSTRAINT DF_ImageAdoptionIssue_Noted DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ImageAdoptionIssue PRIMARY KEY CLUSTERED (PartitionName,LegacyRowKey)
    );
