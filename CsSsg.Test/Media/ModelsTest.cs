using CsSsg.Test.StreamSupport;
using MObject = CsSsg.Src.Media.Object;
using static CsSsg.Src.Media.RoutingExtensions;

namespace CsSsg.Test.Media;

public class ModelsTest
{
#region MObject
    [Theory]
    [InlineData("aa#bb.cc", "aa-bb.cc")]
    [InlineData("aa.bb.cc", "aa-bb.cc")]
    public void VerifyMObject_SlugGeneration_MindsTheExtension(string input, string expected)
    {
        Assert.Equal(expected, SlugifyFilename(input));
    }

    [Fact]
    public void VerifyMObject_StreamChecks()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new MObject("a", new DummyStream(false, false, false))
        );
        Assert.Throws<InvalidOperationException>(() =>
            new MObject("a", new DummyStream(false, true, false))
        );
        Assert.Throws<InvalidOperationException>(() =>
            new MObject("a", new DummyStream(false, true, false))
        );
        var _ = new MObject("a", new DummyStream(true, true, false));
    }
#endregion
}