using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Application.UseCases.Identity;
using Trading.Domain.Audit;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
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

var app = builder.Build();

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
              <li>Next: market data and risk controls</li>
            </ul>
          </article>
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
