(() => {
  "use strict";
  const status = document.getElementById("status");
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
    return String(value);
  };

  const render = data => {
    status.textContent = data.disclaimer;
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
