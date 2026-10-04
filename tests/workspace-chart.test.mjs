import assert from "node:assert/strict";
import test from "node:test";
import { chartPriceGuide, clampAxisCenter, drawCandles, drawHoveredPriceOverlay, drawLiveTradeOverlay,
  fillMarkerTooltip, freshDisplayTrade, hoveredChartPrice, livePriceDirectionColor, workerFillMarkers,
  panAxisCenter, publicMarketSymbol, verticalScaleFactor, zoomedBarCount } from "../src/Trading.Web/wwwroot/workspace.js";

test("public feeds use Kraken's exact slash-form pair without changing trading symbols", () => {
  const compact = { symbol: "0GEUR", displayName: "0G/EUR" };
  assert.equal(publicMarketSymbol(compact), "0G/EUR");
  assert.equal(compact.symbol, "0GEUR");
  assert.equal(publicMarketSymbol({ symbol: "SOL/EUR", displayName: "SOL/EUR" }), "SOL/EUR");
  assert.equal(publicMarketSymbol({ symbol: "0GEUR", displayName: "0GEUR" }), null);
  assert.equal(publicMarketSymbol({ symbol: "0GEUR", displayName: "0G//EUR" }), null);
  assert.equal(publicMarketSymbol({ symbol: "0GEUR", displayName: "0G/EUR?bad" }), null);
});

test("zoomed-out indicator scales allow panning while retaining visible data", () => {
  assert.equal(clampAxisCenter(0, 100, 120, 85), 85);
  assert.equal(clampAxisCenter(0, 100, 120, -100), -35);
  assert.equal(clampAxisCenter(0, 100, 120, 200), 135);
});

test("zoomed-in scales retain a visible portion of the data", () => {
  assert.equal(clampAxisCenter(0, 100, 40, -100), 5);
  assert.equal(clampAxisCenter(0, 100, 40, 200), 95);
});

test("dragging down pans the plotted price and indicators down", () => {
  const screenY = (center, value, range, height) =>
    (center + range / 2 - value) / range * height;
  const priceCenter = panAxisCenter(100, 25, 40, 100);
  const indicatorCenter = panAxisCenter(50, 25, 80, 100);
  assert.ok(screenY(priceCenter, 100, 40, 100) > screenY(100, 100, 40, 100));
  assert.ok(screenY(indicatorCenter, 50, 80, 100) > screenY(50, 50, 80, 100));
  assert.ok(verticalScaleFactor(30, true) < 1);
});

test("zooming out at canvas capacity cannot change the candle count or pan", () => {
  assert.equal(zoomedBarCount(100, 1.25, 320, 300), 100);
  assert.equal(zoomedBarCount(80, 1.25, 320, 300), 100);
  assert.equal(zoomedBarCount(120, 1.25, 60, 600), 60);
});

test("display-only price rejects snapshots, wrong pairs, invalid values and stale trades", () => {
  const now = Date.parse("2026-10-04T07:40:00Z");
  const tick = { symbol: "XBT/EUR", price: "62000.125", asOfUtc: "2026-10-04T07:39:59Z",
    isSnapshot: false };
  assert.equal(freshDisplayTrade(tick, "XBT/EUR", now), 62000.125);
  assert.equal(freshDisplayTrade({ ...tick, isSnapshot: true }, "XBT/EUR", now), null);
  assert.equal(freshDisplayTrade(tick, "LTC/EUR", now), null);
  assert.equal(freshDisplayTrade({ ...tick, price: "0" }, "XBT/EUR", now), null);
  assert.equal(freshDisplayTrade({ ...tick, asOfUtc: "2026-10-04T07:39:44Z" }, "XBT/EUR", now), null);
  assert.equal(freshDisplayTrade({ ...tick, asOfUtc: "2026-10-04T07:40:06Z" }, "XBT/EUR", now), null);
});

test("price guide keeps a truthful selected-pair reference when trades pause or disconnect", () => {
  const now = Date.parse("2026-10-04T07:40:00Z");
  const tick = { symbol: "XBT/EUR", price: "62000.125",
    asOfUtc: "2026-10-04T07:39:59Z", isSnapshot: false };
  const candles = [{ isClosed: true, close: "61995",
    closeTimeUtc: "2026-10-04T07:35:00Z" }];
  assert.deepEqual(chartPriceGuide(tick, "XBT/EUR", candles, "FiveMinutes", now), {
    price: 62000.125, label: "XBTEUR", asOfUtc: tick.asOfUtc, stale: false
  });
  assert.deepEqual(chartPriceGuide(tick, "XBT/EUR", candles, "FiveMinutes", now + 16000), {
    price: 62000.125, label: "Last known", asOfUtc: tick.asOfUtc, stale: true
  });
  assert.deepEqual(chartPriceGuide(tick, "XBT/EUR", candles, "FiveMinutes", now, false), {
    price: 62000.125, label: "Last known", asOfUtc: tick.asOfUtc, stale: true
  });
  const newerCandles = [{ ...candles[0], closeTimeUtc: "2026-10-04T07:40:00Z" }];
  assert.deepEqual(chartPriceGuide(tick, "XBT/EUR", newerCandles, "FiveMinutes", now + 16000), {
    price: 61995, label: "Closed 5m", asOfUtc: newerCandles[0].closeTimeUtc, stale: true
  });
  assert.equal(chartPriceGuide(tick, "LTC/EUR", [], "FiveMinutes", now), null);
  assert.deepEqual(chartPriceGuide(null, "XBT/EUR", candles, "FiveMinutes", now), {
    price: 61995, label: "Closed 5m", asOfUtc: candles[0].closeTimeUtc, stale: true
  });
  assert.equal(chartPriceGuide({ ...tick, price: "-1" }, "XBT/EUR", [], "FiveMinutes", now), null);
  assert.equal(chartPriceGuide({ ...tick, asOfUtc: "2026-10-04T07:40:07Z" },
    "XBT/EUR", [], "FiveMinutes", now), null);
  assert.equal(chartPriceGuide(null, "XBT/EUR", [], "FiveMinutes", now), null);
  assert.equal(chartPriceGuide(null, "XBT/EUR", [{ ...candles[0], isClosed: false }],
    "FiveMinutes", now), null);
  assert.deepEqual(chartPriceGuide({ ...tick, isSnapshot: true }, "XBT/EUR", [], "FiveMinutes", now), {
    price: 62000.125, label: "Snapshot", asOfUtc: tick.asOfUtc, stale: true
  });
});

test("only a fresh quote for the selected pair can replace a trade, never an old-pair response", () => {
  const now = Date.parse("2026-10-04T07:40:00Z");
  const tick = { symbol: "XBT/EUR", price: "62000", asOfUtc: "2026-10-04T07:39:57Z",
    isSnapshot: false };
  const quote = { symbol: "XBT/EUR", price: "62001.25", asOfUtc: "2026-10-04T07:39:59Z" };
  assert.deepEqual(chartPriceGuide(tick, "XBT/EUR", [], "FiveMinutes", now, true, quote), {
    price: 62001.25, label: "MID BBO", asOfUtc: quote.asOfUtc, stale: false
  });
  assert.equal(chartPriceGuide(tick, "LTC/EUR", [], "FiveMinutes", now, true, quote), null);
  assert.equal(chartPriceGuide(null, "LTC/EUR", [], "FiveMinutes", now, true, quote), null);
  assert.deepEqual(chartPriceGuide(tick, "XBT/EUR", [], "FiveMinutes", now, true,
    { ...quote, asOfUtc: "2026-10-04T07:39:55Z" }), {
    price: 62000, label: "XBTEUR", asOfUtc: tick.asOfUtc, stale: false
  });
  assert.deepEqual(chartPriceGuide(null, "XBT/EUR", [], "FiveMinutes", now + 11000, false, quote), {
    price: 62001.25, label: "Last quote", asOfUtc: quote.asOfUtc, stale: true
  });
  assert.equal(chartPriceGuide(null, "XBT/EUR", [], "FiveMinutes", now, true,
    { ...quote, price: "0" }), null);
});

test("current price is stippled across the plot and tagged on the right-hand price axis", () => {
  const operations = [];
  const context = {
    save: () => operations.push(["save"]),
    restore: () => operations.push(["restore"]),
    setLineDash: pattern => operations.push(["dash", ...pattern]),
    beginPath: () => {},
    moveTo: (x, y) => operations.push(["move", x, y]),
    lineTo: (x, y) => operations.push(["line", x, y]),
    stroke: () => operations.push(["stroke"]),
    fillRect: (...rect) => operations.push(["badge", ...rect]),
    fillText: (text, x, y) => operations.push(["text", text, x, y])
  };
  drawLiveTradeOverlay(context, {
    symbol: "SOL/USD", price: 120.97, lastClosedPrice: 121.02,
    levelY: 245, plotLeft: 12, plotRight: 600, priceTop: 16, priceBottom: 500,
    axisWidth: 78
  });
  assert.equal(context.strokeStyle, "#ef5350");
  assert.deepEqual(operations.slice(1, 4), [["dash", 1, 3], ["move", 12, 245.5], ["line", 600, 245.5]]);
  assert.ok(operations.some(op => op[0] === "badge" && op[1] === 600 && op[3] === 74));
  assert.ok(operations.some(op => op[0] === "text" && op[1] === "SOLUSD"));
  assert.ok(operations.some(op => op[0] === "text" && op[1].includes("120.97")));
  assert.equal(livePriceDirectionColor(121.5, 121.02), "#26a69a");
  assert.equal(livePriceDirectionColor(121.5, null), "#a7b1bd");
});

test("cursor price tracks the current vertical chart scale only within the price plot", () => {
  const scale = { left: 12, right: 600, top: 16, bottom: 416, maxPrice: 150, priceRange: 100 };
  assert.equal(hoveredChartPrice({ x: 250, y: 16 }, scale), 150);
  assert.equal(hoveredChartPrice({ x: 250, y: 216 }, scale), 100);
  assert.equal(hoveredChartPrice({ x: 250, y: 416 }, scale), 50);
  assert.equal(hoveredChartPrice({ x: 250, y: 216 }, { ...scale,
    maxPrice: 130, priceRange: 40 }), 110);
  assert.equal(hoveredChartPrice({ x: 601, y: 216 }, scale), null);
  assert.equal(hoveredChartPrice({ x: 250, y: 440 }, scale), null);
  assert.equal(hoveredChartPrice({ x: 250, y: 216, verticalOnly: true }, scale), null);
  assert.equal(hoveredChartPrice({ x: 250, y: 216 }, { ...scale, priceRange: 0 }), null);
  assert.equal(hoveredChartPrice({ x: 250, y: 416 }, { ...scale,
    maxPrice: 50, priceRange: 100 }), null);
});

test("cursor price uses a separate stippled guide and a legible right-axis badge", () => {
  const operations = [];
  const context = {
    save: () => {}, restore: () => {},
    setLineDash: pattern => operations.push(["dash", ...pattern]),
    beginPath: () => {},
    moveTo: (x, y) => operations.push(["move", x, y]),
    lineTo: (x, y) => operations.push(["line", x, y]),
    stroke: () => {},
    fillRect: (...rect) => operations.push(["badge", ...rect]),
    fillText: text => operations.push(["text", text])
  };
  const options = { price: 120.97, levelY: 245, plotLeft: 12, plotRight: 600,
    priceTop: 16, priceBottom: 416, axisWidth: 78 };
  drawHoveredPriceOverlay(context, options);
  assert.deepEqual(operations.slice(0, 3),
    [["dash", 1, 3], ["move", 12, 245.5], ["line", 600, 245.5]]);
  assert.ok(operations.some(op => op[0] === "badge" && op[1] === 600 && op[3] === 74));
  assert.ok(operations.some(op => op[0] === "text" && op[1] === "120.97"));
  assert.equal(context.fillStyle, "#fff");
  operations.length = 0;
  drawHoveredPriceOverlay(context, { ...options, liveLevelY: 245 });
  const hoverBadge = operations.find(op => op[0] === "badge");
  assert.ok(hoverBadge[2] + hoverBadge[4] <= 230 || hoverBadge[2] >= 260,
    "hover badge must not cover the live price badge");
  operations.length = 0;
  drawHoveredPriceOverlay(context, { ...options, levelY: 18, liveLevelY: 16 });
  assert.equal(operations.find(op => op[0] === "badge")[2], 46);
  operations.length = 0;
  drawHoveredPriceOverlay(context, { ...options, levelY: 415, liveLevelY: 416 });
  assert.equal(operations.find(op => op[0] === "badge")[2], 362);
});

test("a complete hover redraw preserves both the current-price and cursor-price guides", () => {
  const operations = [];
  const context = new Proxy({}, {
    get(target, key) {
      if (key === "measureText") return text => ({ width: String(text).length * 6 });
      if (key in target) return target[key];
      return (...args) => operations.push([key, ...args]);
    },
    set(target, key, value) {
      target[key] = value;
      return true;
    }
  });
  const canvas = {
    width: 0, height: 0,
    getContext: () => context,
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 500 })
  };
  const now = Date.now();
  const candles = [{ openTimeUtc: new Date(now - 360000).toISOString(),
    closeTimeUtc: new Date(now - 60000).toISOString(), open: "119",
    high: "122", low: "118", close: "120", volume: "10", isClosed: true }];
  const view = { barCount: 30, rightOffset: 0, priceScale: 1, priceOffset: 0,
    symbol: "SOL/USD", interval: "FiveMinutes", feedConnected: true,
    crosshair: { x: 250, y: 200 } };
  const oldWindow = globalThis.window;
  globalThis.window = { devicePixelRatio: 1 };
  try {
    const geometry = drawCandles(canvas, candles, [], null, [], [], null,
      { rsi: [], macdHistogram: [] },
      { rsi: false, macd: false, ema20: false, ema50: false, bollinger: false },
      view, null, { symbol: "SOL/USD", price: "120.5",
        asOfUtc: new Date(now).toISOString(), isSnapshot: false }, null);
    assert.ok(geometry);
    const badges = operations.filter(op => op[0] === "fillRect" && op[1] === 722);
    assert.equal(badges.length, 2, "both the current price and the hovered price must render");
    assert.deepEqual(badges.map(op => op[4]), [30, 24]);
    assert.ok(operations.some(op => op[0] === "fillText" && op[1] === "SOLUSD"));
    assert.ok(operations.some(op => op[0] === "fillText" && op[1] === "120.5"));
    assert.ok(operations.some(op => op[0] === "setLineDash"
      && Array.isArray(op[1]) && op[1].join() === "1,3"));
  } finally {
    globalThis.window = oldWindow;
  }
});

test("worker fill markers preserve the actual open entry beyond ten recent fills", () => {
  const entry = { direction: "Buy", occurredAtUtc: "2026-10-04T07:00:00Z",
    executionPrice: "120", quantity: "2", symbol: "SOL/USD" };
  const sell = { direction: "Sell", occurredAtUtc: "2026-10-04T07:02:00Z",
    executionPrice: "125", quantity: "0.1", symbol: "SOL/USD" };
  const worker = { slot: 1, symbol: "SOL/USD", positionQuantity: "1.9",
    openPositionEntry: entry, recentTrades: [sell] };
  const markers = workerFillMarkers([worker]);
  assert.deepEqual(markers.map(marker => [marker.side, marker.isOpenEntry, marker.price]),
    [["buy", true, "120"], ["sell", false, "125"]]);
  assert.equal(workerFillMarkers([{ ...worker, recentTrades: [entry, sell] }]).length, 2);
  assert.equal(workerFillMarkers([{ ...worker, positionQuantity: 0,
    recentTrades: [sell] }]).length, 1);
  assert.deepEqual(fillMarkerTooltip(markers[0], "Before visible candles").at(-1),
    "Before visible candles · actual fill time above");
  assert.match(fillMarkerTooltip(markers[1])[0], /Worker 1 · Paper SELL/);
  assert.match(fillMarkerTooltip(markers[0])[2], /Quantity 2 · Fill 120/);
  assert.doesNotMatch(fillMarkerTooltip(markers[0]).join(" "), /fee|decision/i);
});

test("green entry and red exit arrows show fill-specific tooltips even past the latest candle", () => {
  const operations = [];
  const context = new Proxy({}, {
    get(target, key) {
      if (key === "measureText") return text => ({ width: String(text).length * 6 });
      if (key === "fill") return () => operations.push(["fill", context.fillStyle]);
      if (key in target) return target[key];
      return (...args) => operations.push([key, ...args]);
    },
    set(target, key, value) { target[key] = value; return true; }
  });
  const canvas = {
    width: 0, height: 0,
    getContext: () => context,
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 500 })
  };
  const now = Date.now();
  const candle = { openTimeUtc: new Date(now - 360000).toISOString(),
    closeTimeUtc: new Date(now - 60000).toISOString(),
    open: "119", high: "122", low: "118", close: "120", volume: "10", isClosed: true };
  const entry = { slot: 1, symbol: "SOL/USD", positionQuantity: "0.9",
    openPositionEntry: { direction: "Buy", occurredAtUtc: new Date(now - 120000).toISOString(),
      executionPrice: "120", quantity: "1", symbol: "SOL/USD" },
    recentTrades: [{ direction: "Sell", occurredAtUtc: new Date(now + 60000).toISOString(),
      executionPrice: "121", quantity: "0.1", symbol: "SOL/USD" }] };
  const markers = workerFillMarkers([entry]);
  const view = { barCount: 30, rightOffset: 0, priceScale: 1, priceOffset: 0,
    symbol: "SOL/USD", interval: "FiveMinutes", feedConnected: false };
  const oldWindow = globalThis.window;
  globalThis.window = { devicePixelRatio: 1 };
  const draw = () => drawCandles(canvas, [candle], [], null, markers, [], null,
    { rsi: [], macdHistogram: [] },
    { rsi: false, macd: false, ema20: false, ema50: false, bollinger: false },
    view, entry, null, null);
  try {
    const geometry = draw();
    assert.ok(operations.some(op => op[0] === "fill" && op[1] === "#3fb950"));
    assert.ok(operations.some(op => op[0] === "fill" && op[1] === "#f85149"));
    const entryLevel = geometry.top + (geometry.maxPrice - 120) / geometry.priceRange * geometry.height;
    const exitLevel = geometry.top + (geometry.maxPrice - 121) / geometry.priceRange * geometry.height;
    assert.ok(operations.some(op => op[0] === "moveTo"
      && op[1] === 367 && Math.abs(op[2] - (entryLevel + 8)) < 0.001));
    assert.ok(operations.some(op => op[0] === "lineTo"
      && op[1] === 356 && Math.abs(op[2] - (entryLevel + 21)) < 0.001));
    assert.ok(operations.some(op => op[0] === "moveTo"
      && op[1] === 710 && Math.abs(op[2] - (exitLevel - 8)) < 0.001));
    assert.ok(operations.some(op => op[0] === "lineTo"
      && op[1] === 699 && Math.abs(op[2] - (exitLevel - 21)) < 0.001));
    view.crosshair = { x: 367, y: entryLevel + 20 };
    operations.length = 0;
    draw();
    assert.ok(operations.some(op => op[0] === "fillText"
      && op[1] === "Worker 1 · Paper BUY entry"));
    assert.ok(operations.some(op => op[0] === "fillText"
      && op[1] === "Quantity 1 · Fill 120"));
    view.crosshair = { x: 710, y: exitLevel - 20 };
    operations.length = 0;
    draw();
    assert.ok(operations.some(op => op[0] === "fillText"
      && op[1] === "Worker 1 · Paper SELL"));
    assert.ok(operations.some(op => op[0] === "fillText"
      && op[1] === "After latest candle · actual fill time above"));
  } finally {
    globalThis.window = oldWindow;
  }
});

test("selected worker shows only its recorded stop and estimated exit while its position is open", () => {
  const labels = [];
  const context = new Proxy({}, {
    get(target, key) {
      if (key === "fillText") return text => labels.push(text);
      if (key in target) return target[key];
      return () => {};
    },
    set(target, key, value) { target[key] = value; return true; }
  });
  const canvas = {
    width: 0, height: 0,
    getContext: () => context,
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 500 })
  };
  const candle = { openTimeUtc: "2026-10-04T07:00:00Z",
    closeTimeUtc: "2026-10-04T07:05:00Z", open: "119",
    high: "122", low: "118", close: "120", volume: "10", isClosed: true };
  const view = { barCount: 30, rightOffset: 0, priceScale: 1, priceOffset: 0,
    symbol: "SOL/USD", interval: "FiveMinutes", feedConnected: false };
  const draw = worker => drawCandles(canvas, [candle], [], null, [], [], null,
    { rsi: [], macdHistogram: [] },
    { rsi: false, macd: false, ema20: false, ema50: false, bollinger: false },
    view, worker, null, null);
  const oldWindow = globalThis.window;
  globalThis.window = { devicePixelRatio: 1 };
  try {
    const worker = { symbol: "SOL/USD", positionQuantity: "1",
      protectiveStopPrice: "110", estimatedTargetPrice: "135" };
    const geometry = draw(worker);
    assert.ok(geometry.minPrice < 110 && geometry.maxPrice > 135);
    assert.ok(labels.includes("Plan stop (not order) 110"));
    assert.ok(labels.includes("Est. exit (not order) 135"));
    labels.length = 0;
    draw({ ...worker, estimatedTargetPrice: null });
    assert.ok(labels.includes("Plan stop (not order) 110"));
    assert.ok(!labels.some(label => label.startsWith("Est. exit")));
    labels.length = 0;
    draw({ ...worker, positionQuantity: "0" });
    assert.ok(!labels.some(label => label.startsWith("Est. exit")
      || label.startsWith("Plan stop")));
  } finally {
    globalThis.window = oldWindow;
  }
});

test("stale and out-of-range prices remain visibly marked without changing the chart scale", () => {
  const operations = [];
  const context = {
    save: () => {}, restore: () => {}, setLineDash: () => {},
    beginPath: () => {}, stroke: () => {},
    moveTo: (x, y) => operations.push(["move", x, y]),
    lineTo: (x, y) => operations.push(["line", x, y]),
    fillRect: () => {},
    fillText: text => operations.push(["text", text])
  };
  const options = { symbol: "SOL/USD", label: "Closed 5m", price: 120.97,
    lastClosedPrice: 120.97, plotLeft: 12, plotRight: 600, priceTop: 16,
    priceBottom: 500, axisWidth: 78, stale: true };
  drawLiveTradeOverlay(context, { ...options, levelY: -500 });
  assert.equal(context.strokeStyle, "#8b98a9");
  assert.deepEqual(operations.slice(0, 2), [["move", 12, 17.5], ["line", 600, 17.5]]);
  assert.ok(operations.some(op => op[0] === "text" && op[1] === "Closed 5m"));
  assert.ok(operations.some(op => op[0] === "text" && op[1].startsWith("↑ 120.97")));
  operations.length = 0;
  drawLiveTradeOverlay(context, { ...options, levelY: 1000 });
  assert.deepEqual(operations.slice(0, 2), [["move", 12, 499.5], ["line", 600, 499.5]]);
  assert.ok(operations.some(op => op[0] === "text" && op[1].startsWith("↓ 120.97")));
});
