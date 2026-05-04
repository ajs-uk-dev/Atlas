namespace Atlas;

/// <summary>
/// Reusable mapping-action interface for DI-friendly hook logic. Implementations are
/// instantiated via <c>ActivatorUtilities.CreateInstance</c> from the root
/// <see cref="System.IServiceProvider"/> when Atlas is registered through
/// <c>Atlas.Extensions.DependencyInjection</c>; without DI, a public parameterless
/// constructor is required.
/// </summary>
/// <remarks>
/// Constructor injection of singleton and transient services works out of the box.
/// <b>Scoped services (HTTP context, current user, scoped EF DbContext) are NOT supported</b> —
/// the action is resolved from the root provider and cached once per configuration.
/// For HTTP context-aware logic, inject <c>IHttpContextAccessor</c> (which is itself
/// singleton-resolvable) and read the per-request context inside <see cref="Process"/>.
/// </remarks>
public interface IMappingAction<in TSource, in TDestination>
{
    /// <summary>
    /// Runs at the time configured by <c>BeforeMap</c> or <c>AfterMap</c>.
    /// </summary>
    void Process(TSource source, TDestination destination);
}
