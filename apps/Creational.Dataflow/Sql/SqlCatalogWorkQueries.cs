namespace Creational.Dataflow.Sql;

public static class SqlCatalogWorkQueries
{
    // The source thumbnail is the only initial copy shape. Derived shapes require source readiness.
    public static string EligibleSourceRenditionCount(string schema)
    {
        SqlCatalogSchema.ValidateName(schema);
        return $"""
            SELECT COUNT(*)
            FROM [{schema}].[Rendition] r
            JOIN [{schema}].[Image] i ON i.ImageId=r.ImageId
            WHERE r.ShapeId=1 AND (r.Complete=0 OR r.Stale=1)
              AND i.MetadataStatus IN ('commons','enwiki-local')
              AND i.Priority>=0;
            """;
    }
}
