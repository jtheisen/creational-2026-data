IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Image]')
               AND name=N'IX_Image_Priority')
    CREATE INDEX IX_Image_Priority ON [__SCHEMA__].[Image](Priority DESC,ImageId);
