using System.Linq.Expressions;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanCacheTests
{
    private static LambdaExpression DummyLambda() => Expression.Lambda(Expression.Constant(0));

    [Fact]
    public void GetOrBuild_FirstCall_InvokesBuilder()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetOrBuild_SecondCallSameKey_ReturnsCachedAndDoesNotInvokeBuilder()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        var first  = cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        var second = cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetOrBuild_DifferentMaxDepth_BuildsSeparately()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        cache.GetOrBuild(pair, 5, () => { calls++; return DummyLambda(); });
        Assert.Equal(2, calls);
    }

    [Fact]
    public void GetOrBuild_ConcurrentCalls_BuildsOnce()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        Parallel.For(0, 200, _ => cache.GetOrBuild(pair, 3, () =>
        {
            Interlocked.Increment(ref calls);
            return DummyLambda();
        }));
        Assert.Equal(1, calls);
    }
}
