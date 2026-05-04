using System.Reflection;
using Atlas.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas;

/// <summary>
/// Adds Atlas mapper services to <see cref="IServiceCollection"/>. Registers a singleton
/// <see cref="MapperConfiguration"/> built once at startup, and a singleton <see cref="IMapper"/>
/// facade. Profiles are discovered via assembly scan; mappings are eagerly compiled.
/// </summary>
public static class AtlasServiceCollectionExtensions
{
    /// <summary>
    /// Registers Atlas with profiles discovered from the given assemblies.
    /// </summary>
    public static IServiceCollection AddAtlas(this IServiceCollection services, params Assembly[] assemblies) =>
        AddAtlas(services, configure: null, assemblies);

    /// <summary>
    /// Registers Atlas, applying <paramref name="configure"/> to the configuration expression
    /// before scanning <paramref name="assemblies"/> for profiles.
    /// </summary>
    public static IServiceCollection AddAtlas(
        this IServiceCollection services,
        Action<MapperConfigurationExpression>? configure,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MapperConfiguration>(sp =>
        {
            var expression = new MapperConfigurationExpression();
            configure?.Invoke(expression);

            foreach (var profile in ProfileScanner.Discover(assemblies))
                expression.AddProfile(profile);

            var configuration = new MapperConfiguration(expression, sp);
            configuration.CompileMappings();
            return configuration;
        });
        services.AddSingleton<IMapper>(sp => sp.GetRequiredService<MapperConfiguration>().CreateMapper());
        return services;
    }

    /// <summary>
    /// Registers Atlas with profiles discovered from the assembly that declares <typeparamref name="TMarker"/>.
    /// </summary>
    public static IServiceCollection AddAtlas<TMarker>(this IServiceCollection services) =>
        AddAtlas(services, configure: null, typeof(TMarker).Assembly);
}
