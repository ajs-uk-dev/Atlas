using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerHookTests
{
    public class LivingThing { }
    public class Animal : LivingThing { }
    public class Cat : Animal { }
    public class LivingThingDto { }
    public class AnimalDto : LivingThingDto { }
    public class CatDto : AnimalDto { }
    public class Dog : Animal { }
    public class DogDto : AnimalDto { }

    [Fact]
    public void Merge_OneLevel_BeforeHooks_PrependsBase()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        Action<Animal, AnimalDto> A_Before = (s, d) => { };
        animalTm.BeforeHooks.Add(HookEntry.FromLambda(A_Before));

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_Before = (s, d) => { };
        catTm.BeforeHooks.Add(HookEntry.FromLambda(C_Before));

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        Assert.Equal(2, catTm.BeforeHooks.Count);
        Assert.Same(A_Before, catTm.BeforeHooks[0].Lambda);
        Assert.Same(C_Before, catTm.BeforeHooks[1].Lambda);
    }

    [Fact]
    public void Merge_OneLevel_AfterHooks_AppendsBase()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        Action<Animal, AnimalDto> A_After = (s, d) => { };
        animalTm.AfterHooks.Add(HookEntry.FromLambda(A_After));

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_After = (s, d) => { };
        catTm.AfterHooks.Add(HookEntry.FromLambda(C_After));

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        Assert.Equal(2, catTm.AfterHooks.Count);
        Assert.Same(C_After, catTm.AfterHooks[0].Lambda);
        Assert.Same(A_After, catTm.AfterHooks[1].Lambda);
    }

    [Fact]
    public void Merge_ThreeLevelChain_BaseFirstOrder()
    {
        // LivingThing → Animal → Cat
        var ltTm = new TypeMap(typeof(LivingThing), typeof(LivingThingDto), MemberList.None);
        Action<LivingThing, LivingThingDto> LT_B = (s, d) => { };
        Action<LivingThing, LivingThingDto> LT_A = (s, d) => { };
        ltTm.BeforeHooks.Add(HookEntry.FromLambda(LT_B));
        ltTm.AfterHooks.Add(HookEntry.FromLambda(LT_A));

        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        animalTm.IncludedBases.Add(ltTm.Pair);
        Action<Animal, AnimalDto> A_B = (s, d) => { };
        Action<Animal, AnimalDto> A_A = (s, d) => { };
        animalTm.BeforeHooks.Add(HookEntry.FromLambda(A_B));
        animalTm.AfterHooks.Add(HookEntry.FromLambda(A_A));

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_B = (s, d) => { };
        Action<Cat, CatDto> C_A = (s, d) => { };
        catTm.BeforeHooks.Add(HookEntry.FromLambda(C_B));
        catTm.AfterHooks.Add(HookEntry.FromLambda(C_A));

        var typeMaps = new List<TypeMap> { ltTm, animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        // Expected: Cat.BeforeHooks = [LT_B, A_B, C_B]
        Assert.Equal(3, catTm.BeforeHooks.Count);
        Assert.Same(LT_B, catTm.BeforeHooks[0].Lambda);
        Assert.Same(A_B, catTm.BeforeHooks[1].Lambda);
        Assert.Same(C_B, catTm.BeforeHooks[2].Lambda);

        // Expected: Cat.AfterHooks = [C_A, A_A, LT_A]
        Assert.Equal(3, catTm.AfterHooks.Count);
        Assert.Same(C_A, catTm.AfterHooks[0].Lambda);
        Assert.Same(A_A, catTm.AfterHooks[1].Lambda);
        Assert.Same(LT_A, catTm.AfterHooks[2].Lambda);
    }

    [Fact]
    public void Merge_DerivedOnly_NoBaseHooks_Unchanged()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        // No base hooks.

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_B = (s, d) => { };
        Action<Cat, CatDto> C_A = (s, d) => { };
        catTm.BeforeHooks.Add(HookEntry.FromLambda(C_B));
        catTm.AfterHooks.Add(HookEntry.FromLambda(C_A));

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        Assert.Single(catTm.BeforeHooks);
        Assert.Same(C_B, catTm.BeforeHooks[0].Lambda);
        Assert.Single(catTm.AfterHooks);
        Assert.Same(C_A, catTm.AfterHooks[0].Lambda);
    }

    [Fact]
    public void Merge_PreservesPropertyMapMergeBehavior()
    {
        // Regression: existing PropertyMap merge still works after the hook merge addition.
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        // Hooks empty on both — verify nothing leaks.
        Assert.Empty(animalTm.BeforeHooks);
        Assert.Empty(animalTm.AfterHooks);
        Assert.Empty(catTm.BeforeHooks);
        Assert.Empty(catTm.AfterHooks);
    }
}
