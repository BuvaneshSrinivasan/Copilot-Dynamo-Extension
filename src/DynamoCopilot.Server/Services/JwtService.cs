using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DynamoCopilot.Server.Models;
using Microsoft.IdentityModel.Tokens;

namespace DynamoCopilot.Server.Services;

public sealed class JwtService
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryHours;

    public JwtService(IConfiguration config)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException(
                "Jwt:Secret is not configured. Set the JWT__SECRET environment variable.");

        if (secret.Length < 32)
            throw new InvalidOperationException("Jwt:Secret must be at least 32 characters.");

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        _issuer = config["Jwt:Issuer"] ?? "DynamoCopilot";
        _audience = config["Jwt:Audience"] ?? "DynamoCopilot";
        _expiryHours = int.TryParse(config["Jwt:ExpiryHours"], out var h) ? h : 24;
    }

    public string CreateToken(User user)
    {
        var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);

        var claims = new Claim[]
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("tier", user.Tier.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_expiryHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
