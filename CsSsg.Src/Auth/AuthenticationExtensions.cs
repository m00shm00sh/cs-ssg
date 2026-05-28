using System.Collections.Frozen;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;

using CsSsg.Src.Db;
using CsSsg.Src.Post;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace CsSsg.Src.Auth;

internal static class AuthenticationExtensions
{
    // ReSharper disable once InconsistentNaming
    internal const string UID_CLAIM_NAME = JwtRegisteredClaimNames.Sub;
    
    private static readonly FrozenDictionary<RoleNamespace, string> TagMapF = new Dictionary<RoleNamespace, string>
    {
        { RoleNamespace.Search, "search" },
        { RoleNamespace.View, "read" },
        { RoleNamespace.Edit, "write" },
        { RoleNamespace.Special, "special" }
    }.ToFrozenDictionary();
    
    private static readonly FrozenDictionary<string, RoleNamespace> TagMapB =
        TagMapF.Select(kv => new KeyValuePair<string, RoleNamespace>(kv.Value, kv.Key)).ToFrozenDictionary();
    
    internal static bool IsTagValid(string tag)
        => !tag.Contains(':');
    
    internal static string CombineTags(IEnumerable<string> tags)
    => string.Join(":", tags);

    private static string[] SplitTags(string tags)
        => tags.Split(":");

    private static Claim[] EncodeRoles(params (RoleNamespace, string)[] roles)
        => EncodeRoles((IEnumerable<(RoleNamespace, string)>)roles);
    
    internal static Claim[] EncodeRoles(IEnumerable<(RoleNamespace, string)> roles)
    {
        var map = new Dictionary<RoleNamespace, List<string>>();
        foreach (var (ns, tag) in roles)
        {
            if (!map.TryAdd(ns, [tag]))
                map[ns].Add(tag);
        }
        return map.Select(kv => new Claim(TagMapF[kv.Key], CombineTags(kv.Value))).ToArray();
    }

    private static IEnumerable<(RoleNamespace, string)> DecodeRoles(ClaimsPrincipal principal,
        params RoleNamespace[] filters)
    {
        Predicate<Claim> matcher = filters.Length != 0
            ? c => TagMapB.TryGetValue(c.Type, out var tag) && filters.Contains(tag)
            : c => TagMapB.ContainsKey(c.Type);
        return principal.FindAll(matcher)
            .SelectMany(c => 
                SplitTags(c.Value)
                    .Select(v => (TagMapB[c.Type], v)));
    }

    // the null user lacks a subject claim but has read:[public unlisted] search:public claims
    private static ClaimsPrincipal CreateNullUser()
    {
        Claim[] claims = EncodeRoles(
            (RoleNamespace.Search, RepositoryExtensions.TAG_PUBLIC),
            (RoleNamespace.View, RepositoryExtensions.TAG_PUBLIC),
            (RoleNamespace.View, RepositoryExtensions.TAG_UNLISTED)
        );
        return new ClaimsPrincipal([
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme),
            new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme)
        ]);
    }

    internal static readonly ClaimsPrincipal NullUser = CreateNullUser();
    
    extension(ClaimsPrincipal? auth)
    {
        public bool TryGetUidAndSave(out Guid uid)
            => Guid.TryParse(auth?.FindFirstValue(UID_CLAIM_NAME), out uid);
        
        public Guid? TryGetUid()
        {
            if (auth.TryGetUidAndSave(out var uid))
                return uid;
            return null;
        }
        
        public Guid RequireUid()
            => auth?.TryGetUid()
               ?? throw new InvalidOperationException("valid uid not found (did you forget an authorization filter)");
        
        public IEnumerable<(RoleNamespace, string)> GetRoles(params RoleNamespace[] filters)
            => auth != null ? DecodeRoles(auth, filters) : [];
        
        public IEnumerable<string> GetRoles(RoleNamespace filter)
            => auth != null ? DecodeRoles(auth, filter).Select(t => t.Item2) : [];

        internal ClaimsPrincipal WithDifferentUserId(Guid newUid)
        {
            var identity = auth?.Identity as ClaimsIdentity;
            if (identity is null)
                throw new InvalidOperationException("auth?.Identity is not ClaimsIdentity");
            identity = identity.Clone();
            var uidClaim = identity.FindFirst(UID_CLAIM_NAME);
            if (uidClaim is null)
                throw new InvalidOperationException("missing UserId claim in identity");
            identity.RemoveClaim(uidClaim);
            identity.AddClaim(new Claim(uidClaim.Type, newUid.ToString()));
            return new ClaimsPrincipal(identity);
        }
    }

    extension(HttpContext ctx)
    {
        internal Task CreateSignedInUidCookie(User.RepositoryExtensions.UserClaims uc)
            => ctx.CreateSignedInUidCookie(uc.Id, uc.Roles);
        
        public Task CreateSignedInUidCookie(Guid uid, IEnumerable<(RoleNamespace, string)> roles)
        {
            var auth = new ClaimsPrincipal(
                new ClaimsIdentity([
                    new Claim(UID_CLAIM_NAME, uid.ToString()),
                    ..EncodeRoles(roles)
                ], CookieAuthenticationDefaults.AuthenticationScheme)
            );
            return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, auth);
        }
    }

    extension(WebApplicationBuilder builder)
    {
        // we need a no-op default authentication that short circuitedly fails when both cookies and jwt are registered
        public void AddDefaultForbid()
        {
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultChallengeScheme = "forbidScheme";
                options.DefaultForbidScheme = "forbidScheme";
                options.AddScheme<DefaultAuthenticationHandler>("forbidScheme", "Handle forbidden");
            });
        }
    }
}

file class DefaultAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    [Obsolete("Obsolete")]
    public DefaultAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, ISystemClock clock) 
        : base(options, logger, encoder, clock)
    { }
    
    // ReSharper disable once ConvertToPrimaryConstructor
    public DefaultAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, 
        UrlEncoder encoder) : base(options, logger, encoder)
    { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        => AuthenticateResult.NoResult();
}