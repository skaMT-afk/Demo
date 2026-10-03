using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
var connection = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Default before starting.");
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(connection));
builder.Services.AddScoped<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210000);
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuth>("Session", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
// EnsureCreated is only for this disposable EK1 development database; see D-03.
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreatedAsync();
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
    try { await next(context); }
    catch (DbUpdateConcurrencyException) {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "concurrent_change" });
    }
    catch (BadHttpRequestException e) {
        context.Response.StatusCode = e.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
    }
    catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" }) {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "already_exists" });
    }
    catch (Exception e) {
        // Never log payloads, credentials, connection strings or exception text.
        app.Logger.LogError("Request failed: {ExceptionType}; trace {Trace}", e.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "internal_error", traceId = context.TraceIdentifier });
    }
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", () => Results.Ok(new { name = "TaskFlow EK1", health = "/health", api = "/api" }));
app.MapGet("/health", async Task<IResult> (AppDb db) => await db.Database.CanConnectAsync()
    ? Results.Ok(new { status = "ok", database = "reachable" })
    : Results.StatusCode(503));
app.MapPost("/api/auth/register", async Task<IResult> (Credentials input, AppDb db, IPasswordHasher<Account> hasher) => {
    if (input.Login is null || !Regex.IsMatch(input.Login, @"\A[a-zA-Z0-9_]{3,32}\z") ||
        input.Password is null || input.Password.Length < 12 || input.Password.Length > 128)
        return Results.BadRequest(new { error = "login_3_32_ascii_password_12_128" });
    var user = new Account { Login = input.Login.ToLowerInvariant() };
    user.PasswordHash = hasher.HashPassword(user, input.Password);
    db.Accounts.Add(user);
    await db.SaveChangesAsync();
    return Results.Created($"/api/me", new { user.Id, user.Login });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async Task<IResult> (Credentials input, AppDb db, IPasswordHasher<Account> hasher) => {
    if (input.Login is null || input.Login.Length > 32 || input.Password is null || input.Password.Length > 128)
        return Results.Unauthorized();
    var login = input.Login.ToLowerInvariant();
    var user = await db.Accounts.SingleOrDefaultAsync(x => x.Login == login);
    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var session = new Session { TokenHash = SessionAuth.Hash(token), UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
    db.Sessions.Add(session);
    await db.SaveChangesAsync();
    return Results.Ok(new { accessToken = token, tokenType = "Bearer", expiresAt = session.ExpiresAt });
}).RequireRateLimiting("auth");
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/me", async Task<IResult> (ClaimsPrincipal principal, AppDb db) => {
    var id = Access.User(principal);
    var user = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == id);
    return Results.Ok(new { user.Id, user.Login });
});
api.MapPost("/auth/logout", async Task<IResult> (ClaimsPrincipal principal, AppDb db) => {
    var hash = principal.FindFirstValue("session_hash");
    await db.Sessions.Where(x => x.TokenHash == hash).ExecuteDeleteAsync();
    return Results.NoContent();
});
api.MapPost("/teams", async Task<IResult> (NewTeam input, ClaimsPrincipal principal, AppDb db) => {
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100) return Results.BadRequest();
    var team = new Team { Name = input.Name.Trim(), OwnerId = Access.User(principal) };
    db.Teams.Add(team);
    db.Members.Add(new Member { TeamId = team.Id, UserId = team.OwnerId });
    await db.SaveChangesAsync();
    return Results.Created($"/api/teams/{team.Id}", team);
});
api.MapGet("/teams/{id:guid}", async Task<IResult> (Guid id, ClaimsPrincipal principal, AppDb db) => {
    var uid = Access.User(principal);
    if (!await db.Members.AnyAsync(x => x.TeamId == id && x.UserId == uid)) return Results.NotFound();
    return Results.Ok(new { team = await db.Teams.FindAsync(id),
        members = await db.Members.Where(x => x.TeamId == id).Select(x => x.UserId).ToListAsync() });
});
api.MapPost("/teams/{id:guid}/members", async Task<IResult> (Guid id, NewMember input, ClaimsPrincipal principal, AppDb db) => {
    var uid = Access.User(principal);
    var team = await db.Teams.FindAsync(id);
    if (team is null || !await db.Members.AnyAsync(x => x.TeamId == id && x.UserId == uid)) return Results.NotFound();
    if (team.OwnerId != uid) return Results.Forbid();
    if (!await db.Accounts.AnyAsync(x => x.Id == input.UserId)) return Results.BadRequest();
    db.Members.Add(new Member { TeamId = id, UserId = input.UserId });
    await db.SaveChangesAsync();
    return Results.NoContent();
});
api.MapPost("/teams/{id:guid}/tasks", async Task<IResult> (Guid id, NewTask input, ClaimsPrincipal principal, AppDb db) => {
    var uid = Access.User(principal);
    if (!await db.Members.AnyAsync(x => x.TeamId == id && x.UserId == uid)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200 || input.Description is null || input.Description.Length > 4000)
        return Results.BadRequest();
    if (input.AssigneeId == uid || !await db.Members.AnyAsync(x => x.TeamId == id && x.UserId == input.AssigneeId))
        return Results.BadRequest(new { error = "assignee_must_be_another_team_member" });
    var task = new WorkItem { TeamId = id, AuthorId = uid, AssigneeId = input.AssigneeId,
        Title = input.Title.Trim(), Description = input.Description };
    db.Tasks.Add(task);
    await db.SaveChangesAsync();
    return Results.Created($"/api/tasks/{task.Id}", task);
});
api.MapGet("/tasks", async Task<IResult> (ClaimsPrincipal principal, AppDb db) => {
    var uid = Access.User(principal);
    return Results.Ok(await db.Tasks.AsNoTracking()
        .Where(x => (x.AuthorId == uid || x.AssigneeId == uid) &&
            db.Members.Any(m => m.TeamId == x.TeamId && m.UserId == uid))
        .OrderBy(x => x.Id).Take(100).ToListAsync());
});
api.MapGet("/tasks/{id:guid}", async Task<IResult> (Guid id, ClaimsPrincipal principal, AppDb db) => {
    var task = await FindVisible(id, Access.User(principal), db);
    return task is null ? Results.NotFound() : Results.Ok(task);
});
api.MapPost("/tasks/{id:guid}/submit", async Task<IResult> (Guid id, Submission input, ClaimsPrincipal principal, AppDb db) => {
    var uid = Access.User(principal);
    var task = await FindVisible(id, uid, db);
    if (task is null) return Results.NotFound();
    if (task.AssigneeId != uid) return Results.Forbid();
    if (task.Version != input.Version || task.State is not ("assigned" or "rework")) return Results.Conflict();
    if (string.IsNullOrWhiteSpace(input.Result) || input.Result.Length > 8000) return Results.BadRequest();
    task.Result = input.Result;
    task.State = "review";
    task.Version++;
    await db.SaveChangesAsync();
    return Results.Ok(task);
});
api.MapPost("/tasks/{id:guid}/accept", (Guid id, Review input, ClaimsPrincipal principal, AppDb db) =>
    Decide(id, input, principal, db, "accepted"));
api.MapPost("/tasks/{id:guid}/return", (Guid id, Review input, ClaimsPrincipal principal, AppDb db) =>
    Decide(id, input, principal, db, "rework"));
app.Run();

static async Task<WorkItem?> FindVisible(Guid id, Guid uid, AppDb db)
{
    var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == id);
    if (task is null || !Access.Related(task, uid) ||
        !await db.Members.AnyAsync(x => x.TeamId == task.TeamId && x.UserId == uid)) return null;
    return task;
}
static async Task<IResult> Decide(Guid id, Review input, ClaimsPrincipal principal, AppDb db, string state)
{
    var uid = Access.User(principal);
    var task = await FindVisible(id, uid, db);
    if (task is null) return Results.NotFound();
    if (task.AuthorId != uid) return Results.Forbid();
    if (task.Version != input.Version || task.State != "review") return Results.Conflict();
    task.State = state;
    task.Version++;
    await db.SaveChangesAsync();
    return Results.Ok(task);
}
