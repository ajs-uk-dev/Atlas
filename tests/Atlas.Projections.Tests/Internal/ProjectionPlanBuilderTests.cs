using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderTests
{
    private static (MapperRegistry registry, LambdaExpression lambda) Build<TSource, TDestination>(
        Action<MapperConfigurationExpression> configure,
        int maxDepth = 3)
    {
        var config = new MapperConfiguration(configure);
        var registry = config.Internal_Registry;
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(TSource), typeof(TDestination)), maxDepth);
        return (registry, lambda);
    }

    [Fact]
    public void Build_FlatPair_EmitsMemberInitWithBindings()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c => c.CreateMap<BFlatSrc, BFlatDst>());
        Assert.True(AssertExpression.Contains<MemberInitExpression>(lambda.Body));
    }

    [Fact]
    public void Build_FlatPair_DoesNotContainMappingInvokerCall()
    {
        // Load-bearing safety check: projection lambdas must never delegate to the v1 runtime invoker.
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c => c.CreateMap<BFlatSrc, BFlatDst>());
        Assert.False(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "Invoke"));
        Assert.False(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "InvokeToList"));
        Assert.False(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "InvokeToArray"));
    }

    [Fact]
    public void Build_NestedObject_InlinesNestedMemberInit()
    {
        var (_, lambda) = Build<BOuterSrc, BOuterDst>(c =>
        {
            c.CreateMap<BInnerSrc, BInnerDst>();
            c.CreateMap<BOuterSrc, BOuterDst>();
        });
        // Two MemberInit nodes: outer + inner.
        Assert.Equal(2, AssertExpression.CountNodes<MemberInitExpression>(lambda.Body));
    }

    [Fact]
    public void Build_NestedClassMember_WrapsInNullSafeConditional()
    {
        var (_, lambda) = Build<BOuterSrc, BOuterDst>(c =>
        {
            c.CreateMap<BInnerSrc, BInnerDst>();
            c.CreateMap<BOuterSrc, BOuterDst>();
        });
        Assert.True(AssertExpression.Contains<ConditionalExpression>(lambda.Body));
    }

    [Fact]
    public void Build_NumericWidening_EmitsConvert()
    {
        var (_, lambda) = Build<BIntSrc, BLongDst>(c => c.CreateMap<BIntSrc, BLongDst>());
        Assert.True(AssertExpression.Contains<UnaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_ConstantBinding_EmitsConstantNode()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c =>
            c.CreateMap<BFlatSrc, BFlatDst>().ForMember(d => d.Name, o => o.MapFrom("k")));
        Assert.True(AssertExpression.Contains<ConstantExpression>(lambda.Body));
    }

    [Fact]
    public void Build_CustomExpression_RebindsParameter()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c =>
            c.CreateMap<BFlatSrc, BFlatDst>()
                .ForMember(d => d.Name, o => o.MapFrom(s => s.Name + "!")));
        // Compile + invoke proves the parameter was rebound to the outer src parameter.
        var fn = (Func<BFlatSrc, BFlatDst>)lambda.Compile();
        var dst = fn(new BFlatSrc { Id = 1, Name = "x" });
        Assert.Equal("x!", dst.Name);
    }

    [Fact]
    public void Build_CollectionMember_EmitsSelectOverElementProjection()
    {
        var (_, lambda) = Build<BParentSrc, BParentDst>(c =>
        {
            c.CreateMap<BInnerSrc, BInnerDst>();
            c.CreateMap<BParentSrc, BParentDst>();
        });
        Assert.True(AssertExpression.ContainsCallTo(lambda.Body, "Enumerable", "Select"));
    }

    [Fact]
    public void Build_DepthLimit_RecursiveMember_EmitsDefault()
    {
        var (_, lambda) = Build<BNode, BNode>(c => c.CreateMap<BNode, BNode>(MemberList.None), maxDepth: 1);
        // After one level, the recursive Next member becomes default(BNode) — i.e., null literal.
        // The lambda compiles and executing it yields a single-level clone whose Next is null.
        var fn = (Func<BNode, BNode>)lambda.Compile();
        var src = new BNode { Next = new BNode { Next = new BNode() } };
        var dst = fn(src);
        Assert.NotNull(dst);
        Assert.Null(dst.Next);
    }

    [Fact]
    public void Build_IgnoredMember_OmitsBinding()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c =>
            c.CreateMap<BFlatSrc, BFlatDst>().ForMember(d => d.Name, o => o.Ignore()));
        var fn = (Func<BFlatSrc, BFlatDst>)lambda.Compile();
        var dst = fn(new BFlatSrc { Id = 7, Name = "x" });
        Assert.Equal(7, dst.Id);
        Assert.Equal("", dst.Name); // never bound; default of "" from field initializer
    }

    [Fact]
    public void Build_RecordCtor_UsesNewExpressionWithCtorArgs()
    {
        var (_, lambda) = Build<BRecSrc, BRecDst>(c => c.CreateMap<BRecSrc, BRecDst>(MemberList.None));
        Assert.True(AssertExpression.Contains<NewExpression>(lambda.Body));
    }

    [Fact]
    public void Build_LambdaParameterCount_IsExactlyOne()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c => c.CreateMap<BFlatSrc, BFlatDst>());
        Assert.Single(lambda.Parameters);
        Assert.Equal(typeof(BFlatSrc), lambda.Parameters[0].Type);
    }
}

// ---- Test fixtures ----
public class BFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class BFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class BInnerSrc { public string Name { get; set; } = ""; }
public class BInnerDst { public string Name { get; set; } = ""; }
public class BOuterSrc { public BInnerSrc Inner { get; set; } = new(); }
public class BOuterDst { public BInnerDst Inner { get; set; } = new(); }
public class BParentSrc { public List<BInnerSrc> Items { get; set; } = new(); }
public class BParentDst { public List<BInnerDst> Items { get; set; } = new(); }
public class BIntSrc { public int Count { get; set; } }
public class BLongDst { public long Count { get; set; } }
public class BNode { public BNode? Next { get; set; } }
public class BRecSrc { public string DisplayName { get; set; } = ""; }
public record BRecDst(string DisplayName);
