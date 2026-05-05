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
