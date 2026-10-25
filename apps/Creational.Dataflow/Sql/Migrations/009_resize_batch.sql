IF OBJECT_ID(N'[__SCHEMA__].[ImageResizeBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [__SCHEMA__].[ImageResizeBatch] (
        BatchId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ImageResizeBatch PRIMARY KEY,
        ManifestPath VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        ResultPath VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
        ExecutionId VARCHAR(128) NULL,
        Status TINYINT NOT NULL,
        CreatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_ImageResizeBatch_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_ImageResizeBatch_Updated DEFAULT SYSUTCDATETIME(),
        Error VARCHAR(1024) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        CONSTRAINT CK_ImageResizeBatch_Status CHECK (Status BETWEEN 0 AND 4)
    );
    CREATE INDEX IX_ImageResizeBatch_Open ON [__SCHEMA__].[ImageResizeBatch](Status,CreatedAt)
        INCLUDE (ExecutionId,ManifestPath,ResultPath) WHERE Status<3;
END;
