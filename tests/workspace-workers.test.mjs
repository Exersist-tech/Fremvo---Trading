import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../src/Trading.Web/wwwroot/workspace.js", import.meta.url), "utf8");
const styles = await readFile(new URL("../src/Trading.Web/wwwroot/workspace.css", import.meta.url), "utf8");
const app = await readFile(new URL("../src/Trading.Web/Components/App.razor", import.meta.url), "utf8");
const { workerDetailMarkup, workersMarkup, scanStatus, closedChannelLevels,
  workerStrategyVisualMarkup, clampAxisCenter, verticalScaleFactor, panAxisCenter,
  invalidStrategySettings, parseSavedStrategySettings, savedWorkerStrategySettings,
  strategySettingEffects, strategySettingHelpMarkup,
  closedTradesMarkup, overviewMetricsMarkup, recentWorkerFillsMarkup, researchMarkup,
  paperTransactionReportMarkup, paperReportExportsMarkup, adminPaperEntitlementsMarkup,
  adminInvitationsMarkup, ownerPaperEntitlementMarkup } = await import(
  `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

test("strategy chart explanation is in a collapsed disclosure beneath the chart", () => {
  const tradeMarkup = source.slice(source.indexOf('<div id="rsi-pane"'),
    source.indexOf('<div id="strategy-evidence"'));
  assert.match(tradeMarkup, /<details class="worker-visual-guide">\s*<summary>Strategy chart guide<\/summary>\s*<div id="worker-strategy-visuals"/);
  assert.ok(tradeMarkup.indexOf('id="worker-strategy-visuals"') >
    tradeMarkup.indexOf('id="macd-pane"'));
  assert.doesNotMatch(tradeMarkup, /<details class="worker-visual-guide" open/);
});

test("selected worker evidence is folded by default when choosing a worker", () => {
  assert.match(source, /<details id="worker-detail" class="workspace-panel-body worker-detail">/);
  const selection = source.slice(source.indexOf('root.querySelector("#worker-list").addEventListener("click"'),
    source.indexOf('if (worker.symbol)', source.indexOf('root.querySelector("#worker-list").addEventListener("click"')));
  assert.match(selection, /selectedWorkerSlot = worker.slot;\s*root.querySelector\("#worker-detail"\)\.open = false;\s*updateWorkerView\(\)/);
});

test("paper plan console is owner-targeted, explicit, and escapes account labels", () => {
  const plans = [{ id: "plan-1", code: "PAPER-ONE", maxExperimentWorkers: 1 }];
  const empty = adminPaperEntitlementsMarkup(plans);
  assert.match(empty, /admin-plan-assign"[^>]*>/);
  assert.match(empty, /type="submit" disabled>Assign plan/);
  assert.match(empty, /Plans here never enable live or futures trading/);
  const found = adminPaperEntitlementsMarkup(plans, {
    id: "owner-1", email: "<unsafe@example.test>", role: "User",
    status: "Active", assignment: null
  });
  assert.match(found, /&lt;unsafe@example.test&gt;/);
  assert.doesNotMatch(found, /<unsafe@example.test>/);
  assert.match(found, /type="submit" >Assign plan/);
  assert.match(found, /Suspend owner/);
  assert.match(found, /Grant Risk Officer access/);
  assert.match(found, /Approve Administrator access/);
  const suspended = adminPaperEntitlementsMarkup(plans, {
    id: "owner-1", email: "owner@example.test", role: "User",
    status: "Suspended", assignment: null
  });
  assert.match(suspended, /Reactivate owner/);
  assert.doesNotMatch(suspended, /id="admin-owner-role"/);
  assert.doesNotMatch(suspended, /id="admin-owner-administrator"/);
  assert.match(suspended, /type="submit" disabled>Assign plan/);
  const assigned = adminPaperEntitlementsMarkup(plans, {
    id: "owner-1", email: "owner@example.test", status: "Active",
    assignment: { id: "assignment-1", code: "PAPER-ONE", maxExperimentWorkers: 1 }
  });
  assert.match(assigned, /admin-plan-revoke/);
  assert.doesNotMatch(assigned, /admin-trial-extend/);
  assert.match(assigned, /type="submit" disabled>Assign plan/);
  const trial = adminPaperEntitlementsMarkup([], {
    id: "owner-1", email: "owner@example.test", status: "Active",
    assignment: { id: "assignment-1", code: "PAPER-ONE", maxExperimentWorkers: 1,
      trialExpiresAtUtc: "2099-01-01T00:00:00Z" }
  });
  assert.match(trial, /id="admin-trial-extend"/);
  assert.match(adminPaperEntitlementsMarkup([], null, {
    query: "ow", page: 0, hasMore: true,
    items: [{ id: "owner-1", email: 'unsafe"@example.test', role: "User", status: "Active" }]
  }), /unsafe&quot;@example.test/);
  assert.doesNotMatch(adminPaperEntitlementsMarkup([], null, {
    query: "ow", page: 0, hasMore: true,
    items: [{ id: "owner-1", email: 'unsafe"@example.test', role: "User", status: "Active" }]
  }), /data-admin-owner-email="unsafe"@example.test"/);
  assert.doesNotMatch(adminPaperEntitlementsMarkup([], {
    id: "owner-1", email: "owner@example.test", status: "Suspended",
    assignment: { id: "assignment-1", code: "PAPER-ONE", maxExperimentWorkers: 1,
      trialExpiresAtUtc: "2099-01-01T00:00:00Z" }
  }), /id="admin-trial-extend"/);
  const officer = adminPaperEntitlementsMarkup(plans, {
    id: "officer-1", email: "risk@example.test", role: "RiskOfficer",
    status: "Active", assignment: null
  });
  assert.match(officer, /Remove Risk Officer access/);
  assert.match(officer, /type="submit" disabled>Assign plan/);
  const administrator = {
    id: "admin-1", email: "admin@example.test", role: "Administrator",
    status: "Active", assignment: null
  };
  assert.doesNotMatch(adminPaperEntitlementsMarkup(plans, administrator, null, "admin-1"),
    /id="admin-owner-administrator-revoke"/);
  assert.match(adminPaperEntitlementsMarkup(plans, administrator, null, "admin-2"),
    /Remove Administrator access/);
});

test("admin invitation list never includes the issued bearer code", () => {
  const html = adminInvitationsMarkup([{
    id: "invite-id", expiresAtUtc: "2099-01-01T00:00:00Z", isActive: true,
    usedCount: 0, maxUses: 1, recipientEmail: "<unsafe>@example.test"
  }]);
  assert.match(html, /data-invite-revoke="invite-id"/);
  assert.match(html, /&lt;unsafe&gt;@example.test/);
  assert.doesNotMatch(html, /<unsafe>/);
  assert.match(html, /Only the issuance response displays it/);
  assert.match(html, /Fresh verification code/);
  assert.match(adminInvitationsMarkup([], 1, false), /data-invite-page="0"/);
  assert.match(adminInvitationsMarkup([], 1, false), /data-invite-page="2" disabled/);
  assert.doesNotMatch(html, /invitationCode=|localStorage/);
  assert.match(adminInvitationsMarkup([{
    id: "legacy-id", expiresAtUtc: "2099-01-01T00:00:00Z", isActive: true,
    usedCount: 0, maxUses: 1, recipientEmail: null
  }]), /Unbound \(reissue\)/);
});

test("owner plan status distinguishes opt-in capacity from live eligibility", () => {
  const inactive = ownerPaperEntitlementMarkup({
    planCode: null, paperWorkerLimitsEnabled: false, effectivePaperWorkerCapacity: 10,
    trialExpiresAtUtc: null, trialExpired: false
  });
  assert.match(inactive, /Plan limits not yet enabled/);
  assert.match(inactive, /legacy 10-worker paper ceiling/);
  const expired = ownerPaperEntitlementMarkup({
    planCode: "<unsafe>", paperWorkerLimitsEnabled: true,
    effectivePaperWorkerCapacity: 0, trialExpiresAtUtc: "2026-10-03T12:00:00Z",
    trialExpired: true
  });
  assert.match(expired, /&lt;unsafe&gt;/);
  assert.match(expired, /0 new paper workers allowed/);
  assert.match(expired, /Trial expired/);
  assert.match(expired, /does not enable real orders or futures/);
});

const worker = {
  slot: 3,
  workerId: "worker-3",
  strategyId: "platform.three-swing-channel-divergence",
  symbol: "SOL/EUR",
  interval: "FiveMinutes",
  analysisIntervals: ["FourHours", "FiveMinutes", "OneHour"],
  runtimeStatus: "Running",
  startingCash: 1000,
  positionQuantity: "0.2",
  averageEntryPrice: "120.05",
  openBuyFillPrice: "120",
  lastBuyFillPrice: "120",
  tradeCount: 1,
  recentTrades: [{ occurredAtUtc: "2026-10-03T07:00:00Z", direction: "Buy",
    quantity: "0.2", executionPrice: "120", fee: "0.01" }]
};

test("worker rows expose accessible selected slots and reserved markets", () => {
  const html = workersMarkup([worker], 3);
  assert.match(html, /data-worker-slot="3" aria-pressed="true"/);
  assert.match(html, /SOL\/EUR/);
  assert.match(html, /class="worker-row worker-active selected"/);
});

test("running workers have a visible active state distinct from selection", () => {
  const active = workersMarkup([worker]);
  assert.match(active, /class="worker-row worker-active"/);
  assert.match(active, /class="pill worker-running">Running</);
  const selected = workersMarkup([worker], worker.slot);
  assert.match(selected, /class="worker-row worker-active selected"/);
  assert.doesNotMatch(workersMarkup([{ ...worker, runtimeStatus: "Paused" }]),
    /worker-active|worker-running/);
});

test("worker rows prioritize visible signed profit/loss without repeated scan timestamps", () => {
  const html = workersMarkup([
    { ...worker, lastSellFillPrice: "128", realizedProfitAndLoss: "12.5",
      unrealizedProfitAndLoss: "-2.25" }
  ]);
  assert.doesNotMatch(html, /Last scanned|Cost basis|buy fees|FiveMinutes/);
  assert.match(html, /three swing channel divergence/);
  assert.match(html, /Open P&amp;L <span class="pnl-negative">-2\.25/);
  assert.match(html, /Realized P&amp;L <span class="pnl-positive">\+12\.5/);
  assert.match(html, /class="pnl-positive">\+12\.5/);
  assert.match(html, /class="pnl-negative">-2\.25/);
  assert.match(workersMarkup([{ ...worker, positionQuantity: 1,
    unrealizedProfitAndLoss: "2.25" }]), /Open P&amp;L <span class="pnl-positive">\+2\.25/);
  assert.match(workersMarkup([{ ...worker, positionQuantity: 1,
    unrealizedProfitAndLoss: null }]), /Open P&amp;L <span class="pnl-neutral">—<\/span><\/small>\s*<small class="worker-unpriced">Price unavailable/);
  assert.doesNotMatch(workersMarkup([{ ...worker, workerId: null, symbol: null,
    positionQuantity: 0, lastBuyFillPrice: null, openBuyFillPrice: null,
    lastSellFillPrice: null }]), /P&amp;L|Last buy|Price unavailable/);
  assert.match(styles, /grid-auto-rows: max-content/);
  assert.match(source, /worker-scan-warning/);
  assert.match(app, /workspace\.css\?v=.*LastModified\.UtcTicks/);
});

test("Trade and Overview worker cards show executed buy prices without fee-inclusive cost basis", () => {
  const html = workersMarkup([{ ...worker, additionCount: 1, openBuyFillPrice: "120.5",
    lastBuyFillPrice: "125", lastSellFillPrice: "128" }]);
  assert.match(html, /Buy @ <strong>120\.5<\/strong>/);
  assert.doesNotMatch(html, /Last buy|Last sell|cost basis|fee/i);
  const flat = workersMarkup([{ ...worker, positionQuantity: 0, openBuyFillPrice: null,
    lastBuyFillPrice: "125", lastSellFillPrice: "128", realizedProfitAndLoss: "-5" }]);
  assert.match(flat, /Last buy @ <strong>125<\/strong>/);
  assert.match(flat, /Last sell @ <strong>128<\/strong>/);
  assert.match(flat, /Realized P&amp;L <span class="pnl-negative">-5/);
  assert.doesNotMatch(flat, /Open P&amp;L/);
  const noFill = workersMarkup([{ ...worker, openBuyFillPrice: null,
    lastBuyFillPrice: null, lastSellFillPrice: null, tradeCount: 0, positionQuantity: 0 }]);
  assert.doesNotMatch(noFill, /Buy @|Last sell @|P&amp;L/);
  assert.match(workerDetailMarkup(worker, null, []), /Executed prices \(not strategy targets\)/);
  assert.doesNotMatch(workerDetailMarkup(worker, null, []), /cost basis incl\. buy fees/);
});

test("History displays recorded BUY and SELL fills for currently assigned workers", () => {
  const markup = recentWorkerFillsMarkup([{ ...worker, recentTrades: [
    { occurredAtUtc: "2026-10-03T07:01:00Z", direction: "sell",
      quantity: "0.1", executionPrice: "130", fee: "0.02", symbol: "SOL/EUR" },
    { occurredAtUtc: "2026-10-03T07:00:00Z", direction: "buy",
      quantity: "0.2", executionPrice: "120", fee: "0.01", symbol: "<unsafe>" }
  ] }]);
  assert.match(markup, /Recent view, not the complete historical ledger/i);
  assert.match(markup, /Execution price/);
  assert.match(markup, /<td>sell<\/td><td>0\.1<\/td>\s*<td>130<\/td>/);
  assert.match(markup, /<td>buy<\/td><td>0\.2<\/td>\s*<td>120<\/td>/);
  assert.match(markup, /&lt;unsafe&gt;/);
  assert.doesNotMatch(markup, /<unsafe>/);
  assert.match(recentWorkerFillsMarkup([]), /No recent simulated worker fills/);
});

test("Overview and History distinguish signed gains, losses, and unavailable P&L", () => {
  const summary = {
    realizedProfitAndLoss: "12.5", unrealizedProfitAndLoss: "-2.25",
    equity: "1010.25", hasUnpricedExposure: false, asOfUtc: "2026-10-04T09:00:00Z",
    reserved: 1, scanning: 0, workerCount: 1
  };
  const overview = overviewMetricsMarkup(summary);
  assert.match(overview, /metric-value pnl-positive">\+12\.5/);
  assert.match(overview, /metric-value pnl-negative">-2\.25/);
  const unpriced = overviewMetricsMarkup({ ...summary, unrealizedProfitAndLoss: null,
    hasUnpricedExposure: true });
  assert.match(unpriced, /metric-value pnl-neutral">Unpriced/);

  const closed = closedTradesMarkup([
    { workerId: "one", strategyId: "strategy", symbol: "BTC/USD",
      netProfitAndLoss: "9.75", averageBuyFillPrice: "100", averageEntryPrice: "101",
      averageExitPrice: "110", openedAtUtc: "2026-10-04T08:00:00Z",
      closedAtUtc: summary.asOfUtc },
    { workerId: "two", strategyId: "strategy", symbol: "ETH/EUR",
      netProfitAndLoss: "-3", averageBuyFillPrice: "55", averageEntryPrice: "56",
      averageExitPrice: "54", openedAtUtc: "2026-10-04T08:00:00Z",
      closedAtUtc: summary.asOfUtc }
  ]);
  assert.match(closed, /class="pnl-positive">\+9\.75/);
  assert.match(closed, /class="pnl-negative">-3/);
  assert.match(closed, /BUY fill avg<\/th><th>SELL fill avg<\/th>/);
  assert.doesNotMatch(closed, /Cost basis incl\. BUY fees/);
  assert.match(closed, /<td>100<\/td><td>110<\/td>\s*<td class="pnl-positive">\+9\.75<\/td>/);

  const research = researchMarkup([{ strategyId: "strategy", group: "A",
    equity: "100", realizedProfitAndLoss: "1", unrealizedProfitAndLoss: null,
    maximumDrawdown: "0", evaluatedAtUtc: summary.asOfUtc }]);
  assert.match(research, /class="pnl-positive">\+1/);
  assert.match(research, /class="pnl-neutral">—/);
});

test("worker panel distinguishes forward feed evidence from a host heartbeat", () => {
  assert.match(source, /id="forward-feed-state"/);
  assert.match(source, /training\?\.forwardFeedObservedRecently === true/);
  assert.match(source, /"Feed observed" : "Feed unverified"/);
  assert.match(source, /does not prove continuity for every pair/);
});

test("workspace reporting settings keep money in its original currency", () => {
  assert.match(source, /id="reporting-profile"/);
  assert.match(source, /api\("\/api\/reporting\/profile"/);
  assert.match(source, /reportingLocale = profile\.locale/);
  assert.match(source, /timeZone: reportingTimeZone/);
  assert.match(source, /function formatChartTime\(value, interval\)[\s\S]*?Intl\.DateTimeFormat\(reportingLocale/);
  assert.doesNotMatch(source, /toLocaleTimeString\(\)/);
  assert.match(source, /it does not convert, merge, or revalue Spot or paper balances/);
  assert.match(source, /Reporting preferences unavailable:/);
});

test("History paper report displays exact native amounts without merging mixed quotes", () => {
  assert.match(source, /id="paper-report-form"/);
  assert.match(source, /api\("\/api\/reports\/paper-transactions"/);
  assert.match(source, /Generate &amp; save private JSON/);
  assert.match(source, /id="report-country"/);
  assert.match(source, /countryProfileCode/);
  assert.match(source, /export: save/);
  assert.match(source, /Saved paper reports unavailable:/);
  const markup = paperTransactionReportMarkup({
    disclaimer: "Simulated paper only; not tax advice.",
    fromUtc: "2026-10-03T00:00:00Z",
    toUtcExclusive: "2026-10-04T00:00:00Z",
    reportingCurrency: "USD",
    totalInReportingCurrency: null,
    currencyTotals: [
      { currency: "USD", realizedProfitAndLoss: "0.123456789012345678" },
      { currency: "EUR", realizedProfitAndLoss: "-1.25" }
    ],
    transactions: [{
      executedAtUtc: "2026-10-03T12:00:00Z",
      workerId: "worker-42",
      fillId: "fill-123",
      symbol: "<script>alert(1)</script>",
      direction: "sell",
      quantity: "0.000000000000000001",
      price: "123456789012345678.12345678",
      fee: "0.00000001",
      cashChange: "123.45",
      quoteCurrency: "USD",
      realizedProfitAndLoss: "0.123456789012345678"
    }]
  });
  assert.match(markup, /No reporting-currency total: native quote currencies differ/);
  assert.match(markup, /UTC interval: 2026-10-03T00:00:00Z to 2026-10-04T00:00:00Z/);
  assert.match(markup, /0\.000000000000000001/);
  assert.match(markup, /worker-42.*Fill fill-123/);
  assert.match(markup, /123456789012345678\.12345678/);
  assert.match(markup, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
  assert.doesNotMatch(markup, /<script>/);
  assert.match(markup, /Simulated paper only; not tax advice/);
  assert.match(markup, /class="pnl-positive">\+0\.123456789012345678/);
  assert.match(markup, /class="pnl-negative">-1\.25/);
  const localized = paperTransactionReportMarkup({
    ...{
      disclaimer: "Simulated paper only", fromUtc: "2026-03-29T00:00:00Z",
      toUtcExclusive: "2026-03-29T03:00:00Z", reportingCurrency: "GBP",
      totalInReportingCurrency: null, currencyTotals: [], transactions: []
    },
    countryProfile: {
      title: "United Kingdom date-notation example", timeZone: "Europe/London",
      disclaimer: "Not tax advice", transactions: [{
        executedAtLocal: "29/03/2026 02:30:00 +01:00", symbol: "BTC/GBP",
        direction: "sell", quantity: "1", price: "120", fee: "1",
        cashChange: "119", realizedProfitAndLoss: "19", quoteCurrency: "GBP"
      }]
    }
  });
  assert.match(localized, /29\/03\/2026 02:30:00 \+01:00/);
  assert.match(localized, /Not tax advice/);
  assert.match(localized, /class="pnl-positive">\+19/);
  const exports = paperReportExportsMarkup([{
    id: "report-123", fromUtc: "2026-10-03T00:00:00Z",
    toUtcExclusive: "2026-10-04T00:00:00Z",
    reportingCurrency: "USD",
    occurredAtUtc: "2026-10-04T00:05:00Z"
  }]);
  assert.match(exports, /href="\/api\/reports\/paper-transactions\/exports\/report-123" download/);
  assert.match(exports, /Download 2026-10-03T00:00:00Z to 2026-10-04T00:00:00Z UTC/);
});

test("selected worker chart declares only strategy-relevant visual evidence", () => {
  const donchian = workerStrategyVisualMarkup({
    ...worker, strategyId: "platform.donchian-breakout-ensemble",
    strategyParameters: JSON.stringify({ shortChannel: 10, mediumChannel: 30, longChannel: 80 })
  });
  assert.match(donchian, /Configured Donchian periods: 10 \/ 30 \/ 80/);
  assert.match(donchian, /not fills or guarantees/);
  const crossSectional = workerStrategyVisualMarkup({
    ...worker, strategyId: "platform.cross-sectional-momentum-rotation"
  });
  assert.match(crossSectional, /No single-chart overlay represents the full pinned eligible-universe ranking/);
});

test("worker strategy configuration saves approved rule settings with assignments", () => {
  assert.match(source, /async function renderStrategies\(root\)/);
  assert.match(source, /api\("\/api\/paper-training"\)/);
  assert.match(source, /Strategy rule editor/);
  assert.match(source, /strategy-editor-fields/);
  assert.match(source, /strategyParametersJson: JSON\.stringify/);
  assert.match(source, /training\.strategyParameters/);
  assert.match(source, /strategyParameters: Object\.fromEntries/);
  assert.match(source, /Updated assignments\/settings apply when a slot next becomes available/);
  assert.match(source, /Only bounded settings for approved strategy templates are editable/);
  assert.match(source, /id="start-scanner"/);
  assert.match(source, /id="stop-scanner"/);
  assert.match(source, /item\.selectedComponent\.componentDecisionFingerprint/);
  assert.match(source, /Confirmed 1h close/);
  assert.match(source, /item\.admissionCloseUtc/);
  assert.match(source, /New paper admissions use rule v/);
  assert.match(source, /Save while scanning or while paper trades are open/);
  assert.match(source, /Current worker:.*admitted rule v/);
  assert.match(source, /item\.currentStrategyId/);
  assert.match(source, /item\.currentStrategyId === "platform\.relative-strength-pullback-rotation" && item\.currentStrategyVersion >= 5/);
  assert.doesNotMatch(source, /const locked = training\.state === "Active"/);
  assert.doesNotMatch(source, /assignments\.some\(item => item\.state === "Reserved"\)/);
  assert.doesNotMatch(source, /id="scan-state"|class="automation-note"/);
});

test("every approved worker setting explains how changing it affects the rule", async () => {
  const catalog = await readFile(new URL("../src/Trading.Application/Experiments/ApprovedStrategyParameters.cs",
    import.meta.url), "utf8");
  const keys = [...catalog.matchAll(/^\s*[INSB]\("([^"]+)"/gm)].map(match => match[1]);
  assert.ok(keys.length > 200);
  assert.deepEqual([...new Set(keys)].filter(key => !strategySettingEffects[key]), []);
  assert.match(source, /strategySettingHelpMarkup\(field, strategy\)/);
  assert.match(source, /strategyOptionLabels\[option\] \?\? option/);
  assert.match(source, /Periods count closed candles/);
});

test("worker setting help shows direction, fixed bounds and safe, versioned context", () => {
  const strategy = { strategyId: "platform.relative-strength-pullback-rotation", strategyVersion: 5 };
  const numeric = strategySettingHelpMarkup({
    key: "maximumHoldingCandles", description: "Time exit on signal interval.", minimum: 1, maximum: 240
  }, strategy);
  assert.match(numeric, /Raise: the time-based paper exit waits longer/);
  assert.match(numeric, /confirmed 1-hour candles/);
  const fixed = strategySettingHelpMarkup({
    key: "minimumAgreement", description: "All five checks are required.", minimum: 5, maximum: 5
  }, strategy);
  assert.match(fixed, /Fixed approved value/);
  assert.doesNotMatch(fixed, /Raise:/);
  const choice = strategySettingHelpMarkup({
    key: "emaPlanModel", description: "Paper stop model.", type: "select"
  }, strategy);
  assert.match(choice, /legacy ATR option/);
  assert.doesNotMatch(choice, /Fixed approved value/);
  const toggle = strategySettingHelpMarkup({
    key: "requireMacdConfirmation", description: "MACD check.", type: "boolean"
  }, strategy);
  assert.match(toggle, /On: require aligned MACD/);
  assert.doesNotMatch(toggle, /Fixed approved value/);
  const escaped = strategySettingHelpMarkup({
    key: "volumePeriod", description: "<script>alert(1)</script>", minimum: 5, maximum: 100
  }, strategy);
  assert.match(escaped, /&lt;script&gt;/);
  assert.doesNotMatch(escaped, /<script>/);
});

test("saved strategy settings outside revised bounds must be reviewed, never silently overwritten", () => {
  const catalog = [{ strategyId: "platform.ema-trend-continuation", name: "EMA", settings: [
    { key: "minimumAgreement", label: "Minimum confirmations", type: "integer",
      minimum: 4, maximum: 5, step: 1 },
    { key: "requireVolume", label: "Require volume", type: "boolean" }
  ] }];
  assert.deepEqual(invalidStrategySettings(catalog, {
    "platform.ema-trend-continuation": { minimumAgreement: 3, requireVolume: true }
  }), ["EMA: Minimum confirmations needs review"]);
  assert.deepEqual(invalidStrategySettings(catalog, {
    "platform.ema-trend-continuation": { minimumAgreement: 4, requireVolume: true }
  }), []);
  assert.deepEqual(parseSavedStrategySettings(catalog, "platform.ema-trend-continuation",
    '{"minimumAgreement":3}'), { minimumAgreement: 3, emaPlanModel: "legacyAtrPlan" });
  assert.throws(() => parseSavedStrategySettings(catalog, "platform.ema-trend-continuation",
    '{"unsupportedKey":1}'), /cannot be mapped/);
  assert.throws(() => parseSavedStrategySettings(catalog, "platform.ema-trend-continuation",
    '{"minimumAgreement":'), SyntaxError);
  assert.match(source, /cannot be read or disagree across saved assignments\. No configuration will be overwritten/);
  assert.match(source, /unreadableSettings\.length \? "disabled"/);
  assert.match(source, /Update these fields before saving/);
});

test("conflicting or unreadable assignment settings never silently override the saved family map", () => {
  const catalog = [{ strategyId: "approved.family", defaultsJson: '{"period":20,"enabled":true}',
    settings: [{ key: "period" }, { key: "enabled" }] }];
  const consistent = savedWorkerStrategySettings(catalog,
    { "approved.family": '{"enabled":true,"period":30}' },
    [{ strategyId: "approved.family", strategyParameters: '{"period":30,"enabled":true}' }]);
  assert.deepEqual(consistent.unreadableSettings, []);
  assert.equal(consistent.settingsByStrategy["approved.family"].period, 30);
  const conflict = savedWorkerStrategySettings(catalog,
    { "approved.family": '{"period":30}' },
    [{ strategyId: "approved.family", strategyParameters: '{"period":25}' }]);
  assert.deepEqual(conflict.unreadableSettings, ["approved.family"]);
  assert.equal(conflict.settingsByStrategy["approved.family"].period, 30);
  assert.deepEqual(savedWorkerStrategySettings(catalog,
    { "approved.family": '{"period":30}' },
    [{ strategyId: "approved.family", strategyParameters: "{}" }]).unreadableSettings,
  ["approved.family"]);
  const corrupted = savedWorkerStrategySettings(catalog,
    { "approved.family": '{"period":' },
    [{ strategyId: "approved.family", strategyParameters: '{"period":25}' }]);
  assert.deepEqual(corrupted.unreadableSettings, ["approved.family"]);
  assert.match(source, /if \(unreadableSettings\.length\) \{\s*setMessage\(message, settingsNotice, "error"\);\s*return;/);
});

test("legacy relative ranking settings are visible for owner review", () => {
  const catalog = [{ strategyId: "platform.relative-strength-pullback-rotation", settings: [
    { key: "relativeRankingModel", type: "select",
      options: ["dailyExcessBreadth", "legacyRanks"] },
    { key: "relativePlanModel", type: "select",
      options: ["fourHourStructure", "legacyAtrPlan"] },
    { key: "stopSwingLookback", type: "integer", minimum: 3, maximum: 20, step: 1 },
    { key: "topRankPercent", type: "number", minimum: 5, maximum: 50, step: 1 }
  ] }];
  const saved = parseSavedStrategySettings(catalog,
    "platform.relative-strength-pullback-rotation", '{"topRankPercent":25}');
  assert.equal(saved.relativeRankingModel, "legacyRanks");
  assert.equal(saved.relativePlanModel, "legacyAtrPlan");
  assert.equal(parseSavedStrategySettings(catalog,
    "platform.relative-strength-pullback-rotation",
    '{"relativeRankingModel":"dailyExcessBreadth"}').relativePlanModel, "legacyAtrPlan");
  assert.deepEqual(invalidStrategySettings(catalog, {
    "platform.relative-strength-pullback-rotation": { ...saved, stopSwingLookback: 5 }
  }), []);
  assert.match(source, /Review that strategy and select Daily excess breadth/);
  assert.match(source, /Review that strategy and select 4-hour structure/);
});

test("legacy Donchian paper plans require owner review before new worker admissions", () => {
  const catalog = [{ strategyId: "platform.donchian-breakout-ensemble",
    defaultsJson: '{"donchianPlanModel":"priorBreakRange","shortChannel":20}',
    settings: [{ key: "donchianPlanModel" }, { key: "shortChannel" }] }];
  const legacy = parseSavedStrategySettings(catalog, "platform.donchian-breakout-ensemble",
    '{"shortChannel":25}');
  assert.equal(legacy.donchianPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.donchian-breakout-ensemble": '{"shortChannel":25}' },
    [{ strategyId: "platform.donchian-breakout-ensemble",
      strategyParameters: '{"shortChannel":25}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.donchian-breakout-ensemble"]
    .donchianPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select prior break range/);
});

test("legacy Bollinger paper plans require explicit owner review before new worker admissions", () => {
  const catalog = [{ strategyId: "platform.bollinger-mean-reversion",
    defaultsJson: '{"bollingerPlanModel":"excursionMidBand","bollingerPeriod":20}',
    settings: [{ key: "bollingerPlanModel" }, { key: "bollingerPeriod" }] }];
  const legacy = parseSavedStrategySettings(catalog, "platform.bollinger-mean-reversion",
    '{"bollingerPeriod":25}');
  assert.equal(legacy.bollingerPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.bollinger-mean-reversion": '{"bollingerPeriod":25}' },
    [{ strategyId: "platform.bollinger-mean-reversion",
      strategyParameters: '{"bollingerPeriod":25}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.bollinger-mean-reversion"]
    .bollingerPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select excursion mid-band/);
});

test("legacy RSI pullback settings require explicit structural-plan review", () => {
  const catalog = [{ strategyId: "platform.rsi-pullback",
    defaultsJson: '{"rsiPlanModel":"pullbackSwing","stopSwingLookback":5}',
    settings: [{ key: "rsiPlanModel" }, { key: "stopSwingLookback" }] }];
  const legacy = parseSavedStrategySettings(catalog,
    "platform.rsi-pullback", '{"stopSwingLookback":7}');
  assert.equal(legacy.rsiPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.rsi-pullback": '{"stopSwingLookback":7}' },
    [{ strategyId: "platform.rsi-pullback",
      strategyParameters: '{"stopSwingLookback":7}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.rsi-pullback"].rsiPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select pullback swing/);
});

test("legacy MACD settings require explicit structural-plan review", () => {
  const catalog = [{ strategyId: "platform.macd-volume",
    defaultsJson: '{"macdPlanModel":"crossSwing","stopSwingLookback":5}',
    settings: [{ key: "macdPlanModel" }, { key: "stopSwingLookback" }] }];
  const legacy = parseSavedStrategySettings(catalog,
    "platform.macd-volume", '{"stopSwingLookback":7}');
  assert.equal(legacy.macdPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.macd-volume": '{"stopSwingLookback":7}' },
    [{ strategyId: "platform.macd-volume",
      strategyParameters: '{"stopSwingLookback":7}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.macd-volume"].macdPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select pre-cross swing/);
});

test("legacy EMA continuation settings require explicit structural-plan review", () => {
  const catalog = [{ strategyId: "platform.ema-trend-continuation",
    defaultsJson: '{"emaPlanModel":"pullbackSwing","stopSwingLookback":5}',
    settings: [{ key: "emaPlanModel" }, { key: "stopSwingLookback" }] }];
  const legacy = parseSavedStrategySettings(catalog,
    "platform.ema-trend-continuation", '{"stopSwingLookback":7}');
  assert.equal(legacy.emaPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.ema-trend-continuation": '{"stopSwingLookback":7}' },
    [{ strategyId: "platform.ema-trend-continuation",
      strategyParameters: '{"stopSwingLookback":7}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.ema-trend-continuation"].emaPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select pullback swing to allow new paper admissions/);
});

test("legacy compression settings require explicit prior-range review", () => {
  const catalog = [{ strategyId: "platform.volatility-compression-breakout",
    defaultsJson: '{"compressionPlanModel":"priorRange","channelPeriod":20}',
    settings: [{ key: "compressionPlanModel" }, { key: "channelPeriod" }] }];
  const legacy = parseSavedStrategySettings(catalog,
    "platform.volatility-compression-breakout", '{"channelPeriod":15}');
  assert.equal(legacy.compressionPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.volatility-compression-breakout": '{"channelPeriod":15}' },
    [{ strategyId: "platform.volatility-compression-breakout",
      strategyParameters: '{"channelPeriod":15}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.volatility-compression-breakout"]
    .compressionPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select prior range to allow new paper admissions/);
});

test("legacy momentum settings require explicit daily-swing review", () => {
  const catalog = [{ strategyId: "platform.cross-sectional-momentum-rotation",
    defaultsJson: '{"momentumPlanModel":"dailySwing","stopSwingLookback":10}',
    settings: [{ key: "momentumPlanModel" }, { key: "stopSwingLookback" }] }];
  const legacy = parseSavedStrategySettings(catalog,
    "platform.cross-sectional-momentum-rotation", '{"stopSwingLookback":15}');
  assert.equal(legacy.momentumPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.cross-sectional-momentum-rotation": '{"stopSwingLookback":15}' },
    [{ strategyId: "platform.cross-sectional-momentum-rotation",
      strategyParameters: '{"stopSwingLookback":15}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.cross-sectional-momentum-rotation"]
    .momentumPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select daily swing to allow new paper admissions/);
  assert.match(source, /No single-chart overlay represents the full pinned eligible-universe ranking/);
});

test("legacy three-swing settings require confirmed-pivot review", () => {
  const catalog = [{ strategyId: "platform.three-swing-channel-divergence",
    defaultsJson: '{"threeSwingPlanModel":"confirmedPivot","atrPeriod":14}',
    settings: [{ key: "threeSwingPlanModel" }, { key: "atrPeriod" }] }];
  const legacy = parseSavedStrategySettings(catalog,
    "platform.three-swing-channel-divergence", '{"atrPeriod":20}');
  assert.equal(legacy.threeSwingPlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.three-swing-channel-divergence": '{"atrPeriod":20}' },
    [{ strategyId: "platform.three-swing-channel-divergence",
      strategyParameters: '{"atrPeriod":20}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.three-swing-channel-divergence"]
    .threeSwingPlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select confirmed pivot to allow new paper admissions/);
  assert.match(source, /New v4 paper plans freeze a stop below the confirmed third swing/);
});

test("legacy ensemble settings require independent four-hour swing review", () => {
  const catalog = [{ strategyId: "platform.regime-switching-ensemble",
    defaultsJson: '{"regimePlanModel":"fourHourSwing","planAtrPeriod":14}',
    settings: [{ key: "regimePlanModel" }, { key: "planAtrPeriod" }] }];
  const legacy = parseSavedStrategySettings(catalog,
    "platform.regime-switching-ensemble", '{"planAtrPeriod":20}');
  assert.equal(legacy.regimePlanModel, "legacyAtrPlan");
  const loaded = savedWorkerStrategySettings(catalog,
    { "platform.regime-switching-ensemble": '{"planAtrPeriod":20}' },
    [{ strategyId: "platform.regime-switching-ensemble",
      strategyParameters: '{"planAtrPeriod":20}' }]);
  assert.deepEqual(loaded.unreadableSettings, []);
  assert.equal(loaded.settingsByStrategy["platform.regime-switching-ensemble"]
    .regimePlanModel, "legacyAtrPlan");
  assert.match(source, /Review that strategy and select four-hour swing to allow new paper admissions/);
  assert.match(source, /not the selected component's own stop/);
});

test("chart axis pan and zoom retain readable data and use consistent drag direction", () => {
  assert.equal(clampAxisCenter(0, 100, 20, 500), 90);
  assert.equal(clampAxisCenter(0, 100, 20, -500), 10);
  assert.equal(clampAxisCenter(0, 100, 120, 50), 50);
  assert.ok(verticalScaleFactor(-55) < 1, "dragging up zooms indicator ranges in");
  assert.ok(verticalScaleFactor(55) > 1, "dragging down zooms indicator ranges out");
  assert.ok(verticalScaleFactor(-55, true) > 1, "dragging up zooms price in");
  assert.ok(panAxisCenter(50, 20, 100, 100) > 50,
    "dragging price/RSI down moves the plotted values down");
  assert.ok(panAxisCenter(0, 20, 100, 100) > 0,
    "dragging MACD down moves the plotted values down");
  assert.equal(clampAxisCenter(0, 100, 20, 90), 90);
});

test("unprotected, stale-protection, and unresolved workers display prominent safety status", () => {
  for (const runtimeStatus of ["Unprotected", "ProtectionDataStale", "RequiresReconciliation"]) {
    const html = workersMarkup([{ ...worker, runtimeStatus }]);
    assert.match(html, new RegExp(`class="pill live-disabled">${runtimeStatus}<`));
  }
  const frozen = workerDetailMarkup({ ...worker, runtimeStatus: "RequiresReconciliation" },
    { state: "Active", qualifications: [] }, []);
  assert.match(frozen, /Frozen worker ID:<\/strong> worker-3/);
  assert.match(frozen, /do not retry or release it without a verified outcome/);
  const withClaim = workerDetailMarkup({ ...worker, runtimeStatus: "RequiresReconciliation",
    unresolvedExecution: { status: "Unknown", strategyId: "experiment-protective-exit-claim",
      symbol: "protective-exit", decisionAsOfUtc: "2026-10-03T07:00:00Z",
      correlationId: "paper-protective-exit-123", executionCommandId: null,
      commandEvidenceChecked: true, recordedCommandIds: [], matchingWorkerLedgerIds: [],
      portfolioEvidenceChecked: true, recordedPortfolioCommandIds: [],
      auditEvidenceChecked: true, recordedAuditActions: ["Trade.ExecutionUnknown"],
      executionEvidenceChecked: true, recordedExecutions: [
        { executionCommandId: "paper-command", outcome: "Filled", filledQuantity: 1,
          averageFillPrice: 100, fees: 0.8, executedAtUtc: "2026-10-03T07:00:00Z" }
      ], evidenceConflicts: ["Portfolio change differs from the recorded simulated fill."] } },
  { state: "Active", qualifications: [] }, []);
  assert.match(withClaim, /Unresolved paper claim:<\/strong> Unknown/);
  assert.match(withClaim, /correlation paper-protective-exit-123/);
  assert.match(withClaim, /Durable command records: none found/);
  assert.match(withClaim, /Simulated execution results: Filled command paper-command/);
  assert.match(withClaim, /Matching worker-ledger IDs: none found/);
  assert.match(withClaim, /Durable portfolio-update command IDs: none found/);
  assert.match(withClaim, /Trade audit actions: Trade.ExecutionUnknown/);
  assert.match(withClaim, /Contradictory evidence: Portfolio change differs from the recorded simulated fill/);
  assert.match(withClaim, /missing command or portfolio record does not prove/i);
  assert.doesNotMatch(withClaim, /undefined/);
  assert.doesNotMatch(workerDetailMarkup(worker, null, []), /Frozen worker ID/);
});

test("open workers without a current price do not imply reliable unrealized results", () => {
  const html = workerDetailMarkup({
    ...worker, positionQuantity: 1, currentPrice: null, unrealizedProfitAndLoss: null
  }, { state: "Active", qualifications: [] }, []);
  assert.match(html, /Current paper valuation is unavailable/);
  assert.match(html, /Unrealized results are withheld/);
  assert.match(workerDetailMarkup({ ...worker, positionQuantity: 1,
    unrealizedProfitAndLoss: "2.25", realizedProfitAndLoss: "1.5" },
    null, []), /realized <span class="pnl-positive">\+1\.5<\/span>\s*· unrealized <span class="pnl-positive">\+2\.25<\/span>/);
});

test("worker details distinguish scan observations, decisions and paper fills", () => {
  const html = workerDetailMarkup(worker, {
    state: "Active",
    lastScanAtUtc: "2026-10-03T07:05:00Z",
    qualifications: [{ strategyId: worker.strategyId, symbol: worker.symbol,
      interval: worker.interval, disposition: "Admitted", bullishChecks: 4,
      requiredBullishChecks: 4, signalCloseUtc: "2026-10-03T07:00:00Z",
      reason: "<unsafe>" }]
  }, [{ workerId: worker.workerId, strategyId: worker.strategyId,
    closeTimeUtc: "2026-10-03T07:00:00Z", interval: worker.interval,
    action: "Open", reason: "RSI divergence <confirmed>" }]);
  assert.match(html, /Scan observations \(not trades\)/);
  assert.match(html, /Recorded decisions \(pre-risk; not fills\)/);
  assert.match(html, /Simulated execution \(1 total\)/);
  assert.match(html, /4\/4/);
  assert.match(html, /&lt;unsafe&gt;/);
  assert.match(html, /&lt;confirmed&gt;/);
  assert.doesNotMatch(html, /<unsafe>|<confirmed>/);
});

test("idle worker does not claim an active scan or invent a traded pair", () => {
  const idle = { ...worker, symbol: "", interval: "None", workerId: null,
    runtimeStatus: "Scanning", tradeCount: 0, recentTrades: [] };
  const html = workerDetailMarkup(idle, { state: "Active",
    lastScanAtUtc: null, qualifications: [] }, []);
  assert.match(html, /no completed scan recorded/);
  assert.match(html, /not evidence that its host is healthy/);
  assert.match(html, /No pair reserved/);
  assert.match(html, /No paper fill recorded/);
});

test("completed scan is historical evidence, not a process health claim", () => {
  const boundary = "2026-10-03T07:05:00Z";
  const now = Date.parse("2026-10-03T07:14:59Z");
  const recent = scanStatus({ state: "Active", lastScanAtUtc: boundary,
    lastScanMetrics: { eligiblePairs: 12, evaluatedCandidates: 14,
      qualifiedCandidates: 3, admittedCandidates: 1, rejectedForDurableEvidence: 2 } }, now);
  assert.equal(recent.overdue, false);
  assert.match(recent.text, /current host health is not verified/i);
  assert.match(recent.text, /admitted 1, durable-evidence blocks 2/);
  assert.equal(scanStatus({ state: "Active", lastScanAtUtc: boundary },
    now + 2000).overdue, false);
  assert.equal(scanStatus({ state: "Active", lastScanAtUtc: boundary },
    Date.parse(boundary) + 10 * 60000 + 30001).overdue, true);
  assert.equal(scanStatus({ state: "Active", lastScanAtUtc: null }, now).overdue, true);
  assert.equal(scanStatus({ state: "Disabled", lastScanAtUtc: boundary }, now).overdue, false);
});

test("first scan waits for a closed boundary plus settlement grace without a false outage", () => {
  const started = "2026-10-03T07:02:00Z";
  const pending = { state: "Active", changedAtUtc: started, lastScanAtUtc: null,
    hostHealth: [{ name: "scanner", lastHeartbeatUtc: "2026-10-03T07:04:30Z" },
      { name: "experiments", lastHeartbeatUtc: "2026-10-03T07:04:30Z" }] };
  const waiting = scanStatus(pending, Date.parse("2026-10-03T07:05:29Z"));
  assert.equal(waiting.overdue, false);
  assert.match(waiting.text, /first closed-boundary scan is pending/i);
  assert.equal(scanStatus(pending, Date.parse("2026-10-03T07:05:31Z")).overdue, false);
  assert.equal(scanStatus(pending, Date.parse("2026-10-03T07:10:31Z")).overdue, true);
});

test("fresh host heartbeats are separate from scan freshness and stale hosts alert", () => {
  const now = Date.parse("2026-10-03T07:10:00Z");
  const training = { state: "Active", lastScanAtUtc: "2026-10-03T07:05:00Z",
    hostHealth: [
      { name: "scanner", lastHeartbeatUtc: "2026-10-03T07:09:00Z" },
      { name: "experiments", lastHeartbeatUtc: null }
    ] };
  const status = scanStatus(training, now);
  assert.equal(status.overdue, true);
  assert.match(status.text, /scanner.*experiments missing\/stale/);
  assert.match(status.text, /does not verify market-data continuity/);
  const healthy = scanStatus({ ...training,
    hostHealth: training.hostHealth.map(host => ({ ...host,
      lastHeartbeatUtc: "2026-10-03T07:09:00Z" })) }, now);
  assert.equal(healthy.overdue, false);
});

test("channel lines use only the prior contiguous closed candles", () => {
  const candles = [3, 4, 5, 6].map((high, index) => ({
    openTimeUtc: new Date(Date.parse("2026-10-03T00:00:00Z") + index * 300000).toISOString(),
    high, low: high - 2, isClosed: true
  }));
  const levels = closedChannelLevels(candles, 2, 300000);
  assert.deepEqual(levels, [null, null, { high: 4, low: 1 }, { high: 5, low: 2 }]);
  assert.equal(closedChannelLevels([{ ...candles[0], isClosed: false }, ...candles.slice(1)],
    2, 300000)[2], null);
  assert.equal(closedChannelLevels([...candles.slice(0, 2),
    { ...candles[2], openTimeUtc: "2026-10-03T01:00:00Z" }], 2, 300000)[2], null);
  assert.equal(closedChannelLevels([...candles.slice(0, 2),
    { ...candles[2], isClosed: false }], 2, 300000)[2], null);
  assert.throws(() => closedChannelLevels(candles, 0, 300000), RangeError);
});
