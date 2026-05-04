using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class HookEntryTests
{
    [Fact]
    public void FromLambda_StoresLambdaAndNullActionType()
    {
        Action<int, int> hook = (s, d) => { };
        var entry = HookEntry.FromLambda(hook);

        Assert.Same(hook, entry.Lambda);
        Assert.Null(entry.ActionType);
    }

    [Fact]
    public void FromActionType_StoresActionTypeAndNullLambda()
    {
        var entry = HookEntry.FromActionType(typeof(string));

        Assert.Equal(typeof(string), entry.ActionType);
        Assert.Null(entry.Lambda);
    }

    [Fact]
    public void FromLambda_NullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HookEntry.FromLambda(null!));
    }

    [Fact]
    public void FromActionType_NullType_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HookEntry.FromActionType(null!));
    }
}
