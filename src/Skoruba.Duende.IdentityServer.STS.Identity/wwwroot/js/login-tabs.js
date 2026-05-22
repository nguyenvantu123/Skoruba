(function () {
    'use strict';

    function init() {
        var tablist = document.querySelector('[role="tablist"]');
        if (!tablist) {
            return;
        }

        var tabs = tablist.querySelectorAll('[role="tab"]');
        if (!tabs || tabs.length === 0) {
            return;
        }

        function getPanelFor(tab) {
            var panelId = tab.getAttribute('aria-controls');
            if (!panelId) {
                return null;
            }
            return document.getElementById(panelId);
        }

        function activate(targetTab) {
            if (!targetTab) {
                return;
            }

            for (var i = 0; i < tabs.length; i++) {
                var tab = tabs[i];
                var isTarget = tab === targetTab;

                tab.setAttribute('aria-selected', isTarget ? 'true' : 'false');
                tab.setAttribute('tabindex', isTarget ? '0' : '-1');

                if (isTarget) {
                    tab.classList.add('is-active');
                } else {
                    tab.classList.remove('is-active');
                }

                var panel = getPanelFor(tab);
                if (panel) {
                    if (isTarget) {
                        panel.removeAttribute('hidden');
                    } else {
                        panel.setAttribute('hidden', 'hidden');
                    }
                }
            }

            try {
                targetTab.focus();
            } catch (e) {
                // ignore focus errors (e.g., tab not in DOM)
            }
        }

        function indexOfTab(tab) {
            for (var i = 0; i < tabs.length; i++) {
                if (tabs[i] === tab) {
                    return i;
                }
            }
            return -1;
        }

        function onClick(event) {
            activate(event.currentTarget);
        }

        function onKeydown(event) {
            var key = event.key;
            var current = event.currentTarget;
            var index = indexOfTab(current);
            if (index < 0) {
                return;
            }

            if (key === 'ArrowRight') {
                event.preventDefault();
                var next = (index + 1) % tabs.length;
                activate(tabs[next]);
                return;
            }

            if (key === 'ArrowLeft') {
                event.preventDefault();
                var prev = (index - 1 + tabs.length) % tabs.length;
                activate(tabs[prev]);
                return;
            }

            if (key === 'Home') {
                event.preventDefault();
                activate(tabs[0]);
                return;
            }

            if (key === 'End') {
                event.preventDefault();
                activate(tabs[tabs.length - 1]);
                return;
            }

            if (key === 'Enter' || key === ' ' || key === 'Spacebar') {
                event.preventDefault();
                activate(current);
                return;
            }
        }

        for (var i = 0; i < tabs.length; i++) {
            tabs[i].addEventListener('click', onClick);
            tabs[i].addEventListener('keydown', onKeydown);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
