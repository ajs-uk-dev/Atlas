using Atlas;
using Atlas.Configuration;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectTo_OpenGenericTests
{
    [Fact]
    public void ProjectTo_OpenGeneric_GeneratesValidSql()
    {
        var config = new MapperConfiguration(c =>
            c.CreateMap(typeof(Container<>), typeof(ContainerDto<>)));
        using var ctx = ContainerContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.StringContainers.ProjectTo<ContainerDto<string>>(config).ToQueryString();

        // Should generate a SELECT against the Containers table.
        Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Containers", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_OpenGeneric_RowsRoundtrip()
    {
        var config = new MapperConfiguration(c =>
            c.CreateMap(typeof(Container<>), typeof(ContainerDto<>)));
        using var ctx = ContainerContext.CreateInMemory();
        ctx.Seed();

        var dtos = ctx.StringContainers.OrderBy(c => c.Id).ProjectTo<ContainerDto<string>>(config).ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Equal(1, dtos[0].Id);
        Assert.Equal("first", dtos[0].Label);
        Assert.Equal("alpha", dtos[0].Value);
        Assert.Equal(2, dtos[1].Id);
        Assert.Equal("second", dtos[1].Label);
        Assert.Equal("beta", dtos[1].Value);
    }
}
