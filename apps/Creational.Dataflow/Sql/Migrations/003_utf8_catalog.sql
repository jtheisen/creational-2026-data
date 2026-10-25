-- Converts the populated v1 catalog to UTF-8 VARCHAR. Each column/index block is restartable.
IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[Collection]') AND c.name=N'CollectionKey' AND (ty.name<>N'varchar'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Collection]') AND name=N'UQ_Collection_Key')
        ALTER TABLE [__SCHEMA__].[Collection] DROP CONSTRAINT UQ_Collection_Key;
    ALTER TABLE [__SCHEMA__].[Collection] ALTER COLUMN [CollectionKey] VARCHAR(384) NOT NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Collection]') AND name=N'UQ_Collection_Key')
    ALTER TABLE [__SCHEMA__].[Collection] ADD CONSTRAINT UQ_Collection_Key UNIQUE (CollectionKey);

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[Collection]') AND c.name=N'SourceFingerprint' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[Collection] ALTER COLUMN [SourceFingerprint] VARCHAR(64) NULL;
END;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[TaxonomyTree]') AND c.name=N'TreeKey' AND (ty.name<>N'varchar'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[TaxonomyTree]') AND name=N'UQ_TaxonomyTree_Key')
        ALTER TABLE [__SCHEMA__].[TaxonomyTree] DROP CONSTRAINT UQ_TaxonomyTree_Key;
    ALTER TABLE [__SCHEMA__].[TaxonomyTree] ALTER COLUMN [TreeKey] VARCHAR(384) NOT NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[TaxonomyTree]') AND name=N'UQ_TaxonomyTree_Key')
    ALTER TABLE [__SCHEMA__].[TaxonomyTree] ADD CONSTRAINT UQ_TaxonomyTree_Key UNIQUE (TreeKey);

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[TaxonomyTree]') AND c.name=N'SourceFingerprint' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[TaxonomyTree] ALTER COLUMN [SourceFingerprint] VARCHAR(64) NULL;
END;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[TaxonName]') AND c.name=N'Name' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[TaxonName] ALTER COLUMN [Name] VARCHAR(3072) NOT NULL;
END;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[Page]') AND c.name=N'Title' AND (ty.name<>N'varchar'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Page]') AND name=N'IX_Page_Title')
        DROP INDEX IX_Page_Title ON [__SCHEMA__].[Page];
    ALTER TABLE [__SCHEMA__].[Page] ALTER COLUMN [Title] VARCHAR(1536) NOT NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Page]') AND name=N'IX_Page_Title')
    CREATE INDEX IX_Page_Title ON [__SCHEMA__].[Page](CollectionId, Title) INCLUDE (PageId);

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[Image]') AND c.name=N'CanonicalFileTitle' AND (ty.name<>N'varchar' OR c.collation_name<>N'Latin1_General_100_BIN2_UTF8'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Image]') AND name=N'UQ_Image_SourceTitle')
        ALTER TABLE [__SCHEMA__].[Image] DROP CONSTRAINT UQ_Image_SourceTitle;
    ALTER TABLE [__SCHEMA__].[Image] ALTER COLUMN [CanonicalFileTitle] VARCHAR(1536) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Image]') AND name=N'UQ_Image_SourceTitle')
    ALTER TABLE [__SCHEMA__].[Image] ADD CONSTRAINT UQ_Image_SourceTitle UNIQUE (SourceWiki, CanonicalFileTitle);

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[Image]') AND c.name=N'SourceSha1' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[Image] ALTER COLUMN [SourceSha1] VARCHAR(40) NULL;
END;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[ImageRights]') AND c.name=N'LicenseShortName' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[ImageRights] ALTER COLUMN [LicenseShortName] VARCHAR(384) NULL;
END;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[ImageRights]') AND c.name=N'Attribution' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[ImageRights] ALTER COLUMN [Attribution] VARCHAR(6144) NULL;
END;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[PageImage]') AND c.name=N'FileTitle' AND (ty.name<>N'varchar'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[PageImage]') AND name=N'IX_PageImage_Unlinked')
        DROP INDEX IX_PageImage_Unlinked ON [__SCHEMA__].[PageImage];
    ALTER TABLE [__SCHEMA__].[PageImage] ALTER COLUMN [FileTitle] VARCHAR(1536) NOT NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[PageImage]') AND name=N'IX_PageImage_Unlinked')
    CREATE INDEX IX_PageImage_Unlinked ON [__SCHEMA__].[PageImage](CollectionId, PageId, ReferenceIndex) INCLUDE (SourceWiki, FileTitle) WHERE ImageId IS NULL AND SourceWiki IS NOT NULL;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[Rendition]') AND c.name=N'LastError' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[Rendition] ALTER COLUMN [LastError] VARCHAR(3072) NULL;
END;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[CatalogLoadCheckpoint]') AND c.name=N'LoadKey' AND (ty.name<>N'varchar'))
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[CatalogLoadCheckpoint]') AND name=N'PK_CatalogLoadCheckpoint')
        ALTER TABLE [__SCHEMA__].[CatalogLoadCheckpoint] DROP CONSTRAINT PK_CatalogLoadCheckpoint;
    ALTER TABLE [__SCHEMA__].[CatalogLoadCheckpoint] ALTER COLUMN [LoadKey] VARCHAR(768) NOT NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[CatalogLoadCheckpoint]') AND name=N'PK_CatalogLoadCheckpoint')
    ALTER TABLE [__SCHEMA__].[CatalogLoadCheckpoint] ADD CONSTRAINT PK_CatalogLoadCheckpoint PRIMARY KEY CLUSTERED (LoadKey);

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'[__SCHEMA__].[CatalogLoadCheckpoint]') AND c.name=N'SourceFingerprint' AND (ty.name<>N'varchar'))
BEGIN
    ALTER TABLE [__SCHEMA__].[CatalogLoadCheckpoint] ALTER COLUMN [SourceFingerprint] VARCHAR(64) NOT NULL;
END;
