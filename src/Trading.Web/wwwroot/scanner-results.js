// Scanner evidence is server-computed. This page only presents it and writes
// every dynamic value with textContent.
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function setStatus(message, error) {
    var status = $('status');
    status.textContent = message;
    status.className = error ? 'notice error' : 'notice';
  }

  function utc(value) {
    return new Date(value).toISOString().replace('.000Z', ' UTC');
  }

  function textCell(row, value, className) {
    var cell = document.createElement('td');
    cell.textContent = String(value);
    if (className) { cell.className = className; }
    row.appendChild(cell);
  }

  function criteriaText(criteria) {
    return (criteria || []).map(function (criterion) {
      return criterion.label + ': ' + criterion.status + ' — ' + criterion.rationale;
    }).join('; ');
  }

  function render(page) {
    $('scanName').textContent = page.scanName;
    $('scope').textContent = (page.symbolScope || []).join(', ');
    $('interval').textContent = page.interval;

    var host = $('results');
    host.textContent = '';
    if (!(page.results || []).length) {
      var empty = document.createElement('p');
      empty.className = 'empty';
      empty.textContent = 'This scanner has no eligible closed-candle results yet.';
      host.appendChild(empty);
      return;
    }

    var table = document.createElement('table');
    var header = document.createElement('tr');
    ['Rank', 'Symbol', 'Score', 'As of UTC', 'Criteria rationale', 'Data rejection reason']
      .forEach(function (label) {
        var th = document.createElement('th');
        th.textContent = label;
        header.appendChild(th);
      });
    table.appendChild(header);

    (page.results || []).forEach(function (result) {
      var row = document.createElement('tr');
      textCell(row, result.rank, 'numeric');
      textCell(row, result.symbol);
      textCell(row, result.score, 'numeric');
      textCell(row, utc(result.evidenceAsOfUtc));
      textCell(row, criteriaText(result.criteria));
      textCell(row, result.dataRejectionReason || 'Not recorded.');
      table.appendChild(row);
    });
    host.appendChild(table);
  }

  async function load() {
    var query = new URLSearchParams(window.location.search);
    if (!query.get('scanRequestId') || !query.get('scanRunId')) {
      setStatus('Choose a completed scanner result link to view its read-only evidence.', false);
      return;
    }

    setStatus('Loading scanner evidence.', false);
    try {
      var response = await fetch('/api/scanner/results?' + query.toString(), {
        headers: { 'Accept': 'application/json' }
      });
      if (!response.ok) {
        setStatus(response.status === 404
          ? 'Scanner results are unavailable.'
          : 'Scanner results could not be loaded.', true);
        return;
      }

      render(await response.json());
      setStatus('Read-only scanner evidence. Times are UTC.', false);
    } catch (error) {
      setStatus('Scanner results could not be loaded.', true);
    }
  }

  window.addEventListener('DOMContentLoaded', load);
})();
