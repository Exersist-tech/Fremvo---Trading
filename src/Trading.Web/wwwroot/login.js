// The sign-in page.
//
// Its only job is to authenticate and then hand the user to the trading
// application. It renders no account data and holds no credential after the
// request completes.

(function () {
  'use strict';

  // Where to go after a successful sign-in. The trading chart is the entry
  // point to the application.
  var DEFAULT_DESTINATION = '/chart';

  function $(id) { return document.getElementById(id); }

  function setStatus(message, isError) {
    var el = $('status');
    el.textContent = message;
    el.className = isError ? 'notice error' : 'empty';
  }

  /// Accepts only a path on this site. A value such as
  /// "https://example.com/x" or "//example.com/x" would send the user to
  /// another origin immediately after they typed their password, so anything
  /// that is not a single-slash relative path is discarded.
  function safeReturnUrl(value) {
    if (!value) { return null; }
    if (value.charAt(0) !== '/') { return null; }
    if (value.charAt(1) === '/' || value.charAt(1) === '\\') { return null; }
    return value;
  }

  function destination() {
    var params = new URLSearchParams(window.location.search);
    return safeReturnUrl(params.get('returnUrl')) || DEFAULT_DESTINATION;
  }

  async function alreadySignedIn() {
    try {
      var response = await fetch('/api/me', { headers: { 'Accept': 'application/json' } });
      if (!response.ok) { return false; }
      var me = await response.json();
      return me.signedIn === true;
    } catch (error) {
      return false;
    }
  }

  // Revealing the password is a deliberate, temporary action taken by the
  // person at the keyboard. The field is put back to a password field on
  // submit so a revealed password is not left on screen afterwards.
  function togglePassword() {
    var field = $('login-password');
    var button = $('toggle-password');
    var reveal = field.type === 'password';
    field.type = reveal ? 'text' : 'password';
    button.textContent = reveal ? 'Hide' : 'Show';
    button.setAttribute('aria-pressed', reveal ? 'true' : 'false');
    field.focus();
  }

  function hidePassword() {
    var field = $('login-password');
    if (field.type !== 'password') {
      field.type = 'password';
      $('toggle-password').textContent = 'Show';
      $('toggle-password').setAttribute('aria-pressed', 'false');
    }
  }

  // Caps Lock is the most common reason a correct password is rejected, and
  // the server deliberately cannot tell the user which half was wrong.
  function updateCapsLockNote(event) {
    var note = $('capslock-note');
    if (typeof event.getModifierState !== 'function') { return; }
    note.hidden = !event.getModifierState('CapsLock');
  }

  async function signIn(event) {
    event.preventDefault();

    var email = $('login-email').value.trim();
    var passwordField = $('login-password');
    var password = passwordField.value;

    if (!email || !password) {
      setStatus('Enter your email and password.', true);
      return;
    }

    setStatus('Signing in.', false);
    $('login-submit').disabled = true;
    hidePassword();

    try {
      var response = await fetch('/api/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
        body: JSON.stringify({ email: email, password: password })
      });

      // The password is cleared as soon as the request has been made, so it
      // does not sit in the form afterwards.
      passwordField.value = '';

      if (!response.ok) {
        // The server deliberately does not say whether the address exists, so
        // neither does this message.
        setStatus('That email and password combination was not accepted.', true);
        return;
      }

      // Confirm the session actually exists before navigating. A 200 with no
      // usable cookie would otherwise bounce the user straight back here with
      // no explanation.
      if (!(await alreadySignedIn())) {
        setStatus(
          'Sign-in succeeded but the session cookie was not stored. The cookie requires a secure ' +
          'connection, so open the site over https.',
          true);
        return;
      }

      setStatus('Signed in. Opening the trading application.', false);
      window.location.assign(destination());
    } catch (error) {
      setStatus('Sign-in could not be completed. ' + error.message, true);
    } finally {
      $('login-submit').disabled = false;
    }
  }

  window.addEventListener('DOMContentLoaded', async function () {
    $('login-form').addEventListener('submit', signIn);
    $('toggle-password').addEventListener('click', togglePassword);
    $('login-password').addEventListener('keyup', updateCapsLockNote);
    $('login-password').addEventListener('keydown', updateCapsLockNote);
    $('login-password').addEventListener('blur', function () { $('capslock-note').hidden = true; });

    if (new URLSearchParams(window.location.search).has('signedOut')) {
      $('signed-out-note').hidden = false;
    }

    // Someone who is already signed in has no reason to see this page.
    if (await alreadySignedIn()) {
      window.location.replace(destination());
    }
  });
})();
