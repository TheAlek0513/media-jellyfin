/*
 * Mediarium Requests for Jellyfin Web.
 *
 * Loaded into Jellyfin Web (a side-menu entry, a "Request something" row on
 * Home and a "ready to watch" pop-up) and by the plugin's own page at
 * /MediariumRequests/Page, which mounts the same Request page full-screen.
 */
(function () {
    'use strict';
    if (window.__mediariumRequests) {
        return;
    }
    window.__mediariumRequests = true;

    var script = document.currentScript;
    var BASE = script && script.src ? script.src.replace(/\/MediariumRequests\/client\.js.*$/i, '') : '';
    var STANDALONE = !!document.getElementById('mdrm-standalone');
    var STEPS = ['Requested', 'Approved', 'Downloading', 'Ready'];
    var STEP_OF = { pending: 0, approved: 1, downloading: 2, available: 3 };

    // ---------- talking to the plugin ----------

    function session() {
        var ac = window.ApiClient;
        try {
            if (ac && typeof ac.accessToken === 'function' && ac.accessToken()) {
                return { token: ac.accessToken(), userId: ac.getCurrentUserId(), deviceId: typeof ac.deviceId === 'function' ? ac.deviceId() : '' };
            }
        } catch (e) { /* not signed in yet */ }
        try {
            var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = (creds.Servers || []).filter(function (s) { return s.AccessToken; });
            if (servers.length) {
                servers.sort(function (a, b) { return (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0); });
                return { token: servers[0].AccessToken, userId: servers[0].UserId, deviceId: '' };
            }
        } catch (e) { /* nothing stored */ }
        return null;
    }

    function call(method, path, body) {
        var s = session();
        if (!s) {
            return Promise.reject(new Error('Sign in to Jellyfin first.'));
        }
        var init = {
            method: method,
            headers: { 'Authorization': 'MediaBrowser Token="' + s.token + '"', 'Accept': 'application/json' }
        };
        if (body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }
        return fetch(BASE + '/MediariumRequests/' + path, init).then(function (res) {
            if (res.status === 204) {
                return null;
            }
            return res.text().then(function (text) {
                var data = null;
                try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
                if (!res.ok) {
                    var err = new Error((data && data.error) || ('Mediarium answered ' + res.status + '.'));
                    err.status = res.status;
                    throw err;
                }
                return data;
            });
        });
    }

    function api(method, path, body) {
        return call(method, 'Api/' + path, body);
    }

    // ---------- little helpers ----------

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function el(html) {
        var t = document.createElement('template');
        t.innerHTML = html.trim();
        return t.content.firstElementChild;
    }

    function badge(t) {
        switch (t.state) {
            case 'pending': return '<span class="mdrm-badge mdrm-s-pending">Requested</span>';
            case 'declined': return '<span class="mdrm-badge mdrm-s-declined">Declined</span>';
            case 'approved': return '<span class="mdrm-badge mdrm-s-approved">Approved</span>';
            case 'downloading': return '<span class="mdrm-badge mdrm-s-downloading">' + (t.progress != null ? Math.round(t.progress) + '%' : 'Downloading') + '</span>';
            case 'available': return '<span class="mdrm-badge mdrm-s-available">Ready</span>';
            default: return '';
        }
    }

    function toast(text) {
        var t = el('<div class="mdrm-toast" role="status">' + esc(text) + '</div>');
        document.body.appendChild(t);
        setTimeout(function () { t.classList.add('mdrm-toast-out'); }, 4000);
        setTimeout(function () { t.remove(); }, 4600);
    }

    function quotaText(q) {
        if (!q || !q.quota) {
            return '';
        }
        if (!q.quota.limit) {
            return q.asksToAdd ? 'No limit' : 'No limit, added straight away';
        }
        var period = { day: 'today', week: 'this week', month: 'this month' }[q.quota.period] || ('this ' + q.quota.period);
        return q.quota.left + ' of ' + q.quota.limit + ' requests left ' + period;
    }

    // ---------- styles ----------

    var CSS = [
        '.mdrm-overlay{position:fixed;inset:0;z-index:1100;background:#101010;color:#eee;overflow-y:auto;font-family:inherit}',
        '.mdrm-standalone .mdrm-overlay{position:static;min-height:100vh}',
        '.mdrm-wrap{max-width:1400px;margin:0 auto;padding:16px 3.3% 64px}',
        '.mdrm-top{display:flex;align-items:center;gap:12px;flex-wrap:wrap;position:sticky;top:0;background:#101010;padding:12px 0;z-index:2}',
        '.mdrm-top h1{font-size:1.6em;margin:0 8px 0 0;font-weight:500}',
        '.mdrm-search{flex:1 1 240px;min-width:0;background:#292929;border:1px solid #3a3a3a;color:#fff;border-radius:6px;padding:10px 12px;font-size:1em}',
        '.mdrm-pill{background:#00a4dc22;color:#7fd6f5;border:1px solid #00a4dc66;border-radius:999px;padding:6px 12px;font-size:.85em;white-space:nowrap}',
        '.mdrm-x{background:none;border:0;color:#ddd;font-size:1.8em;cursor:pointer;line-height:1;padding:4px 8px}',
        '.mdrm-section h2{font-size:1.2em;font-weight:500;margin:24px 0 10px}',
        '.mdrm-row{display:flex;gap:12px;overflow-x:auto;padding-bottom:8px;scroll-snap-type:x proximity}',
        '.mdrm-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(130px,1fr));gap:14px}',
        '.mdrm-card{flex:0 0 140px;background:none;border:0;color:inherit;text-align:left;padding:0;cursor:pointer;scroll-snap-align:start;font:inherit}',
        '.mdrm-card:focus-visible{outline:2px solid #00a4dc;outline-offset:2px;border-radius:6px}',
        '.mdrm-poster{position:relative;aspect-ratio:2/3;border-radius:6px;background:#222 center/cover no-repeat;overflow:hidden}',
        '.mdrm-poster-none{display:flex;align-items:center;justify-content:center;color:#777;font-size:.8em;padding:8px;text-align:center}',
        '.mdrm-ct{margin-top:6px;font-size:.9em;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
        '.mdrm-cy{font-size:.8em;color:#999}',
        '.mdrm-badge{position:absolute;top:6px;right:6px;font-size:.72em;padding:3px 7px;border-radius:999px;font-weight:600;background:#000a;color:#fff}',
        '.mdrm-s-pending{background:#c78a00}.mdrm-s-declined{background:#b3261e}.mdrm-s-approved{background:#5b6cff}.mdrm-s-downloading{background:#00a4dc}.mdrm-s-available{background:#2e9d4f}',
        '.mdrm-empty{color:#999;padding:8px 0}',
        '.mdrm-panel-bg{position:fixed;inset:0;z-index:1200;background:#000b;display:flex;align-items:center;justify-content:center;padding:16px}',
        '.mdrm-panel{background:#1c1c1c;color:#eee;border-radius:10px;max-width:760px;width:100%;max-height:92vh;overflow-y:auto;box-shadow:0 10px 40px #000}',
        '.mdrm-hero{height:200px;background:#222 center/cover no-repeat;position:relative;border-radius:10px 10px 0 0}',
        '.mdrm-hero:after{content:"";position:absolute;inset:0;background:linear-gradient(transparent 40%,#1c1c1c)}',
        '.mdrm-body{padding:0 20px 20px;margin-top:-60px;position:relative;display:flex;gap:18px}',
        '.mdrm-body .mdrm-poster{flex:0 0 120px;height:180px;aspect-ratio:auto}',
        '.mdrm-info{flex:1;min-width:0;padding-top:64px}',
        '.mdrm-info h3{margin:0 0 4px;font-size:1.4em;font-weight:500}',
        '.mdrm-meta{color:#aaa;font-size:.85em;margin-bottom:10px}',
        '.mdrm-ov{font-size:.92em;line-height:1.45;color:#ccc}',
        '.mdrm-steps{display:flex;gap:4px;margin:16px 0 6px}',
        '.mdrm-step{flex:1;text-align:center;font-size:.75em;color:#888}',
        '.mdrm-step i{display:block;height:4px;border-radius:2px;background:#333;margin-bottom:6px}',
        '.mdrm-step.on{color:#eee}.mdrm-step.on i{background:#00a4dc}',
        '.mdrm-step.now{font-weight:600}',
        '.mdrm-note{background:#b3261e33;border:1px solid #b3261e88;border-radius:6px;padding:8px 10px;font-size:.88em;margin:10px 0}',
        '.mdrm-actions{display:flex;flex-wrap:wrap;gap:10px;margin-top:16px}',
        '.mdrm-btn{background:#00a4dc;color:#fff;border:0;border-radius:6px;padding:10px 16px;font-size:.95em;cursor:pointer;font:inherit}',
        '.mdrm-btn[disabled]{opacity:.5;cursor:default}',
        '.mdrm-btn.alt{background:#333}',
        '.mdrm-btn.danger{background:#5a1d1a}',
        '.mdrm-toast{position:fixed;left:50%;bottom:32px;transform:translateX(-50%);z-index:1300;background:#333;color:#fff;padding:12px 18px;border-radius:6px;box-shadow:0 4px 20px #0008;transition:opacity .5s;max-width:90vw}',
        '.mdrm-toast-out{opacity:0}',
        '.mdrm-ready{position:fixed;right:20px;bottom:20px;z-index:1250;background:#1c1c1c;color:#eee;border-left:4px solid #2e9d4f;border-radius:8px;padding:14px 16px;box-shadow:0 6px 30px #000a;max-width:340px}',
        '.mdrm-ready b{display:block;font-size:1.05em;margin-bottom:10px}',
        '.mdrm-reqs .mdrm-req{display:flex;gap:12px;align-items:center;padding:8px 0;border-bottom:1px solid #262626;cursor:pointer}',
        '.mdrm-req .mdrm-poster{flex:0 0 46px;height:69px;aspect-ratio:auto}',
        '.mdrm-req .mdrm-badge{position:static}',
        '.mdrm-req-t{flex:1;min-width:0}',
        '.mdrm-home .mdrm-row{padding-left:3.3%;padding-right:3.3%;scroll-padding:0 3.3%}',
        '.mdrm-home .sectionTitle{display:flex;align-items:center;gap:12px}',
        '.mdrm-home .mdrm-more{font-size:.6em;color:#00a4dc;cursor:pointer;background:none;border:0;font-family:inherit}',
        '.mdrm-placeholder .mainDetailButtons .btnPlay,.mdrm-placeholder .mainDetailButtons .btnReplay,.mdrm-placeholder .mainDetailButtons .btnResume,.mdrm-placeholder .mainDetailButtons .btnDownload,.mdrm-placeholder .mainDetailButtons .btnShuffle,.mdrm-placeholder .mainDetailButtons .btnInstantMix,.mdrm-placeholder .mainDetailButtons .btnPlaystate{display:none!important}',
        '.mdrm-reqbtn{background:#00a4dc!important;color:#fff!important;border-radius:999px!important;padding:0 18px 0 12px!important}',
        '.mdrm-reqbtn .detailButton-content{display:flex!important;flex-direction:row!important;align-items:center;gap:6px;height:100%}',
        '.mdrm-reqbtn .mdrm-reqlabel{font-size:1rem;font-weight:600;white-space:nowrap}',
        '.mdrm-reqbtn-done{background:#333!important}',
        '.mdrm-search{margin-top:1em}',
        '#searchPage.mdrm-has-results .noItemsMessage{display:none}',
        '@media (max-width:600px){.mdrm-body{flex-direction:column;margin-top:-40px}.mdrm-body .mdrm-poster{display:none}.mdrm-info{padding-top:0}.mdrm-card{flex-basis:110px}.mdrm-grid{grid-template-columns:repeat(auto-fill,minmax(105px,1fr))}}'
    ].join('\n');

    function addStyles() {
        if (!document.getElementById('mdrm-css')) {
            var st = document.createElement('style');
            st.id = 'mdrm-css';
            st.textContent = CSS;
            document.head.appendChild(st);
        }
    }

    // ---------- cards ----------

    function poster(t, extra) {
        if (t.posterUrl) {
            return '<div class="mdrm-poster" style="background-image:url(&quot;' + esc(t.posterUrl) + '&quot;)">' + (extra || '') + '</div>';
        }
        return '<div class="mdrm-poster mdrm-poster-none">' + esc(t.title) + (extra || '') + '</div>';
    }

    function card(t) {
        var c = el('<button type="button" class="mdrm-card" title="' + esc(t.title) + '">' + poster(t, badge(t)) +
            '<div class="mdrm-ct">' + esc(t.title) + '</div><div class="mdrm-cy">' + esc(t.year || '') + (t.media === 'tv' ? ' · Show' : '') + '</div></button>');
        c.addEventListener('click', function () { openTitle(t.media, t.tmdbId, t); });
        return c;
    }

    function fillRow(row, list, emptyText) {
        row.innerHTML = '';
        if (!list || !list.length) {
            row.appendChild(el('<div class="mdrm-empty">' + esc(emptyText || 'Nothing here.') + '</div>'));
            return;
        }
        list.forEach(function (t) { row.appendChild(card(t)); });
    }

    // ---------- the Request page ----------

    var page = null;
    var quota = null;

    function loadQuota() {
        return api('GET', 'quota').then(function (q) {
            quota = q;
            if (page) {
                var pill = page.querySelector('.mdrm-pill');
                pill.textContent = quotaText(q);
                pill.hidden = !pill.textContent;
            }
            return q;
        }).catch(function () { return null; });
    }

    function openPage() {
        addStyles();
        if (page) {
            page.hidden = false;
            refreshPage();
            return;
        }
        page = el(
            '<div class="mdrm-overlay" role="dialog" aria-label="Request">' +
            '<div class="mdrm-wrap">' +
            '<div class="mdrm-top"><h1>Request</h1>' +
            '<input class="mdrm-search" type="search" placeholder="Search movies and shows" aria-label="Search movies and shows">' +
            '<span class="mdrm-pill" hidden></span>' +
            (STANDALONE ? '<a class="mdrm-btn alt" href="' + esc(BASE) + '/web/">Jellyfin</a>' : '<button type="button" class="mdrm-x" aria-label="Close">×</button>') +
            '</div>' +
            '<div class="mdrm-results" hidden><div class="mdrm-section"><h2>Results</h2><div class="mdrm-grid"></div></div></div>' +
            '<div class="mdrm-lists">' +
            '<div class="mdrm-section mdrm-reqs-sec" hidden><h2>Your requests</h2><div class="mdrm-reqs"></div></div>' +
            '<div class="mdrm-section"><h2>Trending</h2><div class="mdrm-row" data-list="discover/mixed/trending"></div></div>' +
            '<div class="mdrm-section"><h2>Popular movies</h2><div class="mdrm-row" data-list="discover/movie/popular"></div></div>' +
            '<div class="mdrm-section"><h2>Popular shows</h2><div class="mdrm-row" data-list="discover/tv/popular"></div></div>' +
            '</div></div></div>');
        var close = page.querySelector('.mdrm-x');
        if (close) {
            close.addEventListener('click', closePage);
        }
        var search = page.querySelector('.mdrm-search');
        var timer = null;
        search.addEventListener('input', function () {
            clearTimeout(timer);
            timer = setTimeout(function () { runSearch(search.value.trim()); }, 300);
        });
        document.body.appendChild(page);
        refreshPage();
        if (!STANDALONE) {
            search.focus();
        }
    }

    function closePage() {
        if (page) {
            page.hidden = true;
        }
    }

    function refreshPage() {
        loadQuota();
        page.querySelectorAll('.mdrm-row[data-list]').forEach(function (row) {
            api('GET', row.getAttribute('data-list')).then(function (list) {
                fillRow(row, list, 'Nothing to show.');
            }).catch(function (e) {
                row.innerHTML = '<div class="mdrm-empty">' + esc(e.message) + '</div>';
            });
        });
        loadRequests();
        var q = page.querySelector('.mdrm-search').value.trim();
        if (q.length >= 2) {
            runSearch(q);
        }
    }

    function loadRequests() {
        var sec = page.querySelector('.mdrm-reqs-sec');
        var box = page.querySelector('.mdrm-reqs');
        api('GET', 'requests').then(function (list) {
            box.innerHTML = '';
            sec.hidden = !list || !list.length;
            (list || []).slice(0, 30).forEach(function (r) {
                var when = r.createdAt ? new Date(r.createdAt).toLocaleDateString() : '';
                var row = el('<div class="mdrm-req" tabindex="0" role="button">' + poster(r) +
                    '<div class="mdrm-req-t"><div>' + esc(r.title) + (r.fourK ? ' (4K)' : '') + '</div>' +
                    '<div class="mdrm-cy">' + esc(r.year || '') + (when ? ' · asked ' + esc(when) : '') +
                    (r.state === 'declined' && r.note ? ' · ' + esc(r.note) : '') + '</div></div>' + badge(r) + '</div>');
                var open = function () { openTitle(r.media, r.tmdbId, r); };
                row.addEventListener('click', open);
                row.addEventListener('keydown', function (e) { if (e.key === 'Enter') { open(); } });
                box.appendChild(row);
            });
        }).catch(function () { sec.hidden = true; });
    }

    function runSearch(q) {
        var results = page.querySelector('.mdrm-results');
        var lists = page.querySelector('.mdrm-lists');
        if (q.length < 2) {
            results.hidden = true;
            lists.hidden = false;
            return;
        }
        results.hidden = false;
        lists.hidden = true;
        var grid = results.querySelector('.mdrm-grid');
        api('GET', 'search?q=' + encodeURIComponent(q)).then(function (list) {
            if (page.querySelector('.mdrm-search').value.trim() !== q) {
                return;
            }
            fillRow(grid, list, 'Nothing found for "' + q + '".');
        }).catch(function (e) {
            grid.innerHTML = '<div class="mdrm-empty">' + esc(e.message) + '</div>';
        });
    }

    // ---------- the title panel ----------

    function openTitle(media, tmdbId, seed) {
        addStyles();
        var bg = el('<div class="mdrm-panel-bg"><div class="mdrm-panel" role="dialog" aria-modal="true"><div class="mdrm-hero"></div>' +
            '<div class="mdrm-body">' + poster(seed || {}) + '<div class="mdrm-info"><h3>' + esc((seed && seed.title) || '') + '</h3>' +
            '<div class="mdrm-meta"></div><div class="mdrm-ov">Loading…</div><div class="mdrm-status"></div><div class="mdrm-actions"></div></div></div></div></div>');
        function close() {
            bg.remove();
            document.removeEventListener('keydown', onKey, true);
        }
        function onKey(e) {
            if (e.key === 'Escape' || e.key === 'Backspace' && e.target.tagName !== 'INPUT') {
                e.stopPropagation();
                e.preventDefault();
                close();
            }
        }
        bg.addEventListener('click', function (e) { if (e.target === bg) { close(); } });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(bg);

        function render() {
            return Promise.all([
                api('GET', 'titles/' + media + '/' + tmdbId),
                api('GET', 'requests').catch(function () { return []; }),
                quota ? Promise.resolve(quota) : loadQuota()
            ]).then(function (all) {
                var d = all[0].detail || {};
                var state = all[0].state || { state: 'none' };
                var mine = (all[1] || []).filter(function (r) { return r.media === media && r.tmdbId === tmdbId; });
                var q = all[2] || {};
                var latest = mine.find(function (r) { return !r.fourK; }) || mine[0];
                var fourKReq = mine.find(function (r) { return r.fourK; });

                if (d.backdropUrl) {
                    bg.querySelector('.mdrm-hero').style.backgroundImage = 'url("' + d.backdropUrl + '")';
                }
                var p = bg.querySelector('.mdrm-body .mdrm-poster');
                if (d.posterUrl && p) {
                    p.outerHTML = poster({ posterUrl: d.posterUrl, title: d.title });
                }
                bg.querySelector('h3').textContent = d.title || (seed && seed.title) || '';
                var genres = (d.genres || []).map(function (g) { return typeof g === 'string' ? g : g.name; }).filter(Boolean);
                var meta = [d.year, media === 'tv' ? 'Show' : 'Movie', d.runtime ? d.runtime + ' min' : '', d.seasons ? d.seasons + ' seasons' : '', genres.slice(0, 3).join(', '), d.rating ? '★ ' + d.rating : '']
                    .filter(Boolean).join(' · ');
                bg.querySelector('.mdrm-meta').textContent = meta;
                bg.querySelector('.mdrm-ov').textContent = d.overview || '';

                var st = state.state || 'none';
                var status = bg.querySelector('.mdrm-status');
                status.innerHTML = '';
                if (st === 'declined') {
                    status.appendChild(el('<div class="mdrm-note">Declined' + (latest && latest.note ? ': ' + esc(latest.note) : '.') + '</div>'));
                } else if (st !== 'none') {
                    var at = STEP_OF[st];
                    var html = '<div class="mdrm-steps">';
                    STEPS.forEach(function (name, i) {
                        var label = name;
                        if (i === 2 && st === 'downloading' && state.progress != null) {
                            label += ' ' + Math.round(state.progress) + '%';
                        }
                        html += '<div class="mdrm-step' + (i <= at ? ' on' : '') + (i === at ? ' now' : '') + '"><i></i>' + esc(label) + '</div>';
                    });
                    status.appendChild(el(html + '</div>'));
                }

                var actions = bg.querySelector('.mdrm-actions');
                actions.innerHTML = '';
                var canFourK = all[0].fourK && q.fourK && q.fourK[media];
                var label4k = (q.fourKLabel || '4K');
                if (st === 'none' || st === 'declined') {
                    addBtn(actions, st === 'declined' ? 'Ask again' : 'Request', '', function () { return request(false); });
                }
                if (canFourK && !fourKReq && st !== 'none' && st !== 'declined') {
                    addBtn(actions, 'Request in ' + label4k + ' too', 'alt', function () { return request(true); });
                } else if (canFourK && !fourKReq && st === 'none') {
                    addBtn(actions, 'Request in ' + label4k + ' too', 'alt', function () {
                        return request(false).then(function () { return request(true); });
                    });
                }
                if (latest && latest.state === 'pending') {
                    addBtn(actions, 'Cancel request', 'danger', function () {
                        return api('DELETE', 'requests/' + latest.id).then(function () { toast('Request taken back.'); });
                    });
                }
                if (fourKReq && fourKReq.state === 'pending') {
                    addBtn(actions, 'Cancel ' + label4k + ' request', 'danger', function () {
                        return api('DELETE', 'requests/' + fourKReq.id).then(function () { toast('Request taken back.'); });
                    });
                }
                var itemId = latest && latest.jellyfinItemId;
                if (st === 'available' && itemId && !STANDALONE) {
                    addBtn(actions, 'Play', '', function () { close(); closePage(); return play(itemId); });
                    addBtn(actions, 'Open', 'alt', function () { close(); closePage(); location.hash = '#/details?id=' + itemId; return Promise.resolve(); });
                }
                addBtn(actions, 'Close', 'alt', function () { close(); return Promise.resolve(); });
            }).catch(function (e) {
                bg.querySelector('.mdrm-ov').textContent = e.message;
                var actions = bg.querySelector('.mdrm-actions');
                actions.innerHTML = '';
                addBtn(actions, 'Close', 'alt', function () { close(); return Promise.resolve(); });
            });
        }

        function request(fourK) {
            return api('POST', 'requests', { media: media, tmdbId: tmdbId, fourK: fourK }).then(function (res) {
                toast((res && res.message) || 'Asked.');
                quota = null;
            });
        }

        function addBtn(box, label, cls, fn) {
            var b = el('<button type="button" class="mdrm-btn ' + cls + '">' + esc(label) + '</button>');
            b.addEventListener('click', function () {
                box.querySelectorAll('button').forEach(function (x) { x.disabled = true; });
                Promise.resolve().then(fn).catch(function (e) { toast(e.message); }).then(function () {
                    if (document.body.contains(bg)) {
                        render();
                    }
                    if (page && !page.hidden) {
                        refreshPage();
                    }
                    refreshHomeRow();
                });
            });
            box.appendChild(b);
        }

        render().then(function () {
            var first = bg.querySelector('.mdrm-actions button');
            if (first) {
                first.focus();
            }
        });
    }

    function play(itemId) {
        var s = session();
        return call('POST', 'Play', { itemId: itemId, deviceId: s && s.deviceId }).catch(function () {
            location.hash = '#/details?id=' + itemId;
        });
    }

    // ---------- inside Jellyfin Web ----------

    function addMenuEntry() {
        var drawer = document.querySelector('.mainDrawer-scrollContainer');
        if (!drawer || drawer.querySelector('.mdrm-nav')) {
            return;
        }
        var link = el('<a is="emby-linkbutton" class="navMenuOption lnkMediaFolder mdrm-nav" href="#">' +
            '<span class="material-icons navMenuOptionIcon add_circle_outline" aria-hidden="true"></span>' +
            '<span class="sectionName navMenuOptionText">Request</span></a>');
        link.addEventListener('click', function (e) {
            e.preventDefault();
            var bgDrawer = document.querySelector('.mainDrawerHandle, .drawer-backdrop');
            document.body.classList.remove('drawerOpen');
            if (bgDrawer && document.querySelector('.mainDrawer.drawer-open')) {
                bgDrawer.click();
            }
            openPage();
        });
        var home = drawer.querySelector('a.navMenuOption[href*="home"]');
        if (home && home.parentNode) {
            home.parentNode.insertBefore(link, home.nextSibling);
        } else {
            drawer.insertBefore(link, drawer.firstChild);
        }
    }

    var homeRowBusy = false;

    function addHomeRow() {
        if (!/^#\/home/.test(location.hash) && location.hash !== '' && location.hash !== '#/') {
            return;
        }
        var container = document.querySelector('.page:not(.hide) .homeSectionsContainer');
        if (!container || container.querySelector('.mdrm-home') || homeRowBusy) {
            return;
        }
        addStyles();
        var sec = el('<div class="verticalSection mdrm-home">' +
            '<h2 class="sectionTitle sectionTitle-cards padded-left">Request something <button type="button" class="mdrm-more">See all</button></h2>' +
            '<div class="mdrm-row"><div class="mdrm-empty">Loading…</div></div></div>');
        sec.querySelector('.mdrm-more').addEventListener('click', openPage);
        homeRowBusy = true;
        api('GET', 'discover/mixed/trending').then(function (list) {
            homeRowBusy = false;
            list = (list || []).filter(function (t) { return t.state !== 'available'; }).slice(0, 20);
            if (!list.length) {
                return;
            }
            fillRow(sec.querySelector('.mdrm-row'), list);
            var target = document.querySelector('.page:not(.hide) .homeSectionsContainer');
            if (target && !target.querySelector('.mdrm-home')) {
                target.insertBefore(sec, target.firstChild);
            }
        }).catch(function () { homeRowBusy = false; });
    }

    function refreshHomeRow() {
        var old = document.querySelector('.mdrm-home');
        if (old) {
            old.remove();
            addHomeRow();
        }
    }

    // ---------- "ready to watch" ----------

    var shown = {};

    function checkNotices() {
        var s = session();
        if (!s || document.hidden) {
            return;
        }
        call('GET', 'Notices?deviceId=' + encodeURIComponent(s.deviceId || '')).then(function (list) {
            (list || []).forEach(function (n) {
                if (shown[n.eventId]) {
                    return;
                }
                shown[n.eventId] = true;
                showReady(n);
            });
        }).catch(function () { /* try again later */ });
    }

    function showReady(n) {
        addStyles();
        var box = el('<div class="mdrm-ready" role="alert"><b>' + esc(n.title) + ' is ready to watch</b><div class="mdrm-actions" style="margin-top:0"></div></div>');
        var actions = box.querySelector('.mdrm-actions');
        function dismiss() {
            box.remove();
            call('DELETE', 'Notices/' + n.eventId).catch(function () { /* fine */ });
        }
        if (n.itemId) {
            var p = el('<button type="button" class="mdrm-btn">Play</button>');
            p.addEventListener('click', function () { dismiss(); closePage(); play(n.itemId); });
            actions.appendChild(p);
            var o = el('<button type="button" class="mdrm-btn alt">Open</button>');
            o.addEventListener('click', function () { dismiss(); closePage(); location.hash = '#/details?id=' + n.itemId; });
            actions.appendChild(o);
        }
        var c = el('<button type="button" class="mdrm-btn alt">Later</button>');
        c.addEventListener('click', dismiss);
        actions.appendChild(c);
        document.body.appendChild(box);
    }

    // ---------- Jellyfin's own search ----------

    var info = null;

    function serverInfo() {
        if (!info) {
            info = call('GET', 'Info').catch(function () { info = null; return {}; });
        }
        return info;
    }

    var searchShown = '';
    var searchBusy = '';

    function searchQuery() {
        var input = document.querySelector('#searchPage:not(.hide) .searchfields-txtSearch');
        return input ? input.value.trim() : '';
    }

    // Jellyfin's results, or the page itself when the library found nothing
    // (then Jellyfin only shows a "no results" line).
    function searchTarget() {
        var pageEl = document.querySelector('#searchPage:not(.hide)');
        return pageEl && (pageEl.querySelector('.searchResults') || pageEl);
    }

    function addSearchResults() {
        var results = searchTarget();
        if (!results) {
            return;
        }
        serverInfo().then(function (i) { hideDiscoverCards(results, i && i.discoverChannelId); });
        var q = searchQuery();
        var existing = results.querySelector('.mdrm-search');
        if (q.length < 2) {
            document.querySelectorAll('#searchPage .mdrm-search').forEach(function (o) { o.remove(); });
            document.querySelector('#searchPage:not(.hide)').classList.remove('mdrm-has-results');
            searchShown = '';
            return;
        }
        if ((existing && searchShown === q) || searchBusy === q) {
            return;
        }
        // A section from the last query, left outside Jellyfin's new results.
        document.querySelectorAll('#searchPage .mdrm-search').forEach(function (o) { o.remove(); });
        searchBusy = q;
        api('GET', 'search?q=' + encodeURIComponent(q)).then(function (list) {
            searchBusy = '';
            if (searchQuery() !== q) {
                return;
            }
            list = (list || []).filter(function (t) { return t.state !== 'available'; });
            var target = searchTarget();
            if (!target) {
                return;
            }
            document.querySelectorAll('#searchPage .mdrm-search').forEach(function (o) { o.remove(); });
            searchShown = q;
            // Hides Jellyfin's "no results" line while there are some here.
            document.querySelector('#searchPage:not(.hide)').classList.toggle('mdrm-has-results', list.length > 0);
            if (!list.length) {
                return;
            }
            addStyles();
            var sec = el('<div class="verticalSection mdrm-search mdrm-home">' +
                '<h2 class="sectionTitle sectionTitle-cards padded-left padded-right">Not in your library</h2>' +
                '<div class="mdrm-row"></div></div>');
            fillRow(sec.querySelector('.mdrm-row'), list);
            target.appendChild(sec);
        }).catch(function () { searchBusy = ''; });
    }

    // Discover placeholders that Jellyfin's search found are shown in our own
    // section with their state, so they're left out of Jellyfin's rows.
    function hideDiscoverCards(results, channelId) {
        if (!channelId) {
            return;
        }
        results.querySelectorAll('.card[data-channelid="' + channelId + '"]').forEach(function (c) {
            c.classList.add('hide');
        });
        results.querySelectorAll('.verticalSection:not(.mdrm-search)').forEach(function (s) {
            var cards = s.querySelectorAll('.card');
            if (cards.length && s.querySelectorAll('.card:not(.hide)').length === 0) {
                s.classList.add('hide');
            }
        });
    }

    // ---------- Request instead of Play on a Discover title ----------

    var placeholders = {};

    function itemIdFromHash() {
        var m = /[?&]id=([0-9a-f-]{32,36})/i.exec(location.hash);
        return /^#\/details/.test(location.hash) && m ? m[1].replace(/-/g, '').toLowerCase() : '';
    }

    function placeholderOf(id) {
        if (!(id in placeholders)) {
            placeholders[id] = call('GET', 'Placeholder/' + id).catch(function () { return null; });
        }
        return placeholders[id];
    }

    var STATE_LABEL = { none: 'Request', declined: 'Ask again', pending: 'Requested', approved: 'Approved', downloading: 'Downloading', available: 'Ready' };

    function addDetailRequest() {
        var detail = document.querySelector('#itemDetailPage:not(.hide)');
        var id = itemIdFromHash();
        if (!detail || !id) {
            return;
        }
        placeholderOf(id).then(function (p) {
            var page2 = document.querySelector('#itemDetailPage:not(.hide)');
            if (!page2 || itemIdFromHash() !== id) {
                return;
            }
            page2.classList.toggle('mdrm-placeholder', !!p);
            var buttons = page2.querySelector('.mainDetailButtons');
            var old = buttons && buttons.querySelector('.mdrm-reqbtn');
            if (old && old.getAttribute('data-for') !== id) {
                old.remove();
                old = null;
            }
            if (!p || !buttons || old) {
                return;
            }
            addStyles();
            var btn = el('<button is="emby-button" type="button" class="button-flat detailButton emby-button mdrm-reqbtn" data-for="' + esc(id) + '" title="Request">' +
                '<div class="detailButton-content"><span class="material-icons detailButton-icon add_circle_outline" aria-hidden="true"></span>' +
                '<span class="mdrm-reqlabel">Request</span></div></button>');
            buttons.insertBefore(btn, buttons.firstChild);
            var state = 'none';
            var setState = function () {
                return api('GET', 'titles/' + p.media + '/' + p.tmdbId).then(function (t) {
                    var s = (t && t.state) || {};
                    state = s.state || 'none';
                    var label = STATE_LABEL[state] || 'Request';
                    if (state === 'downloading' && s.progress != null) {
                        label += ' ' + Math.round(s.progress) + '%';
                    }
                    btn.querySelector('.mdrm-reqlabel').textContent = label;
                    btn.title = label;
                    btn.classList.toggle('mdrm-reqbtn-done', state !== 'none' && state !== 'declined');
                }).catch(function () { /* keep "Request" */ });
            };
            setState();
            btn.addEventListener('click', function () {
                if (state === 'none' || state === 'declined') {
                    btn.disabled = true;
                    api('POST', 'requests', { media: p.media, tmdbId: p.tmdbId, fourK: false }).then(function (res) {
                        toast((res && res.message) || 'Asked.');
                        quota = null;
                    }).catch(function (e) { toast(e.message); }).then(function () {
                        btn.disabled = false;
                        setState();
                    });
                } else {
                    // Already asked: the panel shows where it stands, 4K and cancel.
                    openTitle(p.media, p.tmdbId, { title: p.title });
                }
            });
        });
    }

    // ---------- start ----------

    function start() {
        addStyles();
        if (STANDALONE) {
            document.body.classList.add('mdrm-standalone');
            if (!session()) {
                document.body.appendChild(el('<div class="mdrm-wrap"><h1>Request</h1><p>Sign in to Jellyfin first, then come back.</p><p><a class="mdrm-btn" href="' + esc(BASE) + '/web/">Open Jellyfin</a></p></div>'));
                return;
            }
            openPage();
            return;
        }
        var tick = function () {
            if (!session()) {
                return;
            }
            addMenuEntry();
            addHomeRow();
            addSearchResults();
            addDetailRequest();
        };
        new MutationObserver(function () {
            clearTimeout(tick.t);
            tick.t = setTimeout(tick, 150);
        }).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', function () {
            closePage();
            setTimeout(tick, 300);
        });
        tick();
        setTimeout(checkNotices, 3000);
        setInterval(checkNotices, 20000);
        document.addEventListener('visibilitychange', function () { if (!document.hidden) { checkNotices(); } });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
