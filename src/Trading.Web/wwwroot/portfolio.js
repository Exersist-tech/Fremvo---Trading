(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function formatTime(value) {
    return new Date(value).toLocaleString();
  }

  function setStatus(message, error) {
    var status = $('status');
    status.textContent = message;
    status.className = error ? 'notice error' : 'notice';
  }

  function cell(row, value, numeric) {
    var element = document.createElement('td');
    element.textContent = String(value);
    if (numeric) { element.className = 'numeric'; }
    row.appendChild(element);
  }

  function stat(label, value) {
    var item = document.createElement('div');
    item.className = 'portfolio-stat';
    var labelElement = document.createElement('span');
    labelElement.textContent = label;
    var valueElement = document.createElement('strong');
    valueElement.textContent = String(value);
    item.appendChild(labelElement);
    item.appendChild(valueElement);
    return item;
  }

  function render(payload) {
    var host = $('portfolio');
    host.textContent = '';
    var accounts = payload.accounts || [];

    if (!accounts.length) {
      var empty = document.createElement('p');
      empty.className = 'empty';
      empty.textContent = 'No exchange accounts are connected.';
      host.appendChild(empty);
      return;
    }

    accounts.forEach(function (account) {
      var card = document.createElement('section');
      card.className = 'card portfolio-account';
      var header = document.createElement('div');
      header.className = 'portfolio-account-header';
      var heading = document.createElement('div');
      var title = document.createElement('h2');
      title.textContent = account.displayName + ' · ' + account.exchange;
      heading.appendChild(title);

      var freshness = document.createElement('p');
      if (account.error) {
        freshness.textContent = 'Current reading unavailable: ' + account.error;
        freshness.className = 'notice error';
        heading.appendChild(freshness);
        header.appendChild(heading);
        card.appendChild(header);
        host.appendChild(card);
        return;
      }

      freshness.textContent = 'Read-only Spot balance';
      heading.appendChild(freshness);
      header.appendChild(heading);
      var reading = document.createElement('div');
      reading.className = 'portfolio-reading';
      reading.textContent = 'Updated ' + formatTime(account.retrievedAtUtc);
      header.appendChild(reading);
      card.appendChild(header);

      var balances = (account.balances || []).filter(function (balance) {
        return Number(balance.total) !== 0;
      });
      if (!balances.length) {
        var none = document.createElement('p');
        none.className = 'empty';
        none.textContent = 'The exchange reported no non-zero balances.';
        card.appendChild(none);
        host.appendChild(card);
        return;
      }

      var heldCount = balances.filter(function (balance) {
        return Number(balance.held) !== 0;
      }).length;
      var summary = document.createElement('div');
      summary.className = 'portfolio-summary';
      summary.appendChild(stat('Assets held', balances.length));
      summary.appendChild(stat('Assets available', balances.filter(function (balance) {
        return Number(balance.available) !== 0;
      }).length));
      summary.appendChild(stat('Assets with funds held', heldCount));
      card.appendChild(summary);

      var tableWrap = document.createElement('div');
      tableWrap.className = 'portfolio-table-wrap';
      var table = document.createElement('table');
      var header = document.createElement('tr');
      ['Asset', 'Total', 'Available', 'Held'].forEach(function (name) {
        var column = document.createElement('th');
        column.textContent = name;
        header.appendChild(column);
      });
      table.appendChild(header);
      balances.forEach(function (balance) {
        var row = document.createElement('tr');
        cell(row, balance.asset, false);
        cell(row, balance.total, true);
        cell(row, balance.available, true);
        cell(row, balance.held, true);
        table.appendChild(row);
      });
      tableWrap.appendChild(table);
      card.appendChild(tableWrap);
      host.appendChild(card);
    });
  }

  async function load() {
    $('refresh').disabled = true;
    setStatus('Requesting current balances from the exchange.', false);
    try {
      var response = await fetch('/api/portfolio', { headers: { 'Accept': 'application/json' } });
      var payload = await response.json().catch(function () { return {}; });
      if (!response.ok) {
        setStatus(payload.message || 'Current balances could not be read.', true);
        return;
      }

      render(payload);
      setStatus('Balances shown are a fresh exchange reading. Failed accounts are shown separately.', false);
    } catch (error) {
      setStatus('Current balances could not be read. No previous balance is shown.', true);
    } finally {
      $('refresh').disabled = false;
    }
  }

  $('refresh').addEventListener('click', load);
  load();
})();
