const decimalPattern = /^(?:0|[1-9]\d*)(?:\.\d+)?$/;
let reportingLocale;
let reportingTimeZone;

const escapeHtml = value => String(value ?? "")
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#39;");

const formatNumber = (value, digits = 4) => {
  if (value === null || value === undefined) return "—";
  const number = Number(value);
  if (!Number.isFinite(number)) return "—";
  return new Intl.NumberFormat(reportingLocale, {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits
  }).format(number);
};

const formatTime = value => value
  ? new Intl.DateTimeFormat(reportingLocale, {
    dateStyle: "medium", timeStyle: "short", timeZone: reportingTimeZone
  }).format(new Date(value))
  : "—";

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: "same-origin",
    ...options,
    headers: { "Accept": "application/json", ...(options.headers ?? {}) }
  });
  const text = await response.text();
  let body = null;
  if (text) {
    try { body = JSON.parse(text); }
    catch { body = { message: text }; }
  }
  if (!response.ok) {
    throw new Error(body?.message ?? body?.error ?? `Request failed (${response.status}).`);
  }
  return body;
}

function heading(title, detail, badge = "PAPER") {
  return `<div class="workspace-heading">
    <div><h1>${escapeHtml(title)}</h1><p>${escapeHtml(detail)}</p></div>
    <span class="pill paper">${escapeHtml(badge)}</span>
  </div>`;
}

function setMessage(element, message, kind = "") {
  if (!element) return;
  element.textContent = message ?? "";
  element.className = `form-message ${kind}`.trim();
}

const pnlClass = value => {
  if (value === null || value === undefined || !Number.isFinite(Number(value))) return "pnl-neutral";
  return Number(value) > 0 ? "pnl-positive" : Number(value) < 0 ? "pnl-negative" : "pnl-neutral";
};

const formatSignedNumber = (value, digits = 4) =>
  value !== null && value !== undefined && Number.isFinite(Number(value)) && Number(value) > 0
    ? `+${formatNumber(value, digits)}`
    : formatNumber(value, digits);

const formatSignedExact = value => value === null || value === undefined
  ? "—"
  : `${Number(value) > 0 ? "+" : ""}${escapeHtml(value)}`;

export function workersMarkup(workers, selectedSlot = null, lastScanAtUtc = null, scanOverdue = false) {
  if (!workers?.length) return `<p class="workspace-muted">No worker slots are configured yet.</p>`;
  return workers.map(worker => {
    const activeSymbol = worker.symbol || "No pair reserved";
    const unsafe = ["Unprotected", "RequiresReconciliation", "ProtectionDataStale"].includes(worker.runtimeStatus);
    const active = worker.runtimeStatus === "Running";
    const realizedClass = pnlClass(worker.realizedProfitAndLoss);
    const unrealizedClass = pnlClass(worker.unrealizedProfitAndLoss);
    return `<button type="button" class="worker-row${active ? " worker-active" : ""}${worker.slot === selectedSlot ? " selected" : ""}"
      data-worker-slot="${escapeHtml(worker.slot)}" aria-pressed="${worker.slot === selectedSlot}">
      <div class="worker-meta"><strong>Worker ${escapeHtml(worker.slot)}</strong><span class="pill ${unsafe ? "live-disabled" : active ? "worker-running" : ""}">${escapeHtml(worker.runtimeStatus)}</span></div>
      <small>${escapeHtml(worker.strategyId)}</small>
      <small class="worker-scan${scanOverdue ? " worker-scan-overdue" : ""}">${lastScanAtUtc ? `Last scanned ${escapeHtml(formatTime(lastScanAtUtc))}` : "No completed scan"}${scanOverdue ? " · overdue" : ""}</small>
      <small>${escapeHtml(activeSymbol)}${worker.interval ? ` · ${escapeHtml(worker.interval)}` : ""}</small>
      ${worker.workerId ? `<small class="worker-fill-prices">
        ${worker.openBuyFillPrice != null
          ? `Open BUY fills (weighted) <strong>@ ${formatNumber(worker.openBuyFillPrice, 8)}</strong>`
          : worker.lastBuyFillPrice != null
            ? `Last BUY fill <strong>@ ${formatNumber(worker.lastBuyFillPrice, 8)}</strong>`
            : "No executed BUY fill yet"}
        ${Number(worker.additionCount) > 0 && worker.lastBuyFillPrice != null
          ? ` · Last BUY <strong>@ ${formatNumber(worker.lastBuyFillPrice, 8)}</strong>` : ""}
        ${worker.lastSellFillPrice != null
          ? ` · Last SELL fill <strong>@ ${formatNumber(worker.lastSellFillPrice, 8)}</strong>` : ""}
        ${Number(worker.positionQuantity) > 0 && worker.averageEntryPrice != null
          ? `<span class="secondary-line">Cost basis incl. buy fees @ ${formatNumber(worker.averageEntryPrice, 8)}</span>` : ""}
      </small>` : ""}
      <small class="worker-pnl">Realized <span class="${realizedClass}">${formatSignedNumber(worker.realizedProfitAndLoss)}</span> · Unrealized <span class="${unrealizedClass}">${formatSignedNumber(worker.unrealizedProfitAndLoss)}</span>${Number(worker.positionQuantity) > 0 && worker.unrealizedProfitAndLoss == null ? " (price unavailable)" : ""}</small>
    </button>`;
  }).join("");
}

export function clampAxisCenter(minimum, maximum, range, center, minimumVisibleFraction = 0.25) {
  if (![minimum, maximum, range, center, minimumVisibleFraction].every(Number.isFinite)
    || maximum <= minimum || range <= 0)
    return center;
  const dataRange = maximum - minimum;
  const fraction = Math.min(1, Math.max(0, minimumVisibleFraction));
  const visibleRange = Math.min(dataRange, range, dataRange * fraction);
  const lowestCenter = minimum + visibleRange - range / 2;
  const highestCenter = maximum - visibleRange + range / 2;
  return Math.min(highestCenter, Math.max(lowestCenter, center));
}

export function verticalScaleFactor(deltaY, inverse = false) {
  if (!Number.isFinite(deltaY)) return 1;
  return Math.exp((inverse ? -deltaY : deltaY) / 110);
}

export function panAxisCenter(center, deltaY, range, height, inverse = false) {
  if (![center, deltaY, range, height].every(Number.isFinite) || range <= 0 || height <= 0)
    return center;
  return center + (inverse ? -1 : 1) * deltaY * range / height;
}

export function zoomedBarCount(currentCount, factor, seriesLength, plotWidth) {
  const maximumCount = Math.max(12, Math.floor(plotWidth / 3));
  return Math.min(seriesLength, maximumCount, Math.max(12, Math.round(currentCount * factor)));
}

const intervalLabel = interval => ({
  OneMinute: "1m", FiveMinutes: "5m", FifteenMinutes: "15m",
  ThirtyMinutes: "30m", OneHour: "1h", FourHours: "4h", OneDay: "1d"
})[interval] ?? interval ?? "—";

const strategyDescription = {
  "platform.ema-trend-continuation": "Configurable higher-timeframe EMA trend and slope, bounded signal-EMA pullback, then closed-candle resumption with volume.",
  "platform.donchian-breakout-ensemble": "Configurable nested Donchian channels, higher-timeframe trend, volume participation, and an ATR extension veto.",
  "platform.bollinger-mean-reversion": "Ranging ADX and flat EMA regime, prior Bollinger/RSI extreme, then a closed-candle band re-entry.",
  "platform.rsi-pullback": "Higher-timeframe EMA structure, bounded RSI pullback and turn, then closed-price resumption with volume.",
  "platform.macd-volume": "Configurable higher-timeframe EMA regime, closed-candle MACD cross and accelerating histogram, signal trend EMA, and volume.",
  "platform.volatility-compression-breakout": "Low-percentile Bollinger bandwidth and ATR compression, closed channel break, volume expansion, and ATR extension veto.",
  "platform.cross-sectional-momentum-rotation": "Point-in-time momentum ranking needs the full approved cross-sectional universe; incomplete universe blocks a decision.",
  "platform.relative-strength-pullback-rotation": "Relative-strength pullback across the approved universe needs aligned, closed multi-market evidence.",
  "platform.session-conditioned-breakout": "Closed Donchian break in the approved IANA session; cost and walk-forward baseline gates must also pass.",
  "platform.regime-switching-ensemble": "Higher-timeframe EMA trend, volatility, bandwidth and market-breadth consensus.",
  "platform.three-swing-channel-divergence": "Three confirmed 5m pivots with configurable RSI divergence thresholds, channel proximity, higher-timeframe context, reversal and MACD confirmation."
};

export const strategySettingEffects = {
  minimumAgreement: "Raise: more of the five directional checks must agree, so fewer setups qualify. Lower: more can qualify, but mandatory entry checks and safety gates still apply.",
  maximumHoldingCandles: "Raise: the time-based paper exit waits longer; lower: it expires sooner. A stop, target, or invalidation can still close earlier.",
  regimeFastEma: "Raise: the fast trend average reacts more slowly to recent prices. Lower: it responds sooner and may switch direction more often; it must remain below the slow period.",
  regimeSlowEma: "Raise: the long-term trend filter changes more slowly. Lower: it reacts sooner to reversals; it must remain above the fast period.",
  regimeSlopeLookback: "Raise: measure the fast trend slope over more completed higher-timeframe candles, smoothing short moves. Lower: respond to shorter moves.",
  signalPullbackEma: "Raise: the pullback/resumption reference reacts more slowly and may move the eligible pullback zone. Lower: it follows price more closely; keep it below the invalidation EMA period.",
  signalInvalidationEma: "Raise: the pullback depth and later invalidation reference changes more slowly. Lower: it follows price sooner; keep it above the pullback EMA period.",
  volumePeriod: "Raise: compare current volume against a longer history, smoothing short bursts. Lower: the baseline reacts sooner to recent volume.",
  atrPeriod: "Raise: ATR smooths volatility over more closed candles, changing entry filters or stop buffers more slowly. Lower: it reacts sooner to volatility spikes.",
  minimumAtrPercentile: "Raise: reject more unusually quiet EMA setups. Lower: allow quieter ones; keep this below the maximum percentile.",
  maximumAtrPercentile: "Lower: reject more unusually volatile EMA setups. Raise: allow a wider volatility range; keep this above the minimum.",
  emaPlanModel: "Select pullback swing for new entries: freeze a buffered structural stop and estimated target. The legacy ATR option is retained only for reviewing saved settings and blocks new admissions.",
  stopSwingLookback: "Raise: inspect a longer closed-candle swing, potentially finding a more distant low and wider stop. Lower: use a more recent swing; position sizing can change.",
  stopBufferAtr: "Raise: move the frozen paper stop farther below its structural low, increasing distance and potentially reducing position size. Lower: bring it closer; stops can trigger sooner.",
  targetRiskMultiple: "Raise: place the estimated gross target farther from the signal entry relative to stop distance. Lower: bring it closer; neither setting guarantees a fill or profit.",
  minimumRewardRisk: "Raise: reject more later paper entry prices whose remaining target distance is small relative to stop risk. Lower: admit more marginal gross setups; trading costs still apply.",
  shortChannel: "Raise: a breakout must clear a longer prior short channel, usually requiring a larger move. Lower: detect shorter-term breaks.",
  mediumChannel: "Raise: test a longer medium-term range, making that breakout check harder to pass. Lower: make it more responsive; keep the three channel periods ordered.",
  longChannel: "Raise: test a longer long-term range and filter more breaks. Lower: include shorter breaks; keep it above the medium channel.",
  exitChannel: "Raise: use more preceding closed candles for channel-based invalidation; the relevant boundary can shift. Lower: use a more recent range. This does not disable numeric stops.",
  regimeEma: "Raise: require price relative to a slower higher-timeframe trend average. Lower: let the trend check follow price sooner.",
  volumeMultiplier: "Raise: require a larger current-volume multiple of the preceding average, filtering quieter breakouts. Lower: accept weaker participation.",
  maximumExtensionAtr: "Lower: reject breakouts that have already moved farther beyond the boundary. Raise: tolerate more extension, which can worsen entry price.",
  donchianPlanModel: "Prior break range freezes a buffered stop below the pre-break low and a gross target. Legacy ATR-only plans are shown for review but block new paper admissions.",
  bollingerPeriod: "Raise: calculate bands over a longer window, smoothing the middle band and changing extremes. Lower: follow recent prices more closely.",
  bollingerDeviation: "Raise: move bands farther from the middle, making band extremes rarer. Lower: bring bands closer, allowing more touches; this also affects bandwidth filters.",
  rsiPeriod: "Raise: smooth RSI over more closed candles, reducing short-term swings. Lower: make RSI more responsive and potentially noisier.",
  oversoldRsi: "Raise: a preceding RSI value can qualify as oversold at a less extreme level. Lower: require a deeper prior oversold reading.",
  overboughtRsi: "Lower: a preceding RSI value can qualify as overbought at a less extreme level. Raise: require a stronger prior overbought reading.",
  adxPeriod: "Raise: smooth the range/trend strength reading over more candles. Lower: react to changes in trend strength sooner.",
  maximumAdx: "Lower: demand a flatter market for a mean-reversion entry. Raise: allow entries in stronger trends, which may not revert.",
  flatEmaPeriod: "Raise: calculate the range-regime slope from a slower EMA. Lower: give recent price more influence on the flat-market check.",
  slopeLookback: "Raise: compare EMA slope over more completed regime candles. Lower: respond to shorter changes in trend.",
  maximumEmaSlopePercent: "Lower: require a flatter EMA to call the market ranging. Raise: permit a steeper trend as a range.",
  bollingerPlanModel: "Excursion mid-band freezes a buffered stop below the excursion and targets the signal middle band. Legacy ATR-only plans block new admissions.",
  longExitRsi: "Lower: an open long can close sooner when RSI rises to this level. Raise: wait for a stronger RSI reading; stop/target exits remain active.",
  signalEma: "Raise: the signal-price trend or resumption average reacts more slowly. Lower: it follows recent closes sooner; this changes which closes confirm an entry.",
  longRsiMinimum: "Raise: exclude deeper RSI pullbacks from the bullish setup. Lower: allow deeper pullbacks; this must remain below the long-zone maximum.",
  longRsiMaximum: "Lower: require a deeper bullish pullback before a turn. Raise: allow shallower pullbacks; keep it below the short-zone minimum.",
  shortRsiMinimum: "Raise: require a higher RSI before a bearish pullback turns down. Lower: include less elevated RSI; keep it above the long-zone maximum.",
  shortRsiMaximum: "Lower: exclude the most elevated bearish pullbacks. Raise: include stronger overbought readings; keep it above the short-zone minimum.",
  rsiPlanModel: "Pullback swing freezes a buffered stop below the signal swing and an estimated target. Legacy ATR-only plans block new admissions.",
  macdFast: "Raise: the fast MACD average slows down, potentially delaying crosses. Lower: it reacts sooner; it must stay below the slow period.",
  macdSlow: "Raise: the slow MACD average changes more slowly, affecting cross timing and histogram size. Lower: it reacts sooner; it must stay above the fast period.",
  macdSignal: "Raise: smooth MACD with a slower signal line, delaying some crosses. Lower: react sooner and potentially create more noisy crosses.",
  macdPlanModel: "Pre-cross swing freezes a buffered stop beneath the closed crossing swing and an estimated target. Legacy ATR-only plans block new admissions.",
  compressionPercentile: "Lower: require more unusually compressed ATR and band width before a breakout. Raise: permit less extreme compression.",
  persistenceCandles: "Raise: require compression to last for more preceding closed candles. Lower: accept briefer compressed periods.",
  persistencePercentile: "Lower: each required prior band-width reading must be more compressed. Raise: allow wider prior bands.",
  channelPeriod: "Raise: use a longer prior high/low channel, generally requiring a more substantial break. Lower: react to shorter ranges.",
  compressionPlanModel: "Prior range freezes a buffered stop beneath the compressed channel and an estimated target. Legacy ATR-only plans block new admissions.",
  momentumRankLookback: "Raise: rank returns over a longer daily window, giving recent moves less weight. Lower: emphasize more recent momentum; keep it below the trend lookback.",
  trendRankLookback: "Raise: compare returns over a longer daily trend window. Lower: emphasize more recent performance; keep it above momentum lookback.",
  volatilityLookback: "Raise: estimate daily volatility (and where used, correlation) from a longer sample. Lower: respond sooner to changing risk.",
  liquidityLookback: "Raise: use more closed daily volume observations for the median liquidity estimate. Lower: react sooner to volume changes.",
  trendEma: "Raise: daily price must satisfy a slower absolute-trend average. Lower: the trend reference responds sooner.",
  topRankPercent: "Lower: admit only the strongest-ranked fraction of eligible markets. Raise: include lower-ranked markets too; a rank alone never places a trade.",
  minimumLiquidity: "Raise: demand more median daily quote volume and exclude less-liquid markets. Lower: admit thinner markets; venue minimums still apply.",
  maximumVolatility: "Lower: reject more high-volatility markets. Raise: permit riskier daily-return variation; platform risk limits remain in force.",
  maximumCorrelation: "Lower: exclude markets that track the XBT/EUR benchmark more closely. Raise: allow more correlated non-benchmark markets.",
  momentumMediumWeight: "Raise: medium-window momentum influences the rank more. Lower: it influences the rank less; all four rank weights must total 1.",
  momentumLongWeight: "Raise: long-window momentum influences the rank more. Lower: it influences the rank less; all four rank weights must total 1.",
  momentumTrendWeight: "Raise: distance above the daily trend EMA influences the rank more. Lower: it influences the rank less; all four rank weights must total 1.",
  momentumLiquidityWeight: "Raise: median quote-volume liquidity influences the rank more. Lower: it influences the rank less; all four rank weights must total 1.",
  momentumVolatilityPenalty: "Raise: penalize volatile candidates more in the composite rank. Lower: tolerate more volatility; this is not a risk-limit override.",
  momentumTurnoverPenalty: "Raise: penalize disagreement between medium- and long-term ranks more. Lower: tolerate more rank turnover.",
  momentumPlanModel: "Daily swing freezes a stop beneath prior closed daily lows and an estimated target. Legacy ATR-only plans block new admissions.",
  pullbackFastEma: "Raise: the 4-hour pullback reference follows price more slowly. Lower: it reacts sooner; keep it below the structural EMA period.",
  pullbackSlowEma: "Raise: the 4-hour structural support average moves more slowly. Lower: it follows recent prices sooner; keep it above the fast period.",
  maximumPullbackAtr: "Lower: reject pullbacks farther from EMA support in ATR units. Raise: allow deeper pullbacks; stops and risk checks still apply.",
  pullbackRsiMinimum: "Raise: exclude deeper 4-hour RSI pullbacks. Lower: allow deeper cooling; keep this below the maximum.",
  pullbackRsiMaximum: "Lower: require more RSI cooling before recovery. Raise: admit shallower pullbacks; keep this above the minimum.",
  relativeRankingModel: "Daily excess breadth compares closed daily returns with XBT/EUR and the eligible-universe median. Legacy duplicated ranks block new admissions.",
  relativePlanModel: "4-hour structure freezes a swing stop and estimates a prior-channel target after confirmation. Legacy ATR-only plans block new admissions.",
  targetChannelLookback: "Raise: search a longer preceding 4-hour channel for the estimated resistance target. Lower: focus on nearer history; it must cover the stop swing window.",
  relativeMediumWeight: "Raise: medium-horizon return relative to XBT/EUR affects rank more. Lower: it affects rank less; all five relative weights must total 1.",
  relativeLongWeight: "Raise: long-horizon absolute return affects rank more. Lower: it affects rank less; all five relative weights must total 1.",
  relativeBenchmarkWeight: "Raise: the share of daily returns beating XBT/EUR affects rank more. Lower: it affects rank less; all five relative weights must total 1.",
  relativeMedianWeight: "Raise: the share of daily returns beating the eligible-universe median affects rank more. Lower: it affects rank less; all five relative weights must total 1.",
  relativeRiskAdjustedWeight: "Raise: return adjusted for volatility affects rank more. Lower: it affects rank less; all five relative weights must total 1.",
  sessionTimeZone: "Changing this moves the local session window in UTC, including daylight-saving transitions. It does not change your reporting time zone.",
  sessionStartHour: "Move later: fewer early-session breakouts can qualify. Move earlier: include more of the local day; start must precede end on the same day.",
  sessionStartMinute: "Move later: narrow the eligible local session from its start. Move earlier: widen it; use five-minute increments.",
  sessionEndHour: "Move earlier: exclude late-session breakouts. Move later: include more of the local day; end must follow start on the same day.",
  sessionEndMinute: "Move earlier: narrow the eligible local session at its end. Move later: widen it; use five-minute increments.",
  weekdaysOnly: "On: reject Saturday and Sunday in the selected market time zone. Off: the same intraday window also applies on weekends.",
  signalMomentumLookback: "Raise: compare signal momentum over more completed candles, smoothing short surges. Lower: react sooner to recent moves.",
  crisisAtrPercent: "Lower: declare crisis volatility at a smaller ATR-to-price percentage and block more new entries. Raise: tolerate larger moves; safety ceilings still apply.",
  crisisBandwidthPercent: "Lower: declare a crisis at narrower Bollinger bands and block more new entries. Raise: tolerate wider bands.",
  minimumBreadth: "Raise: require more of the eligible universe to be above its daily trend EMA. Lower: accept weaker market-wide participation.",
  expansionAtrPercent: "Lower: block late entries at a smaller ATR-to-price percentage. Raise: tolerate more expansion; crisis checks still apply.",
  regimePlanModel: "4-hour swing freezes an ensemble-specific buffered stop, not the selected component's stop, and estimates a target. Legacy ATR plans block new admissions.",
  planAtrPeriod: "Raise: smooth the 4-hour ATR used to buffer the ensemble stop. Lower: make its stop buffer react sooner to recent volatility.",
  pivotSideBars: "Raise: require more closed bars on both sides to confirm each pivot, delaying or filtering swings. Lower: recognize pivots sooner, with less confirmation.",
  maximumPivotLookback: "Raise: search farther back for the three confirmed swings. Lower: require a more recent three-swing pattern.",
  channelProximityPercent: "Lower: require the third swing nearer the outside of the 5-minute channel. Raise: accept swings farther inside the channel.",
  contextBearishMinimumPercent: "Raise: require the higher-timeframe bearish context nearer the channel top. Lower: allow bearish setups farther from the top.",
  contextBullishMaximumPercent: "Lower: require the higher-timeframe bullish context nearer the channel bottom. Raise: allow bullish setups farther from the bottom.",
  minimumAlignedContextTimeframes: "Raise from one to two: require both 1-hour and 4-hour channels to align. Lower: accept either one; other mandatory checks remain.",
  minimumRsiDivergencePoints: "Raise: require a stronger opposing RSI change between successive confirmed price swings. Lower: allow subtler divergence.",
  minimumPriceProgressPercent: "Raise: require more progress from first to third price swing. Lower: allow smaller three-swing price changes.",
  requireClosedReversal: "On: require a closed reversal beyond the previous candle's opposite extreme. Off: omit this optional confirmation, but pivots and context still apply.",
  requireMacdConfirmation: "On: require aligned MACD line/signal and improving histogram. Off: omit this optional confirmation, but pivots and context still apply.",
  threeSwingPlanModel: "Confirmed pivot freezes a buffered stop beneath the third swing or reversal low and estimates a target. Legacy ATR-only plans block new admissions.",
  exitOnMacdReversal: "On: a later closed MACD reversal can invalidate an open paper position before its stop or target. Off: this exit is omitted; numeric protection remains active."
};

const strategyOptionLabels = {
  pullbackSwing: "Buffered pullback swing",
  priorBreakRange: "Prior break range",
  excursionMidBand: "Excursion stop / middle-band target",
  crossSwing: "Buffered pre-cross swing",
  priorRange: "Prior compressed range",
  dailySwing: "Prior daily swing",
  fourHourStructure: "4-hour swing / channel target",
  fourHourSwing: "Prior 4-hour ensemble swing",
  confirmedPivot: "Confirmed third pivot",
  dailyExcessBreadth: "Daily excess breadth",
  legacyAtrPlan: "Legacy ATR-only (review; no new entries)",
  legacyRanks: "Legacy duplicated ranks (review; no new entries)"
};

export function strategySettingHelpMarkup(field, strategy) {
  const effect = typeof field.minimum === "number" && field.minimum === field.maximum
    ? "Fixed approved value; changing it is not permitted for this strategy."
    : strategySettingEffects[field.key] ?? "Adjustment guidance is unavailable; do not change this setting without review.";
  const relativeHolding = field.key === "maximumHoldingCandles"
    && strategy.strategyId === "platform.relative-strength-pullback-rotation"
    && strategy.strategyVersion >= 5
    ? " For this version, holding time counts confirmed 1-hour candles."
    : "";
  return `<small><strong>What it controls:</strong> ${escapeHtml(field.description)}</small>
    <small><strong>If changed:</strong> ${escapeHtml(effect + relativeHolding)}</small>`;
}

const strategyChartExplanation = {
  "platform.ema-trend-continuation": "Signal chart: configured pullback/invalidation rules; higher-timeframe EMA regime and slope are recorded separately. New v5 paper plans freeze the closed pullback swing stop and estimated target; admitted plan markers show the recorded levels.",
  "platform.donchian-breakout-ensemble": "Configured prior closed-candle Donchian high-low channels are drawn; higher-timeframe trend and volume gates remain in worker evidence.",
  "platform.bollinger-mean-reversion": "Configured Bollinger and RSI context; regime, ADX, and closed-band re-entry checks use the worker's closed-candle evidence. New v5 paper plans freeze the excursion stop and signal middle-band target; the numeric plan markers show the admitted levels.",
  "platform.rsi-pullback": "Signal chart: configured RSI and trend rules. The higher-timeframe EMA regime is not projected onto this timeframe. New v5 paper plans freeze the closed pullback swing stop and estimated target at admission; plan markers show recorded levels.",
  "platform.macd-volume": "Signal chart: configured MACD and signal trend rules. Higher-timeframe EMA and volume checks remain in worker evidence. New v5 paper plans pin a stop below the closed crossing swing and an estimated risk-multiple target; admitted plan markers show the frozen levels.",
  "platform.volatility-compression-breakout": "Configured Bollinger bandwidth and prior closed-candle Donchian channel; compression, volume expansion, and ATR-extension checks use closed evidence. New v5 paper plans freeze a buffered stop below the prior compressed range and an estimated target; the recorded plan markers show those levels.",
  "platform.cross-sectional-momentum-rotation": "No single-chart overlay represents the full pinned eligible-universe ranking. Recorded worker decisions identify its daily rank, absolute trend, liquidity, volatility and correlation gates; new v4 paper plans freeze a stop below the preceding closed daily swing and a gross estimated target. Admitted plan markers show those levels.",
  "platform.relative-strength-pullback-rotation": "No single-chart overlay represents aligned multi-market relative strength. Recorded decisions identify the cross-market evidence.",
  "platform.session-conditioned-breakout": "Signal chart: configured prior closed-candle Donchian high-low channel. Session, cost, and walk-forward gates remain in worker evidence.",
  "platform.regime-switching-ensemble": "The chart shows price and recorded decisions; the pinned selected component and daily regime, volatility, bandwidth and eligible-universe breadth require multi-input evidence. New v5 paper plans freeze an independent ensemble four-hour swing stop and gross estimated target, not the selected component's own stop. Admitted plan markers show the recorded levels.",
  "platform.three-swing-channel-divergence": "Signal chart: confirmed pivots and RSI divergence are drawn from available closed evidence; configured reversal, MACD and 1h/4h context appear in the worker decision. New v4 paper plans freeze a stop below the confirmed third swing/reversal low and a gross estimated target. Admitted plan markers show those saved levels, not a projected fill."
};

function workerStrategySettings(worker) {
  try {
    const settings = JSON.parse(worker?.strategyParameters || "{}");
    return settings && typeof settings === "object" && !Array.isArray(settings) ? settings : {};
  } catch {
    return {};
  }
}

export function workerStrategyVisualMarkup(worker) {
  if (!worker)
    return `<span class="workspace-muted">Select a worker to display its strategy-specific chart guides.</span>`;
  const description = strategyChartExplanation[worker.strategyId]
    ?? "No strategy-specific chart overlay is registered; review the recorded worker decisions.";
  const settings = workerStrategySettings(worker);
  const configuredLine = worker.strategyId === "platform.donchian-breakout-ensemble"
    ? `Configured Donchian periods: ${settings.shortChannel ?? 20} / ${settings.mediumChannel ?? 55} / ${settings.longChannel ?? 100}.`
    : ["platform.volatility-compression-breakout", "platform.session-conditioned-breakout"].includes(worker.strategyId)
      ? `Configured channel period: ${settings.channelPeriod ?? 20}.`
      : worker.strategyId === "platform.three-swing-channel-divergence"
        ? `Configured 5-minute channel: ${settings.channelPeriod ?? 50}; pivot lookback: ${settings.maximumPivotLookback ?? 120}.`
        : "";
  const interval = worker.interval ? intervalLabel(worker.interval) : "assigned signal interval";
  return `<strong>Worker ${escapeHtml(worker.slot)} strategy overlay</strong>
    <span>${escapeHtml(description)}</span>
    ${configuredLine ? `<small>${escapeHtml(configuredLine)}</small>` : ""}
    <small>Worker signal interval: ${escapeHtml(interval)}. Decision markers are recorded pre-risk decisions, not fills or guarantees.</small>`;
}

export function scanStatus(training, now = Date.now()) {
  if (training?.state !== "Active")
    return { text: "Scanner stopped. No new pairs can be admitted.", overdue: false };
  const scanIntervalMs = 5 * 60 * 1000;
  const restGraceMs = 30 * 1000;
  const hosts = training.hostHealth;
  const hostOverdue = Array.isArray(hosts) && hosts.some(host => {
    const at = Date.parse(host.lastHeartbeatUtc);
    return !Number.isFinite(at) || at > now || now - at > 2 * 60 * 1000;
  });
  const hostText = Array.isArray(hosts)
    ? ` Host heartbeats: ${hosts.map(host => {
      const at = Date.parse(host.lastHeartbeatUtc);
      return `${host.name} ${!Number.isFinite(at) || at > now || now - at > 2 * 60 * 1000
        ? "missing/stale" : formatTime(host.lastHeartbeatUtc)}`;
    }).join(", ")}. A heartbeat does not verify market-data continuity or a trade.`
    : " Current host health is not verified.";
  if (!training.lastScanAtUtc) {
    const started = Date.parse(training.changedAtUtc);
    const firstDue = Math.floor(started / scanIntervalMs) * scanIntervalMs + scanIntervalMs + restGraceMs;
    const pending = Number.isFinite(started) && started <= now && now <= firstDue + scanIntervalMs;
    return {
      text: pending
        ? `Enabled; first closed-boundary scan is pending. Allow for REST settlement and check host health if it remains pending.${hostText}`
        : `Enabled; no completed scan recorded. Check the market-data host.${hostText}`,
      overdue: !pending || Boolean(hostOverdue)
    };
  }
  const boundary = Date.parse(training.lastScanAtUtc);
  if (!Number.isFinite(boundary))
    return { text: "Recorded scan time is invalid. Check the market-data host.", overdue: true };
  const overdue = now - boundary > 2 * scanIntervalMs + restGraceMs || boundary > now;
  const metrics = training.lastScanMetrics;
  const counts = metrics
    ? ` Pairs ${metrics.eligiblePairs}, evaluated ${metrics.evaluatedCandidates}, qualified ${metrics.qualifiedCandidates}, admitted ${metrics.admittedCandidates}, durable-evidence blocks ${metrics.rejectedForDurableEvidence}.`
    : "";
  return { text: `${overdue ? "Scan overdue: " : ""}Last completed scan boundary ${formatTime(training.lastScanAtUtc)}.${counts}${hostText}`,
    overdue: overdue || Boolean(hostOverdue) };
}

export function workerDetailMarkup(worker, training, decisions) {
  if (!worker) return `<p class="workspace-muted">Select a worker to see its assigned strategy, last scan evidence, decisions and simulated fills.</p>`;
  const observations = (training?.qualifications ?? [])
    .filter(item => item.strategyId === worker.strategyId
      && (!worker.symbol || item.symbol?.toLowerCase() === worker.symbol.toLowerCase()))
    .slice(0, 5);
  const recentDecisions = (decisions ?? [])
    .filter(item => item.workerId === worker.workerId && item.strategyId === worker.strategyId)
    .slice(-5).reverse();
  const runtime = worker.runtimeStatus === "WaitingForWorker"
    ? "Pair admitted, but no experiment worker has been created yet. Check that the experiment host is running."
    : worker.runtimeStatus === "Scanning"
      ? "No pair reserved. This slot is eligible to scan, not evidence that its host is healthy."
      : worker.failureReason || `Worker runtime: ${worker.runtimeStatus}.`;
  return `<div class="worker-detail-heading">
      <div><strong>Worker ${escapeHtml(worker.slot)} · ${escapeHtml(worker.strategyId)}</strong>
        <span class="secondary-line">${escapeHtml(worker.symbol || "No pair reserved")} · ${escapeHtml(worker.runtimeStatus)} · paper funds only</span></div>
      ${worker.symbol ? `<span class="pill paper">Chart: ${escapeHtml(worker.symbol)}</span>` : ""}
    </div>
    <p><strong>Approved rule:</strong> ${escapeHtml(strategyDescription[worker.strategyId] ?? "Read the recorded decision and approved strategy definition for the exact gates.")}</p>
    <p class="workspace-muted">The chart displays recorded decision times and simulated fills. Current indicators and three-swing pivots are visual context, not a replay of the historical decision's full multi-timeframe input or an execution guarantee.</p>
    <p><strong>Timeframes:</strong> ${escapeHtml((worker.analysisIntervals ?? []).map(intervalLabel).join(" / ") || "Assigned on admission")}
      ${worker.interval ? `· signal ${escapeHtml(intervalLabel(worker.interval))}` : ""}.
      ${training?.lastScanAtUtc ? `Last scanned ${escapeHtml(formatTime(training.lastScanAtUtc))}.` : "no completed scan recorded."} ${escapeHtml(runtime)}</p>
    ${worker.runtimeStatus === "RequiresReconciliation" && worker.workerId
      ? `<p class="workspace-error"><strong>Frozen worker ID:</strong> ${escapeHtml(worker.workerId)}. Investigate its claim and audit correlation; do not retry or release it without a verified outcome.</p>`
      : ""}
    ${worker.runtimeStatus === "RequiresReconciliation" && worker.unresolvedExecution
      ? `<p class="workspace-error"><strong>Unresolved paper claim:</strong> ${escapeHtml(worker.unresolvedExecution.status)}
        · ${escapeHtml(worker.unresolvedExecution.strategyId)} · ${escapeHtml(worker.unresolvedExecution.symbol)}
        · decision ${escapeHtml(formatTime(worker.unresolvedExecution.decisionAsOfUtc))}
        · correlation ${escapeHtml(worker.unresolvedExecution.correlationId)}
        · command ${escapeHtml(worker.unresolvedExecution.executionCommandId || "not recorded")}.
        ${worker.unresolvedExecution.commandEvidenceChecked
          ? `Durable command records: ${(worker.unresolvedExecution.recordedCommandIds ?? []).length
            ? worker.unresolvedExecution.recordedCommandIds.map(escapeHtml).join(", ") : "none found"}.`
          : "Durable command records not inspected."}
        ${worker.unresolvedExecution.executionEvidenceChecked
          ? `Simulated execution results: ${(worker.unresolvedExecution.recordedExecutions ?? []).length
            ? worker.unresolvedExecution.recordedExecutions.map(result =>
              `${escapeHtml(result.outcome)} command ${escapeHtml(result.executionCommandId)}, quantity ${formatNumber(result.filledQuantity, 8)}, price ${formatNumber(result.averageFillPrice, 8)}, fee ${formatNumber(result.fees, 8)} at ${escapeHtml(formatTime(result.executedAtUtc))}`).join("; ")
            : "none found"}.`
          : "Simulated execution results not inspected."}
        Matching worker-ledger IDs: ${(worker.unresolvedExecution.matchingWorkerLedgerIds ?? []).length
          ? worker.unresolvedExecution.matchingWorkerLedgerIds.map(escapeHtml).join(", ") : "none found"}.
        ${worker.unresolvedExecution.portfolioEvidenceChecked
          ? `Durable portfolio-update command IDs: ${(worker.unresolvedExecution.recordedPortfolioCommandIds ?? []).length
            ? worker.unresolvedExecution.recordedPortfolioCommandIds.map(escapeHtml).join(", ") : "none found"}.`
          : "Durable portfolio updates not inspected."}
        ${worker.unresolvedExecution.auditEvidenceChecked
          ? `Trade audit actions: ${(worker.unresolvedExecution.recordedAuditActions ?? []).length
            ? worker.unresolvedExecution.recordedAuditActions.map(escapeHtml).join(", ") : "none found"}.`
          : "Trade audit actions not inspected."}
        ${(worker.unresolvedExecution.evidenceConflicts ?? []).length
          ? `Contradictory evidence: ${worker.unresolvedExecution.evidenceConflicts.map(escapeHtml).join("; ")}`
          : ""}
        A missing command or portfolio record does not prove that no paper fill occurred; reconcile the durable command, simulated fill, worker ledger, portfolio and audit before changing any state.</p>`
      : ""}
    <p><strong>Paper state:</strong> starting cash ${formatNumber(worker.startingCash)} · cash ${formatNumber(worker.cashBalance)}
      · position ${formatNumber(worker.positionQuantity, 8)} · cost basis incl. buy fees ${formatNumber(worker.averageEntryPrice, 8)}
      · latest closed valuation price ${formatNumber(worker.currentPrice, 8)} (${escapeHtml(formatTime(worker.currentPriceAsOfUtc))})
      · realized ${formatSignedNumber(worker.realizedProfitAndLoss)} · unrealized ${formatSignedNumber(worker.unrealizedProfitAndLoss)}
      · additions ${escapeHtml(worker.additionCount ?? "—")}/${escapeHtml(worker.maximumAdditions ?? "—")}.</p>
    <p><strong>Executed prices (not strategy targets):</strong>
      ${worker.openBuyFillPrice == null ? "No open BUY fill average" : `Open BUY fills weighted @ ${formatNumber(worker.openBuyFillPrice, 8)}`}
      · ${worker.lastBuyFillPrice == null ? "No BUY fill" : `Last BUY @ ${formatNumber(worker.lastBuyFillPrice, 8)}`}
      · ${worker.lastSellFillPrice == null ? "No SELL fill" : `Last SELL @ ${formatNumber(worker.lastSellFillPrice, 8)}`}.</p>
    ${Number(worker.positionQuantity) > 0 && worker.currentPrice == null
      ? `<p class="workspace-error">Current paper valuation is unavailable: the latest closed one-minute or five-minute price is missing, future-dated, or more than ten minutes old. Unrealized results are withheld.</p>`
      : ""}
    <div class="worker-detail-columns">
      <div><strong>Scan observations (not trades)</strong>
        ${observations.length ? observations.map(item => `<p><span class="pill">${escapeHtml(item.disposition)}</span>
          ${escapeHtml(item.symbol)} ${escapeHtml(intervalLabel(item.interval))} · ${escapeHtml(formatTime(item.signalCloseUtc))}
          · checks ${escapeHtml(item.bullishChecks ?? "—")}/${escapeHtml(item.requiredBullishChecks ?? "—")}
          <span class="secondary-line">${escapeHtml(item.reason)}</span></p>`).join("")
          : `<p class="workspace-muted">No recent candidate observation for this strategy${worker.symbol ? " and pair" : ""}. An idle slot does not imply a signal.</p>`}</div>
      <div><strong>Recorded decisions (pre-risk; not fills)</strong>
        ${recentDecisions.length ? recentDecisions.map(item => `<p><span class="pill">${escapeHtml(item.action)}</span>
          ${escapeHtml(formatTime(item.closeTimeUtc))} · ${escapeHtml(intervalLabel(item.interval))}
          <span class="secondary-line">${escapeHtml(item.reason)}</span></p>`).join("")
          : `<p class="workspace-muted">No recorded decision for this worker yet.</p>`}
        <strong>Simulated execution (${escapeHtml(worker.tradeCount)} total)</strong>
        ${(worker.recentTrades ?? []).length ? worker.recentTrades.map(trade => `<p>${escapeHtml(formatTime(trade.occurredAtUtc))}
          · ${escapeHtml(trade.direction)} ${formatNumber(trade.quantity, 8)} @ ${formatNumber(trade.executionPrice, 8)}
          · fee ${formatNumber(trade.fee, 8)}</p>`).join("")
          : `<p class="workspace-muted">No paper fill recorded. A signal never guarantees execution.</p>`}</div>
    </div>`;
}

function metric(label, value, note, className = "") {
  return `<article class="workspace-panel metric-card">
    <div class="metric-label">${escapeHtml(label)}</div>
    <div class="metric-value ${className}">${escapeHtml(value)}</div>
    <div class="metric-foot">${escapeHtml(note)}</div>
  </article>`;
}

function calculateIndicators(candles) {
  const closed = candles
    .map((candle, index) => ({ candle, index }))
    .filter(item => item.candle.isClosed);
  const result = {
    closed,
    rsi: [],
    ema20: [],
    ema50: [],
    bollingerUpper: [],
    bollingerMiddle: [],
    bollingerLower: [],
    macd: [],
    macdSignal: [],
    macdHistogram: []
  };
  const closes = closed.map(item => Number(item.candle.close));
  const assign = (name, offset, values) => {
    values.forEach((value, index) => {
      result[name][closed[offset + index].index] = value;
    });
  };
  const emaSeries = (values, period) => {
    const output = Array(values.length).fill(null);
    if (values.length < period) return output;
    let current = values.slice(0, period).reduce((sum, value) => sum + value, 0) / period;
    output[period - 1] = current;
    const multiplier = 2 / (period + 1);
    for (let index = period; index < values.length; index++) {
      current += (values[index] - current) * multiplier;
      output[index] = current;
    }
    return output;
  };

  if (closes.length > 14) {
    let averageGain = 0;
    let averageLoss = 0;
    for (let index = 1; index <= 14; index++) {
      const change = closes[index] - closes[index - 1];
      averageGain += Math.max(change, 0) / 14;
      averageLoss += Math.max(-change, 0) / 14;
    }
    const values = Array(closes.length).fill(null);
    const rsiValue = () => averageLoss === 0
      ? 100
      : averageGain === 0
        ? 0
        : 100 - (100 / (1 + averageGain / averageLoss));
    values[14] = rsiValue();
    for (let index = 15; index < closes.length; index++) {
      const change = closes[index] - closes[index - 1];
      averageGain = (averageGain * 13 + Math.max(change, 0)) / 14;
      averageLoss = (averageLoss * 13 + Math.max(-change, 0)) / 14;
      values[index] = rsiValue();
    }
    assign("rsi", 0, values);
  }

  assign("ema20", 0, emaSeries(closes, 20));
  assign("ema50", 0, emaSeries(closes, 50));
  if (closes.length >= 20) {
    const upper = Array(closes.length).fill(null);
    const middle = Array(closes.length).fill(null);
    const lower = Array(closes.length).fill(null);
    for (let index = 19; index < closes.length; index++) {
      const window = closes.slice(index - 19, index + 1);
      const average = window.reduce((sum, value) => sum + value, 0) / 20;
      const variance = window.reduce((sum, value) => sum + ((value - average) ** 2), 0) / 20;
      const deviation = Math.sqrt(variance) * 2;
      middle[index] = average;
      upper[index] = average + deviation;
      lower[index] = average - deviation;
    }
    assign("bollingerUpper", 0, upper);
    assign("bollingerMiddle", 0, middle);
    assign("bollingerLower", 0, lower);
  }

  const fast = emaSeries(closes, 12);
  const slow = emaSeries(closes, 26);
  const macdStart = 25;
  const macdValues = closes.map((_, index) =>
    fast[index] === null || slow[index] === null ? null : fast[index] - slow[index]);
  const signalInput = macdValues.slice(macdStart).filter(value => value !== null);
  const signalValues = emaSeries(signalInput, 9);
  const line = Array(closes.length).fill(null);
  const signal = Array(closes.length).fill(null);
  const histogram = Array(closes.length).fill(null);
  for (let index = macdStart; index < closes.length; index++) {
    const value = macdValues[index];
    const signalValue = signalValues[index - macdStart];
    if (value === null || signalValue === null) continue;
    line[index] = value;
    signal[index] = signalValue;
    histogram[index] = value - signalValue;
  }
  assign("macd", 0, line);
  assign("macdSignal", 0, signal);
  assign("macdHistogram", 0, histogram);
  return result;
}

export function closedChannelLevels(candles, period, intervalMilliseconds) {
  if (!Number.isInteger(period) || period < 1 || !Number.isFinite(intervalMilliseconds)
    || intervalMilliseconds <= 0)
    throw new RangeError("A positive candle period and interval are required.");
  return candles.map((current, index) => {
    if (!current.isClosed || index < period) return null;
    const window = candles.slice(index - period, index);
    const firstTime = Date.parse(window[0].openTimeUtc);
    if (!Number.isFinite(firstTime) || !window.every((candle, offset) =>
      candle.isClosed
      && Date.parse(candle.openTimeUtc) === firstTime + offset * intervalMilliseconds)
      || Date.parse(current.openTimeUtc) !== firstTime + period * intervalMilliseconds)
      return null;
    const highs = window.map(candle => Number(candle.high));
    const lows = window.map(candle => Number(candle.low));
    if (!highs.every(Number.isFinite) || !lows.every(Number.isFinite)) return null;
    return { high: Math.max(...highs), low: Math.min(...lows) };
  });
}

function drawIndicatorPane(canvas, candles, indicators, geometry, view, kind) {
  const context = canvas.getContext("2d");
  const bounds = canvas.getBoundingClientRect();
  if (bounds.width <= 0 || bounds.height <= 0 || !geometry) return;
  const ratio = Math.max(1, window.devicePixelRatio || 1);
  const pixelWidth = Math.round(bounds.width * ratio);
  const pixelHeight = Math.round(bounds.height * ratio);
  if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
  if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  const width = bounds.width;
  const height = bounds.height;
  const left = geometry.left;
  const right = width - 78;
  const top = 12;
  const bottom = height - 20;
  const paneHeight = bottom - top;
  const start = geometry.visibleStart;
  const step = geometry.step;
  const indicatorView = view.indicators ??= {
    rsiRange: 120,
    rsiOffset: 0,
    macdScale: 1,
    macdOffset: 0
  };
  context.clearRect(0, 0, width, height);
  context.fillStyle = "#131722";
  context.fillRect(0, 0, width, height);
  context.font = "10px Segoe UI, sans-serif";
  context.strokeStyle = "#2a2e39";
  context.fillStyle = "#8b98a9";

  if (kind === "rsi") {
    const range = indicatorView.rsiRange;
    const center = clampAxisCenter(
      0, 100, range, 50 + (indicatorView.rsiOffset ?? 0));
    indicatorView.rsiOffset = center - 50;
    const minimum = center - range / 2;
    const maximum = center + range / 2;
    const y = value => top + ((maximum - value) / range) * paneHeight;
    context.fillStyle = "#8b5cf61c";
    context.fillRect(left, y(70), right - left, y(30) - y(70));
    for (const value of [100, 70, 50, 30, 0]) {
      if (value < minimum || value > maximum) continue;
      const lineY = y(value);
      context.setLineDash(value === 50 ? [2, 4] : [4, 3]);
      context.beginPath();
      context.moveTo(left, lineY);
      context.lineTo(right, lineY);
      context.stroke();
      context.setLineDash([]);
      context.fillText(String(value), width - 32, lineY - 2);
    }
    context.fillStyle = "#8b98a9";
    context.fillText(formatNumber(maximum, 0), width - 34, top + 9);
    context.fillText(formatNumber(minimum, 0), width - 34, bottom - 2);
    context.save();
    context.beginPath();
    context.rect(left, top, right - left, paneHeight);
    context.clip();
    context.strokeStyle = "#e34b6f";
    context.lineWidth = 1.5;
    context.beginPath();
    let started = false;
    for (let index = start; index < start + geometry.visible.length; index++) {
      const value = indicators.rsi[index];
      if (!Number.isFinite(value)) {
        started = false;
        continue;
      }
      const x = left + (index - start + 0.5) * step;
      if (!started) context.moveTo(x, y(value));
      else context.lineTo(x, y(value));
      started = true;
    }
    context.stroke();
    context.restore();
    if (view.crosshair) {
      const crosshairX = view.crosshair.screenX === undefined
        ? view.crosshair.x
        : view.crosshair.screenX - bounds.left;
      const x = Math.min(Math.max(crosshairX, left), right);
      context.strokeStyle = "#a7b1bd88";
      context.setLineDash([3, 3]);
      context.beginPath();
      context.moveTo(x, top);
      context.lineTo(x, bottom);
      context.stroke();
      context.setLineDash([]);
    }
    const hover = view.indicatorHover?.kind === "rsi" ? view.indicatorHover : null;
    if (hover) {
      const hoverX = Math.min(Math.max(hover.x, left), right);
      const slot = Math.floor((hoverX - left) / step);
      const candleIndex = start + slot;
      const candle = candles[candleIndex];
      const value = indicators.rsi[candleIndex];
      const hoverY = Math.min(Math.max(hover.y, top), bottom);
      context.save();
      context.beginPath();
      context.rect(left, top, right - left, paneHeight);
      context.clip();
      context.strokeStyle = "#a7b1bd88";
      context.setLineDash([3, 3]);
      context.beginPath();
      context.moveTo(hoverX, top);
      context.lineTo(hoverX, bottom);
      context.moveTo(left, hoverY);
      context.lineTo(right, hoverY);
      context.stroke();
      context.setLineDash([]);
      if (Number.isFinite(value)) {
        const valueY = y(value);
        context.fillStyle = "#ef667f";
        context.beginPath();
        context.arc(hoverX, valueY, 3.5, 0, Math.PI * 2);
        context.fill();
        context.fillStyle = "#e6edf3";
        context.font = "11px Segoe UI, sans-serif";
        const readout = `${formatChartTime(candle.openTimeUtc, view.interval)} · Close ${formatNumber(candle.close, 8)} · RSI ${formatNumber(value, 2)}`;
        const readoutWidth = Math.min(right - left, context.measureText(readout).width + 12);
        const readoutX = Math.min(Math.max(left, hoverX + 8), right - readoutWidth);
        context.fillStyle = "#161b22";
        context.fillRect(readoutX, top + 2, readoutWidth, 16);
        context.fillStyle = "#e6edf3";
        context.fillText(readout, readoutX + 5, top + 14);
      }
      context.restore();
    }
    let latest;
    for (let index = start + geometry.visible.length - 1; index >= start; index--) {
      if (Number.isFinite(indicators.rsi[index])) {
        latest = indicators.rsi[index];
        break;
      }
    }
    context.fillStyle = "#ef667f";
    context.fillText(`RSI 14  ${latest === undefined ? "—" : formatNumber(latest, 2)}`, left + 4, 10);
    return;
  }

  const visibleValues = [
    ...indicators.macd.slice(start, start + geometry.visible.length),
    ...indicators.macdSignal.slice(start, start + geometry.visible.length),
    ...indicators.macdHistogram.slice(start, start + geometry.visible.length)
  ].filter(Number.isFinite);
  if (!visibleValues.length) {
    context.fillText("MACD needs at least 34 closed candles.", left + 4, 18);
    return;
  }
  const baseRange = Math.max(...visibleValues.map(Math.abs), 0.00000001) * 1.12;
  const range = baseRange * indicatorView.macdScale;
  const dataMinimum = Math.min(...visibleValues);
  const dataMaximum = Math.max(...visibleValues);
  const offset = clampAxisCenter(dataMinimum, dataMaximum, range * 2,
    indicatorView.macdOffset ?? 0);
  indicatorView.macdOffset = offset;
  const y = value => top + ((range + offset - value) / (2 * range)) * paneHeight;
  const zeroY = y(0);
  context.beginPath();
  context.moveTo(left, zeroY);
  context.lineTo(right, zeroY);
  context.stroke();
  context.fillStyle = "#8b98a9";
  context.fillText(formatNumber(range + offset, 5), width - 74, top + 9);
  context.fillText(formatNumber(offset - range, 5), width - 74, bottom - 2);
  context.save();
  context.beginPath();
  context.rect(left, top, right - left, paneHeight);
  context.clip();
  for (let index = start; index < start + geometry.visible.length; index++) {
    const value = indicators.macdHistogram[index];
    if (!Number.isFinite(value)) continue;
    const x = left + (index - start + 0.5) * step;
    context.fillStyle = value >= 0 ? "#26a69a99" : "#ef535099";
    context.fillRect(x - Math.max(1, step * 0.28), Math.min(zeroY, y(value)),
      Math.max(1, step * 0.56), Math.max(1, Math.abs(y(value) - zeroY)));
  }
  const drawLine = (values, color) => {
    context.strokeStyle = color;
    context.lineWidth = 1.3;
    context.beginPath();
    let started = false;
    for (let index = start; index < start + geometry.visible.length; index++) {
      const value = values[index];
      if (!Number.isFinite(value)) {
        started = false;
        continue;
      }
      const x = left + (index - start + 0.5) * step;
      if (!started) context.moveTo(x, y(value));
      else context.lineTo(x, y(value));
      started = true;
    }
    context.stroke();
  };
  drawLine(indicators.macd, "#58a6ff");
  drawLine(indicators.macdSignal, "#e3b341");
  if (view.crosshair) {
    const crosshairX = view.crosshair.screenX === undefined
      ? view.crosshair.x
      : view.crosshair.screenX - bounds.left;
    const x = Math.min(Math.max(crosshairX, left), right);
    context.strokeStyle = "#a7b1bd88";
    context.setLineDash([3, 3]);
    context.beginPath();
    context.moveTo(x, top);
    context.lineTo(x, bottom);
    context.stroke();
    context.setLineDash([]);
  }
  context.restore();
  const macdHover = view.indicatorHover?.kind === "macd" ? view.indicatorHover : null;
  if (macdHover) {
    const hoverX = Math.min(Math.max(macdHover.x, left), right);
    const candleIndex = start + Math.floor((hoverX - left) / step);
    const candle = candles[candleIndex];
    const macdValue = indicators.macd[candleIndex];
    const signalValue = indicators.macdSignal[candleIndex];
    const histogramValue = indicators.macdHistogram[candleIndex];
    const hoverY = Math.min(Math.max(macdHover.y, top), bottom);
    context.save();
    context.beginPath();
    context.rect(left, top, right - left, paneHeight);
    context.clip();
    context.strokeStyle = "#a7b1bd88";
    context.setLineDash([3, 3]);
    context.beginPath();
    context.moveTo(hoverX, top);
    context.lineTo(hoverX, bottom);
    context.moveTo(left, hoverY);
    context.lineTo(right, hoverY);
    context.stroke();
    context.setLineDash([]);
    if (Number.isFinite(macdValue) && Number.isFinite(signalValue) && Number.isFinite(histogramValue)) {
      context.fillStyle = "#58a6ff";
      context.beginPath();
      context.arc(hoverX, y(macdValue), 3, 0, Math.PI * 2);
      context.fill();
      context.fillStyle = "#e3b341";
      context.beginPath();
      context.arc(hoverX, y(signalValue), 3, 0, Math.PI * 2);
      context.fill();
      context.font = "11px Segoe UI, sans-serif";
      const readout = `${formatChartTime(candle.openTimeUtc, view.interval)} · MACD ${formatNumber(macdValue, 5)} · Signal ${formatNumber(signalValue, 5)} · Hist ${formatNumber(histogramValue, 5)}`;
      const readoutWidth = Math.min(right - left, context.measureText(readout).width + 12);
      const readoutX = Math.min(Math.max(left, hoverX + 8), right - readoutWidth);
      context.fillStyle = "#161b22";
      context.fillRect(readoutX, top + 2, readoutWidth, 16);
      context.fillStyle = "#e6edf3";
      context.fillText(readout, readoutX + 5, top + 14);
    }
    context.restore();
  }
  let lastIndex = start + geometry.visible.length - 1;
  while (lastIndex >= start && !Number.isFinite(indicators.macd[lastIndex])) lastIndex--;
  const latestMacd = indicators.macd[lastIndex];
  const latestSignal = indicators.macdSignal[lastIndex];
  context.fillStyle = "#58a6ff";
  context.fillText(`MACD 12/26/9  ${latestMacd === undefined ? "—" : formatNumber(latestMacd, 5)} / ${latestSignal === undefined ? "—" : formatNumber(latestSignal, 5)}`, left + 4, 10);
}

export function freshDisplayTrade(tick, symbol, now = Date.now()) {
  const age = now - Date.parse(tick?.asOfUtc);
  const price = Number(tick?.price);
  return tick?.symbol === symbol && tick.isSnapshot === false
    && Number.isFinite(price) && price > 0
    && Number.isFinite(age) && age >= -5000 && age <= 15000
    ? price : null;
}

export function chartPriceGuide(tick, symbol, candles, interval, now = Date.now(),
  feedConnected = true, quote = null) {
  const closed = candles?.findLast(candle => candle.isClosed);
  const candlePrice = Number(closed?.close);
  const candleTime = Date.parse(closed?.closeTimeUtc);
  const tickPrice = Number(tick?.price);
  const tickTime = Date.parse(tick?.asOfUtc);
  const quotePrice = Number(quote?.price);
  const quoteTime = Date.parse(quote?.asOfUtc);
  const validTick = tick?.symbol === symbol && Number.isFinite(tickPrice) && tickPrice > 0
    && Number.isFinite(tickTime) && tickTime <= now + 5000;
  const validQuote = quote?.symbol === symbol && Number.isFinite(quotePrice) && quotePrice > 0
    && Number.isFinite(quoteTime) && quoteTime <= now + 5000;
  const freshTrade = feedConnected && freshDisplayTrade(tick, symbol, now) !== null;
  if (validQuote && now - quoteTime <= 10000 && (!freshTrade || quoteTime >= tickTime))
    return { price: quotePrice, label: "MID BBO", asOfUtc: quote.asOfUtc, stale: false };
  if (freshTrade)
    return { price: tickPrice, label: symbol.replace("/", ""), asOfUtc: tick.asOfUtc, stale: false };
  const fallback = validQuote && (!validTick || quoteTime > tickTime)
    ? { price: quotePrice, label: "Last quote", asOfUtc: quote.asOfUtc, stale: true }
    : validTick
      ? { price: tickPrice, label: tick.isSnapshot ? "Snapshot" : "Last known",
        asOfUtc: tick.asOfUtc, stale: true }
      : null;
  if (Number.isFinite(candlePrice) && candlePrice > 0 && Number.isFinite(candleTime)
    && (!fallback || candleTime >= Date.parse(fallback.asOfUtc)))
    return { price: candlePrice, label: `Closed ${intervalLabel(interval)}`,
      asOfUtc: closed.closeTimeUtc, stale: true };
  return fallback;
}

export function livePriceDirectionColor(tradePrice, lastClosedPrice) {
  const reference = Number(lastClosedPrice);
  if (!Number.isFinite(reference) || reference <= 0) return "#a7b1bd";
  return tradePrice < reference ? "#ef5350" : "#26a69a";
}

export function drawLiveTradeOverlay(context, { symbol, price, lastClosedPrice, levelY,
  plotLeft, plotRight, priceTop, priceBottom, axisWidth, stale = false,
  label = symbol.replace("/", "") }) {
  const color = stale ? "#8b98a9" : livePriceDirectionColor(price, lastClosedPrice);
  const badgeWidth = axisWidth - 4;
  const offScale = levelY < priceTop ? "↑ " : levelY > priceBottom ? "↓ " : "";
  const guideY = Math.min(Math.max(levelY, priceTop + 1), priceBottom - 1);
  const badgeY = Math.min(Math.max(guideY - 15, priceTop), priceBottom - 30);
  context.save();
  context.strokeStyle = color;
  context.lineWidth = 1;
  context.setLineDash([1, 3]);
  context.beginPath();
  context.moveTo(plotLeft, guideY + 0.5);
  context.lineTo(plotRight, guideY + 0.5);
  context.stroke();
  context.setLineDash([]);
  context.fillStyle = color;
  context.fillRect(plotRight, badgeY, badgeWidth, 30);
  context.fillStyle = "#fff";
  context.textAlign = "center";
  context.font = "9px Segoe UI, sans-serif";
  context.fillText(label, plotRight + badgeWidth / 2, badgeY + 11,
    badgeWidth - 4);
  context.font = "bold 11px Segoe UI, sans-serif";
  context.fillText(offScale + formatNumber(price, 8), plotRight + badgeWidth / 2, badgeY + 25,
    badgeWidth - 4);
  context.restore();
}

function drawCandles(canvas, candles, positions, evidence, markers, drawings, preview, indicators, options, view, worker, liveTrade, liveQuote) {
  const context = canvas.getContext("2d");
  const bounds = canvas.getBoundingClientRect();
  const ratio = Math.max(1, window.devicePixelRatio || 1);
  if (bounds.width <= 0 || bounds.height <= 0) return;
  const pixelWidth = Math.round(bounds.width * ratio);
  const pixelHeight = Math.round(bounds.height * ratio);
  if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
  if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  const width = bounds.width;
  const height = bounds.height;
  context.clearRect(0, 0, width, height);
  context.fillStyle = "#131722";
  context.fillRect(0, 0, width, height);

  const series = candles ?? [];
  if (!series.length) {
    context.fillStyle = "#8b98a9";
    context.fillText("No market candles are available.", 18, 28);
    return null;
  }

  const pad = { left: 12, right: 78, top: 16, bottom: 34 };
  const plotWidth = width - pad.left - pad.right;
  const volumeHeight = Math.min(70, Math.max(34, height * 0.13));
  const priceBottom = height - pad.bottom - volumeHeight;
  const plotHeight = priceBottom - pad.top;
  const maxVisible = Math.max(12, Math.floor(plotWidth / 3));
  const count = Math.min(Math.max(12, Math.round(view.barCount)), maxVisible, series.length);
  const maxOffset = Math.max(0, series.length - count);
  const minimumOffset = -Math.max(0, count - 12);
  view.rightOffset = Math.min(Math.max(minimumOffset, Math.round(view.rightOffset)), maxOffset);
  const targetEnd = series.length - view.rightOffset;
  const visibleStart = Math.max(0, targetEnd - count);
  const visibleEnd = Math.min(series.length, targetEnd);
  const visible = series.slice(visibleStart, visibleEnd);
  if (!visible.length) {
    context.fillStyle = "#8b98a9";
    context.fillText("No candles are visible at this time offset.", 18, 28);
    return null;
  }
  const visibleStartTime = Date.parse(visible[0].openTimeUtc);
  const visibleEndTime = Date.parse(visible[visible.length - 1].closeTimeUtc);
  const intervalMilliseconds = {
    FiveMinutes: 5 * 60 * 1000,
    FifteenMinutes: 15 * 60 * 1000,
    OneHour: 60 * 60 * 1000,
    FourHours: 4 * 60 * 60 * 1000,
    OneDay: 24 * 60 * 60 * 1000
  }[view.interval] ?? 5 * 60 * 1000;
  const chartEndTime = visibleEndTime + Math.max(0, -view.rightOffset) * intervalMilliseconds;
  const workerParameters = workerStrategySettings(worker);
  const channelPeriods = worker?.interval === view.interval
    && worker.symbol?.toLowerCase() === view.symbol?.toLowerCase()
    ? worker.strategyId === "platform.donchian-breakout-ensemble"
      ? [workerParameters.shortChannel ?? 20, workerParameters.mediumChannel ?? 55, workerParameters.longChannel ?? 100]
      : ["platform.volatility-compression-breakout", "platform.session-conditioned-breakout"].includes(worker.strategyId)
        ? [workerParameters.channelPeriod ?? 20] : []
    : [];
  const channels = channelPeriods.map(period => ({
    period, levels: closedChannelLevels(series, period, intervalMilliseconds)
  }));

  const visiblePositions = (positions ?? []).filter(position =>
    String(position.symbol).toLowerCase() === String(view.symbol).toLowerCase());
  const visibleMarkers = (markers ?? []).filter(marker =>
    String(marker.symbol).toLowerCase() === String(view.symbol).toLowerCase()
      && Number.isFinite(Date.parse(marker.time))
      && (marker.kind === "decision"
        || (Number.isFinite(Number(marker.price)) && Number(marker.price) > 0))
      && Date.parse(marker.time) >= visibleStartTime
      && Date.parse(marker.time) <= visibleEndTime);
  const visibleDrawings = (drawings ?? []).filter(annotation => {
    const startTime = Date.parse(annotation.start?.time);
    const endTime = Date.parse(annotation.end?.time);
    return Number.isFinite(startTime) && Number.isFinite(endTime)
      && Math.min(startTime, endTime) <= chartEndTime
      && Math.max(startTime, endTime) >= visibleStartTime;
  });

  const markerCandleIndex = timestamp => {
    const time = Date.parse(timestamp);
    if (!Number.isFinite(time)) return -1;
    return visible.findIndex(candle => {
      const open = Date.parse(candle.openTimeUtc);
      const close = Date.parse(candle.closeTimeUtc);
      return time >= open && time <= close;
    });
  };

  let minPrice = Math.min(...visible.map(item => Number(item.low)));
  let maxPrice = Math.max(...visible.map(item => Number(item.high)));
  const priceIndicatorNames = [
    ...(options.ema20 ? ["ema20"] : []),
    ...(options.ema50 ? ["ema50"] : []),
    ...(options.bollinger ? ["bollingerUpper", "bollingerMiddle", "bollingerLower"] : [])
  ];
  priceIndicatorNames.forEach(name => {
    indicators[name].slice(visibleStart, visibleEnd).filter(Number.isFinite).forEach(value => {
      minPrice = Math.min(minPrice, value);
      maxPrice = Math.max(maxPrice, value);
    });
    channels.forEach(channel => channel.levels.slice(visibleStart, visibleEnd).forEach(level => {
      if (!level) return;
      minPrice = Math.min(minPrice, level.low);
      maxPrice = Math.max(maxPrice, level.high);
    }));
  });
  visiblePositions.forEach(position => {
    for (const value of [position.entryPrice, position.stopLossPrice, position.takeProfitPrice]) {
      const price = Number(value);
      if (value !== null && value !== undefined && Number.isFinite(price) && price > 0) {
        minPrice = Math.min(minPrice, price);
        maxPrice = Math.max(maxPrice, price);
      }
    }
  });
  visibleMarkers.filter(marker => marker.kind !== "decision").forEach(marker => {
    if (markerCandleIndex(marker.time) >= 0) {
      minPrice = Math.min(minPrice, Number(marker.price));
      maxPrice = Math.max(maxPrice, Number(marker.price));
    }
  });
  visibleDrawings.forEach(annotation => {
    minPrice = Math.min(minPrice, Number(annotation.start.price), Number(annotation.end.price));
    maxPrice = Math.max(maxPrice, Number(annotation.start.price), Number(annotation.end.price));
    if (annotation.kind === "channel") {
      minPrice = Math.min(minPrice, Number(annotation.start.price) + annotation.offset,
        Number(annotation.end.price) + annotation.offset);
      maxPrice = Math.max(maxPrice, Number(annotation.start.price) + annotation.offset,
        Number(annotation.end.price) + annotation.offset);
    }
  });
  const dataRange = Math.max(maxPrice - minPrice, Math.abs(maxPrice) * 0.0001, 0.00000001);
  minPrice -= dataRange * 0.04;
  maxPrice += dataRange * 0.04;
  const baseCenterPrice = (maxPrice + minPrice) / 2;
  const priceRange = (maxPrice - minPrice) / Math.min(20, Math.max(0.05, view.priceScale ?? 1));
  const centerPrice = clampAxisCenter(
    minPrice, maxPrice, priceRange, baseCenterPrice + (view.priceOffset ?? 0));
  view.priceOffset = centerPrice - baseCenterPrice;
  minPrice = centerPrice - priceRange / 2;
  maxPrice = centerPrice + priceRange / 2;
  const y = value => pad.top + ((maxPrice - Number(value)) / priceRange) * plotHeight;

  context.font = "11px Segoe UI, sans-serif";
  context.strokeStyle = "#2a2e39";
  context.fillStyle = "#8b98a9";
  for (let line = 0; line <= 4; line++) {
    const lineY = pad.top + (plotHeight * line / 4);
    const value = maxPrice - (priceRange * line / 4);
    context.beginPath();
    context.moveTo(pad.left, lineY);
    context.lineTo(pad.left + plotWidth, lineY);
    context.stroke();
    context.fillText(formatNumber(value, 6), width - pad.right + 7, lineY + 4);
  }

  const step = plotWidth / count;
  const bodyWidth = Math.max(1, Math.min(14, step * 0.66));
  const maxVolume = Math.max(...visible.map(item => Number(item.volume) || 0), 0);
  visible.forEach((candle, index) => {
    const x = pad.left + index * step + step / 2;
    const bullish = Number(candle.close) >= Number(candle.open);
    const color = candle.isClosed ? (bullish ? "#26a69a" : "#ef5350") : "#d29922";
    context.strokeStyle = color;
    context.fillStyle = color;
    context.beginPath();
    context.moveTo(x, y(candle.high));
    context.lineTo(x, y(candle.low));
    context.stroke();
    const top = y(Math.max(Number(candle.open), Number(candle.close)));
    const bottom = y(Math.min(Number(candle.open), Number(candle.close)));
    if (candle.isClosed) {
      context.fillRect(x - bodyWidth / 2, top, bodyWidth, Math.max(1, bottom - top));
    } else {
      context.setLineDash([3, 2]);
      context.strokeRect(x - bodyWidth / 2, top, bodyWidth, Math.max(1, bottom - top));
      context.setLineDash([]);
    }
    if (maxVolume > 0) {
      const volume = Math.max(0, Number(candle.volume) || 0);
      const barHeight = volume / maxVolume * (volumeHeight - 4);
      context.globalAlpha = candle.isClosed ? 0.55 : 0.3;
      context.fillStyle = color;
      context.fillRect(x - bodyWidth / 2, height - pad.bottom - barHeight, bodyWidth, barHeight);
      context.globalAlpha = 1;
    }
  });

  if (view.interval === "FiveMinutes" && evidence?.isAvailable === true) {
    const channelPeriod = workerStrategySettings(worker).channelPeriod ?? 50;
    const closed = series.map((candle, index) => ({ candle, index }))
      .filter(item => item.candle.isClosed)
      .slice(-channelPeriod);
    if (closed.length === channelPeriod && closed[channelPeriod - 1].index >= visibleStart && closed[0].index < visibleEnd) {
      const lower = Math.min(...closed.map(item => Number(item.candle.low)));
      const upper = Math.max(...closed.map(item => Number(item.candle.high)));
      const top = y(upper);
      const bottom = y(lower);
      if (top <= priceBottom && bottom >= pad.top) {
        context.save();
        context.beginPath();
        context.rect(pad.left, pad.top, plotWidth, priceBottom - pad.top);
        context.clip();
        context.fillStyle = "#a371f71a";
        context.fillRect(pad.left, top, plotWidth, bottom - top);
        context.strokeStyle = "#a371f7";
        context.setLineDash([4, 3]);
        for (const level of [upper, lower]) {
          context.beginPath();
          context.moveTo(pad.left, y(level));
          context.lineTo(pad.left + plotWidth, y(level));
          context.stroke();
        }
        context.setLineDash([]);
        context.fillStyle = "#d2a8ff";
        context.fillText(`Three-swing ${channelPeriod} × 5m range`, pad.left + 5,
          Math.max(pad.top + 12, Math.min(priceBottom - 4, top + 14)));
        context.restore();
      }
    }
  }

  const drawIndicatorLine = (name, color, dashed = false) => {
    context.save();
    context.beginPath();
    context.rect(pad.left, pad.top, plotWidth, priceBottom - pad.top);
    context.clip();
    context.strokeStyle = color;
    context.lineWidth = 1.4;
    if (dashed) context.setLineDash([4, 3]);
    context.beginPath();
    let started = false;
    for (let index = visibleStart; index < visibleEnd; index++) {
      const value = indicators[name][index];
      if (!Number.isFinite(value)) {
        started = false;
        continue;
      }
      const x = pad.left + (index - visibleStart + 0.5) * step;
      if (!started) context.moveTo(x, y(value));
      else context.lineTo(x, y(value));
      started = true;
    }
    context.stroke();
    context.restore();
  };
  if (options.ema20) drawIndicatorLine("ema20", "#58a6ff");
  if (options.ema50) drawIndicatorLine("ema50", "#e3b341");
  channels.forEach(({ period, levels }) => {
    for (const [side, color] of [["high", "#58a6ff"], ["low", "#e3b341"]]) {
      context.save();
      context.beginPath();
      context.rect(pad.left, pad.top, plotWidth, priceBottom - pad.top);
      context.clip();
      context.strokeStyle = color;
      context.setLineDash([3, 4]);
      context.beginPath();
      let started = false;
      for (let index = visibleStart; index < visibleEnd; index++) {
        const level = levels[index];
        if (!level) {
          started = false;
          continue;
        }
        const x = pad.left + (index - visibleStart + 0.5) * step;
        if (started) context.lineTo(x, y(level[side]));
        else context.moveTo(x, y(level[side]));
        started = true;
      }
      context.stroke();
      context.restore();
    }
    context.fillStyle = "#a7b1bd";
    context.fillText(`Prior ${period}-bar high / low`, pad.left + 6,
      pad.top + 12 + channelPeriods.indexOf(period) * 13);
  });
  if (options.bollinger) {
    drawIndicatorLine("bollingerUpper", "#a371f7", true);
    drawIndicatorLine("bollingerMiddle", "#a371f7");
    drawIndicatorLine("bollingerLower", "#a371f7", true);
  }

  const drawLevel = (value, color, label) => {
    if (value === null || value === undefined) return;
    const levelY = y(value);
    if (levelY < pad.top || levelY > priceBottom) return;
    context.strokeStyle = color;
    context.setLineDash([5, 4]);
    context.beginPath();
    context.moveTo(pad.left, levelY);
    context.lineTo(pad.left + plotWidth, levelY);
    context.stroke();
    context.setLineDash([]);
    context.fillStyle = color;
    const text = `${label} ${formatNumber(value, 8)}`;
    context.fillText(text, pad.left + 5, levelY - 4);
  };
  visiblePositions.forEach(position => {
    drawLevel(position.entryPrice, "#6ea8fe", `${position.direction === "Short" ? "Short" : "Long"} entry`);
    drawLevel(position.stopLossPrice, "#f85149", "Stop");
    drawLevel(position.takeProfitPrice, "#3fb950", "Target");
  });
  const pivots = evidence?.pivots ?? [];
  for (const pivot of pivots) {
    const candleIndex = markerCandleIndex(pivot.closeTimeUtc);
    if (candleIndex < 0) continue;
    const x = pad.left + step * candleIndex + step / 2;
    const isBearish = evidence.direction === "Bearish";
    const markerY = y(pivot.price);
    context.fillStyle = isBearish ? "#e3b341" : "#3fb950";
    context.beginPath();
    context.arc(x, markerY, 4, 0, Math.PI * 2);
    context.fill();
  }
  if (pivots.length > 1) {
    const visiblePivots = pivots
      .map(pivot => ({ pivot, candleIndex: markerCandleIndex(pivot.closeTimeUtc) }))
      .filter(item => item.candleIndex >= 0);
    context.save();
    context.beginPath();
    context.rect(pad.left, pad.top, plotWidth, priceBottom - pad.top);
    context.clip();
    context.strokeStyle = evidence.direction === "Bearish" ? "#e3b341" : "#3fb950";
    context.setLineDash([4, 3]);
    context.beginPath();
    visiblePivots.forEach((item, index) => {
      const { pivot, candleIndex } = item;
      const x = pad.left + step * candleIndex + step / 2;
      if (index === 0) context.moveTo(x, y(pivot.price));
      else context.lineTo(x, y(pivot.price));
    });
    context.stroke();
    context.setLineDash([]);
    context.restore();
  }

  visibleMarkers.forEach(marker => {
    const candleIndex = markerCandleIndex(marker.time);
    if (candleIndex < 0) return;
    const x = pad.left + step * candleIndex + step / 2;
    const markerY = y(marker.kind === "decision"
      ? visible[candleIndex].close
      : marker.price);
    const isBuy = marker.side?.toLowerCase() === "buy";
    const color = marker.kind === "decision"
      ? marker.action === "Open" || marker.action === "Add" ? "#58a6ff" : "#e3b341"
      : marker.kind === "exit" ? "#d29922" : isBuy ? "#26a69a" : "#ef5350";
    context.fillStyle = color;
    context.beginPath();
    if (marker.kind === "decision") {
      context.moveTo(x, markerY - 6);
      context.lineTo(x + 6, markerY);
      context.lineTo(x, markerY + 6);
      context.lineTo(x - 6, markerY);
    } else if (isBuy) {
      context.moveTo(x, markerY - 6);
      context.lineTo(x - 5, markerY + 3);
      context.lineTo(x + 5, markerY + 3);
    } else {
      context.moveTo(x, markerY + 6);
      context.lineTo(x - 5, markerY - 3);
      context.lineTo(x + 5, markerY - 3);
    }
    context.closePath();
    context.fill();
  });

  const candleIndexForTime = timestamp => {
    const time = Date.parse(timestamp);
    const index = series.findIndex(candle =>
      time >= Date.parse(candle.openTimeUtc) && time <= Date.parse(candle.closeTimeUtc));
    if (index >= 0) return index;
    if (time < Date.parse(series[0].openTimeUtc)) return 0;
    return series.length
      + Math.max(0, Math.floor((time - Date.parse(series[series.length - 1].closeTimeUtc)) / intervalMilliseconds));
  };
  const drawAnnotation = (annotation, dashed = false) => {
    if (!annotation?.start || !annotation?.end) return;
    const startIndex = candleIndexForTime(annotation.start.time);
    const endIndex = candleIndexForTime(annotation.end.time);
    const startX = pad.left + (startIndex - visibleStart + 0.5) * step;
    const endX = pad.left + (endIndex - visibleStart + 0.5) * step;
    context.save();
    context.beginPath();
    context.rect(pad.left, pad.top, plotWidth, priceBottom - pad.top);
    context.clip();
    context.strokeStyle = annotation.kind === "channel" ? "#58a6ff" : "#a5d6ff";
    context.lineWidth = 1.5;
    if (dashed) context.setLineDash([5, 4]);
    context.beginPath();
    context.moveTo(startX, y(annotation.start.price));
    context.lineTo(endX, y(annotation.end.price));
    context.stroke();
    if (annotation.kind === "channel") {
      const offset = Number(annotation.offset) || 0;
      context.save();
      context.globalAlpha = dashed ? 0.06 : 0.1;
      context.fillStyle = "#58a6ff";
      context.beginPath();
      context.moveTo(startX, y(annotation.start.price));
      context.lineTo(endX, y(annotation.end.price));
      context.lineTo(endX, y(annotation.end.price + offset));
      context.lineTo(startX, y(annotation.start.price + offset));
      context.closePath();
      context.fill();
      context.restore();
      context.setLineDash(dashed ? [5, 4] : []);
      context.beginPath();
      context.moveTo(startX, y(annotation.start.price + offset));
      context.lineTo(endX, y(annotation.end.price + offset));
      context.stroke();
    }
    context.setLineDash([]);
    context.fillStyle = annotation.kind === "channel" ? "#58a6ff" : "#a5d6ff";
    context.beginPath();
    context.arc(startX, y(annotation.start.price), 3, 0, Math.PI * 2);
    context.arc(endX, y(annotation.end.price), 3, 0, Math.PI * 2);
    if (annotation.kind === "channel") {
      context.arc(startX, y(annotation.start.price + annotation.offset), 3, 0, Math.PI * 2);
      context.arc(endX, y(annotation.end.price + annotation.offset), 3, 0, Math.PI * 2);
    }
    context.fill();
    context.restore();
  };
  visibleDrawings.forEach(annotation => drawAnnotation(annotation));
  if (preview) drawAnnotation(preview, true);

  context.fillStyle = "#8b98a9";
  context.textAlign = "center";
  context.textBaseline = "alphabetic";
  [0, Math.floor(visible.length / 2), visible.length - 1].forEach((index, position) => {
    const candle = visible[index];
    if (!candle) return;
    const x = pad.left + index * step + step / 2;
    context.textAlign = position === 0 ? "left" : position === 2 ? "right" : "center";
    context.fillText(formatChartTime(candle.openTimeUtc, view.interval), x, height - 7);
  });
  if (view.rightOffset < 0) {
    const futureSlots = Math.min(count - visible.length, -view.rightOffset);
    const x = pad.left + (visible.length + futureSlots - 0.5) * step;
    context.textAlign = "right";
    context.fillText(formatChartTime(new Date(chartEndTime - intervalMilliseconds).toISOString(), view.interval), x, height - 7);
  }
  context.textAlign = "left";

  if (view.crosshair) {
    const crosshairX = view.crosshair.screenX === undefined
      ? view.crosshair.x
      : view.crosshair.screenX - bounds.left;
    const crossX = Math.min(Math.max(crosshairX, pad.left), pad.left + plotWidth);
    const crossY = Math.min(Math.max(view.crosshair.y, pad.top), priceBottom);
    context.strokeStyle = "#a7b1bd88";
    context.setLineDash([3, 3]);
    context.beginPath();
    context.moveTo(crossX, pad.top);
    context.lineTo(crossX, priceBottom);
    if (!view.crosshair.verticalOnly) {
      context.moveTo(pad.left, crossY);
      context.lineTo(pad.left + plotWidth, crossY);
    }
    context.stroke();
    context.setLineDash([]);
    const slotIndex = Math.max(0, Math.floor((crossX - pad.left) / step));
    const index = Math.min(visible.length - 1, slotIndex);
    const candle = visible[index];
    const absoluteIndex = visibleStart + index;
    const rsiValue = options.rsi ? indicators.rsi[absoluteIndex] : null;
    const macdValue = options.macd ? indicators.macdHistogram[absoluteIndex] : null;
    const details = slotIndex >= visible.length
      ? `Future ${formatChartTime(new Date(Date.parse(series[series.length - 1].closeTimeUtc) + (slotIndex - visible.length) * intervalMilliseconds).toISOString(), view.interval)}`
      : `${formatChartTime(candle.openTimeUtc, view.interval)} O ${formatNumber(candle.open, 8)} H ${formatNumber(candle.high, 8)} L ${formatNumber(candle.low, 8)} C ${formatNumber(candle.close, 8)} V ${formatNumber(candle.volume, 4)}${Number.isFinite(rsiValue) ? ` RSI ${formatNumber(rsiValue, 2)}` : ""}${Number.isFinite(macdValue) ? ` MACD ${formatNumber(macdValue, 5)}` : ""}`;
    const candleMarkers = visibleMarkers.filter(marker => markerCandleIndex(marker.time) === index);
    const fillDetails = candleMarkers.map(marker =>
      marker.kind === "decision"
        ? `${marker.action} decision · ${marker.strategyId}: ${marker.reason}`
        : `${(marker.side ?? "").toUpperCase()} ${formatNumber(marker.price, 8)} ${marker.source}`).join(" · ");
    context.font = "11px Segoe UI, sans-serif";
    const tooltipWidth = Math.min(plotWidth, Math.max(
      context.measureText(details).width,
      fillDetails ? context.measureText(fillDetails).width : 0) + 14);
    const tooltipX = Math.min(Math.max(pad.left, crossX + 10), pad.left + plotWidth - tooltipWidth);
    const tooltipHeight = fillDetails ? 49 : 19;
    const tooltipY = Math.max(pad.top, crossY - tooltipHeight - 5);
    context.save();
    context.beginPath();
    context.rect(pad.left, pad.top, plotWidth, priceBottom - pad.top);
    context.clip();
    context.fillStyle = "#161b22";
    context.fillRect(tooltipX, tooltipY, tooltipWidth, tooltipHeight);
    context.fillStyle = "#e6edf3";
    context.fillText(details, tooltipX + 6, tooltipY + 13);
    if (fillDetails) {
      context.fillStyle = "#58a6ff";
      context.fillText(fillDetails.slice(0, 128), tooltipX + 6, tooltipY + 28);
      if (fillDetails.length > 128) {
        context.fillStyle = "#aab6c2";
        context.fillText(fillDetails.slice(128, 248), tooltipX + 6, tooltipY + 43);
      }
    }
    context.restore();
  }
  const guide = chartPriceGuide(liveTrade, view.symbol, series, view.interval,
    Date.now(), view.feedConnected, liveQuote);
  if (guide) {
    const lastClosed = series.findLast(candle => candle.isClosed);
    drawLiveTradeOverlay(context, {
      symbol: view.symbol, price: guide.price, lastClosedPrice: lastClosed?.close,
      label: guide.label, stale: guide.stale, levelY: y(guide.price),
      plotLeft: pad.left, plotRight: pad.left + plotWidth,
      priceTop: pad.top, priceBottom, axisWidth: pad.right
    });
  }
  return {
    left: pad.left,
    top: pad.top,
    width: plotWidth,
    height: plotHeight,
    priceBottom,
    step,
    visible,
    visibleStart,
    visibleEnd,
    seriesLength: series.length,
    intervalMilliseconds,
    lastCloseTimeUtc: series[series.length - 1].closeTimeUtc,
    minPrice,
    maxPrice,
    priceRange
  };
}

function formatChartTime(value, interval) {
  const date = new Date(value);
  return new Intl.DateTimeFormat(reportingLocale, {
    month: "short", day: "numeric", hour: "2-digit",
    ...(interval === "FiveMinutes" ? { minute: "2-digit" } : {}),
    timeZone: reportingTimeZone
  }).format(date);
}

async function renderTrade(root) {
  root.innerHTML = `${heading("Trade", "Automated paper workers scan and trade approved signals; the order ticket is manual. No order is sent to Kraken.")}
    <div class="workspace-grid">
      <section class="workspace-panel worker-panel">
        <div class="workspace-panel-header"><h2>Strategy workers · 10 slots</h2><span id="forward-feed-state" class="pill live-disabled" title="A recent closed public candle is required before paper Start; this does not prove continuity for every pair.">Feed unverified</span></div>
        <div id="worker-list" class="workspace-panel-body worker-list"><p class="workspace-muted">Loading worker state…</p></div>
        <div class="workspace-panel-body worker-controls">
          <button id="start-scanner" class="primary">Start automatic paper trading</button>
          <button id="stop-scanner" class="secondary">Stop scanner</button>
          <p id="scanner-message" class="form-message" aria-live="polite"></p>
        </div>
      </section>
      <section class="workspace-panel market-panel">
        <div class="market-toolbar">
          <div class="pair-picker">
            <label class="visually-hidden" for="pair-search">Search markets</label>
            <input id="pair-search" class="market-symbol" type="search" role="combobox"
              aria-label="Search markets" aria-autocomplete="list" aria-controls="pair-results"
              aria-expanded="false" placeholder="Search pairs (e.g. SOL/USD)" autocomplete="off" />
            <div id="pair-results" class="pair-results" role="listbox" hidden>
              <div id="pair-result-count" class="pair-result-count" aria-live="polite"></div>
              <div id="pair-result-options"></div>
            </div>
          </div>
          <div class="segmented" aria-label="Chart interval">
            <button type="button" data-interval="FiveMinutes" class="active">5m</button>
            <button type="button" data-interval="FifteenMinutes">15m</button>
            <button type="button" data-interval="OneHour">1h</button>
            <button type="button" data-interval="FourHours">4h</button>
            <button type="button" data-interval="OneDay">1d</button>
          </div>
          <div class="chart-controls" aria-label="Chart controls">
            <button id="chart-draw-channel" type="button" title="Draw a parallel price channel" aria-label="Draw a parallel price channel" aria-pressed="false">Draw channel</button>
            <button id="chart-draw-line" type="button" title="Draw a trend line" aria-label="Draw a trend line" aria-pressed="false">Draw line</button>
            <details class="indicator-picker">
              <summary>Indicators</summary>
              <div id="indicator-controls" class="indicator-controls" aria-label="Chart indicators">
                <label><input type="checkbox" data-indicator="rsi" checked /> RSI 14</label>
                <label><input type="checkbox" data-indicator="ema20" checked /> EMA 20</label>
                <label><input type="checkbox" data-indicator="ema50" checked /> EMA 50</label>
                <label><input type="checkbox" data-indicator="bollinger" /> Bollinger 20, 2</label>
                <label><input type="checkbox" data-indicator="macd" checked /> MACD 12, 26, 9</label>
              </div>
            </details>
            <button id="chart-clear-drawings" type="button" title="Clear drawings for this pair">Clear</button>
            <button id="chart-zoom-out" type="button" title="Zoom out" aria-label="Zoom out">−</button>
            <button id="chart-zoom-in" type="button" title="Zoom in" aria-label="Zoom in">+</button>
            <button id="chart-latest" type="button" title="Show latest candles" aria-label="Show latest candles">Latest</button>
            <button id="chart-reset" type="button" title="Reset chart view" aria-label="Reset chart view">Reset</button>
          </div>
          <span id="drawing-hint" class="chart-drawing-hint" aria-live="polite"></span>
          <span class="spacer"></span><span id="market-live" class="workspace-muted" role="status">Trade feed connecting…</span><span id="market-asof" class="workspace-muted"></span>
        </div>
        <div id="rsi-pane" class="indicator-frame">
          <div class="indicator-pane-header"><strong>RSI 14</strong><span>Drag to pan · scroll to zoom · drag scale to zoom</span></div>
          <canvas id="rsi-chart" aria-label="RSI 14 pane with 30, 50, and 70 reference levels. Drag to pan in time and value; scroll vertically to zoom the RSI scale; drag the right value scale to zoom."></canvas>
        </div>
        <div class="chart-frame"><canvas id="market-chart" tabindex="0" aria-label="Market chart. Drag the chart to pan in time and price. Drag the right price scale vertically or scroll over it to zoom price. Scroll over the chart to zoom time; hold Shift or use horizontal scrolling to pan time. Hover for candle details, activate Draw line and click two chart points, or activate Draw channel and click three points."></canvas></div>
        <div id="macd-pane" class="indicator-frame">
          <div class="indicator-pane-header"><strong>MACD 12/26/9</strong><span>Drag to pan · scroll to zoom · drag scale to zoom</span></div>
          <canvas id="macd-chart" aria-label="MACD 12, 26, 9 pane. Drag to pan in time and value; scroll vertically to zoom MACD; drag the right value scale to zoom."></canvas>
        </div>
        <div class="chart-legend"><span class="legend-price">Position entry</span><span class="legend-buy">Paper buy</span><span class="legend-sell">Paper sell</span><span class="legend-decision">Strategy decision (not fill)</span><span class="legend-signal">Strategy pivots</span><span class="legend-exit">Protective stop / target</span><span class="legend-drawing">User drawing / channel</span><span class="legend-ema20">EMA 20 / prior channel high</span><span class="legend-ema50">EMA 50 / prior channel low</span><span class="legend-bollinger">Bollinger Bands</span><span class="legend-volume">Volume</span></div>
        <details id="worker-detail" class="workspace-panel-body worker-detail">
          <summary>Selected worker · strategy and execution evidence</summary>
          <div id="worker-detail-body" class="worker-detail-body"></div>
          <details class="worker-visual-guide">
            <summary>Strategy chart guide</summary>
            <div id="worker-strategy-visuals" class="worker-strategy-visuals" aria-live="polite"></div>
          </details>
        </details>
        <div id="strategy-evidence" class="workspace-panel-body workspace-muted">Strategy evidence is calculated only from safe, closed candles.</div>
      </section>
      <aside class="workspace-panel workspace-ticket">
        <div class="workspace-panel-header"><h2>Manual order ticket</h2><span class="pill paper">Paper</span></div>
        <div class="workspace-panel-body workspace-ticket-body">
          <div class="ticket-view-tabs" role="tablist" aria-label="Ticket views">
            <button id="ticket-view-order" type="button" role="tab" aria-selected="true" aria-controls="ticket-order-panel" class="active">Order ticket</button>
            <button id="ticket-view-activity" type="button" role="tab" aria-selected="false" aria-controls="ticket-activity-panel">Recent activity</button>
          </div>
          <div id="ticket-order-panel" class="ticket-view-panel ticket-order-panel" role="tabpanel" aria-labelledby="ticket-view-order">
          <div class="ticket-tabs">
            <button type="button" class="active" data-order-type="Market">Market</button>
            <button type="button" disabled title="Limit orders are not supported by the current paper route">Limit</button>
            <button type="button" disabled title="Trigger orders are not supported by the current paper route">Trigger</button>
          </div>
          <div class="ticket-side">
            <button id="side-buy" type="button" class="buy active">Buy</button>
            <button id="side-sell" type="button" class="sell" disabled title="Spot sells may only reduce an existing position">Sell</button>
          </div>
          <div class="ticket-field"><label for="order-quantity">Quantity (base asset)</label><input id="order-quantity" type="number" inputmode="decimal" min="0" step="any" autocomplete="off" placeholder="0.00" /></div>
          <div class="ticket-field"><label for="stop-loss">Stop loss (optional price)</label><input id="stop-loss" type="number" inputmode="decimal" min="0" step="any" autocomplete="off" placeholder="Not set" /></div>
          <div class="ticket-field"><label for="take-profit">Take profit (optional price)</label><input id="take-profit" type="number" inputmode="decimal" min="0" step="any" autocomplete="off" placeholder="Not set" /></div>
          <p class="ticket-note">Market simulations use the last closed 1-minute candle. Protective levels are stored with a new fill; the paper exit evaluator must run to trigger them. No automatic target estimate is implied.</p>
          <div class="ticket-buttons"><button id="submit-paper-order" class="primary">Buy with paper funds</button></div>
          <div class="ticket-buttons"><button id="save-position-exits" type="button" hidden>Update open-position exits</button></div>
          <div class="ticket-buttons"><button id="evaluate-paper-exits" type="button" hidden>Evaluate protective exits</button></div>
          <p id="order-message" class="form-message" aria-live="polite"></p>
          <hr class="workspace-divider" />
          <section class="order-book" aria-label="Public Kraken order book">
            <div class="order-book-heading">
              <h3>Kraken order book</h3>
              <span id="order-book-status" class="order-book-status" role="status">Connecting…</span>
            </div>
            <p class="order-book-caption">Public market depth · top 10 · checksum verified · not an order or fill</p>
            <div class="order-book-columns" aria-hidden="true"><span>Price</span><span>Quantity</span></div>
            <div id="order-book-asks" class="order-book-levels asks" aria-label="Ask levels"></div>
            <div id="order-book-bids" class="order-book-levels bids" aria-label="Bid levels"></div>
            <p id="order-book-updated" class="order-book-updated">Waiting for synchronized snapshot…</p>
          </section>
          <hr class="workspace-divider" />
          <p class="ticket-note"><strong>Futures ticket</strong> is separate and unavailable in this deployment. Futures leverage is disabled; Spot orders are never converted into Futures.</p>
          <p id="account-summary" class="ticket-note">Checking validated Kraken account…</p>
          </div>
          <section id="ticket-activity-panel" class="ticket-view-panel ticket-activity-panel" role="tabpanel" aria-labelledby="ticket-view-activity" hidden>
            <div class="ticket-activity-heading"><h3>Recent paper activity</h3><a href="/workspace/history">Full history</a></div>
            <div id="recent-orders" class="workspace-panel-body"></div>
          </section>
        </div>
      </aside>
    </div>`;

  const pairSearch = root.querySelector("#pair-search");
  const pairResults = root.querySelector("#pair-results");
  const pairResultCount = root.querySelector("#pair-result-count");
  const pairResultOptions = root.querySelector("#pair-result-options");
  const canvas = root.querySelector("#market-chart");
  const rsiCanvas = root.querySelector("#rsi-chart");
  const macdCanvas = root.querySelector("#macd-chart");
  const orderMessage = root.querySelector("#order-message");
  const scannerMessage = root.querySelector("#scanner-message");
  let interval = "FiveMinutes";
  let side = "Buy";
  let selectedSymbol = "";
  let activeTicketTab = "order";
  let filteredPairs = [];
  let activePairIndex = -1;
  let currentPairs = [];
  let workers = [];
  let decisions = [];
  let paperPositions = [];
  let paperOrders = [];
  let accounts = [];
  let training = null;
  let selectedWorkerSlot = null;
  let latestCandles = [];
  let latestLiveTrade = null;
  let pendingLiveTrade = null;
  let tickerEvents = null;
  let chartRequestId = 0;
  let chartRefreshPending = false;
  let evidenceKey = "";
  let latestEvidence = null;
  let orderBookSocket = null;
  let orderBookReconnectTimer = 0;
  let orderBookRetry = 0;
  let orderBookActive = true;
  let orderBookRenderFrame = 0;
  let pendingOrderBook = null;
  let orderBookError = "";
  let latestIndicators = calculateIndicators([]);
  let chartGeometry = null;
  let latestQuote = null;
  let quoteRequest = null;
  let quoteError = "";
  let drawingAnchor = null;
  let drawingEnd = null;
  let drawingPreview = null;
  const chartDrawings = [];
  const chartView = {
    barCount: 120,
    rightOffset: 0,
    priceOffset: 0,
    crosshair: null,
    symbol: "",
    interval: "FiveMinutes",
    feedConnected: false,
    drawingMode: null,
    priceScale: 1,
    indicatorHover: null,
    indicators: { rsiRange: 120, rsiOffset: 0, macdScale: 1, macdOffset: 0 }
  };
  const indicatorOptions = Object.fromEntries(
    [...root.querySelectorAll("[data-indicator]")].map(input => [input.dataset.indicator, input.checked]));

  const currentSymbol = () => selectedSymbol;
  const liveStatus = root.querySelector("#market-live");
  const connectTradeTicker = symbol => {
    tickerEvents?.close();
    quoteRequest?.abort();
    quoteRequest = null;
    latestLiveTrade = null;
    pendingLiveTrade = null;
    latestQuote = null;
    quoteError = "";
    liveStatus.textContent = "Trade feed connecting…";
    const url = new URL("/api/marketdata/ticker/stream", window.location.href);
    url.searchParams.set("symbol", symbol);
    const events = new EventSource(url);
    tickerEvents = events;
    events.onmessage = event => {
      if (tickerEvents !== events) return;
      try {
        const tick = JSON.parse(event.data);
        if (tick.symbol === symbol) pendingLiveTrade = tick;
      } catch {
        liveStatus.textContent = "Invalid public trade feed message";
      }
    };
    events.addEventListener("feed-error", event => {
      if (tickerEvents !== events) return;
      pendingLiveTrade = null;
      liveStatus.textContent = event.data;
      redrawChart();
    });
    events.onerror = () => {
      if (tickerEvents !== events) return;
      pendingLiveTrade = null;
      liveStatus.textContent = "Public trade feed disconnected · retrying";
      redrawChart();
    };
    void pollQuote(symbol);
  };
  const pollQuote = async symbol => {
    if (!symbol || quoteRequest) return;
    const request = new AbortController();
    quoteRequest = request;
    try {
      const quote = await api(`/api/marketdata/ticker/quote?symbol=${encodeURIComponent(symbol)}`,
        { signal: request.signal });
      if (quoteRequest !== request || symbol !== currentSymbol()) return;
      if (quote.symbol !== symbol || !Number.isFinite(Number(quote.price))
        || Number(quote.price) <= 0 || !Number.isFinite(Date.parse(quote.asOfUtc)))
        throw new Error("Invalid public quote response");
      latestQuote = quote;
      quoteError = "";
      redrawChart();
    } catch (error) {
      if (!request.signal.aborted && quoteRequest === request)
        quoteError = `Quote refresh unavailable: ${error.message}`;
    } finally {
      if (quoteRequest === request) quoteRequest = null;
    }
  };
  const setOrderBookStatus = (message, state = "") => {
    const status = root.querySelector("#order-book-status");
    status.textContent = message;
    status.dataset.state = state;
  };
  const renderOrderBook = snapshot => {
    const renderSide = (container, levels) => {
      container.replaceChildren();
      for (const level of levels) {
        const row = document.createElement("div");
        row.className = "order-book-level";
        const price = document.createElement("span");
        price.textContent = level.price;
        const quantity = document.createElement("span");
        quantity.textContent = level.quantity;
        row.append(price, quantity);
        container.append(row);
      }
    };
    renderSide(root.querySelector("#order-book-asks"), [...snapshot.asks].reverse());
    renderSide(root.querySelector("#order-book-bids"), snapshot.bids);
    root.querySelector("#order-book-updated").textContent =
      `Synchronized ${new Intl.DateTimeFormat(reportingLocale, {
        timeStyle: "short", timeZone: reportingTimeZone
      }).format(new Date(snapshot.asOfUtc))} · CRC ${snapshot.checksum}`;
    setOrderBookStatus("Live · synchronized", "live");
  };
  const connectOrderBook = symbol => {
    window.clearTimeout(orderBookReconnectTimer);
    orderBookError = "";
    if (orderBookSocket) {
      orderBookSocket.onclose = null;
      orderBookSocket.close();
    }
    const url = new URL("/api/marketdata/orderbook/stream", window.location.href);
    url.protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    url.searchParams.set("symbol", symbol);
    const socket = new WebSocket(url);
    orderBookSocket = socket;
    root.querySelector("#order-book-asks").replaceChildren();
    root.querySelector("#order-book-bids").replaceChildren();
    root.querySelector("#order-book-updated").textContent = "Waiting for synchronized snapshot…";
    setOrderBookStatus("Connecting…");
    socket.addEventListener("open", () => {
      if (orderBookSocket !== socket) return;
      orderBookRetry = 0;
      orderBookError = "";
      setOrderBookStatus("Connected · syncing");
    });
    socket.addEventListener("message", event => {
      if (orderBookSocket !== socket) return;
      let message;
      try {
        message = JSON.parse(event.data);
      } catch {
        setOrderBookStatus("Invalid feed message", "error");
        socket.close();
        return;
      }
      if (message.type === "error") {
        orderBookError = message.message || "Feed synchronization failed";
        setOrderBookStatus(orderBookError, "error");
        return;
      }
      pendingOrderBook = message;
      if (!orderBookRenderFrame) {
        orderBookRenderFrame = window.requestAnimationFrame(() => {
          orderBookRenderFrame = 0;
          if (pendingOrderBook && orderBookSocket === socket) {
            renderOrderBook(pendingOrderBook);
            pendingOrderBook = null;
          }
        });
      }
    });
    socket.addEventListener("close", event => {
      if (!orderBookActive || orderBookSocket !== socket) return;
      const closeReason = event.reason || `WebSocket closed (${event.code})`;
      setOrderBookStatus(`${orderBookError || closeReason} · reconnecting`, "stale");
      const delay = Math.min(30_000, 1_000 * (2 ** Math.min(orderBookRetry++, 5)));
      orderBookReconnectTimer = window.setTimeout(() => connectOrderBook(symbol), delay);
    });
    socket.addEventListener("error", () => {
      if (orderBookSocket === socket) {
        orderBookError = "Public order-book relay WebSocket could not connect";
        setOrderBookStatus(orderBookError, "stale");
      }
    });
  };
  const matchingPosition = () => paperPositions.find(position =>
    position.symbol.toLowerCase() === currentSymbol().toLowerCase());
  const tradeMarkers = () => {
    const displayedWorkers = selectedWorkerSlot === null
      ? workers
      : workers.filter(worker => worker.slot === selectedWorkerSlot);
    const workerMarkers = displayedWorkers.flatMap(worker => (worker.recentTrades ?? []).map(trade => ({
      symbol: trade.symbol ?? worker.symbol,
      side: trade.direction,
      kind: trade.direction?.toLowerCase() === "sell" ? "exit" : "trade",
      time: trade.occurredAtUtc,
      price: trade.executionPrice,
      source: `Worker ${worker.slot} · ${worker.strategyId}`
    })));
    const positionMarkers = paperPositions.map(position => ({
    symbol: position.symbol,
    side: position.direction === "Short" ? "sell" : "buy",
    kind: "position-entry",
    time: position.openedAtUtc,
    price: position.entryPrice,
    source: "Open paper position"
    }));
    const selectedWorkerId = displayedWorkers.length === 1 ? displayedWorkers[0].workerId : null;
    const decisionMarkers = decisions
    .filter(decision => selectedWorkerSlot === null || decision.workerId === selectedWorkerId)
    .filter(decision => decision.action !== "Neutral")
    .map(decision => ({
      symbol: decision.symbol,
      kind: "decision",
      action: decision.action,
      time: decision.closeTimeUtc,
      strategyId: decision.strategyId,
      reason: decision.reason,
      source: "Pre-risk strategy decision"
    }));
    return [...workerMarkers, ...positionMarkers, ...decisionMarkers];
  };
  const redrawChart = () => {
    const selectedWorker = workers.find(worker => worker.slot === selectedWorkerSlot);
    chartView.symbol = currentSymbol();
    chartView.interval = interval;
    chartView.feedConnected = tickerEvents?.readyState === EventSource.OPEN;
    root.querySelector("#rsi-pane").hidden = !indicatorOptions.rsi;
    root.querySelector("#macd-pane").hidden = !indicatorOptions.macd;
    chartGeometry = drawCandles(
      canvas,
      latestCandles,
      paperPositions,
      selectedWorkerSlot === null || selectedWorker?.strategyId === "platform.three-swing-channel-divergence"
        ? latestEvidence : null,
      tradeMarkers(),
      chartDrawings.filter(item => item.symbol.toLowerCase() === currentSymbol().toLowerCase()),
      drawingPreview,
      latestIndicators,
      indicatorOptions,
      chartView,
      selectedWorker ?? null,
      latestLiveTrade,
      latestQuote);
    if (indicatorOptions.rsi)
      drawIndicatorPane(rsiCanvas, latestCandles, latestIndicators, chartGeometry, chartView, "rsi");
    if (indicatorOptions.macd)
      drawIndicatorPane(macdCanvas, latestCandles, latestIndicators, chartGeometry, chartView, "macd");
  };
  const chartPoint = event => {
    if (!chartGeometry) return null;
    const rect = canvas.getBoundingClientRect();
    const x = Math.min(Math.max(event.clientX - rect.left, chartGeometry.left),
      chartGeometry.left + chartGeometry.width - Number.EPSILON);
    const slotIndex = Math.max(0, Math.floor((x - chartGeometry.left) / chartGeometry.step));
    const seriesIndex = chartGeometry.visibleStart + slotIndex;
    const candle = seriesIndex < chartGeometry.seriesLength
      ? latestCandles[seriesIndex]
      : null;
    const yPosition = Math.min(Math.max(event.clientY - rect.top, chartGeometry.top), chartGeometry.priceBottom);
    return {
      time: candle?.openTimeUtc
        ?? new Date(Date.parse(chartGeometry.lastCloseTimeUtc)
          + (seriesIndex - chartGeometry.seriesLength) * chartGeometry.intervalMilliseconds).toISOString(),
      price: chartGeometry.maxPrice
        - ((yPosition - chartGeometry.top) / chartGeometry.height) * chartGeometry.priceRange
    };
  };

  const refreshSideState = () => {
    const canSell = matchingPosition()?.direction === "Long";
    root.querySelector("#side-sell").disabled = !canSell;
    if (!canSell && side === "Sell") side = "Buy";
    root.querySelector("#side-buy").classList.toggle("active", side === "Buy");
    root.querySelector("#side-sell").classList.toggle("active", side === "Sell");
    root.querySelector("#submit-paper-order").textContent = `${side} with paper funds`;
    root.querySelector("#save-position-exits").hidden = !matchingPosition();
    root.querySelector("#evaluate-paper-exits").hidden = paperPositions.length === 0;
  };

  async function refreshChart(background = false) {
    const symbol = currentSymbol();
    if (!symbol) return;
    const selectedInterval = interval;
    const requestId = ++chartRequestId;
    if (!background)
      root.querySelector("#market-asof").textContent = "Loading candles…";
    const query = new URLSearchParams({ symbol, interval: selectedInterval });
    const chartData = await api(`/api/marketdata/candles?${query}`);
    if (requestId !== chartRequestId || symbol !== currentSymbol() || selectedInterval !== interval) return;
    latestCandles = Array.isArray(chartData) ? chartData : chartData.candles;
    latestIndicators = calculateIndicators(latestCandles);
    redrawChart();
    const latestClosed = latestCandles.filter(item => item.isClosed).at(-1);
    root.querySelector("#market-asof").textContent = latestClosed
      ? `Closed ${formatTime(latestClosed.closeTimeUtc)}`
      : "No closed candle";
    const nextEvidenceKey = `${symbol}|${selectedInterval}|${latestClosed?.closeTimeUtc ?? ""}`;
    if (background && evidenceKey === nextEvidenceKey) return;
    evidenceKey = nextEvidenceKey;
    try {
      const result = await api(`/api/strategy/three-swing/evidence?symbol=${encodeURIComponent(symbol)}`);
      if (requestId !== chartRequestId || symbol !== currentSymbol() || selectedInterval !== interval) return;
      latestEvidence = result;
      root.querySelector("#strategy-evidence").classList.remove("workspace-error");
    } catch (error) {
      if (requestId !== chartRequestId || symbol !== currentSymbol() || selectedInterval !== interval) return;
      latestEvidence = { direction: "Unavailable", reason: error.message, pivots: [] };
      root.querySelector("#strategy-evidence").classList.add("workspace-error");
    }
    redrawChart();
    renderEvidence(root.querySelector("#strategy-evidence"), latestEvidence);
  }

  const initialData = await Promise.all([
    api("/api/marketdata/pairs"),
    api("/api/experiments"),
    api("/api/paper-training"),
    api("/api/orders?mode=Paper"),
    api("/api/paper/positions"),
    api("/api/exchange/accounts"),
    api("/api/me").catch(() => null)
  ]);
  currentPairs = initialData[0].filter(pair => pair.isActive);
  workers = initialData[1].workers ?? [];
  decisions = initialData[1].decisions ?? [];
  training = initialData[2];
  paperOrders = initialData[3].orders ?? [];
  paperPositions = initialData[4].positions ?? [];
  accounts = initialData[5] ?? [];
  const preferenceKey = initialData[6]?.id
    ? `fremvo.trade.preferences.v1.${initialData[6].id}`
    : "";
  let savedPreferences = {};
  if (preferenceKey) {
    try {
      const parsed = JSON.parse(window.localStorage.getItem(preferenceKey) ?? "{}");
      if (parsed && typeof parsed === "object" && !Array.isArray(parsed))
        savedPreferences = parsed;
    } catch {
      savedPreferences = {};
    }
  }
  activeTicketTab = savedPreferences.ticketTab === "activity" ? "activity" : "order";
  const validIntervals = ["FiveMinutes", "FifteenMinutes", "OneHour", "FourHours", "OneDay"];
  if (validIntervals.includes(savedPreferences.interval))
    interval = savedPreferences.interval;
  for (const [name, enabled] of Object.entries(savedPreferences.indicators ?? {})) {
    if (Object.hasOwn(indicatorOptions, name) && typeof enabled === "boolean") {
      indicatorOptions[name] = enabled;
      const input = root.querySelector(`[data-indicator="${name}"]`);
      if (input) input.checked = enabled;
    }
  }
  if (Number.isFinite(savedPreferences.indicatorScale?.rsiRange))
    chartView.indicators.rsiRange = savedPreferences.indicatorScale.rsiRange === 100
      ? 120
      : Math.min(240, Math.max(20, savedPreferences.indicatorScale.rsiRange));
  if (Number.isFinite(savedPreferences.indicatorScale?.rsiOffset))
    chartView.indicators.rsiOffset = savedPreferences.indicatorScale.rsiOffset;
  if (Number.isFinite(savedPreferences.indicatorScale?.macdScale))
    chartView.indicators.macdScale = Math.min(20, Math.max(0.05, savedPreferences.indicatorScale.macdScale));
  if (Number.isFinite(savedPreferences.indicatorScale?.macdOffset))
    chartView.indicators.macdOffset = savedPreferences.indicatorScale.macdOffset;
  if (Number.isInteger(savedPreferences.chart?.barCount))
    chartView.barCount = Math.min(500, Math.max(12, savedPreferences.chart.barCount));
  if (Number.isInteger(savedPreferences.chart?.rightOffset))
    chartView.rightOffset = Math.min(10_000, Math.max(-500, savedPreferences.chart.rightOffset));
  if (Number.isFinite(savedPreferences.chart?.priceScale))
    chartView.priceScale = Math.min(20, Math.max(0.05, savedPreferences.chart.priceScale));
  if (Number.isFinite(savedPreferences.chart?.priceOffset))
    chartView.priceOffset = savedPreferences.chart.priceOffset;
  const validDrawingPoint = point => point
    && typeof point.time === "string"
    && Number.isFinite(Date.parse(point.time))
    && Number.isFinite(point.price)
    && point.price > 0;
  if (Array.isArray(savedPreferences.drawings)) {
    for (const drawing of savedPreferences.drawings.slice(-500)) {
      if (!drawing || typeof drawing.symbol !== "string" || drawing.symbol.length > 40
        || !validDrawingPoint(drawing.start) || !validDrawingPoint(drawing.end))
        continue;
      if (drawing.kind === "line") {
        chartDrawings.push({
          symbol: drawing.symbol,
          kind: "line",
          start: { time: drawing.start.time, price: drawing.start.price },
          end: { time: drawing.end.time, price: drawing.end.price }
        });
      } else if (drawing.kind === "channel" && Number.isFinite(drawing.offset)) {
        chartDrawings.push({
          symbol: drawing.symbol,
          kind: "channel",
          start: { time: drawing.start.time, price: drawing.start.price },
          end: { time: drawing.end.time, price: drawing.end.price },
          offset: drawing.offset
        });
      }
    }
  }
  let preferenceSaveTimer = 0;
  const flushTradePreferences = () => {
    window.clearTimeout(preferenceSaveTimer);
    if (!preferenceKey) return;
    try {
      window.localStorage.setItem(preferenceKey, JSON.stringify({
        symbol: selectedSymbol,
        interval,
        ticketTab: activeTicketTab,
        indicators: indicatorOptions,
        indicatorScale: chartView.indicators,
        chart: {
          barCount: chartView.barCount,
          rightOffset: chartView.rightOffset,
          priceScale: chartView.priceScale,
          priceOffset: chartView.priceOffset
        },
        drawings: chartDrawings.slice(-500)
      }));
    } catch {
      return;
    }
  };
  const saveTradePreferences = () => {
    window.clearTimeout(preferenceSaveTimer);
    preferenceSaveTimer = window.setTimeout(flushTradePreferences, 150);
  };
  const updateWorkerView = () => {
    const selected = workers.find(worker => worker.slot === selectedWorkerSlot);
    const scan = scanStatus(training);
    const feed = root.querySelector("#forward-feed-state");
    const observed = training?.forwardFeedObservedRecently === true;
    feed.textContent = observed ? "Feed observed" : "Feed unverified";
    feed.classList.toggle("live-disabled", !observed);
    feed.classList.toggle("paper", observed);
    root.querySelector("#worker-list").innerHTML = workersMarkup(
      workers, selectedWorkerSlot, training?.lastScanAtUtc, scan.overdue);
    root.querySelector("#worker-detail-body").innerHTML = workerDetailMarkup(selected, training, decisions);
    root.querySelector("#worker-strategy-visuals").innerHTML = workerStrategyVisualMarkup(selected);
    root.querySelector("#strategy-evidence").hidden = selectedWorkerSlot !== null
      && selected?.strategyId !== "platform.three-swing-channel-divergence";
  };
  updateWorkerView();
  root.querySelector("#recent-orders").innerHTML = ordersMarkup(initialData[3].orders ?? []);
  root.querySelector("#account-summary").textContent = accounts.some(account => account.canTrade)
    ? "Kraken account validated. Orders here remain simulated and cannot reach the exchange."
    : "Connect and validate a Kraken account before placing paper orders.";
  const restoredSymbol = typeof savedPreferences.symbol === "string"
    ? savedPreferences.symbol.toUpperCase()
    : "";
  const restoredPair = currentPairs.find(pair => pair.symbol.toUpperCase() === restoredSymbol);
  const preferred = restoredPair
    ?? currentPairs.find(pair => pair.symbol.toUpperCase() === "SOL/USD")
    ?? currentPairs.find(pair => pair.symbol.toUpperCase() === "SOL/EUR")
    ?? currentPairs.find(pair => pair.symbol.toUpperCase() === "XBT/EUR")
    ?? currentPairs[0];
  if (preferred) {
    selectedSymbol = preferred.symbol;
    pairSearch.value = preferred.displayName || preferred.symbol;
    connectOrderBook(selectedSymbol);
    connectTradeTicker(selectedSymbol);
  }
  root.querySelectorAll("[data-interval]").forEach(button =>
    button.classList.toggle("active", button.dataset.interval === interval));
  const setTicketTab = tab => {
    activeTicketTab = tab;
    const isOrder = tab === "order";
    root.querySelector("#ticket-order-panel").hidden = !isOrder;
    root.querySelector("#ticket-activity-panel").hidden = isOrder;
    root.querySelector("#ticket-view-order").classList.toggle("active", isOrder);
    root.querySelector("#ticket-view-activity").classList.toggle("active", !isOrder);
    root.querySelector("#ticket-view-order").setAttribute("aria-selected", String(isOrder));
    root.querySelector("#ticket-view-activity").setAttribute("aria-selected", String(!isOrder));
    saveTradePreferences();
  };
  root.querySelector("#ticket-view-order").addEventListener("click", () => setTicketTab("order"));
  root.querySelector("#ticket-view-activity").addEventListener("click", () => setTicketTab("activity"));
  setTicketTab(activeTicketTab);
  renderPairResults("");
  refreshSideState();

  pairSearch.addEventListener("focus", () => {
    renderPairResults(pairSearch.value === (currentPairs.find(pair => pair.symbol === selectedSymbol)?.displayName || selectedSymbol)
      ? ""
      : pairSearch.value);
  });
  pairSearch.addEventListener("input", () => {
    activePairIndex = -1;
    renderPairResults(pairSearch.value);
  });
  pairSearch.addEventListener("keydown", async event => {
    if (event.key === "Escape") {
      pairResults.hidden = true;
      pairSearch.setAttribute("aria-expanded", "false");
      pairSearch.value = currentPairs.find(pair => pair.symbol === selectedSymbol)?.displayName || selectedSymbol;
      return;
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (pairResults.hidden) renderPairResults(pairSearch.value);
      if (!filteredPairs.length) return;
      const delta = event.key === "ArrowDown" ? 1 : -1;
      activePairIndex = (activePairIndex + delta + filteredPairs.length) % filteredPairs.length;
      updateActivePairOption();
      return;
    }
    if (event.key === "Enter" && !pairResults.hidden) {
      event.preventDefault();
      const candidate = filteredPairs[activePairIndex] ?? filteredPairs[0];
      if (candidate) await selectPair(candidate);
    }
  });
  pairResults.addEventListener("click", async event => {
    const option = event.target.closest("[data-pair-symbol]");
    if (!option) return;
    const pair = currentPairs.find(item => item.symbol === option.dataset.pairSymbol);
    if (pair) await selectPair(pair);
  });
  const closePairResults = event => {
    if (!event.target.closest(".pair-picker")) {
      pairResults.hidden = true;
      pairSearch.setAttribute("aria-expanded", "false");
    }
  };
  document.addEventListener("pointerdown", closePairResults);
  root.querySelectorAll("[data-interval]").forEach(button => {
    button.addEventListener("click", async () => {
      interval = button.dataset.interval;
      chartView.barCount = 120;
      chartView.rightOffset = 0;
      chartView.priceScale = 1;
      chartView.priceOffset = 0;
      chartView.indicators = { rsiRange: 120, rsiOffset: 0, macdScale: 1, macdOffset: 0 };
      chartView.crosshair = null;
      drawingAnchor = null;
      drawingEnd = null;
      drawingPreview = null;
      root.querySelector("#drawing-hint").textContent = chartView.drawingMode === "channel"
        ? "Channel drawing restarted for this timeframe. Click two points along one edge, then the offset."
        : chartView.drawingMode === "line"
          ? "Trend-line drawing restarted for this timeframe. Click two points."
          : "";
      root.querySelectorAll("[data-interval]").forEach(item => item.classList.toggle("active", item === button));
      saveTradePreferences();
      await refreshChart();
    });
  });
  root.querySelector("#worker-list").addEventListener("click", async event => {
    const row = event.target.closest("[data-worker-slot]");
    if (!row) return;
    const worker = workers.find(item => item.slot === Number(row.dataset.workerSlot));
    if (!worker) return;
    selectedWorkerSlot = worker.slot;
    root.querySelector("#worker-detail").open = false;
    updateWorkerView();
    if (worker.symbol) {
      const requiredIndicators = {
        "platform.ema-trend-continuation": ["ema20", "ema50"],
        "platform.bollinger-mean-reversion": ["bollinger", "rsi"],
        "platform.rsi-pullback": ["ema20", "ema50", "rsi"],
        "platform.macd-volume": ["ema50", "macd"],
        "platform.volatility-compression-breakout": ["bollinger"],
        "platform.three-swing-channel-divergence": ["rsi", "macd"]
      }[worker.strategyId] ?? [];
      for (const name of requiredIndicators) {
        indicatorOptions[name] = true;
        root.querySelector(`[data-indicator="${name}"]`).checked = true;
      }
      const pair = currentPairs.find(item => item.symbol.toLowerCase() === worker.symbol.toLowerCase())
        ?? { symbol: worker.symbol, displayName: worker.symbol };
      if (validIntervals.includes(worker.interval)) {
        interval = worker.interval;
        root.querySelectorAll("[data-interval]").forEach(button =>
          button.classList.toggle("active", button.dataset.interval === interval));
      }
      try {
        await selectPair(pair, true);
      } catch (error) {
        setMessage(scannerMessage, `Worker ${worker.slot} chart could not load: ${error.message}`, "error");
      }
    } else {
      redrawChart();
    }
  });
  root.querySelector("#chart-zoom-in").addEventListener("click", () => {
    zoomChart(0.75);
    saveTradePreferences();
  });
  root.querySelector("#chart-zoom-out").addEventListener("click", () => {
    zoomChart(1.35);
    saveTradePreferences();
  });
  root.querySelector("#chart-latest").addEventListener("click", () => {
    chartView.rightOffset = 0;
    chartView.crosshair = null;
    saveTradePreferences();
    redrawChart();
  });
  root.querySelector("#chart-reset").addEventListener("click", () => {
    chartView.barCount = 120;
    chartView.rightOffset = 0;
    chartView.priceScale = 1;
    chartView.priceOffset = 0;
    chartView.crosshair = null;
    chartView.indicators = { rsiRange: 120, rsiOffset: 0, macdScale: 1, macdOffset: 0 };
    saveTradePreferences();
    redrawChart();
  });
  const drawingButtons = [
    [root.querySelector("#chart-draw-line"), "line"],
    [root.querySelector("#chart-draw-channel"), "channel"]
  ];
  const setDrawingMode = mode => {
    chartView.drawingMode = chartView.drawingMode === mode ? null : mode;
    drawingAnchor = null;
    drawingEnd = null;
    drawingPreview = null;
    root.querySelector("#drawing-hint").textContent = "";
    drawingButtons.forEach(([button, buttonMode]) => {
      button.classList.toggle("active", chartView.drawingMode === buttonMode);
      button.setAttribute("aria-pressed", String(chartView.drawingMode === buttonMode));
    });
    canvas.classList.toggle("chart-drawing", chartView.drawingMode !== null);
    root.querySelector("#drawing-hint").textContent = chartView.drawingMode === "channel"
      ? "Channel: click two points along one edge, then a third point to set the parallel offset."
      : chartView.drawingMode === "line"
        ? "Trend line: click two points. Press Escape to cancel the current drawing."
        : "";
    redrawChart();
  };
  drawingButtons.forEach(([button, mode]) =>
    button.addEventListener("click", () => setDrawingMode(mode)));
  root.querySelectorAll("[data-indicator]").forEach(input => {
    input.addEventListener("change", () => {
      indicatorOptions[input.dataset.indicator] = input.checked;
      saveTradePreferences();
      redrawChart();
    });
  });
  const bindIndicatorScale = (indicatorCanvas, kind) => {
    let drag = null;
    const plotMetrics = () => {
      const bounds = indicatorCanvas.getBoundingClientRect();
      const top = 12;
      const bottom = bounds.height - 20;
      return { bounds, top, height: Math.max(1, bottom - top), scaleLeft: bounds.width - 78 };
    };
    const macdBaseRange = () => {
      const start = chartGeometry?.visibleStart ?? 0;
      const end = start + (chartGeometry?.visible.length ?? 0);
      const values = [
        ...latestIndicators.macd.slice(start, end),
        ...latestIndicators.macdSignal.slice(start, end),
        ...latestIndicators.macdHistogram.slice(start, end)
      ].filter(Number.isFinite);
      return Math.max(0.00000001, ...values.map(Math.abs)) * 1.12;
    };
    const macdAxisBounds = () => {
      const start = chartGeometry?.visibleStart ?? 0;
      const end = start + (chartGeometry?.visible.length ?? 0);
      const values = [
        ...latestIndicators.macd.slice(start, end),
        ...latestIndicators.macdSignal.slice(start, end),
        ...latestIndicators.macdHistogram.slice(start, end)
      ].filter(Number.isFinite);
      if (!values.length) return [-0.00000001, 0.00000001];
      const low = Math.min(...values);
      const high = Math.max(...values);
      const padding = Math.max((high - low) * 0.08, Math.max(Math.abs(low), Math.abs(high)) * 0.02, 0.00000001);
      return [low - padding, high + padding];
    };
    const panIndicator = (startOffset, deltaY, range, height) => {
      const center = kind === "rsi" ? 50 + startOffset : startOffset;
      const proposed = panAxisCenter(center, deltaY,
        kind === "rsi" ? range : range * 2, height);
      const [minimum, maximum] = kind === "rsi" ? [0, 100] : macdAxisBounds();
      const bounded = clampAxisCenter(minimum, maximum,
        kind === "rsi" ? range : range * 2, proposed);
      if (kind === "rsi") chartView.indicators.rsiOffset = bounded - 50;
      else chartView.indicators.macdOffset = bounded;
    };
    const zoomIndicator = (factor, anchorRatio, range, baseRange) => {
      if (kind === "rsi") {
        const anchor = 50 + chartView.indicators.rsiOffset + range / 2 - anchorRatio * range;
        const nextRange = Math.min(240, Math.max(20, range * factor));
        const center = anchor + (anchorRatio - 0.5) * nextRange;
        chartView.indicators.rsiRange = nextRange;
        chartView.indicators.rsiOffset = clampAxisCenter(0, 100, nextRange, center) - 50;
      } else {
        const anchor = range / 2 + chartView.indicators.macdOffset - anchorRatio * range;
        const nextScale = Math.min(20, Math.max(0.05, chartView.indicators.macdScale * factor));
        const nextRange = baseRange * nextScale * 2;
        const center = anchor + (anchorRatio - 0.5) * nextRange;
        const [minimum, maximum] = macdAxisBounds();
        chartView.indicators.macdScale = nextScale;
        chartView.indicators.macdOffset = clampAxisCenter(minimum, maximum, nextRange, center);
      }
    };
    indicatorCanvas.addEventListener("pointermove", event => {
      const { bounds, scaleLeft } = plotMetrics();
      const onScale = event.clientX - bounds.left >= scaleLeft;
      indicatorCanvas.classList.toggle("indicator-scale-hover", onScale);
      if (!drag || drag.pointerId !== event.pointerId) {
        chartView.indicatorHover = onScale ? null : {
          kind,
          x: event.clientX - bounds.left,
          y: event.clientY - bounds.top
        };
        const mainBounds = canvas.getBoundingClientRect();
        chartView.crosshair = onScale ? null : {
          x: event.clientX - mainBounds.left,
          screenX: event.clientX,
          y: 0,
          verticalOnly: true
        };
        indicatorCanvas.classList.toggle("indicator-pan-hover", !onScale);
        redrawChart();
        return;
      }
      if (drag.mode === "pan") {
        const deltaY = event.clientY - drag.startY;
        panIndicator(drag.startOffset, deltaY, drag.range, drag.height);
        const step = Math.max(1, chartGeometry?.step ?? bounds.width / Math.max(1, chartView.barCount));
        chartView.rightOffset = Math.max(-Math.max(0, chartView.barCount - 12),
          drag.rightOffset + Math.round((event.clientX - drag.startX) / step));
      } else {
        const multiplier = verticalScaleFactor(event.clientY - drag.startY);
        if (kind === "rsi") {
          const range = Math.min(240, Math.max(20, drag.startRange * multiplier));
          chartView.indicators.rsiRange = range;
          const center = drag.anchorValue + (drag.anchorRatio - 0.5) * range;
          chartView.indicators.rsiOffset = clampAxisCenter(0, 100, range, center) - 50;
        } else {
          const scale = Math.min(20, Math.max(0.05, drag.startScale * multiplier));
          const range = drag.baseRange * scale;
          chartView.indicators.macdScale = scale;
          const center = drag.anchorValue + (drag.anchorRatio - 0.5) * range * 2;
          const [minimum, maximum] = macdAxisBounds();
          chartView.indicators.macdOffset = clampAxisCenter(minimum, maximum, range * 2, center);
        }
      }
      redrawChart();
    });
    indicatorCanvas.addEventListener("pointerdown", event => {
      if (event.button !== 0) return;
      const { bounds, top, height, scaleLeft } = plotMetrics();
      const localX = event.clientX - bounds.left;
      const onScale = localX >= scaleLeft;
      const onPlot = localX >= 12 && !onScale
        && event.clientY - bounds.top >= top
        && event.clientY - bounds.top <= top + height;
      if (!onScale && !onPlot) return;
      const anchorRatio = Math.min(1, Math.max(0,
        (event.clientY - bounds.top - top) / height));
      const range = kind === "rsi"
        ? chartView.indicators.rsiRange
        : macdBaseRange() * chartView.indicators.macdScale;
      const offset = kind === "rsi"
        ? chartView.indicators.rsiOffset
        : chartView.indicators.macdOffset;
      const maximum = kind === "rsi" ? 50 + offset + range / 2 : range + offset;
      const anchorValue = maximum - anchorRatio * (kind === "rsi" ? range : range * 2);
      drag = onScale
        ? {
          pointerId: event.pointerId,
          mode: "scale",
          startY: event.clientY,
          startRange: chartView.indicators.rsiRange,
          startScale: chartView.indicators.macdScale,
          baseRange: kind === "macd" ? macdBaseRange() : range,
          anchorRatio,
          anchorValue
        }
        : {
          pointerId: event.pointerId,
          mode: "pan",
          startY: event.clientY,
          startX: event.clientX,
          rightOffset: chartView.rightOffset,
          startOffset: offset,
          range,
          height
        };
      indicatorCanvas.classList.toggle("indicator-panning", !onScale);
      indicatorCanvas.setPointerCapture(event.pointerId);
      event.preventDefault();
    });
    const endDrag = event => {
      if (drag?.pointerId !== event.pointerId) return;
      if (indicatorCanvas.hasPointerCapture(event.pointerId))
        indicatorCanvas.releasePointerCapture(event.pointerId);
      drag = null;
      indicatorCanvas.classList.remove("indicator-scale-hover");
      indicatorCanvas.classList.remove("indicator-panning");
      saveTradePreferences();
    };
    indicatorCanvas.addEventListener("pointerup", endDrag);
    indicatorCanvas.addEventListener("pointercancel", endDrag);
    indicatorCanvas.addEventListener("pointerleave", () => {
      if (drag) return;
      chartView.indicatorHover = null;
      chartView.crosshair = null;
      indicatorCanvas.classList.remove("indicator-pan-hover");
      redrawChart();
    });
    indicatorCanvas.addEventListener("wheel", event => {
      const { bounds, top, height, scaleLeft } = plotMetrics();
      const horizontal = event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY);
      if (horizontal) {
        event.preventDefault();
        const step = Math.max(1, chartGeometry?.step ?? bounds.width / Math.max(1, chartView.barCount));
        chartView.rightOffset = Math.max(-Math.max(0, chartView.barCount - 12),
          chartView.rightOffset + Math.round((event.deltaX || event.deltaY) / step));
      } else {
        event.preventDefault();
        const factor = event.deltaY < 0 ? 0.8 : 1.25;
        const ratio = Math.min(1, Math.max(0,
          (event.clientY - bounds.top - top) / height));
        const baseRange = kind === "macd" ? macdBaseRange() : 1;
        const currentRange = kind === "rsi"
          ? chartView.indicators.rsiRange
          : baseRange * chartView.indicators.macdScale * 2;
        zoomIndicator(factor, ratio, currentRange, baseRange);
      }
      redrawChart();
      saveTradePreferences();
    }, { passive: false });
  };
  bindIndicatorScale(rsiCanvas, "rsi");
  bindIndicatorScale(macdCanvas, "macd");
  function redrawIndicator(kind) {
    if (!chartGeometry) return;
    const isRsi = kind === "rsi";
    if ((isRsi && indicatorOptions.rsi) || (!isRsi && indicatorOptions.macd))
      drawIndicatorPane(isRsi ? rsiCanvas : macdCanvas, latestCandles, latestIndicators, chartGeometry, chartView, kind);
  }
  root.querySelector("#chart-clear-drawings").addEventListener("click", () => {
    const symbol = currentSymbol().toLowerCase();
    for (let index = chartDrawings.length - 1; index >= 0; index--) {
      if (chartDrawings[index].symbol.toLowerCase() === symbol) chartDrawings.splice(index, 1);
    }
    saveTradePreferences();
    drawingAnchor = null;
    drawingEnd = null;
    drawingPreview = null;
    redrawChart();
  });
  let dragState = null;
  canvas.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    if (chartView.drawingMode === "line") {
      const point = chartPoint(event);
      if (!point) return;
      if (!drawingAnchor) {
        drawingAnchor = point;
      } else {
        chartDrawings.push({
          symbol: currentSymbol(),
          kind: "line",
          start: drawingAnchor,
          end: point
        });
        saveTradePreferences();
        drawingAnchor = null;
      }
      drawingEnd = null;
      drawingPreview = null;
      redrawChart();
      return;
    }
    if (chartView.drawingMode === "channel") {
      const point = chartPoint(event);
      if (!point) return;
      if (!drawingAnchor) {
        drawingAnchor = point;
        root.querySelector("#drawing-hint").textContent = "Channel: click the second point along the first edge.";
      } else if (!drawingEnd) {
        drawingEnd = point;
        root.querySelector("#drawing-hint").textContent = "Channel: click a third point to set the parallel edge spacing.";
      } else {
        const startTime = Date.parse(drawingAnchor.time);
        const endTime = Date.parse(drawingEnd.time);
        const offsetTime = Date.parse(point.time);
        const span = endTime - startTime;
        const ratio = span === 0 ? 0 : (offsetTime - startTime) / span;
        const centerPrice = drawingAnchor.price
          + (drawingEnd.price - drawingAnchor.price) * ratio;
        chartDrawings.push({
          symbol: currentSymbol(),
          kind: "channel",
          start: drawingAnchor,
          end: drawingEnd,
          offset: point.price - centerPrice
        });
        saveTradePreferences();
        drawingAnchor = null;
        drawingEnd = null;
        root.querySelector("#drawing-hint").textContent = "Parallel channel added. Choose Draw channel again to add another.";
      }
      drawingPreview = null;
      redrawChart();
      return;
    }
    const rect = canvas.getBoundingClientRect();
    const isPriceScaleDrag = event.clientX - rect.left >= rect.width - 78;
    const plotTop = chartGeometry?.top ?? 16;
    const plotHeight = Math.max(1, chartGeometry?.height ?? rect.height - 100);
    const anchorRatio = Math.min(1, Math.max(0, (event.clientY - rect.top - plotTop) / plotHeight));
    dragState = isPriceScaleDrag
      ? {
        pointerId: event.pointerId,
        kind: "price-scale",
        y: event.clientY,
        priceScale: chartView.priceScale,
        baseRange: (chartGeometry?.priceRange ?? 1) * chartView.priceScale,
        anchorRatio,
        anchorValue: (chartGeometry?.maxPrice ?? 0) - anchorRatio * (chartGeometry?.priceRange ?? 1),
        baseCenter: ((chartGeometry?.maxPrice ?? 0) + (chartGeometry?.minPrice ?? 0)) / 2 - chartView.priceOffset
      }
      : {
        pointerId: event.pointerId,
        kind: "pan",
        x: event.clientX,
        y: event.clientY,
        rightOffset: chartView.rightOffset,
        priceOffset: chartView.priceOffset,
        priceRange: chartGeometry?.priceRange ?? 1,
        plotHeight
      };
    chartView.crosshair = {
      x: event.clientX - rect.left,
      screenX: event.clientX,
      y: event.clientY - rect.top
    };
    canvas.setPointerCapture(event.pointerId);
    canvas.classList.toggle("chart-scaling", isPriceScaleDrag);
    canvas.classList.toggle("chart-dragging", !isPriceScaleDrag);
    redrawChart();
  });
  canvas.addEventListener("pointermove", event => {
    const rect = canvas.getBoundingClientRect();
    canvas.classList.toggle("chart-scale-hover", !dragState && event.clientX - rect.left >= rect.width - 78);
    if (dragState?.pointerId === event.pointerId) {
      if (dragState.kind === "price-scale") {
        const scale = Math.min(20, Math.max(0.05,
          dragState.priceScale * verticalScaleFactor(event.clientY - dragState.y, true)));
        const range = dragState.baseRange / scale;
        const center = dragState.anchorValue + (dragState.anchorRatio - 0.5) * range;
        chartView.priceScale = scale;
        chartView.priceOffset = center - dragState.baseCenter;
      } else {
        const step = Math.max(1, chartGeometry?.step ?? (rect.width - 90) / Math.max(1, chartView.barCount));
        chartView.rightOffset = Math.max(-Math.max(0, chartView.barCount - 12), dragState.rightOffset
          + Math.round((event.clientX - dragState.x) / step));
        chartView.priceOffset = panAxisCenter(dragState.priceOffset,
          event.clientY - dragState.y, dragState.priceRange, dragState.plotHeight);
      }
    }
    chartView.crosshair = {
      x: event.clientX - rect.left,
      screenX: event.clientX,
      y: event.clientY - rect.top
    };
    if (chartView.drawingMode === "line" && drawingAnchor) {
      drawingPreview = { kind: "line", start: drawingAnchor, end: chartPoint(event) };
    } else if (chartView.drawingMode === "channel" && drawingAnchor) {
      const point = chartPoint(event);
      if (!drawingEnd) {
        drawingPreview = { kind: "line", start: drawingAnchor, end: point };
      } else {
        const startTime = Date.parse(drawingAnchor.time);
        const endTime = Date.parse(drawingEnd.time);
        const offsetTime = Date.parse(point.time);
        const span = endTime - startTime;
        const ratio = span === 0 ? 0 : (offsetTime - startTime) / span;
        const centerPrice = drawingAnchor.price
          + (drawingEnd.price - drawingAnchor.price) * ratio;
        drawingPreview = {
          kind: "channel",
          start: drawingAnchor,
          end: drawingEnd,
          offset: point.price - centerPrice
        };
      }
    } else {
      drawingPreview = null;
    }
    redrawChart();
  });
  const finishChartDrag = event => {
    if (dragState?.pointerId !== event.pointerId) return;
    if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
    dragState = null;
    canvas.classList.remove("chart-dragging");
    canvas.classList.remove("chart-scaling");
    saveTradePreferences();
  };
  canvas.addEventListener("pointerup", finishChartDrag);
  canvas.addEventListener("pointercancel", finishChartDrag);
  canvas.addEventListener("pointerleave", event => {
    if (dragState) return;
    canvas.classList.remove("chart-scale-hover");
    chartView.crosshair = null;
    drawingPreview = null;
    redrawChart();
  });
  canvas.addEventListener("keydown", event => {
    if (event.key !== "Escape" || chartView.drawingMode === null) return;
    drawingAnchor = null;
    drawingEnd = null;
    drawingPreview = null;
    root.querySelector("#drawing-hint").textContent = `${chartView.drawingMode === "channel" ? "Channel" : "Trend line"} drawing cancelled.`;
    redrawChart();
  });
  canvas.addEventListener("wheel", event => {
    event.preventDefault();
    const rect = canvas.getBoundingClientRect();
    if (event.clientX - rect.left >= rect.width - 78) {
      const factor = event.deltaY < 0 ? 1.2 : 1 / 1.2;
      const geometry = chartGeometry;
      if (!geometry) return;
      const ratio = Math.min(1, Math.max(0,
        (event.clientY - rect.top - geometry.top) / geometry.height));
      const anchor = geometry.maxPrice - ratio * geometry.priceRange;
      const baseRange = geometry.priceRange * chartView.priceScale;
      const baseCenter = (geometry.maxPrice + geometry.minPrice) / 2 - chartView.priceOffset;
      const scale = Math.min(20, Math.max(0.05, chartView.priceScale * factor));
      const range = baseRange / scale;
      chartView.priceScale = scale;
      chartView.priceOffset = anchor + (ratio - 0.5) * range - baseCenter;
      saveTradePreferences();
      redrawChart();
      return;
    }
    if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
      chartView.rightOffset = Math.max(-Math.max(0, chartView.barCount - 12), chartView.rightOffset
        + Math.round((event.deltaX || event.deltaY) / 30));
      chartView.crosshair = null;
      saveTradePreferences();
      redrawChart();
      return;
    }
    const pointerRatio = Math.min(1, Math.max(0, (event.clientX - rect.left - 12) / Math.max(1, rect.width - 90)));
    zoomChart(event.deltaY < 0 ? 0.8 : 1.25, pointerRatio);
    saveTradePreferences();
  }, { passive: false });
  root.querySelector("#side-buy").addEventListener("click", () => { side = "Buy"; refreshSideState(); });
  root.querySelector("#side-sell").addEventListener("click", () => {
    if (!root.querySelector("#side-sell").disabled) { side = "Sell"; refreshSideState(); }
  });
  root.querySelector("#submit-paper-order").addEventListener("click", async () => {
    setMessage(orderMessage, "");
    const quantity = root.querySelector("#order-quantity").value.trim();
    if (!decimalPattern.test(quantity) || /^0(?:\.0+)?$/.test(quantity)) {
      setMessage(orderMessage, "Enter a positive quantity using a decimal point.", "error");
      return;
    }
    const stop = root.querySelector("#stop-loss").value.trim();
    const target = root.querySelector("#take-profit").value.trim();
    if ((stop && (!decimalPattern.test(stop) || /^0(?:\.0+)?$/.test(stop)))
      || (target && (!decimalPattern.test(target) || /^0(?:\.0+)?$/.test(target)))) {
      setMessage(orderMessage, "Stop and target prices must be positive decimal values.", "error");
      return;
    }
    if (matchingPosition() && (stop || target)) {
      setMessage(orderMessage, "Update exits separately before reducing an open position.", "error");
      return;
    }
    if (side === "Sell" && matchingPosition()?.direction !== "Long") {
      setMessage(orderMessage, "Spot Sell is available only to reduce an existing long position.", "error");
      return;
    }
    const symbol = currentSymbol();
    const body = `{"symbol":${JSON.stringify(symbol)},"side":${JSON.stringify(side)},"quantity":${quantity},"clientOrderId":${JSON.stringify(`workspace-${crypto.randomUUID()}`)},"stopLossPrice":${stop || "null"},"takeProfitPrice":${target || "null"}}`;
    try {
      const response = await api("/api/paper/orders", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body
      });
      let message = `Paper ${response.order.side} filled at ${formatNumber(response.order.fillPrice)}.`;
      if (response.position?.stopLossPrice || response.position?.takeProfitPrice)
        message += " Protective levels saved with the fill.";
      setMessage(orderMessage, message, "success");
      root.querySelector("#order-quantity").value = "";
      await refreshPaperPositions();
      const orders = await api("/api/orders?mode=Paper");
      paperOrders = orders.orders ?? [];
      root.querySelector("#recent-orders").innerHTML = ordersMarkup(paperOrders);
      await refreshChart();
    } catch (error) {
      setMessage(orderMessage, error.message, "error");
    }
  });
  root.querySelector("#start-scanner").addEventListener("click", async () => {
    setMessage(scannerMessage, "");
    try {
      await api("/api/paper-training", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: "{}"
      });
      setMessage(scannerMessage, "Paper scanning enabled. Await a completed scan and an experiment worker before expecting simulated trades.", "success");
      await refreshWorkers();
    } catch (error) {
      setMessage(scannerMessage, error.message, "error");
    }
  });
  root.querySelector("#stop-scanner").addEventListener("click", async () => {
    setMessage(scannerMessage, "");
    if (workers.some(worker => Number(worker.positionQuantity) > 0)
      && !window.confirm("Stop new paper entries? Open worker positions remain active and their protective exits still require the market-data and experiment hosts to keep running.")) {
      setMessage(scannerMessage, "Paper training remains active.");
      return;
    }
    try {
      await api("/api/paper-training/me/disable", { method: "POST" });
      setMessage(scannerMessage, "New paper admissions and entries stopped. Open positions keep protective monitoring while both worker hosts remain running.", "success");
      await refreshWorkers();
    } catch (error) {
      setMessage(scannerMessage, error.message, "error");
    }
  });
  root.querySelector("#save-position-exits").addEventListener("click", async () => {
    const position = matchingPosition();
    if (!position) return;
    const stop = root.querySelector("#stop-loss").value.trim();
    const target = root.querySelector("#take-profit").value.trim();
    if ((stop && (!decimalPattern.test(stop) || /^0(?:\.0+)?$/.test(stop)))
      || (target && (!decimalPattern.test(target) || /^0(?:\.0+)?$/.test(target)))) {
      setMessage(orderMessage, "Stop and target prices must be positive decimal values.", "error");
      return;
    }
    const body = `{"stopLossPrice":${stop || "null"},"takeProfitPrice":${target || "null"}}`;
    try {
      await api(`/api/paper/positions/${encodeURIComponent(position.id)}/exits`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body
      });
      setMessage(orderMessage, "Protective exits updated for the open paper position.", "success");
      await refreshPaperPositions();
      await refreshChart();
    } catch (error) {
      setMessage(orderMessage, error.message, "error");
    }
  });
  root.querySelector("#evaluate-paper-exits").addEventListener("click", async () => {
    try {
      const response = await api("/api/paper/exits/evaluate", { method: "POST" });
      setMessage(orderMessage, `Exit evaluator completed. Closed ${response.closed} paper position(s).`, "success");
      await refreshPaperPositions();
      await refreshPaperOrders();
      await refreshChart();
    } catch (error) {
      setMessage(orderMessage, error.message, "error");
    }
  });

  window.addEventListener("resize", onResize);
  window.addEventListener("pagehide", flushTradePreferences);
  const stateRefreshTimer = window.setInterval(async () => {
    try {
      await Promise.all([refreshWorkers(), refreshPaperPositions(), refreshPaperOrders()]);
      redrawChart();
    } catch (error) {
      setMessage(scannerMessage, `Could not refresh paper trading state: ${error.message}`, "error");
    }
  }, 10000);
  const tickerRenderTimer = window.setInterval(() => {
    if (pendingLiveTrade) {
      latestLiveTrade = pendingLiveTrade;
      pendingLiveTrade = null;
    }
    const feedAvailable = tickerEvents?.readyState === EventSource.OPEN;
    const guide = chartPriceGuide(latestLiveTrade, currentSymbol(), latestCandles, interval,
      Date.now(), feedAvailable, latestQuote);
    liveStatus.textContent = guide
      ? `${guide.stale ? guide.label : guide.label === "MID BBO" ? "Midquote" : "Last trade"} ${formatNumber(guide.price, 8)} · ${formatTime(guide.asOfUtc)}${guide.stale ? " · not live" : ""}${feedAvailable ? "" : " · trade feed disconnected"}${quoteError ? ` · ${quoteError}` : ""} · display only`
      : `No selected-pair price available · ${feedAvailable ? "waiting for public trades/quotes" : "trade feed disconnected"}${quoteError ? ` · ${quoteError}` : ""}`;
    redrawChart();
  }, 1000);
  const quoteRefreshTimer = window.setInterval(() => {
    void pollQuote(currentSymbol());
  }, 3000);
  const candleRefreshTimer = window.setInterval(async () => {
    if (document.hidden || chartRefreshPending || !currentSymbol()) return;
    chartRefreshPending = true;
    try {
      await refreshChart(true);
    } catch (error) {
      root.querySelector("#market-asof").textContent = `Candle refresh unavailable: ${error.message}`;
    } finally {
      chartRefreshPending = false;
    }
  }, 15000);
  root._workspaceDispose = () => {
    chartRequestId++;
    flushTradePreferences();
    window.removeEventListener("resize", onResize);
    window.removeEventListener("pagehide", flushTradePreferences);
    document.removeEventListener("pointerdown", closePairResults);
    window.clearInterval(stateRefreshTimer);
    window.clearInterval(tickerRenderTimer);
    window.clearInterval(quoteRefreshTimer);
    window.clearInterval(candleRefreshTimer);
    tickerEvents?.close();
    quoteRequest?.abort();
    orderBookActive = false;
    window.clearTimeout(orderBookReconnectTimer);
    if (orderBookRenderFrame) window.cancelAnimationFrame(orderBookRenderFrame);
    if (orderBookSocket) orderBookSocket.close();
  };
  await refreshChart();

  function renderPairResults(query) {
    const normalized = query.trim().toLocaleLowerCase();
    filteredPairs = currentPairs
      .filter(pair => !normalized
        || pair.symbol.toLocaleLowerCase().includes(normalized)
        || (pair.displayName ?? "").toLocaleLowerCase().includes(normalized));
    activePairIndex = filteredPairs.length ? 0 : -1;
    pairResultCount.textContent = normalized
      ? `${filteredPairs.length} matching active ${filteredPairs.length === 1 ? "pair" : "pairs"}`
      : `${filteredPairs.length} active ${filteredPairs.length === 1 ? "pair" : "pairs"}`;
    pairResultOptions.innerHTML = filteredPairs.length
      ? filteredPairs.map((pair, index) => `<button type="button" role="option"
          id="pair-option-${index}" aria-selected="${index === activePairIndex}"
          data-pair-symbol="${escapeHtml(pair.symbol)}">
          <strong>${escapeHtml(pair.displayName || pair.symbol)}</strong>
          <small>${escapeHtml(pair.symbol)}</small>
        </button>`).join("")
      : `<p class="pair-empty">No active pair matches that search.</p>`;
    pairResults.hidden = false;
    pairSearch.setAttribute("aria-expanded", "true");
    updateActivePairOption();
  }

  function updateActivePairOption() {
    pairResultOptions.querySelectorAll("[data-pair-symbol]").forEach((option, index) => {
      const active = index === activePairIndex;
      option.setAttribute("aria-selected", String(active));
      if (active) {
        pairSearch.setAttribute("aria-activedescendant", option.id);
        option.scrollIntoView({ block: "nearest" });
      }
    });
    if (activePairIndex < 0)
      pairSearch.removeAttribute("aria-activedescendant");
  }

  async function selectPair(pair, fromWorker = false) {
    if (!fromWorker) {
      selectedWorkerSlot = null;
      root.querySelector("#worker-detail").open = false;
      updateWorkerView();
    }
    selectedSymbol = pair.symbol;
    latestEvidence = null;
    latestCandles = [];
    latestIndicators = calculateIndicators([]);
    chartRequestId++;
    saveTradePreferences();
    connectOrderBook(selectedSymbol);
    connectTradeTicker(selectedSymbol);
    chartView.barCount = 120;
    chartView.rightOffset = 0;
    chartView.priceScale = 1;
    chartView.priceOffset = 0;
    chartView.indicators = { rsiRange: 120, rsiOffset: 0, macdScale: 1, macdOffset: 0 };
    chartView.crosshair = null;
    drawingAnchor = null;
    drawingPreview = null;
    drawingEnd = null;
    root.querySelector("#drawing-hint").textContent = chartView.drawingMode === "channel"
      ? "Channel drawing restarted for this market. Click two points along one edge, then the offset."
      : chartView.drawingMode === "line"
        ? "Trend-line drawing restarted for this market. Click two points."
        : "";
    pairSearch.value = pair.displayName || pair.symbol;
    pairResults.hidden = true;
    pairSearch.setAttribute("aria-expanded", "false");
    pairSearch.removeAttribute("aria-activedescendant");
    refreshSideState();
    await refreshChart();
  }

  async function refreshWorkers() {
    const [monitor, state] = await Promise.all([api("/api/experiments"), api("/api/paper-training")]);
    workers = monitor.workers ?? [];
    decisions = monitor.decisions ?? [];
    training = state;
    if (selectedWorkerSlot !== null && !workers.some(worker => worker.slot === selectedWorkerSlot))
      selectedWorkerSlot = null;
    updateWorkerView();
    redrawChart();
  }

  async function refreshPaperPositions() {
    const response = await api("/api/paper/positions");
    paperPositions = response.positions ?? [];
    refreshSideState();
  }

  async function refreshPaperOrders() {
    const response = await api("/api/orders?mode=Paper");
    paperOrders = response.orders ?? [];
    root.querySelector("#recent-orders").innerHTML = ordersMarkup(paperOrders);
  }

  function onResize() {
    if (latestCandles.length)
      redrawChart();
  }

  function zoomChart(factor, anchorRatio = 1) {
    if (!latestCandles.length) return;
    const oldCount = chartGeometry
      ? Math.round(chartGeometry.width / chartGeometry.step)
      : Math.max(12, chartView.barCount);
    const nextCount = zoomedBarCount(oldCount, factor, latestCandles.length,
      chartGeometry?.width ?? canvas.clientWidth - 90);
    if (nextCount === oldCount) return;
    const oldStart = latestCandles.length - chartView.rightOffset - oldCount;
    const anchorIndex = oldStart + Math.round(oldCount * anchorRatio);
    chartView.barCount = nextCount;
    chartView.rightOffset = Math.max(-Math.max(0, nextCount - 12), latestCandles.length - anchorIndex
      - Math.round(nextCount * (1 - anchorRatio)));
    chartView.crosshair = null;
    redrawChart();
  }
}

function renderEvidence(element, evidence) {
  if (!element) return;
  element.classList.remove("workspace-error");
  if (!evidence) {
    element.textContent = "No strategy evidence is available yet.";
    return;
  }
  if (evidence.isAvailable === false)
    element.classList.add("workspace-error");
  const pivots = (evidence.pivots ?? []).map(pivot =>
    `${formatTime(pivot.closeTimeUtc)}: ${formatNumber(pivot.price)} price / RSI ${formatNumber(pivot.rsi, 2)}`).join(" · ");
  const macd = evidence.macd
    ? `MACD ${formatNumber(evidence.macd.line, 6)} / signal ${formatNumber(evidence.macd.signal, 6)} / histogram ${formatNumber(evidence.macd.histogram, 6)}`
    : "MACD unavailable";
  element.innerHTML = `<strong>Three-swing ${escapeHtml(evidence.direction ?? "Neutral")}</strong>
    <span class="secondary-line">${escapeHtml(evidence.reason ?? "")}</span>
    <span class="secondary-line">5m channel ${formatNumber(evidence.fiveMinuteChannelPositionPercent, 2)}% · 1h ${formatNumber(evidence.oneHourChannelPositionPercent, 2)}% · 4h ${formatNumber(evidence.fourHourChannelPositionPercent, 2)}% · RSI 14 ${formatNumber(evidence.rsi, 2)}</span>
    <span class="secondary-line">${escapeHtml(macd)} · MACD confirmation ${evidence.hasMacdConfirmation ? "yes" : "no"} · reversal confirmation ${evidence.hasReversalConfirmation ? "yes" : "no"}</span>
    <span class="secondary-line">${escapeHtml(pivots || "No confirmed three-swing pivots.")}</span>
    <span class="secondary-line">Blue diamonds are recorded pre-risk worker decisions (not guaranteed fills); fill markers use recorded paper execution data. Hover a decision for its strategy reason.</span>`;
}

export function parseSavedStrategySettings(catalog, strategyId, json) {
  const saved = JSON.parse(json);
  const definition = catalog.find(strategy => strategy.strategyId === strategyId);
  if (!definition || !saved || typeof saved !== "object" || Array.isArray(saved)
      || Object.keys(saved).some(key => !(definition.settings ?? []).some(field => field.key === key)))
    throw new Error("Saved settings cannot be mapped to the approved editor.");
  if (strategyId === "platform.relative-strength-pullback-rotation"
      && Object.keys(saved).length) {
    if (!Object.hasOwn(saved, "relativeRankingModel"))
      saved.relativeRankingModel = "legacyRanks";
    if (!Object.hasOwn(saved, "relativePlanModel"))
      saved.relativePlanModel = "legacyAtrPlan";
  }
  if (strategyId === "platform.donchian-breakout-ensemble"
      && Object.keys(saved).length && !Object.hasOwn(saved, "donchianPlanModel"))
    saved.donchianPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.bollinger-mean-reversion"
      && Object.keys(saved).length && !Object.hasOwn(saved, "bollingerPlanModel"))
    saved.bollingerPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.rsi-pullback"
      && Object.keys(saved).length && !Object.hasOwn(saved, "rsiPlanModel"))
    saved.rsiPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.ema-trend-continuation"
      && Object.keys(saved).length && !Object.hasOwn(saved, "emaPlanModel"))
    saved.emaPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.volatility-compression-breakout"
      && Object.keys(saved).length && !Object.hasOwn(saved, "compressionPlanModel"))
    saved.compressionPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.macd-volume"
      && Object.keys(saved).length && !Object.hasOwn(saved, "macdPlanModel"))
    saved.macdPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.cross-sectional-momentum-rotation"
      && Object.keys(saved).length && !Object.hasOwn(saved, "momentumPlanModel"))
    saved.momentumPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.three-swing-channel-divergence"
      && Object.keys(saved).length && !Object.hasOwn(saved, "threeSwingPlanModel"))
    saved.threeSwingPlanModel = "legacyAtrPlan";
  if (strategyId === "platform.regime-switching-ensemble"
      && Object.keys(saved).length && !Object.hasOwn(saved, "regimePlanModel"))
    saved.regimePlanModel = "legacyAtrPlan";
  return saved;
}

export function invalidStrategySettings(catalog, settingsByStrategy) {
  const invalid = [];
  for (const strategy of catalog) {
    const values = settingsByStrategy[strategy.strategyId] ?? {};
    for (const field of strategy.settings ?? []) {
      const value = values[field.key];
      const numeric = field.type === "number" || field.type === "integer";
      if (numeric && (!Number.isFinite(value) || value < field.minimum || value > field.maximum
          || field.type === "integer" && !Number.isInteger(value)
          || field.step > 0 && Math.abs((value - field.minimum) / field.step
            - Math.round((value - field.minimum) / field.step)) > 1e-7)
        || field.type === "select" && !(field.options ?? []).includes(value)
        || field.type === "boolean" && typeof value !== "boolean") {
        invalid.push(`${strategy.name ?? strategy.strategyId}: ${field.label} needs review`);
      }
    }
  }
  return invalid;
}

export function savedWorkerStrategySettings(catalog, savedSettings = {}, assignments = []) {
  const defaults = Object.fromEntries(catalog.map(strategy => [
    strategy.strategyId,
    JSON.parse(strategy.defaultsJson || "{}")
  ]));
  const settingsByStrategy = Object.fromEntries(
    Object.entries(defaults).map(([strategyId, values]) => [strategyId, { ...values }]));
  const unreadableSettings = new Set();
  const matchedSettings = new Map();
  const load = (strategyId, json) => {
    try {
      const normalized = {
        ...defaults[strategyId],
        ...parseSavedStrategySettings(catalog, strategyId, json)
      };
      const earlier = matchedSettings.get(strategyId);
      if (earlier && (Object.keys(earlier).length !== Object.keys(normalized).length
          || Object.keys(normalized).some(key => normalized[key] !== earlier[key]))) {
        unreadableSettings.add(strategyId);
        return;
      }
      matchedSettings.set(strategyId, normalized);
      settingsByStrategy[strategyId] = normalized;
    } catch {
      unreadableSettings.add(strategyId);
    }
  };
  for (const [strategyId, json] of Object.entries(savedSettings))
    load(strategyId, json);
  for (const assignment of assignments)
    load(assignment.strategyId, assignment.strategyParameters || "{}");
  return { settingsByStrategy, unreadableSettings: [...unreadableSettings] };
}

function renderStrategyConfiguration(root, training) {
  const host = root.querySelector("#worker-config");
  if (!host) return;
  const assignments = training?.workerAssignments ?? [];
  const catalog = training?.catalog ?? [];
  if (assignments.length !== 10) {
    host.innerHTML = `<p class="ticket-note">Start the scanner once to create its ten persistent slots, then configure the approved strategy per slot.</p>
      <section class="strategy-parameters-note"><h2>Approved strategy rules</h2>
      <p>After the worker slots exist, edit bounded controls for every approved strategy. Both assignments and unused-strategy settings are saved to your account.</p></section>`;
    return;
  }
  const { settingsByStrategy, unreadableSettings } = savedWorkerStrategySettings(
    catalog, training.strategyParameters ?? {}, assignments);
  const reviewSettings = invalidStrategySettings(catalog, settingsByStrategy);
  const legacyRelativeSettings = settingsByStrategy["platform.relative-strength-pullback-rotation"]?.relativeRankingModel === "legacyRanks";
  const legacyRelativePlan = settingsByStrategy["platform.relative-strength-pullback-rotation"]?.relativePlanModel === "legacyAtrPlan";
  const legacyDonchianPlan = settingsByStrategy["platform.donchian-breakout-ensemble"]?.donchianPlanModel === "legacyAtrPlan";
  const legacyBollingerPlan = settingsByStrategy["platform.bollinger-mean-reversion"]?.bollingerPlanModel === "legacyAtrPlan";
  const legacyRsiPlan = settingsByStrategy["platform.rsi-pullback"]?.rsiPlanModel === "legacyAtrPlan";
  const legacyMacdPlan = settingsByStrategy["platform.macd-volume"]?.macdPlanModel === "legacyAtrPlan";
  const legacyEmaPlan = settingsByStrategy["platform.ema-trend-continuation"]?.emaPlanModel === "legacyAtrPlan";
  const legacyCompressionPlan = settingsByStrategy["platform.volatility-compression-breakout"]?.compressionPlanModel === "legacyAtrPlan";
  const legacyMomentumPlan = settingsByStrategy["platform.cross-sectional-momentum-rotation"]?.momentumPlanModel === "legacyAtrPlan";
  const legacyThreeSwingPlan = settingsByStrategy["platform.three-swing-channel-divergence"]?.threeSwingPlanModel === "legacyAtrPlan";
  const legacyRegimePlan = settingsByStrategy["platform.regime-switching-ensemble"]?.regimePlanModel === "legacyAtrPlan";
  const reviewNotices = [
    legacyRelativeSettings ? "Relative-strength settings use the historical duplicated ranking factors. Review that strategy and select Daily excess breadth to allow new paper admissions." : null,
    legacyRelativePlan ? "Relative-strength settings use the historical ATR-only stop. Review that strategy and select 4-hour structure to allow new paper admissions." : null,
    legacyDonchianPlan ? "Donchian settings use the historical ATR-only stop. Review that strategy and select prior break range to allow new paper admissions." : null,
    legacyBollingerPlan ? "Bollinger settings use the historical ATR-only stop. Review that strategy and select excursion mid-band to allow new paper admissions." : null,
    legacyRsiPlan ? "RSI pullback settings use the historical ATR-only stop. Review that strategy and select pullback swing to allow new paper admissions." : null,
    legacyMacdPlan ? "MACD settings use the historical ATR-only stop. Review that strategy and select pre-cross swing to allow new paper admissions." : null,
    legacyEmaPlan ? "EMA continuation settings use the historical ATR-only stop. Review that strategy and select pullback swing to allow new paper admissions." : null,
    legacyCompressionPlan ? "Compression breakout settings use the historical ATR-only stop. Review that strategy and select prior range to allow new paper admissions." : null,
    legacyMomentumPlan ? "Momentum rotation settings use the historical ATR-only stop. Review that strategy and select daily swing to allow new paper admissions." : null,
    legacyThreeSwingPlan ? "Three-swing settings use the historical ATR-only stop. Review that strategy and select confirmed pivot to allow new paper admissions." : null,
    legacyRegimePlan ? "Ensemble settings use the historical ATR-only stop. Review that strategy and select four-hour swing to allow new paper admissions." : null
  ].filter(Boolean);
  const settingsNotice = unreadableSettings.length
    ? `Saved strategy settings for ${unreadableSettings.join(", ")} cannot be read or disagree across saved assignments. No configuration will be overwritten; contact an administrator to review these settings.`
    : reviewSettings.length
      ? `${reviewSettings.join("; ")}. Update these fields before saving.`
      : reviewNotices.join(" ");
  host.innerHTML = `<div class="strategy-subtabs" role="tablist" aria-label="Worker strategy configuration">
      <button type="button" role="tab" aria-selected="true" aria-controls="strategy-assignment-panel" data-strategy-tab="assignments" class="active">Worker assignments</button>
      <button type="button" role="tab" aria-selected="false" aria-controls="strategy-editor-panel" data-strategy-tab="editor">Strategy rule editor</button>
    </div>
    <form id="worker-config-form">
    <p class="ticket-note">Save while scanning or while paper trades are open. Reserved workers keep their admitted strategy settings and protective behavior. Updated assignments/settings apply when a slot next becomes available.</p>
    <section id="strategy-assignment-panel" role="tabpanel" data-strategy-panel="assignments">
    <div class="settings-grid">${assignments.map(item => `<div class="worker-setting">
      <label for="worker-strategy-${item.slot}">Worker ${item.slot}</label>
      <select id="worker-strategy-${item.slot}" data-worker-slot="${item.slot}">
        ${catalog.map(strategy => `<option value="${escapeHtml(strategy.strategyId)}" ${strategy.strategyId === item.strategyId ? "selected" : ""}>${escapeHtml(strategy.strategyId)}</option>`).join("")}
      </select>
      <small class="workspace-muted strategy-description">${escapeHtml(strategyDescription[item.strategyId] ?? "Approved strategy; inspect its recorded decision evidence for the exact gates.")}</small>
      <small class="workspace-muted">${escapeHtml(item.state)} · ${escapeHtml(item.mode)} · rule v${escapeHtml(item.strategyVersion ?? catalog.find(strategy => strategy.strategyId === item.strategyId)?.strategyVersion ?? "unknown")}</small>
      ${item.state === "Reserved" ? `<small class="workspace-muted">Current worker: ${escapeHtml(item.currentStrategyId ?? item.strategyId)} · admitted rule v${escapeHtml(item.currentStrategyVersion ?? "unknown")}. Selection above applies after this slot becomes available.</small>` : ""}
      ${item.admissionCloseUtc ? `<small class="workspace-muted">${item.currentStrategyId === "platform.relative-strength-pullback-rotation" && item.currentStrategyVersion >= 5 ? "Confirmed 1h close" : "Admitted signal close"}: ${escapeHtml(formatTime(item.admissionCloseUtc))}</small>` : ""}
      ${item.selectedComponent ? `<small class="workspace-muted">Selected ${escapeHtml(item.selectedComponent.familyId)} v${escapeHtml(item.selectedComponent.version)} · ${escapeHtml(item.selectedComponent.universeSize)} ranked markets · decision ${escapeHtml((item.selectedComponent.componentDecisionFingerprint ?? "").slice(0, 12))} · ${escapeHtml(formatTime(item.selectedComponent.signalAsOfUtc))}</small>` : ""}
    </div>`).join("")}</div>
    </section>
    <section id="strategy-editor-panel" role="tabpanel" data-strategy-panel="editor" hidden>
      <label for="strategy-editor-select">Approved strategy</label>
      <select id="strategy-editor-select">
        ${catalog.map(strategy => `<option value="${escapeHtml(strategy.strategyId)}">${escapeHtml(strategy.name ?? strategy.strategyId)}</option>`).join("")}
      </select>
      <p id="strategy-editor-description" class="workspace-muted"></p>
      <p class="workspace-muted">Each control explains what it measures and how changing it affects future paper setups. Periods count closed candles on the stated timeframe, not minutes unless the signal interval is one minute. Limits and other mandatory checks still apply; these are not profit predictions.</p>
      <div id="strategy-editor-fields" class="strategy-editor-fields"></div>
    </section>
    <section class="strategy-parameters-note" aria-labelledby="strategy-parameters-title">
      <h2 id="strategy-parameters-title">Execution and safety boundaries</h2>
      <p>Only bounded settings for approved strategy templates are editable. Platform risk ceilings, paper-only execution, exchange filters, mandatory stale-data checks, idempotency, and protective-exit controls cannot be weakened here.</p>
    </section>
    <button type="submit" class="primary" ${unreadableSettings.length ? "disabled" : ""}>Save worker assignments</button>
    <p id="worker-config-message" class="form-message ${settingsNotice ? "error" : ""}" aria-live="polite">${escapeHtml(settingsNotice)}</p>
  </form>`;
  const assignmentsPanel = host.querySelector("#strategy-assignment-panel");
  const editorPanel = host.querySelector("#strategy-editor-panel");
  const editorSelect = host.querySelector("#strategy-editor-select");
  const editorDescription = host.querySelector("#strategy-editor-description");
  const editorFields = host.querySelector("#strategy-editor-fields");
  let selectedEditorStrategy = editorSelect.value;
  const captureEditor = () => {
    const values = { ...(settingsByStrategy[selectedEditorStrategy] ?? {}) };
    for (const field of catalog.find(item => item.strategyId === selectedEditorStrategy)?.settings ?? []) {
      const input = editorFields.querySelector(`[data-setting-key="${field.key}"]`);
      if (!input) continue;
      values[field.key] = field.type === "boolean"
        ? input.checked
        : field.type === "select"
          ? input.value
          : Number(input.value);
    }
    settingsByStrategy[selectedEditorStrategy] = values;
  };
  const renderEditor = () => {
    const strategy = catalog.find(item => item.strategyId === editorSelect.value);
    if (!strategy) return;
    selectedEditorStrategy = strategy.strategyId;
    const values = settingsByStrategy[strategy.strategyId] ?? JSON.parse(strategy.defaultsJson || "{}");
    settingsByStrategy[strategy.strategyId] = values;
    editorDescription.textContent = `New paper admissions use rule v${strategy.strategyVersion ?? "unknown"}. ${
      strategyDescription[strategy.strategyId] ?? strategy.description ?? ""}`;
    editorFields.innerHTML = (strategy.settings ?? []).map(field => {
      const value = values[field.key] ?? field.defaultNumber ?? field.defaultText;
      const help = strategySettingHelpMarkup(field, strategy);
      if (field.type === "select")
        return `<label class="strategy-field">${escapeHtml(field.label)}<select data-setting-key="${escapeHtml(field.key)}">${
          (field.options ?? []).map(option => `<option value="${escapeHtml(option)}" ${option === value ? "selected" : ""}>${escapeHtml(strategyOptionLabels[option] ?? option)}</option>`).join("")
        }</select>${help}</label>`;
      if (field.type === "boolean")
        return `<label class="strategy-field strategy-field-toggle"><span><input type="checkbox" data-setting-key="${escapeHtml(field.key)}" ${value ? "checked" : ""} /> ${escapeHtml(field.label)}</span>${help}</label>`;
      return `<label class="strategy-field">${escapeHtml(field.label)}<input type="number" data-setting-key="${escapeHtml(field.key)}" min="${field.minimum}" max="${field.maximum}" step="${field.step}" value="${value}" />${help}<small>Approved range: ${field.minimum}–${field.maximum}</small></label>`;
    }).join("");
  };
  renderEditor();
  editorSelect.addEventListener("change", () => {
    captureEditor();
    renderEditor();
  });
  editorFields.addEventListener("input", captureEditor);
  editorFields.addEventListener("change", captureEditor);
  host.querySelectorAll("[data-strategy-tab]").forEach(button => {
    button.addEventListener("click", () => {
      captureEditor();
      const showEditor = button.dataset.strategyTab === "editor";
      assignmentsPanel.hidden = showEditor;
      editorPanel.hidden = !showEditor;
      host.querySelectorAll("[data-strategy-tab]").forEach(tab => {
        const selected = tab === button;
        tab.classList.toggle("active", selected);
        tab.setAttribute("aria-selected", String(selected));
      });
    });
  });
  host.querySelector("#worker-config-form").addEventListener("submit", async event => {
    event.preventDefault();
    const message = host.querySelector("#worker-config-message");
    if (unreadableSettings.length) {
      setMessage(message, settingsNotice, "error");
      return;
    }
    captureEditor();
    const needsReview = invalidStrategySettings(catalog, settingsByStrategy);
    if (needsReview.length) {
      setMessage(message, `${needsReview.join("; ")}. Update these fields before saving.`, "error");
      return;
    }
    const selected = [...host.querySelectorAll("[data-worker-slot]")].map(select => ({
      slot: Number.parseInt(select.dataset.workerSlot, 10),
      strategyId: select.value,
      strategyParametersJson: JSON.stringify(settingsByStrategy[select.value]
        ?? JSON.parse(catalog.find(item => item.strategyId === select.value)?.defaultsJson ?? "{}"))
    }));
    try {
      await api("/api/paper-training/strategies", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          assignments: selected,
          strategyParameters: Object.fromEntries(Object.entries(settingsByStrategy)
            .map(([strategyId, settings]) => [strategyId, JSON.stringify(settings)]))
        })
      });
      setMessage(message, "Worker assignments and strategy settings saved.", "success");
    } catch (error) {
      setMessage(message, error.message, "error");
    }
  });
  host.querySelectorAll("[data-worker-slot]").forEach(select => {
    select.addEventListener("change", () => {
      const strategy = catalog.find(item => item.strategyId === select.value);
      const setting = select.closest(".worker-setting").querySelector(".strategy-description");
      if (setting)
        setting.textContent = strategyDescription[strategy?.strategyId] ?? strategy?.description ?? "Approved strategy; inspect its recorded decision evidence for the exact gates.";
    });
  });
}

async function renderStrategies(root) {
  root.innerHTML = `${heading("Worker strategies", "Assign approved strategies and configure their bounded decision rules")}
    <section class="workspace-panel">
      <div class="workspace-panel-header"><h2>Persistent worker assignments</h2><a href="/workspace/overview">Overview</a></div>
      <div id="worker-config" class="workspace-panel-body"><p class="workspace-muted">Loading strategy configuration…</p></div>
    </section>`;
  const training = await api("/api/paper-training");
  renderStrategyConfiguration(root, training);
}

function ordersMarkup(orders, limit = 6) {
  if (!orders.length) return `<p class="workspace-muted">No paper orders recorded.</p>`;
  return `<div class="workspace-table-wrap"><table class="workspace-table">
    <thead><tr><th>Market</th><th>Side</th><th>Status</th><th>Filled / quantity</th><th>Time</th></tr></thead>
    <tbody>${orders.slice(0, limit).map(order => `<tr>
      <td>${escapeHtml(order.symbol)}</td><td>${escapeHtml(order.side)}</td>
      <td>${escapeHtml(order.state)}</td>
      <td>${formatNumber(order.filledQuantity)} / ${formatNumber(order.quantity)}</td>
      <td>${formatTime(order.lastTransitionAtUtc ?? order.createdAtUtc)}</td>
    </tr>`).join("")}</tbody></table></div>`;
}

export function overviewMetricsMarkup(summary) {
    const unrealized = summary.unrealizedProfitAndLoss === null
      ? "Unpriced"
      : formatSignedNumber(summary.unrealizedProfitAndLoss);
    const equity = summary.equity === null ? "Unavailable" : formatNumber(summary.equity);
    return [
      metric("Paper equity", equity, summary.hasUnpricedExposure ? "At least one open position is stale or unpriced" : `As of ${formatTime(summary.asOfUtc)}`),
      metric("Realized P&L", formatSignedNumber(summary.realizedProfitAndLoss), "Closed paper-trade result", pnlClass(summary.realizedProfitAndLoss)),
      metric("Unrealized P&L", unrealized, summary.hasUnpricedExposure ? "Unavailable until all open positions are priced" : "Open paper positions", pnlClass(summary.unrealizedProfitAndLoss)),
      metric("Worker slots", `${summary.reserved} reserved / ${summary.scanning} scanning`, `${summary.workerCount} configured paper slots`)
    ].join("");
}

async function renderOverview(root) {
  root.innerHTML = `${heading("Overview", "Paper-only worker performance and scanner health")}
    <div id="overview-metrics" class="metric-grid"></div>
    <div class="notice"><strong>Separate ledgers.</strong> These figures are paper worker results only. They never include a Kraken account balance, live Spot order, or Futures position.</div>
    <section class="workspace-panel">
      <div class="workspace-panel-header"><h2>Worker pool</h2><a href="/workspace/trade">Open Trade</a></div>
      <div id="overview-workers" class="workspace-panel-body worker-list"></div>
    </section>
    <section class="workspace-panel" style="margin-top:0.8rem">
      <div class="workspace-panel-header"><h2>Scanner configuration</h2><span id="scanner-state" class="pill">Loading</span></div>
      <div class="workspace-panel-body"><p id="training-notice" class="workspace-muted"></p>
        <a href="/workspace/strategies">Configure approved worker strategies</a> · <a href="/workspace/trade">Open Trade</a>
      </div>
    </section>`;
  const [summary, monitor, training] = await Promise.all([
    api("/api/workspace/overview"), api("/api/experiments"), api("/api/paper-training")
  ]);
  root.querySelector("#overview-metrics").innerHTML = overviewMetricsMarkup(summary);
  root.querySelector("#overview-workers").innerHTML = workersMarkup(monitor.workers ?? []);
  root.querySelector("#scanner-state").textContent = training.state;
  root.querySelector("#training-notice").textContent = training.notice ?? "";
}

async function renderPortfolio(root) {
  root.innerHTML = `${heading("Portfolio", "Exchange balances and simulated positions shown in separate ledgers", "READ ONLY")}
    <div class="portfolio-ledger-note"><span class="pill live-disabled">Live · read only</span><span>Kraken balances</span><span class="ledger-separator">/</span><span class="pill paper">Paper · fake funds</span><span>Simulated positions</span></div>
    <div class="portfolio-overview">
      <section class="workspace-panel portfolio-section">
        <div class="workspace-panel-header portfolio-section-header">
          <div><h2>Exchange balances</h2><p>Live account data · no trading or transfers from this view</p></div>
          <a href="/workspace/accounts">Manage accounts</a>
        </div>
        <div id="exchange-balances" class="portfolio-section-body"></div>
      </section>
      <section class="workspace-panel portfolio-section paper-portfolio-section">
        <div class="workspace-panel-header portfolio-section-header">
          <div><h2>Paper positions</h2><p>Simulated Spot exposure · never combined with exchange balances</p></div>
          <span id="paper-position-count" class="pill paper">Loading</span>
        </div>
        <div id="paper-positions" class="portfolio-section-body"></div>
      </section>
    </div>`;
  const [portfolio, positions] = await Promise.all([
    api("/api/portfolio"), api("/api/paper/positions")
  ]);
  const balances = root.querySelector("#exchange-balances");
  const accounts = portfolio.accounts ?? [];
  balances.innerHTML = accounts.length ? accounts.map(account => {
    const accountBalances = account.balances ?? [];
    return `<article class="portfolio-account">
      <div class="portfolio-account-heading">
        <div><h3>${escapeHtml(account.displayName)}</h3><span>${escapeHtml(account.exchange)} account</span></div>
        <div class="portfolio-account-retrieved"><span>Retrieved</span><strong>${formatTime(account.retrievedAtUtc)}</strong></div>
      </div>
      ${account.error
        ? `<p class="portfolio-error">${escapeHtml(account.error)}</p>`
        : accountBalances.length
          ? `<div class="workspace-table-wrap portfolio-table-wrap"><table class="workspace-table portfolio-table">
              <thead><tr><th>Asset</th><th>Total balance</th><th>Available to trade</th><th>Spot trade hold</th></tr></thead>
              <tbody>${accountBalances.map(balance => `<tr>
                <td><strong class="portfolio-asset">${escapeHtml(balance.asset)}</strong></td>
                <td class="portfolio-number">${formatNumber(balance.total, 8)}</td>
                <td class="portfolio-number">${formatNumber(balance.available, 8)}</td>
                <td class="portfolio-number">${formatNumber(balance.held, 8)}</td>
              </tr>`).join("")}</tbody>
            </table></div><p class="portfolio-action-note">Trade holds reflect spot non-margin orders only; other asset restrictions may apply. Check Kraken before placing an order.</p>`
          : `<p class="portfolio-empty-inline">No non-zero balances were returned for this account.</p>`}
    </article>`;
  }).join("") : `<div class="portfolio-empty">
    <strong>No exchange account connected</strong>
    <p>Connect a Kraken account to view its read-only balances here. Paper positions remain separate.</p>
    <a class="portfolio-action" href="/workspace/accounts">Connect account</a>
  </div>`;

  const paperPositions = positions.positions ?? [];
  root.querySelector("#paper-position-count").textContent =
    `${paperPositions.length} open ${paperPositions.length === 1 ? "position" : "positions"}`;
  root.querySelector("#paper-positions").innerHTML = paperPositions.length
    ? `<div class="workspace-table-wrap portfolio-table-wrap"><table class="workspace-table portfolio-table paper-position-table">
        <thead><tr><th>Market</th><th>Direction</th><th>Quantity</th><th>Average entry</th><th>Latest mark</th><th>Unrealized P&amp;L</th><th>Stop loss</th><th>Take profit</th><th>Price status</th></tr></thead>
        <tbody>${paperPositions.map(position => `<tr>
          <td><strong class="portfolio-market">${escapeHtml(position.symbol)}</strong><span class="secondary-line">Paper Spot</span></td>
          <td><span class="portfolio-direction">${escapeHtml(position.direction)}</span></td>
          <td class="portfolio-number">${formatNumber(position.quantity, 8)}</td>
          <td class="portfolio-number">${formatNumber(position.entryPrice, 8)}</td>
          <td class="portfolio-number">${formatNumber(position.markPrice, 8)}</td>
          <td class="portfolio-number">${formatNumber(position.unrealisedPnl, 8)}</td>
          <td class="portfolio-number">${formatNumber(position.stopLossPrice, 8)}</td>
          <td class="portfolio-number">${formatNumber(position.takeProfitPrice, 8)}</td>
          <td><span class="portfolio-price-status ${position.priceIsStale ? "stale" : "fresh"}">${position.priceIsStale ? "Stale" : "Current"}</span>
            <span class="secondary-line">${formatTime(position.pricedAtUtc)}</span></td>
        </tr>`).join("")}</tbody></table></div>`
    : `<div class="portfolio-empty paper-empty">
        <strong>No open paper positions</strong>
        <p>Positions opened by automatic paper workers or the manual paper ticket will appear here.</p>
        <a class="portfolio-action" href="/workspace/trade">Open Trade</a>
      </div>`;
}

async function renderHistory(root) {
  root.innerHTML = `${heading("History", "Persisted orders, closed paper trades, and research snapshots", "PAPER")}
    <section class="workspace-panel"><div class="workspace-panel-header"><h2>Paper orders</h2><span class="pill paper">No live orders</span></div><div id="history-orders" class="workspace-panel-body"></div></section>
    <section class="workspace-panel" style="margin-top:0.8rem"><div class="workspace-panel-header"><h2>Recent recorded worker fills</h2><span class="pill paper">Up to 10 per active slot</span></div><div id="history-worker-fills" class="workspace-panel-body"></div></section>
    <section class="workspace-panel" style="margin-top:0.8rem"><div class="workspace-panel-header"><h2>Closed worker trades</h2></div><div id="closed-trades" class="workspace-panel-body"></div></section>
    <section class="workspace-panel" style="margin-top:0.8rem">
      <div class="workspace-panel-header"><h2>Paper transaction report</h2><span class="pill paper">Fake funds only</span></div>
      <form id="paper-report-form" class="workspace-panel-body">
        <div class="ticket-field"><label for="report-from">From UTC (inclusive)</label><input id="report-from" type="datetime-local" required /></div>
        <div class="ticket-field"><label for="report-to">To UTC (exclusive)</label><input id="report-to" type="datetime-local" required /></div>
        <div class="ticket-field"><label for="report-country">Optional presentation format</label>
          <select id="report-country"><option value="">General (native currencies)</option>
            <option value="GB">GB date notation example (not a tax return)</option></select></div>
        <button class="primary" type="submit" value="view">Generate paper report</button>
        <button type="submit" value="save">Generate &amp; save private JSON</button>
        <p id="paper-report-message" class="form-message" aria-live="polite"></p>
      </form>
      <div id="paper-report-results" class="workspace-panel-body"></div>
      <div id="paper-report-exports" class="workspace-panel-body" aria-live="polite"></div>
    </section>
    <section class="workspace-panel" style="margin-top:0.8rem"><div class="workspace-panel-header"><h2>Research snapshots</h2><span class="pill">Research only</span></div><div id="research-results" class="workspace-panel-body"></div></section>`;
  const [orders, results] = await Promise.all([
    api("/api/orders?mode=Paper"), api("/api/experiment-results?page=0&pageSize=25")
  ]);
  root.querySelector("#history-orders").innerHTML = ordersMarkup(orders.orders ?? [], Number.MAX_SAFE_INTEGER);
  root.querySelector("#closed-trades").innerHTML = closedTradesMarkup(results.closedTrades ?? []);
  root.querySelector("#research-results").innerHTML = researchMarkup(results.results ?? []);
  try {
    const monitor = await api("/api/experiments");
    root.querySelector("#history-worker-fills").innerHTML = recentWorkerFillsMarkup(monitor.workers ?? []);
  } catch (error) {
    root.querySelector("#history-worker-fills").innerHTML =
      `<p class="workspace-error">Recent worker fills unavailable: ${escapeHtml(error.message)}</p>`;
  }
  const now = new Date();
  root.querySelector("#report-from").value = new Date(now.getTime() - 30 * 86400000).toISOString().slice(0, 16);
  root.querySelector("#report-to").value = now.toISOString().slice(0, 16);
  root.querySelector("#paper-report-form").addEventListener("submit", async event => {
    event.preventDefault();
    const save = event.submitter?.value === "save";
    const message = root.querySelector("#paper-report-message");
    const output = root.querySelector("#paper-report-results");
    try {
      const fromUtc = new Date(`${root.querySelector("#report-from").value}Z`).toISOString();
      const toUtcExclusive = new Date(`${root.querySelector("#report-to").value}Z`).toISOString();
      const countryProfileCode = root.querySelector("#report-country").value || null;
      const report = await api("/api/reports/paper-transactions", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ fromUtc, toUtcExclusive, export: save, countryProfileCode })
      });
      output.innerHTML = paperTransactionReportMarkup(report);
      setMessage(message, `${report.transactions.length} simulated transactions reported.${report.exportId ? " Private JSON saved." : ""}`, "success");
      if (report.exportId) await loadPaperReportExports(root);
    } catch (error) {
      output.replaceChildren();
      setMessage(message, error.message, "error");
    }
  });
  await loadPaperReportExports(root);
}

async function loadPaperReportExports(root) {
  const element = root.querySelector("#paper-report-exports");
  try {
    const exports = await api("/api/reports/paper-transactions/exports");
    element.innerHTML = paperReportExportsMarkup(exports);
  } catch (error) {
    element.innerHTML = `<p class="workspace-error">Saved paper reports unavailable: ${escapeHtml(error.message)}</p>`;
  }
}

export function paperReportExportsMarkup(exports) {
  return `<h3>Saved private JSON exports</h3>${exports.length
    ? exports.map(item => `<p><a href="/api/reports/paper-transactions/exports/${encodeURIComponent(item.id)}" download>
        Download ${escapeHtml(item.fromUtc)} to ${escapeHtml(item.toUtcExclusive)} UTC (exclusive)
      </a> · ${escapeHtml(item.reportingCurrency)}${item.countryProfileCode ? ` · ${escapeHtml(item.countryProfileCode)} example` : ""} · saved ${escapeHtml(formatTime(item.occurredAtUtc))}</p>`).join("")
    : `<p class="workspace-muted">No saved paper reports yet.</p>`}`;
}

export function recentWorkerFillsMarkup(workers) {
  const fills = workers.flatMap(worker => (worker.recentTrades ?? [])
    .map(fill => ({ slot: worker.slot, ...fill })))
    .sort((left, right) => Date.parse(right.occurredAtUtc) - Date.parse(left.occurredAtUtc));
  if (!fills.length) return `<p class="workspace-muted">No recent simulated worker fills in the currently assigned slots. An order or signal is not an execution.</p>`;
  return `<p class="workspace-muted">These are recorded simulated fill prices, not order limits or strategy targets. This is a recent view, not the complete historical ledger.</p>
    <div class="workspace-table-wrap"><table class="workspace-table">
      <thead><tr><th>Time</th><th>Worker</th><th>Market</th><th>Side</th><th>Filled quantity</th><th>Execution price</th><th>Fee</th></tr></thead>
      <tbody>${fills.map(fill => `<tr>
        <td>${formatTime(fill.occurredAtUtc)}</td><td>${escapeHtml(fill.slot)}</td><td>${escapeHtml(fill.symbol)}</td>
        <td>${escapeHtml(fill.direction)}</td><td>${formatNumber(fill.quantity, 8)}</td>
        <td>${formatNumber(fill.executionPrice, 8)}</td><td>${formatNumber(fill.fee, 8)}</td>
      </tr>`).join("")}</tbody>
    </table></div>`;
}

export function paperTransactionReportMarkup(report) {
  const totals = report.currencyTotals.map(item =>
    `<span>${escapeHtml(item.currency)} realized P&amp;L <strong class="${pnlClass(item.realizedProfitAndLoss)}">${formatSignedExact(item.realizedProfitAndLoss)}</strong></span>`).join(" · ");
  const summary = report.totalInReportingCurrency === null
    ? "No reporting-currency total: native quote currencies differ. No conversion was made."
    : `${escapeHtml(report.reportingCurrency)} realized P&amp;L <strong class="${pnlClass(report.totalInReportingCurrency)}">${formatSignedExact(report.totalInReportingCurrency)}</strong>`;
  const displayZone = reportingTimeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone;
  const country = report.countryProfile;
  const countryRows = country?.transactions?.map(item => `<tr>
    <td>${escapeHtml(item.executedAtLocal)}</td>
    <td>${escapeHtml(item.symbol)}</td><td>${escapeHtml(item.direction)}</td>
    <td>${escapeHtml(item.quantity)}</td><td>${escapeHtml(item.price)}</td>
    <td>${escapeHtml(item.fee)}</td><td>${escapeHtml(item.cashChange)}</td>
    <td class="${pnlClass(item.realizedProfitAndLoss)}">${formatSignedExact(item.realizedProfitAndLoss)}</td>
    <td>${escapeHtml(item.quoteCurrency)}</td></tr>`).join("");
  const rows = report.transactions.map(item => `<tr>
    <td>${escapeHtml(formatTime(item.executedAtUtc))}</td>
    <td>${escapeHtml(item.workerId)}<span class="secondary-line">Fill ${escapeHtml(item.fillId)}</span></td>
    <td>${escapeHtml(item.symbol)}</td>
    <td>${escapeHtml(item.direction)}</td>
    <td>${escapeHtml(item.quantity)}</td>
    <td>${escapeHtml(item.price)} ${escapeHtml(item.quoteCurrency)}</td>
    <td>${escapeHtml(item.fee)} ${escapeHtml(item.quoteCurrency)}</td>
    <td>${escapeHtml(item.cashChange)} ${escapeHtml(item.quoteCurrency)}</td>
    <td class="${pnlClass(item.realizedProfitAndLoss)}">${item.realizedProfitAndLoss === null ? "—" : `${formatSignedExact(item.realizedProfitAndLoss)} ${escapeHtml(item.quoteCurrency)}`}</td>
  </tr>`).join("");
  return `<p class="workspace-muted">${escapeHtml(report.disclaimer)}</p>
    <p class="workspace-muted">UTC interval: ${escapeHtml(report.fromUtc)} to ${escapeHtml(report.toUtcExclusive)} (exclusive). Times below use ${escapeHtml(displayZone)}.</p>
    <p><strong>${summary}</strong>${totals ? ` · ${totals}` : ""}</p>
    ${rows ? `<div class="workspace-table-wrap"><table class="workspace-table">
      <thead><tr><th>Executed (${escapeHtml(displayZone)})</th><th>Worker / fill ID</th><th>Pair</th><th>Side</th><th>Quantity</th><th>Price</th><th>Fee</th><th>Cash change</th><th>Realized P&amp;L</th></tr></thead>
      <tbody>${rows}</tbody></table></div>`
    : `<p class="workspace-muted">No simulated worker fills in this UTC interval.</p>`}
    ${country ? `<h3>${escapeHtml(country.title)}</h3><p class="workspace-muted">${escapeHtml(country.disclaimer)} Dates use ${escapeHtml(country.timeZone)}.</p>
      ${countryRows ? `<div class="workspace-table-wrap"><table class="workspace-table">
        <thead><tr><th>Local date</th><th>Pair</th><th>Side</th><th>Quantity</th><th>Price</th><th>Fee</th><th>Cash change</th><th>Realized P&amp;L</th><th>Native quote</th></tr></thead>
        <tbody>${countryRows}</tbody></table></div>` : `<p>No country-format rows in this UTC interval.</p>`}`
      : ""}`;
}

export function closedTradesMarkup(items) {
  if (!items.length) return `<p class="workspace-muted">No closed worker trades are available.</p>`;
  return `<p class="workspace-muted">Prices are quantity-weighted simulated fills. BUY fill excludes fees; cost basis includes BUY fees. Results include all recorded fees.</p>
    <div class="workspace-table-wrap"><table class="workspace-table closed-trades-table"><thead><tr>
      <th>Worker</th><th>Strategy</th><th>Market</th><th>BUY fill avg</th><th>SELL fill avg</th>
      <th>Cost basis incl. BUY fees</th><th>Net P&amp;L</th><th>Opened</th><th>Closed</th>
    </tr></thead><tbody>
    ${items.map(item => `<tr><td>${escapeHtml(item.workerId)}</td><td>${escapeHtml(item.strategyId)}</td><td>${escapeHtml(item.symbol)}</td>
      <td>${formatNumber(item.averageBuyFillPrice, 8)}</td><td>${formatNumber(item.averageExitPrice, 8)}</td>
      <td>${formatNumber(item.averageEntryPrice, 8)}</td>
      <td class="${pnlClass(item.netProfitAndLoss)}">${formatSignedNumber(item.netProfitAndLoss)}</td>
      <td>${formatTime(item.openedAtUtc)}</td><td>${formatTime(item.closedAtUtc)}</td></tr>`).join("")}
  </tbody></table></div>`;
}

export function researchMarkup(items) {
  if (!items.length) return `<p class="workspace-muted">No persisted research snapshots are available.</p>`;
  return `<div class="workspace-table-wrap"><table class="workspace-table"><thead><tr><th>Strategy</th><th>Group</th><th>Equity</th><th>Realized / unrealized</th><th>Drawdown</th><th>Evaluated</th></tr></thead><tbody>
    ${items.map(item => `<tr><td>${escapeHtml(item.provenance?.strategyId ?? item.strategyId ?? "—")}</td><td>${escapeHtml(item.provenance?.group ?? item.group ?? "—")}</td>
      <td>${formatNumber(item.equity)}</td><td><span class="${pnlClass(item.realizedProfitAndLoss)}">${formatSignedNumber(item.realizedProfitAndLoss)}</span> / <span class="${pnlClass(item.unrealizedProfitAndLoss)}">${formatSignedNumber(item.unrealizedProfitAndLoss)}</span></td>
      <td>${formatNumber(item.maximumDrawdown)}%</td><td>${formatTime(item.evaluatedAtUtc)}</td></tr>`).join("")}
  </tbody></table></div>`;
}

export function adminPaperEntitlementsMarkup(plans, owner = null, ownerSearch = null, viewerId = null) {
  const assignment = owner?.assignment;
  const expired = assignment?.trialExpiresAtUtc
    && Date.parse(assignment.trialExpiresAtUtc) <= Date.now();
  const selected = owner ? `<p><strong>${escapeHtml(owner.email)}</strong> · ${escapeHtml(owner.role)} · ${escapeHtml(owner.status)}
    ${assignment
      ? ` · ${escapeHtml(assignment.code)} (${escapeHtml(assignment.maxExperimentWorkers)} workers)${assignment.trialExpiresAtUtc
        ? ` · trial ${expired ? "expired" : "ends"} ${escapeHtml(formatTime(assignment.trialExpiresAtUtc))}${expired ? " · new paper entries blocked until renewed" : ""}`
        : " · no trial expiry"}`
      : " · no active plan"}</p>`
    : `<p class="workspace-muted">Look up an account by exact email to manage its paper plan.</p>`;
  return `<section class="workspace-panel" style="margin-top:0.8rem">
    <div class="workspace-panel-header"><h2>Paper plans and owner assignments</h2></div>
    <div class="workspace-panel-body">
      <p class="workspace-muted">Administrator actions require a fresh, unused six-digit authenticator code. Codes are never saved in the browser. Plans here never enable live or futures trading.</p>
      <form id="admin-plan-create" class="settings-grid">
        <div class="ticket-field"><label for="admin-plan-code">Plan code</label><input id="admin-plan-code" required maxlength="40" pattern="[A-Z0-9-]+" autocomplete="off" /></div>
        <div class="ticket-field"><label for="admin-plan-name">Plan name</label><input id="admin-plan-name" required maxlength="120" autocomplete="off" /></div>
        <div class="ticket-field"><label for="admin-plan-workers">Maximum paper workers (0–10)</label><input id="admin-plan-workers" type="number" min="0" max="10" step="1" required /></div>
        <div class="ticket-field"><label for="admin-plan-code-mfa">Fresh verification code</label><input id="admin-plan-code-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="primary" type="submit">Create paper plan</button>
      </form>
      <form id="admin-owner-lookup" class="settings-grid">
        <div class="ticket-field"><label for="admin-owner-email">Account email</label><input id="admin-owner-email" type="email" maxlength="256" required autocomplete="off" /></div>
        <button type="submit">Find owner</button>
      </form>
      <form id="admin-owner-search" class="settings-grid">
        <div class="ticket-field"><label for="admin-owner-search-query">Search account emails (prefix, at least 3 characters)</label><input id="admin-owner-search-query" type="search" minlength="3" maxlength="256" required autocomplete="off" /></div>
        <button type="submit">Search owners</button>
      </form>
      ${ownerSearch ? `<div class="workspace-table-wrap"><table class="workspace-table"><thead><tr><th>Owner</th><th>Role</th><th>Status</th></tr></thead><tbody>
        ${ownerSearch.items.map(item => `<tr><td><button type="button" data-admin-owner-email="${escapeHtml(item.email)}">${escapeHtml(item.email)}</button></td><td>${escapeHtml(item.role)}</td><td>${escapeHtml(item.status)}</td></tr>`).join("")}
        </tbody></table></div>
        <div class="workspace-panel-actions"><button type="button" data-owner-search-page="${ownerSearch.page - 1}" ${ownerSearch.page ? "" : "disabled"}>Previous</button>
        <span>Page ${ownerSearch.page + 1}</span>
        <button type="button" data-owner-search-page="${ownerSearch.page + 1}" ${ownerSearch.hasMore ? "" : "disabled"}>Next</button></div>` : ""}
      <div id="admin-owner-current">${selected}</div>
      ${owner?.role === "User" ? `<form id="admin-owner-status" class="settings-grid">
        <div class="ticket-field"><label for="admin-owner-reason">Reason to ${owner.status === "Suspended" ? "reactivate" : "suspend"} this owner</label><input id="admin-owner-reason" required maxlength="200" autocomplete="off" /></div>
        <div class="ticket-field"><label for="admin-owner-status-mfa">Fresh verification code</label><input id="admin-owner-status-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="${owner.status === "Suspended" ? "primary" : "danger"}" type="submit">${owner.status === "Suspended" ? "Reactivate" : "Suspend"} owner</button>
      </form>` : ""}
      ${owner?.status === "Active" && ["User", "RiskOfficer"].includes(owner.role)
        ? `<form id="admin-owner-role" class="settings-grid">
        <p class="workspace-muted">Risk Officers can halt trading and manage paper protection. This does not grant administrator access.</p>
        <div class="ticket-field"><label for="admin-owner-role-reason">Reason to ${owner.role === "User" ? "grant" : "remove"} Risk Officer access</label><input id="admin-owner-role-reason" required maxlength="200" autocomplete="off" /></div>
        <div class="ticket-field"><label for="admin-owner-role-mfa">Fresh verification code</label><input id="admin-owner-role-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="${owner.role === "User" ? "danger" : "primary"}" type="submit">${owner.role === "User" ? "Grant" : "Remove"} Risk Officer access</button>
      </form>` : ""}
      ${owner?.role === "User" && owner.status === "Active"
        ? `<form id="admin-owner-administrator" class="settings-grid">
        <p class="workspace-muted">Administrator access requires the user to verify their separately provisioned MFA secret from their own signed-in account within five minutes. This invalidates their current session.</p>
        <div class="ticket-field"><label for="admin-owner-administrator-reason">Reason for administrator access</label><input id="admin-owner-administrator-reason" required maxlength="200" autocomplete="off" /></div>
        <div class="ticket-field"><label for="admin-owner-administrator-mfa">Fresh administrator verification code</label><input id="admin-owner-administrator-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="danger" type="submit">Approve Administrator access</button>
      </form>` : ""}
      ${owner?.role === "Administrator" && owner.id !== viewerId
        ? `<form id="admin-owner-administrator-revoke" class="settings-grid">
        <p class="workspace-muted">Removing Administrator access immediately invalidates the current session. Arrange separate Key Vault secret revocation.</p>
        <div class="ticket-field"><label for="admin-owner-administrator-revoke-reason">Reason to remove Administrator access</label><input id="admin-owner-administrator-revoke-reason" required maxlength="200" autocomplete="off" /></div>
        <div class="ticket-field"><label for="admin-owner-administrator-revoke-mfa">Fresh administrator verification code</label><input id="admin-owner-administrator-revoke-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="danger" type="submit">Remove Administrator access</button>
      </form>` : ""}
      <form id="admin-plan-assign" class="settings-grid">
        <div class="ticket-field"><label for="admin-plan-selection">Approved paper plan</label><select id="admin-plan-selection" required>
          ${plans.map(plan => `<option value="${escapeHtml(plan.id)}">${escapeHtml(plan.code)} · ${escapeHtml(plan.maxExperimentWorkers)} workers</option>`).join("")}
        </select></div>
        <div class="ticket-field"><label for="admin-trial-expiry">Trial expiry (optional, UTC)</label><input id="admin-trial-expiry" type="datetime-local" /></div>
        <div class="ticket-field"><label for="admin-assign-mfa">Fresh verification code</label><input id="admin-assign-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="primary" type="submit" ${owner?.role === "User" && owner.status === "Active" && !assignment && plans.length ? "" : "disabled"}>Assign plan</button>
      </form>
      ${assignment?.trialExpiresAtUtc && owner?.status === "Active"
        && !assignment.liveTradingEligible && !assignment.futuresEligible ? `<form id="admin-trial-extend" class="settings-grid">
        <div class="ticket-field"><label for="admin-trial-extend-expiry">Extend trial to (UTC)</label><input id="admin-trial-extend-expiry" type="datetime-local" required /></div>
        <div class="ticket-field"><label for="admin-trial-extend-mfa">Fresh verification code</label><input id="admin-trial-extend-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="primary" type="submit">Extend paper trial</button>
      </form>` : ""}
      ${assignment ? `<form id="admin-plan-revoke" class="settings-grid">
        <div class="ticket-field"><label for="admin-revoke-mfa">Fresh verification code to revoke</label><input id="admin-revoke-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="danger" type="submit">Revoke assignment</button>
      </form>` : ""}
      <p id="admin-entitlement-message" class="form-message" aria-live="polite"></p>
    </div>
  </section>`;
}

async function renderAdminPaperEntitlements(root, viewerId) {
  const region = root.querySelector("#admin-paper-entitlements");
  let plans = await api("/api/admin/entitlements/plans");
  let owner = null;
  let ownerSearch = null;
  const searchOwners = async (query, page) => {
    const result = await api(`/api/admin/entitlements/owners/search?query=${encodeURIComponent(query)}&page=${page}`);
    ownerSearch = { ...result, query };
    show();
    region.querySelector("#admin-owner-search-query").value = query;
  };
  const show = () => {
    region.innerHTML = adminPaperEntitlementsMarkup(plans, owner, ownerSearch, viewerId);
    const message = region.querySelector("#admin-entitlement-message");
    if (ownerSearch) region.querySelector("#admin-owner-search-query").value = ownerSearch.query;
    region.querySelector("#admin-owner-search").addEventListener("submit", async event => {
      event.preventDefault();
      const query = region.querySelector("#admin-owner-search-query").value.trim();
      try { await searchOwners(query, 0); }
      catch (error) { setMessage(message, error.message, "error"); }
    });
    region.querySelectorAll("[data-owner-search-page]").forEach(button =>
      button.addEventListener("click", async () => {
        try { await searchOwners(ownerSearch.query, Number(button.dataset.ownerSearchPage)); }
        catch (error) { setMessage(message, error.message, "error"); }
      }));
    region.querySelectorAll("[data-admin-owner-email]").forEach(button =>
      button.addEventListener("click", async () => {
        try {
          owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(button.dataset.adminOwnerEmail)}`);
          show();
          region.querySelector("#admin-owner-email").value = owner.email;
        } catch (error) { setMessage(message, error.message, "error"); }
      }));
    region.querySelector("#admin-owner-lookup").addEventListener("submit", async event => {
      event.preventDefault();
      const email = region.querySelector("#admin-owner-email").value.trim();
      try {
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
      } catch (error) {
        owner = null;
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"), error.message, "error");
      }
    });
    region.querySelector("#admin-owner-status")?.addEventListener("submit", async event => {
      event.preventDefault();
      if (!owner || !window.confirm(`${owner.status === "Suspended" ? "Reactivate" : "Suspend"} ${owner.email}?`)) return;
      const mfa = region.querySelector("#admin-owner-status-mfa");
      const request = {
        status: owner.status === "Suspended" ? "Active" : "Suspended",
        reason: region.querySelector("#admin-owner-reason").value.trim(),
        oneTimeCode: mfa.value
      };
      mfa.value = "";
      try {
        await api(`/api/admin/entitlements/owners/${encodeURIComponent(owner.id)}/status`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        const email = owner.email;
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"),
          `Owner is now ${owner.status}. Existing paper positions still require protection and monitoring.`, "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { request.oneTimeCode = ""; }
    });
    region.querySelector("#admin-owner-role")?.addEventListener("submit", async event => {
      event.preventDefault();
      if (!owner || !window.confirm(`${owner.role === "User" ? "Grant" : "Remove"} Risk Officer access for ${owner.email}?`)) return;
      const mfa = region.querySelector("#admin-owner-role-mfa");
      const request = {
        role: owner.role === "User" ? "RiskOfficer" : "User",
        reason: region.querySelector("#admin-owner-role-reason").value.trim(),
        oneTimeCode: mfa.value
      };
      mfa.value = "";
      try {
        await api(`/api/admin/entitlements/owners/${encodeURIComponent(owner.id)}/role`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        const email = owner.email;
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"),
          `${owner.email} now has the ${owner.role} role. Existing sessions must sign in again.`, "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { request.oneTimeCode = ""; }
    });
    region.querySelector("#admin-owner-administrator")?.addEventListener("submit", async event => {
      event.preventDefault();
      if (!owner || !window.confirm(`Grant Administrator access to ${owner.email}? The user must have just proved their own MFA secret.`)) return;
      const mfa = region.querySelector("#admin-owner-administrator-mfa");
      const request = {
        reason: region.querySelector("#admin-owner-administrator-reason").value.trim(),
        oneTimeCode: mfa.value
      };
      mfa.value = "";
      try {
        await api(`/api/admin/entitlements/owners/${encodeURIComponent(owner.id)}/administrator`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        const email = owner.email;
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"),
          `${owner.email} must sign in again with administrator MFA.`, "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { request.oneTimeCode = ""; }
    });
    region.querySelector("#admin-owner-administrator-revoke")?.addEventListener("submit", async event => {
      event.preventDefault();
      if (!owner || !window.confirm(`Remove Administrator access for ${owner.email}?`)) return;
      const mfa = region.querySelector("#admin-owner-administrator-revoke-mfa");
      const request = {
        reason: region.querySelector("#admin-owner-administrator-revoke-reason").value.trim(),
        oneTimeCode: mfa.value
      };
      mfa.value = "";
      try {
        await api(`/api/admin/entitlements/owners/${encodeURIComponent(owner.id)}/administrator/revoke`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        const email = owner.email;
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"),
          `${owner.email} no longer has Administrator access; revoke their Key Vault MFA secret separately.`, "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { request.oneTimeCode = ""; }
    });
    region.querySelector("#admin-plan-create").addEventListener("submit", async event => {
      event.preventDefault();
      const mfa = region.querySelector("#admin-plan-code-mfa");
      const request = {
        code: region.querySelector("#admin-plan-code").value.trim(),
        name: region.querySelector("#admin-plan-name").value.trim(),
        maxExperimentWorkers: Number(region.querySelector("#admin-plan-workers").value),
        oneTimeCode: mfa.value
      };
      mfa.value = "";
      try {
        await api("/api/admin/entitlements/plans", {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        plans = await api("/api/admin/entitlements/plans");
        show();
        setMessage(region.querySelector("#admin-entitlement-message"), "Paper plan created.", "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { request.oneTimeCode = ""; }
    });
    region.querySelector("#admin-plan-assign").addEventListener("submit", async event => {
      event.preventDefault();
      if (!owner || owner.assignment) return;
      const mfa = region.querySelector("#admin-assign-mfa");
      const expiry = region.querySelector("#admin-trial-expiry").value;
      const oneTimeCode = mfa.value;
      mfa.value = "";
      let request;
      try {
        request = {
          planId: region.querySelector("#admin-plan-selection").value,
          trialExpiresAtUtc: expiry ? new Date(`${expiry}Z`).toISOString() : null,
          oneTimeCode
        };
        await api(`/api/admin/entitlements/owners/${encodeURIComponent(owner.id)}/assign`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        const email = owner.email;
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"), "Paper plan assigned.", "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { if (request) request.oneTimeCode = ""; }
    });
    region.querySelector("#admin-plan-revoke")?.addEventListener("submit", async event => {
      event.preventDefault();
      if (!owner?.assignment || !window.confirm(`Revoke the paper plan for ${owner.email}?`)) return;
      const mfa = region.querySelector("#admin-revoke-mfa");
      const request = { oneTimeCode: mfa.value };
      mfa.value = "";
      try {
        await api(`/api/admin/entitlements/owners/${encodeURIComponent(owner.id)}/revoke/${encodeURIComponent(owner.assignment.id)}`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        const email = owner.email;
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"), "Paper plan revoked.", "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { request.oneTimeCode = ""; }
    });
    region.querySelector("#admin-trial-extend")?.addEventListener("submit", async event => {
      event.preventDefault();
      if (!owner?.assignment?.trialExpiresAtUtc || owner.status !== "Active") return;
      const mfa = region.querySelector("#admin-trial-extend-mfa");
      const oneTimeCode = mfa.value;
      mfa.value = "";
      let request;
      try {
        request = {
          trialExpiresAtUtc: new Date(`${region.querySelector("#admin-trial-extend-expiry").value}Z`).toISOString(),
          oneTimeCode
        };
        await api(`/api/admin/entitlements/owners/${encodeURIComponent(owner.id)}/trials/${encodeURIComponent(owner.assignment.id)}/extend`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
        });
        const email = owner.email;
        owner = await api(`/api/admin/entitlements/owners?email=${encodeURIComponent(email)}`);
        show();
        region.querySelector("#admin-owner-email").value = email;
        setMessage(region.querySelector("#admin-entitlement-message"), "Paper trial extended.", "success");
      } catch (error) { setMessage(message, error.message, "error"); }
      finally { if (request) request.oneTimeCode = ""; }
    });
  };
  show();
}

export function adminInvitationsMarkup(invitations, page = 0, hasMore = false) {
  return `<section class="workspace-panel" style="margin-top:0.8rem">
    <div class="workspace-panel-header"><h2>Invitations</h2></div>
    <div class="workspace-panel-body">
      <p class="workspace-muted">Codes are single-use, email-bound invitations valid for 30 days. Share the issued code privately with the intended recipient. Only the issuance response displays it; the list and audit never include it.</p>
      <form id="admin-invitation-create" class="settings-grid">
        <div class="ticket-field"><label for="admin-invite-email">Recipient email</label><input id="admin-invite-email" type="email" maxlength="256" required autocomplete="off" /></div>
        <div class="ticket-field"><label for="admin-invite-mfa">Fresh verification code</label><input id="admin-invite-mfa" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button class="primary" type="submit">Issue invitation</button>
      </form>
      <p id="admin-invite-issued" class="form-message" aria-live="polite"></p>
      <div class="workspace-table-wrap"><table class="workspace-table"><thead><tr>
        <th>Recipient</th><th>Expires</th><th>State</th><th>Action</th>
      </tr></thead><tbody>${invitations.map(invitation => `<tr>
        <td>${escapeHtml(invitation.recipientEmail || "Unbound (reissue)")}</td>
        <td>${escapeHtml(formatTime(invitation.expiresAtUtc))}</td>
        <td>${!invitation.recipientEmail ? "Unbound (reissue)"
          : invitation.isActive && invitation.usedCount < invitation.maxUses
          && Date.parse(invitation.expiresAtUtc) > Date.now() ? "Unused" : "Used, expired or revoked"}</td>
        <td>${invitation.isActive && invitation.usedCount < invitation.maxUses
          && Date.parse(invitation.expiresAtUtc) > Date.now()
          ? `<form data-invite-revoke="${escapeHtml(invitation.id)}">
            <label>Fresh verification code <input inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></label>
            <button class="danger" type="submit">Revoke</button></form>` : "—"}</td>
      </tr>`).join("")}</tbody></table></div>
      <div class="workspace-panel-actions"><button type="button" data-invite-page="${page - 1}" ${page ? "" : "disabled"}>Previous</button>
        <span>Page ${page + 1}</span>
        <button type="button" data-invite-page="${page + 1}" ${hasMore ? "" : "disabled"}>Next</button></div>
      <p id="admin-invite-message" class="form-message" aria-live="polite"></p>
    </div>
  </section>`;
}

export function ownerPaperEntitlementMarkup(entitlement) {
  const plan = entitlement.planCode ? escapeHtml(entitlement.planCode) : "No active plan assignment";
  const trial = entitlement.trialExpiresAtUtc
    ? `${entitlement.trialExpired ? "Trial expired" : "Trial ends"} ${escapeHtml(formatTime(entitlement.trialExpiresAtUtc))}`
    : "No trial expiry";
  return `<strong>${plan}</strong>
    <p>${entitlement.paperWorkerLimitsEnabled
      ? `Plan limits enabled · ${escapeHtml(entitlement.effectivePaperWorkerCapacity)} new paper workers allowed. ${trial}.`
      : `Plan limits not yet enabled · active owners retain the legacy ${escapeHtml(entitlement.effectivePaperWorkerCapacity)}-worker paper ceiling. ${trial}.`}</p>
    <p class="workspace-muted">An eligible plan does not enable real orders or futures. Open paper positions retain independent protective-exit monitoring if an assignment expires.</p>`;
}

async function renderAdminInvitations(root) {
  const region = root.querySelector("#admin-invitations");
  let invitations = await api("/api/admin/invitations?page=0");
  const show = () => {
    region.innerHTML = adminInvitationsMarkup(invitations.items, invitations.page, invitations.hasMore);
    region.querySelectorAll("[data-invite-page]").forEach(button =>
      button.addEventListener("click", async () => {
        try {
          invitations = await api(`/api/admin/invitations?page=${button.dataset.invitePage}`);
          show();
        } catch (error) {
          setMessage(region.querySelector("#admin-invite-message"), error.message, "error");
        }
      }));
    region.querySelector("#admin-invitation-create").addEventListener("submit", async event => {
      event.preventDefault();
      const email = region.querySelector("#admin-invite-email");
      const mfa = region.querySelector("#admin-invite-mfa");
      const request = { email: email.value.trim(), oneTimeCode: mfa.value };
      mfa.value = "";
      try {
        const invitation = await api("/api/invitations", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(request)
        });
        invitations = await api("/api/admin/invitations?page=0");
        show();
        const issued = region.querySelector("#admin-invite-issued");
        issued.textContent = `Share this single-use invitation code privately: ${invitation.code}`;
        issued.classList.add("success");
        region.querySelector("#admin-invite-email").value = email.value;
      } catch (error) {
        setMessage(region.querySelector("#admin-invite-message"), error.message, "error");
      } finally { request.oneTimeCode = ""; }
    });
    region.querySelectorAll("[data-invite-revoke]").forEach(form => form.addEventListener("submit", async event => {
      event.preventDefault();
      if (!window.confirm("Revoke this unused invitation?")) return;
      const code = form.querySelector("input");
      const request = { oneTimeCode: code.value };
      code.value = "";
      try {
        await api(`/api/admin/invitations/${encodeURIComponent(form.dataset.inviteRevoke)}/revoke`, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(request)
        });
        invitations = await api(`/api/admin/invitations?page=${invitations.page}`);
        if (!invitations.items.length && invitations.page > 0)
          invitations = await api(`/api/admin/invitations?page=${invitations.page - 1}`);
        show();
        setMessage(region.querySelector("#admin-invite-message"), "Invitation revoked.", "success");
      } catch (error) {
        setMessage(region.querySelector("#admin-invite-message"), error.message, "error");
      } finally { request.oneTimeCode = ""; }
    }));
  };
  show();
}

async function renderAccounts(root) {
  const me = await api("/api/me");
  root.innerHTML = `${heading("Exchange accounts", "Connect a Kraken account for read access and paper-trading eligibility", "LIVE EXECUTION OFF")}
    <div class="notice"><strong>Never enable withdrawals.</strong> Only submit API keys with the minimum required permissions. The API secret is sent once to the server-side secret store and is never returned to this page or saved in browser storage.</div>
    <section class="workspace-panel"><div class="workspace-panel-header"><h2>Connect Kraken API credentials</h2></div>
      <form id="connect-account" class="workspace-panel-body">
        <div class="ticket-field"><label for="account-name">Account label</label><input id="account-name" required maxlength="80" autocomplete="off" /></div>
        <div class="ticket-field"><label for="api-key">API key</label><input id="api-key" type="password" required autocomplete="new-password" /></div>
        <div class="ticket-field"><label for="api-secret">API secret</label><input id="api-secret" type="password" required autocomplete="new-password" /></div>
        <p class="account-secret-warning">Credentials are validated before activation. Withdrawals are not supported. Live Spot and Futures remain disabled by the deployment gate.</p>
        <button class="primary" type="submit">Validate and connect</button>
        <p id="account-message" class="form-message" aria-live="polite"></p>
      </form></section>
    <section class="workspace-panel" style="margin-top:0.8rem"><div class="workspace-panel-header"><h2>Connected accounts</h2></div><div id="account-list" class="workspace-panel-body"></div></section>
    <section class="workspace-panel" style="margin-top:0.8rem">
      <div class="workspace-panel-header"><h2>Your paper worker capacity</h2></div>
      <div id="owner-entitlement" class="workspace-panel-body" aria-live="polite"></div>
    </section>
    ${me.role === "User" ? `<section class="workspace-panel" style="margin-top:0.8rem">
      <div class="workspace-panel-header"><h2>Administrator MFA verification</h2></div>
      <form id="prove-administrator-mfa" class="workspace-panel-body">
        <p class="workspace-muted">Only if an administrator has arranged separate MFA provisioning for you: verify your own authenticator code here before they approve your role. This does not grant access by itself. Do not share the code or secret with the administrator.</p>
        <div class="ticket-field"><label for="prove-administrator-code">Fresh authenticator code</label><input id="prove-administrator-code" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" required autocomplete="one-time-code" /></div>
        <button type="submit">Verify my MFA</button>
        <p id="prove-administrator-message" class="form-message" aria-live="polite"></p>
      </form>
    </section>` : ""}
    <section class="workspace-panel" style="margin-top:0.8rem">
      <div class="workspace-panel-header"><h2>Display and reporting preferences</h2></div>
      <form id="reporting-profile" class="workspace-panel-body">
        <div class="ticket-field"><label for="reporting-locale">Locale for dates and numbers</label><input id="reporting-locale" required maxlength="16" placeholder="en-US" autocomplete="language" /></div>
        <div class="ticket-field"><label for="reporting-timezone">IANA time zone</label><input id="reporting-timezone" required maxlength="64" placeholder="Europe/London" autocomplete="off" /></div>
        <div class="ticket-field"><label for="reporting-currency">Reporting currency (ISO code)</label><input id="reporting-currency" required maxlength="3" pattern="[A-Z]{3}" placeholder="USD" autocomplete="off" /></div>
        <p class="workspace-muted">Locale and time zone format workspace values; interface text remains English. Currency is saved for future informational reports; it does not convert, merge, or revalue Spot or paper balances.</p>
        <button class="primary" type="submit">Save preferences</button>
        <p id="reporting-message" class="form-message" aria-live="polite"></p>
      </form>
    </section>${me.isAdministrator ? `<div id="admin-paper-entitlements"></div><div id="admin-invitations"></div>` : ""}`;
  const accountMessage = root.querySelector("#account-message");
  const list = root.querySelector("#account-list");
  const refresh = async () => {
    const accounts = await api("/api/exchange/accounts");
    list.innerHTML = accounts.length ? `<div class="workspace-table-wrap"><table class="workspace-table"><thead><tr><th>Label</th><th>Exchange</th><th>Status</th><th>Stage</th><th>Permissions</th><th>Action</th></tr></thead><tbody>
      ${accounts.map(account => `<tr><td>${escapeHtml(account.displayName)}</td><td>${escapeHtml(account.exchange)}</td><td>${escapeHtml(account.status)}</td><td>${escapeHtml(account.stage)}</td>
        <td>${account.canTrade ? "Validated for trading" : "Not trade-enabled"}</td><td><button type="button" class="danger" data-delete-account="${escapeHtml(account.id)}">Disconnect</button></td></tr>`).join("")}
    </tbody></table></div>` : `<p class="workspace-muted">No exchange account is connected.</p>`;
    list.querySelectorAll("[data-delete-account]").forEach(button => button.addEventListener("click", async () => {
      if (!window.confirm("Disconnect this account?")) return;
      try {
        await api(`/api/exchange/accounts/${encodeURIComponent(button.dataset.deleteAccount)}`, { method: "DELETE" });
        await refresh();
        root.querySelector("#prove-administrator-mfa")?.addEventListener("submit", async event => {
          event.preventDefault();
          const code = root.querySelector("#prove-administrator-code");
          const request = { oneTimeCode: code.value };
          code.value = "";
          try {
            await api("/api/account/administrator-mfa/prove", {
              method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
            });
            setMessage(root.querySelector("#prove-administrator-message"),
              "MFA ownership proved. An existing administrator must approve your role within five minutes.", "success");
          } catch (error) {
            setMessage(root.querySelector("#prove-administrator-message"), error.message, "error");
          } finally { request.oneTimeCode = ""; }
        });
        setMessage(accountMessage, "Account disconnected.", "success");
      } catch (error) { setMessage(accountMessage, error.message, "error"); }
    }));
  };
  await refresh();
  try {
    const entitlement = await api("/api/entitlements/me");
    root.querySelector("#owner-entitlement").innerHTML = ownerPaperEntitlementMarkup(entitlement);
  } catch (error) {
    root.querySelector("#owner-entitlement").textContent = `Paper worker capacity unavailable: ${error.message}`;
  }
  const profileMessage = root.querySelector("#reporting-message");
  try {
    const profile = await api("/api/reporting/profile");
    root.querySelector("#reporting-locale").value = profile.locale;
    root.querySelector("#reporting-timezone").value = profile.timeZone;
    root.querySelector("#reporting-currency").value = profile.reportingCurrency;
  } catch (error) {
    setMessage(profileMessage, error.message, "error");
  }
  root.querySelector("#reporting-profile").addEventListener("submit", async event => {
    event.preventDefault();
    const request = {
      locale: root.querySelector("#reporting-locale").value.trim(),
      timeZone: root.querySelector("#reporting-timezone").value.trim(),
      reportingCurrency: root.querySelector("#reporting-currency").value.trim()
    };
    try {
      const profile = await api("/api/reporting/profile", {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
      });
      reportingLocale = profile.locale;
      reportingTimeZone = profile.timeZone;
      setMessage(profileMessage, "Preferences saved. Times and numbers now use your selected settings.", "success");
    } catch (error) {
      setMessage(profileMessage, error.message, "error");
    }
  });
  root.querySelector("#connect-account").addEventListener("submit", async event => {
    event.preventDefault();
    const name = root.querySelector("#account-name");
    const key = root.querySelector("#api-key");
    const secret = root.querySelector("#api-secret");
    const request = {
      displayName: name.value.trim(),
      apiKey: key.value,
      apiSecret: secret.value
    };
    key.value = "";
    secret.value = "";
    setMessage(accountMessage, "Validating credentials…");
    try {
      await api("/api/exchange/accounts", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
      });
      setMessage(accountMessage, "Kraken account validated and connected. Secret fields were cleared.", "success");
      await refresh();
    } catch (error) {
      setMessage(accountMessage, error.message, "error");
    } finally {
      request.apiKey = "";
      request.apiSecret = "";
      name.value = "";
      key.value = "";
      secret.value = "";
    }
  });
  if (me.isAdministrator)
  {
    await renderAdminPaperEntitlements(root, me.id);
    await renderAdminInvitations(root);
  }
}

export async function mount(root, section) {
  root._workspaceDispose?.();
  const normalized = String(section ?? "overview").toLowerCase();
  try {
    let profileError = null;
    try {
      const profile = await api("/api/reporting/profile");
      reportingLocale = profile.locale;
      reportingTimeZone = profile.timeZone;
    } catch (error) {
      reportingLocale = undefined;
      reportingTimeZone = undefined;
      profileError = error.message;
    }
    if (normalized === "trade") await renderTrade(root);
    else if (normalized === "portfolio") await renderPortfolio(root);
    else if (normalized === "history") await renderHistory(root);
    else if (normalized === "strategies") await renderStrategies(root);
    else if (normalized === "accounts") await renderAccounts(root);
    else await renderOverview(root);
    if (profileError)
      root.insertAdjacentHTML("afterbegin", `<div class="notice">Reporting preferences unavailable: ${escapeHtml(profileError)}. Showing dates and numbers in your browser settings.</div>`);
  } catch (error) {
    root.innerHTML = `<section class="workspace-panel workspace-panel-body">
      <h1>Workspace data unavailable</h1>
      <p class="workspace-error">${escapeHtml(error.message)}</p>
      <p class="workspace-muted">The server returned an error rather than replacing unavailable account or market data with a zero value.</p>
      <button type="button" id="workspace-retry">Retry</button>
    </section>`;
    root.querySelector("#workspace-retry").addEventListener("click", () => mount(root, normalized));
  }
}
