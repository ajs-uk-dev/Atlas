using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore.Fixtures;

public class Container<T>
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public T Value { get; set; } = default!;
}

public class ContainerDto<T>
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public T Value { get; set; } = default!;
}

public sealed class ContainerContext : DbContext
{
    public DbSet<Container<string>> StringContainers => Set<Container<string>>();

    public ContainerContext(DbContextOptions<ContainerContext> options) : base(options) { }

    public static ContainerContext CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<ContainerContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var ctx = new ContainerContext(options);
        ctx.Database.OpenConnection();
        ctx.Database.EnsureCreated();
        return ctx;
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Container<string>>(b =>
        {
            b.ToTable("Containers");
            b.HasKey(c => c.Id);
        });
    }

    public void Seed()
    {
        StringContainers.Add(new Container<string> { Id = 1, Label = "first", Value = "alpha" });
        StringContainers.Add(new Container<string> { Id = 2, Label = "second", Value = "beta" });
        SaveChanges();
    }
}
