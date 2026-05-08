using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceCacheTests
{
    private static readonly Expression<Func<UEDS_CacheDto, bool>> _stableLambda =
        d => d.Total > 100m;

    [Fact]
    public void SameLambdaReference_ReturnsCachedResult()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var cache = TranslationPlanCacheRegistry.For(cfg);
        var pair = new TypePair(typeof(UEDS_CacheSrc), typeof(UEDS_CacheDto));

        int factoryCalls = 0;
        LambdaExpression Factory()
        {
            factoryCalls++;
            return ExpressionTranslator.Translate(cfg.Internal_Registry, pair, _stableLambda);
        }

        var first = cache.GetOrTranslate(pair, _stableLambda, Factory);
        var second = cache.GetOrTranslate(pair, _stableLambda, Factory);

        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void DistinctLambdaInstances_TranslateIndependently()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var cache = TranslationPlanCacheRegistry.For(cfg);
        var pair = new TypePair(typeof(UEDS_CacheSrc), typeof(UEDS_CacheDto));

        int factoryCalls = 0;
        LambdaExpression Factory(LambdaExpression src) =>
            ExpressionTranslator.Translate(cfg.Internal_Registry, pair, src);

        // Two separate lambda instances with identical structure.
        Expression<Func<UEDS_CacheDto, bool>> first = d => d.Total > 100m;
        Expression<Func<UEDS_CacheDto, bool>> second = d => d.Total > 100m;
        Assert.NotSame(first, second);

        cache.GetOrTranslate(pair, first, () => { factoryCalls++; return Factory(first); });
        cache.GetOrTranslate(pair, second, () => { factoryCalls++; return Factory(second); });

        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void DifferentMapperConfigurations_HaveSeparateCaches()
    {
        var cfgA = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var cfgB = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());

        var cacheA = TranslationPlanCacheRegistry.For(cfgA);
        var cacheB = TranslationPlanCacheRegistry.For(cfgB);

        Assert.NotSame(cacheA, cacheB);
    }

    [Fact]
    public void SameMapperConfiguration_ReturnsSameCacheInstance()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());

        var first = TranslationPlanCacheRegistry.For(cfg);
        var second = TranslationPlanCacheRegistry.For(cfg);

        Assert.Same(first, second);
    }
}

public class UEDS_CacheSrc { public decimal Total { get; set; } }
public class UEDS_CacheDto { public decimal Total { get; set; } }

public class TranslateExtensionTests
{
    [Fact]
    public void Translate_ReturnsStronglyTypedExpression()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());

        Expression<Func<UEDS_CacheDto, bool>> destExpr = d => d.Total > 100m;
        Expression<Func<UEDS_CacheSrc, bool>> srcExpr =
            cfg.Translate<UEDS_CacheSrc, UEDS_CacheDto, bool>(destExpr);

        var compiled = srcExpr.Compile();
        Assert.True(compiled(new UEDS_CacheSrc { Total = 150m }));
        Assert.False(compiled(new UEDS_CacheSrc { Total = 50m }));
    }

    [Fact]
    public void UseAsDataSource_ReturnsIntermediate_ForReturnsWrapper()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var queryable = new[] { new UEDS_CacheSrc { Total = 150m } }.AsQueryable();

        var intermediate = queryable.UseAsDataSource(cfg);
        Assert.NotNull(intermediate);

        var wrapper = intermediate.For<UEDS_CacheDto>();
        Assert.NotNull(wrapper);
    }
}
