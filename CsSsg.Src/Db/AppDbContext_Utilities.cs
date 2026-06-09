using EntityFramework.Exceptions.Common;
using EntityFramework.Exceptions.PostgreSQL;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Npgsql;

using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.Db;

public partial class AppDbContext
{

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseExceptionProcessor();
    }

    /// Tries to commit context's changes to the DB, converting expected exceptions to Failure.
    /// Resolves to None on success.
    /// <remarks>Unexpected exceptions still resolve to thrown exception.</remarks>
    internal async Task<Option<Failure>> TryToCommitChangesAsync(CancellationToken token)
    {
        try
        {
            await SaveChangesAsync(token);
            return Option<Failure>.None;
        }
        catch (DbUpdateException dbe)
        {
            var failVal = ConvertDbUpdateExceptionToFailure(dbe);
            if (failVal == default)
                throw;
            return failVal;
        }
    }
    
    /// Tries to execute an operation inside a transaction, converting expected exceptions or exception-wrapped Failure
    /// to Failure.
    /// The operation is expected to retry on transient failures, if the context is configured with a retry strategy.
    /// Resolves to None on success.
    /// <remarks>Unexpected exceptions still resolve to thrown exception.</remarks>
    internal Task<Option<Failure>> ExecuteFailableTransactionAsync(Func<CancellationToken, Task> operation, 
        CancellationToken token)
        => ExecuteFailableTransactionAsync(operation, _ => Task.FromResult(true), token);
    
    /// Tries to execute an operation inside a transaction, converting expected exceptions or exception-wrapped Failure
    /// to Failure.
    /// The operation is expected to retry on transient failures, if the context is configured with a retry strategy.
    /// A verification function is used to verify that a commit succeeded.
    /// Resolves to None on success.
    /// <remarks>Unexpected exceptions still resolve to thrown exception.</remarks>
    internal async Task<Option<Failure>> ExecuteFailableTransactionAsync(Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task<bool>> verify, CancellationToken token)
    {
        // the EF tutorial on resilient transaction creates a strategy then passes it the action
        // https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#execution-strategies-and-transactions
        // https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue
        var strategy = Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(operation, verify, token);
            return Option<Failure>.None;
        }
        catch (FailureException fEx)
        {
            return fEx.Code;
        }
        catch (PostgresException pgEx)
        {
            var failVal = pgEx.AsFailure();
            if (failVal != default)
                throw;
            return failVal;
        }
    }
    
    private static Failure ConvertDbUpdateExceptionToFailure(DbUpdateException ex)
        => ex switch
        {
            CannotInsertNullException => Failure.NotPermitted,   
            // typically produced by inserting a post with an invalid author id
            ReferenceConstraintException => Failure.NotPermitted,
            // typically produced by email or title already existing
            UniqueConstraintException => Failure.Conflict,
            // typically produced by email or title being too long
            MaxLengthExceededException => Failure.TooLong,
            NumericOverflowException => Failure.TooLong,
            // DeadlockException => Failure.Conflict,
            _ => default
        };
}
