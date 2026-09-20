// Open positions across every pair.
//
// Money figures are rendered exactly as the server computed them in decimal.
// Nothing here recalculates a profit or loss, so this page cannot disagree
// with the platform's own books.

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function setStatus(message, isError) {
    var el = $('status');
    el.textContent = message;
    el.className = isError ? 'notice error' : 'notice';
  }

  function formatPrice(value) {
    var n = Number(value);
    if (!isFinite(n)) { return String(value); }
    var decimals = Math.abs(n) >= 100 ? 2 : Math.abs(n) >= 1 ? 4 : 8;
    return n.toFixed(decimals);
  }

  function formatSigned(value) {
    var n = Number(value);
    if (!isFinite(n)) { return String(value); }
    return (n > 0 ? '+' : '') + formatPrice(n);
  }

  function formatTime(iso) {
    return new Date(iso).toLocaleString();
  }

  function isShort(direction) {
    var value = String(direction || '').toLowerCase();
    return value === 'short' || value === 'directionshort';
  }

  function openChart(symbol) {
    window.location.assign('/chart?symbol=' + encodeURIComponent(symbol));
  }

  function renderSummary(positions) {
    var host = $('summary');
    host.textContent = '';

    if (!positions.length) { return; }

    // Only positions that actually have a price contribute to a total. Adding
    // an unpriced position as zero would present an incomplete total as a
    // complete one.
    var priced = positions.filter(function (p) {
      return p.unrealisedPnl !== null && p.unrealisedPnl !== undefined;
    });

    var total = 0;
    priced.forEach(function (p) { total += Number(p.unrealisedPnl); });

    var line = document.createElement('p');
    line.className = 'notice';

    var label = document.createElement('span');
    label.textContent = positions.length + ' open position' + (positions.length === 1 ? '' : 's') + '. ' +
      'Unrealised across the ' + priced.length + ' that could be priced: ';
    line.appendChild(label);

    var figure = document.createElement('strong');
    figure.textContent = formatSigned(total);
    figure.style.color = total >= 0 ? '#3fbf6f' : '#ff6b6b';
    line.appendChild(figure);

    if (priced.length !== positions.length) {
      var caveat = document.createElement('span');
      caveat.textContent = ' ' + (positions.length - priced.length) +
        ' position could not be priced and is excluded from this total.';
      line.appendChild(caveat);
    }

    host.appendChild(line);
  }

  function renderTable(positions) {
    var host = $('positions');
    host.textContent = '';

    if (!positions.length) {
      var empty = document.createElement('p');
      empty.className = 'empty';
      empty.textContent = 'No open positions. Place a paper order from the chart to open one.';
      host.appendChild(empty);
      return;
    }

    var table = document.createElement('table');
    var head = document.createElement('tr');
    ['Pair', 'Direction', 'Quantity', 'Entry', 'Last closed', 'Unrealised', 'Return', 'Stop', 'Target', 'Opened', '']
      .forEach(function (title) {
        var th = document.createElement('th');
        th.textContent = title;
        head.appendChild(th);
      });
    table.appendChild(head);

    positions.forEach(function (p) {
      var tr = document.createElement('tr');
      tr.style.cursor = 'pointer';
      tr.addEventListener('click', function () { openChart(p.symbol); });

      var unrealised = p.unrealisedPnl;
      var percent = p.unrealisedPercent;

      var cells = [
        p.symbol,
        isShort(p.direction) ? 'Short' : 'Long',
        p.quantity,
        formatPrice(p.entryPrice),
        p.markPrice === null || p.markPrice === undefined ? 'unavailable' : formatPrice(p.markPrice),
        unrealised === null || unrealised === undefined ? 'unknown' : formatSigned(unrealised),
        percent === null || percent === undefined ? 'unknown' : formatSigned(percent) + '%',
        p.stopLossPrice === null || p.stopLossPrice === undefined ? 'none' : formatPrice(p.stopLossPrice),
        p.takeProfitPrice === null || p.takeProfitPrice === undefined ? 'none' : formatPrice(p.takeProfitPrice),
        formatTime(p.openedAtUtc)
      ];

      cells.forEach(function (value, index) {
        var td = document.createElement('td');
        // textContent everywhere: no venue or user string is parsed as markup.
        td.textContent = String(value);

        if ((index === 5 || index === 6) && unrealised !== null && unrealised !== undefined) {
          td.style.color = Number(unrealised) >= 0 ? '#3fbf6f' : '#ff6b6b';
        }

        // An unprotected position is stated plainly. A blank cell reads as
        // though a stop exists and simply was not shown.
        if ((index === 7 || index === 8) && value === 'none') {
          td.style.color = '#9aa0a6';
        }

        tr.appendChild(td);
      });

      var actionCell = document.createElement('td');
      var button = document.createElement('button');
      button.type = 'button';
      button.textContent = 'Chart';
      button.addEventListener('click', function (event) {
        event.stopPropagation();
        openChart(p.symbol);
      });
      actionCell.appendChild(button);
      tr.appendChild(actionCell);

      table.appendChild(tr);
    });

    host.appendChild(table);

    var stale = positions.filter(function (p) { return p.priceIsStale; });
    if (stale.length) {
      var warning = document.createElement('p');
      warning.className = 'notice error';
      warning.textContent = stale.length + ' position priced from a candle older than 30 minutes. ' +
        'Treat those results as out of date.';
      host.appendChild(warning);
    }

    var unpriced = positions.filter(function (p) { return p.priceUnavailableReason; });
    if (unpriced.length) {
      var reason = document.createElement('p');
      reason.className = 'notice error';
      reason.textContent = unpriced.length + ' position could not be priced: ' +
        unpriced[0].priceUnavailableReason;
      host.appendChild(reason);
    }
  }

  async function load() {
    setStatus('Loading positions.', false);
    $('refresh').disabled = true;

    try {
      var response = await fetch('/api/paper/positions', { headers: { 'Accept': 'application/json' } });

      if (!response.ok) {
        setStatus('Positions could not be loaded.', true);
        return;
      }

      var payload = await response.json();
      var positions = payload.positions || [];

      setStatus(payload.disclaimer || 'Paper trading.', false);
      renderSummary(positions);
      renderTable(positions);
    } catch (error) {
      setStatus('Positions could not be loaded. ' + error.message, true);
    } finally {
      $('refresh').disabled = false;
    }
  }

  async function evaluate() {
    setStatus('Checking stops and targets against closed candles.', false);
    $('evaluate').disabled = true;

    try {
      var response = await fetch('/api/paper/exits/evaluate', {
        method: 'POST',
        headers: { 'Accept': 'application/json' }
      });

      if (!response.ok) {
        setStatus('Stops and targets could not be checked.', true);
        return;
      }

      var payload = await response.json();

      if (!payload.closed) {
        setStatus('No stop or target was reached on a closed candle.', false);
      } else {
        var ambiguous = (payload.fills || []).filter(function (f) { return f.bothLevelsTouched; });
        var message = payload.closed + ' position closed by a stop or target.';

        if (ambiguous.length) {
          // Stated rather than hidden: the result depended on an assumption.
          message += ' ' + ambiguous.length + ' of them reached both levels within one candle. ' +
            'The order of events cannot be recovered from a candle, so the stop was taken.';
        }

        setStatus(message, false);
      }

      await load();
    } catch (error) {
      setStatus('Stops and targets could not be checked. ' + error.message, true);
    } finally {
      $('evaluate').disabled = false;
    }
  }

  window.addEventListener('DOMContentLoaded', function () {
    $('refresh').addEventListener('click', load);
    $('evaluate').addEventListener('click', evaluate);
    load();
  });
})();
