using Atlas.Configuration;

namespace Atlas.Tests;

public class MapperConditionTests
{
    public sealed class Order
    {
        public List<OrderItem>? Items { get; set; }
        public string? Description { get; set; }
    }
    public sealed class OrderItem
    {
        public decimal Price { get; set; }
        public int Quantity { get; set; }
    }
    public sealed class OrderDto
    {
        public decimal Total { get; set; }
        public string Description { get; set; } = "";
    }

    [Fact]
    public void HeadlineExample_PreConditionTrue_AndConditionTrue_AssignsTotal()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Total, opt =>
                {
                    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
                    opt.MapFrom(s => s.Items!.Sum(i => i.Price * i.Quantity));
                    opt.Condition((s, total) => total > 0);
                }));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<OrderDto>(new Order
        {
            Items = new List<OrderItem>
            {
                new() { Price = 10m, Quantity = 2 },
                new() { Price = 5m,  Quantity = 1 },
            },
        });

        Assert.Equal(25m, dto.Total);
    }

    [Fact]
    public void HeadlineExample_PreConditionFalse_TotalIsZero()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Total, opt =>
                {
                    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
                    opt.MapFrom(s => s.Items!.Sum(i => i.Price * i.Quantity));
                    opt.Condition((s, total) => total > 0);
                }));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<OrderDto>(new Order { Items = null });

        Assert.Equal(0m, dto.Total);
    }

    [Fact]
    public void Condition_ReadsResolvedValue_AfterTransformer_AndUsesSourceParam()
    {
        // Transformer trims; Condition fires on the post-trim length AND inspects the source.
        // This test exercises BOTH parameters of the Condition lambda (s and desc) — closing
        // the Task-4-review carry-over coverage gap.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s.Trim());
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Description, opt =>
                {
                    opt.MapFrom(s => s.Description ?? "");
                    opt.Condition((s, desc) => s.Description != null && desc.Length > 0);
                });
        });
        var mapper = cfg.CreateMapper();

        var nullSource = mapper.Map<OrderDto>(new Order { Description = null });
        var emptyAfterTrim = mapper.Map<OrderDto>(new Order { Description = "  " });
        var realText = mapper.Map<OrderDto>(new Order { Description = "  hello  " });

        // Source's Description is null → s.Description != null is false → skip → default(string) (null)
        Assert.Null(nullSource.Description);
        // Source non-null but post-trim empty → desc.Length > 0 false → skip → default(string) (null)
        Assert.Null(emptyAfterTrim.Description);
        // Both non-null source and non-empty post-trim → assign
        Assert.Equal("hello", realText.Description);
    }

    [Fact]
    public void Collection_ElementMapHasConditions_AppliedPerElement()
    {
        // Atlas convention: List<S> -> List<D> requires an explicit CreateMap on the
        // collection types. The element map's predicates fire on each element via the
        // collection-mapping inner invoke.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Total, opt =>
                {
                    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
                    opt.MapFrom(s => s.Items!.Sum(i => i.Price * i.Quantity));
                });
            c.CreateMap<List<Order>, List<OrderDto>>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var orders = new List<Order>
        {
            new() { Items = new List<OrderItem> { new() { Price = 10m, Quantity = 1 } } },
            new() { Items = null },
            new() { Items = new List<OrderItem> { new() { Price = 5m, Quantity = 4 } } },
        };

        var dtos = mapper.Map<List<OrderDto>>(orders);

        Assert.Equal(3, dtos.Count);
        Assert.Equal(10m, dtos[0].Total);
        Assert.Equal(0m, dtos[1].Total);    // PreCondition false → default
        Assert.Equal(20m, dtos[2].Total);
    }

    [Fact]
    public void Inheritance_BasePredicate_FlowsToDerivedMap()
    {
        // Tests that base-map's predicate (set via ForMember) flows to derived map via
        // InheritanceMerger when derived doesn't override.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Animal, AnimalDto>(MemberList.None)
                .Include<Dog, DogDto>()
                .ForMember(d => d.Legs, opt =>
                {
                    opt.PreCondition(s => s.Legs > 0);
                    opt.MapFrom(s => s.Legs);
                });
            c.CreateMap<Dog, DogDto>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var positive = mapper.Map<DogDto>(new Dog { Legs = 4 });
        var negative = mapper.Map<DogDto>(new Dog { Legs = -1 });

        Assert.Equal(4, positive.Legs);
        Assert.Equal(0, negative.Legs);   // base PreCondition flowed to derived
    }

    public class Animal { public int Legs { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public int Legs { get; set; } }
    public class DogDto : AnimalDto { }
}
