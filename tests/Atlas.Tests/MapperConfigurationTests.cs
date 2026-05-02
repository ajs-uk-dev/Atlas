using Atlas;
using Atlas.Internal;

namespace Atlas.Tests;

public class MapperConfigurationTests
{
    [Fact]
    public void CompileMappings_RegistersDelegateForEveryTypeMap()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Cfg6Src, Cfg6Dst>();
            cfg.CreateMap<Cfg6Inner, Cfg6InnerDst>();
        });

        config.CompileMappings();

        Assert.True(config.Internal_HasDelegate(new TypePair(typeof(Cfg6Src), typeof(Cfg6Dst))));
        Assert.True(config.Internal_HasDelegate(new TypePair(typeof(Cfg6Inner), typeof(Cfg6InnerDst))));
    }

    [Fact]
    public void CompileMappings_Twice_IsIdempotent()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Cfg6Src, Cfg6Dst>());
        config.CompileMappings();
        config.CompileMappings();   // must not throw or duplicate-compile

        Assert.Equal(1, config.Internal_CompileCountFor(new TypePair(typeof(Cfg6Src), typeof(Cfg6Dst))));
    }

    [Fact]
    public void CreateMapper_ReturnsNonNull()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Cfg6Src, Cfg6Dst>());

        Assert.NotNull(config.CreateMapper());
    }

    [Fact]
    public void CreateMapper_NewInstanceOnSecondCall()
    {
        // v1 documented choice: each call to CreateMapper returns a new IMapper. The mapper is stateless,
        // so this is observationally equivalent to returning the same instance — but new lets the DI
        // container manage lifetime however it prefers.
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Cfg6Src, Cfg6Dst>());

        var a = config.CreateMapper();
        var b = config.CreateMapper();
        Assert.NotSame(a, b);
    }

    [Fact]
    public void Configuration_AfterBuild_IsImmutable_AddingMapThrows()
    {
        var expr = new MapperConfigurationExpression();
        expr.CreateMap<Cfg6Src, Cfg6Dst>();
        _ = new MapperConfiguration(expr);

        Assert.Throws<InvalidOperationException>(() => expr.CreateMap<Cfg6Src, Cfg6Dst>());
    }

    [Fact]
    public void LazyCompilation_FirstMapCall_CompilesOnDemand()
    {
        // No CompileMappings call — the first Map call must compile lazily.
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Cfg6Src, Cfg6Dst>());
        var mapper = config.CreateMapper();
        var pair = new TypePair(typeof(Cfg6Src), typeof(Cfg6Dst));

        Assert.False(config.Internal_HasDelegate(pair));

        var dst = mapper.Map<Cfg6Src, Cfg6Dst>(new Cfg6Src { Id = 7 });

        Assert.Equal(7, dst.Id);
        Assert.True(config.Internal_HasDelegate(pair));
    }

    [Fact]
    public void LazyCompilation_ConcurrentCalls_CompileOnce()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Cfg6Src, Cfg6Dst>());
        var mapper = config.CreateMapper();

        Parallel.For(0, 200, _ => mapper.Map<Cfg6Src, Cfg6Dst>(new Cfg6Src { Id = 1 }));

        var pair = new TypePair(typeof(Cfg6Src), typeof(Cfg6Dst));
        Assert.Equal(1, config.Internal_CompileCountFor(pair));
    }

    [Fact]
    public void ConfigurationProvider_OnMapper_ReturnsOriginalConfig()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Cfg6Src, Cfg6Dst>());
        var mapper = config.CreateMapper();

        Assert.Same(config, mapper.ConfigurationProvider);
    }
}

// ---- Test fixtures ----

public class Cfg6Src { public int Id { get; set; } }
public class Cfg6Dst { public int Id { get; set; } }

public class Cfg6Inner { public string Name { get; set; } = ""; }
public class Cfg6InnerDst { public string Name { get; set; } = ""; }
