IF OBJECT_ID(N'[__SCHEMA__].[CollectionTargetState]', N'U') IS NULL
BEGIN
    CREATE TABLE [__SCHEMA__].[CollectionTargetState] (
        CollectionName VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        Kind VARCHAR(80) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        TargetKey VARCHAR(400) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        StateJson VARCHAR(MAX) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        Outcome VARCHAR(32) NOT NULL,
        AttemptId VARCHAR(64) NULL,
        UpdatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_CollectionTargetState_Updated DEFAULT SYSUTCDATETIME(),
        Version ROWVERSION NOT NULL,
        CONSTRAINT PK_CollectionTargetState PRIMARY KEY CLUSTERED (CollectionName, Kind, TargetKey)
    );
    CREATE INDEX IX_CollectionTargetState_Active
        ON [__SCHEMA__].[CollectionTargetState](Outcome, CollectionName)
        INCLUDE (Kind, TargetKey, AttemptId)
        WHERE AttemptId IS NOT NULL;
END;

IF OBJECT_ID(N'[__SCHEMA__].[CollectionTargetAttempt]', N'U') IS NULL
BEGIN
    CREATE TABLE [__SCHEMA__].[CollectionTargetAttempt] (
        AttemptId VARCHAR(64) NOT NULL,
        CollectionName VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        Kind VARCHAR(80) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        TargetKey VARCHAR(400) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        Outcome VARCHAR(32) NOT NULL,
        ExecutionId VARCHAR(128) NULL,
        StartedAt DATETIME2(3) NOT NULL,
        FinishedAt DATETIME2(3) NULL,
        Error VARCHAR(1024) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        CONSTRAINT PK_CollectionTargetAttempt PRIMARY KEY CLUSTERED (AttemptId)
    );
    CREATE INDEX IX_CollectionTargetAttempt_Target
        ON [__SCHEMA__].[CollectionTargetAttempt](CollectionName, Kind, TargetKey, StartedAt DESC);
END;

IF OBJECT_ID(N'[__SCHEMA__].[CollectionSetting]', N'U') IS NULL
    CREATE TABLE [__SCHEMA__].[CollectionSetting] (
        CollectionName VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        Parallelism TINYINT NOT NULL CONSTRAINT DF_CollectionSetting_Parallelism DEFAULT (1),
        CONSTRAINT PK_CollectionSetting PRIMARY KEY CLUSTERED (CollectionName),
        CONSTRAINT CK_CollectionSetting_Parallelism CHECK (Parallelism BETWEEN 1 AND 16)
    );
