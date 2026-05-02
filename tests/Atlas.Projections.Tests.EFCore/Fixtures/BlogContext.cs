using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore.Fixtures;

public sealed class BlogContext : DbContext
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();

    public BlogContext(DbContextOptions<BlogContext> options) : base(options) { }

    public static BlogContext CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<BlogContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var ctx = new BlogContext(options);
        ctx.Database.OpenConnection();
        ctx.Database.EnsureCreated();
        return ctx;
    }

    public void Seed()
    {
        var b = new Blog
        {
            Title = "T1",
            Posts =
            {
                new Post { Body = "p1", WordCount = 100 },
                new Post { Body = "p2", WordCount = null },
            },
        };
        Blogs.Add(b);
        SaveChanges();
    }
}
