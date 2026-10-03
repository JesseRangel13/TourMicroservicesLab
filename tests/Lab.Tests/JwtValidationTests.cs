using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Shared.Infrastructure;

namespace Lab.Tests;

public sealed class JwtValidationTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("signature")]
    [InlineData("algorithm")]
    public void ValidatorEnforcesTrustAndLifetime(string scenario)
    {
        using var trusted = RSA.Create(2048);
        using var untrusted = RSA.Create(2048);
        var key = new RsaSecurityKey(scenario == "signature" ? untrusted : trusted);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            scenario == "issuer" ? "untrusted" : "tourlab-identity",
            scenario == "audience" ? "wrong-api" : "tourlab-api",
            [new Claim("sub", "alice-id"), new Claim("role", "Tourist")],
            scenario == "future" ? now.AddMinutes(1) : now.AddMinutes(-10),
            scenario == "expired" ? now.AddSeconds(-1) : now.AddMinutes(15),
            new SigningCredentials(key, scenario == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var encoded = handler.WriteToken(token);
        if (scenario == "valid")
        {
            var principal = handler.ValidateToken(encoded, JwtValidation.Create(new RsaSecurityKey(trusted)), out _);
            Assert.Equal("alice-id", principal.FindFirst("sub")?.Value);
            Assert.True(principal.IsInRole("Tourist"));
            Assert.False(principal.IsInRole("Admin"));
        }
        else Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(encoded, JwtValidation.Create(new RsaSecurityKey(trusted)), out _));
    }
}
