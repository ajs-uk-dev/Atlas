namespace Atlas.Tests;

public class MapperReverseMapTests
{
    public class Customer { public string? Name { get; set; } public string? Email { get; set; } }
    public class Order
    {
        public int Id { get; set; }
        public Customer? Customer { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Tax { get; set; }
    }
    public class OrderDto
    {
        public string? CustomerName { get; set; }
        public string? CustomerEmail { get; set; }
        public decimal OrderTotal { get; set; }
    }

    public class City { public string? Name { get; set; } }
    public class CustomerAddressed { public City? Address { get; set; } }
    public class OrderDeep { public CustomerAddressed? Customer { get; set; } }
    public class OrderDeepDto { public string? CustomerAddressName { get; set; } }

    [Fact]
    public void RoundTrip_OrderDtoToOrder_FlattenedThenUnflattened()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Order, OrderDto>()
                .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
                .ReverseMap());
        var mapper = cfg.CreateMapper();

        var entity = new Order { Id = 7, Customer = new Customer { Name = "Alice", Email = "a@x" }, Subtotal = 90m, Tax = 10m };
        var dto = mapper.Map<OrderDto>(entity);

        Assert.Equal("Alice", dto.CustomerName);
        Assert.Equal("a@x", dto.CustomerEmail);
        Assert.Equal(100m, dto.OrderTotal);

        var roundTripped = mapper.Map<Order>(dto);
        Assert.NotNull(roundTripped.Customer);
        Assert.Equal("Alice", roundTripped.Customer!.Name);
        Assert.Equal("a@x", roundTripped.Customer.Email);
        // Id, Subtotal, Tax are not on the DTO and not configured on reverse — defaults expected.
        Assert.Equal(0, roundTripped.Id);
        Assert.Equal(0m, roundTripped.Subtotal);
        Assert.Equal(0m, roundTripped.Tax);
    }

    [Fact]
    public void Reverse_UnflatteningWritesNestedIntermediate()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var dto = new OrderDto { CustomerName = "Bob", CustomerEmail = "b@x" };
        var entity = mapper.Map<Order>(dto);

        Assert.NotNull(entity.Customer);
        Assert.Equal("Bob", entity.Customer!.Name);
        Assert.Equal("b@x", entity.Customer.Email);
    }

    [Fact]
    public void Reverse_ThreeLevelUnflattenWorks()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<OrderDeep, OrderDeepDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var dto = new OrderDeepDto { CustomerAddressName = "London" };
        var entity = mapper.Map<OrderDeep>(dto);

        Assert.NotNull(entity.Customer);
        Assert.NotNull(entity.Customer!.Address);
        Assert.Equal("London", entity.Customer.Address!.Name);
    }

    public class OrderWithPricing
    {
        public Pricing? Pricing { get; set; }
    }
    public class Pricing { public decimal Total { get; set; } }
    public class PricingDto { public decimal OrderTotal { get; set; } }

    [Fact]
    public void Reverse_ForPathOverride_BeatsMirroredBinding()
    {
        // Forward: OrderWithPricing.Pricing.Total → PricingDto.OrderTotal (convention flattening).
        // Reverse override: ForPath(d => d.Pricing.Total, opt => opt.MapFrom(s => s.OrderTotal * 2)).
        var cfg = new MapperConfiguration(c => c.CreateMap<OrderWithPricing, PricingDto>()
            .ReverseMap()
            .ForPath(d => d.Pricing!.Total, opt => opt.MapFrom(s => s.OrderTotal * 2)));
        var mapper = cfg.CreateMapper();

        var dto = new PricingDto { OrderTotal = 50m };
        var entity = mapper.Map<OrderWithPricing>(dto);

        Assert.NotNull(entity.Pricing);
        Assert.Equal(100m, entity.Pricing!.Total);   // override doubled it
    }

    [Fact]
    public void Reverse_IgnoreOnReverse_Honoured()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>()
            .ReverseMap()
            .ForMember(d => d.Customer, opt => opt.Ignore()));   // wholesale ignore Customer on reverse
        var mapper = cfg.CreateMapper();

        var dto = new OrderDto { CustomerName = "should not be written", CustomerEmail = "ignored" };
        var entity = mapper.Map<Order>(dto);

        // Mirror's skip-rule-2 sees IsExplicit Customer binding (Ignored=true) — Customer.Name/Email mirror entries suppressed.
        Assert.Null(entity.Customer);
    }

    [Fact]
    public void Reverse_MemberListDestination_TriggersValidationErrors()
    {
        // Reverse with MemberList.Destination should report unmapped Order properties (Id, Subtotal, Tax)
        // but NOT report Customer (path[0] coverage).
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>()
            .ReverseMap(MemberList.Destination));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());

        Assert.Contains("Id", ex.Message);
        Assert.Contains("Subtotal", ex.Message);
        Assert.Contains("Tax", ex.Message);
        Assert.DoesNotContain("Customer:", ex.Message);   // colon suffix — would have caught the validator bug
    }

    [Fact]
    public void Reverse_TwoLevelChain_PreservesValueViaRoundTrip()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var entity1 = new Order { Customer = new Customer { Name = "round" } };
        var dto = mapper.Map<OrderDto>(entity1);
        var entity2 = mapper.Map<Order>(dto);

        Assert.Equal("round", entity2.Customer!.Name);
    }

    [Fact]
    public void Reverse_UpdateInPlace_MapSourceDestExistingDest_Works()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var existingChild = new Customer { Name = "old", Email = "preset@x" };
        var existingEntity = new Order { Id = 99, Customer = existingChild };
        var dto = new OrderDto { CustomerName = "Updated", CustomerEmail = "updated@x" };

        mapper.Map<OrderDto, Order>(dto, existingEntity);

        // The Customer instance is preserved (same reference) because the coalesce
        // in BuildNestedAssign reuses the existing intermediate.
        Assert.Same(existingChild, existingEntity.Customer);
        Assert.Equal("Updated", existingEntity.Customer!.Name);
        Assert.Equal("updated@x", existingEntity.Customer.Email);
        // Id is NOT mapped at all (no PM, DTO has no Id) — preserved.
        Assert.Equal(99, existingEntity.Id);
    }
}
