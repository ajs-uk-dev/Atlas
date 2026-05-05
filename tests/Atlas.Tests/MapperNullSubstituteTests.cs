using Atlas.Configuration;

namespace Atlas.Tests;

public class MapperNullSubstituteTests
{
    public sealed class Customer
    {
        public string? Name { get; set; }
        public int? Score { get; set; }
        public DateTime? LastLogin { get; set; }
    }
    public sealed class CustomerDto
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
        public DateTime LastLogin { get; set; }
    }

    [Fact]
    public void HeadlineExample_FromReferenceDoc()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Customer, CustomerDto>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                })
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(0);
                })
                .ForMember(d => d.LastLogin, opt =>
                {
                    opt.MapFrom(s => s.LastLogin);
                    opt.NullSubstitute(() => DateTime.UnixEpoch);
                }));
        var mapper = cfg.CreateMapper();

        var allNull = mapper.Map<CustomerDto>(new Customer
        {
            Name = null,
            Score = null,
            LastLogin = null,
        });
        var allReal = mapper.Map<CustomerDto>(new Customer
        {
            Name = "Alice",
            Score = 42,
            LastLogin = new DateTime(2024, 6, 1),
        });

        Assert.Equal("Anonymous", allNull.Name);
        Assert.Equal(0, allNull.Score);
        Assert.Equal(DateTime.UnixEpoch, allNull.LastLogin);

        Assert.Equal("Alice", allReal.Name);
        Assert.Equal(42, allReal.Score);
        Assert.Equal(new DateTime(2024, 6, 1), allReal.LastLogin);
    }

    [Fact]
    public void Update_NullSubstitute_AppliesUniformly()
    {
        // Update-in-place uses BuildSourceExpression too, so the substitute applies
        // automatically via the same Coalesce wrap.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Customer, CustomerDto>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                })
                .ForMember(d => d.Score, opt => opt.Ignore())
                .ForMember(d => d.LastLogin, opt => opt.Ignore()));
        var mapper = cfg.CreateMapper();

        var existing = new CustomerDto { Name = "OldValue" };
        mapper.Map(new Customer { Name = null }, existing);

        // Substitute fires on null source, overwriting the existing destination value.
        Assert.Equal("Anonymous", existing.Name);
    }

    [Fact]
    public void Inheritance_BaseSubstitute_FlowsToDerived()
    {
        // Base map sets NullSubstitute on Nickname; derived map (via Include) inherits it.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Animal, AnimalDto>(MemberList.None)
                .Include<Dog, DogDto>()
                .ForMember(d => d.Nickname, opt =>
                {
                    opt.MapFrom(s => s.Nickname);
                    opt.NullSubstitute("Pet");
                });
            c.CreateMap<Dog, DogDto>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var nullNickname = mapper.Map<DogDto>(new Dog { Nickname = null });
        var realNickname = mapper.Map<DogDto>(new Dog { Nickname = "Rex" });

        Assert.Equal("Pet", nullNickname.Nickname);
        Assert.Equal("Rex", realNickname.Nickname);
    }

    public class Animal { public string? Nickname { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public string Nickname { get; set; } = ""; }
    public class DogDto : AnimalDto { }
}
