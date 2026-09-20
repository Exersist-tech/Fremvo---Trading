// Values are formatted by the server. This read-only renderer performs no
// financial calculations and writes every supplied string as text.
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }
  function element(name, text, className) {
    var node = document.createElement(name);
    node.textContent = text == null ? 'Not recorded.' : String(text);
    if (className) { node.className = className; }
    return node;
  }
  function status(message, error) {
    var node = $('status');
    node.textContent = message;
    node.className = error ? 'notice error' : 'notice';
  }
  function table(headers, rows) {
    var result = document.createElement('table');
    var head = document.createElement('thead');
    var headerRow = document.createElement('tr');
    headers.forEach(function (header) { headerRow.appendChild(element('th', header.label, header.numeric ? 'numeric' : '')); });
    head.appendChild(headerRow);
    result.appendChild(head);
    var body = document.createElement('tbody');
    rows.forEach(function (values) {
      var row = document.createElement('tr');
      values.forEach(function (value, index) { row.appendChild(element('td', value, headers[index].numeric ? 'numeric' : '')); });
      body.appendChild(row);
    });
    result.appendChild(body);
    return result;
  }
  function parameterText(parameters) {
    return (parameters || []).map(function (parameter) { return parameter.name + '=' + parameter.value; }).join(', ');
  }
  function splitRows(report) {
    return [report.training, report.validation, report.holdout].map(function (split) {
      return [split.type, split.datasetId, split.datasetVersionIdentity, split.source, split.symbol, split.interval,
        split.fromUtc, split.toUtc, split.candleCount];
    });
  }
  function reportCard(report) {
    var card = document.createElement('section');
    card.className = 'card';
    card.appendChild(element('h2', report.researchId + ' completed research report'));
    card.appendChild(table(
      [{ label: 'Completed UTC' }, { label: 'Provenance' }],
      [[report.completedAtUtc, report.provenance]]));
    card.appendChild(element('h3', 'Immutable dataset split provenance'));
    card.appendChild(table(
      [{ label: 'Split' }, { label: 'Dataset ID' }, { label: 'Dataset version' }, { label: 'Source' },
        { label: 'Symbol' }, { label: 'Interval' }, { label: 'From UTC' }, { label: 'To UTC' },
        { label: 'Candles', numeric: true }],
      splitRows(report)));
    card.appendChild(element('h3', 'Validation-ranked candidates'));
    card.appendChild(element('p', 'Candidates are ranked only by recorded validation score; holdout is excluded from selection.', 'empty'));
    card.appendChild(table(
      [{ label: 'Rank', numeric: true }, { label: 'Parameters' }, { label: 'Validation score', numeric: true }],
      (report.candidates || []).map(function (candidate) {
        return [candidate.rank, parameterText(candidate.parameters), candidate.validationScore];
      })));
    if (report.hasAdditionalCandidates) {
      card.appendChild(element('p', 'Only the first bounded set of recorded candidates is shown.', 'empty'));
    }
    card.appendChild(element('h3', 'One-time holdout verification'));
    if (!report.holdoutVerification) {
      card.appendChild(element('p', 'No one-time holdout verification result was recorded.', 'empty'));
    } else {
      card.appendChild(table(
        [{ label: 'Status' }, { label: 'Score', numeric: true }, { label: 'Verified UTC' }],
        [[report.holdoutVerification.status, report.holdoutVerification.score, report.holdoutVerification.verifiedAtUtc]]));
    }
    card.appendChild(element('h3', 'Walk-forward evaluation'));
    var folds = report.walkForwardFolds || [];
    if (!folds.length) {
      card.appendChild(element('p', 'No walk-forward fold summary was recorded.', 'empty'));
    } else {
      card.appendChild(table(
        [{ label: 'Fold' }, { label: 'Training from UTC' }, { label: 'Training to UTC' },
          { label: 'Validation from UTC' }, { label: 'Validation to UTC' }, { label: 'Parameters' },
          { label: 'Objective', numeric: true }],
        folds.map(function (fold) {
          return [fold.id, fold.trainingFromUtc, fold.trainingToUtc, fold.validationFromUtc,
            fold.validationToUtc, parameterText(fold.parameters), fold.objectiveValue];
        })));
    }
    if (report.walkForwardAggregates) {
      var aggregate = report.walkForwardAggregates;
      card.appendChild(element('h3', 'Walk-forward decimal aggregates'));
      card.appendChild(table(
        [{ label: 'Fold count', numeric: true }, { label: 'Average objective', numeric: true },
          { label: 'Minimum objective', numeric: true }, { label: 'Maximum objective', numeric: true }],
        [[aggregate.foldCount, aggregate.averageObjective, aggregate.minimumObjective, aggregate.maximumObjective]]));
    }
    return card;
  }
  function render(page) {
    var host = $('results');
    host.textContent = '';
    var reports = page.results || [];
    if (!reports.length) {
      host.appendChild(element('p', 'No completed optimization research report is available from a durable source.', 'empty'));
      return;
    }
    reports.forEach(function (report) { host.appendChild(reportCard(report)); });
  }
  async function load() {
    status('Loading completed optimization research reports.', false);
    try {
      var response = await fetch('/api/optimization/results', { headers: { 'Accept': 'application/json' } });
      if (!response.ok) { status('Completed optimization research reports could not be loaded.', true); return; }
      render(await response.json());
      status('Read-only hypothetical research. Times are UTC and scores are server-rendered decimal values.', false);
    } catch (error) {
      status('Completed optimization research reports could not be loaded.', true);
    }
  }
  window.addEventListener('DOMContentLoaded', load);
})();
