(function () {
    var meta = document.querySelector("meta[http-equiv=refresh]");
    var redirectUrl = meta && meta.getAttribute("data-url");

    if (!redirectUrl) {
        return;
    }

    var navigate = function () {
        window.location.replace(redirectUrl);
    };

    var scheduleRedirect = function () {
        window.requestAnimationFrame(function () {
            window.setTimeout(navigate, 75);
        });
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", scheduleRedirect, { once: true });
        return;
    }

    scheduleRedirect();
})();
