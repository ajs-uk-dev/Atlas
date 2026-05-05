using System.Linq.Expressions;
using Atlas;
using Atlas.Configuration;
using Atlas.Internal;
using Atlas.Projections;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderNullSubstituteTests
{
    public struct S { public string? Name { get; set; } public int? Score { get; set; } }
    public class D { public string Name { get; set; } = ""; public int Score { get; set; } }
    public struct SLong { public int? Score { get; set; } }
    public class DLong { public long? Score { get; set; } }

    private static MapperRegistry BuildRegistry(Action<MapperConfigurationExpression> configure)
    {
        var cfg = new MapperConfiguration(configure);
        return cfg.Internal_Registry;
    }

    [Fact]
    public void Projection_BindingContainsCoalesce_WhenSubstituteSet()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var nameBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.Name));

        // The binding must contain a Coalesce node somewhere.
        Assert.True(AssertExpression.Contains<BinaryExpression>(nameBinding.Expression));
        // More precise: top-level node should be Coalesce (or wrap a Convert containing one).
        var coalesce = FindCoalesce(nameBinding.Expression);
        Assert.NotNull(coalesce);
    }

    [Fact]
    public void Projection_BindingHasNoCoalesce_WhenSubstituteUnset()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name)));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var nameBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.Name));

        // No Coalesce node should appear when no substitute is configured.
        Assert.Null(FindCoalesce(nameBinding.Expression));
    }

    [Fact]
    public void Projection_NullableSource_To_NullableWiderDestination_GeneratesCorrectExpression()
    {
        // Regression: previously the lifted-nullable branch in ApplyProjectionNullSubstitute
        // produced an int-typed Coalesce that ConvertOrInline couldn't widen to long?.
        var registry = BuildRegistry(c =>
            c.CreateMap<SLong, DLong>(MemberList.None)
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(0);
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(SLong), typeof(DLong)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var scoreBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(DLong.Score));

        // Binding type must be long? (the destination type) — would have failed compilation
        // before the fix because ConvertOrInline couldn't widen int (post-Coalesce) to long?.
        Assert.Equal(typeof(long?), scoreBinding.Expression.Type);
        // Binding must contain a Coalesce node.
        Assert.NotNull(FindCoalesce(scoreBinding.Expression));
    }

    private static BinaryExpression? FindCoalesce(Expression node)
    {
        var visitor = new CoalesceFinder();
        visitor.Visit(node);
        return visitor.Found;
    }

    private sealed class CoalesceFinder : ExpressionVisitor
    {
        public BinaryExpression? Found { get; private set; }
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Coalesce && Found is null)
                Found = node;
            return base.VisitBinary(node);
        }
    }
}
