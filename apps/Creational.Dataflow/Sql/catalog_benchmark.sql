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
SELECT COUNT_BIG(*) AS TitledReferencesWithoutKnownSource FROM catalog.PageImage
WHERE SourceWiki IS NULL;
SELECT SUM(CAST(UnresolvedImageCount AS BIGINT)) AS ReferencesWithoutFileTitle FROM catalog.Page;

DECLARE @treeId SMALLINT=1, @collectionId SMALLINT=1, @ottId INT=44565; -- Carnivora
WITH root AS (
    SELECT DfsStart,DfsEndExclusive FROM catalog.Taxon
    WHERE TreeId=@treeId AND OttId=@ottId
), subtree_pages AS (
    SELECT DISTINCT p.PageId
    FROM root r
    JOIN catalog.Taxon t ON t.TreeId=@treeId
        AND t.DfsStart>=r.DfsStart AND t.DfsStart<r.DfsEndExclusive
    JOIN catalog.TaxonPage p ON p.TreeId=t.TreeId AND p.OttId=t.OttId
        AND p.CollectionId=@collectionId
), subtree_images AS (
    SELECT DISTINCT pi.ImageId
    FROM subtree_pages p
    JOIN catalog.PageImage pi ON pi.CollectionId=@collectionId AND pi.PageId=p.PageId
    WHERE pi.ImageId IS NOT NULL
)
SELECT (SELECT COUNT_BIG(*) FROM subtree_pages) AS Pages,
       COUNT_BIG(*) AS DistinctImages,
       SUM(i.OriginalBytes) AS TotalOriginalBytes
FROM subtree_images x JOIN catalog.Image i ON i.ImageId=x.ImageId;

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;
