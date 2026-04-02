(function () {
    var banner = document.getElementById("tenant-status-banner");
    if (!banner) {
        return;
    }

    var endpoint = banner.getAttribute("data-endpoint");
    if (!endpoint) {
        return;
    }

    var titleNode = document.getElementById("tenant-status-title");
    var messageNode = document.getElementById("tenant-status-message");
    var indicator = document.getElementById("tenant-status-indicator");
    var logoContainer = document.getElementById("tenant-status-logo");
    var logoImage = document.getElementById("tenant-status-logo-image");

    function hideElement(id) {
        var element = document.getElementById(id);
        if (!element) {
            return;
        }

        element.classList.add("hidden");
    }

    function hideTenantLocalActions() {
        hideElement("tenant-login-remember");
        hideElement("tenant-login-submit");
        hideElement("tenant-login-links");
        hideElement("tenant-header-login");
        hideElement("tenant-header-login-mobile");
    }

    function setIndicator(state) {
        if (!indicator) {
            return;
        }

        indicator.className = "inline-flex h-5 w-5 rounded-full border-2 border-current";

        if (state === "loading") {
            indicator.className += " border-r-transparent text-muted-foreground animate-spin";
            return;
        }

        if (state === "resolved") {
            indicator.className += " border-emerald-500 bg-emerald-500";
            return;
        }

        indicator.className += " border-amber-500 bg-amber-500";
    }

    function setBanner(title, message, showBanner, state) {
        if (!titleNode || !messageNode) {
            return;
        }

        titleNode.textContent = title || "";
        messageNode.textContent = message || "";
        banner.classList.toggle("hidden", !showBanner);
        setIndicator(state || "loading");
    }

    function setLogo(logoUrl) {
        if (!logoContainer || !logoImage) {
            return;
        }

        if (logoUrl) {
            logoImage.setAttribute("src", logoUrl);
            logoContainer.classList.remove("hidden");
            return;
        }

        logoImage.removeAttribute("src");
        logoContainer.classList.add("hidden");
    }

    setLogo(null);
    hideTenantLocalActions();
    setBanner(
        banner.getAttribute("data-loading-title") || "Loading tenant",
        banner.getAttribute("data-loading-message") || "",
        true,
        "loading"
    );

    fetch(endpoint, {
        method: "GET",
        credentials: "same-origin",
        headers: {
            "X-Requested-With": "XMLHttpRequest"
        }
    })
        .then(function (response) {
            if (!response.ok) {
                throw new Error("tenant status request failed");
            }

            return response.json();
        })
        .then(function (payload) {
            if (!payload || payload.state === "global") {
                banner.classList.add("hidden");
                return;
            }

            if (payload.state === "resolved") {
                setLogo(payload.logoUrl || null);

                var readyPrefix = banner.getAttribute("data-ready-prefix") || "Signing in to";
                var readyName = payload.displayName || payload.tenantKey || "tenant";
                var readyMessage = payload.tenantKey || banner.getAttribute("data-default-ready-message") || "";

                setBanner(readyPrefix + " " + readyName, readyMessage, true, "resolved");
                return;
            }

            setLogo(null);
            setBanner(
                payload.state === "missing"
                    ? (banner.getAttribute("data-missing-title") || "Tenant details unavailable")
                    : (banner.getAttribute("data-error-title") || "Tenant lookup unavailable"),
                payload.message || banner.getAttribute("data-fallback-message") || "",
                true,
                "warning"
            );
        })
        .catch(function () {
            setLogo(null);
            setBanner(
                banner.getAttribute("data-error-title") || "Tenant lookup unavailable",
                banner.getAttribute("data-fallback-message") || "You can still sign in.",
                true,
                "warning"
            );
        });
})();
