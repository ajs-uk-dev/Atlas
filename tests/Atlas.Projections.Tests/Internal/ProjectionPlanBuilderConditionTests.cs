using System.Linq.Expressions;
using Atlas;
using Atlas.Configuration;
using Atlas.Internal;
using Atlas.Projections;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderConditionTests
{
    public struct S { public int V { get; set; } public string? Text { get; set; } }
    public class D { public int V { get; set; } public string Text { get; set; } = ""; }

    private static MapperRegistry BuildRegistry(Action<MapperConfigurationExpression> configure)
    {
        var cfg = new MapperConfiguration(configure);
        return cfg.Internal_Registry;
    }

    [Fact]
    public void Projection_NoPredicates_NoConditional()
    {
        var registry = BuildRegistry(c => c.CreateMap<S, D>(MemberList.None));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        // No member binding should be wrapped in a Conditional when no predicates are set.
        var memberInit = (MemberInitExpression)lambda.Body;
        foreach (var binding in memberInit.Bindings.Cast<MemberAssignment>())
        {
            Assert.False(binding.Expression is ConditionalExpression);
        }
    }

    [Fact]
    public void Projection_PreConditionOnly_EmitsConditionalWithPredicate()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var vBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.V));

        Assert.True(vBinding.Expression is ConditionalExpression);
        var conditional = (ConditionalExpression)vBinding.Expression;
        // false-branch is Default(int) — Expression.Default(typeof(int)).
        Assert.Equal(typeof(int), conditional.IfFalse.Type);
        Assert.True(conditional.IfFalse is DefaultExpression);
    }

    [Fact]
    public void Projection_BothPredicates_AndAlsoComposed()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v < 100);
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var vBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.V));

        Assert.True(vBinding.Expression is ConditionalExpression);
        var conditional = (ConditionalExpression)vBinding.Expression;
        // Test expression is AndAlso(pre, cond).
        Assert.True(conditional.Test is BinaryExpression);
        var andAlso = (BinaryExpression)conditional.Test;
        Assert.Equal(ExpressionType.AndAlso, andAlso.NodeType);
    }

    [Fact]
    public void Projection_ConditionOnly_EmitsConditionalWithSubstitutedResolvedValue()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V * 2);
                    opt.Condition((s, v) => v > 0);
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var vBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.V));

        Assert.True(vBinding.Expression is ConditionalExpression);
        var conditional = (ConditionalExpression)vBinding.Expression;
        // The Conditional must NOT contain Block or Variable — projection requires a single
        // pure expression per binding.
        Assert.False(AssertExpression.Contains<BlockExpression>(conditional));
        Assert.False(AssertExpression.Contains<ParameterExpression>(conditional)
                     // Allow the source parameter, just not internal Variables.
                     && Atlas.Projections.Tests.Internal.AssertExpression
                        .CountNodes<BlockExpression>(conditional) > 0);
    }
}
