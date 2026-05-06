using System;
using System.Collections.Generic;
using System.Linq;
using Atlas;
using Atlas.Projections;

namespace Atlas.Projections.Tests;

public class ProjectionRejectsDynamicMappingTests
{
    [Fact]
    public void ProjectTo_FromDynamicShapeQueryable_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(_ => { });
        var queryable = new List<IDictionary<string, object>>().AsQueryable();
        var ex = Assert.Throws<AtlasProjectionException>(
            () => queryable.ProjectTo<TargetPoco>(cfg).ToList());
        Assert.Contains("dynamic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_NonDynamicMappings_StillWork()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SourcePoco, TargetPoco>());
        var queryable = new List<SourcePoco> { new() { Id = 1 } }.AsQueryable();
        var result = queryable.ProjectTo<TargetPoco>(cfg).ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    private sealed class SourcePoco { public int Id { get; set; } }
    private sealed class TargetPoco { public int Id { get; set; } }
}
