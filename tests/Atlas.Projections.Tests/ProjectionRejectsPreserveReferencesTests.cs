using System;
using System.Collections.Generic;
using System.Linq;
using Atlas;
using Atlas.Projections;

namespace Atlas.Projections.Tests;

public class ProjectionRejectsPreserveReferencesTests
{
    [Fact]
    public void ProjectTo_PreserveReferencesTypeMap_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Source, Target>().PreserveReferences());
        var queryable = new List<Source> { new() { Id = 1 } }.AsQueryable();

        var ex = Assert.Throws<AtlasProjectionException>(
            () => queryable.ProjectTo<Target>(cfg).ToList());

        Assert.True(
            ex.Message.Contains("PreserveReferences", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("identity tracking", StringComparison.OrdinalIgnoreCase),
            $"Expected message to mention PreserveReferences or identity tracking; got: {ex.Message}");
    }

    [Fact]
    public void ProjectTo_NonPreserveReferencesMap_StillWorks()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Source, Target>());   // NOT flagged
        var queryable = new List<Source> { new() { Id = 1 } }.AsQueryable();

        var result = queryable.ProjectTo<Target>(cfg).ToList();

        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    private sealed class Source { public int Id { get; set; } }
    private sealed class Target { public int Id { get; set; } }
}
