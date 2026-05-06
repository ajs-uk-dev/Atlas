using Atlas;

namespace Atlas.Tests;

public class MapperPreserveReferencesUpdateInPlaceTests
{
    [Fact]
    public void UpdateInPlace_FreshDestination_CycleResolvedToExisting()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existingDto = new PersonDto();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map(alice, existingDto);

        Assert.Equal("Alice", existingDto.Name);
        Assert.Same(existingDto, existingDto.Boss);
    }

    [Fact]
    public void UpdateInPlace_PreservesNonMappedFields()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existingDto = new PersonDto { LocalNote = "do not overwrite" };
        var alice = new Person { Name = "Alice" };

        mapper.Map(alice, existingDto);

        Assert.Equal("Alice", existingDto.Name);
        Assert.Equal("do not overwrite", existingDto.LocalNote);
    }

    [Fact]
    public void UpdateInPlace_NoCycle_BehavesLikeFreshMap()
    {
        // Without a cycle, update-in-place should set fields normally.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existingDto = new PersonDto();
        var alice = new Person { Name = "Alice", Age = 30 };

        mapper.Map(alice, existingDto);

        Assert.Equal("Alice", existingDto.Name);
        Assert.Equal(30, existingDto.Age);
    }

    [Fact]
    public void UpdateInPlace_AcrossDifferentDestinationTypes_SeparateContextPerCall()
    {
        // Each top-level Map call allocates its own MappingContext, so two calls with the same
        // source instance and different destination types don't interfere.
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>().PreserveReferences();
            c.CreateMap<Person, PersonSummary>().PreserveReferences();
        }).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = new PersonDto();
        var summary = new PersonSummary();
        mapper.Map(alice, dto);
        mapper.Map(alice, summary);

        Assert.Same(dto, dto.Boss);
        Assert.Equal("Alice", summary.Name);
    }

    [Fact]
    public void UpdateInPlace_WithoutPreserveReferences_NonCyclicSource_StillWorks()
    {
        // Sanity check: update-in-place without PR should still work for non-cyclic sources.
        // (We don't test cycles without PR because StackOverflowException is unrecoverable.)
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();   // NO .PreserveReferences()
        var existingDto = new PersonDto { LocalNote = "preserved" };
        var alice = new Person { Name = "Alice", Age = 30 };

        mapper.Map(alice, existingDto);

        Assert.Equal("Alice", existingDto.Name);
        Assert.Equal(30, existingDto.Age);
        Assert.Equal("preserved", existingDto.LocalNote);
    }

    private sealed class Person
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public Person? Boss { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public PersonDto? Boss { get; set; }
        public string? LocalNote { get; set; }
    }

    private sealed class PersonSummary
    {
        public string? Name { get; set; }
    }
}
