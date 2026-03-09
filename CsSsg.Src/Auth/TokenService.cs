using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

using CsSsg.Src.Program;

namespace CsSsg.Src.Auth;

internal class TokenService
{
    private readonly string _issuer;
    private readonly SymmetricSecurityKey _key;

    private static (string, SymmetricSecurityKey) _fetchFromConfig(IConfiguration configuration)
        => (
            configuration.GetFromEnvironmentOrConfig("JWT_ISSUER", "Jwt:Issuer"),
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                configuration.GetFromEnvironmentOrConfig("JWT_SECRET", "Jwt:Secret")
            ))
        );

    public static TokenValidationParameters MakeJwtValidationParameters(IConfiguration configuration)
    {
        var (issuer, key) = _fetchFromConfig(configuration);
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            IssuerSigningKey = key,
            NameClaimType = "name",
            RoleClaimType = "roles"
        };
    }

    public TokenService(IConfiguration configuration)
    {
        (_issuer, _key) = _fetchFromConfig(configuration);
    }

    public string GenerateToken(Guid userId)
    {
        var uidString = userId.ToString();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, uidString),
        };
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}