using System.Text;
using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Middleware;
using CCMC.Cloud.Application.Audit;
using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Dashboard;
using CCMC.Cloud.Application.MasterData;
using CCMC.Cloud.Application.Reception;
using CCMC.Cloud.Infrastructure.Auth;
using CCMC.Cloud.Infrastructure.Persistence;
using CCMC.Cloud.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// --- Hosting: honor Render's (or any similar PaaS's) injected PORT env var ---
// ASP.NET Core has no built-in concept of a generic "PORT" variable (Render's
// own convention, not an ASP.NET Core one) - only ASPNETCORE_URLS/
// ASPNETCORE_HTTP_PORTS. When PORT is present, bind explicitly to it on
// 0.0.0.0 (never localhost/127.0.0.1 - the container's loopback interface is
// not reachable from outside it). When absent (local `dotnet run`, or a
// container started with ASPNETCORE_URLS/ASPNETCORE_HTTP_PORTS already set,
// e.g. the official .NET 8 ASP.NET runtime image's own default of 8080),
// this is a no-op and existing behavior (launchSettings.json's port 5000,
// or the base image's default) is unchanged.
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

// --- Configuration ---------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("CcmcDb")
    ?? throw new InvalidOperationException("Missing configuration: ConnectionStrings:CcmcDb.");

// Bound once, validated eagerly (ValidateOnStart - fails fast at startup, not
// on the first request, matching the previous eager-throw behavior), then
// resolved via IOptions<JwtOptions> by BOTH JwtTokenGenerator (issuance) and
// the JWT Bearer setup below (validation) - a single source of truth for
// Secret/Issuer/Audience. Previously these were read twice, independently,
// as separate top-level `builder.Configuration[...]` expressions - which is
// exactly how a real bug shipped: the issuance and validation sides silently
// disagreed on the Issuer/Audience fallback the moment only Jwt:Secret was
// supplied (e.g. `docker run -e Jwt__Secret=...` with no Issuer/Audience),
// producing a token every subsequent request would then fail to validate.
// See JwtOptions' doc comment for the full story.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Secret),
        "Missing configuration: Jwt:Secret. Set it via appsettings.Development.json (dev only), " +
        "an environment variable (Jwt__Secret), or user-secrets - never commit a real value.")
    .ValidateOnStart();

// --- Persistence -------------------------------------------------------------
builder.Services.AddDbContext<CcmcDbContext>(options => options.UseNpgsql(connectionString));

// --- Application / Infrastructure services ----------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<UserContextService>();
builder.Services.AddScoped<IPasswordHasher, PasswordHasherAdapter>();
builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<AuditQueryService>();
builder.Services.AddScoped<AuthenticationService>();
builder.Services.AddScoped<ReceptionService>();
builder.Services.AddScoped<CentreService>();
builder.Services.AddScoped<SourceService>();
builder.Services.AddScoped<VehicleService>();
builder.Services.AddScoped<QualityRuleService>();
builder.Services.AddScoped<RateFormulaSettingsService>();
builder.Services.AddScoped<DashboardService>();

// --- Authentication (JWT Bearer) --------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Without this, the JWT handler silently renames "sub"/"email" to long
        // legacy XML-namespace claim URIs on the way into ClaimsPrincipal,
        // breaking CurrentUserAccessor's JwtRegisteredClaimNames.Sub lookup
        // (found the hard way: real login/GET requests returned 401/403 with
        // "No authenticated user" despite a valid token - the claim was
        // present, just renamed). Preserves the claim names exactly as issued
        // by JwtTokenGenerator.
        options.MapInboundClaims = false;
    });

// Deferred to DI-resolution time (same moment JwtTokenGenerator itself reads
// IOptions<JwtOptions>), not at this eager top-level point - see the
// AddOptions<JwtOptions>() comment above for why this replaced two
// independent `builder.Configuration[...]` reads.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearerOptions, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

// --- Authorization (dynamic permission-code policies) -----------------------
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddAuthorization();

// --- MVC / Swagger -----------------------------------------------------------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CCMC Cloud API",
        Version = "v1",
        Description = "Cloud API for the CCMC native Windows client. Authenticate via POST /auth/login, then send the returned accessToken as a Bearer token.",
    });

    var bearerScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste ONLY the accessToken value returned by POST /auth/login (Swagger UI adds the 'Bearer ' prefix itself).",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    options.AddSecurityDefinition("Bearer", bearerScheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearerScheme] = [] });
});

// --- Health checks -----------------------------------------------------------
// Separate liveness ("is the process alive") from the database check, per
// this session's explicit instruction. /health/live never touches Postgres;
// /health/db does, and is what actually proves the API can reach the database.
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql", tags: ["db"]);

var app = builder.Build();

// No CORS policy is configured, deliberately: the only client is a native
// Windows application (System.Net.Http.HttpClient), not a browser SPA - CORS
// is a browser-enforced concept that does not apply to it at all. Adding a
// permissive CORS policy here would be unused complexity, not a requirement
// (this session's explicit instruction: "do not add browser-oriented CORS
// complexity unless actually required").

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// /health - trivial liveness probe, no dependencies (acceptance criterion §25).
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false, // run zero registered checks - "is the process alive" only
});
// /health/db - proves the API can actually reach PostgreSQL.
app.MapHealthChecks("/health/db", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("db"),
});

// Applies pending EF Core migrations at startup (never EnsureCreated() - this
// session's explicit requirement for a real, tracked migration history), then
// runs the idempotent development seed ONLY in the Development environment -
// production deployments must run migrations via a deliberate `dotnet ef
// database update` step instead (see README "Deployment").
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<CcmcDbContext>();

    logger.LogInformation("Applying database migrations...");
    db.Database.Migrate();
    logger.LogInformation("Database migrations up to date.");

    if (app.Environment.IsDevelopment())
    {
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        await DevelopmentSeeder.SeedAsync(db, passwordHasher, logger);
    }
}

app.Logger.LogInformation("CCMC Cloud API starting up.");

app.Run();

// Exposed for CCMC.Cloud.Api.Tests' WebApplicationFactory<Program>.
public partial class Program;
