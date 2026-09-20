// Candlestick chart.
//
// The numbers here drive pixels only. Every financial decision is made on the
// server against decimal values; nothing on this page is used to size, price or
// evaluate a trade, so converting to JavaScript numbers for drawing is safe.
//
// The bar that is still forming is drawn differently from finished bars on
// purpose. A partial bar looks exactly like a complete one on most charts,
// which invites reading a signal off a candle that has not finished yet.

(function () {
  'use strict';

  var UP = '#1a7f49';
  var DOWN = '#c03030';
  var FORMING = '#8a6d1f';
  var AXIS = '#9aa0a6';
  var GRID = '#2a2d31';
  var TEXT = '#d6d8db';
  var ENTRY_LONG = '#3da5ff';
  var ENTRY_SHORT = '#ff9f43';
  var STOP = '#ff5f56';
  var TARGET = '#3fbf6f';

  var state = {
    candles: [],
    positions: [],
    orders: [],
    pairs: [],
    symbol: '',
    interval: '',
    // View window over the candle series. barCount is how many bars are drawn;
    // rightOffset is how many bars back from the newest the window ends.
    barCount: 160,
    rightOffset: 0
  };

  var MIN_BARS = 12;
  var pairSearchTimer = null;
  var pairSearchGeneration = 0;

  function $(id) { return document.getElementById(id); }

  function setStatus(message, isError) {
    var el = $('status');
    el.textContent = message;
    el.className = isError ? 'notice error' : 'notice';
  }

  async function readJsonResponse(response) {
    var contentType = response.headers.get('content-type') || '';
    if (!contentType.toLowerCase().includes('application/json')) {
      return {};
    }

    return await response.json().catch(function () { return {}; });
  }

  function formatPrice(value) {
    var n = Number(value);
    if (!isFinite(n)) { return String(value); }
    var decimals = Math.abs(n) >= 100 ? 2 : Math.abs(n) >= 1 ? 4 : 8;
    return n.toFixed(decimals);
  }

  function formatTime(iso) {
    var d = new Date(iso);
    // The user's own locale and zone. Times are transported as UTC; only the
    // presentation is localised.
    return d.toLocaleString();
  }

  function formatSigned(value) {
    var n = Number(value);
    if (!isFinite(n)) { return String(value); }
    return (n > 0 ? '+' : '') + formatPrice(n);
  }

  function draw() {
    var canvas = $('chart');
    var ctx = canvas.getContext('2d');

    var ratio = window.devicePixelRatio || 1;
    var cssWidth = canvas.clientWidth;
    var cssHeight = canvas.clientHeight;
    canvas.width = Math.floor(cssWidth * ratio);
    canvas.height = Math.floor(cssHeight * ratio);
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, cssWidth, cssHeight);

    var candles = state.candles;
    if (!candles.length) {
      ctx.fillStyle = TEXT;
      ctx.font = '14px system-ui, sans-serif';
      ctx.fillText('No candles loaded.', 16, 28);
      return;
    }

    var padLeft = 8;
    var padRight = 74;
    var padTop = 14;
    var volumeHeight = Math.round(cssHeight * 0.18);
    var axisHeight = 22;
    var priceBottom = cssHeight - axisHeight - volumeHeight - 8;
    var plotWidth = cssWidth - padLeft - padRight;

    // The visible window is chosen by the user through zoom and pan. It is
    // clamped to the series so panning can never scroll past the data and
    // present an empty chart as if the market had no prices.
    var maxBars = Math.max(MIN_BARS, Math.floor(plotWidth / 2));
    var barCount = Math.min(Math.max(MIN_BARS, Math.round(state.barCount)), Math.min(maxBars, candles.length));
    var maxOffset = Math.max(0, candles.length - barCount);
    var offset = Math.min(Math.max(0, Math.round(state.rightOffset)), maxOffset);

    state.barCount = barCount;
    state.rightOffset = offset;

    var end = candles.length - offset;
    var visible = candles.slice(Math.max(0, end - barCount), end);
    if (!visible.length) { return; }

    var high = -Infinity;
    var low = Infinity;
    var maxVolume = 0;
    visible.forEach(function (c) {
      high = Math.max(high, Number(c.high));
      low = Math.min(low, Number(c.low));
      maxVolume = Math.max(maxVolume, Number(c.volume));
    });

    // Entry lines must stay on screen, otherwise a position can look absent.
    relevantEntries().forEach(function (entry) {
      high = Math.max(high, entry.price);
      low = Math.min(low, entry.price);
    });

    if (!isFinite(high) || !isFinite(low)) { return; }
    if (high === low) { high = low + 1; }

    var span = high - low;
    var pad = span * 0.06;
    high += pad;
    low -= pad;
    span = high - low;

    function y(price) {
      return padTop + (high - Number(price)) / span * (priceBottom - padTop);
    }

    var slot = plotWidth / visible.length;
    var bodyWidth = Math.max(1, Math.min(14, slot * 0.68));

    // Price grid and right-hand scale.
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    var lines = 5;
    for (var i = 0; i <= lines; i++) {
      var price = low + span * (i / lines);
      var py = y(price);
      ctx.strokeStyle = GRID;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(padLeft, py + 0.5);
      ctx.lineTo(padLeft + plotWidth, py + 0.5);
      ctx.stroke();
      ctx.fillStyle = AXIS;
      ctx.textAlign = 'left';
      ctx.fillText(formatPrice(price), padLeft + plotWidth + 6, py);
    }

    // Candles.
    visible.forEach(function (c, index) {
      var cx = padLeft + slot * index + slot / 2;
      var open = Number(c.open);
      var close = Number(c.close);
      var rising = close >= open;
      var colour = c.isClosed ? (rising ? UP : DOWN) : FORMING;

      ctx.strokeStyle = colour;
      ctx.fillStyle = colour;
      ctx.lineWidth = 1;

      ctx.beginPath();
      ctx.moveTo(Math.round(cx) + 0.5, y(c.high));
      ctx.lineTo(Math.round(cx) + 0.5, y(c.low));
      ctx.stroke();

      var top = y(Math.max(open, close));
      var bottom = y(Math.min(open, close));
      var height = Math.max(1, bottom - top);
      var left = cx - bodyWidth / 2;

      if (c.isClosed) {
        ctx.fillRect(left, top, bodyWidth, height);
      } else {
        // Outlined, not filled: the bar is not final.
        ctx.setLineDash([2, 2]);
        ctx.strokeRect(left + 0.5, top + 0.5, bodyWidth - 1, height - 1);
        ctx.setLineDash([]);
      }

      // Volume.
      if (maxVolume > 0) {
        var vh = Number(c.volume) / maxVolume * (volumeHeight - 4);
        ctx.globalAlpha = c.isClosed ? 0.55 : 0.3;
        ctx.fillStyle = colour;
        ctx.fillRect(left, cssHeight - axisHeight - vh, bodyWidth, vh);
        ctx.globalAlpha = 1;
      }
    });

    // Entry and position levels.
    relevantEntries().forEach(function (entry) {
      var py = y(entry.price);
      if (py < padTop || py > priceBottom) { return; }

      ctx.strokeStyle = entry.colour;
      ctx.lineWidth = 1;
      ctx.setLineDash([5, 3]);
      ctx.beginPath();
      ctx.moveTo(padLeft, py + 0.5);
      ctx.lineTo(padLeft + plotWidth, py + 0.5);
      ctx.stroke();
      ctx.setLineDash([]);

      var label = entry.label + ' ' + formatPrice(entry.price);
      ctx.font = '11px system-ui, sans-serif';
      var width = ctx.measureText(label).width + 8;
      ctx.fillStyle = entry.colour;
      ctx.fillRect(padLeft, py - 8, width, 16);
      ctx.fillStyle = '#10131a';
      ctx.textAlign = 'left';
      ctx.fillText(label, padLeft + 4, py);
    });

    // Time axis: first, middle and last visible bar.
    ctx.fillStyle = AXIS;
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'alphabetic';
    [0, Math.floor(visible.length / 2), visible.length - 1].forEach(function (index, slotIndex) {
      var c = visible[index];
      if (!c) { return; }
      var cx = padLeft + slot * index + slot / 2;
      ctx.textAlign = slotIndex === 0 ? 'left' : slotIndex === 2 ? 'right' : 'center';
      ctx.fillText(formatTime(c.openTimeUtc), Math.min(Math.max(cx, padLeft), padLeft + plotWidth), cssHeight - 6);
    });

    var formingCount = visible.filter(function (c) { return !c.isClosed; }).length;
    $('legend').textContent =
      'Showing bars ' + (end - visible.length + 1) + '\u2013' + end + ' of ' + candles.length + '. ' +
      (formingCount
        ? formingCount + ' bar still forming, drawn dashed. It is not a finished candle and no closed-candle signal uses it.'
        : 'All bars shown are closed.');
  }

  function isShort(direction) {
    // The API reports the enum name, which is DirectionShort rather than
    // Short. Comparing against 'Short' alone silently drew every short
    // position with the long label and colour.
    var value = String(direction || '').toLowerCase();
    return value === 'short' || value === 'directionshort';
  }

  function relevantEntries() {
    var entries = [];
    var symbol = state.symbol.toUpperCase();

    state.positions.forEach(function (p) {
      if (String(p.symbol).toUpperCase() !== symbol) { return; }
      var short = isShort(p.direction);
      entries.push({
        price: Number(p.entryPrice),
        label: (short ? 'Short entry' : 'Long entry'),
        colour: short ? ENTRY_SHORT : ENTRY_LONG
      });

      // A stop and a target are drawn distinctly from the entry, because
      // mistaking a stop for an entry misreads the risk on the trade.
      if (p.stopLossPrice !== null && p.stopLossPrice !== undefined) {
        entries.push({ price: Number(p.stopLossPrice), label: 'Stop', colour: STOP });
      }

      if (p.takeProfitPrice !== null && p.takeProfitPrice !== undefined) {
        entries.push({ price: Number(p.takeProfitPrice), label: 'Target', colour: TARGET });
      }
    });

    state.orders.forEach(function (o) {
      if (String(o.symbol).toUpperCase() !== symbol) { return; }
      if (o.price === null || o.price === undefined) { return; }
      if (o.state === 'Filled' || o.state === 'Cancelled' || o.state === 'Rejected') { return; }
      entries.push({
        price: Number(o.price),
        label: 'Working ' + String(o.side).toLowerCase(),
        colour: o.side === 'Sell' ? ENTRY_SHORT : ENTRY_LONG
      });
    });

    return entries.filter(function (e) { return isFinite(e.price) && e.price > 0; });
  }

  function buildExitRow(position) {
    var tr = document.createElement('tr');
    var td = document.createElement('td');
    td.colSpan = 12;

    var wrap = document.createElement('div');
    wrap.className = 'toolbar';

    var label = document.createElement('span');
    label.textContent = 'Protective exits for ' + position.symbol + ':';
    wrap.appendChild(label);

    var stop = document.createElement('input');
    stop.type = 'number';
    stop.step = 'any';
    stop.min = '0';
    stop.placeholder = 'stop';
    stop.value = position.stopLossPrice === null || position.stopLossPrice === undefined
      ? '' : String(position.stopLossPrice);
    wrap.appendChild(stop);

    var target = document.createElement('input');
    target.type = 'number';
    target.step = 'any';
    target.min = '0';
    target.placeholder = 'target';
    target.value = position.takeProfitPrice === null || position.takeProfitPrice === undefined
      ? '' : String(position.takeProfitPrice);
    wrap.appendChild(target);

    var save = document.createElement('button');
    save.type = 'button';
    save.textContent = 'Save levels';
    save.addEventListener('click', async function () {
      save.disabled = true;

      try {
        // An empty box means no level, not zero. Sending zero would place a
        // stop that can never be reached and read as protection that is not
        // there.
        var body = {
          stopLossPrice: stop.value === '' ? null : Number(stop.value),
          takeProfitPrice: target.value === '' ? null : Number(target.value)
        };

        var response = await fetch('/api/paper/positions/' + encodeURIComponent(position.id) + '/exits', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
          body: JSON.stringify(body)
        });

        var payload = await response.json().catch(function () { return {}; });

        if (!response.ok) {
          // The domain's own wording is surfaced, because it explains why the
          // level was refused rather than just that it was.
          setStatus(payload.message || 'The levels were refused.', true);
          return;
        }

        setStatus('Levels saved. They are checked against closed candles only.', false);
        await load();
      } catch (error) {
        setStatus('The levels could not be saved. ' + error.message, true);
      } finally {
        save.disabled = false;
      }
    });
    wrap.appendChild(save);

    var close = document.createElement('button');
    close.type = 'button';
    close.textContent = 'Close position';
    close.className = 'danger';
    close.addEventListener('click', function () { closePosition(position, close); });
    wrap.appendChild(close);

    var note = document.createElement('span');
    note.className = 'empty';
    note.textContent = 'Checked on closed candles only. A candle that reaches both levels is settled as the stop.';
    wrap.appendChild(note);

    td.appendChild(wrap);
    tr.appendChild(td);
    return tr;
  }

  // Closing is an exposure-reducing order for the whole open quantity in the
  // opposite direction. It deliberately goes through the same submission path
  // as any other order rather than mutating the position directly, so a halt,
  // a stale price or a duplicate is judged by the same rules and the close
  // leaves an order and an audit trail behind like any other fill.
  async function closePosition(position, button) {
    var side = isShort(position.direction) ? 'Buy' : 'Sell';
    var live = tradingMode === 'Live';

    var confirmed = window.confirm(
      'Close the ' + (isShort(position.direction) ? 'short' : 'long') + ' position of ' +
      position.quantity + ' ' + position.symbol + '?\n\n' +
      'This submits a ' + side.toLowerCase() + ' order for the full quantity. ' +
      (live ? 'REAL money on a real exchange.' : 'Fake funds only.'));

    if (!confirmed) { return; }

    if (live) {
      button.disabled = true;
      try {
        await submitLiveTrade(position.symbol, side, position.quantity);
      } finally {
        button.disabled = false;
      }
      return;
    }

    button.disabled = true;
    setTradeStatus('Closing ' + position.symbol + '.', false);

    try {
      var response = await fetch('/api/paper/orders', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify({
          symbol: position.symbol,
          side: side,
          quantity: position.quantity,
          clientOrderId: null
        })
      });

      var payload = await readJsonResponse(response);

      if (!response.ok) {
        setTradeStatus(
          (payload && payload.error ? payload.error + ': ' : 'Refused: ') +
          (payload && payload.message ? payload.message : 'The position was not closed.'),
          true);
        return;
      }

      setTradeStatus(
        'Closed ' + payload.order.quantity + ' ' + payload.order.symbol +
        ' at ' + formatPrice(payload.order.fillPrice) + '. Fake funds only.',
        false);

      await load();
    } catch (error) {
      // The position is left exactly as it was. Reporting a close that may not
      // have happened would be worse than reporting nothing.
      setTradeStatus('The position could not be closed. ' + error.message, true);
    } finally {
      button.disabled = false;
    }
  }

  async function evaluateExits() {
    // Evaluation changes state, so it is a POST and never happens as a side
    // effect of simply reading the page.
    try {
      var response = await fetch('/api/paper/exits/evaluate', {
        method: 'POST',
        headers: { 'Accept': 'application/json' }
      });

      if (!response.ok) { return null; }
      return await response.json();
    } catch (error) {
      return null;
    }
  }

  function renderPositionTable() {
    var host = $('positions');
    host.textContent = '';

    var rows = state.positions.filter(function (p) {
      return String(p.symbol).toUpperCase() === state.symbol.toUpperCase();
    });

    if (!rows.length) {
      var p = document.createElement('p');
      p.className = 'empty';
      p.textContent = tradingMode === 'Paper'
        ? 'No open position on ' + state.symbol + '. Positions appear here once paper trading opens one.'
        : 'No open live position on ' + state.symbol + '. A live position appears only after the exchange reports a fill.';
      host.appendChild(p);
      return;
    }

    var table = document.createElement('table');
    var head = document.createElement('tr');
    ['Pair', 'Direction', 'Quantity', 'Entry', 'Last closed price', 'Unrealised', 'Return', 'Break even', 'Stop', 'Target', 'Priced at', 'Opened']
      .forEach(function (title) {
        var th = document.createElement('th');
        th.textContent = title;
        head.appendChild(th);
      });
    table.appendChild(head);

    rows.forEach(function (position) {
      var tr = document.createElement('tr');

      // Every money figure below is computed on the server in decimal and
      // shown as received. Nothing here recalculates a profit or loss, so the
      // page cannot disagree with the platform's own books.
      var unrealised = position.unrealisedPnl;
      var percent = position.unrealisedPercent;

      var cells = [
        position.symbol,
        isShort(position.direction) ? 'Short' : 'Long',
        position.quantity,
        formatPrice(position.entryPrice),
        position.markPrice === null || position.markPrice === undefined
          ? 'unavailable'
          : formatPrice(position.markPrice),
        unrealised === null || unrealised === undefined ? 'unknown' : formatSigned(unrealised),
        percent === null || percent === undefined ? 'unknown' : formatSigned(percent) + '%',
        formatPrice(position.breakEvenPrice),
        position.stopLossPrice === null || position.stopLossPrice === undefined
          ? 'none'
          : formatPrice(position.stopLossPrice),
        position.takeProfitPrice === null || position.takeProfitPrice === undefined
          ? 'none'
          : formatPrice(position.takeProfitPrice),
        position.pricedAtUtc ? formatTime(position.pricedAtUtc) : 'not priced',
        formatTime(position.openedAtUtc)
      ];

      cells.forEach(function (value, index) {
        var td = document.createElement('td');
        td.textContent = value === null || value === undefined ? '-' : String(value);

        // Colour only the two result columns, and only when a result is known.
        if ((index === 5 || index === 6) && unrealised !== null && unrealised !== undefined) {
          td.style.color = Number(unrealised) >= 0 ? '#3fbf6f' : '#ff6b6b';
        }

        // An unprotected position says so in grey rather than showing a blank
        // cell, which would read as though a level existed and was not shown.
        if ((index === 8 || index === 9) && value === 'none') {
          td.style.color = '#9aa0a6';
        }

        tr.appendChild(td);
      });

      table.appendChild(tr);
      table.appendChild(buildExitRow(position));
    });

    host.appendChild(table);

    // A stale or absent valuation is stated rather than left to look current.
    var warnings = rows.filter(function (r) { return r.priceIsStale || !r.markPrice; });
    if (warnings.length) {
      var warning = document.createElement('p');
      warning.className = 'notice error';
      warning.textContent = rows[0].priceUnavailableReason
        ? 'This position could not be priced: ' + rows[0].priceUnavailableReason
        : 'The price used to value this position is older than 30 minutes. Treat the result as out of date.';
      host.appendChild(warning);
    }

    var note = document.createElement('p');
    note.className = 'empty';
    note.textContent =
      'Valued at the close of the last closed candle. Fees, spread and slippage are not modelled, ' +
      'so break even is the raw entry price and the unrealised figure is an upper bound. ' +
      'A planned exit price is not part of a position yet.';
    host.appendChild(note);
  }

  function renderActiveOrderTable() {
    var host = $('activeOrders');
    host.textContent = '';

    var activeOrders = state.orders.filter(function (order) {
      if (String(order.symbol).toUpperCase() !== state.symbol.toUpperCase()) {
        return false;
      }

      return order.state !== 'Filled'
        && order.state !== 'Cancelled'
        && order.state !== 'Rejected';
    });

    if (!activeOrders.length) {
      var empty = document.createElement('p');
      empty.className = 'empty';
      empty.textContent = 'No active ' + tradingMode.toLowerCase() + ' orders on ' + state.symbol + '.';
      host.appendChild(empty);
      return;
    }

    var table = document.createElement('table');
    var header = document.createElement('tr');
    ['State', 'Side', 'Quantity', 'Filled', 'Limit price', 'Submitted', 'Reconciliation']
      .forEach(function (title) {
        var th = document.createElement('th');
        th.textContent = title;
        header.appendChild(th);
      });
    table.appendChild(header);

    activeOrders.forEach(function (order) {
      var row = document.createElement('tr');
      var stateLabel = order.state === 'Accepted'
        ? 'Accepted - working'
        : order.state;
      var values = [
        stateLabel,
        order.side,
        order.quantity,
        order.filledQuantity || 0,
        order.price === null || order.price === undefined ? 'unavailable' : formatPrice(order.price),
        order.createdAtUtc ? formatTime(order.createdAtUtc) : 'unknown',
        order.requiresReconciliation ? 'Required - do not resubmit' : 'Current'
      ];

      values.forEach(function (value, index) {
        var td = document.createElement('td');
        td.textContent = String(value);
        if (index >= 2 && index <= 4) {
          td.className = 'numeric';
        }
        if (index === 0 && order.state === 'Failed') {
          td.style.color = '#ffb4b4';
        }
        row.appendChild(td);
      });
      table.appendChild(row);
    });

    host.appendChild(table);

    var note = document.createElement('p');
    note.className = 'empty';
    note.textContent = tradingMode === 'Live'
      ? 'Accepted means Kraken accepted the order request. It is confirmed only when Kraken reports a fill and it appears above as a position.'
      : 'Paper orders are simulated and appear as positions once their simulated fill is recorded.';
    host.appendChild(note);
  }

  async function loadPairs() {
    var select = $('symbol');

    try {
      var response = await fetch('/api/marketdata/pairs', { headers: { 'Accept': 'application/json' } });
      var payload = await readJsonResponse(response);

      if (!response.ok) {
        setStatus(payload && payload.message ? payload.message : 'The pair list could not be loaded.', true);
        return false;
      }

      function replacePairOptions() {
        var select = $('symbol');
        var previous = select.value;

        select.textContent = '';
        state.pairs.forEach(function (pair) {
          var option = document.createElement('option');
          option.value = pair.symbol;
          option.textContent = pair.displayName;
          select.appendChild(option);
        });

        if (state.pairs.some(function (pair) { return pair.symbol === previous; })) {
          select.value = previous;
        }
      }

      async function refreshPairsForSearch(query, generation) {
        try {
          var response = await fetch('/api/marketdata/pairs', { headers: { 'Accept': 'application/json' } });
          var payload = await response.json();

          if (!response.ok || generation !== pairSearchGeneration) {
            return;
          }

          state.pairs = payload.filter(function (pair) { return pair.isActive; });
          replacePairOptions();
          renderPairSearch(query);
        } catch (error) {
          // Keep the last known active catalogue visible. A temporary public-data
          // outage must not make the already selected trading pair disappear.
          if (generation === pairSearchGeneration) {
            renderPairSearch(query);
          }
        }
      }

      function schedulePairSearch(query) {
        renderPairSearch(query);

        if (pairSearchTimer !== null) {
          window.clearTimeout(pairSearchTimer);
        }

        var generation = ++pairSearchGeneration;
        pairSearchTimer = window.setTimeout(function () {
          refreshPairsForSearch(query, generation);
        }, 250);
      }

      // Only pairs the venue is actually accepting orders on are offered.
      // Listing a delisted pair would let someone build a position they cannot
      // trade out of.
      state.pairs = payload.filter(function (p) { return p.isActive; });

      select.textContent = '';
      state.pairs.forEach(function (pair) {
        var option = document.createElement('option');
        option.value = pair.symbol;
        // textContent, so a venue-supplied name is never parsed as markup.
        option.textContent = pair.displayName;
        select.appendChild(option);
      });

      // A pair may be requested by link, for example from the positions page.
      // It is honoured only if the venue actually lists it, so a hand-edited
      // query string cannot select a pair that is not tradable.
      var requested = new URLSearchParams(window.location.search).get('symbol');
      var requestedMatch = requested
        ? state.pairs.filter(function (p) { return p.symbol === requested.toUpperCase(); })
        : [];

      if (requestedMatch.length) {
        select.value = requestedMatch[0].symbol;
        setPairSearchValue(requestedMatch[0]);
        return true;
      }

      var preferred = state.pairs.filter(function (p) { return p.symbol === 'XBTUSD'; });
      select.value = preferred.length ? 'XBTUSD' : (state.pairs.length ? state.pairs[0].symbol : '');
      setPairSearchValue(selectedPair());
      return state.pairs.length > 0;
    } catch (error) {
      setStatus('The pair list could not be loaded. ' + error.message, true);
      return false;
    }

    function selectedPair() {
      var selected = $('symbol').value;
      var matches = state.pairs.filter(function (pair) { return pair.symbol === selected; });
      return matches.length ? matches[0] : null;
    }

    function setPairSearchValue(pair) {
      if (!pair) { return; }
      $('pairSearch').value = pair.displayName + ' (' + pair.symbol + ')';
    }

    function clearPairResults() {
      var host = $('pairResults');
      host.textContent = '';
      host.classList.remove('has-results');
      var empty = $('pairSearchEmpty');
      if (empty) {
        empty.textContent = '';
        empty.classList.remove('has-message');
      }
    }

    function renderPairSearch(query) {
      var host = $('pairResults');
      var needle = String(query || '').trim().toUpperCase();
      var matches = state.pairs.filter(function (pair) {
        if (!needle) { return true; }
        return pair.symbol.toUpperCase().indexOf(needle) !== -1 ||
          pair.displayName.toUpperCase().indexOf(needle) !== -1 ||
          pair.baseAsset.toUpperCase().indexOf(needle) !== -1 ||
          pair.quoteAsset.toUpperCase().indexOf(needle) !== -1;
      }).slice(0, 8);

      host.textContent = '';

      matches.forEach(function (pair) {
        var option = document.createElement('button');
        option.type = 'button';
        option.className = 'pair-result';
        option.setAttribute('role', 'option');
        option.setAttribute('aria-selected', String(pair.symbol === $('symbol').value));
        option.textContent = pair.displayName;

        var detail = document.createElement('small');
        detail.textContent = pair.symbol + ' · ' + pair.baseAsset + '/' + pair.quoteAsset;
        option.appendChild(detail);

        option.addEventListener('click', function () {
          $('symbol').value = pair.symbol;
          setPairSearchValue(pair);
          clearPairResults();
          load();
        });

        host.appendChild(option);
      });

      host.classList.toggle('has-results', matches.length > 0);
        var empty = $('pairSearchEmpty');
        if (empty) {
          empty.textContent = needle && !matches.length
            ? 'No active Kraken pair matches "' + String(query).trim() + '".'
            : '';
          empty.classList.toggle('has-message', Boolean(needle && !matches.length));
        }
    }
  }

  function setPairSearchValue(pair) {
    if (!pair) { return; }
    $('pairSearch').value = pair.displayName + ' (' + pair.symbol + ')';
  }

  function replacePairOptions() {
    var select = $('symbol');
    var previous = select.value;

    select.textContent = '';
    state.pairs.forEach(function (pair) {
      var option = document.createElement('option');
      option.value = pair.symbol;
      option.textContent = pair.displayName;
      select.appendChild(option);
    });

    if (state.pairs.some(function (pair) { return pair.symbol === previous; })) {
      select.value = previous;
    }
  }

  function clearPairResults() {
    var host = $('pairResults');
    host.textContent = '';
    host.classList.remove('has-results');
    var empty = $('pairSearchEmpty');
    empty.textContent = '';
    empty.classList.remove('has-message');
  }

  function renderPairSearch(query) {
    var host = $('pairResults');
    var needle = String(query || '').trim().toUpperCase();
    var matches = state.pairs.filter(function (pair) {
      if (!needle) { return true; }
      return pair.symbol.toUpperCase().indexOf(needle) !== -1 ||
        pair.displayName.toUpperCase().indexOf(needle) !== -1 ||
        pair.baseAsset.toUpperCase().indexOf(needle) !== -1 ||
        pair.quoteAsset.toUpperCase().indexOf(needle) !== -1;
    }).slice(0, 8);

    host.textContent = '';

    matches.forEach(function (pair) {
      var option = document.createElement('button');
      option.type = 'button';
      option.className = 'pair-result';
      option.setAttribute('role', 'option');
      option.setAttribute('aria-selected', String(pair.symbol === $('symbol').value));
      option.textContent = pair.displayName;

      var detail = document.createElement('small');
      detail.textContent = pair.symbol + ' · ' + pair.baseAsset + '/' + pair.quoteAsset;
      option.appendChild(detail);
      option.addEventListener('click', function () {
        $('symbol').value = pair.symbol;
        setPairSearchValue(pair);
        clearPairResults();
        load();
      });

      host.appendChild(option);
    });

    host.classList.toggle('has-results', matches.length > 0);
    var empty = $('pairSearchEmpty');
    empty.textContent = needle && !matches.length
      ? 'No active Kraken pair matches "' + String(query).trim() + '".'
      : '';
    empty.classList.toggle('has-message', Boolean(needle && !matches.length));
  }

  async function refreshPairsForSearch(query, generation) {
    try {
      var response = await fetch('/api/marketdata/pairs', { headers: { 'Accept': 'application/json' } });
      var payload = await response.json();

      if (!response.ok || generation !== pairSearchGeneration) {
        return;
      }

      state.pairs = payload.filter(function (pair) { return pair.isActive; });
      replacePairOptions();
      renderPairSearch(query);
    } catch (error) {
      // Keep the last known active catalogue visible. A temporary public-data
      // outage must not make the already selected trading pair disappear.
      if (generation === pairSearchGeneration) {
        renderPairSearch(query);
      }
    }
  }

  function schedulePairSearch(query) {
    renderPairSearch(query);

    if (pairSearchTimer !== null) {
      window.clearTimeout(pairSearchTimer);
    }

    var generation = ++pairSearchGeneration;
    pairSearchTimer = window.setTimeout(function () {
      refreshPairsForSearch(query, generation);
    }, 250);
  }

  function normalizeAsset(asset) {
    var code = String(asset || '').toUpperCase();
    if (code === 'XBT' || code === 'XXBT') { return 'BTC'; }
    if (code === 'XDG') { return 'DOGE'; }
    return code.length === 4 && (code.charAt(0) === 'X' || code.charAt(0) === 'Z')
      ? code.slice(1) : code;
  }

  async function loadPairHolding() {
    var host = $('pairHolding');
    var pair = state.pairs.filter(function (item) { return item.symbol === state.symbol; })[0];
    if (!pair) {
      host.textContent = 'Current exchange holding is unavailable because the selected pair is unknown.';
      host.className = 'pair-balance-value error';
      return;
    }

    var target = normalizeAsset(pair.baseAsset);
    host.textContent = 'Loading current ' + target + ' holding from the exchange.';
    host.className = 'pair-balance-value';

    try {
      var response = await fetch('/api/portfolio', { headers: { 'Accept': 'application/json' } });
      var payload = await readJsonResponse(response);
      if (!response.ok) {
        throw new Error('The current holding could not be read.');
      }

      var messages = [];
      var hasUnavailableAccount = false;
      (payload.accounts || []).forEach(function (account) {
        if (account.error) {
          hasUnavailableAccount = true;
          messages.push(account.displayName + ': current balance unavailable');
          return;
        }

        var balance = (account.balances || []).filter(function (item) {
          return normalizeAsset(item.asset) === target;
        })[0];
        var total = balance ? balance.total : 0;
        var available = balance ? balance.available : 0;
        var held = balance ? balance.held : 0;
        messages.push(
          account.displayName + ': ' + total + ' ' + target +
          ' total (' + available + ' available, ' + held + ' held; read ' +
          formatTime(account.retrievedAtUtc) + ')');
      });

      host.textContent = messages.length
        ? 'Current ' + target + ' exchange holding: ' + messages.join(' | ')
        : 'No connected exchange account is available for a current holding reading.';
      host.className = hasUnavailableAccount ? 'pair-balance-value error' : 'pair-balance-value';
    } catch (error) {
      host.textContent = 'Current exchange holding could not be read. No previous balance is shown.';
      host.className = 'pair-balance-value error';
    }
  }

  function renderPairFilters() {
    var host = $('pairFilters');
    var match = state.pairs.filter(function (p) { return p.symbol === state.symbol; });

    if (!match.length) {
      host.textContent = '';
      return;
    }

    var pair = match[0];
    host.textContent =
      'Kraken order rules for ' + pair.displayName + ': minimum ' + pair.minimumQuantity + ' ' +
      pair.baseAsset + ', quantity step ' + pair.quantityStep + ', price tick ' + pair.priceTick +
      '. These are the venue\u2019s own filters; an order breaking them would be rejected.';
  }

  async function load() {
    var symbol = $('symbol').value.trim();
    var interval = $('interval').value;

    if (!symbol) {
      setStatus('Select a pair.', true);
      return;
    }

    setStatus('Loading ' + symbol + ' ' + interval + '.', false);
    $('load').disabled = true;

    try {
      var response = await fetch(
        '/api/marketdata/candles?symbol=' + encodeURIComponent(symbol) +
        '&interval=' + encodeURIComponent(interval),
        { headers: { 'Accept': 'application/json' } });

      var payload = await response.json();

      if (!response.ok) {
        // The venue's reason is shown rather than an empty chart, because
        // "no data" and "the request failed" mean different things.
        setStatus(payload && payload.message ? payload.message : 'Candles could not be loaded.', true);
        state.candles = [];
        draw();
        return;
      }

      var switchedPair = state.symbol !== symbol;

      state.candles = payload;
      state.symbol = symbol;
      state.interval = interval;

      // A new pair or interval starts at the most recent bars. Keeping the old
      // window would show a different market at a scroll position chosen for
      // the previous one.
      if (switchedPair || state.interval !== interval) {
        state.rightOffset = 0;
      }

      // Positions come from the valuation route so the profit and loss figures
      // are the server's decimal results, not numbers derived in the browser.
      //
      // Exits are evaluated first. Reading a position before checking its stop
      // would show a trade as open that a closed candle already ended.
      var exits = tradingMode === 'Paper' ? await evaluateExits() : null;

      if (tradingMode === 'Paper') {
        var valued = await fetch('/api/paper/positions', { headers: { 'Accept': 'application/json' } });
        state.positions = valued.ok ? ((await valued.json()).positions || []) : [];
      } else {
        // The live book is read from its own route, which asks the exchange
        // first. It is filtered to live positions server-side, so a paper
        // position can never appear here as though it were real.
        var liveValued = await fetch('/api/live/positions', { headers: { 'Accept': 'application/json' } });
        state.positions = liveValued.ok ? ((await liveValued.json()).positions || []) : [];
      }

      var ordersUrl = tradingMode === 'Live'
        ? '/api/live/orders'
        : '/api/orders?mode=' + encodeURIComponent(tradingMode);

      var orders = await fetch(ordersUrl, { headers: { 'Accept': 'application/json' } });
      state.orders = orders.ok ? ((await orders.json()).orders || []) : [];

      var message = tradingMode + ' trading. ' + payload.length + ' bars of ' + symbol + ' loaded from Kraken.';

      if (exits && exits.closed) {
        message += ' ' + exits.closed + ' position closed by a stop or target.';

        var ambiguous = (exits.fills || []).filter(function (f) { return f.bothLevelsTouched; });
        if (ambiguous.length) {
          message += ' ' + ambiguous.length + ' reached both levels inside one candle; the order of ' +
            'events cannot be recovered from a candle, so the stop was taken.';
        }
      }

      setStatus(message, false);

      draw();
      renderPositionTable();
      renderActiveOrderTable();
      renderPairFilters();
      await loadPairHolding();
    } catch (error) {
      setStatus('Candles could not be loaded. ' + error.message, true);
    } finally {
      $('load').disabled = false;
    }
  }

  // The server refuses an order when no exchange account is connected. The
  // ticket mirrors that here so the refusal is visible before a click rather
  // than after one. This is presentation only: the server check is the control.
  async function refreshExchangeConnection() {
    var button = $('submitTrade');

    try {
      var response = await fetch('/api/exchange/accounts', { headers: { 'Accept': 'application/json' } });
      if (!response.ok) {
        throw new Error('Connected accounts could not be read.');
      }

      var accounts = await response.json();
      var connected = Array.isArray(accounts) && accounts.some(function (account) { return account.canTrade; });

      button.disabled = !connected;

      if (!connected) {
        setTradeStatus(
          'Connect a Kraken account before trading. Orders stay simulated with fake funds either way.',
          true);
      }

      return connected;
    } catch (error) {
      // Failing closed: if the connection state cannot be read, the ticket
      // stays shut rather than inviting an order the server will refuse.
      button.disabled = true;
      setTradeStatus('Exchange connection could not be checked. ' + error.message, true);
      return false;
    }
  }

  // The selected book. The chart never changes with it: candles are the same
  // market data whichever book you trade into, and drawing them twice would
  // invite the two tabs to disagree about what the market did.
  var tradingMode = 'Paper';
  var modeCapability = null;

  function liveAccount() {
    if (!modeCapability || !modeCapability.accounts) { return null; }

    // The account must be able to reach the exchange and must have been
    // promoted out of paper. Picking any connected account would route a real
    // order to whichever one happened to come first.
    for (var i = 0; i < modeCapability.accounts.length; i++) {
      var account = modeCapability.accounts[i];
      if (account.canReachExchange && account.stage !== 'Paper') { return account; }
    }

    return null;
  }

  function applyMode() {
    var paper = tradingMode === 'Paper';

    $('modePaper').className = paper ? 'tab active' : 'tab';
    $('modeLive').className = paper ? 'tab' : 'tab active';

    var notice = $('modeNotice');
    var ticket = $('tradeTicket');
    var submit = $('submitTrade');

    if (paper) {
      notice.className = 'notice';
      notice.innerHTML = '<strong>Fake funds. No exchange is contacted.</strong> ' +
        'The fill is priced at the close of the last closed candle. It does not model spread, ' +
        'slippage, fees or partial fills, so a paper result is an upper bound on what the same ' +
        'decision would have returned live.';
      ticket.style.display = '';
      submit.textContent = 'Submit paper order';
      return;
    }

    var account = liveAccount();
    var available = modeCapability && modeCapability.live && modeCapability.live.available && account;

    if (!available) {
      notice.className = 'notice error';
      notice.innerHTML = '<strong>Live trading is not available.</strong> ' +
        (modeCapability && modeCapability.live && modeCapability.live.reason
          ? escapeHtml(modeCapability.live.reason)
          : 'No connected account has been promoted out of paper.');

      // The ticket is removed rather than disabled. A greyed-out live ticket
      // reads as "one setting away", which would understate what promotion
      // means.
      ticket.style.display = 'none';
      return;
    }

    notice.className = 'notice error';
    notice.innerHTML = '<strong>Real money. Orders go to ' + escapeHtml(account.exchange) + '.</strong> ' +
      'Trading through <em>' + escapeHtml(account.displayName) + '</em> at the ' +
      escapeHtml(account.stage) + ' stage. Orders are sent as limit orders priced at the last ' +
      'closed candle. Acceptance by the exchange is not a fill, and no strategy is guaranteed ' +
      'to be profitable.';

    ticket.style.display = '';
    submit.textContent = 'Submit LIVE order';
  }

  function escapeHtml(value) {
    var div = document.createElement('div');
    div.textContent = String(value);
    return div.innerHTML;
  }

  async function selectMode(mode) {
    tradingMode = mode;
    applyMode();
    await load();
  }

  async function refreshModeCapability() {
    try {
      var response = await fetch('/api/trading/modes', { headers: { 'Accept': 'application/json' } });
      if (response.ok) { modeCapability = await response.json(); }
    } catch (error) {
      modeCapability = null;
    }

    applyMode();
  }

  function setTradeStatus(message, isError) {    var element = $('tradeStatus');
    element.textContent = message;
    element.className = isError ? 'notice error' : 'notice';
  }

  async function submitTrade() {
    var symbol = $('symbol').value.trim();
    var side = $('tradeSide').value;
    var quantity = Number($('tradeQuantity').value);

    if (!symbol) {
      setTradeStatus('Enter a pair first.', true);
      return;
    }

    if (!isFinite(quantity) || quantity <= 0) {
      setTradeStatus('Quantity must be a positive number.', true);
      return;
    }

    if (tradingMode === 'Live') {
      await submitLiveTrade(symbol, side, quantity);
      return;
    }

    setTradeStatus('Submitting paper order.', false);
    $('submitTrade').disabled = true;

    try {
      var response = await fetch('/api/paper/orders', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        // No client order id is sent, so each click is a distinct intent. A
        // reused identifier would be treated as a repeat of the earlier order
        // and refused.
        body: JSON.stringify({ symbol: symbol, side: side, quantity: quantity, clientOrderId: null })
      });

      var payload = await response.json();

      if (!response.ok) {
        // The refusal reason is shown verbatim so the user can tell a halt
        // from stale market data from a rejected size.
        setTradeStatus(
          (payload && payload.error ? payload.error + ': ' : 'Refused: ') +
          (payload && payload.message ? payload.message : 'The paper order was not accepted.'),
          true);
        return;
      }

      setTradeStatus(
        'Paper order ' + payload.order.state.toLowerCase() + ': ' +
        payload.order.side + ' ' + payload.order.quantity + ' ' + payload.order.symbol +
        ' at ' + formatPrice(payload.order.fillPrice) + '. Fake funds only.',
        false);

      // Reload so the new entry line and position row are drawn from stored
      // state rather than from the response, which would show a position the
      // server may not actually hold.
      await load();
    } catch (error) {
      setTradeStatus('The paper order could not be submitted. ' + error.message, true);
    } finally {
      // Re-read rather than blindly re-enabling: a connection that was revoked
      // mid-session must not leave an enabled ticket behind.
      await refreshExchangeConnection();
    }
  }

  // Sends a real order with the user's own money.
  //
  // Two things here are deliberate. The confirmation names the amount and the
  // venue, because a live click must not feel like a paper click. And a 202
  // answer is treated as neither success nor failure: it means the platform
  // does not know, and the one thing the user must not do is click again.
  async function submitLiveTrade(symbol, side, quantity) {
    var account = liveAccount();

    if (!account) {
      setTradeStatus('No promoted account can reach the exchange.', true);
      return;
    }

    var confirmed = window.confirm(
      'Send a REAL ' + side.toLowerCase() + ' order for ' + quantity + ' ' + symbol +
      ' to ' + account.exchange + '?\n\n' +
      'This uses your own funds through "' + account.displayName + '".\n' +
      'It is sent as a limit order priced at the last closed candle and may not fill.');

    if (!confirmed) { return; }

    setTradeStatus('Sending live order to ' + account.exchange + '.', false);
    $('submitTrade').disabled = true;

    try {
      var response = await fetch('/api/live/orders', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify({
          exchangeAccountId: account.id,
          symbol: symbol,
          side: side,
          quantity: quantity,
          clientOrderId: null
        })
      });

      var payload = await response.json();

      if (response.status === 202) {
        setTradeStatus(
          'UNKNOWN: ' + (payload && payload.message ? payload.message : '') +
          ' Do not submit this order again. It is being reconciled with the exchange.',
          true);
        await load();
        return;
      }

      if (!response.ok) {
        setTradeStatus(
          (payload && payload.error ? payload.error + ': ' : 'Refused: ') +
          (payload && payload.message ? payload.message : 'The live order was not accepted.'),
          true);
        return;
      }

      setTradeStatus(
        'Live order accepted by ' + account.exchange + ': ' +
        payload.order.side + ' ' + payload.order.quantity + ' ' + payload.order.symbol +
        ' at limit ' + formatPrice(payload.order.limitPrice) +
        '. Accepted is not filled.',
        false);

      await load();
    } catch (error) {
      // The order may have reached the exchange. Saying it failed would be a
      // claim the browser cannot support.
      setTradeStatus(
        'The answer did not arrive, so this order may or may not exist. ' +
        'Do not submit it again; check the orders list. ' + error.message,
        true);
    } finally {
      await refreshExchangeConnection();
    }
  }

  function zoom(factor, anchorRatio) {
    var before = state.barCount;
    var next = Math.round(before * factor);

    if (next === before) {
      next = before + (factor < 1 ? -1 : 1);
    }

    state.barCount = Math.max(MIN_BARS, next);

    // Keep the bar under the pointer roughly in place. Without this the chart
    // jumps to a different part of the series on every wheel step, which makes
    // it hard to follow a level you were looking at.
    if (typeof anchorRatio === 'number') {
      var delta = state.barCount - before;
      state.rightOffset = Math.max(0, Math.round(state.rightOffset - delta * (1 - anchorRatio)));
    }

    draw();
  }

  function attachChartInteraction() {
    var canvas = $('chart');

    canvas.addEventListener('wheel', function (event) {
      if (!state.candles.length) { return; }
      event.preventDefault();

      var rect = canvas.getBoundingClientRect();
      var ratio = rect.width ? (event.clientX - rect.left) / rect.width : 1;
      zoom(event.deltaY > 0 ? 1.2 : 1 / 1.2, Math.min(Math.max(ratio, 0), 1));
    }, { passive: false });

    var dragging = false;
    var dragStartX = 0;
    var dragStartOffset = 0;

    canvas.addEventListener('pointerdown', function (event) {
      if (!state.candles.length) { return; }
      dragging = true;
      dragStartX = event.clientX;
      dragStartOffset = state.rightOffset;
      canvas.setPointerCapture(event.pointerId);
    });

    canvas.addEventListener('pointermove', function (event) {
      if (!dragging) { return; }

      var rect = canvas.getBoundingClientRect();
      var barsPerPixel = rect.width ? state.barCount / rect.width : 0;
      // Dragging right moves back in time, which is the direction every other
      // charting tool uses.
      state.rightOffset = Math.max(0, Math.round(dragStartOffset + (event.clientX - dragStartX) * barsPerPixel));
      draw();
    });

    function endDrag(event) {
      if (!dragging) { return; }
      dragging = false;
      if (canvas.hasPointerCapture(event.pointerId)) {
        canvas.releasePointerCapture(event.pointerId);
      }
    }

    canvas.addEventListener('pointerup', endDrag);
    canvas.addEventListener('pointercancel', endDrag);
  }

  window.addEventListener('DOMContentLoaded', async function () {
    $('load').addEventListener('click', load);
    $('interval').addEventListener('change', load);
    $('symbol').addEventListener('change', load);
    $('pairSearch').addEventListener('focus', function () {
      renderPairSearch($('pairSearch').value);
    });
    $('pairSearch').addEventListener('input', function () {
      schedulePairSearch($('pairSearch').value);
    });
    $('pairSearch').addEventListener('keydown', function (event) {
      if (event.key === 'Escape') {
        clearPairResults();
        $('pairSearch').blur();
        return;
      }

      if (event.key === 'Enter') {
        var first = $('pairResults').querySelector('.pair-result');
        if (first) {
          event.preventDefault();
          first.click();
        }
      }
    });
    $('pairSearch').addEventListener('blur', function () {
      // Let a result receive its click before the list is removed.
      window.setTimeout(clearPairResults, 150);
    });
    $('submitTrade').addEventListener('click', submitTrade);
    $('modePaper').addEventListener('click', function () { selectMode('Paper'); });
    $('modeLive').addEventListener('click', function () { selectMode('Live'); });

    $('zoomIn').addEventListener('click', function () { zoom(1 / 1.4); });
    $('zoomOut').addEventListener('click', function () { zoom(1.4); });
    $('zoomReset').addEventListener('click', function () {
      state.barCount = 160;
      state.rightOffset = 0;
      draw();
    });

    attachChartInteraction();

    // Whether an exchange is connected decides whether the ticket is usable at
    // all, so it is resolved before the first candle is drawn.
    await refreshExchangeConnection();
    await refreshModeCapability();

    // The pair list has to arrive before the first load, otherwise there is no
    // symbol to request.
    var ready = await loadPairs();
    if (ready) { load(); }
  });

  window.addEventListener('resize', function () { draw(); });
})();
