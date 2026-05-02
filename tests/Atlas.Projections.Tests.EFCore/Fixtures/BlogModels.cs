namespace Atlas.Projections.Tests.EFCore.Fixtures;

public class Blog
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public List<Post> Posts { get; set; } = new();
}

public class Post
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
    public int? WordCount { get; set; }
    public int BlogId { get; set; }
    public Blog? Blog { get; set; }
}

public class BlogDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public List<PostDto> Posts { get; set; } = new();
}

public class PostDto
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
    public long? WordCount { get; set; } // numeric widening: int? -> long?
}
