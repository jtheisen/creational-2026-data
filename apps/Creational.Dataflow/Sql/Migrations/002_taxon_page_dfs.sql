IF COL_LENGTH(N'__SCHEMA__.TaxonPage', N'DfsStart') IS NULL
    ALTER TABLE [__SCHEMA__].[TaxonPage] ADD DfsStart INT NULL;
