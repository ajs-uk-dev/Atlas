namespace Atlas.Tests;

public class MapperOpenGenericTests
{
    public class Wrapper<T>
    {
        public T Value { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public List<T> History { get; set; } = new();
    }

    public class WrapperDto<T>
    {
        public T Value { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public List<T> History { get; set; } = new();
    }

    public class Customer
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
    }

    public class CustomerDto
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
    }

    public class TwoArg<T1, T2>
    {
        public T1 First { get; set; } = default!;
        public T2 Second { get; set; } = default!;
    }

    public class TwoArgDto<T1, T2>
    {
        public T1 First { get; set; } = default!;
        public T2 Second { get; set; } = default!;
    }

    [Fact]
    public void Map_PrimitiveTypeArg_HeadlineExample()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<WrapperDto<int>>(new Wrapper<int>
        {
            Value = 42,
            CreatedAt = new DateTime(2024, 1, 1),
            History = new List<int> { 1, 2, 3 }
        });

        Assert.Equal(42, dto.Value);
        Assert.Equal(new DateTime(2024, 1, 1), dto.CreatedAt);
        Assert.Equal(new[] { 1, 2, 3 }, dto.History);
    }

    [Fact]
    public void Map_ReferenceTypeArg_NestedMapResolved()
    {
        // Heterogeneous T positions: Wrapper<Customer> source → WrapperDto<CustomerDto> dest.
        // The Customer → CustomerDto registered closed pair handles the nested mapping.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Customer, CustomerDto>();
        });
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<WrapperDto<CustomerDto>>(new Wrapper<Customer>
        {
            Value = new Customer { Name = "Alice", Score = 100 },
            CreatedAt = new DateTime(2024, 6, 1),
            History = new List<Customer> { new() { Name = "Bob", Score = 50 } }
        });

        Assert.Equal("Alice", dto.Value.Name);
        Assert.Equal(100, dto.Value.Score);
        Assert.Single(dto.History);
        Assert.Equal("Bob", dto.History[0].Name);
    }

    [Fact]
    public void Map_HigherArity_TupleStyle()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(TwoArg<,>), typeof(TwoArgDto<,>)));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<TwoArgDto<int, string>>(new TwoArg<int, string>
        {
            First = 7,
            Second = "hello"
        });

        Assert.Equal(7, dto.First);
        Assert.Equal("hello", dto.Second);
    }

    [Fact]
    public void Update_OpenGeneric_AppliesUniformly()
    {
        // Update-in-place calls BuildSourceExpression too, so materialized closed pairs
        // work uniformly for update via the same TypeMap path.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var mapper = cfg.CreateMapper();

        var existing = new WrapperDto<int> { Value = 99 };
        mapper.Map(new Wrapper<int> { Value = 7 }, existing);

        Assert.Equal(7, existing.Value);
    }
}
