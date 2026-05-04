namespace Atlas.Internal;

/// <summary>
/// One BeforeMap or AfterMap registration. Exactly one of <see cref="Lambda"/> or
/// <see cref="ActionType"/> is non-null. Lambda entries store the user's
/// <c>Action&lt;TSource, TDestination&gt;</c> directly; ActionType entries reference an
/// <see cref="IMappingAction{TSource, TDestination}"/> implementation type to be instantiated
/// at config-build time by <c>HookResolver</c>.
/// </summary>
internal sealed record HookEntry(Delegate? Lambda, Type? ActionType)
{
    public static HookEntry FromLambda(Delegate lambda) =>
        new(lambda ?? throw new ArgumentNullException(nameof(lambda)), null);

    public static HookEntry FromActionType(Type actionType) =>
        new(null, actionType ?? throw new ArgumentNullException(nameof(actionType)));
}
