using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Gateway.Web.Identity;

public sealed class JwtIssuer : IDisposable
{
    private readonly RSA rsa = RSA.Create();

    public JwtIssuer(IConfiguration config)
    {
        rsa.ImportFromPem(File.ReadAllText(config["Jwt:PrivateKeyPath"]
            ?? throw new InvalidOperationException("JWT private key path is required.")));
    }

    public string Issue(string userId, string name, IEnumerable<string> roles)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new("sub", userId), new("name", name), new("jti", Guid.NewGuid().ToString()) };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        var token = new JwtSecurityToken("tourlab-identity", "tourlab-api", claims, now, now.AddMinutes(15),
            new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose() => rsa.Dispose();
}
