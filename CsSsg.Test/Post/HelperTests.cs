using CsSsg.Src.Db;
using CsSsg.Src.Post;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CsSsg.Test.Post;

public class HelperTests
{
    public static IList<object[]> FailureToResultTransformations =
    [
        [Failure.NotFound, typeof(NotFound)],
        [Failure.NotPermitted, typeof(ForbidHttpResult)],
        [Failure.Conflict, typeof(BadRequest)],
        [Failure.TooLong, typeof(BadRequest)]
    ];

    [Theory]
    [MemberData(nameof(FailureToResultTransformations))]
    public void CheckFailureToResultTransformations(object /* Failure */ fv, Type expectedType)
    {
        var f = (Failure)fv;
        var asResult = f.AsResult();
        Assert.Equal(expectedType, asResult.GetType());
    }
}