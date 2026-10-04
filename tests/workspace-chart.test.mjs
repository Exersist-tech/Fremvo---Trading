import assert from "node:assert/strict";
import test from "node:test";
import { chartPriceGuide, clampAxisCenter, drawLiveTradeOverlay, freshDisplayTrade, livePriceDirectionColor,
  panAxisCenter, verticalScaleFactor, zoomedBarCount } from "../src/Trading.Web/wwwroot/workspace.js";

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
