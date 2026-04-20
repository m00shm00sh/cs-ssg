using Microsoft.AspNetCore.Http.HttpResults;

namespace CsSsg.Src.SharedTypes;

internal enum Failure
{
    /// entry not found
    NotFound = 1,
    /// entry found but permissions do not permit access
    NotPermitted,
    /// cannot create entry because it would cause a conflict
    Conflict,
    /// cannot create entry because a column failed length constraints
    TooLong
}

internal static class FailureExtensions
{
    // Due to https://github.com/dotnet/roslyn/issues/81180 , extension(Failure) { internal ... } doesn't work in 
    // .NET < 10.0.200, which affects unit tests
    ///<summary>
    /// Converts a Failure code to a status-code-only IResult.
    /// </summary>
    /// <returns>
    /// <list>
    ///     <item>a <see cref="NotFound"/> for NotFound</item>
    ///     <item>a <see cref="ForbidHttpResult"/> for NotPermitted</item>
    ///     <item>a <see cref="BadRequest"/> for conflict or length result</item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">when Failure value is unhandled</exception>
    internal static IResult AsResult(this Failure f) 
        => f switch
        {
            Failure.NotFound =>
                TypedResults.NotFound(),
            Failure.NotPermitted =>
                TypedResults.Forbid(),
            Failure.Conflict or
                Failure.TooLong => 
                // a Results.UnprocessableEntity would also do here since it's a validation failure
                TypedResults.BadRequest(),
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, null)
        }; 
}