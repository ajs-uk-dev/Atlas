using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests;

public class ConfigurationValidatorHookTests
{
    public sealed class S { public int X { get; set; } }
    public sealed class D { public int X { get; set; } }

    public sealed class GoodAction : IMappingAction<S, D>
    {
        public void Process(S source, D destination) { }
    }

    public sealed class CtorRequiredAction : IMappingAction<S, D>
    {
        public CtorRequiredAction(int x) { _ = x; }
        public void Process(S source, D destination) { }
    }

    public sealed class WrongPair : IMappingAction<int, string>
    {
        public void Process(int source, string destination) { }
    }

    public sealed class ScopedDep
    {
        public ScopedDep() { }
    }

    public sealed class ActionWithScopedDep : IMappingAction<S, D>
    {
        public ActionWithScopedDep(ScopedDep dep) { _ = dep; }
        public void Process(S source, D destination) { }
    }

    [Fact]
    public void ValidateHooks_ActionTypeWithoutParameterlessCtor_NoDI_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap<CtorRequiredAction>());

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("CtorRequiredAction", ex.Message);
        Assert.Contains("parameterless", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateHooks_ActionTypeNotImplementingInterface_Errors()
    {
        // The fluent API's generic constraint prevents this at COMPILE time, but a
        // hand-crafted HookEntry could carry an arbitrary type. Validate via a TypeMap
        // built bypassing the fluent surface.
        var expression = new MapperConfigurationExpression();
        var fwdExpr = expression.CreateMap<S, D>(MemberList.None);
        // Reach into the TypeMap and add a malformed HookEntry.
        var tm = ((Atlas.Configuration.MappingExpression<S, D>)fwdExpr).TypeMap;
        tm.BeforeHooks.Add(Atlas.Internal.HookEntry.FromActionType(typeof(WrongPair)));
        var cfg = new MapperConfiguration(expression);

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("WrongPair", ex.Message);
        Assert.Contains("IMappingAction", ex.Message);
    }

    [Fact]
    public void ValidateHooks_ValidLambdaAndAction_Pass()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => { })
                .AfterMap<GoodAction>());

        cfg.AssertConfigurationIsValid();   // does not throw
    }

    [Fact]
    public void ValidateHooks_ScopedServiceDependency_Errors_WithClearMessage()
    {
        // Note: ValidateScopes = true makes ActivatorUtilities.CreateInstance throw
        // when invoked against the root SP for a scoped dep. Without this, the construction
        // would silently succeed at validate time and only fail at scope-creation later.
        // Production use through `services.BuildServiceProvider(...)` defaults to false;
        // users who want config-time scope detection enable it explicitly.
        var services = new ServiceCollection();
        services.AddScoped<ScopedDep>();
        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap<ActionWithScopedDep>(),
            sp);

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("ActionWithScopedDep", ex.Message);
        // The wrapped message contains "scoped" via the helpful "scoped services are not supported" guidance.
        Assert.Contains("scoped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
