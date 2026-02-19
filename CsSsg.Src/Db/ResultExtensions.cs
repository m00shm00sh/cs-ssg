// This file contains shared DB helpers not generated from EF Scaffolding.

using EntityFramework.Exceptions.Common;
using Microsoft.EntityFrameworkCore;

namespace CsSsg.Src.Db;

internal enum Failure
{
    /// entry not found
    NotFound = 1,
    /// entry found but permissions do not permit access
    NotPermitted,
    /// cannot create entry because it would cause a conflict
    Conflict,
    /// cannot create entry because a column failed length constraints
    TooLong,
}


internal static class DbContextExtensions
{
    extension(AppDbContext ctx)
    {
        /// Tries to commit context's changes to the DB, converting expected exceptions to Failure.
        /// Resolves to null on success.
        internal async Task<Failure?> TryToCommitChangesAsync(CancellationToken token)
        {
            try
            {
                await ctx.SaveChangesAsync(token);
                return null;
            }
            catch (DbUpdateException dbe)
            {
                var failVal = dbe switch
                {
                    // typically produced by inserting a post with an invalid author id
                    ReferenceConstraintException => Failure.NotPermitted,
                    // typically produced by email or title already existing
                    UniqueConstraintException => Failure.Conflict,
                    // typically produced by email or title being too long
                    MaxLengthExceededException => Failure.TooLong,
                    _ => default
                };
                if (failVal == default)
                    throw;
                return failVal;
            }
        }
    }
}
