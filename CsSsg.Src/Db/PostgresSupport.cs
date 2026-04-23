using System.Data;
using Npgsql;

using CsSsg.Src.SharedTypes;
using Microsoft.EntityFrameworkCore;

namespace CsSsg.Src.Db;

internal static class PostgresSupportExtensions
{
    extension(NpgsqlException e)
    {
        // converts Npgsql ADO.NET exceptions to Failure codes
        internal Failure AsFailure()
            => e.SqlState switch
            {
                PostgresErrorCodes.ForeignKeyViolation => Failure.NotPermitted,
                PostgresErrorCodes.UniqueViolation => Failure.Conflict,
                PostgresErrorCodes.StringDataRightTruncation => Failure.TooLong,
                _ => default
            };
    }
    
    extension(AppDbContext ctx)
    {
      
        /// <summary>
        /// Gets the underlying Postgres connection from an EF context.<br/>
        /// <em>Do not use <c>await using</c> because EF takes care of disposal semantics</em>.
        /// </summary>
        /// <param name="token">cancellation token</param>
        /// <returns>the underlying postgres connection</returns>
        internal async Task<NpgsqlConnection> GetPostgresConnectionAsync(CancellationToken token)
        {
            var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(token);
            return conn;
        }
    }
}
