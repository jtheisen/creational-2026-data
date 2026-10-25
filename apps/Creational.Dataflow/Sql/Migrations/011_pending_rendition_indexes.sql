IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Rendition]')
               AND name=N'IX_Rendition_Incomplete')
    CREATE INDEX IX_Rendition_Incomplete ON [__SCHEMA__].[Rendition](ShapeId,ImageId)
        WHERE Complete=0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[__SCHEMA__].[Rendition]')
               AND name=N'IX_Rendition_Stale')
    CREATE INDEX IX_Rendition_Stale ON [__SCHEMA__].[Rendition](ShapeId,ImageId)
        WHERE Stale=1;
