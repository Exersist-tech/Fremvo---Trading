// Backtest values are preformatted by the server. This presentation script
// performs no financial calculations and writes every dynamic value as text.
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function setStatus(message, error) {
    var status = $('status');
    status.textContent = message;
    status.className = error ? 'notice error' : 'notice';
  }

  function element(name, text, className) {
    var item = document.createElement(name);
    item.textContent = text;
    if (className) { item.className = className; }
    return item;
  }

  function table(headers, rows) {
    var result = document.createElement('table');
    var head = document.createElement('thead');
    var headerRow = document.createElement('tr');
    headers.forEach(function (header) {
      headerRow.appendChild(element('th', header.label, header.numeric ? 'numeric' : ''));
    });
    head.appendChild(headerRow);
    result.appendChild(head);

    var body = document.createElement('tbody');
    rows.forEach(function (values) {
      var row = document.createElement('tr');
      values.forEach(function (value, index) {
        row.appendChild(element('td', value == null ? 'Not recorded.' : String(value),
          headers[index].numeric ? 'numeric' : ''));
      });
      body.appendChild(row);
    });
    result.appendChild(body);
    return result;
  }

  function metadata(result) {
    return table(
      [
        { label: 'Strategy' }, { label: 'Template version' }, { label: 'Symbol' },
        { label: 'From UTC' }, { label: 'To UTC' }, { label: 'As of UTC' },
        { label: 'Completed UTC' }, { label: 'Dataset version identity' }, { label: 'Provenance' }
      ],
      [[
        result.strategyId, result.strategyTemplateVersion, result.symbol, result.fromUtc, result.toUtc,
        result.asOfUtc, result.completedAtUtc, result.datasetVersionIdentity, result.provenance
      ]]);
  }

  function metrics(result) {
    return table(
      [
        { label: 'Initial portfolio', numeric: true }, { label: 'Final portfolio', numeric: true },
        { label: 'Net P&L', numeric: true }, { label: 'Total fees', numeric: true },
        { label: 'Total slippage', numeric: true }, { label: 'Trade count', numeric: true },
        { label: 'Event count', numeric: true }, { label: 'Rejected simulations', numeric: true }
      ],
      [[
        result.initialPortfolio, result.finalPortfolio, result.netPnl, result.totalFees,
        result.totalSlippage, result.tradeCount, result.eventCount, result.rejectedEventCount
      ]]);
  }

  function reportCard(result) {
    var card = document.createElement('section');
    card.className = 'card';
    card.appendChild(element('h2', result.symbol + ' completed backtest'));
    card.appendChild(metadata(result));
    card.appendChild(element('h3', 'Recorded portfolio and modeled costs'));
    card.appendChild(metrics(result));

    card.appendChild(element('h3', 'Simulation events and rejection explanations'));
    var events = result.events || [];
    if (!events.length) {
      card.appendChild(element('p', 'No simulation events were recorded.', 'empty'));
    } else {
      card.appendChild(table(
        [
          { label: 'Observed UTC' }, { label: 'Outcome' }, { label: 'Rationale' },
          { label: 'Quantity', numeric: true }, { label: 'Price', numeric: true },
          { label: 'Fee', numeric: true }, { label: 'Slippage', numeric: true },
          { label: 'Cash balance', numeric: true }, { label: 'Base quantity', numeric: true },
          { label: 'Reference price', numeric: true }
        ],
        events.map(function (eventItem) {
          return [
            eventItem.observedAtUtc, eventItem.status, eventItem.rationale, eventItem.quantity,
            eventItem.price, eventItem.fee, eventItem.slippage, eventItem.cashBalance,
            eventItem.baseQuantity, eventItem.referencePrice
          ];
        })));
    }
    if (result.hasAdditionalEvents) {
      card.appendChild(element('p', 'Only the first recorded simulation events are shown.', 'empty'));
    }

    card.appendChild(element('h3', 'Recorded equity snapshots'));
    var snapshots = result.equitySnapshots || [];
    if (!snapshots.length) {
      card.appendChild(element('p', 'No equity snapshots were recorded.', 'empty'));
    } else {
      card.appendChild(table(
        [
          { label: 'Observed UTC' }, { label: 'Equity', numeric: true },
          { label: 'Cash balance', numeric: true }, { label: 'Base quantity', numeric: true },
          { label: 'Mark price', numeric: true }
        ],
        snapshots.map(function (snapshot) {
          return [
            snapshot.observedAtUtc, snapshot.equity, snapshot.cashBalance,
            snapshot.baseQuantity, snapshot.markPrice
          ];
        })));
    }
    if (result.hasAdditionalEquitySnapshots) {
      card.appendChild(element('p', 'Only the first recorded equity snapshots are shown.', 'empty'));
    }
    return card;
  }

  function render(page) {
    var host = $('results');
    host.textContent = '';
    var results = page.results || [];
    if (!results.length) {
      host.appendChild(element(
        'p',
        'No completed reproducible backtest is available yet.',
        'empty'));
      return;
    }

    results.forEach(function (result) {
      host.appendChild(reportCard(result));
    });
  }

  async function load() {
    setStatus('Loading completed backtest reports.', false);
    try {
      var response = await fetch('/api/backtests/results', { headers: { 'Accept': 'application/json' } });
      if (!response.ok) {
        setStatus('Completed backtest reports could not be loaded.', true);
        return;
      }

      render(await response.json());
      setStatus('Read-only historical analysis. Times are UTC; amounts are server-rendered values.', false);
    } catch (error) {
      setStatus('Completed backtest reports could not be loaded.', true);
    }
  }

  window.addEventListener('DOMContentLoaded', load);
})();
