(function () {
  'use strict';
  var d = document;
  var ROW_CAP = 50;
  var START_CAP = 10;
  function q(sel, root) { return Array.prototype.slice.call((root || d).querySelectorAll(sel)); }

  var main = d.querySelector('main');
  if (!main) { return; }

  // Toolbar: search, severity chips, expand/collapse. Only exists when scripts run.
  var bar = d.createElement('div');
  bar.className = 'toolbar';
  bar.innerHTML =
    '<label class="search"><span class="visually-hidden">Search findings</span><input type="search" placeholder="Search people, groups, roles, scopes"></label>' +
    ['high', 'medium', 'low', 'info'].map(function (s) {
      return '<button type="button" class="chip on" data-severity="' + s + '" aria-pressed="true"><span class="dot ' + s + '"></span>' + s + '</button>';
    }).join('') +
    '<span class="sep"></span>' +
    '<button type="button" class="chip" data-open="1">Expand all</button>' +
    '<button type="button" class="chip" data-open="0">Collapse all</button>';
  main.insertBefore(bar, main.firstChild);
  var input = bar.querySelector('input');
  var off = {};

  // "No findings match." note per area, inserted here so the no-script page carries no hidden markup.
  q('details.area').forEach(function (area) {
    var p = d.createElement('p');
    p.className = 'nomatch muted';
    p.textContent = 'No findings match.';
    p.hidden = true;
    area.appendChild(p);
  });

  // Caps: hide rows past the limit and offer "Show all".
  function cap(container, rows, limit, noun) {
    if (rows.length <= limit) { return; }
    rows.slice(limit).forEach(function (r) { r.hidden = true; r.setAttribute('data-capped', '1'); });
    var more = d.createElement('div');
    more.className = 'more';
    more.innerHTML = '<span>Showing ' + limit + ' of ' + rows.length + (noun ? ' ' + noun : '') + '</span><button type="button" class="btn">Show all</button>';
    more.querySelector('button').addEventListener('click', function () {
      rows.forEach(function (r) { r.hidden = false; r.removeAttribute('data-capped'); });
      more.parentNode.removeChild(more);
      apply();
    });
    container.appendChild(more);
  }
  q('.table-scroll').forEach(function (scroll) { cap(scroll, q('tbody > tr', scroll), ROW_CAP); });
  var start = d.querySelector('ol.start');
  if (start) { cap(start.parentNode, q(':scope > li', start), START_CAP, 'actions'); }

  function apply() {
    var term = input.value.trim().toLowerCase();
    q('[data-search]').forEach(function (el) {
      var sev = el.getAttribute('data-severity');
      var miss = term && el.getAttribute('data-search').indexOf(term) < 0;
      var capped = !term && el.hasAttribute('data-capped');
      el.hidden = !!((sev && off[sev]) || miss || capped);
    });
    q('details.rule').forEach(function (rule) {
      var sev = rule.getAttribute('data-severity');
      var any = q('[data-search]', rule).some(function (el) { return !el.hidden; });
      rule.hidden = !!(off[sev] || (term && !any));
      if (term && any) { rule.open = true; }
    });
    q('details.area').forEach(function (area) {
      var rules = q('details.rule', area);
      var any = rules.some(function (r) { return !r.hidden; });
      area.querySelector('.nomatch').hidden = !term || any || rules.length === 0;
      if (term && any) { area.open = true; }
    });
  }

  input.addEventListener('input', apply);
  q('.chip[data-severity]', bar).forEach(function (chip) {
    chip.addEventListener('click', function () {
      var s = chip.getAttribute('data-severity');
      off[s] = !off[s];
      chip.classList.toggle('on', !off[s]);
      chip.setAttribute('aria-pressed', String(!off[s]));
      apply();
    });
  });
  q('.chip[data-open]', bar).forEach(function (chip) {
    chip.addEventListener('click', function () {
      var open = chip.getAttribute('data-open') === '1';
      q('details').forEach(function (x) { x.open = open; });
    });
  });

  function openHash() {
    var id = location.hash.slice(1);
    if (!id) { return; }
    var el = d.getElementById(id);
    var det = el && el.closest('details');
    while (det) { det.open = true; det = det.parentElement && det.parentElement.closest('details'); }
  }
  addEventListener('hashchange', openHash);
  openHash();
  addEventListener('beforeprint', function () {
    off = {};
    q('.chip[data-severity]', bar).forEach(function (chip) {
      chip.classList.add('on');
      chip.setAttribute('aria-pressed', 'true');
    });
    input.value = '';
    q('[data-capped]').forEach(function (r) { r.removeAttribute('data-capped'); });
    apply();
    q('details').forEach(function (x) { x.open = true; });
  });

  apply();
})();
