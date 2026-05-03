namespace Atlas.Tests;

public class AtlasMappingExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_PreservesMessage()
    {
        var ex = new AtlasMappingException("source value is undefined");
        Assert.Equal("source value is undefined", ex.Message);
    }

    [Fact]
    public void IsAssignableTo_Exception_ForCatchHandling()
    {
        var ex = new AtlasMappingException("any message");
        Assert.IsAssignableFrom<Exception>(ex);
    }
}
