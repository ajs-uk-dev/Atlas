using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class ConfigurationValidatorNullSubstituteTests
{
    public class WithInt { public int Value { get; set; } }
    public class WithIntDto { public int Value { get; set; } }
    public class WithNullableInt { public int? Value { get; set; } }
    public class WithNullableIntDto { public int Value { get; set; } }
    public class WithString { public string? Name { get; set; } }
    public class WithStringDto { public string Name { get; set; } = ""; }
    public class WithEnum { public DayOfWeek Day { get; set; } }
    public class WithEnumDto { public DayOfWeek Day { get; set; } }

    [Fact]
    public void Validator_NullSubstitute_OnNonNullableValueType_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithInt, WithIntDto>(MemberList.None)
                .ForMember(d => d.Value, opt =>
                {
                    opt.MapFrom(s => s.Value);
                    opt.NullSubstitute(0);
                }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("unreachable", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void Validator_NullSubstitute_OnEnum_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithEnum, WithEnumDto>(MemberList.None)
                .ForMember(d => d.Day, opt =>
                {
                    opt.MapFrom(s => s.Day);
                    opt.NullSubstitute(DayOfWeek.Monday);
                }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("unreachable", ex.Message);
        Assert.Contains("DayOfWeek", ex.Message);
    }

    [Fact]
    public void Validator_NullSubstitute_OnNullableValueType_Passes()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithNullableInt, WithNullableIntDto>(MemberList.None)
                .ForMember(d => d.Value, opt =>
                {
                    opt.MapFrom(s => s.Value);
                    opt.NullSubstitute(0);
                }));

        cfg.AssertConfigurationIsValid();   // no throw
    }

    [Fact]
    public void Validator_NullSubstitute_OnReferenceType_Passes()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithString, WithStringDto>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Default");
                }));

        cfg.AssertConfigurationIsValid();   // no throw
    }

    [Fact]
    public void Validator_NullSubstitute_TypeMismatch_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithNullableInt, WithNullableIntDto>(MemberList.None)
                .ForMember(d => d.Value, opt =>
                {
                    opt.MapFrom(s => s.Value);
                    opt.NullSubstitute("not-an-int");
                }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("not assignable", ex.Message);
        Assert.Contains("String", ex.Message);
    }
}
