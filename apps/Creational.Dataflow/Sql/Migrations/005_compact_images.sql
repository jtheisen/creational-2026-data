SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'[__SCHEMA__].[ImageRights]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM [__SCHEMA__].[ImageRights])
        THROW 51001, 'ImageRights is populated; migrate it before replacing the legacy schema.', 1;
    DROP TABLE [__SCHEMA__].[ImageRights];
END;

IF OBJECT_ID(N'[__SCHEMA__].[Rendition]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[__SCHEMA__].[Rendition]', 'Variant') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM [__SCHEMA__].[Rendition])
        THROW 51002, 'Legacy Rendition is populated; migrate it before replacing the schema.', 1;
    DROP TABLE [__SCHEMA__].[Rendition];
END;

IF OBJECT_ID(N'[__SCHEMA__].[LicenseCategory]', N'U') IS NULL
    CREATE TABLE [__SCHEMA__].[LicenseCategory] (
        LicenseCategoryId TINYINT NOT NULL,
        Code VARCHAR(32) NOT NULL,
        DisplayName VARCHAR(64) NOT NULL,
        CONSTRAINT PK_LicenseCategory PRIMARY KEY CLUSTERED (LicenseCategoryId),
        CONSTRAINT UQ_LicenseCategory_Code UNIQUE (Code)
    );

IF OBJECT_ID(N'[__SCHEMA__].[RenditionShape]', N'U') IS NULL
    CREATE TABLE [__SCHEMA__].[RenditionShape] (
        ShapeId TINYINT NOT NULL,
        PathKey VARCHAR(8) NOT NULL,
        Width SMALLINT NOT NULL,
        OutputFormat VARCHAR(16) NOT NULL,
        SourceKind VARCHAR(16) NOT NULL,
        RecipeVersion VARCHAR(64) NOT NULL,
        CONSTRAINT PK_RenditionShape PRIMARY KEY CLUSTERED (ShapeId),
        CONSTRAINT UQ_RenditionShape_PathKey UNIQUE (PathKey),
        CONSTRAINT CK_RenditionShape_Width CHECK (Width>0)
    );

IF NOT EXISTS (SELECT 1 FROM [__SCHEMA__].[LicenseCategory])
    INSERT INTO [__SCHEMA__].[LicenseCategory](LicenseCategoryId,Code,DisplayName)
    VALUES (1,'cc-by','CC BY'),(2,'cc-by-sa','CC BY-SA'),(3,'cc0','CC0'),
           (4,'public-domain','Public domain'),(5,'other','Other reported license'),
           (6,'missing','No license reported');

IF NOT EXISTS (SELECT 1 FROM [__SCHEMA__].[RenditionShape])
    INSERT INTO [__SCHEMA__].[RenditionShape]
        (ShapeId,PathKey,Width,OutputFormat,SourceKind,RecipeVersion)
    VALUES (1,'w250',250,'source','wikimedia','wikimedia-w250-v1'),
           (2,'w20',20,'webp','derived','webp-from-w250-v1'),
           (3,'w40',40,'webp','derived','webp-from-w250-v1'),
           (4,'w60',60,'webp','derived','webp-from-w250-v1'),
           (5,'w120',120,'webp','derived','webp-from-w250-v1');

IF EXISTS (
    SELECT * FROM (VALUES (1,'cc-by','CC BY'),(2,'cc-by-sa','CC BY-SA'),
        (3,'cc0','CC0'),(4,'public-domain','Public domain'),
        (5,'other','Other reported license'),(6,'missing','No license reported')) v(Id,Code,Name)
    EXCEPT SELECT LicenseCategoryId,Code,DisplayName FROM [__SCHEMA__].[LicenseCategory]
   )
    THROW 51003, 'LicenseCategory definitions differ from the immutable seed.', 1;

IF EXISTS (
    SELECT * FROM (VALUES
        (1,'w250',250,'source','wikimedia','wikimedia-w250-v1'),
        (2,'w20',20,'webp','derived','webp-from-w250-v1'),
        (3,'w40',40,'webp','derived','webp-from-w250-v1'),
        (4,'w60',60,'webp','derived','webp-from-w250-v1'),
        (5,'w120',120,'webp','derived','webp-from-w250-v1')) v(Id,PathKey,Width,Format,Kind,Recipe)
    EXCEPT SELECT ShapeId,PathKey,Width,OutputFormat,SourceKind,RecipeVersion
    FROM [__SCHEMA__].[RenditionShape]
   )
    THROW 51004, 'RenditionShape definitions differ from the immutable seed.', 1;

IF COL_LENGTH(N'[__SCHEMA__].[Image]', 'LicenseCategoryId') IS NULL
    ALTER TABLE [__SCHEMA__].[Image] ADD LicenseCategoryId TINYINT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name=N'FK_Image_LicenseCategory'
                 AND parent_object_id=OBJECT_ID(N'[__SCHEMA__].[Image]'))
    ALTER TABLE [__SCHEMA__].[Image]
        ADD CONSTRAINT FK_Image_LicenseCategory FOREIGN KEY (LicenseCategoryId)
            REFERENCES [__SCHEMA__].[LicenseCategory](LicenseCategoryId);

IF OBJECT_ID(N'[__SCHEMA__].[Rendition]', N'U') IS NULL
    CREATE TABLE [__SCHEMA__].[Rendition] (
        ImageId INT NOT NULL,
        ShapeId TINYINT NOT NULL,
        Bytes INT NULL,
        Complete BIT NOT NULL CONSTRAINT DF_Rendition_Complete DEFAULT (0),
        Stale BIT NOT NULL CONSTRAINT DF_Rendition_Stale DEFAULT (0),
        Failed BIT NOT NULL CONSTRAINT DF_Rendition_Failed DEFAULT (0),
        CONSTRAINT PK_Rendition PRIMARY KEY CLUSTERED (ImageId,ShapeId),
        CONSTRAINT FK_Rendition_Image FOREIGN KEY (ImageId) REFERENCES [__SCHEMA__].[Image](ImageId),
        CONSTRAINT FK_Rendition_Shape FOREIGN KEY (ShapeId) REFERENCES [__SCHEMA__].[RenditionShape](ShapeId),
        CONSTRAINT CK_Rendition_Bytes CHECK (
            (Complete=0 AND Bytes IS NULL) OR (Complete=1 AND Bytes IS NOT NULL AND Bytes>=0))
    );

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Rendition]') AND name=N'IX_Rendition_Work')
    CREATE INDEX IX_Rendition_Work ON [__SCHEMA__].[Rendition](ShapeId,Complete,Stale,ImageId);

COMMIT TRANSACTION;
