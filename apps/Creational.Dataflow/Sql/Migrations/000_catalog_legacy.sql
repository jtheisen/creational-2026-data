CREATE TABLE [__SCHEMA__].[Collection] (
    CollectionId SMALLINT NOT NULL,
    CollectionKey NVARCHAR(128) NOT NULL,
    SourceFingerprint CHAR(64) NULL,
    CONSTRAINT PK_Collection PRIMARY KEY CLUSTERED (CollectionId),
    CONSTRAINT UQ_Collection_Key UNIQUE (CollectionKey)
);

CREATE TABLE [__SCHEMA__].[TaxonomyTree] (
    TreeId SMALLINT NOT NULL,
    TreeKey NVARCHAR(128) NOT NULL,
    SourceFingerprint CHAR(64) NULL,
    CONSTRAINT PK_TaxonomyTree PRIMARY KEY CLUSTERED (TreeId),
    CONSTRAINT UQ_TaxonomyTree_Key UNIQUE (TreeKey)
);

CREATE TABLE [__SCHEMA__].[Taxon] (
    TreeId SMALLINT NOT NULL,
    OttId INT NOT NULL,
    ParentOttId INT NULL,
    DfsStart INT NOT NULL,
    DfsEndExclusive INT NOT NULL,
    CONSTRAINT PK_Taxon PRIMARY KEY CLUSTERED (TreeId, OttId),
    CONSTRAINT FK_Taxon_Tree FOREIGN KEY (TreeId) REFERENCES [__SCHEMA__].[TaxonomyTree](TreeId),
    CONSTRAINT CK_Taxon_Dfs CHECK (DfsStart >= 0 AND DfsEndExclusive > DfsStart)
);
CREATE UNIQUE INDEX IX_Taxon_DfsStart ON [__SCHEMA__].[Taxon](TreeId, DfsStart) INCLUDE (DfsEndExclusive);

CREATE TABLE [__SCHEMA__].[TaxonName] (
    TreeId SMALLINT NOT NULL,
    OttId INT NOT NULL,
    Name NVARCHAR(1024) NOT NULL,
    CONSTRAINT PK_TaxonName PRIMARY KEY CLUSTERED (TreeId, OttId),
    CONSTRAINT FK_TaxonName_Taxon FOREIGN KEY (TreeId, OttId) REFERENCES [__SCHEMA__].[Taxon](TreeId, OttId)
);

CREATE TABLE [__SCHEMA__].[Page] (
    CollectionId SMALLINT NOT NULL,
    PageId BIGINT NOT NULL,
    Title NVARCHAR(512) NOT NULL,
    RevisionId BIGINT NULL,
    UnresolvedImageCount INT NOT NULL CONSTRAINT DF_Page_UnresolvedImageCount DEFAULT (0),
    CONSTRAINT PK_Page PRIMARY KEY CLUSTERED (CollectionId, PageId),
    CONSTRAINT FK_Page_Collection FOREIGN KEY (CollectionId) REFERENCES [__SCHEMA__].[Collection](CollectionId),
    CONSTRAINT CK_Page_UnresolvedImageCount CHECK (UnresolvedImageCount >= 0)
);
CREATE INDEX IX_Page_Title ON [__SCHEMA__].[Page](CollectionId, Title) INCLUDE (PageId);

CREATE TABLE [__SCHEMA__].[TaxonPage] (
    TreeId SMALLINT NOT NULL,
    OttId INT NOT NULL,
    CollectionId SMALLINT NOT NULL,
    PageId BIGINT NOT NULL,
    MatchKind VARCHAR(32) NOT NULL,
    CONSTRAINT PK_TaxonPage PRIMARY KEY CLUSTERED (TreeId, OttId, CollectionId, PageId),
    CONSTRAINT FK_TaxonPage_Taxon FOREIGN KEY (TreeId, OttId) REFERENCES [__SCHEMA__].[Taxon](TreeId, OttId),
    CONSTRAINT FK_TaxonPage_Page FOREIGN KEY (CollectionId, PageId) REFERENCES [__SCHEMA__].[Page](CollectionId, PageId)
);
CREATE INDEX IX_TaxonPage_Page ON [__SCHEMA__].[TaxonPage](CollectionId, PageId) INCLUDE (TreeId, OttId);

CREATE TABLE [__SCHEMA__].[Image] (
    ImageId INT IDENTITY(1,1) NOT NULL,
    SourceWiki VARCHAR(16) NOT NULL,
    CanonicalFileTitle NVARCHAR(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Priority REAL NOT NULL CONSTRAINT DF_Image_Priority DEFAULT (0),
    MetadataStatus VARCHAR(32) NOT NULL CONSTRAINT DF_Image_MetadataStatus DEFAULT ('pending'),
    MetadataStale BIT NOT NULL CONSTRAINT DF_Image_MetadataStale DEFAULT (0),
    OriginalBytes BIGINT NULL,
    SourceSha1 CHAR(40) NULL,
    UploadedAt DATETIME2(0) NULL,
    Mime VARCHAR(128) NULL,
    MediaType VARCHAR(32) NULL,
    CONSTRAINT PK_Image PRIMARY KEY CLUSTERED (ImageId),
    CONSTRAINT CK_Image_OriginalBytes CHECK (OriginalBytes IS NULL OR OriginalBytes >= 0),
    CONSTRAINT UQ_Image_SourceTitle UNIQUE (SourceWiki, CanonicalFileTitle)
);

CREATE TABLE [__SCHEMA__].[ImageRights] (
    ImageId INT NOT NULL,
    LicenseShortName NVARCHAR(128) NULL,
    LicenseUrl VARCHAR(1024) NULL,
    Attribution NVARCHAR(2048) NULL,
    MissingRights BIT NOT NULL CONSTRAINT DF_ImageRights_MissingRights DEFAULT (0),
    RawImageInfoPath VARCHAR(512) NULL,
    FetchedAt DATETIME2(0) NULL,
    CONSTRAINT PK_ImageRights PRIMARY KEY CLUSTERED (ImageId),
    CONSTRAINT FK_ImageRights_Image FOREIGN KEY (ImageId) REFERENCES [__SCHEMA__].[Image](ImageId)
);

CREATE TABLE [__SCHEMA__].[PageImage] (
    CollectionId SMALLINT NOT NULL,
    PageId BIGINT NOT NULL,
    ReferenceIndex INT NOT NULL,
    FileTitle NVARCHAR(512) NOT NULL,
    SourceWiki VARCHAR(16) NULL,
    ImageId INT NULL,
    Role VARCHAR(32) NOT NULL,
    CONSTRAINT PK_PageImage PRIMARY KEY CLUSTERED (CollectionId, PageId, ReferenceIndex),
    CONSTRAINT FK_PageImage_Page FOREIGN KEY (CollectionId, PageId) REFERENCES [__SCHEMA__].[Page](CollectionId, PageId),
    CONSTRAINT FK_PageImage_Image FOREIGN KEY (ImageId) REFERENCES [__SCHEMA__].[Image](ImageId)
);
CREATE INDEX IX_PageImage_Image ON [__SCHEMA__].[PageImage](ImageId) INCLUDE (CollectionId, PageId, Role) WHERE ImageId IS NOT NULL;
CREATE INDEX IX_PageImage_Unlinked ON [__SCHEMA__].[PageImage](CollectionId, PageId, ReferenceIndex)
    INCLUDE (SourceWiki, FileTitle) WHERE ImageId IS NULL AND SourceWiki IS NOT NULL;

CREATE TABLE [__SCHEMA__].[Rendition] (
    ImageId INT NOT NULL,
    Variant VARCHAR(16) NOT NULL,
    Status VARCHAR(24) NOT NULL,
    Stale BIT NOT NULL CONSTRAINT DF_Rendition_Stale DEFAULT (0),
    Bytes BIGINT NULL,
    Sha256 BINARY(32) NULL,
    SourceSha256 BINARY(32) NULL,
    RecipeVersion VARCHAR(64) NULL,
    LastError NVARCHAR(1024) NULL,
    UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Rendition_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Rendition PRIMARY KEY CLUSTERED (ImageId, Variant),
    CONSTRAINT FK_Rendition_Image FOREIGN KEY (ImageId) REFERENCES [__SCHEMA__].[Image](ImageId),
    CONSTRAINT CK_Rendition_Bytes CHECK (Bytes IS NULL OR Bytes >= 0)
);

CREATE TABLE [__SCHEMA__].[CatalogLoadCheckpoint] (
    LoadKey NVARCHAR(256) NOT NULL,
    SourceFingerprint CHAR(64) NOT NULL,
    NextOrdinal BIGINT NOT NULL,
    Completed BIT NOT NULL CONSTRAINT DF_CatalogLoadCheckpoint_Completed DEFAULT (0),
    UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CatalogLoadCheckpoint_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_CatalogLoadCheckpoint PRIMARY KEY CLUSTERED (LoadKey),
    CONSTRAINT CK_CatalogLoadCheckpoint_NextOrdinal CHECK (NextOrdinal >= 0)
);
