(function () {
    'use strict';

    if (window.__sspNativeProgressLoaded) {
        return;
    }
    window.__sspNativeProgressLoaded = true;

    var CONTAINER_ID = 'ssp-native-progress';
    var BAR_ID = 'ssp-native-progress-bar';
    var STYLE_ID = 'ssp-native-progress-style';
    var STATUS_PATH = '/SmartShufflePlaylist/Status';
    var SVG_NS = 'http://www.w3.org/2000/svg';
    var CIRCUMFERENCE = 2 * Math.PI * 42;
    var CACHE_MS = 4000;
    var POLL_MS = 1000;

    var statusPromise = null;
    var statusFetchedAt = 0;
    var pollTimer = null;

    function getItemIdFromRoute() {
        var hash = window.location.hash || '';
        var match = hash.match(/[#\/]details\?(?:[^#]*&)?id=([0-9a-fA-F]{32})/);
        return match ? match[1].toLowerCase() : null;
    }

    function removeProgress() {
        var container = document.getElementById(CONTAINER_ID);
        if (container) {
            container.remove();
        }
        var bar = document.getElementById(BAR_ID);
        if (bar) {
            bar.remove();
        }
    }

    function fetchStatus() {
        if (!window.ApiClient || !window.ApiClient.getJSON) {
            return Promise.reject(new Error('ApiClient unavailable'));
        }
        var now = Date.now();
        if (statusPromise && now - statusFetchedAt < CACHE_MS) {
            return statusPromise;
        }
        statusFetchedAt = now;
        statusPromise = window.ApiClient.getJSON(window.ApiClient.getUrl(STATUS_PATH)).catch(function (err) {
            statusPromise = null;
            statusFetchedAt = 0;
            throw err;
        });
        return statusPromise;
    }

    function createDonut(percent) {
        var svg = document.createElementNS(SVG_NS, 'svg');
        svg.setAttribute('viewBox', '0 0 100 100');
        svg.setAttribute('role', 'img');
        svg.setAttribute('aria-label', 'Playlist progress');

        var track = document.createElementNS(SVG_NS, 'circle');
        track.setAttribute('class', 'ssp-native-donut-track');
        track.setAttribute('cx', '50');
        track.setAttribute('cy', '50');
        track.setAttribute('r', '42');

        var fill = document.createElementNS(SVG_NS, 'circle');
        fill.setAttribute('class', 'ssp-native-donut-fill');
        fill.setAttribute('cx', '50');
        fill.setAttribute('cy', '50');
        fill.setAttribute('r', '42');
        fill.setAttribute('stroke-dasharray', String(CIRCUMFERENCE));
        fill.setAttribute('stroke-dashoffset', String(CIRCUMFERENCE * (1 - percent / 100)));

        svg.appendChild(track);
        svg.appendChild(fill);
        return svg;
    }

    function render(status) {
        var itemId = getItemIdFromRoute();
        var playlistId = status && status.PlaylistId ? String(status.PlaylistId).toLowerCase() : null;
        if (!itemId || !playlistId || itemId !== playlistId) {
            removeProgress();
            return;
        }

        var info = document.querySelector('.detailPagePrimaryContainer .infoWrapper .itemMiscInfo-primary');
        if (!info || !info.parentNode) {
            return;
        }

        var total = Number(status.Total) || 0;
        var played = Number(status.Played) || 0;
        var remaining = Number(status.Remaining) || 0;
        var percent = total > 0 ? Math.round(played / total * 100) : 0;
        var key = percent + '|' + played + '|' + total + '|' + remaining;

        var container = document.getElementById(CONTAINER_ID);
        var bar = document.getElementById(BAR_ID);
        if (container && bar && container.dataset.sspKey === key && document.contains(container)) {
            return;
        }

        if (!container) {
            container = document.createElement('div');
            container.id = CONTAINER_ID;
            container.className = 'ssp-native-progress';
            info.parentNode.insertBefore(container, info.nextSibling);
        }
        container.dataset.sspKey = key;
        container.innerHTML = '';

        var donut = document.createElement('div');
        donut.className = 'ssp-native-donut';
        donut.appendChild(createDonut(percent));

        var text = document.createElement('div');
        text.className = 'ssp-native-text';
        var percentText = document.createElement('strong');
        percentText.textContent = percent + '% complete';
        var summary = document.createElement('span');
        summary.textContent = played + ' of ' + total + ' played \u00b7 ' + remaining + ' remaining';
        text.appendChild(percentText);
        text.appendChild(summary);

        container.appendChild(donut);
        container.appendChild(text);

        if (!bar) {
            bar = document.createElement('div');
            bar.id = BAR_ID;
            bar.className = 'ssp-native-bar';
            bar.appendChild(document.createElement('div'));
        }
        if (!document.contains(bar)) {
            container.parentNode.insertBefore(bar, container.nextSibling);
        }
        bar.firstChild.className = 'ssp-native-bar-fill';
        bar.firstChild.style.width = percent + '%';
    }

    function refresh() {
        if (!getItemIdFromRoute()) {
            removeProgress();
            return;
        }
        fetchStatus().then(render).catch(function () {
            removeProgress();
        });
    }

    function injectStyles() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = [
            '.ssp-native-progress{display:flex;align-items:center;gap:.8em;margin:.15em 0 .55em;max-width:460px;color:inherit;}',
            '.ssp-native-donut{position:relative;width:56px;height:56px;flex:0 0 56px;}',
            '.ssp-native-donut svg{width:100%;height:100%;transform:rotate(-90deg);}',
            '.ssp-native-donut-track{fill:none;stroke:currentColor;stroke-opacity:.18;stroke-width:11;}',
            '.ssp-native-donut-fill{fill:none;stroke:var(--accent,#00a4dc);stroke-width:11;stroke-linecap:round;transition:stroke-dashoffset .35s ease;}',
            '.ssp-native-text{display:flex;flex-direction:column;line-height:1.3;min-width:0;}',
            '.ssp-native-text strong{font-size:1.12em;font-weight:600;}',
            '.ssp-native-text span{font-size:.88em;opacity:.72;}',
            '.ssp-native-bar{height:4px;max-width:460px;margin:0 0 .95em;border-radius:2px;background:currentColor;background:color-mix(in srgb,currentColor 18%,transparent);overflow:hidden;}',
            '.ssp-native-bar-fill{height:100%;background:var(--accent,#00a4dc);border-radius:2px;transition:width .35s ease;}'
        ].join('');
        document.head.appendChild(style);
    }

    function start() {
        injectStyles();
        window.addEventListener('hashchange', refresh);
        document.addEventListener('viewshow', refresh);
        if (pollTimer) {
            clearInterval(pollTimer);
        }
        pollTimer = setInterval(refresh, POLL_MS);
        refresh();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
