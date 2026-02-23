using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OneOf;
using static Soenneker.Hashing.Argon2.Argon2HashingUtil;

using CsSsg.Src.Db;

namespace CsSsg.Src.User;

// we need the type reminders for OneOf<T...>.(Match|Switch)(Func<T, R>...)
[SuppressMessage("ReSharper", "RedundantLambdaParameterType")]
internal static class RepositoryExtensions
{
    extension(AppDbContext ctx)
    {
        /// Creates a new user, returning either the new UUID or the Failure.
        public async Task<OneOf<Guid, Failure>> CreateUserAsync(Request request, CancellationToken token)
        {
            var row = await request.ToDbRow();
            var validity = row.CheckValidity();
            if (validity is not null)
                return validity.Value;
            await ctx.Users.AddAsync(row, token);
            var result = await ctx.TryToCommitChangesAsync(token);
            return result is null ? row.Id : result.Value;
        }

        /// Logs in a user, returning either the found UUID or failure.
        public Task<OneOf<Guid, Failure>> LoginUserAsync(Request req, CancellationToken token)
            => ctx._doLoginUserAsync(req, token);

        /// Finds user id by email alone. <b>This bypasses password checking and must be treated with care.</b> 
        internal Task<OneOf<Guid, Failure>> FindUserByEmailAsync(string email, CancellationToken token)
            => ctx._doLoginUserAsync(new Request
            {
                Email = email,
                // ReSharper disable once NullableWarningSuppressionIsUsed
                Password = null!
            }, token, checkPassword: false);
        
        private async Task<OneOf<Guid, Failure>> _doLoginUserAsync(Request request, CancellationToken token,
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

        public async Task<string?> FindEmailForUserAsync(Guid userId, CancellationToken token)
            => (await ctx.Users.FindAsync([userId], token))?.Email;

        /// Updates user details for userId. Returns null on success.
        public async Task<Failure?> UpdateUserAsync(Guid userId, Request newDetails, CancellationToken token)
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

        /// Deletes the user associated with the id. Returns null on success and failure value otherwise.
        public async Task<Failure?> DeleteUserAsync(Guid userId, CancellationToken token)
        {
            var row = await ctx.Users.FindAsync([userId], token);
            if (row is null)
                return Failure.NotFound;
            ctx.Users.Remove(row);
            var result = await ctx.TryToCommitChangesAsync(token);
            return result;
        }

        /// Checks if user can create new content.
        public ValueTask<bool> DoesUserHaveCreatePermissionAsync(Guid userId, CancellationToken token)
            => new(true);
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