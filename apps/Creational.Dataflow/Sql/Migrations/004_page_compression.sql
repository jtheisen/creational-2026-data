-- Measured PAGE-compression candidates; each index is independently restartable.
IF EXISTS (SELECT 1 FROM sys.partitions p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
           WHERE p.object_id=OBJECT_ID(N'[__SCHEMA__].[TaxonName]') AND i.name=N'PK_TaxonName'
             AND p.data_compression_desc<>N'PAGE')
    ALTER INDEX [PK_TaxonName] ON [__SCHEMA__].[TaxonName] REBUILD WITH (DATA_COMPRESSION=PAGE, MAXDOP=1);

IF EXISTS (SELECT 1 FROM sys.partitions p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
           WHERE p.object_id=OBJECT_ID(N'[__SCHEMA__].[Image]') AND i.name=N'PK_Image'
             AND p.data_compression_desc<>N'PAGE')
    ALTER INDEX [PK_Image] ON [__SCHEMA__].[Image] REBUILD WITH (DATA_COMPRESSION=PAGE, MAXDOP=1);

IF EXISTS (SELECT 1 FROM sys.partitions p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
           WHERE p.object_id=OBJECT_ID(N'[__SCHEMA__].[Image]') AND i.name=N'UQ_Image_SourceTitle'
             AND p.data_compression_desc<>N'PAGE')
    ALTER INDEX [UQ_Image_SourceTitle] ON [__SCHEMA__].[Image] REBUILD WITH (DATA_COMPRESSION=PAGE, MAXDOP=1);

IF EXISTS (SELECT 1 FROM sys.partitions p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
           WHERE p.object_id=OBJECT_ID(N'[__SCHEMA__].[Page]') AND i.name=N'PK_Page'
             AND p.data_compression_desc<>N'PAGE')
    ALTER INDEX [PK_Page] ON [__SCHEMA__].[Page] REBUILD WITH (DATA_COMPRESSION=PAGE, MAXDOP=1);

IF EXISTS (SELECT 1 FROM sys.partitions p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
           WHERE p.object_id=OBJECT_ID(N'[__SCHEMA__].[Page]') AND i.name=N'IX_Page_Title'
             AND p.data_compression_desc<>N'PAGE')
    ALTER INDEX [IX_Page_Title] ON [__SCHEMA__].[Page] REBUILD WITH (DATA_COMPRESSION=PAGE, MAXDOP=1);

IF EXISTS (SELECT 1 FROM sys.partitions p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id
           WHERE p.object_id=OBJECT_ID(N'[__SCHEMA__].[PageImage]') AND i.name=N'PK_PageImage'
             AND p.data_compression_desc<>N'PAGE')
    ALTER INDEX [PK_PageImage] ON [__SCHEMA__].[PageImage] REBUILD WITH (DATA_COMPRESSION=PAGE, MAXDOP=1);
