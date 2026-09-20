// Builds the shared navigation bar and footer on every page.
//
// Every value is written with textContent or element attributes rather than
// innerHTML, so no server or user supplied string can be interpreted as
// markup.
(function () {
  'use strict';

  var LINKS = [
    { href: '/', label: 'Overview' },
    { href: '/orders', label: 'Orders' },
    { href: '/experiments', label: 'Experiments' },
    { href: '/optimization', label: 'Optimization' },
    { href: '/admin/universe', label: 'Universe' },
    { href: '/admin/risk', label: 'Risk' },
    { href: '/account', label: 'Account' }
  ];

  var DISCLAIMER =
    'Fremvo Trading never holds, transfers or withdraws funds, and never ' +
    'predicts or guarantees results. Nothing shown here is tax, legal or ' +
    'financial advice.';

  function buildNav() {
    var nav = document.createElement('nav');
    nav.className = 'app-nav';

    var brand = document.createElement('a');
    brand.className = 'brand';
    brand.href = '/';
    brand.appendChild(document.createTextNode('Fremvo '));
    var accent = document.createElement('span');
    accent.textContent = 'Trading';
    brand.appendChild(accent);
    nav.appendChild(brand);

    var path = window.location.pathname.replace(/\/+$/, '') || '/';

    LINKS.forEach(function (link) {
      var anchor = document.createElement('a');
      anchor.className = 'nav-link';
      anchor.href = link.href;
      anchor.textContent = link.label;
      if (path === link.href) {
        anchor.className += ' active';
      }
      nav.appendChild(anchor);
    });

    var spacer = document.createElement('div');
    spacer.className = 'spacer';
    nav.appendChild(spacer);

    // Live trading is disabled by default and the shell says so on every
    // page, so an operator can never be unsure which mode they are in.
    var badge = document.createElement('span');
    badge.className = 'mode-badge';
    badge.textContent = 'live trading disabled';
    nav.appendChild(badge);

    return nav;
  }

  function buildFooter() {
    var footer = document.createElement('footer');
    footer.className = 'app-footer';
    footer.textContent = DISCLAIMER;
    return footer;
  }

  function mount() {
    if (document.querySelector('.app-nav')) {
      return;
    }

    document.body.insertBefore(buildNav(), document.body.firstChild);
    document.body.appendChild(buildFooter());
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', mount);
  } else {
    mount();
  }
})();
