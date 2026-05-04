namespace Atlas.Tests;

public class MapperConfigurationExpressionValueTransformersTests
{
    [Fact]
    public void ValueTransformers_PropertyExposedAndAccessible()
    {
        var expr = new MapperConfigurationExpression();

        Assert.NotNull(expr.ValueTransformers);
    }

    [Fact]
    public void ValueTransformers_RegistrationsPersistOnExpression()
    {
        var expr = new MapperConfigurationExpression();

        expr.ValueTransformers.Add<string>(s => s.Trim());
        expr.ValueTransformers.Add<int>(i => i + 1);

        var all = expr.ValueTransformers.AllTransformers;
        Assert.Equal(2, all.Count);
        Assert.Single(all[typeof(string)]);
        Assert.Single(all[typeof(int)]);
    }
}
