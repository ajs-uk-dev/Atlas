using System.Collections.Generic;
using Atlas;

namespace Atlas.Tests;

public class MapperPreserveReferencesTests
{
    // ─── Cycle-breaking ─────────────────────────────────────────────────────────

    [Fact]
    public void SelfCycle_PersonBossEqualsSelf_TerminatesAndPreservesIdentity()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.NotNull(dto.Boss);
        Assert.Same(dto, dto.Boss);
    }

    [Fact]
    public void MutualCycle_TwoPeoplePointAtEachOther_BothEdgesPreserved()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        alice.Boss = bob;
        bob.Boss = alice;

        var aliceDto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", aliceDto.Name);
        Assert.Equal("Bob", aliceDto.Boss!.Name);
        Assert.Same(aliceDto, aliceDto.Boss.Boss);
    }

    [Fact]
    public void LongerCycle_ABThenBA_TerminatesAndPreservesIdentity()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var a = new Person { Name = "A" };
        var b = new Person { Name = "B" };
        var ccc = new Person { Name = "C" };
        a.Boss = b;
        b.Boss = ccc;
        ccc.Boss = a;

        var aDto = mapper.Map<PersonDto>(a);

        Assert.Equal("A", aDto.Name);
        Assert.Equal("B", aDto.Boss!.Name);
        Assert.Equal("C", aDto.Boss.Boss!.Name);
        Assert.Same(aDto, aDto.Boss.Boss.Boss);
    }

    [Fact]
    public void SelfCycleViaCollection_PersonFriendsContainsSelf()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Friends = new List<Person> { alice };

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Single(dto.Friends!);
        Assert.Same(dto, dto.Friends![0]);
    }

    [Fact]
    public void CycleAcrossCollectionElements_BothElementsReferenceEachOther()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        alice.Friends = new List<Person> { bob };
        bob.Friends = new List<Person> { alice };

        var aliceDto = mapper.Map<PersonDto>(alice);

        Assert.Same(aliceDto, aliceDto.Friends![0].Friends![0]);
    }

    // ─── Shared-reference deduplication ─────────────────────────────────────────

    [Fact]
    public void SharedReference_DepartmentAcrossManyEmployees_AllocatedOnce()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();
        }).CreateMapper();

        var sales = new Department { Name = "Sales" };
        var emp1 = new Employee { Name = "Alice", Department = sales };
        var emp2 = new Employee { Name = "Bob", Department = sales };
        var emp3 = new Employee { Name = "Carol", Department = sales };
        sales.Employees = new List<Employee> { emp1, emp2, emp3 };

        var dto = mapper.Map<DepartmentDto>(sales);

        Assert.Same(dto.Employees![0].Department, dto.Employees[1].Department);
        Assert.Same(dto.Employees[1].Department, dto.Employees[2].Department);
        Assert.Same(dto, dto.Employees[0].Department);
    }

    [Fact]
    public void SharedReference_TwoElementsInSameList_DedupedOnSecondOccurrence()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };

        // alice appears twice in the source list
        var src = new Person { Name = "Root", Friends = new List<Person> { alice, bob, alice } };

        var dto = mapper.Map<PersonDto>(src);

        Assert.Equal(3, dto.Friends!.Count);
        Assert.Equal("Alice", dto.Friends[0].Name);
        Assert.Equal("Bob", dto.Friends[1].Name);
        Assert.Same(dto.Friends[0], dto.Friends[2]);  // second alice deduped
    }

    [Fact]
    public void SharedReference_TwoCollectionsReferencingSameInstance_PreservesIdentity()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Group, GroupDto>().PreserveReferences();
            c.CreateMap<Person, PersonDto>();
        }).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var grp = new Group
        {
            Members = new List<Person> { alice },
            Admins = new List<Person> { alice }
        };

        var dto = mapper.Map<GroupDto>(grp);

        Assert.Same(dto.Members![0], dto.Admins![0]);  // alice deduped across two lists
    }

    [Fact]
    public void SharedReference_AcrossNestedAndOuterScope()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();
        }).CreateMapper();

        var dept = new Department { Name = "Engineering" };
        var emp = new Employee { Name = "Alice", Department = dept };
        dept.Employees = new List<Employee> { emp };

        var dto = mapper.Map<DepartmentDto>(dept);

        Assert.Same(dto, dto.Employees![0].Department);
    }

    // ─── Fresh-map allocation ───────────────────────────────────────────────────

    [Fact]
    public void Map_FreshSimpleCycle_ReturnsNewInstance_NotSourceReference()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.NotSame(alice, dto);
    }

    [Fact]
    public void Map_FreshGraph_AllPropertiesPopulatedCorrectly_DespiteCycle()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice", Age = 30 };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Equal(30, dto.Age);
        Assert.Same(dto, dto.Boss);
    }

    [Fact]
    public void Map_NullSource_ReturnsDefault_RegardlessOfPreserveReferences()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        Person? src = null;

        var dto = mapper.Map<Person?, PersonDto?>(src);

        Assert.Null(dto);
    }

    [Fact]
    public void Map_NullCycleField_LeavesDestinationCycleFieldNull()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice", Boss = null };

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Null(dto.Boss);
    }

    // ─── OFF path ───────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutPreserveReferences_SharedRef_NotDeduped()
    {
        // Confirms the v1 behavior is unchanged when flag is off: shared instances are NOT
        // deduplicated; each occurrence produces a distinct destination object.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();    // NO .PreserveReferences()
        var alice = new Person { Name = "Alice" };
        var src = new Person { Name = "Root", Friends = new List<Person> { alice, alice } };

        var dto = mapper.Map<PersonDto>(src);

        // Without PreserveReferences the two occurrences of alice map to two distinct dtos.
        Assert.NotSame(dto.Friends![0], dto.Friends[1]);
    }

    [Fact]
    public void WithoutPreserveReferences_NonCyclicGraph_StillMapsCorrectly()
    {
        // Verifies the cache preamble is a no-op when ctx is null (i.e., flag is off).
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();
        var alice = new Person { Name = "Alice", Age = 30 };

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Equal(30, dto.Age);
    }

    // ─── Multiple top-level calls ──────────────────────────────────────────────

    [Fact]
    public void MultipleTopLevelCalls_EachAllocatesFreshContext()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto1 = mapper.Map<PersonDto>(alice);
        var dto2 = mapper.Map<PersonDto>(alice);

        Assert.NotSame(dto1, dto2);  // each call gets a fresh context, fresh destination
        Assert.Same(dto1, dto1.Boss);
        Assert.Same(dto2, dto2.Boss);
    }

    // ─── Reference vs value-type sources ───────────────────────────────────────

    [Fact]
    public void ValueTypeSource_PreserveReferences_WorksWithoutCachePreamble()
    {
        // struct sources skip the cache preamble at codegen time. The flag is harmless.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<PointStruct, PointDto>().PreserveReferences()).CreateMapper();
        var p = new PointStruct { X = 1, Y = 2 };

        var dto = mapper.Map<PointDto>(p);

        Assert.Equal(1, dto.X);
        Assert.Equal(2, dto.Y);
    }

    [Fact]
    public void ReferenceTypeSource_CachePreambleEmittedForReferenceTypeSources()
    {
        // Sanity check on the codegen rule: class-typed sources get the cache preamble.
        // We verify behaviorally (cycle gets resolved) rather than via codegen inspection.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);  // would stack-overflow without cache

        Assert.Same(dto, dto.Boss);
    }

    // ─── Fresh-map with primitive properties ───────────────────────────────────

    [Fact]
    public void Map_PrimitiveProperties_PopulatedCorrectly_WithCycle()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice", Age = 30 };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Equal(30, dto.Age);
    }

    [Fact]
    public void Map_DeepGraphWithoutCycles_PreserveReferencesOff_StillWorks()
    {
        // Control: deep non-cyclic graph; no PreserveReferences; should map fine.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();
        var src = new Person
        {
            Name = "A",
            Boss = new Person { Name = "B", Boss = new Person { Name = "C" } }
        };

        var dto = mapper.Map<PersonDto>(src);

        Assert.Equal("A", dto.Name);
        Assert.Equal("B", dto.Boss!.Name);
        Assert.Equal("C", dto.Boss.Boss!.Name);
    }

    // ─── Test fixtures ──────────────────────────────────────────────────────────

    private sealed class Person
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public Person? Boss { get; set; }
        public List<Person>? Friends { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public PersonDto? Boss { get; set; }
        public List<PersonDto>? Friends { get; set; }
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
    }

    private sealed class EmployeeDto
    {
        public string? Name { get; set; }
        public DepartmentDto? Department { get; set; }
    }

    private sealed class Group
    {
        public List<Person>? Members { get; set; }
        public List<Person>? Admins { get; set; }
    }

    private sealed class GroupDto
    {
        public List<PersonDto>? Members { get; set; }
        public List<PersonDto>? Admins { get; set; }
    }

    private struct PointStruct
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    private sealed class PointDto
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
