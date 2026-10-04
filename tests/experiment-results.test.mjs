import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("standalone results color signed profits and losses without coloring unknown values", async () => {
  const node = name => ({
    name, textContent: "", className: "", children: [],
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; }
  });
  const elements = Object.fromEntries(
    ["status", "closed-trades", "results"].map(id => [id, node(id)]));
  const priorDocument = globalThis.document;
  const priorFetch = globalThis.fetch;
  try {
    globalThis.document = {
      getElementById: id => elements[id],
      createElement: node,
      createTextNode: text => ({ textContent: text })
    };
    globalThis.fetch = async () => ({
      ok: true,
      json: async () => ({
        disclaimer: "Research only",
        closedTrades: [
          { closedAtUtc: "2026-10-04T09:00:00Z", strategyId: "strategy",
            symbol: "BTC/USD", quantity: "1", averageEntryPrice: "100",
            averageBuyFillPrice: "99.5", averageExitPrice: "111", grossProfitAndLoss: "11", fees: "1",
            netProfitAndLoss: "10", returnPercent: "10", holdingSeconds: 60,
            buyFillCount: 1, sellFillCount: 1 },
          { closedAtUtc: "2026-10-04T09:01:00Z", strategyId: "strategy",
            symbol: "ETH/USD", quantity: "1", averageEntryPrice: "100",
            averageBuyFillPrice: "99.5", averageExitPrice: "98", grossProfitAndLoss: "-2", fees: "1",
            netProfitAndLoss: "-3", returnPercent: "-3", holdingSeconds: 60,
            buyFillCount: 1, sellFillCount: 1 }
        ],
        results: [
          { evaluatedAtUtc: "2026-10-04T09:00:00Z", realizedProfitAndLoss: "2",
            unrealizedProfitAndLoss: "-1" },
          { evaluatedAtUtc: "2026-10-04T09:01:00Z", realizedProfitAndLoss: null,
            unrealizedProfitAndLoss: null }
        ]
      })
    });
    const source = await readFile(
      new URL("../src/Trading.Web/wwwroot/experiment-results.js", import.meta.url), "utf8");
    await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
    await new Promise(resolve => setImmediate(resolve));

    const descendants = root => [root, ...(root.children ?? []).flatMap(descendants)];
    const closed = descendants(elements["closed-trades"]);
    assert.ok(closed.some(item => item.textContent === "BUY fill avg"));
    assert.ok(closed.some(item => item.textContent === "Cost basis incl. BUY fees"));
    assert.ok(closed.some(item => item.textContent === "SELL fill avg"));
    assert.ok(closed.some(item => item.textContent === "99.5"));
    assert.ok(closed.some(item => item.textContent === "111"));
    assert.ok(closed.some(item => item.className === "positive" && item.textContent === "+7"));
    assert.ok(closed.some(item => item.className === "positive" && item.textContent === "+10"));
    assert.ok(closed.some(item => item.className === "negative" && item.textContent === "-3"));
    const snapshots = descendants(elements.results);
    assert.ok(snapshots.some(item => item.className === "positive" && item.textContent === "+2"));
    assert.ok(snapshots.some(item => item.className === "negative" && item.textContent === "-1"));
    assert.ok(snapshots.some(item => item.className === "" && item.textContent === "Unknown"));
  } finally {
    globalThis.document = priorDocument;
    globalThis.fetch = priorFetch;
  }
});
