// Window Manager — JS-driven drag/resize/snap/tile/persistence engine
// All visual movement runs at 60fps in JS. C# is notified only on completion.

(function () {
    'use strict';

    // --- Constants ---
    const SNAP_EDGE_THRESHOLD = 16;   // px from viewport edge to activate snap zone
    const SNAP_WIN_THRESHOLD = 8;     // px for window-to-window magnetic snapping
    const MIN_WIDTH = 320;
    const MIN_HEIGHT = 120;
    const TITLEBAR_H = 36;            // menu bar height (top boundary)
    const TASKBAR_H = 44;             // taskbar height (bottom boundary)
    const GRAB_MARGIN = 80;           // keep at least this many px visible on drag

    // --- State ---
    const _windows = new Map();       // id -> HTMLElement
    let _dotnetRef = null;
    let _vpWidth = window.innerWidth;
    let _vpHeight = window.innerHeight;

    // Active drag state
    let _drag = null;
    // { winId, el, startMouseX, startMouseY, startLeft, startTop, pointerId, preSnapWidth, preSnapHeight }

    // Active resize state
    let _resize = null;
    // { winId, el, edges, startMouseX, startMouseY, startX, startY, startW, startH, pointerId }

    // Snap preview element
    let _snapPreview = null;
    let _currentSnapZone = null;

    // RAF handle for batching
    let _rafId = null;
    let _pendingX = 0, _pendingY = 0;
    let _pendingRect = null;

    // --- Helpers ---
    function usableArea() {
        return { x: 0, y: TITLEBAR_H, w: _vpWidth, h: _vpHeight - TITLEBAR_H - TASKBAR_H };
    }

    function getWinId(el) {
        const attr = el.getAttribute('data-win-id');
        return attr !== null ? parseInt(attr, 10) : null;
    }

    function getWindowEl(id) {
        return _windows.get(id) || document.querySelector(`[data-win-id="${id}"]`);
    }

    function getAllVisibleWindows() {
        const result = [];
        for (const [id, el] of _windows) {
            if (el.classList.contains('minimized') || el.style.display === 'none') continue;
            result.push({ id, el, rect: getElRect(el) });
        }
        return result;
    }

    function getElRect(el) {
        return {
            x: parseFloat(el.style.left) || 0,
            y: parseFloat(el.style.top) || 0,
            w: parseFloat(el.style.width) || el.offsetWidth,
            h: parseFloat(el.style.height) || el.offsetHeight
        };
    }

    // --- Snap Zone Detection ---
    function detectSnapZone(cursorX, cursorY) {
        const ua = usableArea();
        const cornerSize = ua.h * 0.4;

        // Top edge -> maximize
        if (cursorY < TITLEBAR_H + SNAP_EDGE_THRESHOLD) {
            return { zone: 'maximize', rect: { x: ua.x, y: ua.y, w: ua.w, h: ua.h } };
        }

        // Left edge
        if (cursorX < SNAP_EDGE_THRESHOLD) {
            // Top-left corner
            if (cursorY < ua.y + cornerSize) {
                return { zone: 'top-left', rect: { x: ua.x, y: ua.y, w: ua.w / 2, h: ua.h / 2 } };
            }
            // Bottom-left corner
            if (cursorY > ua.y + ua.h - cornerSize) {
                return { zone: 'bottom-left', rect: { x: ua.x, y: ua.y + ua.h / 2, w: ua.w / 2, h: ua.h / 2 } };
            }
            // Left half
            return { zone: 'left-half', rect: { x: ua.x, y: ua.y, w: ua.w / 2, h: ua.h } };
        }

        // Right edge
        if (cursorX > _vpWidth - SNAP_EDGE_THRESHOLD) {
            // Top-right corner
            if (cursorY < ua.y + cornerSize) {
                return { zone: 'top-right', rect: { x: ua.x + ua.w / 2, y: ua.y, w: ua.w / 2, h: ua.h / 2 } };
            }
            // Bottom-right corner
            if (cursorY > ua.y + ua.h - cornerSize) {
                return { zone: 'bottom-right', rect: { x: ua.x + ua.w / 2, y: ua.y + ua.h / 2, w: ua.w / 2, h: ua.h / 2 } };
            }
            // Right half
            return { zone: 'right-half', rect: { x: ua.x + ua.w / 2, y: ua.y, w: ua.w / 2, h: ua.h } };
        }

        // Bottom edge — top/bottom half
        if (cursorY > _vpHeight - TASKBAR_H - SNAP_EDGE_THRESHOLD) {
            if (cursorX < _vpWidth / 2) {
                return { zone: 'bottom-left', rect: { x: ua.x, y: ua.y + ua.h / 2, w: ua.w / 2, h: ua.h / 2 } };
            }
            return { zone: 'bottom-right', rect: { x: ua.x + ua.w / 2, y: ua.y + ua.h / 2, w: ua.w / 2, h: ua.h / 2 } };
        }

        return null;
    }

    // --- Window-to-Window Magnetic Snapping ---
    function magneticSnap(x, y, w, h, dragWinId) {
        let snappedX = x, snappedY = y;
        let snapX = false, snapY = false;

        for (const [id, el] of _windows) {
            if (id === dragWinId) continue;
            if (el.classList.contains('minimized') || el.style.display === 'none') continue;

            const other = getElRect(el);

            // Left edge of dragged -> right edge of other
            if (!snapX && Math.abs(x - (other.x + other.w)) < SNAP_WIN_THRESHOLD) {
                snappedX = other.x + other.w; snapX = true;
            }
            // Right edge of dragged -> left edge of other
            if (!snapX && Math.abs((x + w) - other.x) < SNAP_WIN_THRESHOLD) {
                snappedX = other.x - w; snapX = true;
            }
            // Left edge alignment
            if (!snapX && Math.abs(x - other.x) < SNAP_WIN_THRESHOLD) {
                snappedX = other.x; snapX = true;
            }
            // Right edge alignment
            if (!snapX && Math.abs((x + w) - (other.x + other.w)) < SNAP_WIN_THRESHOLD) {
                snappedX = other.x + other.w - w; snapX = true;
            }

            // Top edge of dragged -> bottom edge of other
            if (!snapY && Math.abs(y - (other.y + other.h)) < SNAP_WIN_THRESHOLD) {
                snappedY = other.y + other.h; snapY = true;
            }
            // Bottom edge of dragged -> top edge of other
            if (!snapY && Math.abs((y + h) - other.y) < SNAP_WIN_THRESHOLD) {
                snappedY = other.y - h; snapY = true;
            }
            // Top edge alignment
            if (!snapY && Math.abs(y - other.y) < SNAP_WIN_THRESHOLD) {
                snappedY = other.y; snapY = true;
            }
            // Bottom edge alignment
            if (!snapY && Math.abs((y + h) - (other.y + other.h)) < SNAP_WIN_THRESHOLD) {
                snappedY = other.y + other.h - h; snapY = true;
            }
        }

        return { x: snappedX, y: snappedY };
    }

    // --- Snap Preview ---
    function showSnapPreview(rect) {
        if (!_snapPreview) return;
        _snapPreview.style.left = rect.x + 'px';
        _snapPreview.style.top = rect.y + 'px';
        _snapPreview.style.width = rect.w + 'px';
        _snapPreview.style.height = rect.h + 'px';
        _snapPreview.classList.add('visible');
    }

    function hideSnapPreview() {
        if (_snapPreview) _snapPreview.classList.remove('visible');
        _currentSnapZone = null;
    }

    // --- Drag ---
    function startDrag(e, titlebar) {
        if (_vpWidth <= 768) return; // Mobile: skip

        const winEl = titlebar.closest('.app-window');
        if (!winEl) return;
        const winId = getWinId(winEl);
        if (winId === null) return;

        // If maximized, skip drag (could implement un-maximize-on-drag later)
        if (winEl.classList.contains('maximized')) return;

        const rect = getElRect(winEl);
        _drag = {
            winId,
            el: winEl,
            startMouseX: e.clientX,
            startMouseY: e.clientY,
            startLeft: rect.x,
            startTop: rect.y,
            pointerId: e.pointerId,
            width: rect.w,
            height: rect.h,
            // Store pre-snap size for snap-then-restore behavior
            preSnapWidth: rect.w,
            preSnapHeight: rect.h
        };

        winEl.classList.add('win-dragging');
        winEl.setAttribute('data-dragging', '');
        winEl.setPointerCapture(e.pointerId);

        // Bring to front via .NET
        if (_dotnetRef) {
            _dotnetRef.invokeMethodAsync('OnBringToFront', winId);
        }

        e.preventDefault();
    }

    function moveDrag(e) {
        if (!_drag) return;

        const dx = e.clientX - _drag.startMouseX;
        const dy = e.clientY - _drag.startMouseY;
        let newX = _drag.startLeft + dx;
        let newY = _drag.startTop + dy;

        // Clamp: keep at least GRAB_MARGIN visible
        newX = Math.max(-(_drag.width - GRAB_MARGIN), Math.min(newX, _vpWidth - GRAB_MARGIN));
        newY = Math.max(TITLEBAR_H, Math.min(newY, _vpHeight - GRAB_MARGIN));

        // Window-to-window magnetic snap
        const mag = magneticSnap(newX, newY, _drag.width, _drag.height, _drag.winId);
        newX = mag.x;
        newY = mag.y;

        // Detect snap zone
        const snap = detectSnapZone(e.clientX, e.clientY);
        if (snap) {
            if (_currentSnapZone !== snap.zone) {
                _currentSnapZone = snap.zone;
                showSnapPreview(snap.rect);
            }
        } else {
            if (_currentSnapZone) hideSnapPreview();
        }

        // Apply position directly — no Blazor roundtrip
        _pendingX = newX;
        _pendingY = newY;
        if (!_rafId) {
            _rafId = requestAnimationFrame(() => {
                if (_drag) {
                    _drag.el.style.left = _pendingX + 'px';
                    _drag.el.style.top = _pendingY + 'px';
                }
                _rafId = null;
            });
        }
    }

    function endDrag(e) {
        if (!_drag) return;
        const d = _drag;
        _drag = null;

        if (_rafId) { cancelAnimationFrame(_rafId); _rafId = null; }

        d.el.classList.remove('win-dragging');
        d.el.removeAttribute('data-dragging');
        hideSnapPreview();

        const snap = detectSnapZone(e.clientX, e.clientY);
        if (snap && _dotnetRef) {
            // Maximize snap zone — use proper maximize state instead of snap
            if (snap.zone === 'maximize') {
                _dotnetRef.invokeMethodAsync('OnWindowAction', d.winId, 'maximize');
                autoSave();
            } else {
                // Apply snap with animation
                d.el.classList.add('win-animating');
                d.el.classList.remove('maximized');
                d.el.style.left = snap.rect.x + 'px';
                d.el.style.top = snap.rect.y + 'px';
                d.el.style.width = snap.rect.w + 'px';
                d.el.style.height = snap.rect.h + 'px';

                d.el.addEventListener('transitionend', function onEnd() {
                    d.el.removeEventListener('transitionend', onEnd);
                    d.el.classList.remove('win-animating');
                });

                _dotnetRef.invokeMethodAsync('OnSnapApplied', d.winId,
                    snap.rect.x, snap.rect.y, snap.rect.w, snap.rect.h, snap.zone);

                autoSave();
            }
        } else {
            // If was previously snapped/maximized, clear those classes
            d.el.classList.remove('maximized');

            // Normal drag end — sync final position
            const finalX = parseFloat(d.el.style.left) || 0;
            const finalY = parseFloat(d.el.style.top) || 0;

            if (_dotnetRef) {
                _dotnetRef.invokeMethodAsync('OnDragEnd', d.winId, finalX, finalY);
            }
            autoSave();
        }
    }

    // --- Resize ---
    function startResize(e, handle) {
        if (_vpWidth <= 768) return;

        const winEl = handle.closest('.app-window');
        if (!winEl || winEl.classList.contains('maximized')) return;
        const winId = getWinId(winEl);
        if (winId === null) return;

        // Determine edges from class name: app-window-resize-{edge}
        const cls = handle.className;
        const match = cls.match(/app-window-resize-(\w+)/);
        if (!match) return;
        const edgeStr = match[1].toUpperCase(); // e.g., "nw", "se", "n"

        const rect = getElRect(winEl);
        _resize = {
            winId, el: winEl,
            edges: edgeStr,
            startMouseX: e.clientX, startMouseY: e.clientY,
            startX: rect.x, startY: rect.y,
            startW: rect.w, startH: rect.h,
            pointerId: e.pointerId
        };

        winEl.classList.add('win-resizing');
        winEl.setAttribute('data-resizing', '');
        winEl.setPointerCapture(e.pointerId);

        if (_dotnetRef) {
            _dotnetRef.invokeMethodAsync('OnBringToFront', winId);
        }

        e.preventDefault();
        e.stopPropagation();
    }

    function moveResize(e) {
        if (!_resize) return;
        const r = _resize;
        const dx = e.clientX - r.startMouseX;
        const dy = e.clientY - r.startMouseY;
        const edges = r.edges;

        let x = r.startX, y = r.startY, w = r.startW, h = r.startH;

        // East
        if (edges.includes('E')) {
            w = Math.max(MIN_WIDTH, Math.min(r.startW + dx, _vpWidth - r.startX));
        }
        // South
        if (edges.includes('S')) {
            h = Math.max(MIN_HEIGHT, Math.min(r.startH + dy, _vpHeight - r.startY));
        }
        // West
        if (edges.includes('W')) {
            let newW = Math.max(MIN_WIDTH, r.startW - dx);
            let newX = r.startX + (r.startW - newW);
            if (newX < 0) { newW += newX; newX = 0; newW = Math.max(MIN_WIDTH, newW); }
            x = newX; w = newW;
        }
        // North
        if (edges.includes('N')) {
            let newH = Math.max(MIN_HEIGHT, r.startH - dy);
            let newY = r.startY + (r.startH - newH);
            if (newY < TITLEBAR_H) { newH -= (TITLEBAR_H - newY); newY = TITLEBAR_H; newH = Math.max(MIN_HEIGHT, newH); }
            y = newY; h = newH;
        }

        _pendingRect = { x, y, w, h };
        if (!_rafId) {
            _rafId = requestAnimationFrame(() => {
                if (_resize && _pendingRect) {
                    _resize.el.style.left = _pendingRect.x + 'px';
                    _resize.el.style.top = _pendingRect.y + 'px';
                    _resize.el.style.width = _pendingRect.w + 'px';
                    _resize.el.style.height = _pendingRect.h + 'px';
                }
                _rafId = null;
            });
        }
    }

    function endResize(e) {
        if (!_resize) return;
        const r = _resize;
        _resize = null;

        if (_rafId) { cancelAnimationFrame(_rafId); _rafId = null; }

        r.el.classList.remove('win-resizing');
        r.el.removeAttribute('data-resizing');

        const finalX = parseFloat(r.el.style.left) || 0;
        const finalY = parseFloat(r.el.style.top) || 0;
        const finalW = parseFloat(r.el.style.width) || r.startW;
        const finalH = parseFloat(r.el.style.height) || r.startH;

        if (_dotnetRef) {
            _dotnetRef.invokeMethodAsync('OnResizeEnd', r.winId, finalX, finalY, finalW, finalH);
        }
        autoSave();
    }

    // --- Global Pointer Listeners ---
    function onPointerDown(e) {
        // Titlebar drag
        const titlebar = e.target.closest('.app-window-titlebar');
        if (titlebar && !e.target.closest('.app-window-controls')) {
            startDrag(e, titlebar);
            return;
        }

        // Resize handle
        const handle = e.target.closest('[class*="app-window-resize-"]');
        if (handle) {
            startResize(e, handle);
            return;
        }

        // Click anywhere on a window — bring to front
        const winEl = e.target.closest('.app-window');
        if (winEl && _dotnetRef) {
            const winId = getWinId(winEl);
            if (winId !== null) {
                _dotnetRef.invokeMethodAsync('OnBringToFront', winId);
            }
        }
    }

    function onPointerMove(e) {
        if (_drag) { moveDrag(e); e.preventDefault(); }
        else if (_resize) { moveResize(e); e.preventDefault(); }
    }

    function onPointerUp(e) {
        if (_drag) endDrag(e);
        if (_resize) endResize(e);
    }

    function onPointerCancel(e) {
        if (_drag) endDrag(e);
        if (_resize) endResize(e);
    }

    // --- Animations ---
    function animateWindow(el, targetRect) {
        return new Promise(resolve => {
            el.classList.add('win-animating');
            el.style.left = targetRect.x + 'px';
            el.style.top = targetRect.y + 'px';
            el.style.width = targetRect.w + 'px';
            el.style.height = targetRect.h + 'px';

            function onEnd() {
                el.removeEventListener('transitionend', onEnd);
                el.classList.remove('win-animating');
                resolve();
            }
            el.addEventListener('transitionend', onEnd);
            // Fallback timeout in case transitionend doesn't fire
            setTimeout(() => { el.classList.remove('win-animating'); resolve(); }, 300);
        });
    }

    function animateMinimize(el) {
        return new Promise(resolve => {
            el.classList.add('win-minimizing');
            function onEnd() {
                el.removeEventListener('transitionend', onEnd);
                resolve();
            }
            el.addEventListener('transitionend', onEnd);
            setTimeout(resolve, 300);
        });
    }

    function animateRestore(el) {
        // Remove minimized state, then remove the minimizing class to trigger reverse animation
        el.classList.remove('win-minimizing');
    }

    function animateClose(el) {
        return new Promise(resolve => {
            el.classList.add('win-closing');
            function onEnd() {
                el.removeEventListener('transitionend', onEnd);
                resolve();
            }
            el.addEventListener('transitionend', onEnd);
            setTimeout(resolve, 250);
        });
    }

    // --- Tiling Layouts ---
    function computeTileLayout(layout, windowIds) {
        const ua = usableArea();
        const gap = 4;
        const ids = windowIds || Array.from(_windows.keys()).filter(id => {
            const el = _windows.get(id);
            return el && !el.classList.contains('minimized');
        });
        const n = ids.length;
        if (n === 0) return [];

        const results = [];

        switch (layout) {
            case 'cascade': {
                const baseW = Math.min(700, ua.w * 0.6);
                const baseH = Math.min(500, ua.h * 0.6);
                ids.forEach((id, i) => {
                    results.push({
                        id,
                        rect: {
                            x: ua.x + 30 * i,
                            y: ua.y + 30 * i,
                            w: baseW,
                            h: baseH
                        }
                    });
                });
                break;
            }
            case 'side-by-side': {
                const colW = (ua.w - gap * (n - 1)) / n;
                ids.forEach((id, i) => {
                    results.push({
                        id,
                        rect: {
                            x: ua.x + i * (colW + gap),
                            y: ua.y,
                            w: colW,
                            h: ua.h
                        }
                    });
                });
                break;
            }
            case 'stack': {
                const rowH = (ua.h - gap * (n - 1)) / n;
                ids.forEach((id, i) => {
                    results.push({
                        id,
                        rect: {
                            x: ua.x,
                            y: ua.y + i * (rowH + gap),
                            w: ua.w,
                            h: rowH
                        }
                    });
                });
                break;
            }
            case 'grid': {
                const cols = Math.ceil(Math.sqrt(n));
                const rows = Math.ceil(n / cols);
                const cellW = (ua.w - gap * (cols - 1)) / cols;
                const cellH = (ua.h - gap * (rows - 1)) / rows;
                ids.forEach((id, i) => {
                    const col = i % cols;
                    const row = Math.floor(i / cols);
                    results.push({
                        id,
                        rect: {
                            x: ua.x + col * (cellW + gap),
                            y: ua.y + row * (cellH + gap),
                            w: cellW,
                            h: cellH
                        }
                    });
                });
                break;
            }
        }

        return results;
    }

    // --- Persistence ---
    function getLayoutKey(name) { return 'c2-layout-' + (name || '__auto__'); }

    function saveLayout(name) {
        const layout = [];
        for (const [id, el] of _windows) {
            const rect = getElRect(el);
            const isMin = el.classList.contains('minimized');
            const isMax = el.classList.contains('maximized');
            const panel = el.getAttribute('data-panel') || '';
            const agentUuid = el.getAttribute('data-agent-uuid') || null;
            const snapZone = el.getAttribute('data-snap-zone') || null;

            layout.push({
                panel, agentUuid, snapZone,
                x: rect.x, y: rect.y, width: rect.w, height: rect.h,
                minimized: isMin, maximized: isMax
            });
        }
        try {
            localStorage.setItem(getLayoutKey(name), JSON.stringify(layout));
        } catch (e) { /* quota exceeded — silently fail */ }
    }

    function loadLayout(name) {
        try {
            const json = localStorage.getItem(getLayoutKey(name));
            return json ? JSON.parse(json) : null;
        } catch (e) { return null; }
    }

    function listLayouts() {
        const result = [];
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            if (key && key.startsWith('c2-layout-') && key !== 'c2-layout-__auto__') {
                result.push(key.substring('c2-layout-'.length));
            }
        }
        return result;
    }

    function deleteLayout(name) {
        localStorage.removeItem(getLayoutKey(name));
    }

    function autoSave() {
        saveLayout('__auto__');
    }

    // --- Keyboard Shortcuts ---
    function onKeyDown(e) {
        if (!e.ctrlKey || !e.altKey) return;

        // Find focused (top z-index) window
        let topWin = null, topZ = -1;
        for (const [id, el] of _windows) {
            if (el.classList.contains('minimized')) continue;
            const z = parseInt(el.style.zIndex) || 0;
            if (z > topZ) { topZ = z; topWin = { id, el }; }
        }

        const ua = usableArea();

        switch (e.key) {
            case 'ArrowLeft':
                if (topWin) {
                    e.preventDefault();
                    snapWindowTo(topWin.id, topWin.el, 'left-half',
                        { x: ua.x, y: ua.y, w: ua.w / 2, h: ua.h });
                }
                break;
            case 'ArrowRight':
                if (topWin) {
                    e.preventDefault();
                    snapWindowTo(topWin.id, topWin.el, 'right-half',
                        { x: ua.x + ua.w / 2, y: ua.y, w: ua.w / 2, h: ua.h });
                }
                break;
            case 'ArrowUp':
                if (topWin) {
                    e.preventDefault();
                    if (_dotnetRef) _dotnetRef.invokeMethodAsync('OnWindowAction', topWin.id, 'maximize');
                }
                break;
            case 'ArrowDown':
                if (topWin) {
                    e.preventDefault();
                    if (_dotnetRef) _dotnetRef.invokeMethodAsync('OnWindowAction', topWin.id, 'restore');
                }
                break;
            case 'g':
            case 'G':
                e.preventDefault();
                tileAll('grid');
                break;
            case 's':
            case 'S':
                e.preventDefault();
                tileAll('side-by-side');
                break;
        }
    }

    function snapWindowTo(winId, el, zone, rect) {
        if (zone === 'maximize') {
            el.classList.add('win-animating');
            el.classList.add('maximized');
            el.style.left = '';
            el.style.top = '';
            el.style.width = '';
            el.style.height = '';
            setTimeout(() => el.classList.remove('win-animating'), 250);
        } else {
            el.classList.remove('maximized');
            animateWindow(el, rect);
        }
        if (_dotnetRef) {
            _dotnetRef.invokeMethodAsync('OnSnapApplied', winId, rect.x, rect.y, rect.w, rect.h, zone);
        }
        autoSave();
    }

    function tileAll(layout) {
        const tiles = computeTileLayout(layout);
        tiles.forEach(t => {
            const el = _windows.get(t.id);
            if (el) animateWindow(el, t.rect);
        });
        if (_dotnetRef) {
            _dotnetRef.invokeMethodAsync('OnTileApplied', layout,
                tiles.map(t => t.id),
                tiles.map(t => t.rect.x), tiles.map(t => t.rect.y),
                tiles.map(t => t.rect.w), tiles.map(t => t.rect.h));
        }
        autoSave();
    }

    // --- Titlebar Double-Click (maximize toggle) ---
    function onDblClick(e) {
        const titlebar = e.target.closest('.app-window-titlebar');
        if (!titlebar || e.target.closest('.app-window-controls')) return;

        const winEl = titlebar.closest('.app-window');
        if (!winEl) return;
        const winId = getWinId(winEl);
        if (winId === null) return;

        if (_dotnetRef) {
            _dotnetRef.invokeMethodAsync('OnWindowAction', winId, 'toggle-maximize');
        }
        e.preventDefault();
    }

    // --- Viewport ---
    function onWindowResize() {
        _vpWidth = window.innerWidth;
        _vpHeight = window.innerHeight;
        if (_dotnetRef) {
            _dotnetRef.invokeMethodAsync('OnViewportResize', _vpWidth, _vpHeight);
        }
    }

    // --- Public API ---
    window.c2Windows = {
        init(dotnetRef) {
            _dotnetRef = dotnetRef;
            _snapPreview = document.getElementById('c2-snap-preview');

            document.addEventListener('pointerdown', onPointerDown, true);
            document.addEventListener('pointermove', onPointerMove, true);
            document.addEventListener('pointerup', onPointerUp, true);
            document.addEventListener('pointercancel', onPointerCancel, true);
            document.addEventListener('dblclick', onDblClick, true);
            document.addEventListener('keydown', onKeyDown, true);
            window.addEventListener('resize', onWindowResize);

            // Send initial viewport size
            onWindowResize();
        },

        dispose() {
            document.removeEventListener('pointerdown', onPointerDown, true);
            document.removeEventListener('pointermove', onPointerMove, true);
            document.removeEventListener('pointerup', onPointerUp, true);
            document.removeEventListener('pointercancel', onPointerCancel, true);
            document.removeEventListener('dblclick', onDblClick, true);
            document.removeEventListener('keydown', onKeyDown, true);
            window.removeEventListener('resize', onWindowResize);
            _windows.clear();
            _dotnetRef = null;
            _drag = null;
            _resize = null;
        },

        registerWindow(id) {
            const el = document.querySelector(`[data-win-id="${id}"]`);
            if (el) _windows.set(id, el);
        },

        unregisterWindow(id) {
            _windows.delete(id);
        },

        bringToFront(id, zIndex) {
            const el = getWindowEl(id);
            if (el) el.style.zIndex = zIndex;
        },

        applyPosition(id, x, y, w, h, animate) {
            const el = getWindowEl(id);
            if (!el) return;
            if (animate) {
                animateWindow(el, { x, y, w, h });
            } else {
                el.style.left = x + 'px';
                el.style.top = y + 'px';
                el.style.width = w + 'px';
                el.style.height = h + 'px';
            }
        },

        setMaximized(id, maximized) {
            const el = getWindowEl(id);
            if (!el) return;
            el.classList.add('win-animating');
            if (maximized) {
                el.classList.add('maximized');
            } else {
                el.classList.remove('maximized');
            }
            setTimeout(() => el.classList.remove('win-animating'), 250);
        },

        async setMinimized(id, minimized) {
            const el = getWindowEl(id);
            if (!el) return;
            if (minimized) {
                await animateMinimize(el);
                el.classList.add('minimized');
                el.classList.remove('win-minimizing');
            } else {
                el.classList.remove('minimized');
                animateRestore(el);
            }
        },

        async animateClose(id) {
            const el = getWindowEl(id);
            if (el) await animateClose(el);
        },

        tileWindows(layout, windowIds) {
            const tiles = computeTileLayout(layout, windowIds);
            const promises = [];
            tiles.forEach(t => {
                const el = _windows.get(t.id);
                if (el) promises.push(animateWindow(el, t.rect));
            });
            return Promise.all(promises);
        },

        // Persistence
        saveLayout(name) { saveLayout(name); },
        loadLayout(name) { return loadLayout(name); },
        listLayouts() { return listLayouts(); },
        deleteLayout(name) { deleteLayout(name); },

        // Viewport
        getViewport() {
            return { width: _vpWidth, height: _vpHeight };
        }
    };
})();
