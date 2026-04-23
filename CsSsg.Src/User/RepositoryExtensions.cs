using System.Diagnostics.CodeAnalysis;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using static Soenneker.Hashing.Argon2.Argon2HashingUtil;

using CsSsg.Src.Db;
using CsSsg.Src.SharedTypes;

namespace CsSsg.Src.User;

internal static class RepositoryExtensions
{
    extension(AppDbContext ctx)
    {
        /// <summary>
        /// Creates a new user for a given <see cref="Request"/>.
        /// </summary>
        /// <param name="request">user details</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the result of inserting, <see cref="Either"/> <see cref="Failure"/> or created <see cref="Guid"/>
        /// </returns>
        public async Task<Either<Failure, Guid>> CreateUserAsync(Request request, CancellationToken token)
        {
            var row = await request.ToDbRow();
            var validity = row.CheckValidity();
            if (validity is not null)
                return validity.Value;
            await ctx.Users.AddAsync(row, token);
            var result = await ctx.TryToCommitChangesAsync(token);
            return result.ToEither(row.Id).Swap();
        }

        /// <summary>
        /// Logs in a user, returning either the found UUID or failure.
        /// </summary>
        /// <param name="req">user details</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>
        ///     the result of login,
        ///     <see cref="Either"/> <see cref="Failure"/> or the authenticated user <see cref="Guid"/>
        /// </returns>
        public Task<Either<Failure, Guid>> LoginUserAsync(Request req, CancellationToken token)
            => ctx._doLoginUserAsync(req, token);

        /// Finds user id by email alone. <b>This bypasses password checking and must be treated with care.</b> 
        internal Task<Either<Failure, Guid>> FindUserByEmailAsync(string email, CancellationToken token)
            => ctx._doLoginUserAsync(new Request
            {
                Email = email,
                // ReSharper disable once NullableWarningSuppressionIsUsed
                Password = null!
            }, token, checkPassword: false);
        
        private async Task<Either<Failure, Guid>> _doLoginUserAsync(Request request, CancellationToken token,
            bool checkPassword = true)
        {
            var row = await ctx.Users
                .Where(u => u.Email == request.Email)
                .Select(u => new
                {
                    u.Id,
                    u.PassArgon2id
                })
                .SingleOrDefaultAsync(token);
            if (row is null)
                return Failure.NotFound;
            var hash = Argon2idHashedValue.FromHash(row.PassArgon2id);
            if (checkPassword && !await hash.VerifyPlaintext(request.Password))
                return Failure.NotPermitted;
            return row.Id;
        }

        /// <summary>
        /// Finds a <see cref="UserEntry"/> for a given <see cref="Guid"/>, if one exists.
        /// </summary>
        /// <param name="userId">user id to query</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>the <see cref="UserEntry"/>, if one exists, otherwise <c>None</c></returns>
        public async Task<Option<UserEntry>> FindEntryForUserAsync(Guid userId, CancellationToken token)
        {
            var row = await ctx.Users.FindAsync([userId], token);
            if (row is null)
                return Option<UserEntry>.None;
            return Option<UserEntry>.Some(new UserEntry
            {
                Email = row.Email,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt
            });
        }

        /// <summary>
        /// Updates user details for given user <see cref="Guid"/>.
        /// </summary>
        /// <param name="userId">user id to modify</param>
        /// <param name="newDetails">new user details</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public async Task<Option<Failure>> UpdateUserAsync(Guid userId, Request newDetails, CancellationToken token)
        {
            var newRow = await newDetails.ToDbRow();
            var validity = newRow.CheckValidity();
            if (validity is not null)
                return validity.Value;
            var row = await ctx.Users.FindAsync([userId], token);
            if (row is null)
                return Failure.NotFound;
            row.Email = newRow.Email;
            row.PassArgon2id = newRow.PassArgon2id;
            var result = await ctx.TryToCommitChangesAsync(token);
            return result;
        }

        /// <summary>
        /// Deletes the user associated with the user <see cref="Guid"/>.
        /// </summary>
        /// <param name="userId">user id to delete</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>a <see cref="Failure"/>, if any occurred, otherwise <c>None</c></returns>
        public async Task<Option<Failure>> DeleteUserAsync(Guid userId, CancellationToken token)
        {
            var row = await ctx.Users.FindAsync([userId], token);
            if (row is null)
                return Failure.NotFound;
            ctx.Users.Remove(row);
            var result = await ctx.TryToCommitChangesAsync(token);
            return result;
        }

        /// <summary>
        /// Checks if user can create new content.
        /// </summary>
        /// <param name="userId">user id to query</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>whether the user can create new content</returns>
        public ValueTask<bool> DoesUserHaveCreatePermissionAsync(Guid userId, CancellationToken token)
            // this will become more elaborate should actual roles be implemented
            => new(userId != Guid.Empty);
        
        /// <summary>
        /// Checks if user can create new media.
        /// </summary>
        /// <param name="userId">user id to query</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>whether the user can create new content</returns>
        public ValueTask<bool> DoesUserHaveCreateMediaPermissionAsync(Guid userId, CancellationToken token)
            // this will become more elaborate should actual roles be implemented
            => new(userId != Guid.Empty);

        /// <summary>
        /// Fetches user max upload size.
        /// </summary>
        /// <param name="userId">user id to query</param>
        /// <param name="token">async cancellation token</param>
        /// <returns>upload file limit</returns>
        public ValueTask<int> GetUserMediaUploadSizeLimitAsync(Guid userId, CancellationToken token)
            // this will become more elaborate should actual roles be implemented
            => new(50 * (1024 * 1024));
    }
}

// ReSharper disable once InconsistentNaming
file readonly struct Argon2idHashedValue
{
    internal string Value { get; }
    
    private Argon2idHashedValue(string hashedValue)
    {
        Value = hashedValue;
    }
    
    public static async Task<Argon2idHashedValue> FromPlaintext(string plainText)
        =>new(await Hash(plainText));

    public static Argon2idHashedValue FromHash(string hash)
        => new(hash);

    public async Task<bool> VerifyPlaintext(string plainText)
        => await Verify(plainText, Value);
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
file static class RepositoryExtensionsHelpers
{
    extension(Request req)
    {
        internal async Task<Src.Db.User> ToDbRow()
            => new()
            {
                Email = req.Email,
                PassArgon2id = (await Argon2idHashedValue.FromPlaintext(req.Password)).Value
            };
    }
    
    
    extension(Src.Db.User user)
    {
        internal Failure? CheckValidity()
        {
            if (user.Email.Length is < 1 or > USER_EMAIL_MAXLEN)
                return Failure.TooLong;
            // if this fails, the defaults for Argon2.Hash changed, which would be a problem
            if (user.PassArgon2id.Length != USER_PASS2ARGONID_EXPECTEDLEN)
                throw new InvalidOperationException("Unexpected password hash length.");
            return null;
        }
    }
    
    private const int USER_EMAIL_MAXLEN = 256;
    private const int USER_PASS2ARGONID_EXPECTEDLEN = 101;
}