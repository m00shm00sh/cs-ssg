using CsSsg.Src.SharedTypes;

using Microsoft.AspNetCore.Http.HttpResults;

namespace CsSsg.Test.SharedTypes;

public class TransformationTests
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