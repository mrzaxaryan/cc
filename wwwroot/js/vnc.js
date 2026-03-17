// VNC panel — hardware-accelerated canvas rendering via createImageBitmap,
// ResizeObserver, mouse/keyboard forwarding, draggable toolbar, fullscreen.

window.c2Vnc = (() => {
    /** @type {Map<string, VncSession>} */
    const sessions = new Map();

    class VncSession {
        constructor(rootEl, canvas, dotnetRef) {
            this.root = rootEl;
            this.canvas = canvas;
            this.ctx = canvas.getContext('2d', { alpha: false });
            this.dotnetRef = dotnetRef;
            /** @type {ImageBitmap|null} */
            this.frame = null;
            this.frameW = 0;
            this.frameH = 0;
            this.pendingPaint = false;

            // Offscreen compositing buffer
            this.offscreen = null;
            this.offCtx = null;

            // Toolbar
            this._toolbarEl = rootEl.querySelector('.vnc-toolbar');
            this._idleTimer = null;
            this._streaming = false;

            // Scale mode: 'stretch' | 'fit' | 'fill'
            this._scaleMode = 'stretch';

            // Toolbar drag state
            this._dragging = false;
            this._dragOffsetX = 0;
            this._dragOffsetY = 0;
            this._toolbarCustomPos = false;

            // Bound handlers
            this._boundResize = () => this._onResize();
            this._boundPointerDown = e => this._onMouse(e, 'down');
            this._boundPointerUp = e => this._onMouse(e, 'up');
            this._boundPointerMove = e => this._onMouse(e, 'move');
            this._boundWheel = e => this._onWheel(e);
            this._boundContextMenu = e => e.preventDefault();
            this._boundKeyDown = e => this._onKey(e, 'down');
            this._boundKeyUp = e => this._onKey(e, 'up');
            this._boundRootMove = () => this._onActivity();
            this._boundToolbarDown = e => this._onToolbarDragStart(e);
            this._boundToolbarMove = e => this._onToolbarDragMove(e);
            this._boundToolbarUp = e => this._onToolbarDragEnd(e);

            // ResizeObserver
            this.ro = new ResizeObserver(this._boundResize);
            this.ro.observe(canvas.parentElement);
            this._syncSize();

            // Canvas mouse events
            canvas.addEventListener('pointerdown', this._boundPointerDown);
            canvas.addEventListener('pointerup', this._boundPointerUp);
            canvas.addEventListener('pointermove', this._boundPointerMove);
            canvas.addEventListener('wheel', this._boundWheel, { passive: false });
            canvas.addEventListener('contextmenu', this._boundContextMenu);

            // Keyboard
            canvas.addEventListener('keydown', this._boundKeyDown);
            canvas.addEventListener('keyup', this._boundKeyUp);
            canvas.tabIndex = 0;

            // Toolbar activity tracking
            rootEl.addEventListener('pointermove', this._boundRootMove);

            // Toolbar drag — only on the drag handle
            const handle = rootEl.querySelector('.vnc-drag-handle');
            if (handle) {
                handle.addEventListener('pointerdown', this._boundToolbarDown);
            }
            document.addEventListener('pointermove', this._boundToolbarMove);
            document.addEventListener('pointerup', this._boundToolbarUp);
        }

        // --- Rendering ---

        async renderFrame(isFullFrame, sections) {
            if (!sections || sections.length === 0) return;

            // Decode all JPEG sections in parallel
            const decoded = await Promise.all(
                sections.map(s => createImageBitmap(new Blob([s.data], { type: 'image/jpeg' })))
            );

            if (isFullFrame) {
                // First section is the full frame — sets dimensions and resets offscreen
                const bmp = decoded[0];
                this.frame?.close();
                this.frame = bmp;
                this.frameW = bmp.width;
                this.frameH = bmp.height;

                this.offscreen = new OffscreenCanvas(bmp.width, bmp.height);
                this.offCtx = this.offscreen.getContext('2d', { alpha: false });
                this.offCtx.drawImage(bmp, 0, 0);

                // Draw any additional sections
                for (let i = 1; i < decoded.length; i++) {
                    this.offCtx.drawImage(decoded[i], sections[i].x, sections[i].y);
                    decoded[i].close();
                }
            } else {
                if (!this.offscreen || !this.offCtx) {
                    for (const bmp of decoded) bmp.close();
                    return;
                }
                // All sections are incremental
                for (let i = 0; i < decoded.length; i++) {
                    this.offCtx.drawImage(decoded[i], sections[i].x, sections[i].y);
                    decoded[i].close();
                }
            }

            this._requestPaint();
        }

        _requestPaint() {
            if (this.pendingPaint) return;
            this.pendingPaint = true;
            requestAnimationFrame(() => {
                this.pendingPaint = false;
                this._paint();
            });
        }

        _paint() {
            const ctx = this.ctx;
            const cw = this.canvas.width;
            const ch = this.canvas.height;

            ctx.fillStyle = '#000';
            ctx.fillRect(0, 0, cw, ch);

            const src = this.offscreen || this.frame;
            if (!src) return;

            const fw = this.frameW;
            const fh = this.frameH;
            let dx = 0, dy = 0, dw = cw, dh = ch;

            if (this._scaleMode === 'fit') {
                // Fit inside canvas, preserving aspect ratio (black bars)
                const scale = Math.min(cw / fw, ch / fh);
                dw = fw * scale;
                dh = fh * scale;
                dx = (cw - dw) / 2;
                dy = (ch - dh) / 2;
            } else if (this._scaleMode === 'fill') {
                // Fill canvas, preserving aspect ratio (cropped)
                const scale = Math.max(cw / fw, ch / fh);
                dw = fw * scale;
                dh = fh * scale;
                dx = (cw - dw) / 2;
                dy = (ch - dh) / 2;
            }
            // 'stretch': dx=0, dy=0, dw=cw, dh=ch (default)

            ctx.drawImage(src, dx, dy, dw, dh);

            // Store for mouse coordinate mapping
            this._dx = dx;
            this._dy = dy;
            this._scaleX = dw / fw;
            this._scaleY = dh / fh;
            this._hasFrame = true;
        }

        setScaleMode(mode) {
            this._scaleMode = mode;
            this._requestPaint();
        }

        // --- Resize ---

        _onResize() {
            this._syncSize();
            this._paint();
            // Re-center toolbar if not manually positioned
            if (!this._toolbarCustomPos) this._centerToolbar();
        }

        _syncSize() {
            const parent = this.canvas.parentElement;
            if (!parent) return;
            const w = parent.clientWidth;
            const h = parent.clientHeight;
            if (w === 0 || h === 0) return;
            // Backing store = container size
            this.canvas.width = w;
            this.canvas.height = h;
            // CSS size must match exactly (canvas is position:absolute)
            this.canvas.style.width = w + 'px';
            this.canvas.style.height = h + 'px';
        }

        // --- Toolbar drag ---

        _onToolbarDragStart(e) {
            if (e.button !== 0) return;
            this._dragging = true;
            const rect = this._toolbarEl.getBoundingClientRect();
            this._dragOffsetX = e.clientX - rect.left;
            this._dragOffsetY = e.clientY - rect.top;
            e.preventDefault();
            e.stopPropagation();
        }

        _onToolbarDragMove(e) {
            if (!this._dragging) return;
            const rootRect = this.root.getBoundingClientRect();
            let x = e.clientX - rootRect.left - this._dragOffsetX;
            let y = e.clientY - rootRect.top - this._dragOffsetY;

            // Clamp within root bounds
            const tw = this._toolbarEl.offsetWidth;
            const th = this._toolbarEl.offsetHeight;
            x = Math.max(0, Math.min(x, rootRect.width - tw));
            y = Math.max(0, Math.min(y, rootRect.height - th));

            this._toolbarEl.style.left = x + 'px';
            this._toolbarEl.style.top = y + 'px';
            this._toolbarEl.style.bottom = 'auto';
            this._toolbarEl.style.transform = 'none';
            this._toolbarCustomPos = true;
        }

        _onToolbarDragEnd() {
            this._dragging = false;
        }

        _centerToolbar() {
            if (!this._toolbarEl) return;
            this._toolbarEl.style.left = '50%';
            this._toolbarEl.style.top = '8px';
            this._toolbarEl.style.bottom = 'auto';
            this._toolbarEl.style.transform = 'translateX(-50%)';
        }

        // --- Toolbar auto-hide ---

        setStreaming(streaming) {
            this._streaming = streaming;
            if (!streaming) {
                this._showToolbar();
                clearTimeout(this._idleTimer);
                this._idleTimer = null;
            } else {
                this._resetIdleTimer();
            }
        }

        _onActivity() {
            this._showToolbar();
            if (this._streaming) this._resetIdleTimer();
        }

        _resetIdleTimer() {
            clearTimeout(this._idleTimer);
            this._idleTimer = setTimeout(() => this._hideToolbar(), 3000);
        }

        _showToolbar() {
            if (this._toolbarEl) this._toolbarEl.classList.remove('vnc-toolbar--hidden');
        }

        _hideToolbar() {
            if (this._toolbarEl && this._streaming) this._toolbarEl.classList.add('vnc-toolbar--hidden');
        }

        // --- Mouse ---

        _toFrameCoords(e) {
            if (!this._hasFrame) return null;
            const rect = this.canvas.getBoundingClientRect();
            const ratioX = this.canvas.width / rect.width;
            const ratioY = this.canvas.height / rect.height;
            const cx = (e.clientX - rect.left) * ratioX;
            const cy = (e.clientY - rect.top) * ratioY;
            const fx = (cx - this._dx) / this._scaleX;
            const fy = (cy - this._dy) / this._scaleY;
            if (fx < 0 || fy < 0 || fx > this.frameW || fy > this.frameH) return null;
            return { x: Math.round(fx), y: Math.round(fy) };
        }

        _onMouse(e, action) {
            const pos = this._toFrameCoords(e);
            if (!pos) return;
            this.dotnetRef.invokeMethodAsync('OnMouseEvent', action, pos.x, pos.y, e.button);
        }

        _onWheel(e) {
            e.preventDefault();
            const pos = this._toFrameCoords(e);
            if (!pos) return;
            this.dotnetRef.invokeMethodAsync('OnWheelEvent', pos.x, pos.y, Math.sign(e.deltaY));
        }

        // --- Keyboard ---

        _onKey(e, action) {
            if (e.ctrlKey && ['c', 'v', 'x', 'a', 'z', 'r', 'l', 'w', 't'].includes(e.key.toLowerCase())) return;
            e.preventDefault();
            this.dotnetRef.invokeMethodAsync('OnKeyEvent', action, e.key, e.code,
                e.ctrlKey, e.shiftKey, e.altKey, e.metaKey);
        }

        // --- Fullscreen ---

        async toggleFullscreen() {
            if (!this.root) return;
            if (document.fullscreenElement === this.root) {
                await document.exitFullscreen();
            } else {
                await this.root.requestFullscreen();
            }
        }

        // --- Cleanup ---

        dispose() {
            this.ro.disconnect();
            clearTimeout(this._idleTimer);

            const c = this.canvas;
            c.removeEventListener('pointerdown', this._boundPointerDown);
            c.removeEventListener('pointerup', this._boundPointerUp);
            c.removeEventListener('pointermove', this._boundPointerMove);
            c.removeEventListener('wheel', this._boundWheel);
            c.removeEventListener('contextmenu', this._boundContextMenu);
            c.removeEventListener('keydown', this._boundKeyDown);
            c.removeEventListener('keyup', this._boundKeyUp);
            this.root.removeEventListener('pointermove', this._boundRootMove);
            document.removeEventListener('pointermove', this._boundToolbarMove);
            document.removeEventListener('pointerup', this._boundToolbarUp);

            this.frame?.close();
            this.frame = null;
            this.offscreen = null;
            this.offCtx = null;
        }
    }

    return {
        init(id, rootEl, canvas, dotnetRef) {
            if (sessions.has(id)) sessions.get(id).dispose();
            sessions.set(id, new VncSession(rootEl, canvas, dotnetRef));
        },

        async renderFrame(id, isFullFrame, sections) {
            const s = sessions.get(id);
            if (s) await s.renderFrame(isFullFrame, sections);
        },

        setStreaming(id, streaming) {
            const s = sessions.get(id);
            if (s) s.setStreaming(streaming);
        },

        setScaleMode(id, mode) {
            const s = sessions.get(id);
            if (s) s.setScaleMode(mode);
        },

        async toggleFullscreen(id) {
            const s = sessions.get(id);
            if (s) await s.toggleFullscreen();
        },

        focus(id) {
            const s = sessions.get(id);
            if (s) s.canvas.focus();
        },

        dispose(id) {
            const s = sessions.get(id);
            if (s) {
                s.dispose();
                sessions.delete(id);
            }
        }
    };
})();
