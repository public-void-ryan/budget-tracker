using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BudgetTracker.Api.Accounts;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Budgets;
using BudgetTracker.Api.Categories;
using BudgetTracker.Api.Transactions;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Imports;
using BudgetTracker.Api.Rules;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BudgetDbContext>(options =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("Default"))
        .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<RuleApplier>();
builder.Services.Configure<RegistrationOptions>(builder.Configuration.GetSection(RegistrationOptions.Section));
builder.Services
    .AddOptions<AuthSessionOptions>()
    .Bind(builder.Configuration.GetSection(AuthSessionOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<ExpiredSessionPurger>();
builder.Services
    .AddOptions<RuleOptions>()
    .Bind(builder.Configuration.GetSection(RuleOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.Configure<AuthRateLimitOptions>(builder.Configuration.GetSection(AuthRateLimitOptions.Section));

// ASP.NET reads numbers from strings by default ("12.50" as 12.5). Amounts are documented as
// JSON numbers, and quietly accepting strings would hide a client sending the wrong type.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSchemeTransformer>();
    options.AddOperationTransformer<BearerRequirementTransformer>();
});
builder.Services.AddExceptionHandler<ConstraintViolationHandler>();

builder.Services
    .AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // One budget per client address, shared by register and login. Behind a reverse proxy the
    // address is the proxy's unless forwarded headers are configured (a deployment concern).
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0,
            });
    });

    options.OnRejected = async (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails =
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests.",
                    Extensions = { ["code"] = "rate_limited" },
                },
            });
    };
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithTags("Health")
    .WithName("Health")
    .WithSummary("Liveness check.");
app.MapAuthEndpoints();
app.MapCategoryEndpoints();
app.MapAccountEndpoints();
app.MapTransactionEndpoints();
app.MapImportEndpoints();
app.MapRuleEndpoints();
app.MapBudgetEndpoints();

// Off unless switched on: appsettings.Development.json enables it for local work. Read from
// app.Configuration (after Build) so every configuration source has been applied.
if (app.Configuration.GetValue<bool>("Documentation:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
