using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Internal;

/// <summary>
/// Resolves a <see cref="HookEntry"/> to a runnable <c>Action&lt;TSource, TDestination&gt;</c>.
/// Lambda entries are returned directly. ActionType entries are instantiated ONCE per
/// configuration via <see cref="ActivatorUtilities.CreateInstance(IServiceProvider, Type, object[])"/>
/// (when SP is non-null) or <see cref="Activator.CreateInstance(Type)"/> (when SP is null).
/// Instances are cached in <see cref="MapperRegistry.ActionInstances"/>.
/// </summary>
internal static class HookResolver
{
    public static Action<TSource, TDestination> Resolve<TSource, TDestination>(
        HookEntry entry,
        MapperRegistry registry)
    {
        if (entry.Lambda is Action<TSource, TDestination> typedLambda)
            return typedLambda;

        if (entry.Lambda is not null)
            throw new InvalidOperationException(
                $"Hook lambda has type {entry.Lambda.GetType().Name} but expected " +
                $"Action<{typeof(TSource).Name}, {typeof(TDestination).Name}>.");

        var actionType = entry.ActionType
            ?? throw new InvalidOperationException("HookEntry has neither Lambda nor ActionType set.");

        if (!registry.ActionInstances.TryGetValue(actionType, out var instance))
        {
            try
            {
                instance = registry.ServiceProvider is { } sp
                    ? ActivatorUtilities.CreateInstance(sp, actionType)
                    : Activator.CreateInstance(actionType)
                        ?? throw new InvalidOperationException(
                            $"Activator.CreateInstance returned null for {actionType.Name}.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Failed to construct mapping action {actionType.Name}. " +
                    "When Atlas is used without the DI extension, the action must have a public parameterless constructor. " +
                    "When using DI, ensure all constructor dependencies are registered as singleton or transient (scoped services are not supported).",
                    ex);
            }
            registry.ActionInstances[actionType] = instance;
        }

        var action = (IMappingAction<TSource, TDestination>)instance;
        return action.Process;
    }
}
