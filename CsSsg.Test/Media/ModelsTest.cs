using CsSsg.Src.Media;
using CsSsg.Test.StreamSupport;
using MObject = CsSsg.Src.Media.Object;

namespace CsSsg.Test.Media;

public class ModelsTest
{
#region MObject
    [Theory]
    [InlineData("aa#bb.cc", "aa-bb.cc")]
    [InlineData("aa.bb.cc", "aa-bb.cc")]
    public void VerifyMObject_SlugGeneration_MindsTheExtension(string input, string expected)
    {
        Assert.Equal(expected, Entry.SlugifyFilename(input));
    }

    [Fact]
    public void VerifyMObject_StreamChecks()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new MObject("a", new DummyStream(false, false, false))
        );
        var _ = new MObject("a", new DummyStream(true, false, false));
    }
#endregion
}