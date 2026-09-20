// Builds the shared navigation bar and footer on every page.
//
// Every value is written with textContent or element attributes rather than
// innerHTML, so no server or user supplied string can be interpreted as
// markup.
(function () {
  'use strict';

  var LINKS = [
    { href: '/', label: 'Overview' },
    { href: '/chart', label: 'Chart' },
    { href: '/scanner', label: 'Scanner' },
    { href: '/portfolio', label: 'Portfolio' },
    { href: '/positions', label: 'Positions' },
    { href: '/orders', label: 'Orders' },
    { href: '/experiments', label: 'Experiments' },
    { href: '/optimization', label: 'Optimization' },
    { href: '/admin/universe', label: 'Universe' },
    { href: '/admin/risk', label: 'Risk' },
    { href: '/exchange', label: 'Exchange' },
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

    // Filled in once /api/me answers. Until then it says nothing rather than
    // guessing, because showing "signed out" to a signed-in user and then
    // correcting it is worse than a brief blank.
    var identity = document.createElement('span');
    identity.className = 'nav-identity';
    identity.id = 'nav-identity';
    nav.appendChild(identity);

    return nav;
  }

  async function signOut() {
    try {
      await fetch('/api/logout', { method: 'POST', headers: { 'Accept': 'application/json' } });
    } catch (error) {
      // Even if the call fails, leaving the page is the right move: the user
      // asked to sign out, so they must not be left looking at their data.
    }

    // A full navigation rather than a re-render, so no page keeps data
    // fetched while the previous session was active.
    window.location.assign('/login?signedOut=1');
  }

  async function renderIdentity() {
    var host = document.getElementById('nav-identity');
    if (!host) { return; }

    var me = null;

    try {
      var response = await fetch('/api/me', { headers: { 'Accept': 'application/json' } });
      if (response.ok) { me = await response.json(); }
    } catch (error) {
      me = null;
    }

    host.textContent = '';

    if (!me || me.signedIn !== true) {
      var signIn = document.createElement('a');
      signIn.className = 'nav-link nav-signin';
      signIn.href = '/login';
      signIn.textContent = 'Sign in';
      host.appendChild(signIn);
      return;
    }

    var who = document.createElement('span');
    who.className = 'nav-user';
    // textContent, so a display name can never be interpreted as markup.
    who.textContent = me.displayName || me.email;
    who.title = me.email;
    host.appendChild(who);

    if (me.isAdministrator) {
      var role = document.createElement('span');
      role.className = 'nav-role';
      role.textContent = 'admin';
      host.appendChild(role);
    }

    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'secondary nav-signout';
    button.textContent = 'Sign out';
    button.addEventListener('click', signOut);
    host.appendChild(button);
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
    renderIdentity();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', mount);
  } else {
    mount();
  }
})();
