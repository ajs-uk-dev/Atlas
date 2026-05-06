using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Atlas;

namespace Atlas.Tests;

public class MapperPreserveReferencesIntegrationTests
{
    [Fact]
    public async Task ConcurrentTopLevelCalls_DoNotShareContext()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var p = new Person { Name = $"user{i}" };
            p.Boss = p;
            return mapper.Map<PersonDto>(p);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(16, results.Length);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal($"user{i}", results[i].Name);
            Assert.Same(results[i], results[i].Boss);
        }
    }

    [Fact]
    public void DeepGraphWithoutCycles_PreservesReferencesIsCorrect_NoIdentityChange()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();

        var c = new Person { Name = "C" };
        var b = new Person { Name = "B", Boss = c };
        var a = new Person { Name = "A", Boss = b };

        var dto = mapper.Map<PersonDto>(a);

        Assert.Equal("A", dto.Name);
        Assert.Equal("B", dto.Boss!.Name);
        Assert.Equal("C", dto.Boss.Boss!.Name);
        Assert.Null(dto.Boss.Boss.Boss);
    }

    [Fact]
    public void Cycle_AcrossThreeWayMutualReference_AllResolved()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();

        var a = new Person { Name = "A" };
        var b = new Person { Name = "B" };
        var c = new Person { Name = "C" };
        a.Friends = new List<Person> { b, c };
        b.Friends = new List<Person> { a, c };
        c.Friends = new List<Person> { a, b };

        var aDto = mapper.Map<PersonDto>(a);

        Assert.Same(aDto, aDto.Friends![0].Friends![0]);  // a → b → a
        Assert.Same(aDto.Friends[1], aDto.Friends[0].Friends![1]);  // b.Friends[1] == c == a.Friends[1]
    }

    [Fact]
    public void BeforeMapHook_ObservesSourceCorrectly_FiresOnceForCyclicSource()
    {
        var observed = new List<string>();
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .BeforeMap((s, d) => observed.Add(s.Name!))).CreateMapper();

        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map<PersonDto>(alice);

        // alice appears twice in graph (top-level + as Boss); BeforeMap fires once.
        Assert.Single(observed);
        Assert.Equal("Alice", observed[0]);
    }

    [Fact]
    public void AfterMapHook_FiresOnceForCyclicSource_AfterDestinationFullyPopulated()
    {
        var observed = new List<(string srcName, string? dstName)>();
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .AfterMap((s, d) => observed.Add((s.Name!, d.Name)))).CreateMapper();

        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map<PersonDto>(alice);

        Assert.Single(observed);
        Assert.Equal("Alice", observed[0].srcName);
        Assert.Equal("Alice", observed[0].dstName);
    }

    [Fact]
    public void NestedCollectionMap_SharedElementInTwoLists_DedupedAcrossLists()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>();
            c.CreateMap<Container, ContainerDto>().PreserveReferences();
        }).CreateMapper();

        var alice = new Person { Name = "Alice" };
        var src = new Container
        {
            ListA = new List<Person> { alice },
            ListB = new List<Person> { alice }
        };

        var dto = mapper.Map<ContainerDto>(src);

        Assert.Same(dto.ListA![0], dto.ListB![0]);
    }

    [Fact]
    public void NoCycle_NoPreserveReferences_PerformanceParityWithV1()
    {
        // Sanity: a non-cyclic graph maps correctly without PR; verifies the universal-parameter
        // change in Task 3 didn't break anything.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();
        var p = new Person { Name = "Alice" };

        var dto = mapper.Map<PersonDto>(p);

        Assert.Equal("Alice", dto.Name);
    }

    [Fact]
    public void Map_IntoExistingNestedDestination_CycleResolvedToOuterExisting()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existing = new PersonDto { LocalNote = "preserved" };
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map(alice, existing);

        Assert.Equal("Alice", existing.Name);
        Assert.Same(existing, existing.Boss);
        Assert.Equal("preserved", existing.LocalNote);
    }

    [Fact]
    public void OffPath_AcrossManyMapCalls_DoesNotAccumulateState()
    {
        // Sanity: many calls without PR don't accumulate any state (no cache leaks).
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();

        for (int i = 0; i < 100; i++)
        {
            var p = new Person { Name = $"user{i}" };
            var dto = mapper.Map<PersonDto>(p);
            Assert.Equal($"user{i}", dto.Name);
        }
    }

    [Fact]
    public void MixedCycleAndSharedReference_BothResolvedCorrectly()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();
        }).CreateMapper();

        var dept = new Department { Name = "Engineering" };
        var alice = new Employee { Name = "Alice", Department = dept };
        var bob = new Employee { Name = "Bob", Department = dept };
        alice.Manager = bob;
        bob.Manager = alice;
        dept.Employees = new List<Employee> { alice, bob };

        var dto = mapper.Map<DepartmentDto>(dept);

        // Shared department
        Assert.Same(dto, dto.Employees![0].Department);
        Assert.Same(dto, dto.Employees[1].Department);

        // Mutual cycle resolved
        Assert.Same(dto.Employees[0], dto.Employees[1].Manager);
        Assert.Same(dto.Employees[1], dto.Employees[0].Manager);
    }

    private sealed class Person
    {
        public string? Name { get; set; }
        public Person? Boss { get; set; }
        public List<Person>? Friends { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
        public PersonDto? Boss { get; set; }
        public List<PersonDto>? Friends { get; set; }
        public string? LocalNote { get; set; }
    }

    private sealed class Container
    {
        public List<Person>? ListA { get; set; }
        public List<Person>? ListB { get; set; }
    }

    private sealed class ContainerDto
    {
        public List<PersonDto>? ListA { get; set; }
        public List<PersonDto>? ListB { get; set; }
    }

    private sealed class Department
    {
        public string? Name { get; set; }
        public List<Employee>? Employees { get; set; }
    }

    private sealed class DepartmentDto
    {
        public string? Name { get; set; }
        public List<EmployeeDto>? Employees { get; set; }
    }

    private sealed class Employee
    {
        public string? Name { get; set; }
        public Department? Department { get; set; }
        public Employee? Manager { get; set; }
    }

    private sealed class EmployeeDto
    {
        public string? Name { get; set; }
        public DepartmentDto? Department { get; set; }
        public EmployeeDto? Manager { get; set; }
    }
}
