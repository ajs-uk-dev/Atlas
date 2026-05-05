namespace Atlas.Tests;

public class MapperConfigurationOpenGenericValidationTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Customer { public string Name { get; set; } = ""; }
    public class CustomerDto { public string Name { get; set; } = ""; }

    [Fact]
    public void AssertConfigurationIsValid_OpenGenericOnly_DoesNotThrow()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));

        // Open-generic templates are excluded from validation per the design's §6.1.
        // No closed pairs registered → validator iterates an empty AllTypeMaps and exits cleanly.
        cfg.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_OpenGenericPlusClosedPairs_ValidatesClosedPairsOnly()
    {
        // Closed pair Customer → CustomerDto WILL be validated (uses MemberList.Destination
        // by default per CreateMap<TS, TD> overload). Validation should pass since
        // CustomerDto.Name is mapped via convention from Customer.Name.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Customer, CustomerDto>();
        });

        cfg.AssertConfigurationIsValid();   // no throw
    }
}
