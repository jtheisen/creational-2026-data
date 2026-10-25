IF COL_LENGTH(N'[__SCHEMA__].[Image]', 'SourceGeneration') IS NULL
    ALTER TABLE [__SCHEMA__].[Image] ADD SourceGeneration INT NOT NULL
        CONSTRAINT DF_Image_SourceGeneration DEFAULT (0);

IF COL_LENGTH(N'[__SCHEMA__].[ImageWorkAttempt]', 'SourceGeneration') IS NULL
    ALTER TABLE [__SCHEMA__].[ImageWorkAttempt] ADD SourceGeneration INT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Image]')
               AND name=N'IX_Image_MetadataStale')
    CREATE INDEX IX_Image_MetadataStale ON [__SCHEMA__].[Image](MetadataStale,Priority DESC,ImageId)
        INCLUDE (SourceWiki) WHERE MetadataStale=1;
