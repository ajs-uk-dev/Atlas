using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerNullSubstituteTests
{
    public class Animal { public string? Nickname { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public string Nickname { get; set; } = ""; }
    public class DogDto : AnimalDto { }

    [Fact]
    public void BaseNullSubstitute_PropagatesToDerived_WhenDerivedHasNoExplicit()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Nickname))!);
        Expression<Func<string>> baseSub = () => "Pet";
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Nickname))! });
        basePm.NullSubstitute = baseSub;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var derivedPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(AnimalDto.Nickname));
        Assert.Same(baseSub, derivedPm.NullSubstitute);
    }

    [Fact]
    public void DerivedExplicit_OverridesBaseExplicit_NullSubstitute()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Nickname))!);
        Expression<Func<string>> baseSub = () => "Pet";
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Nickname))! });
        basePm.NullSubstitute = baseSub;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);
        var derivedPm = PropertyMap.ForProperty(typeof(DogDto).GetProperty(nameof(DogDto.Nickname))!);
        Expression<Func<string>> derivedSub = () => "Rex";
        derivedPm.SourcePath = new SourceMemberPath(new[] { typeof(Dog).GetProperty(nameof(Dog.Nickname))! });
        derivedPm.NullSubstitute = derivedSub;
        derivedPm.IsExplicit = true;
        dogTm.PropertyMaps.Add(derivedPm);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var resultPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(DogDto.Nickname));
        Assert.Same(derivedSub, resultPm.NullSubstitute);
    }
}
