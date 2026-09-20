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

  var state = {
    candles: [],
    positions: [],
    orders: [],
    symbol: '',
    interval: ''
  };

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

  function formatTime(iso) {
    var d = new Date(iso);
    // The user's own locale and zone. Times are transported as UTC; only the
    // presentation is localised.
    return d.toLocaleString();
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

    // Show the most recent bars that fit rather than squeezing the whole
    // series into unreadable slivers.
    var minSlot = 3;
    var maxBars = Math.max(10, Math.floor(plotWidth / minSlot));
    var visible = candles.slice(Math.max(0, candles.length - maxBars));

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
      visible.length + ' of ' + candles.length + ' bars shown. ' +
      (formingCount
        ? formingCount + ' bar still forming, drawn dashed. It is not a finished candle and no closed-candle signal uses it.'
        : 'All bars shown are closed.');
  }

  function relevantEntries() {
    var entries = [];
    var symbol = state.symbol.toUpperCase();

    state.positions.forEach(function (p) {
      if (String(p.symbol).toUpperCase() !== symbol) { return; }
      entries.push({
        price: Number(p.entryPrice),
        label: (p.direction === 'Short' ? 'Short entry' : 'Long entry'),
        colour: p.direction === 'Short' ? ENTRY_SHORT : ENTRY_LONG
      });
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

  function renderPositionTable() {
    var host = $('positions');
    host.textContent = '';

    var rows = state.positions.filter(function (p) {
      return String(p.symbol).toUpperCase() === state.symbol.toUpperCase();
    });

    if (!rows.length) {
      var p = document.createElement('p');
      p.className = 'empty';
      p.textContent = 'No open position on ' + state.symbol + '. Positions appear here once paper trading opens one.';
      host.appendChild(p);
      return;
    }

    var table = document.createElement('table');
    var head = document.createElement('tr');
    ['Symbol', 'Direction', 'Quantity', 'Entry', 'Mark', 'Unrealized', 'Opened'].forEach(function (title) {
      var th = document.createElement('th');
      th.textContent = title;
      head.appendChild(th);
    });
    table.appendChild(head);

    rows.forEach(function (position) {
      var tr = document.createElement('tr');
      [
        position.symbol,
        position.direction,
        position.quantity,
        formatPrice(position.entryPrice),
        position.markPrice === null ? '-' : formatPrice(position.markPrice),
        position.unrealizedPnl === null ? '-' : formatPrice(position.unrealizedPnl),
        formatTime(position.openedAtUtc)
      ].forEach(function (value) {
        var td = document.createElement('td');
        // textContent everywhere: no venue or user string is ever parsed as markup.
        td.textContent = value === null || value === undefined ? '-' : String(value);
        tr.appendChild(td);
      });
      table.appendChild(tr);
    });

    host.appendChild(table);
  }

  async function load() {
    var symbol = $('symbol').value.trim();
    var interval = $('interval').value;

    if (!symbol) {
      setStatus('Enter a pair, for example XBTUSD.', true);
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

      state.candles = payload;
      state.symbol = symbol;
      state.interval = interval;

      var orders = await fetch('/api/orders', { headers: { 'Accept': 'application/json' } });
      if (orders.ok) {
        var book = await orders.json();
        state.positions = book.positions || [];
        state.orders = book.orders || [];
      } else {
        state.positions = [];
        state.orders = [];
      }

      setStatus(
        'Paper trading. ' + payload.length + ' bars of ' + symbol + ' loaded from Kraken. ' +
        'Nothing on this page places an order.',
        false);

      draw();
      renderPositionTable();
    } catch (error) {
      setStatus('Candles could not be loaded. ' + error.message, true);
    } finally {
      $('load').disabled = false;
    }
  }

  window.addEventListener('DOMContentLoaded', function () {
    $('load').addEventListener('click', load);
    $('symbol').addEventListener('keydown', function (event) {
      if (event.key === 'Enter') { load(); }
    });
    $('interval').addEventListener('change', load);
    load();
  });

  window.addEventListener('resize', function () { draw(); });
})();
