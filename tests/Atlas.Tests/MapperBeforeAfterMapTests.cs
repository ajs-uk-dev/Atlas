using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests;

public class MapperBeforeAfterMapTests
{
    public class S { public int X { get; set; } }
    public class D { public int X { get; set; } }

    public class Animal { public string? Name { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public string? Name { get; set; } }
    public class DogDto : AnimalDto { }

    [Fact]
    public void BeforeMap_Lambda_FiresOncePerMap()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add("before")));
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());

        Assert.Single(trace);
    }

    [Fact]
    public void BeforeMap_FiresPerElement_OnCollectionMapping()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add($"item {s.X}"));
            c.CreateMap<List<S>, List<D>>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var srcs = new List<S> { new() { X = 1 }, new() { X = 2 }, new() { X = 3 } };
        mapper.Map<List<S>, List<D>>(srcs);

        Assert.Equal(3, trace.Count);
        Assert.Equal("item 1", trace[0]);
        Assert.Equal("item 2", trace[1]);
        Assert.Equal("item 3", trace[2]);
    }

    [Fact]
    public void MultipleHooks_FireInFifoOrder()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add("before-1"))
                .BeforeMap((s, d) => trace.Add("before-2"))
                .BeforeMap((s, d) => trace.Add("before-3"))
                .AfterMap((s, d) => trace.Add("after-1"))
                .AfterMap((s, d) => trace.Add("after-2")));
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());

        Assert.Equal(new[] { "before-1", "before-2", "before-3", "after-1", "after-2" }, trace);
    }

    [Fact]
    public void Inheritance_HookOrder_MatchesStackUnwind()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Animal, AnimalDto>(MemberList.None)
                .Include<Dog, DogDto>()
                .BeforeMap((s, d) => trace.Add("animal-before"))
                .AfterMap((s, d) => trace.Add("animal-after"));
            c.CreateMap<Dog, DogDto>(MemberList.None)
                .IncludeBase<Animal, AnimalDto>()
                .BeforeMap((s, d) => trace.Add("dog-before"))
                .AfterMap((s, d) => trace.Add("dog-after"));
        });
        var mapper = cfg.CreateMapper();

        mapper.Map<DogDto>(new Dog { Name = "Rex" });

        // Expected: animal-before → dog-before → [property mapping] → dog-after → animal-after.
        Assert.Equal(new[] { "animal-before", "dog-before", "dog-after", "animal-after" }, trace);
    }

    public sealed class Counter
    {
        public int Value;
    }

    public sealed class IncAction : IMappingAction<S, D>
    {
        private readonly Counter _counter;
        public IncAction(Counter counter) => _counter = counter;
        public void Process(S source, D destination) => _counter.Value++;
    }

    [Fact]
    public void IMappingAction_DI_ResolvesAndCallsProcess()
    {
        var counter = new Counter();
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        var sp = services.BuildServiceProvider();

        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap<IncAction>(),
            sp);
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());
        mapper.Map<D>(new S());
        mapper.Map<D>(new S());

        Assert.Equal(3, counter.Value);
    }

    [Fact]
    public void Hook_ExceptionPropagates()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => throw new InvalidOperationException("boom")));
        var mapper = cfg.CreateMapper();

        var ex = Assert.Throws<InvalidOperationException>(() => mapper.Map<S, D>(new S()));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void UpdateInPlace_FiresHooks()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add($"before existing.X={d.X}"))
                .AfterMap((s, d) => trace.Add($"after dest.X={d.X}")));
        var mapper = cfg.CreateMapper();

        var existing = new D { X = 99 };
        mapper.Map<S, D>(new S { X = 7 }, existing);

        Assert.Equal(new[] { "before existing.X=99", "after dest.X=7" }, trace);
    }
}
