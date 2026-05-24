// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// phone-dial-code.js — composes the dial-code <select> and the local-number
// <input> in `_PhoneRequestPanel.cshtml` into an E.164-formatted value that
// posts via the hidden `PhoneNumber` field.
//
// Lives as an external script (rather than inline) so the page-level CSP
// `default-src 'self'` (no `unsafe-inline`) does not block it.

(function () {
    'use strict';

    function init() {
        var form = document.getElementById('phone-request-form');
        var dialSelect = document.getElementById('phoneOtpDialCode');
        var localInput = document.getElementById('phoneOtpLocalNumber');
        var hidden = document.getElementById('phoneOtpPhoneNumber');
        if (!form || !dialSelect || !localInput || !hidden) {
            return;
        }

        // Strip every char that is not a digit; keep raw digits so we can join
        // them onto the dial code without spaces, hyphens, parens or '+'.
        function digitsOnly(value) {
            return (value || '').replace(/\D+/g, '');
        }

        function compose() {
            var dial = dialSelect.value || '+84';
            var local = digitsOnly(localInput.value);
            // Drop a single leading trunk-prefix '0' which is common in Vietnam,
            // United Kingdom, etc. libphonenumber would normalize this anyway
            // when given a region, but we strip it client-side so the assembled
            // E.164 doesn't carry the trunk zero.
            if (local.charAt(0) === '0') {
                local = local.replace(/^0+/, '');
            }
            hidden.value = local ? dial + local : '';
        }

        form.addEventListener('submit', compose);

        // Also keep the hidden input fresh on input/blur so server-side error
        // re-renders don't show a stale value if the page bounces back.
        localInput.addEventListener('blur', compose);
        dialSelect.addEventListener('change', compose);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
