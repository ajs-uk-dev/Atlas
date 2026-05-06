using System.Collections.Generic;
using Atlas;
using Atlas.Internal;

namespace Atlas.Tests;

public class MapperPreserveReferencesPropagationTests
{
    [Fact]
    public void Inheritance_BasePreserveReferences_PropagatesToDerivedViaInclude()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>().PreserveReferences().Include<Manager, ManagerDto>();
            c.CreateMap<Manager, ManagerDto>();
        });

        var derivedTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Manager), typeof(ManagerDto)));
        Assert.NotNull(derivedTm);
        Assert.True(derivedTm!.PreserveReferences);
    }

    [Fact]
    public void Inheritance_BaseWithoutPreserveReferences_DoesNotForcePreserveReferencesOnDerived()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>().Include<Manager, ManagerDto>();   // no PR
            c.CreateMap<Manager, ManagerDto>();
        });

        var derivedTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Manager), typeof(ManagerDto)));
        Assert.NotNull(derivedTm);
        Assert.False(derivedTm!.PreserveReferences);
    }

    [Fact]
    public void ReverseMap_PropagatesPreserveReferencesToReversePair()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences().ReverseMap());

        var forwardTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Person), typeof(PersonDto)));
        var reverseTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(PersonDto), typeof(Person)));
        Assert.NotNull(forwardTm);
        Assert.NotNull(reverseTm);
        Assert.True(forwardTm!.PreserveReferences);
        Assert.True(reverseTm!.PreserveReferences);
    }

    [Fact]
    public void ReverseMap_OrderIndependent_PreserveReferencesAfterReverseMap_PropagatesToReverse()
    {
        // Verify the fix for holistic-review I-1: PreserveReferences() should propagate to the reverse
        // pair regardless of fluent-call ordering.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().ReverseMap().PreserveReferences());

        var forwardTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Person), typeof(PersonDto)));
        var reverseTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(PersonDto), typeof(Person)));
        Assert.NotNull(forwardTm);
        Assert.NotNull(reverseTm);
        Assert.True(forwardTm!.PreserveReferences);
        Assert.True(reverseTm!.PreserveReferences,
            "Reverse pair should inherit PreserveReferences regardless of fluent-call ordering. " +
            "If this fails, the fix in MappingExpression.PreserveReferences() to propagate to " +
            "CachedReverseExpression's TypeMap is missing.");
    }

    [Fact]
    public void OpenGeneric_PropagatesFromTemplateToClosedMaterialization()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)).PreserveReferences());

        var mapper = cfg.CreateMapper();
        // Trigger materialization by performing a map call:
        var src = new Wrapper<int> { Value = 42 };
        var dst = mapper.Map<Wrapper<int>, WrapperDto<int>>(src);

        var closedTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)));
        Assert.NotNull(closedTm);
        Assert.True(closedTm!.PreserveReferences);
    }

    [Fact]
    public void DownPropagation_OuterFlagged_InnerNotFlagged_InnerStillCacheActive()
    {
        // Department has PR; Employee does not. Cycle inside Employee subgraph still resolves
        // because the runtime ctx is propagated from outer Department call.
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();   // NOT flagged
        }).CreateMapper();

        var dept = new Department { Name = "Engineering" };
        var alice = new Employee { Name = "Alice", Department = dept };
        var bob = new Employee { Name = "Bob", Department = dept, Manager = null };
        alice.Manager = bob;
        bob.Manager = alice;   // cycle inside Employee subgraph
        dept.Employees = new List<Employee> { alice, bob };

        var dto = mapper.Map<DepartmentDto>(dept);

        Assert.Same(dto.Employees![0], dto.Employees[1].Manager);  // bob.Manager == alice (same instance)
        Assert.Same(dto.Employees![1], dto.Employees[0].Manager);  // alice.Manager == bob (same instance)
    }

    [Fact]
    public void DownPropagation_OuterNotFlagged_InnerFlagged_NoCacheActive()
    {
        // Outer Department NOT flagged, inner Employee flagged. When outer is not flagged,
        // no context is allocated. The shared inner Department (sharedDept) is duplicated
        // across the two EmployeeDto destinations, proving no cache is active.
        //
        // Note: alice and bob point to sharedDept (a DIFFERENT department from the root),
        // so there is no back-reference cycle that could stack-overflow.
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>();   // NOT flagged
            c.CreateMap<Employee, EmployeeDto>().PreserveReferences();
        }).CreateMapper();

        var rootDept = new Department { Name = "Root" };
        var sharedDept = new Department { Name = "Shared" };   // shared by both employees
        var alice = new Employee { Name = "Alice", Department = sharedDept };
        var bob = new Employee { Name = "Bob", Department = sharedDept };
        rootDept.Employees = new List<Employee> { alice, bob };

        var dto = mapper.Map<DepartmentDto>(rootDept);

        // Without outer PR, the cache is not allocated → sharedDept is mapped twice.
        // Use NotSame to verify duplication (proving cache is OFF).
        Assert.NotSame(dto.Employees![0].Department, dto.Employees[1].Department);
    }

    [Fact]
    public void DownPropagation_InnerCallExplicitly_AllocatesContextWhenInnerFlagged()
    {
        // Calling mapper.Map<EmployeeDto>(alice) directly → outer typemap is Employee → flagged →
        // context allocated → cycles resolved.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Employee, EmployeeDto>().PreserveReferences()).CreateMapper();

        var alice = new Employee { Name = "Alice" };
        alice.Manager = alice;

        var dto = mapper.Map<EmployeeDto>(alice);

        Assert.Same(dto, dto.Manager);
    }

    [Fact]
    public void Hooks_FireOnFirstAllocation_NotOnCacheHit()
    {
        var beforeCount = 0;
        var afterCount = 0;
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .BeforeMap((s, d) => beforeCount++)
                .AfterMap((s, d) => afterCount++);
        }).CreateMapper();

        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map<PersonDto>(alice);

        // alice appears twice in the call graph (top-level + as Boss), but hooks should fire only ONCE.
        Assert.Equal(1, beforeCount);
        Assert.Equal(1, afterCount);
    }

    [Fact]
    public void ValueTransformers_ApplyToResult_TransformedCorrectly()
    {
        // Verifies that value transformers run and produce the correct output when PreserveReferences
        // is active. The transformer uppercases the Name. The cycle (alice.Boss = alice) means the
        // destination is cache-hit on the second traversal, so the transformer fires exactly once —
        // verified by the final dto.Name value being "ALICE" (not double-transformed, which ToUpper
        // is idempotent for, but Boss.Name is the same dto so it also equals "ALICE").
        var mapper = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.ToUpper());
            c.CreateMap<Person, PersonDto>().PreserveReferences();
        }).CreateMapper();

        var alice = new Person { Name = "alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("ALICE", dto.Name);
        Assert.Same(dto, dto.Boss);  // cycle resolved — Boss is the same instance as dto
        Assert.Equal("ALICE", dto.Boss!.Name);  // same instance, same Name
    }

    // ─── Test fixtures ──────────────────────────────────────────────────────────

    private class Person
    {
        public string? Name { get; set; }
        public Person? Boss { get; set; }
    }

    private class PersonDto
    {
        public string? Name { get; set; }
        public PersonDto? Boss { get; set; }
    }

    private class Manager : Person
    {
        public string? Department { get; set; }
    }

    private class ManagerDto : PersonDto
    {
        public string? Department { get; set; }
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

    private sealed class Wrapper<T> { public T Value { get; set; } = default!; }
    private sealed class WrapperDto<T> { public T Value { get; set; } = default!; }
}
