using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class SessionAuth(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AppDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header[7..];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            return AuthenticateResult.Fail("Invalid session");
        var hash = Hash(token);
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
            return AuthenticateResult.Fail("Invalid session");
        var identity = new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim("session_hash", hash)
        }, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
public static class Access
{
    public static Guid User(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static bool Related(WorkItem task, Guid user) => task.AuthorId == user || task.AssigneeId == user;
}
