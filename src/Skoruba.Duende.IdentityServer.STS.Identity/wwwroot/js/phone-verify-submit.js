(function () {
    'use strict';

    function init() {
        var form = document.getElementById('phoneOtpVerifyForm');
        if (!form) {
            return;
        }

        var errorBox = document.querySelector('[data-phone-otp-verify-error="true"]');
        var submitButton = form.querySelector('[data-phone-otp-verify-submit="true"]');
        var otpInput = document.getElementById('phoneOtpCode');

        function setError(message) {
            if (!errorBox) {
                return;
            }

            var text = (message || '').trim();
            if (!text) {
                errorBox.textContent = '';
                errorBox.classList.add('hidden');
                return;
            }

            errorBox.textContent = text;
            errorBox.classList.remove('hidden');
        }

        function setSubmitting(isSubmitting) {
            if (!submitButton) {
                return;
            }

            submitButton.disabled = isSubmitting;
            submitButton.setAttribute('aria-disabled', isSubmitting ? 'true' : 'false');
        }

        function extractErrorFromHtml(html) {
            if (!html) {
                return '';
            }

            var parser = new DOMParser();
            var documentFragment = parser.parseFromString(html, 'text/html');
            var nextError = documentFragment.querySelector('[data-phone-otp-verify-error="true"]');
            return nextError && nextError.textContent
                ? nextError.textContent.trim()
                : '';
        }

        form.addEventListener('submit', async function (event) {
            event.preventDefault();
            setError('');
            setSubmitting(true);

            try {
                var response = await fetch(form.action, {
                    method: 'POST',
                    body: new FormData(form),
                    credentials: 'same-origin',
                    headers: {
                        'X-Requested-With': 'XMLHttpRequest'
                    }
                });

                if (response.redirected && response.url) {
                    window.location.assign(response.url);
                    return;
                }

                var contentType = response.headers.get('content-type') || '';
                if (contentType.indexOf('application/json') >= 0) {
                    var json = await response.json();
                    if (json && json.redirectUrl) {
                        window.location.assign(json.redirectUrl);
                        return;
                    }

                    setError('Cannot verify OTP right now. Please try again.');
                } else
                if (contentType.indexOf('text/html') >= 0) {
                    var html = await response.text();
                    var errorMessage = extractErrorFromHtml(html);
                    setError(errorMessage || 'The OTP code is invalid or expired.');
                } else {
                    setError('The OTP code is invalid or expired.');
                }

                if (otpInput) {
                    otpInput.focus();
                    otpInput.select();
                }
            } catch (error) {
                setError('Cannot verify OTP right now. Please try again.');
                if (otpInput) {
                    otpInput.focus();
                }
            } finally {
                setSubmitting(false);
            }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
