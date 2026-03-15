// VNC panel — hardware-accelerated canvas rendering via createImageBitmap,
// ResizeObserver for responsive sizing, mouse/keyboard event forwarding.

window.c2Vnc = (() => {
    /** @type {Map<string, VncSession>} */
    const sessions = new Map();

    class VncSession {
        constructor(canvas, dotnetRef) {
            this.canvas = canvas;
            this.ctx = canvas.getContext('2d', { alpha: false });
            this.dotnetRef = dotnetRef;
            /** @type {ImageBitmap|null} */
            this.frame = null;
            this.frameW = 0;
            this.frameH = 0;
            this.pendingPaint = false;

            // OffscreenCanvas for compositing incremental sections
            this.offscreen = null;
            this.offCtx = null;

            // ResizeObserver
            this.ro = new ResizeObserver(() => this._onResize());
            this.ro.observe(canvas.parentElement);
            this._syncSize();

            // Mouse events
            canvas.addEventListener('pointerdown', e => this._onMouse(e, 'down'));
            canvas.addEventListener('pointerup', e => this._onMouse(e, 'up'));
            canvas.addEventListener('pointermove', e => this._onMouse(e, 'move'));
            canvas.addEventListener('wheel', e => this._onWheel(e), { passive: false });
            canvas.addEventListener('contextmenu', e => e.preventDefault());

            // Keyboard events
            canvas.addEventListener('keydown', e => this._onKey(e, 'down'));
            canvas.addEventListener('keyup', e => this._onKey(e, 'up'));
            canvas.tabIndex = 0;
        }

        // --- Rendering ---

        /** Render a full frame from raw JPEG bytes (Uint8Array) */
        async renderFullFrame(jpegBytes) {
            const blob = new Blob([jpegBytes], { type: 'image/jpeg' });
            const bmp = await createImageBitmap(blob);
            this.frame?.close();
            this.frame = bmp;
            this.frameW = bmp.width;
            this.frameH = bmp.height;

            // Reset offscreen to match frame size
            this.offscreen = new OffscreenCanvas(bmp.width, bmp.height);
            this.offCtx = this.offscreen.getContext('2d', { alpha: false });
            this.offCtx.drawImage(bmp, 0, 0);

            this._requestPaint();
        }

        /** Render incremental sections onto the existing frame */
        async renderSections(sectionsJson) {
            if (!this.offscreen || !this.offCtx) return;

            const sections = JSON.parse(sectionsJson);
            for (const s of sections) {
                const bytes = Uint8Array.from(atob(s.d), c => c.charCodeAt(0));
                const blob = new Blob([bytes], { type: 'image/jpeg' });
                const bmp = await createImageBitmap(blob);
                this.offCtx.drawImage(bmp, s.x, s.y);
                bmp.close();
            }

            // Capture current composite as the display frame
            this.frame?.close();
            this.frame = await createImageBitmap(this.offscreen);
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

            if (!this.frame) return;

            const fw = this.frameW;
            const fh = this.frameH;
            const scale = Math.min(cw / fw, ch / fh);
            const dw = fw * scale;
            const dh = fh * scale;
            const dx = (cw - dw) / 2;
            const dy = (ch - dh) / 2;

            ctx.drawImage(this.frame, dx, dy, dw, dh);

            // Store transform for mouse coordinate mapping
            this._dx = dx;
            this._dy = dy;
            this._scale = scale;
        }

        // --- Resize ---

        _onResize() {
            this._syncSize();
            this._paint();
        }

        _syncSize() {
            const parent = this.canvas.parentElement;
            if (!parent) return;
            const r = parent.getBoundingClientRect();
            const dpr = window.devicePixelRatio || 1;
            this.canvas.width = Math.round(r.width * dpr);
            this.canvas.height = Math.round(r.height * dpr);
            this.canvas.style.width = r.width + 'px';
            this.canvas.style.height = r.height + 'px';
        }

        // --- Mouse ---

        _toFrameCoords(e) {
            if (!this._scale || this._scale === 0) return null;
            const rect = this.canvas.getBoundingClientRect();
            const dpr = window.devicePixelRatio || 1;
            const cx = (e.clientX - rect.left) * dpr;
            const cy = (e.clientY - rect.top) * dpr;
            const fx = (cx - this._dx) / this._scale;
            const fy = (cy - this._dy) / this._scale;
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
            const delta = Math.sign(e.deltaY);
            this.dotnetRef.invokeMethodAsync('OnWheelEvent', pos.x, pos.y, delta);
        }

        // --- Keyboard ---

        _onKey(e, action) {
            // Don't capture browser shortcuts
            if (e.ctrlKey && ['c', 'v', 'x', 'a', 'z', 'r', 'l', 'w', 't'].includes(e.key.toLowerCase())) return;
            e.preventDefault();
            this.dotnetRef.invokeMethodAsync('OnKeyEvent', action, e.key, e.code,
                e.ctrlKey, e.shiftKey, e.altKey, e.metaKey);
        }

        // --- Fullscreen ---

        async toggleFullscreen() {
            const wrap = this.canvas.parentElement;
            if (!wrap) return;
            if (document.fullscreenElement === wrap) {
                await document.exitFullscreen();
            } else {
                await wrap.requestFullscreen();
            }
        }

        // --- Cleanup ---

        dispose() {
            this.ro.disconnect();
            this.frame?.close();
            this.frame = null;
            this.offscreen = null;
            this.offCtx = null;
        }
    }

    return {
        /** Initialize a VNC session for a canvas element */
        init(id, canvas, dotnetRef) {
            if (sessions.has(id)) sessions.get(id).dispose();
            sessions.set(id, new VncSession(canvas, dotnetRef));
        },

        /** Render a full JPEG frame */
        async renderFullFrame(id, jpegBytes) {
            const s = sessions.get(id);
            if (s) await s.renderFullFrame(jpegBytes);
        },

        /** Render incremental JPEG sections */
        async renderSections(id, sectionsJson) {
            const s = sessions.get(id);
            if (s) await s.renderSections(sectionsJson);
        },

        /** Toggle fullscreen */
        async toggleFullscreen(id) {
            const s = sessions.get(id);
            if (s) await s.toggleFullscreen();
        },

        /** Focus the canvas for keyboard input */
        focus(id) {
            const s = sessions.get(id);
            if (s) s.canvas.focus();
        },

        /** Dispose a session */
        dispose(id) {
            const s = sessions.get(id);
            if (s) {
                s.dispose();
                sessions.delete(id);
            }
        }
    };
})();
