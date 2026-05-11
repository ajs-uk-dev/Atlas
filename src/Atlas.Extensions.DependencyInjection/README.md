# OGToolz.Atlas.Extensions.DependencyInjection

`Microsoft.Extensions.DependencyInjection` integration for the [Atlas](https://github.com/ajs-uk-dev/Atlas) object-to-object mapper. Adds `AddAtlas(...)` extension methods with assembly scanning for `MapperProfile` subclasses.

## Install

```bash
dotnet add package OGToolz.Atlas
dotnet add package OGToolz.Atlas.Extensions.DependencyInjection
```

## Quick start

```csharp
using Atlas;

// Scan one or more assemblies for MapperProfile subclasses.
services.AddAtlas(typeof(Program).Assembly);

// Or with an inline configuration callback:
services.AddAtlas(cfg =>
{
    cfg.CaseSensitive = false;
}, typeof(Program).Assembly);
```

Both `MapperConfiguration` and `IMapper` are registered as **singletons** — configuration is expensive to build and cheap to use. Profiles must be public top-level classes with a public parameterless constructor; violations throw `AtlasConfigurationException` at registration time.

Mappings are eagerly compiled at the end of `AddAtlas` so the JIT cost is paid at startup, not on first request.

## Documentation

See the main repository for the full developer guide:

- Repository: https://github.com/ajs-uk-dev/Atlas
- Developer guide: [`docs/DeveloperGuide.md`](https://github.com/ajs-uk-dev/Atlas/blob/main/docs/DeveloperGuide.md)

## License

MIT
