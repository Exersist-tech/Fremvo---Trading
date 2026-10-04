(() => {
  "use strict";
  const status = document.getElementById("status");
  const closedOutput = document.getElementById("closed-trades");
  const output = document.getElementById("results");
  const columns = [
    ["Evaluated (UTC)", "evaluatedAtUtc"], ["Worker", "workerId"], ["Strategy", "strategyId"],
    ["Equity", "equity"], ["Cash", "cash"], ["Position", "positionQuantity"],
    ["Realized P&L", "realizedProfitAndLoss"], ["Unrealized P&L", "unrealizedProfitAndLoss"],
    ["Drawdown", "maximumDrawdown"], ["Fees", "fees"], ["Slippage", "slippage"],
    ["Rejected fills", "rejectedFillCount"], ["Rejected actions", "rejectedActionCount"],
    ["Exposure", "exposure"], ["Gate failures", "gateFailureCount"], ["Seed", "seed"]
  ];

  const format = (value, key) => {
    if (value === null || value === undefined) return "Unknown";
    if (key === "evaluatedAtUtc") return new Date(value).toISOString();
    if (key === "positionQuantity") return quantity(value);
    if (key === "realizedProfitAndLoss" || key === "unrealizedProfitAndLoss")
      return signed(value);
    if ([
      "equity", "cash", "realizedProfitAndLoss", "unrealizedProfitAndLoss",
      "maximumDrawdown", "fees", "slippage", "exposure"
    ].includes(key)) return number(value);
    return String(value);
  };

  const number = value => Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 });
  const quantity = value => Number(value).toLocaleString(undefined, { maximumFractionDigits: 8 });
  const price = value => Number(value).toLocaleString(undefined, { maximumFractionDigits: 8 });
  const signed = value => {
    const numeric = Number(value);
    return `${numeric > 0 ? "+" : ""}${number(numeric)}`;
  };
  const pnlClass = value => value === null || value === undefined ? ""
    : Number(value) > 0 ? "positive" : Number(value) < 0 ? "negative" : "";
  const duration = seconds => {
    const totalMinutes = Math.floor(Number(seconds) / 60);
    const days = Math.floor(totalMinutes / 1440);
    const hours = Math.floor((totalMinutes % 1440) / 60);
    const minutes = totalMinutes % 60;
    return [days ? `${days}d` : "", hours ? `${hours}h` : "", `${minutes}m`]
      .filter(Boolean)
      .join(" ");
  };
  const cell = (row, value, className) => {
    const element = document.createElement("td");
    element.textContent = value;
    if (className) element.className = className;
    row.append(element);
  };

  const renderClosedTrades = trades => {
    if (!trades || trades.length === 0) {
      closedOutput.replaceChildren(Object.assign(document.createElement("p"), {
        className: "empty",
        textContent: "No fully closed paper trades are available for this owner."
      }));
      return;
    }

    const summary = document.createElement("p");
    summary.className = "notice";
    const net = trades.reduce((total, trade) => total + Number(trade.netProfitAndLoss), 0);
    const fees = trades.reduce((total, trade) => total + Number(trade.fees), 0);
    const wins = trades.filter(trade => Number(trade.netProfitAndLoss) > 0).length;
    const losses = trades.filter(trade => Number(trade.netProfitAndLoss) < 0).length;
    summary.append(
      document.createTextNode(`${trades.length} closed trades · ${wins} profitable · ${losses} losing · Net P&L `),
      Object.assign(document.createElement("strong"), {
        className: pnlClass(net), textContent: signed(net)
      }),
      document.createTextNode(` · Fees ${number(fees)}`)
    );

    const table = document.createElement("table");
    const head = document.createElement("thead");
    const headerRow = document.createElement("tr");
    [
      "Closed", "Strategy", "Pair", "Quantity", "BUY fill avg", "Cost basis incl. BUY fees", "SELL fill avg",
      "Gross P&L", "Fees", "Net P&L", "Return", "Held", "Fills", "Exit reason"
    ].forEach(label => {
      const element = document.createElement("th");
      element.textContent = label;
      headerRow.append(element);
    });
    head.append(headerRow);
    table.append(head);
    const body = document.createElement("tbody");
    trades.forEach(trade => {
      const row = document.createElement("tr");
      const resultClass = pnlClass(trade.netProfitAndLoss);
      cell(row, new Date(trade.closedAtUtc).toLocaleString());
      cell(row, trade.strategyId);
      cell(row, trade.symbol);
      cell(row, quantity(trade.quantity));
      cell(row, price(trade.averageBuyFillPrice));
      cell(row, price(trade.averageEntryPrice));
      cell(row, price(trade.averageExitPrice));
      cell(row, signed(trade.grossProfitAndLoss), pnlClass(trade.grossProfitAndLoss));
      cell(row, number(trade.fees));
      cell(row, signed(trade.netProfitAndLoss), resultClass);
      cell(row, `${signed(trade.returnPercent)}%`, resultClass);
      cell(row, duration(trade.holdingSeconds));
      cell(row, `${trade.buyFillCount} buy / ${trade.sellFillCount} sell`);
      cell(row, trade.exitReason || "Exit decision reason unavailable");
      body.append(row);
    });
    table.append(body);
    closedOutput.replaceChildren(summary, table);
  };

  const render = data => {
    status.textContent = data.disclaimer;
    renderClosedTrades(data.closedTrades);
    if (!data.results || data.results.length === 0) {
      output.replaceChildren(Object.assign(document.createElement("p"), { className: "empty", textContent: "No immutable experiment result snapshots are available for this owner." }));
      return;
    }
    const table = document.createElement("table");
    const head = document.createElement("thead");
    const headerRow = document.createElement("tr");
    columns.forEach(([label]) => {
      const cell = document.createElement("th");
      cell.textContent = label;
      headerRow.append(cell);
    });
    head.append(headerRow);
    table.append(head);
    const body = document.createElement("tbody");
    data.results.forEach(result => {
      const row = document.createElement("tr");
      columns.forEach(([, key]) => {
        const cell = document.createElement("td");
        cell.textContent = format(result[key], key);
        if (key === "realizedProfitAndLoss" || key === "unrealizedProfitAndLoss")
          cell.className = pnlClass(result[key]);
        row.append(cell);
      });
      body.append(row);
    });
    table.append(body);
    output.replaceChildren(table);
  };

  fetch("/api/experiment-results?page=0&pageSize=25", { headers: { Accept: "application/json" } })
    .then(response => response.ok ? response.json() : response.json().catch(() => ({})).then(body => Promise.reject(new Error(body.error || `Request failed (${response.status})`))))
    .then(render)
    .catch(error => {
      status.classList.add("error");
      status.textContent = `Unable to load experiment results: ${error.message}`;
      output.replaceChildren(Object.assign(document.createElement("p"), { className: "empty", textContent: "No results are shown because the request failed." }));
    });
})();
