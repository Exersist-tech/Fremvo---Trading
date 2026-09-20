using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Trading.Application.Execution;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Application.UseCases.Exchange;
using Trading.Application.UseCases.Identity;
using Trading.Application.Universe;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Market;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Domain.Universe;
using Trading.Domain.Users;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
using Trading.Exchanges.Kraken;
using Trading.Exchanges.Kraken.Execution;
using Trading.Exchanges.Kraken.MarketData;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Audit;
using Trading.Infrastructure.Data.Execution;
using Trading.Infrastructure.Data.ExchangeAccounts;
using Trading.Infrastructure.Secrets;
using Trading.MarketData;
using Trading.Optimization;
using Trading.Risk;
using Trading.Web.Development;
using Trading.Web.Extensions;
using Trading.Web.Optimization;
using Trading.Web.Security;
using ITradingAuthenticationService = Trading.Application.UseCases.Identity.IAuthenticationService;
using TradingAuthenticationService = Trading.Application.UseCases.Identity.AuthenticationService;

var featureNames = new[]
{
    "Invitation management",
    "Registration flow",
    "Account connection readiness",
    "Market-data foundation",
    "Safety-first trading gates"
};

const string ExperimentDisclaimer =
    "Experiment workers trade with fake funds only and cannot place an order on a real exchange. " +
    "Simulated results do not indicate future results, and no strategy is guaranteed to be profitable.";

const string OrdersDisclaimer =
    "Orders shown here are paper orders placed with fake funds. " +
    "No result shown is a prediction, and no strategy is guaranteed to be profitable.";

const string LiveOrdersDisclaimer =
    "These are real orders placed with your own funds on a real exchange. " +
    "Acceptance by the exchange is not a fill, and an accepted order may still be " +
    "cancelled, partly filled, or filled at a different time than expected. " +
    "No result shown is a prediction, and no strategy is guaranteed to be profitable.";

// The live book is empty because no order has ever been sent to a venue from
// this deployment. Saying so is more useful than describing it as paper, which
// the list is not.
const string LiveBookDisclaimer =
    "This deployment has no execution route to any exchange, so no order has ever reached a venue. " +
    "This list is empty for that reason, not because of a filter. " +
    "No result shown is a prediction, and no strategy is guaranteed to be profitable.";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTradingInfrastructure(builder.Configuration);
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IRegistrationService, RegistrationService>();
// Password hashing is a singleton: it holds a lazily computed decoy hash used
// to spend equal time on unknown accounts, and recomputing that per request
// would waste the cost it exists to impose.
builder.Services.AddSingleton<Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IPasswordHasher>(sp => sp.GetRequiredService<Pbkdf2PasswordHasher>());
builder.Services.AddScoped<ITradingAuthenticationService, TradingAuthenticationService>();
builder.Services.AddScoped<IAdministratorMfaPolicyService, AdministratorMfaPolicyService>();
builder.Services.AddScoped<IAuditEventWriter, EfAuditEventWriter>();
builder.Services.AddScoped<IAuditQueryService>(provider =>
    new AuditQueryService(provider.GetRequiredService<TradingDbContext>().AuditEvents));

builder.Services.AddScoped<IExchangeAccountService, ExchangeAccountService>();
builder.Services.AddScoped<IExchangeAccountRepository, EfExchangeAccountRepository>();
builder.Services.AddScoped<IExchangeAccountConnectionService, ExchangeAccountConnectionService>();

// Whether this deployment can reach a real venue. Registering an
// ILiveExecutionRoute is the single act that opens the promotion ladder out of
// paper, so it is done deliberately and only when an operator has asked for it.
// The switch cannot invent a capability: it can only register a route whose
// gateway already exists in this build, and an exchange with no gateway stays
// unreachable no matter what configuration says.
var liveExecutionEnabled = builder.Configuration.GetValue<bool>("Trading:LiveExecution:Kraken:Enabled");

builder.Services.AddSingleton<ILiveExecutionRouteProvider, LiveExecutionRouteProvider>();
builder.Services.AddScoped<IExchangeAccountStageService, ExchangeAccountStageService>();

// Platform ceilings on live trading. Deliberately small by default, and with no
// approved proving instruments, so a deployment that enables live execution
// without configuring bounds still cannot send a large or arbitrary order.
var liveTradingOptions = new LiveTradingOptions();
builder.Configuration.GetSection("Trading:LiveExecution").Bind(liveTradingOptions);
builder.Services.AddSingleton(liveTradingOptions);

// The order gateway is registered whether or not live execution is enabled: it
// is also what reconciliation and order synchronisation read through, and both
// of those must keep working for an account that was promoted and then had its
// route withdrawn. Only the route registration below grants the ability to
// promote an account in the first place.
builder.Services.AddHttpClient<ISpotOrderGateway, KrakenSpotOrderGateway>(client =>
{
    client.BaseAddress = new Uri("https://api.kraken.com");
    client.Timeout = TimeSpan.FromSeconds(20);
});

if (liveExecutionEnabled)
{
    builder.Services.AddSingleton<ILiveExecutionRoute>(provider =>
        new KrakenSpotLiveExecutionRoute(provider.GetRequiredService<ISpotOrderGateway>()));
}

builder.Services.AddScoped<ISpotExecutionAccountSource, ExchangeAccountExecutionSource>();
builder.Services.AddScoped<IExecutionAdapter>(provider => new SpotExecutionAdapter(
    provider.GetRequiredService<ISpotOrderGateway>(),
    provider.GetRequiredService<ISpotExecutionAccountSource>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ILiveTradingService, LiveTradingService>();
builder.Services.AddScoped<LiveOrderSyncService>();

// The Kraken permission probe talks to Kraken's private API to establish what a
// user's key may do. It is the only component that sees a credential, and it
// never performs a withdrawal: it only detects that the capability exists so a
// withdrawal-capable key can be refused.
// Kraken rejects a private request whose nonce is not strictly greater than the
// last one used with that key. A single probe makes three private calls, so the
// source is registered once for the process rather than derived from the clock
// at each call site.
builder.Services.AddSingleton<IKrakenNonceSource, KrakenNonceSource>();

builder.Services.AddHttpClient<IExchangePermissionProbe, KrakenPermissionProbe>(client =>
{
    client.BaseAddress = new Uri("https://api.kraken.com");
    client.Timeout = TimeSpan.FromSeconds(20);
});

// Historical candles come from Kraken's public OHLC endpoint. It is registered
// separately from the permission probe and deliberately carries no credential:
// reading price history needs no permission on any user's account, so this
// client must never be given one.
builder.Services.AddHttpClient<IHistoricalCandleSource, KrakenHistoricalCandleSource>(client =>
{
    client.BaseAddress = new Uri("https://api.kraken.com");
    client.Timeout = TimeSpan.FromSeconds(20);
});

// Credential storage. Azure uses Key Vault through a managed identity. A
// developer machine has no Key Vault, so Development uses a Data Protection
// encrypted file outside the repository; it refuses to construct in any other
// environment.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDataProtection();
    builder.Services.AddSingleton<ISecretStore>(provider => new DevelopmentFileSecretStore(
        provider.GetRequiredService<IDataProtectionProvider>(),
        provider.GetRequiredService<IHostEnvironment>(),
        DevelopmentFileSecretStore.DefaultFilePath));
}
else
{
    var keyVaultUri = builder.Configuration["KeyVault:Uri"];
    if (!Uri.TryCreate(keyVaultUri, UriKind.Absolute, out var keyVaultEndpoint)
        || keyVaultEndpoint.Scheme != Uri.UriSchemeHttps
        || !string.IsNullOrEmpty(keyVaultEndpoint.UserInfo))
    {
        throw new InvalidOperationException(
            "Production requires a valid HTTPS KeyVault:Uri. Exchange credentials must never use in-memory storage outside Development.");
    }

    builder.Services.AddSingleton(_ => new SecretClient(keyVaultEndpoint, new DefaultAzureCredential()));
    builder.Services.AddSingleton<ISecretStore, AzureKeyVaultSecretStore>();
}

// Sign-in state is held in a cookie that the browser cannot read, so no identity or session
// material is ever exposed to client-side script.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "exersist.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);

        // API callers get a status code rather than a redirect to a sign-in page.
        // A browser navigating to a page is sent to the sign-in form instead, so
        // an unauthenticated person lands somewhere they can act on rather than
        // on an empty error. This changes presentation only: the route itself is
        // still refused, and nothing becomes reachable without a session.
        options.Events.OnRedirectToLogin = context =>
        {
            if (WantsHtmlPage(context.Request))
            {
                // The return path is passed as a relative path only. Echoing an
                // absolute URL here would turn the sign-in page into an open
                // redirect that could bounce a user to an attacker's site.
                var returnPath = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
                context.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(returnPath));
                return Task.CompletedTask;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

static bool WantsHtmlPage(HttpRequest request) =>
    !request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
    && HttpMethods.IsGet(request.Method)
    && request.Headers.Accept.Any(value =>
        value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase));

builder.Services.AddAuthorization();

// Experiment state is paper-only and non-durable. Registering the in-memory store here keeps
// experiment data out of the trading database until a durable store is designed for it.
builder.Services.AddSingleton<IExperimentWorkerRepository, InMemoryExperimentWorkerRepository>();
builder.Services.AddSingleton<ExperimentWorkerPool>();

// Halt state is shared by every trading path in this process. It is registered as a singleton so
// an emergency stop takes effect immediately for all callers.
builder.Services.AddSingleton<InMemoryTradingHaltState>();
builder.Services.AddSingleton<ITradingHaltState>(sp => sp.GetRequiredService<InMemoryTradingHaltState>());

// Execution storage. These are durable: an order that exists at the exchange
// must never outlive its local record, because the client order id stored here
// is the only handle by which a lost order can be found again. In-memory
// storage would lose that handle on every restart, which is survivable for a
// simulated fill and not survivable for a real one.
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<IPositionRepository, EfPositionRepository>();
builder.Services.AddScoped<IOrderReconciliationRepository, EfOrderReconciliationRepository>();
builder.Services.AddScoped<IExchangeOrderStatusQuery, OrderBackedSpotOrderStatusQuery>();
builder.Services.AddScoped<OrderReconciliationService>();
builder.Services.AddSingleton(_ => new RiskEngine());

// Instrument universe. The thresholds registered here are the mandatory
// platform floor; operator configuration is combined with them and may only
// ever be stricter.
builder.Services.AddSingleton(TimeProvider.System);

// Paper trading (Phase 5). Scoped because it writes audit events through the
// scoped writer. This service has no exchange adapter of any kind: its fills
// are simulated against published closed candles and it cannot reach a venue.
builder.Services.AddScoped<PaperTradingService>();

// Values open positions against closed candles. Read-only: it creates no order
// and changes no position.
builder.Services.AddScoped<IPositionValuationService, PositionValuationService>();

// Enforces stop and target levels on paper positions. Without this the levels
// would be decorative, which is worse than not offering them at all.
builder.Services.AddScoped<IProtectiveExitEvaluator, ProtectiveExitEvaluator>();

// The tradable pair list is public reference data shared by every user, so it
// is a singleton with its own short-lived cache. It holds no user, account,
// balance or credential, so nothing leaks between users.
builder.Services.AddHttpClient<ITradablePairSource, KrakenTradablePairSource>(client =>
{
    client.BaseAddress = new Uri("https://api.kraken.com");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<IUniverseInstrumentProvider, SeedUniverseInstrumentProvider>();
builder.Services.AddSingleton<IUniverseEvidenceSource, UnconfiguredUniverseEvidenceSource>();
builder.Services.AddSingleton(_ => new EligibilityThresholds(
    minimumRollingQuoteVolume: 5_000_000m,
    minimumMedianQuoteVolume: 3_000_000m,
    maximumSpread: 0.0020m,
    maximumEstimatedSlippage: 0.0035m,
    minimumHistoryCandles: 1_000,
    minimumListingAge: TimeSpan.FromDays(90),
    maximumEvidenceAge: TimeSpan.FromHours(6)));
builder.Services.AddSingleton(sp => new InstrumentEligibilityEvaluator(
    new[] { MarketUniverseSeed.QuoteAsset },
    sp.GetRequiredService<EligibilityThresholds>()));
builder.Services.AddSingleton(sp => new UniverseAdminQueryService(
    sp.GetRequiredService<InstrumentEligibilityEvaluator>(),
    new InstrumentDegradationPolicy(sp.GetRequiredService<EligibilityThresholds>().MaximumEvidenceAge),
    NewListingPolicy.PlatformFloor,
    sp.GetRequiredService<EligibilityThresholds>(),
    sp.GetRequiredService<IUniverseEvidenceSource>(),
    sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();

// Local demo bootstrap. This is a no-op unless Development:SeedDemoData is
// set, and it refuses to run outside the Development environment.
await DevelopmentDataSeeder.SeedAsync(app).ConfigureAwait(false);

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new
{
    application = "Exersist Trading",
    status = "Beta",
    stage = "Identity and platform foundation",
    features = featureNames
}));

app.MapGet("/api/audit", async (IAuditQueryService queryService, CancellationToken cancellationToken) =>
{
    var events = await queryService.ListAsync(0, 10, cancellationToken).ConfigureAwait(false);
    return Results.Ok(events.Select(e => new
    {
        e.Id,
        e.ActorUserId,
        e.Action,
        e.TargetType,
        e.TargetId,
        e.OccurredAtUtc,
        e.CorrelationId
    }));
});

app.MapPost("/api/audit", async (IAuditEventWriter writer, CancellationToken cancellationToken, AuditEvent request) =>
{
    ArgumentNullException.ThrowIfNull(request);
    await writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
    return Results.Ok(new { request.Id, request.Action, request.CorrelationId });
});

// The entry point is a decision, not a page. An anonymous visitor is sent to
// sign-in, because on an invitation-only platform there is nothing else for
// them to do; a signed-in user is sent straight to the trading application
// rather than to a marketing page they have already read. The overview itself
// still exists at /welcome for anyone who wants it.
app.MapGet("/", (ClaimsPrincipal principal) =>
    principal.Identity?.IsAuthenticated == true
        ? Results.Redirect("/chart")
        : Results.Redirect("/login"));

app.MapGet("/welcome", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Exersist Trading Beta</title>
      <style>
        :root {
          --bg: #07111c;
          --panel: #111d2d;
          --panel-2: #172b3c;
          --line: #24415d;
          --primary: #62d0ff;
          --accent: #89f7a5;
          --text: #eaf4ff;
          --muted: #9bb6cd;
          --warning: #ffd166;
          --danger: #ff7b7b;
        }
        * { box-sizing: border-box; }
        body {
          margin: 0;
          font-family: Segoe UI, Arial, sans-serif;
          background: linear-gradient(180deg, var(--bg), #0b1725 35%, #0b1521 100%);
          color: var(--text);
        }
        .container { max-width: 1180px; margin: 0 auto; padding: 32px 20px 80px; }
        .hero {
          background: rgba(17, 29, 45, 0.9);
          border: 1px solid var(--line);
          border-radius: 18px;
          padding: 24px 28px;
          box-shadow: 0 10px 30px rgba(0,0,0,0.25);
        }
        .badge {
          display: inline-block;
          padding: 6px 12px;
          border-radius: 999px;
          border: 1px solid rgba(98,208,255,0.4);
          background: rgba(98,208,255,0.08);
          color: var(--primary);
          letter-spacing: 0.08em; text-transform: uppercase; font-size: 11px; font-weight: 700;
        }
        h1 { font-size: clamp(2.2rem, 4vw, 4rem); margin: 18px 0 12px; }
        .subtitle { color: var(--muted); font-size: 1.1rem; max-width: 760px; line-height: 1.6; }
        .grid { display: grid; gap: 18px; grid-template-columns: repeat(auto-fit,minmax(230px,1fr)); margin-top: 28px; }
        .card {
          background: rgba(23, 43, 60, 0.8);
          border: 1px solid var(--line);
          border-radius: 14px;
          padding: 20px;
        }
        .card h3 { margin-top: 0; }
        ul { margin: 12px 0 0 18px; color: var(--muted); padding: 0; }
        .status-row { display: flex; gap: 12px; flex-wrap: wrap; margin-top: 22px; }
        .pill {
          display: inline-flex; align-items: center; background: rgba(137,247,165,0.08);
          border: 1px solid rgba(137,247,165,0.3); border-radius: 999px; padding: 8px 12px; color: var(--accent);
          font-weight: 600;
        }
        .warning { background: rgba(255,209,102,0.08); border-color: rgba(255,209,102,0.3); color: var(--warning); }
      </style>
    </head>
    <body>
      <div class="container">
        <header class="hero">
          <div class="badge">Beta</div>
          <h1>Exersist Trading</h1>
          <p class="subtitle">
            A security-first, exchange-neutral crypto trading platform foundation built for disciplined market analysis,
            risk controls, and staged rollout through exchange sandbox environments before any live trading is enabled.
          </p>
          <div class="status-row">
            <span class="pill">Identity foundation ready</span>
            <span class="pill">Invitation flow enabled</span>
            <span class="pill">Audit trail active</span>
            <span class="pill warning">Live trading disabled by design</span>
          </div>
        </header>

        <section class="grid">
          <article class="card">
            <h3>Platform scope</h3>
            <ul>
              <li>Kraken-first connector architecture</li>
              <li>Exchange-neutral domain design</li>
              <li>No withdrawals; user-owned accounts only</li>
              <li>Market data and strategy groundwork</li>
            </ul>
          </article>

          <article class="card">
            <h3>Safety controls</h3>
            <ul>
              <li>Risk engine gates before execution</li>
              <li>Idempotent order identifiers</li>
              <li>Stale-data enforcement</li>
              <li>Human approval required for live paths</li>
            </ul>
          </article>

          <article class="card">
            <h3>Phase readiness</h3>
            <ul>
              <li>Identity and invitation domain</li>
              <li>Persistence model for auth metadata</li>
              <li>Audit writer and query API enabled</li>
              <li>Order and position state machines persisted</li>
              <li>Unknown-order reconciliation enforced</li>
            </ul>
          </article>
        </section>

        <section class="grid" style="margin-top:1.5rem">
          <a class="card-link" href="/orders"><article class="card">
            <h3>Orders and reconciliation</h3>
            <p>Paper orders, open positions, and any order frozen because its exchange outcome could not be established.</p>
          </article></a>
          <a class="card-link" href="/experiments"><article class="card">
            <h3>Experiment workers</h3>
            <p>Up to ten isolated paper workers with separate balances, state and random seeds.</p>
          </article></a>
          <a class="card-link" href="/optimization"><article class="card">
            <h3>Optimization</h3>
            <p>Training, validation, untouched holdout and walk-forward plan validation.</p>
          </article></a>
          <a class="card-link" href="/admin/universe"><article class="card">
            <h3>Market universe</h3>
            <p>Instrument eligibility, data quality evidence and listing-age restrictions.</p>
          </article></a>
          <a class="card-link" href="/admin/risk"><article class="card">
            <h3>Risk and halts</h3>
            <p>Emergency stop, global and scoped trading halts, close-only and reduce-only modes.</p>
          </article></a>
          <a class="card-link" href="/account"><article class="card">
            <h3>Account</h3>
            <p>Invitation-only registration and sign in. Secrets never reach the browser.</p>
          </article></a>
        </section>
      </div>
    </body>
    </html>
    """,
    "text/html"));

// Issuing an invitation is an administrator action on an invitation-only platform. It is
// authenticated, role-restricted, persisted, and audited.
app.MapPost("/api/invitations", async (
    ClaimsPrincipal principal,
    IInvitationService service,
    TradingDbContext dbContext,
    IAuditEventWriter auditWriter,
    InvitationRequest request,
    CancellationToken cancellationToken) =>
{
    ArgumentNullException.ThrowIfNull(request);

    var issuerId = CurrentUser.TryGetUserId(principal);
    if (issuerId is null)
    {
        return Results.Unauthorized();
    }

    // The code is generated, never derived from the invitee's email address, which would make it
    // guessable by anyone who knows the address.
    var invitation = service.CreateInvitation(
        issuerId.Value,
        InvitationCodeGenerator.Generate(),
        1,
        DateTimeOffset.UtcNow.AddDays(30));

    dbContext.Invitations.Add(invitation);
    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

    await auditWriter.WriteAsync(
        new AuditEvent(
            Guid.NewGuid(),
            issuerId.Value,
            "Invitation.Issued",
            nameof(Invitation),
            invitation.Id.ToString(),
            DateTimeOffset.UtcNow,
            null,
            // The code itself is never written to the audit trail or to logs.
            $"Invitation issued for {request.Email}.",
            Guid.NewGuid().ToString()),
        cancellationToken).ConfigureAwait(false);

    return Results.Ok(new { invitation.Id, invitation.Code, invitation.ExpiresAtUtc });
}).RequireAuthorization(policy => policy.RequireRole(nameof(RoleType.Administrator)));

app.MapPost("/api/register", async (
    TradingDbContext dbContext,
    IRegistrationService registrationService,
    IAuditEventWriter auditWriter,
    RegisterUserRequest request,
    CancellationToken cancellationToken) =>
{
    ArgumentNullException.ThrowIfNull(request);

    var invitation = await dbContext.Invitations
        .SingleOrDefaultAsync(i => i.Code == request.InvitationCode, cancellationToken)
        .ConfigureAwait(false);

    if (invitation is null)
    {
        return Results.BadRequest(new { error = "Invitation code not found." });
    }

    if (!invitation.IsUsableAt(DateTimeOffset.UtcNow))
    {
        return Results.BadRequest(new { error = "Invitation code is expired or exhausted." });
    }

    var user = registrationService.Register(request, invitation.Id, Guid.NewGuid());

    dbContext.Users.Add(user);
    var redeemedInvitation = invitation.Consume();
    dbContext.Invitations.Update(redeemedInvitation);
    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

    var audit = new AuditEvent(
        Guid.NewGuid(),
        user.Id,
        "User.Registered",
        nameof(User),
        user.Id.ToString(),
        DateTimeOffset.UtcNow,
        null,
        null,
        Guid.NewGuid().ToString());

    await auditWriter.WriteAsync(audit, cancellationToken).ConfigureAwait(false);

    return Results.Ok(new { user.Id, user.Email, user.DisplayName, user.Role });
});

// Tells the browser who it is signed in as. Without this the UI has no way to
// know, which is why the navigation could not show a signed-in state and
// signing out appeared to change nothing.
//
// It returns only what the interface needs to render. No credential, no
// exchange key, no secret reference.
app.MapGet("/api/me", (ClaimsPrincipal principal, TradingDbContext dbContext) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Json(new { signedIn = false }, statusCode: StatusCodes.Status200OK);
    }

    var user = dbContext.Users.SingleOrDefault(u => u.Id == userId.Value);
    if (user is null)
    {
        // The cookie names a user that no longer exists. Report signed out
        // rather than half-signed-in, so the UI offers a way back.
        return Results.Json(new { signedIn = false }, statusCode: StatusCodes.Status200OK);
    }

    return Results.Ok(new
    {
        signedIn = true,
        user.Id,
        user.Email,
        user.DisplayName,
        role = user.Role.ToString(),
        isAdministrator = user.Role == RoleType.Administrator,
        requiresMfaSetup = user.RequiresMfaForAdministrator
    });
});

app.MapPost("/api/login", async (
    HttpContext httpContext,
    ITradingAuthenticationService authService,
    Pbkdf2PasswordHasher passwordHasher,
    TradingDbContext dbContext,
    LoginRequest request) =>
{
    ArgumentNullException.ThrowIfNull(request);

    // A body without an address is a malformed request, not a membership
    // question, so it is refused with the same generic message rather than
    // faulting the endpoint.
    if (string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new { error = "Invalid login" });
    }

    var email = request.Email.Trim();

    var user = dbContext.Users
        .SingleOrDefault(u => u.Email == email);

    // The user is passed in even when null so the service performs the same
    // password verification either way. Short-circuiting here would make an
    // unknown address measurably faster to reject than a known one, which on
    // an invitation-only platform discloses who holds an account.
    var outcome = authService.Authenticate(user, request.Password);

    if (!outcome.Succeeded)
    {
        // One response for every failure. The specific reason is deliberately
        // not returned: distinguishing "no such account" from "wrong password"
        // or "suspended" turns this endpoint into a membership oracle.
        return Results.BadRequest(new { error = "Invalid login" });
    }

    if (outcome.PasswordNeedsRehash && user is not null)
    {
        // The password was correct but stored under weaker parameters. Upgrade
        // it now, while the plaintext is available, rather than leaving it.
        user.SetPasswordHash(passwordHasher.Hash(request.Password));
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    if (user is null)
    {
        return Results.BadRequest(new { error = "Invalid login" });
    }

    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
    identity.AddClaim(new Claim(CurrentUser.UserIdClaim, user.Id.ToString()));
    identity.AddClaim(new Claim(ClaimTypes.Name, user.Email));
    identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));

    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity)).ConfigureAwait(false);

    return Results.Ok(new { user.Id, user.Email });
});

app.MapPost("/api/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    return Results.Ok(new { signedOut = true });
});

app.MapPost("/api/optimization/validate-plan", (OptimizationPlanDto request) =>
{
    ArgumentNullException.ThrowIfNull(request);

    OptimizationPlanValidationResult result;

    try
    {
        result = OptimizationPlanValidator.Validate(request.ToRequest());
    }
    catch (ArgumentException ex)
    {
        result = OptimizationPlanValidationResult.Invalid(new[] { ex.Message });
    }

    return Results.Ok(new
    {
        isValid = result.IsValid,
        errors = result.Errors,
        canBeExecuted = result.CanBeExecuted,
        executionBlockedReason = result.ExecutionBlockedReason,
        disclaimer = OptimizationRunResult.NoGuaranteeDisclaimer
    });
});

app.MapGet("/optimization", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Optimization plan — Exersist Trading</title>
      <style>
        body { margin:0; font-family: Segoe UI, Arial, sans-serif; background:#0b1725; color:#eaf4ff; }
        .container { max-width: 900px; margin: 0 auto; padding: 32px 20px 80px; }
        h1 { font-size: 2rem; margin-bottom: 8px; }
        p.muted { color:#9bb6cd; line-height:1.6; }
        fieldset { border:1px solid #24415d; border-radius:12px; margin:18px 0; padding:16px 18px; }
        legend { color:#62d0ff; font-weight:700; letter-spacing:0.04em; text-transform:uppercase; font-size:12px; }
        label { display:block; margin:10px 0 4px; color:#9bb6cd; font-size:13px; }
        input { width:100%; padding:9px 10px; border-radius:8px; border:1px solid #24415d; background:#0f1c2b; color:#eaf4ff; }
        .row { display:grid; grid-template-columns:1fr 1fr; gap:14px; }
        button { margin-top:18px; padding:11px 18px; border-radius:10px; border:1px solid rgba(98,208,255,0.4);
                 background:rgba(98,208,255,0.12); color:#62d0ff; font-weight:700; cursor:pointer; }
        .notice { border:1px solid rgba(255,209,102,0.35); background:rgba(255,209,102,0.08); color:#ffd166;
                  border-radius:12px; padding:14px 16px; margin:18px 0; line-height:1.5; }
        pre { background:#0f1c2b; border:1px solid #24415d; border-radius:12px; padding:14px; white-space:pre-wrap;
              word-break:break-word; color:#cfe6fa; }
      </style>
    </head>
    <body>
      <div class="container">
        <h1>Optimization plan</h1>
        <p class="muted">
          Configure the training, validation, and untouched holdout windows for a parameter search.
          Splits must be time-ordered and non-overlapping so that no future data can leak into
          parameter selection. The holdout window is locked and may only ever be scored once,
          after selection is final.
        </p>

        <div class="notice">
          <strong>Results are not available yet.</strong> This screen validates split
          configuration only. Scoring requires the backtest engine (plan task 5.5), which is not
          implemented. No simulated or placeholder results are shown.
        </div>

        <form id="plan">
          <fieldset>
            <legend>Instrument</legend>
            <label for="symbol">Symbol</label>
            <input id="symbol" value="XBTUSD" />
          </fieldset>

          <fieldset>
            <legend>Training window (UTC)</legend>
            <div class="row">
              <div><label for="trainFrom">From</label><input id="trainFrom" type="datetime-local" value="2024-01-01T00:00" /></div>
              <div><label for="trainTo">To</label><input id="trainTo" type="datetime-local" value="2024-04-01T00:00" /></div>
            </div>
          </fieldset>

          <fieldset>
            <legend>Validation window (UTC)</legend>
            <div class="row">
              <div><label for="valFrom">From</label><input id="valFrom" type="datetime-local" value="2024-04-01T00:00" /></div>
              <div><label for="valTo">To</label><input id="valTo" type="datetime-local" value="2024-06-01T00:00" /></div>
            </div>
          </fieldset>

          <fieldset>
            <legend>Holdout window (UTC) — scored once, never used for selection</legend>
            <div class="row">
              <div><label for="holdFrom">From</label><input id="holdFrom" type="datetime-local" value="2024-06-01T00:00" /></div>
              <div><label for="holdTo">To</label><input id="holdTo" type="datetime-local" value="2024-07-01T00:00" /></div>
            </div>
          </fieldset>

          <fieldset>
            <legend>Parameter range</legend>
            <label for="pName">Name</label><input id="pName" value="lookback" />
            <div class="row">
              <div><label for="pMin">Minimum</label><input id="pMin" value="5" /></div>
              <div><label for="pMax">Maximum</label><input id="pMax" value="50" /></div>
            </div>
            <label for="pDefault">Default</label><input id="pDefault" value="20" />
          </fieldset>

          <button type="submit">Validate plan</button>
        </form>

        <pre id="output">No plan validated yet.</pre>
      </div>

      <script>
        const utc = id => new Date(document.getElementById(id).value + 'Z').toISOString();
        document.getElementById('plan').addEventListener('submit', async e => {
          e.preventDefault();
          const body = {
            symbol: document.getElementById('symbol').value,
            trainingFromUtc: utc('trainFrom'),
            trainingToUtc: utc('trainTo'),
            validationFromUtc: utc('valFrom'),
            validationToUtc: utc('valTo'),
            holdoutFromUtc: utc('holdFrom'),
            holdoutToUtc: utc('holdTo'),
            parameters: [{
              name: document.getElementById('pName').value,
              minimum: Number(document.getElementById('pMin').value),
              maximum: Number(document.getElementById('pMax').value),
              defaultValue: Number(document.getElementById('pDefault').value),
              description: 'User-configured range'
            }]
          };
          const res = await fetch('/api/optimization/validate-plan', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
          });
          document.getElementById('output').textContent = JSON.stringify(await res.json(), null, 2);
        });
      </script>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

app.MapGet("/api/experiments", async (
    ClaimsPrincipal principal,
    IExperimentWorkerRepository repository,
    CancellationToken cancellationToken) =>
{
    // The owning user comes from the signed-in principal only. There is no user id parameter, so
    // one user cannot request another user's experiment workers.
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var workers = await repository.ListAsync(userId.Value, cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        maxWorkers = ExperimentWorker.MaxWorkersPerUser,
        used = workers.Count,
        tradingMode = "Paper",
        disclaimer = ExperimentDisclaimer,
        workers = workers
            .OrderBy(w => w.CreatedAtUtc)
            .Select(w => new
            {
                w.Id,
                w.Name,
                strategyTemplateId = w.StrategyId,
                w.MarketSymbol,
                status = w.Status.ToString(),
                w.RandomSeed,
                w.StartingCash,
                w.CashBalance,
                w.PositionQuantity,
                w.AverageEntryPrice,
                w.RealizedProfitAndLoss,
                w.FailureReason,
                tradeCount = w.Ledger.Count,
                w.CreatedAtUtc
            })
    });
}).RequireAuthorization();

app.MapPost("/api/experiments", async (
    ClaimsPrincipal principal,
    ExperimentWorkerPool pool,
    CreateExperimentWorkerRequest request,
    CancellationToken cancellationToken) =>
{
    ArgumentNullException.ThrowIfNull(request);

    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    try
    {
        var worker = await pool.CreateWorkerAsync(
            userId.Value,
            request.Name,
            request.StrategyTemplateId,
            request.MarketSymbol,
            request.StartingCash,
            DateTimeOffset.UtcNow,
            request.RandomSeed,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new { worker.Id, worker.Name, status = worker.Status.ToString() });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/experiments", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Experiment workers — Exersist Trading</title>
      <style>
        body { margin:0; font-family: Segoe UI, Arial, sans-serif; background:#0b1725; color:#eaf4ff; }
        .container { max-width: 1000px; margin: 0 auto; padding: 32px 20px 80px; }
        h1 { font-size: 2rem; margin-bottom: 8px; }
        p.muted { color:#9bb6cd; line-height:1.6; }
        .badge { display:inline-block; border-radius:999px; padding:4px 12px; font-size:12px; font-weight:700;
                 border:1px solid rgba(98,208,255,0.4); background:rgba(98,208,255,0.12); color:#62d0ff; }
        .notice { border:1px solid rgba(255,209,102,0.35); background:rgba(255,209,102,0.08); color:#ffd166;
                  border-radius:12px; padding:14px 16px; margin:18px 0; line-height:1.5; }
        table { width:100%; border-collapse:collapse; margin-top:18px; }
        th, td { text-align:left; padding:10px 12px; border-bottom:1px solid #24415d; font-size:14px; }
        th { color:#9bb6cd; font-weight:600; text-transform:uppercase; font-size:11px; letter-spacing:0.05em; }
        pre { background:#0f1c2b; border:1px solid #24415d; border-radius:12px; padding:14px; white-space:pre-wrap;
              word-break:break-word; color:#cfe6fa; }
      </style>
    </head>
    <body>
      <div class="container">
        <h1>Experiment workers <span class="badge">Paper only</span></h1>
        <p class="muted">
          Up to ten isolated workers per user. Each worker keeps its own balance, position,
          strategy state, parameters, random seed, and results. Workers may read the same
          immutable historical data but never share mutable state, and a worker that fails does
          not stop the others.
        </p>

        <div class="notice">
          Experiment workers trade with fake funds only. They cannot place an order on a real
          exchange. Past or simulated results do not indicate future results, and no strategy is
          guaranteed to be profitable.
        </div>

        <div id="state"></div>
        <table id="grid" hidden>
          <thead>
            <tr>
              <th>Name</th><th>Symbol</th><th>Status</th><th>Seed</th>
              <th>Cash</th><th>Position</th><th>Realized P&amp;L</th><th>Trades</th>
            </tr>
          </thead>
          <tbody></tbody>
        </table>
      </div>

      <script>
        const n = v => Number(v).toLocaleString(undefined, { maximumFractionDigits: 8 });
        (async () => {
          const state = document.getElementById('state');
          const res = await fetch('/api/experiments');
          if (res.status === 401) {
            state.innerHTML = '<p class="muted">Sign in to view your experiment workers.</p>';
            return;
          }
          const data = await res.json();
          state.innerHTML = '<p class="muted">Using ' + data.used + ' of ' + data.maxWorkers + ' workers.</p>';
          if (data.workers.length === 0) {
            state.innerHTML += '<p class="muted">No experiment workers yet.</p>';
            return;
          }
          const grid = document.getElementById('grid');
          grid.hidden = false;
          grid.querySelector('tbody').innerHTML = data.workers.map(w =>
            '<tr><td>' + w.name + '</td><td>' + w.marketSymbol + '</td><td>' + w.status +
            '</td><td>' + w.randomSeed + '</td><td>' + n(w.cashBalance) + '</td><td>' +
            n(w.positionQuantity) + '</td><td>' + n(w.realizedProfitAndLoss) + '</td><td>' +
            w.tradeCount + '</td></tr>').join('');
        })();
      </script>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

app.MapGet("/api/risk/halts", (InMemoryTradingHaltState halts) => Results.Ok(new
{
    emergencyStop = halts.EmergencyStop,
    liveTradingEnabled = false,
    note = "Live trading is disabled platform-wide. The emergency stop blocks every new order for every user."
})).RequireAuthorization(policy => policy.RequireRole(
    nameof(RoleType.Administrator), nameof(RoleType.RiskOfficer)));

app.MapPost("/api/risk/halts", async (
    ClaimsPrincipal principal,
    InMemoryTradingHaltState halts,
    IAuditEventWriter auditWriter,
    HaltCommandRequest request,
    CancellationToken cancellationToken) =>
{
    ArgumentNullException.ThrowIfNull(request);

    var actorId = CurrentUser.TryGetUserId(principal);
    if (actorId is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        // A halt or a release is an operator decision that must be explainable afterwards.
        return Results.BadRequest(new { error = "A reason is required for every halt change." });
    }

    string action;

    switch (request.Scope?.Trim().ToUpperInvariant())
    {
        case "EMERGENCY":
            if (request.Engage)
            {
                halts.EngageEmergencyStop();
            }
            else
            {
                halts.ReleaseEmergencyStop();
            }

            action = request.Engage ? "Trading.EmergencyStopEngaged" : "Trading.EmergencyStopReleased";
            break;

        case "MARKET":
            if (string.IsNullOrWhiteSpace(request.Symbol))
            {
                return Results.BadRequest(new { error = "A symbol is required for a market halt." });
            }

            if (request.Engage)
            {
                halts.HaltMarket(request.Symbol);
            }
            else
            {
                halts.ResumeMarket(request.Symbol);
            }

            action = request.Engage ? "Trading.MarketHalted" : "Trading.MarketResumed";
            break;

        case "USER":
            if (request.TargetId is not { } targetUserId || targetUserId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "A user id is required for a user halt." });
            }

            if (request.Engage)
            {
                halts.HaltUser(targetUserId);
            }
            else
            {
                halts.ResumeUser(targetUserId);
            }

            action = request.Engage ? "Trading.UserHalted" : "Trading.UserResumed";
            break;

        case "STRATEGY":
            if (request.TargetId is not { } targetStrategyId || targetStrategyId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "A strategy id is required for a strategy halt." });
            }

            if (request.Engage)
            {
                halts.HaltStrategy(targetStrategyId);
            }
            else
            {
                halts.ResumeStrategy(targetStrategyId);
            }

            action = request.Engage ? "Trading.StrategyHalted" : "Trading.StrategyResumed";
            break;

        case "CLOSEONLY":
            if (request.TargetId is not { } closeOnlyUserId || closeOnlyUserId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "A user id is required for close-only mode." });
            }

            halts.SetCloseOnly(closeOnlyUserId, request.Engage);
            action = request.Engage ? "Trading.CloseOnlyEnabled" : "Trading.CloseOnlyDisabled";
            break;

        case "REDUCEONLY":
            if (request.TargetId is not { } reduceOnlyUserId || reduceOnlyUserId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "A user id is required for reduce-only mode." });
            }

            halts.SetReduceOnly(reduceOnlyUserId, request.Engage);
            action = request.Engage ? "Trading.ReduceOnlyEnabled" : "Trading.ReduceOnlyDisabled";
            break;

        default:
            return Results.BadRequest(new { error = "Unknown halt scope." });
    }

    await auditWriter.WriteAsync(
        new AuditEvent(
            Guid.NewGuid(),
            actorId.Value,
            action,
            "TradingHalt",
            request.TargetId?.ToString() ?? request.Symbol ?? "platform",
            DateTimeOffset.UtcNow,
            null,
            request.Reason,
            Guid.NewGuid().ToString()),
        cancellationToken).ConfigureAwait(false);

    return Results.Ok(new { action, emergencyStop = halts.EmergencyStop });
}).RequireAuthorization(policy => policy.RequireRole(
    nameof(RoleType.Administrator), nameof(RoleType.RiskOfficer)));

app.MapGet("/admin/risk", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Trading safety controls — Exersist Trading</title>
      <style>
        body { margin:0; font-family: Segoe UI, Arial, sans-serif; background:#0b1725; color:#eaf4ff; }
        .container { max-width: 860px; margin: 0 auto; padding: 32px 20px 80px; }
        h1 { font-size: 2rem; margin-bottom: 8px; }
        p.muted { color:#9bb6cd; line-height:1.6; }
        fieldset { border:1px solid #24415d; border-radius:12px; margin:18px 0; padding:16px 18px; }
        legend { color:#62d0ff; font-weight:700; letter-spacing:0.04em; text-transform:uppercase; font-size:12px; }
        label { display:block; margin:10px 0 4px; color:#9bb6cd; font-size:13px; }
        input, select { width:100%; padding:9px 10px; border-radius:8px; border:1px solid #24415d;
                        background:#0f1c2b; color:#eaf4ff; }
        button { margin-top:14px; padding:11px 18px; border-radius:10px; border:1px solid rgba(98,208,255,0.4);
                 background:rgba(98,208,255,0.12); color:#62d0ff; font-weight:700; cursor:pointer; }
        button.stop { border-color:rgba(255,107,107,0.5); background:rgba(255,107,107,0.14); color:#ff8f8f; }
        .state { border-radius:12px; padding:14px 16px; margin:18px 0; line-height:1.5; }
        .state.ok { border:1px solid rgba(98,208,255,0.35); background:rgba(98,208,255,0.08); color:#8fd8ff; }
        .state.halted { border:1px solid rgba(255,107,107,0.45); background:rgba(255,107,107,0.12); color:#ff8f8f; }
        pre { background:#0f1c2b; border:1px solid #24415d; border-radius:12px; padding:14px; white-space:pre-wrap;
              color:#cfe6fa; }
      </style>
    </head>
    <body>
      <div class="container">
        <h1>Trading safety controls</h1>
        <p class="muted">
          Halts take effect immediately for every trading path in this process. The emergency stop
          blocks every new order for every user. Halts never close existing positions on their own;
          use close-only or reduce-only to wind exposure down.
        </p>

        <div id="state" class="state ok">Loading halt state…</div>

        <form id="halt">
          <fieldset>
            <legend>Halt control</legend>
            <label for="scope">Scope</label>
            <select id="scope">
              <option value="EMERGENCY">Platform emergency stop</option>
              <option value="MARKET">Market (symbol)</option>
              <option value="USER">User</option>
              <option value="STRATEGY">Strategy</option>
              <option value="CLOSEONLY">Close-only (user)</option>
              <option value="REDUCEONLY">Reduce-only (user)</option>
            </select>
            <label for="symbol">Symbol (market scope only)</label>
            <input id="symbol" placeholder="XBTUSD" />
            <label for="targetId">Target id (user or strategy scope)</label>
            <input id="targetId" placeholder="00000000-0000-0000-0000-000000000000" />
            <label for="reason">Reason (required, recorded in the audit trail)</label>
            <input id="reason" placeholder="Why this halt is being changed" />
          </fieldset>
          <button type="submit" class="stop" value="engage" id="engage">Engage halt</button>
          <button type="submit" value="release" id="release">Release halt</button>
        </form>

        <pre id="output">No change submitted yet.</pre>
      </div>

      <script>
        const out = document.getElementById('output');
        async function refresh() {
          const res = await fetch('/api/risk/halts');
          const el = document.getElementById('state');
          if (res.status === 401 || res.status === 403) {
            el.className = 'state ok';
            el.textContent = 'Sign in as an administrator or risk officer to view halt state.';
            return;
          }
          const data = await res.json();
          el.className = data.emergencyStop ? 'state halted' : 'state ok';
          el.textContent = data.emergencyStop
            ? 'EMERGENCY STOP ENGAGED. No new order will be accepted for any user.'
            : 'No platform emergency stop. Live trading is disabled platform-wide.';
        }
        async function submit(engage) {
          const body = {
            scope: document.getElementById('scope').value,
            engage: engage,
            symbol: document.getElementById('symbol').value || null,
            targetId: document.getElementById('targetId').value || null,
            reason: document.getElementById('reason').value
          };
          const res = await fetch('/api/risk/halts', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
          });
          out.textContent = JSON.stringify(await res.json(), null, 2);
          await refresh();
        }
        document.getElementById('engage').addEventListener('click', e => { e.preventDefault(); submit(true); });
        document.getElementById('release').addEventListener('click', e => { e.preventDefault(); submit(false); });
        refresh();
      </script>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

app.MapGet("/api/universe/instruments", async (
    UniverseAdminQueryService query,
    IUniverseInstrumentProvider instruments,
    string? purpose,
    string? interval,
    CancellationToken cancellationToken) =>
{
    if (!Enum.TryParse<EligibilityPurpose>(purpose ?? nameof(EligibilityPurpose.Research), true, out var parsedPurpose)
        || parsedPurpose == EligibilityPurpose.None)
    {
        return Results.BadRequest(new { error = "A concrete eligibility purpose is required." });
    }

    if (!Enum.TryParse<CandleInterval>(interval ?? nameof(CandleInterval.OneHour), true, out var parsedInterval)
        || parsedInterval == CandleInterval.None)
    {
        return Results.BadRequest(new { error = "A concrete candle interval is required." });
    }

    var known = await instruments.GetAllAsync(cancellationToken).ConfigureAwait(false);
    var views = await query
        .GetAsync(known, parsedPurpose, parsedInterval, cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(new
    {
        purpose = parsedPurpose.ToString(),
        interval = parsedInterval.ToString(),
        seedVersion = MarketUniverseSeed.Version,
        eligible = views.Count(view => view.Eligible),
        total = views.Count,
        instruments = views
    });
}).RequireAuthorization(policy => policy.RequireRole(
    nameof(RoleType.Administrator), nameof(RoleType.RiskOfficer)));

app.MapGet("/admin/universe", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Instrument universe — Exersist Trading</title>
      <style>
        body { margin:0; font-family: Segoe UI, Arial, sans-serif; background:#0b1725; color:#eaf4ff; }
        .container { max-width: 1100px; margin: 0 auto; padding: 32px 20px 80px; }
        h1 { font-size: 2rem; margin-bottom: 8px; }
        p.muted { color:#9bb6cd; line-height:1.6; }
        table { width:100%; border-collapse: collapse; margin-top: 18px; font-size: 13px; }
        th, td { text-align:left; padding:9px 10px; border-bottom:1px solid #1b3249; vertical-align: top; }
        th { color:#62d0ff; text-transform:uppercase; font-size:11px; letter-spacing:0.05em; }
        .no { color:#ff8f8f; font-weight:700; }
        .yes { color:#7ee787; font-weight:700; }
        .note { border:1px solid rgba(255,196,107,0.4); background:rgba(255,196,107,0.08);
                color:#ffd79a; border-radius:12px; padding:14px 16px; margin:18px 0; line-height:1.55; }
        select { padding:9px 10px; border-radius:8px; border:1px solid #24415d;
                 background:#0f1c2b; color:#eaf4ff; margin-right:10px; }
        details summary { cursor:pointer; color:#9bb6cd; }
        code { color:#8fd8ff; }
      </style>
    </head>
    <body>
      <div class="container">
        <h1>Instrument universe</h1>
        <p class="muted">
          Every configured pair and every gate decision behind its current standing. Membership of the
          research seed grants nothing: an instrument becomes usable only when its gates pass against
          current evidence.
        </p>
        <div class="note">
          Eligibility here is never a prediction and never a recommendation. It states only that the
          platform's data-quality, liquidity, and safety conditions are currently met. Test and live
          purposes are never granted automatically; they require an explicit, audited approval.
        </div>
        <div>
          <select id="purpose">
            <option>Research</option><option>Backtest</option><option>Paper</option>
            <option>SpotTest</option><option>SpotLive</option>
          </select>
          <select id="interval">
            <option>OneMinute</option><option>FiveMinutes</option><option>FifteenMinutes</option>
            <option selected>OneHour</option><option>FourHours</option><option>OneDay</option>
          </select>
        </div>
        <p class="muted" id="summary">Loading…</p>
        <table>
          <thead>
            <tr><th>Symbol</th><th>Class</th><th>State</th><th>Exchange</th>
                <th>Exposure</th><th>Eligible</th><th>Why</th></tr>
          </thead>
          <tbody id="rows"></tbody>
        </table>
      </div>
      <script>
        async function load() {
          const purpose = document.getElementById('purpose').value;
          const interval = document.getElementById('interval').value;
          const response = await fetch(`/api/universe/instruments?purpose=${purpose}&interval=${interval}`);
          const summary = document.getElementById('summary');
          const rows = document.getElementById('rows');
          rows.textContent = '';
          if (!response.ok) {
            summary.textContent = 'Not authorised to view the instrument universe.';
            return;
          }
          const data = await response.json();
          summary.textContent =
            `${data.eligible} of ${data.total} eligible for ${data.purpose} at ${data.interval} ` +
            `(seed ${data.seedVersion}).`;
          for (const item of data.instruments) {
            const tr = document.createElement('tr');
            const failed = item.gates.filter(g => !g.passed).map(g => `${g.gate}: ${g.detail}`);
            const cells = [
              item.exchangeSymbol,
              item.assetClass,
              item.state,
              item.isPresentOnExchange ? item.exchangeStatus : 'absent',
              `${item.exposureDirective} — ${item.exposureReason}`
            ];
            for (const value of cells) {
              const td = document.createElement('td');
              td.textContent = value;
              tr.appendChild(td);
            }
            const eligible = document.createElement('td');
            eligible.textContent = item.eligible ? 'yes' : 'no';
            eligible.className = item.eligible ? 'yes' : 'no';
            tr.appendChild(eligible);
            const why = document.createElement('td');
            const details = document.createElement('details');
            const sum = document.createElement('summary');
            sum.textContent = failed.length ? `${failed.length} gate(s) failing` : 'all gates passed';
            details.appendChild(sum);
            for (const line of failed) {
              const div = document.createElement('div');
              div.textContent = line;
              details.appendChild(div);
            }
            why.appendChild(details);
            tr.appendChild(why);
            rows.appendChild(tr);
          }
        }
        document.getElementById('purpose').addEventListener('change', load);
        document.getElementById('interval').addEventListener('change', load);
        load();
      </script>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

// Orders and positions for the signed-in user only. There is no user id parameter, so one
// user cannot read another user's trading activity.
app.MapGet("/api/orders", async (
    ClaimsPrincipal principal,
    IOrderRepository orders,
    IPositionRepository positions,
    string? mode,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    // An unrecognised mode resolves to Paper rather than to "everything". A
    // request that cannot be understood must not be answered with the real
    // book alongside the simulated one.
    var selectedMode = Enum.TryParse<TradingMode>(mode, ignoreCase: true, out var parsedMode)
        ? parsedMode
        : TradingMode.Paper;

    var ownedOrders = (await orders.ListAsync(userId.Value, cancellationToken).ConfigureAwait(false))
        .Where(order => order.Mode == selectedMode)
        .ToList();
    var ownedPositions = (await positions.ListOpenAsync(userId.Value, cancellationToken).ConfigureAwait(false))
        .Where(position => position.Mode == selectedMode)
        .ToList();

    return Results.Ok(new
    {
        tradingMode = selectedMode.ToString(),
        disclaimer = selectedMode == TradingMode.Paper ? OrdersDisclaimer : LiveBookDisclaimer,
        frozen = ownedOrders.Count(order => order.RequiresReconciliation),
        orders = ownedOrders.Select(order => new
        {
            order.Id,
            order.Symbol,
            side = order.Side.ToString(),
            type = order.Type.ToString(),
            state = order.State.ToString(),
            order.Quantity,
            order.FilledQuantity,
            order.RemainingQuantity,
            order.Price,
            order.ClientOrderId,
            order.ExchangeOrderId,
            order.ReduceOnly,
            order.CloseOnly,
            order.RequiresReconciliation,
            order.ReconciliationReason,
            order.CanResubmit,
            order.CreatedAtUtc,
            order.LastTransitionAtUtc
        }),
        positions = ownedPositions.Select(position => new
        {
            position.Id,
            position.Symbol,
            direction = position.Direction.ToString(),
            status = position.Status.ToString(),
            position.Quantity,
            position.EntryPrice,
            position.MarkPrice,
            position.UnrealizedPnl,
            position.PermitsIncrease,
            position.OpenedAtUtc
        })
    });
}).RequireAuthorization();

// Outstanding reconciliations. Every record listed here blocks resubmission of its order until
// the exchange has proven what actually happened.
app.MapGet("/api/orders/reconciliations", async (
    ClaimsPrincipal principal,
    IOrderReconciliationRepository reconciliations,
    IOrderRepository orders,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var ownedOrderIds = (await orders.ListAsync(userId.Value, cancellationToken).ConfigureAwait(false))
        .Select(order => order.Id)
        .ToHashSet();

    var unresolved = await reconciliations.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        disclaimer = OrdersDisclaimer,
        records = unresolved
            .Where(record => ownedOrderIds.Contains(record.OrderId))
            .Select(record => new
            {
                record.Id,
                record.OrderId,
                record.ExchangeOrderId,
                observedStatus = record.ObservedStatus.ToString(),
                record.ObservedAtUtc,
                record.Source,
                record.RequiresManualReview,
                record.RequiresResolutionBeforeResubmission,
                record.IsResolved
            })
    });
}).RequireAuthorization();

app.MapGet("/orders", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Orders and reconciliation</title>
    </head>
    <body>
      <main>
        <h1>Orders and reconciliation</h1>
        <p class="lede">
          Every order placed on your behalf, together with any order whose exchange outcome could
          not be established. Paper trading is the only mode currently enabled.
        </p>

        <div class="notice">
          <strong>Unknown outcomes are never retried.</strong>
          An order whose exchange status is unknown may already be live. It stays frozen until the
          exchange confirms what happened, because resubmitting on a guess would double real
          exposure.
        </div>

        <h2>Frozen awaiting reconciliation</h2>
        <div id="reconciliations"><p class="empty">Loading.</p></div>

        <h2>Orders</h2>
        <div id="orders"><p class="empty">Loading.</p></div>

        <h2>Open positions</h2>
        <div id="positions"><p class="empty">Loading.</p></div>
      </main>

      <script>
        // Every cell is written with textContent so no exchange or user supplied
        // string can be interpreted as markup.
        function cell(row, text, className) {
          var td = document.createElement('td');
          td.textContent = text === null || text === undefined ? '-' : String(text);
          if (className) { td.className = className; }
          row.appendChild(td);
          return td;
        }

        function pill(row, text, tone) {
          var td = document.createElement('td');
          var span = document.createElement('span');
          span.className = 'pill ' + tone;
          span.textContent = text;
          td.appendChild(span);
          row.appendChild(td);
        }

        function table(container, headers, rows, builder, emptyText) {
          container.textContent = '';
          if (!rows.length) {
            var p = document.createElement('p');
            p.className = 'empty';
            p.textContent = emptyText;
            container.appendChild(p);
            return;
          }

          var t = document.createElement('table');
          var thead = document.createElement('thead');
          var hr = document.createElement('tr');
          headers.forEach(function (h) {
            var th = document.createElement('th');
            th.textContent = h;
            hr.appendChild(th);
          });
          thead.appendChild(hr);
          t.appendChild(thead);

          var tbody = document.createElement('tbody');
          rows.forEach(function (item) {
            var tr = document.createElement('tr');
            builder(tr, item);
            tbody.appendChild(tr);
          });
          t.appendChild(tbody);
          container.appendChild(t);
        }

        function unauthorized(container) {
          container.textContent = '';
          var p = document.createElement('p');
          p.className = 'empty';
          p.textContent = 'Sign in to view your orders.';
          container.appendChild(p);
        }

        async function load() {
          var ordersEl = document.getElementById('orders');
          var positionsEl = document.getElementById('positions');
          var reconEl = document.getElementById('reconciliations');

          var response = await fetch('/api/orders', { headers: { 'Accept': 'application/json' } });
          if (response.status === 401) {
            unauthorized(ordersEl);
            unauthorized(positionsEl);
            unauthorized(reconEl);
            return;
          }

          var data = await response.json();

          table(ordersEl,
            ['Symbol', 'Side', 'Type', 'State', 'Quantity', 'Filled', 'Price', 'Client order id'],
            data.orders,
            function (tr, o) {
              cell(tr, o.symbol);
              cell(tr, o.side);
              cell(tr, o.type);
              pill(tr, o.state, o.requiresReconciliation ? 'bad' : 'ok');
              cell(tr, o.quantity, 'numeric');
              cell(tr, o.filledQuantity, 'numeric');
              cell(tr, o.price, 'numeric');
              cell(tr, o.clientOrderId);
            },
            'No orders yet.');

          table(positionsEl,
            ['Symbol', 'Direction', 'Status', 'Quantity', 'Entry', 'Mark', 'Unrealized'],
            data.positions,
            function (tr, p) {
              cell(tr, p.symbol);
              cell(tr, p.direction);
              pill(tr, p.status, p.permitsIncrease ? 'ok' : 'warn');
              cell(tr, p.quantity, 'numeric');
              cell(tr, p.entryPrice, 'numeric');
              cell(tr, p.markPrice, 'numeric');
              cell(tr, p.unrealizedPnl, 'numeric');
            },
            'No open positions.');

          var recon = await fetch('/api/orders/reconciliations', { headers: { 'Accept': 'application/json' } });
          if (recon.status === 401) {
            unauthorized(reconEl);
            return;
          }

          var reconData = await recon.json();
          table(reconEl,
            ['Order', 'Observed status', 'Observed at', 'Source', 'Blocks resubmission'],
            reconData.records,
            function (tr, r) {
              cell(tr, r.orderId);
              pill(tr, r.observedStatus, 'bad');
              cell(tr, r.observedAtUtc);
              cell(tr, r.source);
              cell(tr, r.requiresResolutionBeforeResubmission ? 'Yes' : 'No');
            },
            'Nothing is awaiting reconciliation.');
        }

        load();
      </script>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

// ---------------------------------------------------------------------------
// Exchange account connection (Phase 2.5)
//
// These endpoints are the only place a user's API credential enters the
// platform. The secret is handed straight to the secret store and a reference
// is kept in SQL. No endpoint here returns a credential, and there is no
// endpoint that reads one back out: once stored, a secret leaves only through
// the connector that signs a request with it.
// ---------------------------------------------------------------------------

app.MapGet("/api/exchange/accounts", async (
    ClaimsPrincipal principal,
    IExchangeAccountConnectionService connections,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var accounts = await connections.ListAsync(userId.Value, cancellationToken).ConfigureAwait(false);

    // Only safe metadata is projected. CredentialReference is the secret's
    // name and is deliberately omitted so the browser never learns where a
    // credential lives.
    return Results.Ok(accounts.Select(account => new
    {
        id = account.Id,
        exchange = account.ExchangeKind.ToString(),
        displayName = account.DisplayName,
        status = account.Status.ToString(),
        stage = account.Stage.ToString(),
        canTrade = account.CanTrade,
        canReachExchange = account.CanReachExchange,
        createdAtUtc = account.CreatedAtUtc,
        lastValidatedAtUtc = account.LastValidatedAtUtc
    }));
}).RequireAuthorization();

// ---------------------------------------------------------------------------
// Market data (Phase 3.2). Candles are read from the venue's public endpoint,
// so this route involves no credential and no user-owned exchange account.
// Every candle reports whether it is closed, because a bar still forming must
// never be mistaken for a finished one by anything that draws or trades on it.
// ---------------------------------------------------------------------------

app.MapGet("/api/marketdata/candles", async (
    string symbol,
    string interval,
    IHistoricalCandleSource candleSource,
    CancellationToken cancellationToken) =>
{
    if (!Enum.TryParse<CandleInterval>(interval, ignoreCase: true, out var parsedInterval)
        || parsedInterval == CandleInterval.None)
    {
        return Results.BadRequest(new { error = "UnknownInterval", message = "Supported intervals: OneMinute, FiveMinutes, TenMinutes, FifteenMinutes, ThirtyMinutes, OneHour, FourHours, OneDay." });
    }

    try
    {
        var candles = await candleSource
            .FetchAsync(symbol, parsedInterval, DateTimeOffset.UnixEpoch, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(candles.Select(candle => new
        {
            openTimeUtc = candle.OpenTimeUtc,
            closeTimeUtc = candle.CloseTimeUtc,
            open = candle.Open,
            high = candle.High,
            low = candle.Low,
            close = candle.Close,
            volume = candle.Volume,
            isClosed = candle.IsClosed,
            isDerived = candle.IsDerived
        }));
    }
    catch (MarketDataIntervalNotSupportedException exception)
    {
        // Reported distinctly so a caller can route to the derived-candle
        // builder rather than believing the venue had no data.
        return Results.BadRequest(new { error = "IntervalNotSupportedByVenue", message = exception.Message });
    }
    catch (MarketDataSourceException exception)
    {
        return Results.BadRequest(new { error = "MarketDataUnavailable", message = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = "InvalidRequest", message = exception.Message });
    }
}).RequireAuthorization();

// ---------------------------------------------------------------------------
// Paper trading (Phase 5). This is the only route in the application that
// creates an order or a position, and it creates them with fake funds only.
// The trading mode is fixed inside the service; there is no request field,
// header, or configuration switch that can redirect this route to a venue.
// ---------------------------------------------------------------------------

// The pairs a venue will trade, with the order filters it enforces. Public
// reference data: no credential is used and no user data is involved.
app.MapGet("/api/marketdata/pairs", async (
    ITradablePairSource pairs,
    CancellationToken cancellationToken) =>
{
    try
    {
        var available = await pairs.ListAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(available.Select(pair => new
        {
            pair.Symbol,
            pair.DisplayName,
            pair.BaseAsset,
            pair.QuoteAsset,
            pair.IsActive,
            pair.MinimumQuantity,
            pair.QuantityStep,
            pair.PriceTick
        }));
    }
    catch (MarketDataSourceException exception)
    {
        // Reported as a failure rather than an empty list: "the venue trades
        // nothing" and "the request failed" lead to opposite conclusions.
        return Results.BadRequest(new { error = "MarketDataUnavailable", message = exception.Message });
    }
}).RequireAuthorization();

// Open positions priced against the last closed candle. Every figure is
// computed server-side in decimal; the browser receives finished numbers so it
// never derives a profit or loss figure in binary floating point.
app.MapGet("/api/paper/positions", async (
    ClaimsPrincipal principal,
    IPositionValuationService valuation,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var valued = await valuation
        .ValueOpenPositionsAsync(userId.Value, cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(new
    {
        tradingMode = "Paper",
        disclaimer = OrdersDisclaimer,
        // This endpoint is the paper book by name, so it is filtered to the
        // paper book by value. A real position must never be listed here.
        positions = valued.Where(item => item.Position.Mode == TradingMode.Paper).Select(item => new
        {
            item.Position.Id,
            item.Position.Symbol,
            direction = item.Position.Direction == PositionDirection.DirectionShort ? "Short" : "Long",
            status = item.Position.Status.ToString(),
            item.Position.Quantity,
            item.Position.EntryPrice,
            markPrice = item.MarkPrice,
            unrealisedPnl = item.UnrealisedPnl,
            unrealisedPercent = item.UnrealisedPercent,
            breakEvenPrice = item.BreakEvenPriceExcludingFees,
            stopLossPrice = item.Position.StopLossPrice,
            takeProfitPrice = item.Position.TakeProfitPrice,
            pricedAtUtc = item.PricedAtUtc,
            priceIsStale = item.PriceIsStale,
            priceUnavailableReason = item.PriceUnavailableReason,
            item.Position.OpenedAtUtc
        })
    });
}).RequireAuthorization();

// The live book, valued the same way the paper book is.
//
// It is a separate route from the paper one, filtered by mode, so a simulated
// position can never appear among real ones and vice versa. Mixing them would
// present fake exposure as real, which is the most dangerous thing this
// application could display.
app.MapGet("/api/live/positions", async (
    ClaimsPrincipal principal,
    IPositionValuationService valuation,
    LiveOrderSyncService sync,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var staleWarning = (string?)null;

    try
    {
        // Positions follow observed fills, so the exchange is asked before the
        // book is read. Without this a working order would never become a
        // position until something else happened to look.
        await sync.SyncAsync(userId.Value, cancellationToken).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // A failed refresh must not hide the positions themselves.
    catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
    {
        staleWarning = "The exchange could not be reached, so this book may be out of date.";
    }

    var valued = await valuation
        .ValueOpenPositionsAsync(userId.Value, cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(new
    {
        tradingMode = "Live",
        disclaimer = LiveOrdersDisclaimer,
        staleWarning,
        positions = valued.Where(item => item.Position.Mode == TradingMode.Live).Select(item => new
        {
            item.Position.Id,
            item.Position.Symbol,
            direction = item.Position.Direction == PositionDirection.DirectionShort ? "Short" : "Long",
            status = item.Position.Status.ToString(),
            item.Position.Quantity,
            item.Position.EntryPrice,
            markPrice = item.MarkPrice,
            unrealisedPnl = item.UnrealisedPnl,
            unrealisedPercent = item.UnrealisedPercent,
            breakEvenPrice = item.BreakEvenPriceExcludingFees,
            stopLossPrice = item.Position.StopLossPrice,
            takeProfitPrice = item.Position.TakeProfitPrice,
            pricedAtUtc = item.PricedAtUtc,
            priceIsStale = item.PriceIsStale,
            priceUnavailableReason = item.PriceUnavailableReason,
            item.Position.OpenedAtUtc
        })
    });
}).RequireAuthorization();

// Sets or clears the stop and target on an open paper position. The levels are
// validated against the position's direction, so a stop on the profitable side
// is refused rather than stored and triggered immediately.
app.MapPost("/api/paper/positions/{positionId:guid}/exits", async (
    Guid positionId,
    SetProtectiveExitsRequest request,
    ClaimsPrincipal principal,
    IPositionRepository positions,
    IAuditEventWriter auditWriter,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    // Read through the user-scoped list, so a position belonging to another
    // user reads as not found rather than forbidden.
    var open = await positions.ListOpenAsync(userId.Value, cancellationToken).ConfigureAwait(false);
    var position = open.FirstOrDefault(candidate => candidate.Id == positionId);

    if (position is null)
    {
        return Results.NotFound(new { error = "PositionNotFound", message = "No open position with that id." });
    }

    try
    {
        position.SetProtectiveExits(request.StopLossPrice, request.TakeProfitPrice, timeProvider.GetUtcNow());
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.BadRequest(new { error = "InvalidExitLevel", message = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = "PositionNotOpen", message = exception.Message });
    }

    await positions.UpdateAsync(position, cancellationToken).ConfigureAwait(false);

    await auditWriter.WriteAsync(
        new AuditEvent(
            Guid.NewGuid(),
            userId.Value,
            "PaperProtectiveExitsSet",
            targetType: "PaperPosition",
            targetId: positionId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            occurredAtUtc: timeProvider.GetUtcNow(),
            before: null,
            after: FormattableString.Invariant($"Stop {position.StopLossPrice}, target {position.TakeProfitPrice}."),
            correlationId: null),
        cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        position.Id,
        stopLossPrice = position.StopLossPrice,
        takeProfitPrice = position.TakeProfitPrice
    });
}).RequireAuthorization();

// Runs the stop and target check over the user's open positions.
//
// This is an explicit call rather than a side effect of reading positions, so
// a read never changes state. A background worker must own this before exits
// can be relied on while nobody is looking at the page; until then a level is
// only enforced when this route runs.
app.MapPost("/api/paper/exits/evaluate", async (
    ClaimsPrincipal principal,
    IProtectiveExitEvaluator evaluator,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var fills = await evaluator.EvaluateAsync(userId.Value, cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        tradingMode = "Paper",
        closed = fills.Count,
        fills = fills.Select(fill => new
        {
            fill.Position.Id,
            fill.Position.Symbol,
            kind = fill.Kind.ToString(),
            fill.ExitPrice,
            fill.RealisedPnl,
            fill.BothLevelsTouched,
            fill.CandleCloseTimeUtc
        })
    });
}).RequireAuthorization();

app.MapPost("/api/paper/orders", async (
    SubmitPaperOrderRequest request,
    ClaimsPrincipal principal,
    PaperTradingService paperTrading,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    // The owner comes from the signed-in principal, never from the request
    // body, so a caller cannot trade into another user's book.
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    if (!Enum.TryParse<OrderSide>(request.Side, ignoreCase: true, out var side))
    {
        return Results.BadRequest(new { error = "InvalidSide", message = "Side must be Buy or Sell." });
    }

    PaperTradeResult result;
    try
    {
        result = await paperTrading
            .SubmitAsync(userId.Value, request.Symbol, side, request.Quantity, request.ClientOrderId, cancellationToken)
            .ConfigureAwait(false);
    }
    catch (DbUpdateException exception)
    {
        // A persistence fault can happen after one part of a simulated fill
        // was written. Do not report a clean rejection or invite an immediate
        // duplicate: the user must refresh the paper book first.
        Trading.Web.PaperOrderEndpointLog.PersistenceFailure(
            loggerFactory.CreateLogger("Trading.Web.PaperOrderEndpoint"),
            exception);
        return Results.Json(
            new
            {
                error = "PaperTradingPersistenceUnavailable",
                message = "The paper order could not be recorded completely. Refresh orders and positions before trying again."
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!result.Succeeded)
    {
        // Each refusal keeps its own outcome so the operator can tell a halt
        // from stale data from a duplicate. Collapsing them into one generic
        // failure would hide which safety control actually fired.
        var status = result.Outcome switch
        {
            PaperTradeOutcome.Duplicate => StatusCodes.Status409Conflict,
            PaperTradeOutcome.Blocked => StatusCodes.Status403Forbidden,
            PaperTradeOutcome.PriceStale or PaperTradeOutcome.PriceUnavailable => StatusCodes.Status503ServiceUnavailable,
            PaperTradeOutcome.NoConnectedExchange => StatusCodes.Status412PreconditionFailed,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(
            new { error = result.Outcome.ToString(), message = result.Message },
            statusCode: status);
    }

    var order = result.Order!;
    return Results.Ok(new
    {
        tradingMode = "Paper",
        disclaimer = OrdersDisclaimer,
        order = new
        {
            order.Id,
            order.ClientOrderId,
            order.Symbol,
            side = order.Side.ToString(),
            state = order.State.ToString(),
            order.Quantity,
            order.FilledQuantity,
            fillPrice = order.Price
        },
        position = result.Position is null ? null : new
        {
            result.Position.Symbol,
            direction = result.Position.Direction.ToString(),
            result.Position.Quantity,
            result.Position.EntryPrice,
            status = result.Position.Status.ToString()
        }
    });
}).RequireAuthorization();

// Lists the user's live orders, after asking the exchange what actually
// happened to each working one.
//
// The refresh runs before the read because the platform's own record of a
// working order is only ever a claim about the past. Showing it without asking
// would present the moment of submission as though it were the present.
app.MapGet("/api/live/orders", async (
    ClaimsPrincipal principal,
    IOrderRepository orders,
    LiveOrderSyncService sync,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var synced = 0;
    var syncFailure = (string?)null;

    try
    {
        synced = await sync.SyncAsync(userId.Value, cancellationToken).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // A failed refresh must not hide the orders themselves.
    catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
    {
        syncFailure = "The exchange could not be reached, so these states may be out of date.";
    }

    var all = await orders.ListAsync(userId.Value, cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        tradingMode = "Live",
        disclaimer = LiveOrdersDisclaimer,
        refreshed = synced,
        staleWarning = syncFailure,
        orders = all
            .Where(order => order.Mode == TradingMode.Live)
            .OrderByDescending(order => order.CreatedAtUtc)
            .Select(order => new
            {
                order.Id,
                order.ClientOrderId,
                order.Symbol,
                side = order.Side.ToString(),
                state = order.State.ToString(),
                order.Quantity,
                order.FilledQuantity,
                price = order.Price,
                limitPrice = order.Price,
                order.CreatedAtUtc,
                requiresReconciliation = order.State == OrderState.Failed
            })
    });
}).RequireAuthorization();

// Submits a real order with real money.
//
// Every refusal keeps its own outcome and its own status code. The one that
// matters most is Unknown: it is answered with 202 and an explicit instruction
// not to retry, because a retry after an unestablished submission is how one
// intended position becomes two.
app.MapPost("/api/live/orders", async (
    SubmitLiveOrderRequest request,
    ClaimsPrincipal principal,
    ILiveTradingService liveTrading,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    ArgumentNullException.ThrowIfNull(request);

    if (!Enum.TryParse<OrderSide>(request.Side, ignoreCase: true, out var side))
    {
        return Results.BadRequest(new { error = "InvalidSide", message = "Side must be Buy or Sell." });
    }

    var result = await liveTrading
        .SubmitAsync(
            userId.Value,
            request.ExchangeAccountId,
            request.Symbol,
            side,
            request.Quantity,
            request.ClientOrderId,
            cancellationToken)
        .ConfigureAwait(false);

    if (result.Outcome == LiveTradeOutcome.Unknown)
    {
        return Results.Json(
            new
            {
                error = result.Outcome.ToString(),
                message = result.Message,
                orderId = result.Order?.Id,
                clientOrderId = result.Order?.ClientOrderId,
                action = "Do not resubmit this order. Its state is being reconciled with the exchange."
            },
            statusCode: StatusCodes.Status202Accepted);
    }

    if (!result.Succeeded)
    {
        var status = result.Outcome switch
        {
            LiveTradeOutcome.Duplicate => StatusCodes.Status409Conflict,
            LiveTradeOutcome.Blocked or LiveTradeOutcome.RiskBlocked => StatusCodes.Status403Forbidden,
            LiveTradeOutcome.PriceUnavailable => StatusCodes.Status503ServiceUnavailable,
            LiveTradeOutcome.InstrumentUnavailable => StatusCodes.Status503ServiceUnavailable,
            LiveTradeOutcome.AccountNotEligible => StatusCodes.Status412PreconditionFailed,
            LiveTradeOutcome.Rejected => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(
            new { error = result.Outcome.ToString(), message = result.Message },
            statusCode: status);
    }

    var order = result.Order!;
    return Results.Ok(new
    {
        tradingMode = "Live",
        disclaimer = LiveOrdersDisclaimer,
        message = result.Message,
        order = new
        {
            order.Id,
            order.ClientOrderId,
            order.Symbol,
            side = order.Side.ToString(),
            state = order.State.ToString(),
            order.Quantity,
            order.FilledQuantity,
            limitPrice = order.Price
        }
    });
}).RequireAuthorization();

// What this deployment can actually do, per mode. The page asks rather than
// assumes, so the tab it offers matches what the server will accept.
app.MapGet("/api/trading/modes", async (
    ClaimsPrincipal principal,
    IExchangeAccountConnectionService connections,
    ILiveExecutionRouteProvider routes,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var accounts = await connections.ListAsync(userId.Value, cancellationToken).ConfigureAwait(false);
    var connected = accounts.Where(account => account.CanTrade).ToList();
    var liveRoute = connected.Exists(account => routes.HasRouteFor(account.ExchangeKind));

    return Results.Ok(new
    {
        paper = new
        {
            available = connected.Count > 0,
            reason = connected.Count > 0
                ? null
                : "Connect an exchange account to enable paper trading."
        },
        live = new
        {
            available = liveRoute && connected.Exists(account =>
                account.CanReachExchange && account.Stage != TradingStage.Paper),
            // Each reason names the specific thing that is missing, so a user
            // is never told "unavailable" when the only obstacle is a promotion
            // they can request themselves.
            reason = !liveRoute
                ? "Live trading is unavailable: this deployment has no execution route to the exchange, so no order could reach it."
                : connected.TrueForAll(account => account.Stage == TradingStage.Paper)
                    ? "No account has been promoted out of paper. Promote an account to Proving to send a first small real order."
                    : null
        },
        accounts = connected.Select(account => new
        {
            id = account.Id,
            displayName = account.DisplayName,
            exchange = account.ExchangeKind.ToString(),
            stage = account.Stage.ToString(),
            canReachExchange = account.CanReachExchange
        })
    });
}).RequireAuthorization();

app.MapPost("/api/exchange/accounts/{accountId:guid}/stage", async (
    Guid accountId,
    ChangeTradingStageRequest request,
    ClaimsPrincipal principal,
    IExchangeAccountStageService stages,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    ArgumentNullException.ThrowIfNull(request);

    // Returning to paper is a risk-reducing action and is always available.
    if (string.Equals(request.Stage, "Paper", StringComparison.OrdinalIgnoreCase))
    {
        var reverted = await stages
            .ReturnToPaperAsync(userId.Value, accountId, cancellationToken)
            .ConfigureAwait(false);

        return reverted.IsSuccess
            ? Results.Ok(new { stage = reverted.Stage.ToString(), message = reverted.Message })
            : Results.NotFound(new { error = reverted.Outcome.ToString(), message = reverted.Message });
    }

    if (!Enum.TryParse<TradingStage>(request.Stage, ignoreCase: true, out var target))
    {
        return Results.BadRequest(new { error = "InvalidStage", message = "Stage must be Paper, Proving or Live." });
    }

    var result = await stages
        .PromoteAsync(userId.Value, accountId, target, cancellationToken)
        .ConfigureAwait(false);

    if (result.IsSuccess)
    {
        return Results.Ok(new { stage = result.Stage.ToString(), message = result.Message });
    }

    var status = result.Outcome switch
    {
        TradingStageChangeOutcome.AccountNotFound => StatusCodes.Status404NotFound,
        // The capability is genuinely absent rather than forbidden to this
        // user, so it is reported as unimplemented rather than as a refusal
        // they could argue with.
        TradingStageChangeOutcome.LiveRouteUnavailable => StatusCodes.Status501NotImplemented,
        TradingStageChangeOutcome.LiveTradingNotEntitled => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status409Conflict
    };

    return Results.Json(
        new { error = result.Outcome.ToString(), message = result.Message, stage = result.Stage.ToString() },
        statusCode: status);
}).RequireAuthorization();

app.MapPost("/api/exchange/accounts", async (
    ConnectExchangeAccountRequest request,
    ClaimsPrincipal principal,
    IExchangeAccountConnectionService connections,
    IAuditEventWriter auditWriter,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    if (request is null
        || string.IsNullOrWhiteSpace(request.DisplayName)
        || string.IsNullOrWhiteSpace(request.ApiKey)
        || string.IsNullOrWhiteSpace(request.ApiSecret))
    {
        return Results.BadRequest(new { error = "Display name, API key and API secret are required." });
    }

    var credential = new ExchangeCredential(request.ApiKey, request.ApiSecret);

    var result = await connections.ConnectAsync(
        userId.Value,
        ExchangeKind.Kraken,
        request.DisplayName,
        credential,
        cancellationToken).ConfigureAwait(false);

    if (!result.IsSuccess)
    {
        // The reason is written by the application layer and carries no
        // credential material, so it is safe to show the user.
        await auditWriter.WriteAsync(
            new AuditEvent(
                Guid.NewGuid(),
                userId.Value,
                "ExchangeAccountConnectionRejected",
                "ExchangeAccount",
                result.Outcome.ToString(),
                DateTimeOffset.UtcNow,
                null,
                result.FailureReason,
                Guid.NewGuid().ToString()),
            cancellationToken).ConfigureAwait(false);

        return Results.BadRequest(new
        {
            outcome = result.Outcome.ToString(),
            error = result.FailureReason
        });
    }

    var account = result.Account!;

    await auditWriter.WriteAsync(
        new AuditEvent(
            Guid.NewGuid(),
            userId.Value,
            "ExchangeAccountConnected",
            "ExchangeAccount",
            account.Id.ToString(),
            DateTimeOffset.UtcNow,
            null,
            $"{account.ExchangeKind} account connected in {account.Stage} stage.",
            Guid.NewGuid().ToString()),
        cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        id = account.Id,
        exchange = account.ExchangeKind.ToString(),
        displayName = account.DisplayName,
        status = account.Status.ToString(),
        stage = account.Stage.ToString()
    });
}).RequireAuthorization();

app.MapDelete("/api/exchange/accounts/{accountId:guid}", async (
    Guid accountId,
    ClaimsPrincipal principal,
    IExchangeAccountConnectionService connections,
    IAuditEventWriter auditWriter,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var removed = await connections
        .DisconnectAsync(userId.Value, accountId, cancellationToken)
        .ConfigureAwait(false);

    if (!removed)
    {
        return Results.NotFound();
    }

    await auditWriter.WriteAsync(
        new AuditEvent(
            Guid.NewGuid(),
            userId.Value,
            "ExchangeAccountDisconnected",
            "ExchangeAccount",
            accountId.ToString(),
            DateTimeOffset.UtcNow,
            null,
            "Account disconnected and stored credential removed.",
            Guid.NewGuid().ToString()),
        cancellationToken).ConfigureAwait(false);

    return Results.Ok(new { disconnected = true });
}).RequireAuthorization();

app.MapGet("/positions", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <script defer src="/positions.js"></script>
      <title>Positions</title>
    </head>
    <body>
      <main>
        <h1>Open positions</h1>
        <p class="lede">
          Every pair you currently hold, valued at the last closed candle. Select a row to open
          that pair on the chart.
        </p>

        <div class="notice">
          <strong>Paper positions. Fake funds.</strong>
          Fees, spread and slippage are not modelled, so break even is the raw entry price and
          these results are an upper bound on what the same trades would have returned live.
          Nothing here is a prediction and no strategy is guaranteed to be profitable.
        </div>

        <div class="toolbar">
          <button id="refresh" type="button">Refresh</button>
          <button id="evaluate" type="button" title="Check whether any stop or target was reached">
            Check stops and targets
          </button>
        </div>

        <div id="status" class="notice">Loading.</div>
        <div id="summary"></div>
        <div id="positions"><p class="empty">Loading.</p></div>
      </main>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

app.MapGet("/chart", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <script defer src="/chart.js"></script>
      <title>Chart</title>
    </head>
    <body>
      <main>
        <h1>Chart</h1>
        <p class="lede">
          Price history from Kraken with your open positions and working orders marked on it.
          Orders placed here are paper orders filled with fake funds against the last closed
          candle. Nothing on this page can reach an exchange.
        </p>

        <div class="notice">
          <strong>The bar still forming is drawn dashed.</strong>
          A partial bar looks like a finished one on most charts, which invites reading a signal
          off a candle that has not closed. Strategies here only act on closed candles, and the
          chart shows the same distinction.
        </div>

        <div id="status" class="notice">Loading.</div>

        <div class="chart-toolbar">
          <div class="pair-selector">
            <label for="pairSearch">Search pairs</label>
            <input id="pairSearch" type="search" placeholder="Search BTC, EUR, XBTUSD" autocomplete="off"
                   aria-autocomplete="list" aria-controls="pairResults" />
            <!-- The select remains the canonical selected value for the chart
                 and order ticket. Pair search only chooses from its active
                 Kraken-backed options; it never accepts arbitrary symbols. -->
            <select id="symbol" class="visually-hidden" aria-hidden="true" tabindex="-1"></select>
            <div id="pairResults" class="pair-results" role="listbox" aria-label="Matching active Kraken pairs"></div>
            <p id="pairSearchEmpty" class="pair-search-empty" aria-live="polite"></p>
          </div>

          <div class="chart-actions">
            <label for="interval">Interval</label>
            <select id="interval">
              <option value="OneMinute">1 minute</option>
              <option value="FiveMinutes">5 minutes</option>
              <option value="TenMinutes">10 minutes</option>
              <option value="FifteenMinutes">15 minutes</option>
              <option value="ThirtyMinutes">30 minutes</option>
              <option value="OneHour" selected>1 hour</option>
              <option value="FourHours">4 hours</option>
              <option value="OneDay">1 day</option>
            </select>

            <button id="load" type="button">Load</button>
            <button id="zoomIn" type="button" title="Show fewer bars">Zoom in</button>
            <button id="zoomOut" type="button" title="Show more bars">Zoom out</button>
            <button id="zoomReset" type="button" title="Back to the most recent bars">Reset</button>
          </div>
        </div>

        <div class="chart-stage">
          <canvas id="chart" width="1100" height="460"
                  style="width:100%;height:460px;background:#14171c;border-radius:6px;"></canvas>
        </div>
        <p id="legend" class="empty"></p>
        <p class="empty">Scroll on the chart to zoom. Drag it sideways to pan.</p>

        <h2>Position on this pair</h2>
        <div id="positions"><p class="empty">Loading.</p></div>
        <p id="pairFilters" class="empty"></p>

        <h2>Trade</h2>

        <!-- The chart above is shared. Only the book and the ticket change
             with the tab, because the market data is the same market data
             whichever book you are trading into. -->
        <div class="tabs" role="tablist">
          <button id="modePaper" class="tab active" type="button" role="tab">Paper</button>
          <button id="modeLive" class="tab" type="button" role="tab">Live</button>
        </div>

        <div id="modeNotice" class="notice">
          <strong>Fake funds. No exchange is contacted.</strong>
          The fill is priced at the close of the last closed candle, so it reflects a price that
          actually settled. It does not model spread, slippage, fees or partial fills, so a paper
          result is an upper bound on what the same decision would have returned live.
        </div>

        <div class="toolbar" id="tradeTicket">
          <label for="tradeSide">Side</label>
          <select id="tradeSide">
            <option value="Buy">Buy</option>
            <option value="Sell">Sell</option>
          </select>

          <label for="tradeQuantity">Quantity</label>
          <input id="tradeQuantity" value="0.01" size="10" inputmode="decimal" autocomplete="off" />

          <button id="submitTrade" type="button">Submit paper order</button>
        </div>

        <div id="tradeStatus" class="empty">No paper order submitted yet.</div>

        <p class="empty">
          Kraken serves no ten-minute candle. That interval is refused rather than answered with a
          different size; a ten-minute bar is built from ten closed one-minute bars and marked as
          derived.
        </p>
      </main>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

app.MapGet("/exchange", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Exchange accounts</title>
    </head>
    <body>
      <main>
        <h1>Exchange accounts</h1>
        <p class="lede">
          Connect your own Kraken account with an API key. You enter the key once. It is
          encrypted and stored server side, so it is still connected the next time you sign in.
        </p>

        <div class="notice">
          <strong>Create the key without withdrawal permission.</strong>
          Fremvo Trading never holds, transfers or withdraws funds, and no withdrawal code
          exists anywhere in the product. A key that carries withdrawal permission is refused
          outright rather than stored. Your secret is never written to the database, never
          logged, and never sent back to the browser.
        </div>

        <h2>Trading stage</h2>
        <p>
          Simulated and real money are a property of the account, not a display option, so a
          view toggle can never turn fake orders into real ones.
        </p>
        <table>
          <thead>
            <tr><th>Stage</th><th>Money at risk</th><th>Reaches Kraken</th><th>Availability</th></tr>
          </thead>
          <tbody>
            <tr>
              <td><span class="badge badge-ok">Paper</span></td>
              <td>Simulated only</td>
              <td>No</td>
              <td>Available now. Every account starts here.</td>
            </tr>
            <tr>
              <td><span class="badge badge-warn">Proving</span></td>
              <td>Real, minimum size</td>
              <td>Yes</td>
              <td>Requires an enabled route, an operator-approved cohort, and an approved proving pair.</td>
            </tr>
            <tr>
              <td><span class="badge badge-bad">Live</span></td>
              <td>Real</td>
              <td>Yes</td>
              <td>Requires a fully reconciled proving fill for this exact exchange account.</td>
            </tr>
          </tbody>
        </table>

        <h2>Connect a Kraken account</h2>
        <form id="connect-form" class="card">
          <div class="field">
            <label for="connect-name">Name for this connection</label>
            <input id="connect-name" type="text" required placeholder="Kraken main" />
          </div>
          <div class="field">
            <label for="connect-key">API key</label>
            <input id="connect-key" type="text" autocomplete="off" spellcheck="false" required />
          </div>
          <div class="field">
            <label for="connect-secret">Private key</label>
            <input id="connect-secret" type="password" autocomplete="off" spellcheck="false" required />
          </div>
          <button type="submit">Connect</button>
        </form>

        <p id="status" class="empty" role="status" aria-live="polite"></p>
        <p class="empty">
          The key is checked against Kraken before it is stored. A key that can withdraw funds
          is refused outright.
        </p>

        <h2>Connected accounts</h2>
        <table>
          <thead>
            <tr>
              <th>Name</th><th>Exchange</th><th>Status</th><th>Stage</th>
              <th>Last validated</th><th></th>
            </tr>
          </thead>
          <tbody id="accounts"><tr><td colspan="6" class="empty">Loading.</td></tr></tbody>
        </table>
      </main>

      <script>
        var status = document.getElementById('status');
        var tbody = document.getElementById('accounts');

        function report(text) { status.textContent = text; status.className = 'empty'; show(); }
        function reportOk(text) { status.textContent = text; status.className = 'notice ok'; show(); }
        function reportError(text) { status.textContent = text; status.className = 'notice error'; show(); }

        // The result sits below the form, so on a short window the page could
        // change without anything visibly happening. Bringing it into view
        // means an answer is never missed.
        function show() {
          if (status.scrollIntoView) {
            status.scrollIntoView({ block: 'nearest' });
          }
        }

        function cell(row, text, className) {
          var td = document.createElement('td');
          td.textContent = text;
          if (className) { td.className = className; }
          row.appendChild(td);
          return td;
        }

        function stageClass(stage) {
          if (stage === 'Paper') { return 'badge badge-ok'; }
          if (stage === 'Proving') { return 'badge badge-warn'; }
          return 'badge badge-bad';
        }

        async function changeStage(account, target) {
          var question = target === 'Paper'
            ? 'Return "' + account.displayName + '" to paper trading? No new order can reach Kraken.'
            : 'Move "' + account.displayName + '" to ' + target + '?\n\n' +
              'This allows real-money limit orders through Kraken subject to all safety limits.';

          if (!window.confirm(question)) { return; }

          report('Changing trading stage.');
          var response;
          try {
            response = await fetch('/api/exchange/accounts/' + encodeURIComponent(account.id) + '/stage', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
              body: JSON.stringify({ stage: target })
            });
          } catch (error) {
            reportError('The stage could not be changed. ' + error.message);
            return;
          }

          var body = await response.json().catch(function () { return {}; });
          if (response.ok) {
            reportOk(body.message || 'Trading stage updated.');
          } else {
            reportError(body.message || 'The requested trading-stage change was refused.');
          }
          await load();
        }

        async function load() {
          var response = await fetch('/api/exchange/accounts');
          tbody.textContent = '';

          if (response.status === 401) {
            var authRow = document.createElement('tr');
            cell(authRow, 'Sign in to manage exchange accounts.', 'empty').colSpan = 6;
            tbody.appendChild(authRow);
            return;
          }

          if (!response.ok) {
            var errRow = document.createElement('tr');
            cell(errRow, 'Could not load accounts.', 'empty').colSpan = 6;
            tbody.appendChild(errRow);
            return;
          }

          var accounts = await response.json();

          if (!accounts.length) {
            var emptyRow = document.createElement('tr');
            cell(emptyRow, 'No exchange account connected yet.', 'empty').colSpan = 6;
            tbody.appendChild(emptyRow);
            return;
          }

          accounts.forEach(function (account) {
            var row = document.createElement('tr');
            cell(row, account.displayName);
            cell(row, account.exchange);
            cell(row, account.status);

            var stageCell = document.createElement('td');
            var badge = document.createElement('span');
            badge.className = stageClass(account.stage);
            badge.textContent = account.stage;
            stageCell.appendChild(badge);
            row.appendChild(stageCell);

            cell(row, account.lastValidatedAtUtc
              ? new Date(account.lastValidatedAtUtc).toLocaleString()
              : 'Never');

            var actionCell = document.createElement('td');
            var button = document.createElement('button');
            button.className = 'secondary';
            button.type = 'button';
            button.textContent = 'Disconnect';
            button.addEventListener('click', async function () {
              report('Disconnecting.');
              var result = await fetch('/api/exchange/accounts/' + account.id, { method: 'DELETE' });
              if (result.ok) {
                reportOk('Disconnected. The stored credential was deleted from the secret store.');
              } else {
                reportError('Disconnect failed. The credential is still stored.');
              }
              await load();
            });
            actionCell.appendChild(button);

            var stageButton = document.createElement('button');
            stageButton.className = 'secondary';
            stageButton.type = 'button';
            stageButton.style.marginLeft = '0.5rem';
            if (account.stage === 'Paper') {
              stageButton.textContent = 'Start proving';
              stageButton.addEventListener('click', function () { changeStage(account, 'Proving'); });
            } else if (account.stage === 'Proving') {
              stageButton.textContent = 'Enable live';
              stageButton.addEventListener('click', function () { changeStage(account, 'Live'); });
            } else {
              stageButton.textContent = 'Return to paper';
              stageButton.addEventListener('click', function () { changeStage(account, 'Paper'); });
            }
            actionCell.appendChild(stageButton);
            row.appendChild(actionCell);

            tbody.appendChild(row);
          });
        }

        document.getElementById('connect-form').addEventListener('submit', async function (event) {
          event.preventDefault();

          var submit = event.target.querySelector('button[type=submit]');
          var secretField = document.getElementById('connect-secret');
          var keyField = document.getElementById('connect-key');

          if (!keyField.value.trim() || !secretField.value) {
            reportError('Enter both the API key and the private key from Kraken.');
            return;
          }

          report('Checking the key with Kraken. This contacts the exchange, so it can take a moment.');
          submit.disabled = true;

          var response;
          try {
            response = await fetch('/api/exchange/accounts', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
              body: JSON.stringify({
                displayName: document.getElementById('connect-name').value,
                apiKey: keyField.value.trim(),
                apiSecret: secretField.value
              })
            });
          } catch (error) {
            secretField.value = '';
            submit.disabled = false;
            reportError('The key could not be checked because the request failed: ' + error.message);
            return;
          } finally {
            // The secret is cleared from the form as soon as the request has
            // been made, so it does not sit in the page afterwards.
            secretField.value = '';
            submit.disabled = false;
          }

          if (response.status === 401) {
            reportError('Your session has ended. Sign in again before connecting a key.');
            window.setTimeout(function () {
              window.location.assign('/login?returnUrl=%2Fexchange');
            }, 1500);
            return;
          }

          var body = await response.json().catch(function () { return {}; });

          if (response.ok) {
            reportOk(
              'Key accepted. Kraken confirmed it can read account data and place orders, and that it ' +
              'cannot withdraw funds. It is stored encrypted and stays connected between sign-ins, so ' +
              'you do not enter it again. The account starts in the ' + (body.stage || 'Paper') +
              ' stage, where no order reaches Kraken.');
            document.getElementById('connect-form').reset();
          } else if (body.outcome === 'WithdrawalPermissionPresent') {
            reportError(
              'Rejected: this key can withdraw funds, so it was refused and nothing was stored. ' +
              'Create a new Kraken key with Query Funds and Create & Modify Orders only, and with ' +
              'every withdrawal permission left off.');
          } else if (body.outcome === 'MissingReadPermission') {
            reportError('Rejected: this key cannot read account data. Enable Query Funds on the Kraken key.');
          } else if (body.outcome === 'MissingTradePermission') {
            reportError('Rejected: this key cannot place orders. Enable Create & Modify Orders on the Kraken key.');
          } else if (body.outcome === 'CredentialNotUsable') {
            reportError('Rejected: ' + (body.error || 'those values are not a usable Kraken key.') +
              ' Nothing was stored.');
          } else if (body.outcome === 'ProbeFailed') {
            reportError((body.error || 'Kraken did not accept the key when it was checked.') +
              ' Nothing was stored.');
          } else {
            reportError(body.error || 'The key could not be connected and nothing was stored.');
          }

          await load();
        });

        load();
      </script>
    </body>
    </html>
    """,
    "text/html")).RequireAuthorization();

// A dedicated sign-in page. Sign-in is the only thing on it, so an
// unauthenticated visitor lands somewhere with one obvious action rather than
// on a page mixing sign-in, registration and account management.
app.MapGet("/login", (IHostEnvironment environment, IConfiguration configuration) =>
{
    // The seeded demo credentials are shown on the page only when this process
    // actually seeded them: Development environment *and* the seed flag. Both
    // conditions are the same ones the seeder itself checks, so the hint can
    // never describe an account that does not exist, and it cannot appear in a
    // deployed environment even if the flag is set by mistake.
    var demoHint = environment.IsDevelopment()
        && configuration.GetValue<bool>("Development:SeedDemoData")
            ? $"""
              <div class="notice ok demo-hint">
                <strong>Development build.</strong>
                Seeded sign-in: <code>{DevelopmentDataSeeder.AdministratorEmail}</code>
                / <code>{DevelopmentDataSeeder.DemoPassword}</code>.
                These exist only in the local development database.
              </div>
              """
            : string.Empty;

    return Results.Content(
        $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <link rel="stylesheet" href="/app.css" />
          <script defer src="/login.js"></script>
          <title>Sign in - Fremvo Trading</title>
        </head>
        <body class="signin-body">
          <main class="signin">
            <section class="signin-brand">
              <div class="brand-mark">Fremvo <span class="accent">Trading</span></div>
              <h1>Trade your own exchange account.</h1>
              <p class="lede">
                An invitation-only platform for scanning markets, testing approved strategy
                templates against history, and trading them through your own exchange keys.
              </p>
              <ul class="brand-points">
                <li><strong>Your funds stay yours.</strong> The platform never holds, transfers or
                    withdraws funds, and no withdrawal capability exists anywhere in the product.</li>
                <li><strong>Paper first.</strong> Every account starts with fake funds. Live trading
                    is off by default and has to be earned, not switched on.</li>
                <li><strong>No profit claims.</strong> Backtests and simulations describe the past.
                    They are not a prediction and not financial advice.</li>
              </ul>
            </section>

            <section class="signin-panel">
              <div class="card signin-card">
                <h2>Sign in</h2>
                <p class="signin-sub">Use the email your invitation was issued to.</p>

                <div id="signed-out-note" class="notice" hidden>You are signed out.</div>

                <form id="login-form" novalidate>
                  <div class="field">
                    <label for="login-email">Email</label>
                    <input id="login-email" type="email" inputmode="email" spellcheck="false"
                           autocomplete="username" required autofocus placeholder="you@example.com" />
                  </div>
                  <div class="field">
                    <label for="login-password">Password</label>
                    <div class="input-with-action">
                      <input id="login-password" type="password" autocomplete="current-password" required />
                      <button id="toggle-password" type="button" class="link-button"
                              aria-controls="login-password" aria-pressed="false">Show</button>
                    </div>
                    <p id="capslock-note" class="hint" hidden>Caps Lock is on.</p>
                  </div>
                  <button id="login-submit" type="submit" class="primary wide">Sign in</button>
                </form>

                <p id="status" class="empty" role="status" aria-live="polite"></p>

                <p class="signin-alt">
                  No account? Registration requires an invitation code from an administrator.
                  <a href="/account">Register with an invitation</a>.
                </p>
              </div>

              {{demoHint}}
            </section>
          </main>
        </body>
        </html>
        """,
        "text/html");
});

app.MapGet("/account", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <link rel="stylesheet" href="/app.css" />
      <script defer src="/nav.js"></script>
      <title>Account</title>
    </head>
    <body>
      <main>
        <h1>Account</h1>
        <p class="lede">
          Fremvo Trading is invitation only. Registration requires a valid invitation code issued
          by an administrator.
        </p>

        <div class="notice">
          <strong>Your keys stay yours.</strong>
          Exchange API secrets are never stored in this application's database, never written to
          logs, and never returned to the browser. The platform cannot withdraw or transfer funds,
          and no withdrawal capability exists anywhere in the product.
        </div>

        <h2>Signed in as</h2>
        <div id="identity" class="card">
          <p class="empty">Checking your session.</p>
        </div>

        <h2>Register with an invitation</h2>
        <form id="register-form" class="card">
          <div class="field">
            <label for="register-code">Invitation code</label>
            <input id="register-code" type="text" required />
          </div>
          <div class="field">
            <label for="register-email">Email</label>
            <input id="register-email" type="email" autocomplete="username" required />
          </div>
          <div class="field">
            <label for="register-name">Display name</label>
            <input id="register-name" type="text" required />
          </div>
          <div class="field">
            <label for="register-password">Password</label>
            <input id="register-password" type="password" autocomplete="new-password" required />
          </div>
          <button type="submit">Register</button>
        </form>

        <p id="status" class="empty" role="status" aria-live="polite"></p>
      </main>

      <script>
        var status = document.getElementById('status');

        function report(text) {
          status.textContent = text;
          status.className = 'empty';
        }

        function reportOk(text) {
          status.textContent = text;
          status.className = 'notice ok';
        }

        function reportError(text) {
          status.textContent = text;
          status.className = 'notice error';
        }

        async function post(url, body) {
          var response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: JSON.stringify(body)
          });

          if (response.ok) { return { ok: true }; }

          // Server messages are deliberately generic. Nothing here reveals whether an
          // account exists, and no credential is ever echoed back.
          return { ok: false, status: response.status };
        }

        // Renders the current session so the page states plainly whether you
        // are signed in. Without this the page looked identical either way.
        async function renderIdentity() {
          var host = document.getElementById('identity');
          host.textContent = '';

          var me = null;
          try {
            var response = await fetch('/api/me', { headers: { 'Accept': 'application/json' } });
            if (response.ok) { me = await response.json(); }
          } catch (error) { me = null; }

          if (!me || me.signedIn !== true) {
            var p = document.createElement('p');
            p.textContent = 'You are not signed in.';
            host.appendChild(p);

            var link = document.createElement('a');
            link.href = '/login';
            link.textContent = 'Go to the sign-in page';
            host.appendChild(link);
            return;
          }

          var name = document.createElement('p');
          name.textContent = me.displayName + ' (' + me.email + ')';
          host.appendChild(name);

          var role = document.createElement('p');
          role.className = 'empty';
          role.textContent = 'Role: ' + me.role;
          host.appendChild(role);

          var out = document.createElement('button');
          out.type = 'button';
          out.className = 'secondary';
          out.textContent = 'Sign out';
          out.addEventListener('click', async function () {
            out.disabled = true;
            await fetch('/api/logout', { method: 'POST' }).catch(function () { });
            // A full navigation, so no page keeps data loaded under the
            // session that was just ended.
            window.location.assign('/login?signedOut=1');
          });
          host.appendChild(out);
        }

        document.getElementById('register-form').addEventListener('submit', async function (event) {
          event.preventDefault();
          report('Registering.');
          var result = await post('/api/register', {
            invitationCode: document.getElementById('register-code').value,
            email: document.getElementById('register-email').value,
            displayName: document.getElementById('register-name').value,
            password: document.getElementById('register-password').value
          });
          document.getElementById('register-password').value = '';

          if (result.ok) {
            reportOk('Registered. You can now sign in.');
            document.getElementById('register-form').reset();
          } else {
            reportError('Registration failed. Check the invitation code and that the password is long enough.');
          }
        });

        renderIdentity();
      </script>
    </body>
    </html>
    """,
    "text/html"));

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// A request to connect an exchange account.
/// </summary>
/// <remarks>
/// This record carries the credential from the browser to the secret store and
/// nowhere else. It is never persisted, never logged, and never returned. The
/// secret is deliberately not exposed on any response model.
/// </remarks>
internal sealed record ConnectExchangeAccountRequest(
    string DisplayName,
    string ApiKey,
    string ApiSecret);

internal sealed record ChangeTradingStageRequest(string Stage);

/// <summary>
/// Creation request for a paper experiment worker. It deliberately carries no user id: the owner
/// is taken from the signed-in principal.
/// </summary>
internal sealed record CreateExperimentWorkerRequest(
    string Name,
    string StrategyTemplateId,
    string MarketSymbol,
    decimal StartingCash,
    int RandomSeed);

/// <summary>
/// Halt change request. Every change requires a reason, which is written to the audit trail.
/// </summary>
internal sealed record HaltCommandRequest(
    string? Scope,
    bool Engage,
    string? Symbol,
    Guid? TargetId,
    string? Reason);

/// <summary>
/// Paper order submission. It carries no user id, because the owner is taken from the signed-in
/// principal, and no trading mode, because a paper order can never be redirected to a venue.
/// </summary>
internal sealed record SubmitPaperOrderRequest(
    string Symbol,
    string Side,
    decimal Quantity,
    string? ClientOrderId);

/// <summary>
/// Live order submission. It carries no user id, because the owner is taken
/// from the signed-in principal, and it names the exchange account explicitly
/// so a user with more than one cannot have an order routed to whichever
/// account the server happened to pick.
/// </summary>
internal sealed record SubmitLiveOrderRequest(
    Guid ExchangeAccountId,
    string Symbol,
    string Side,
    decimal Quantity,
    string? ClientOrderId);

/// <summary>
/// Stop and target levels for an open position. Either may be null, which
/// removes that level. The position id comes from the route, and ownership is
/// checked against the signed-in principal.
/// </summary>
internal sealed record SetProtectiveExitsRequest(
    decimal? StopLossPrice,
    decimal? TakeProfitPrice);
