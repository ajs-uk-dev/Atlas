using System.Linq.Expressions;
using Atlas;
using Atlas.Configuration;
using Atlas.Internal;
using Atlas.Projections;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }

    private static MapperRegistry BuildRegistry(Action<MapperConfigurationExpression> configure)
    {
        var cfg = new MapperConfiguration(configure);
        return cfg.Internal_Registry;
    }

    [Fact]
    public void Projection_OpenGenericTemplate_ProducesCorrectMemberInit()
    {
        var registry = BuildRegistry(c => c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));

        var lambda = ProjectionPlanBuilder.Build(
            registry,
            new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)),
            maxDepth: 5);

        // Build returns a valid lambda; body should be a MemberInit on WrapperDto<int>.
        Assert.NotNull(lambda);
        var memberInit = Assert.IsType<MemberInitExpression>(lambda.Body);
        Assert.Equal(typeof(WrapperDto<int>), memberInit.Type);
        Assert.Single(memberInit.Bindings.OfType<MemberAssignment>(), b => b.Member.Name == "Value");
    }

    [Fact]
    public void Projection_ClosedPairTakesPrecedence()
    {
        // Register both an open template AND a specific closed pair with a custom
        // MapFrom — projection should use the closed pair, not the template.
        var registry = BuildRegistry(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Wrapper<int>, WrapperDto<int>>(MemberList.None)
                .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value * 2));
        });

        var lambda = ProjectionPlanBuilder.Build(
            registry,
            new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)),
            maxDepth: 5);

        // The Value binding should reflect the closed-pair's MapFrom (s.Value * 2),
        // visible as a Multiply node in the binding's expression tree.
        var memberInit = (MemberInitExpression)lambda.Body;
        var valueBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == "Value");

        Assert.True(ContainsMultiply(valueBinding.Expression));
    }

    private static bool ContainsMultiply(Expression node)
    {
        var visitor = new MultiplyFinder();
        visitor.Visit(node);
        return visitor.Found;
    }

    private sealed class MultiplyFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Multiply) Found = true;
            return base.VisitBinary(node);
        }
    }
}
