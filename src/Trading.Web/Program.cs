using Microsoft.EntityFrameworkCore;
using Trading.Application.UseCases.Audit;
using Trading.Application.UseCases.Identity;
using Trading.Domain.Audit;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Audit;
using Trading.Optimization;
using Trading.Web.Extensions;
using Trading.Web.Optimization;

var featureNames = new[]
{
    "Invitation management",
    "Registration flow",
    "Account connection readiness",
    "Market-data foundation",
    "Safety-first trading gates"
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTradingInfrastructure(builder.Configuration);
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IRegistrationService, RegistrationService>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IAdministratorMfaPolicyService, AdministratorMfaPolicyService>();
builder.Services.AddScoped<IAuditEventWriter, EfAuditEventWriter>();
builder.Services.AddScoped<IAuditQueryService>(provider =>
    new AuditQueryService(provider.GetRequiredService<TradingDbContext>().AuditEvents));

var app = builder.Build();

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

app.MapPost("/api/invitations", (IInvitationService service, InvitationRequest request) =>
{
    var invitation = service.CreateInvitation(
        Guid.NewGuid(),
        request.Email,
        1,
        DateTimeOffset.UtcNow.AddDays(30));

    return Results.Ok(new { invitation.Id, invitation.Code, invitation.ExpiresAtUtc });
});

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

app.MapPost("/api/login", (IAuthenticationService authService, TradingDbContext dbContext, LoginRequest request) =>
{
    ArgumentNullException.ThrowIfNull(request);

    var user = dbContext.Users
        .SingleOrDefault(u => u.Email == request.Email.Trim());

    if (user is null)
    {
        return Results.BadRequest(new { error = "Invalid login" });
    }

    return authService.ValidateCredentials(user, request.Password)
        ? Results.Ok(new { user.Id, user.Email })
        : Results.BadRequest(new { error = "Invalid login" });
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

app.Run();
