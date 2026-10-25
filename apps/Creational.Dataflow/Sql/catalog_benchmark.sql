-- Run after --load-sql-catalog completes. Replace catalog if a different schema was selected.
-- Run the subtree query twice and record elapsed time and logical reads from the Messages pane.
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

SELECT t.name AS TableName,
       SUM(CASE WHEN p.index_id IN (0,1) THEN p.row_count ELSE 0 END) AS Rows,
       CAST(SUM(p.reserved_page_count) * 8.0 / 1024 AS DECIMAL(12,2)) AS ReservedMb
FROM sys.dm_db_partition_stats p
JOIN sys.tables t ON t.object_id=p.object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name=N'catalog'
GROUP BY t.name
ORDER BY ReservedMb DESC;

SELECT name,
       CAST(size * 8.0 / 1024 AS DECIMAL(12,2)) AS AllocatedMb,
       CAST(FILEPROPERTY(name,'SpaceUsed') * 8.0 / 1024 AS DECIMAL(12,2)) AS UsedMb
FROM sys.database_files;

SELECT LoadKey,SourceFingerprint,NextOrdinal,Completed,UpdatedAt
FROM catalog.CatalogLoadCheckpoint ORDER BY LoadKey;

SELECT COUNT_BIG(*) AS DuplicateImageKeys FROM (
    SELECT SourceWiki,CanonicalFileTitle FROM catalog.Image
    GROUP BY SourceWiki,CanonicalFileTitle HAVING COUNT_BIG(*)>1
) d;
SELECT COUNT_BIG(*) AS UnlinkedResolvedReferences FROM catalog.PageImage
WHERE SourceWiki IS NOT NULL AND ImageId IS NULL;
SELECT COUNT_BIG(*) AS MissingDfsPositions FROM catalog.TaxonPage WHERE DfsStart IS NULL;
SELECT COUNT_BIG(*) AS IncorrectDfsPositions
FROM catalog.TaxonPage p JOIN catalog.Taxon t ON t.TreeId=p.TreeId AND t.OttId=p.OttId
WHERE p.DfsStart<>t.DfsStart;
SELECT COUNT_BIG(*) AS TitledReferencesWithoutKnownSource FROM catalog.PageImage
WHERE SourceWiki IS NULL;
SELECT SUM(CAST(UnresolvedImageCount AS BIGINT)) AS ReferencesWithoutFileTitle FROM catalog.Page;

DECLARE @treeId SMALLINT=1, @collectionId SMALLINT=1, @ottId INT=44565; -- Carnivora
DECLARE @start INT, @end INT;
SELECT @start=DfsStart,@end=DfsEndExclusive FROM catalog.Taxon
WHERE TreeId=@treeId AND OttId=@ottId;
IF @start IS NULL THROW 50001, 'OTT taxon does not exist in the selected tree', 1;

CREATE TABLE #SubtreePages(PageId BIGINT NOT NULL PRIMARY KEY CLUSTERED);
INSERT INTO #SubtreePages(PageId)
SELECT DISTINCT p.PageId FROM catalog.TaxonPage p WITH (INDEX(IX_TaxonPage_DfsStart))
WHERE p.TreeId=@treeId AND p.CollectionId=@collectionId
  AND p.DfsStart>=@start AND p.DfsStart<@end OPTION (RECOMPILE);

CREATE TABLE #SubtreeImages(ImageId INT NOT NULL PRIMARY KEY CLUSTERED);
INSERT INTO #SubtreeImages(ImageId)
SELECT DISTINCT pi.ImageId
FROM #SubtreePages p JOIN catalog.PageImage pi
    ON pi.CollectionId=@collectionId AND pi.PageId=p.PageId
WHERE pi.ImageId IS NOT NULL OPTION (RECOMPILE);

SELECT (SELECT COUNT_BIG(*) FROM #SubtreePages) AS Pages,
       COUNT_BIG(*) AS DistinctImages,
       SUM(i.OriginalBytes) AS TotalOriginalBytes
FROM #SubtreeImages x JOIN catalog.Image i ON i.ImageId=x.ImageId;
DROP TABLE #SubtreeImages;
DROP TABLE #SubtreePages;

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;
