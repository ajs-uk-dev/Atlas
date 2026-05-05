using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerConditionTests
{
    public class Animal { public int Legs { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public int Legs { get; set; } }
    public class DogDto : AnimalDto { }

    [Fact]
    public void BasePreCondition_PropagatesToDerived_WhenDerivedHasNoExplicit()
    {
        // Base: explicit ForMember on Legs with PreCondition.
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Legs))!);
        Expression<Func<Animal, bool>> basePre = s => s.Legs > 0;
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Legs))! });
        basePm.PreCondition = basePre;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        // Derived: no PropertyMap for Legs yet (will get one via merger).
        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var derivedPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(AnimalDto.Legs));
        Assert.Same(basePre, derivedPm.PreCondition);
    }

    [Fact]
    public void BaseCondition_PropagatesToDerived_WhenDerivedHasNoExplicit()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Legs))!);
        Expression<Func<Animal, int, bool>> baseCond = (s, v) => v < 100;
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Legs))! });
        basePm.Condition = baseCond;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var derivedPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(AnimalDto.Legs));
        Assert.Same(baseCond, derivedPm.Condition);
    }

    [Fact]
    public void DerivedExplicit_OverridesBaseExplicit_ForBothPredicates()
    {
        // Base sets both predicates.
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Legs))!);
        Expression<Func<Animal, bool>> basePre = s => s.Legs > 0;
        Expression<Func<Animal, int, bool>> baseCond = (s, v) => v < 100;
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Legs))! });
        basePm.PreCondition = basePre;
        basePm.Condition = baseCond;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        // Derived sets its own predicates (IsExplicit = true wins).
        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);
        var derivedPm = PropertyMap.ForProperty(typeof(DogDto).GetProperty(nameof(DogDto.Legs))!);
        Expression<Func<Dog, bool>> derivedPre = s => s.Legs > 2;
        Expression<Func<Dog, int, bool>> derivedCond = (s, v) => v == 4;
        derivedPm.SourcePath = new SourceMemberPath(new[] { typeof(Dog).GetProperty(nameof(Dog.Legs))! });
        derivedPm.PreCondition = derivedPre;
        derivedPm.Condition = derivedCond;
        derivedPm.IsExplicit = true;
        dogTm.PropertyMaps.Add(derivedPm);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var resultPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(DogDto.Legs));
        Assert.Same(derivedPre, resultPm.PreCondition);
        Assert.Same(derivedCond, resultPm.Condition);
    }
}
