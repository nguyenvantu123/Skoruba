document.addEventListener("DOMContentLoaded", () => {
    const resendButton = document.querySelector("[data-resend-button='true']");
    const resendText = resendButton?.querySelector("[data-resend-text='true']");

    if (!resendButton || !resendText) {
        return;
    }

    const baseLabel = resendButton.getAttribute("data-resend-label") || resendText.textContent?.trim() || "Resend";
    let remainingSeconds = Number.parseInt(resendButton.getAttribute("data-cooldown-seconds") || "0", 10);

    if (!Number.isFinite(remainingSeconds) || remainingSeconds <= 0) {
        resendButton.disabled = false;
        resendButton.setAttribute("aria-disabled", "false");
        resendText.textContent = baseLabel;
        return;
    }

    const render = () => {
        if (remainingSeconds <= 0) {
            resendButton.disabled = false;
            resendButton.setAttribute("aria-disabled", "false");
            resendButton.setAttribute("data-cooldown-seconds", "0");
            resendText.textContent = baseLabel;
            return true;
        }

        resendButton.disabled = true;
        resendButton.setAttribute("aria-disabled", "true");
        resendText.textContent = `${baseLabel} (${remainingSeconds}s)`;
        return false;
    };

    if (render()) {
        return;
    }

    const timer = window.setInterval(() => {
        remainingSeconds -= 1;
        if (render()) {
            window.clearInterval(timer);
        }
    }, 1000);
});
