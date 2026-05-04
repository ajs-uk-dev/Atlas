using Atlas.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests.Internal;

public class HookResolverTests
{
    public sealed class S { }
    public sealed class D { }

    public sealed class ParameterlessAction : IMappingAction<S, D>
    {
        public int CallCount { get; private set; }
        public void Process(S source, D destination) => CallCount++;
    }

    public sealed class ServiceDependency
    {
        public string Tag { get; } = "from-DI";
    }

    public sealed class DiAction : IMappingAction<S, D>
    {
        public ServiceDependency Dep { get; }
        public DiAction(ServiceDependency dep) => Dep = dep;
        public void Process(S source, D destination) { }
    }

    public sealed class NoCtorAction : IMappingAction<S, D>
    {
        public NoCtorAction(int x) { _ = x; }
        public void Process(S source, D destination) { }
    }

    private static MapperRegistry NewRegistry(IServiceProvider? sp = null) =>
        new(Array.Empty<TypeMap>(), null, sp);

    [Fact]
    public void Resolve_LambdaEntry_ReturnsTypedDelegate()
    {
        Action<S, D> hook = (s, d) => { };
        var entry = HookEntry.FromLambda(hook);
        var registry = NewRegistry();

        var resolved = HookResolver.Resolve<S, D>(entry, registry);

        Assert.Same(hook, resolved);
    }

    [Fact]
    public void Resolve_ActionType_NoDI_RequiresParameterlessCtor()
    {
        var entry = HookEntry.FromActionType(typeof(ParameterlessAction));
        var registry = NewRegistry();

        var resolved = HookResolver.Resolve<S, D>(entry, registry);
        var src = new S(); var dst = new D();
        resolved(src, dst);

        Assert.True(registry.ActionInstances.ContainsKey(typeof(ParameterlessAction)));
        var instance = (ParameterlessAction)registry.ActionInstances[typeof(ParameterlessAction)];
        Assert.Equal(1, instance.CallCount);
    }

    [Fact]
    public void Resolve_ActionType_DI_ConstructsViaActivatorUtilities()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ServiceDependency>();
        var sp = services.BuildServiceProvider();
        var entry = HookEntry.FromActionType(typeof(DiAction));
        var registry = NewRegistry(sp);

        var resolved = HookResolver.Resolve<S, D>(entry, registry);
        resolved(new S(), new D());

        var instance = (DiAction)registry.ActionInstances[typeof(DiAction)];
        Assert.Equal("from-DI", instance.Dep.Tag);
    }

    [Fact]
    public void Resolve_ActionType_CachedAcrossCalls()
    {
        var entry = HookEntry.FromActionType(typeof(ParameterlessAction));
        var registry = NewRegistry();

        var first = HookResolver.Resolve<S, D>(entry, registry);
        var second = HookResolver.Resolve<S, D>(entry, registry);

        Assert.Single(registry.ActionInstances);
        // Both delegates target the same instance.
        Assert.Same(first.Target, second.Target);
    }

    [Fact]
    public void Resolve_ActionTypeWithoutCtor_NoDI_Throws()
    {
        var entry = HookEntry.FromActionType(typeof(NoCtorAction));
        var registry = NewRegistry();

        Assert.Throws<InvalidOperationException>(() => HookResolver.Resolve<S, D>(entry, registry));
    }
}
