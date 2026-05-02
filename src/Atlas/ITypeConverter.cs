namespace Atlas;

/// <summary>
/// Globally-registered conversion between two unrelated types. Once registered via
/// <c>CreateMap&lt;TSource, TDestination&gt;().ConvertUsing&lt;TConverter&gt;()</c>, the converter
/// is invoked anywhere the (TSource, TDestination) pair appears in a mapping.
/// </summary>
public interface ITypeConverter<in TSource, TDestination>
{
    TDestination Convert(TSource source, TDestination destination);
}
