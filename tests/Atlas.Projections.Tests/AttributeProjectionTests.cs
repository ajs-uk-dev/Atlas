using Atlas;
using Atlas.Projections;

namespace Atlas.Projections.Tests;

public class AttributeProjectionTests
{
    private static MapperConfiguration BuildConfig(Type fixtureType)
    {
        return new MapperConfiguration(c =>
        {
            try { c.AddMaps(fixtureType.Assembly); }
            catch (AtlasConfigurationException) { /* tolerate sibling-fixture pollution */ }
        });
    }

    [Fact]
    public void AttributeMap_ProjectsViaProjectTo()
    {
        var cfg = BuildConfig(typeof(ProjectionAttrDto));
        var src = new[]
        {
            new ProjectionAttrSource { Id = 1, Name = "A" },
            new ProjectionAttrSource { Id = 2, Name = "B" },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionAttrDto>(cfg).ToArray();
        Assert.Equal(2, result.Length);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("A", result[0].Name);
    }

    [Fact]
    public void SkipAttribute_ExcludesMemberFromProjection()
    {
        var cfg = BuildConfig(typeof(ProjectionIgnoreDto));
        var src = new[]
        {
            new ProjectionIgnoreSource { Id = 1, Skipped = "should-not-appear" },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionIgnoreDto>(cfg).Single();
        Assert.Equal(1, result.Id);
        Assert.Null(result.Skipped);
    }

    [Fact]
    public void From_DottedPath_ProjectsAsNavigation()
    {
        var cfg = BuildConfig(typeof(ProjectionDottedDto));
        var src = new[]
        {
            new ProjectionDottedSource { Id = 1, Customer = new ProjectionDottedCustomer { Name = "X" } },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionDottedDto>(cfg).Single();
        Assert.Equal("X", result.CustomerName);
    }

    [Fact]
    public void DefaultWhenNull_TranslatesToCoalesce()
    {
        var cfg = BuildConfig(typeof(ProjectionNullSubDto));
        var src = new[]
        {
            new ProjectionNullSubSource { Id = 1, MaybeName = null },
            new ProjectionNullSubSource { Id = 2, MaybeName = "set" },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionNullSubDto>(cfg).ToArray();
        Assert.Equal("(none)", result[0].Name);
        Assert.Equal("set", result[1].Name);
    }

    [Fact]
    public void PreserveReferencesAttribute_RejectedAtProjectionBuild()
    {
        var cfg = BuildConfig(typeof(ProjectionPreserveDto));
        var src = Array.Empty<ProjectionPreserveSource>();
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            src.AsQueryable().ProjectTo<ProjectionPreserveDto>(cfg).ToArray());
        Assert.True(
            ex.Message.Contains("PreserveReferences", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("identity tracking", StringComparison.OrdinalIgnoreCase),
            $"Expected message to mention PreserveReferences or identity tracking; got: {ex.Message}");
    }
}

public class ProjectionAttrSource { public int Id { get; set; } public string Name { get; set; } = ""; }
[Map(typeof(ProjectionAttrSource))]
public class ProjectionAttrDto { public int Id { get; set; } public string Name { get; set; } = ""; }

public class ProjectionIgnoreSource { public int Id { get; set; } public string? Skipped { get; set; } }
[Map(typeof(ProjectionIgnoreSource))]
public class ProjectionIgnoreDto { public int Id { get; set; } [Skip] public string? Skipped { get; set; } }

public class ProjectionDottedCustomer { public string Name { get; set; } = ""; }
public class ProjectionDottedSource { public int Id { get; set; } public ProjectionDottedCustomer Customer { get; set; } = new(); }
[Map(typeof(ProjectionDottedSource))]
public class ProjectionDottedDto
{
    public int Id { get; set; }
    [From("Customer.Name")] public string CustomerName { get; set; } = "";
}

public class ProjectionNullSubSource { public int Id { get; set; } public string? MaybeName { get; set; } }
[Map(typeof(ProjectionNullSubSource))]
public class ProjectionNullSubDto
{
    public int Id { get; set; }
    [From(nameof(ProjectionNullSubSource.MaybeName))]
    [DefaultWhenNull("(none)")]
    public string Name { get; set; } = "";
}

public class ProjectionPreserveSource { public int Id { get; set; } }
[Map(typeof(ProjectionPreserveSource), PreserveReferences = true)]
public class ProjectionPreserveDto { public int Id { get; set; } }
