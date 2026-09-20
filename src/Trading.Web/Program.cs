using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Trading.Application.Execution;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Application.UseCases.Identity;
using Trading.Application.Universe;
using Trading.Domain.Audit;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Market;
using Trading.Domain.Universe;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Audit;
using Trading.Optimization;
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
    "Live trading is disabled. Orders shown here are paper orders placed with fake funds. " +
    "No result shown is a prediction, and no strategy is guaranteed to be profitable.";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTradingInfrastructure(builder.Configuration);
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IRegistrationService, RegistrationService>();
builder.Services.AddScoped<ITradingAuthenticationService, TradingAuthenticationService>();
builder.Services.AddScoped<IAdministratorMfaPolicyService, AdministratorMfaPolicyService>();
builder.Services.AddScoped<IAuditEventWriter, EfAuditEventWriter>();
builder.Services.AddScoped<IAuditQueryService>(provider =>
    new AuditQueryService(provider.GetRequiredService<TradingDbContext>().AuditEvents));

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
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

// Experiment state is paper-only and non-durable. Registering the in-memory store here keeps
// experiment data out of the trading database until a durable store is designed for it.
builder.Services.AddSingleton<IExperimentWorkerRepository, InMemoryExperimentWorkerRepository>();
builder.Services.AddSingleton<ExperimentWorkerPool>();

// Halt state is shared by every trading path in this process. It is registered as a singleton so
// an emergency stop takes effect immediately for all callers.
builder.Services.AddSingleton<InMemoryTradingHaltState>();
builder.Services.AddSingleton<ITradingHaltState>(sp => sp.GetRequiredService<InMemoryTradingHaltState>());

// Execution storage. These are the non-durable implementations, which is acceptable only
// because live trading is disabled. Enabling live trading requires swapping these for the
// Entity Framework repositories so an exchange order can never outlive its local record.
builder.Services.AddSingleton<InMemoryOrderRepository>();
builder.Services.AddSingleton<IOrderRepository>(sp => sp.GetRequiredService<InMemoryOrderRepository>());
builder.Services.AddSingleton<IPositionRepository, InMemoryPositionRepository>();
builder.Services.AddSingleton<IOrderReconciliationRepository, InMemoryOrderReconciliationRepository>();

// Instrument universe. The thresholds registered here are the mandatory
// platform floor; operator configuration is combined with them and may only
// ever be stricter.
builder.Services.AddSingleton(TimeProvider.System);
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

app.MapGet("/", () => Results.Content(
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
            risk controls, and staged rollout from Binance testnet paths before any live trading is enabled.
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
              <li>Binance-first connector architecture</li>
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

app.MapPost("/api/login", async (
    HttpContext httpContext,
    ITradingAuthenticationService authService,
    TradingDbContext dbContext,
    LoginRequest request) =>
{
    ArgumentNullException.ThrowIfNull(request);

    var user = dbContext.Users
        .SingleOrDefault(u => u.Email == request.Email.Trim());

    // The same response is returned for an unknown address and a wrong password so the endpoint
    // cannot be used to discover which addresses are registered.
    if (user is null || !authService.ValidateCredentials(user, request.Password))
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
            <input id="symbol" value="BTCUSDT" />
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
    "text/html"));

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
    "text/html"));

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
            <input id="symbol" placeholder="BTCUSDT" />
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
    "text/html"));

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
    "text/html"));

// Orders and positions for the signed-in user only. There is no user id parameter, so one
// user cannot read another user's trading activity.
app.MapGet("/api/orders", async (
    ClaimsPrincipal principal,
    IOrderRepository orders,
    IPositionRepository positions,
    CancellationToken cancellationToken) =>
{
    var userId = CurrentUser.TryGetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var ownedOrders = await orders.ListAsync(userId.Value, cancellationToken).ConfigureAwait(false);
    var ownedPositions = await positions.ListOpenAsync(userId.Value, cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        tradingMode = "Paper",
        disclaimer = OrdersDisclaimer,
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
    "text/html"));

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

        <h2>Sign in</h2>
        <form id="login-form" class="card">
          <div class="field">
            <label for="login-email">Email</label>
            <input id="login-email" type="email" autocomplete="username" required />
          </div>
          <div class="field">
            <label for="login-password">Password</label>
            <input id="login-password" type="password" autocomplete="current-password" required />
          </div>
          <button type="submit">Sign in</button>
        </form>

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

        <p id="status" class="empty"></p>

        <h2>Sign out</h2>
        <button id="logout" class="secondary" type="button">Sign out</button>
      </main>

      <script>
        var status = document.getElementById('status');

        function report(text) {
          status.textContent = text;
        }

        async function post(url, body) {
          var response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
          });

          if (response.ok) { return { ok: true }; }

          // Server messages are deliberately generic. Nothing here reveals whether an
          // account exists, and no credential is ever echoed back.
          return { ok: false, status: response.status };
        }

        document.getElementById('login-form').addEventListener('submit', async function (event) {
          event.preventDefault();
          report('Signing in.');
          var result = await post('/api/login', {
            email: document.getElementById('login-email').value,
            password: document.getElementById('login-password').value
          });
          report(result.ok ? 'Signed in.' : 'Sign in failed.');
          document.getElementById('login-password').value = '';
        });

        document.getElementById('register-form').addEventListener('submit', async function (event) {
          event.preventDefault();
          report('Registering.');
          var result = await post('/api/register', {
            invitationCode: document.getElementById('register-code').value,
            email: document.getElementById('register-email').value,
            displayName: document.getElementById('register-name').value,
            password: document.getElementById('register-password').value
          });
          report(result.ok ? 'Registered. You can now sign in.' : 'Registration failed.');
          document.getElementById('register-password').value = '';
        });

        document.getElementById('logout').addEventListener('click', async function () {
          await fetch('/api/logout', { method: 'POST' });
          report('Signed out.');
        });
      </script>
    </body>
    </html>
    """,
    "text/html"));

app.Run();

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
