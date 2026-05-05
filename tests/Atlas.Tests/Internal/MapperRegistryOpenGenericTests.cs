using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class MapperRegistryOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Customer { public string Name { get; set; } = ""; }
    public class CustomerDto { public string Name { get; set; } = ""; }

    [Fact]
    public void GetTypeMap_PrimitiveTypeArg_MaterializesAndCaches()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var registry = cfg.Internal_Registry;
        var pair = new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>));

        var first = registry.GetTypeMap(pair);
        var second = registry.GetTypeMap(pair);

        Assert.NotNull(first);
        Assert.Same(first, second);   // cache hit on second call
        Assert.Equal(typeof(Wrapper<int>), first!.SourceType);
        Assert.Equal(typeof(WrapperDto<int>), first.DestinationType);
        Assert.True(first.IsSealed);
    }

    [Fact]
    public void GetTypeMap_ReferenceTypeArg_MaterializesAndCaches()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var registry = cfg.Internal_Registry;
        var pair = new TypePair(typeof(Wrapper<Customer>), typeof(WrapperDto<Customer>));

        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.Equal(typeof(Wrapper<Customer>), tm!.SourceType);
        Assert.Single(tm.PropertyMaps, p => p.Name == "Value");
    }

    [Fact]
    public void GetTypeMap_NestedClosedPairAlreadyRegistered_UsesExistingMap()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Customer, CustomerDto>();
        });
        var registry = cfg.Internal_Registry;

        // Materializing (Wrapper<Customer>, WrapperDto<CustomerDto>) — heterogeneous T positions.
        var pair = new TypePair(typeof(Wrapper<Customer>), typeof(WrapperDto<CustomerDto>));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        // Convention engine should resolve Value: Customer → CustomerDto via the registered nested map.
        var valuePm = tm!.PropertyMaps.Single(p => p.Name == "Value");
        Assert.NotNull(valuePm.SourcePath);
    }

    [Fact]
    public void GetTypeMap_ClosedPairTakesPrecedenceOverOpenGeneric()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Wrapper<int>, WrapperDto<int>>(MemberList.None);
        });
        var registry = cfg.Internal_Registry;
        var pair = new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>));

        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        // RegistrationOrigin should reflect the closed-pair registration, not "(closed at runtime as ...)".
        Assert.DoesNotContain("(closed at runtime", tm!.RegistrationOrigin);
    }

    [Fact]
    public void GetTypeMap_NoMatchingOpenGeneric_ReturnsNull()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var registry = cfg.Internal_Registry;

        // (Customer, CustomerDto) — neither generic, no template matches.
        var pair = new TypePair(typeof(Customer), typeof(CustomerDto));

        Assert.Null(registry.GetTypeMap(pair));
    }
}
