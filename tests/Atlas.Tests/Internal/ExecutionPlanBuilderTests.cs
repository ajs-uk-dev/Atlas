using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class ExecutionPlanBuilderTests
{
    private static MapperRegistry RegistryFor(params TypeMap[] maps) => new(maps);

    private static ConventionOptions DefaultOpts() =>
        new(NamingConvention.PascalCase, NamingConvention.PascalCase, true);

    [Fact]
    public void FlatPoco_SingleProperty_ProducesAssignment()
    {
        var typeMap = new TypeMap(typeof(FpSrc), typeof(FpDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(typeMap, DefaultOpts());

        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap));
        var del = (Func<FpSrc, FpDst>)lambda.Compile();

        var dst = del(new FpSrc { Id = 42 });
        Assert.Equal(42, dst.Id);
    }

    [Fact]
    public void FlatPoco_NullSource_LambdaReturnsDefault()
    {
        var typeMap = new TypeMap(typeof(FpSrc), typeof(FpDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(typeMap, DefaultOpts());

        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap));
        var del = (Func<FpSrc?, FpDst?>)lambda.Compile();

        Assert.Null(del(null));
    }

    [Fact]
    public void Flattened_TwoLevels_LambdaContainsNullCheck()
    {
        var typeMap = new TypeMap(typeof(FlatSrc), typeof(FlatDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(typeMap, DefaultOpts());

        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap));
        var del = (Func<FlatSrc, FlatDst>)lambda.Compile();

        // Behavioural: walking through a null intermediate must not throw.
        var dst = del(new FlatSrc { Customer = null! });
        Assert.NotNull(dst);
        Assert.Null(dst.CustomerName);

        var dst2 = del(new FlatSrc { Customer = new FlatCustomer { Name = "Ada" } });
        Assert.Equal("Ada", dst2.CustomerName);
    }

    [Fact]
    public void Constant_MapFrom_ProducesConstantExpression()
    {
        var typeMap = new TypeMap(typeof(FpSrc), typeof(FpDst), MemberList.Destination);
        var pm = PropertyMap.ForProperty(typeof(FpDst).GetProperty(nameof(FpDst.Id))!);
        pm.HasConstant = true;
        pm.ConstantValue = 99;
        typeMap.PropertyMaps.Add(pm);

        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap));
        var del = (Func<FpSrc, FpDst>)lambda.Compile();

        Assert.Equal(99, del(new FpSrc { Id = 1 }).Id);
    }

    [Fact]
    public void Ignored_PropertyOmittedFromLambda()
    {
        var typeMap = new TypeMap(typeof(FpSrc), typeof(FpDst), MemberList.Destination);
        var pm = PropertyMap.ForProperty(typeof(FpDst).GetProperty(nameof(FpDst.Id))!);
        pm.Ignored = true;
        typeMap.PropertyMaps.Add(pm);

        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap));
        var del = (Func<FpSrc, FpDst>)lambda.Compile();

        // Source has Id=42 but destination keeps default because property is ignored.
        Assert.Equal(0, del(new FpSrc { Id = 42 }).Id);
    }

    [Fact]
    public void NestedMap_GoesThroughRegistry_NotInlinedRecursively()
    {
        var innerMap = new TypeMap(typeof(InnerSrc), typeof(InnerDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(innerMap, DefaultOpts());

        var outerMap = new TypeMap(typeof(OuterSrc), typeof(OuterDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(outerMap, DefaultOpts());

        var registry = RegistryFor(outerMap, innerMap);
        var lambda = ExecutionPlanBuilder.Build(outerMap, registry);

        // Structural: the outer lambda must call MappingInvoker.Invoke for the nested type pair,
        // not include a recursive `new InnerDst()` block inline.
        var visitor = new MethodCallFinder();
        visitor.Visit(lambda);
        Assert.Contains(visitor.Calls, m => m.Name == nameof(MappingInvoker.Invoke));
    }

    [Fact]
    public void Collection_ListSource_ListDestination_ProducesForEach()
    {
        var elementMap = new TypeMap(typeof(InnerSrc), typeof(InnerDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(elementMap, DefaultOpts());

        var typeMap = new TypeMap(typeof(List<InnerSrc>), typeof(List<InnerDst>), MemberList.None);
        var registry = RegistryFor(typeMap, elementMap);

        var lambda = ExecutionPlanBuilder.Build(typeMap, registry);
        var del = (Func<List<InnerSrc>, List<InnerDst>>)lambda.Compile();

        var input = new List<InnerSrc> { new() { Value = "a" }, new() { Value = "b" } };
        var output = del(input);

        Assert.Equal(2, output.Count);
        Assert.Equal("a", output[0].Value);
        Assert.Equal("b", output[1].Value);
    }

    [Fact]
    public void Collection_NullSource_ProducesEmptyList()
    {
        var elementMap = new TypeMap(typeof(InnerSrc), typeof(InnerDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(elementMap, DefaultOpts());

        var typeMap = new TypeMap(typeof(List<InnerSrc>), typeof(List<InnerDst>), MemberList.None);
        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap, elementMap));
        var del = (Func<List<InnerSrc>?, List<InnerDst>>)lambda.Compile();

        var output = del(null);
        Assert.NotNull(output);
        Assert.Empty(output);
    }

    [Fact]
    public void Collection_ArrayDestination_PreallocatesWhenSourceIsICollection()
    {
        var elementMap = new TypeMap(typeof(InnerSrc), typeof(InnerDst), MemberList.Destination);
        ConventionEngine.ResolveMissingMembers(elementMap, DefaultOpts());

        var typeMap = new TypeMap(typeof(List<InnerSrc>), typeof(InnerDst[]), MemberList.None);
        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap, elementMap));
        var del = (Func<List<InnerSrc>, InnerDst[]>)lambda.Compile();

        var input = new List<InnerSrc> { new() { Value = "x" }, new() { Value = "y" }, new() { Value = "z" } };
        var output = del(input);

        Assert.Equal(3, output.Length);
        Assert.Equal("x", output[0].Value);
        Assert.Equal("z", output[2].Value);
    }

    [Fact]
    public void Constructor_ParameterizedDestination_UsesNewExpressionWithCtor()
    {
        var typeMap = new TypeMap(typeof(CtorSrc), typeof(CtorDst), MemberList.None);
        ConventionEngine.ResolveMissingMembers(typeMap, DefaultOpts());

        var lambda = ExecutionPlanBuilder.Build(typeMap, RegistryFor(typeMap));
        var del = (Func<CtorSrc, CtorDst>)lambda.Compile();

        var dst = del(new CtorSrc { DisplayName = "Alice" });
        Assert.Equal("Alice", dst.DisplayName);
    }
}

// ---- Test fixtures ----

public class FpSrc { public int Id { get; set; } }
public class FpDst { public int Id { get; set; } }

public class FlatCustomer { public string Name { get; set; } = ""; }
public class FlatSrc { public FlatCustomer Customer { get; set; } = new(); }
public class FlatDst { public string? CustomerName { get; set; } }

public class InnerSrc { public string Value { get; set; } = ""; }
public class InnerDst { public string Value { get; set; } = ""; }

public class OuterSrc { public InnerSrc Inner { get; set; } = new(); }
public class OuterDst { public InnerDst Inner { get; set; } = new(); }

public class CtorSrc { public string DisplayName { get; set; } = ""; }
public class CtorDst
{
    public CtorDst(string displayName) { DisplayName = displayName; }
    public string DisplayName { get; }
}

internal sealed class MethodCallFinder : ExpressionVisitor
{
    public List<System.Reflection.MethodInfo> Calls { get; } = new();
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        Calls.Add(node.Method);
        return base.VisitMethodCall(node);
    }
}
