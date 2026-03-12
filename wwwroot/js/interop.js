// Minimal JS interop for Blazor — browser APIs that can't be done from C#

window.c2Interop = {
    setTheme(name) {
        document.documentElement.setAttribute('data-theme', name);
        document.documentElement.setAttribute('data-bs-theme', name);
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.content = name === 'light' ? '#eff1f5' : '#1e1e2e';
    },
    getViewport() {
        return { width: window.innerWidth, height: window.innerHeight };
    },
    onResize(dotnetRef) {
        const handler = () => {
            dotnetRef.invokeMethodAsync('OnViewportResize', window.innerWidth, window.innerHeight);
        };
        window.addEventListener('resize', handler);
        // Return dispose handle
        window._c2ResizeHandler = handler;
        // Send initial size immediately
        handler();
    },
    disposeResize() {
        if (window._c2ResizeHandler) {
            window.removeEventListener('resize', window._c2ResizeHandler);
            window._c2ResizeHandler = null;
        }
    },
    setPointerCapture(element, pointerId) {
        if (element && element.setPointerCapture) {
            try { element.setPointerCapture(pointerId); } catch (_) {}
        }
    }
};

window.c2SaveFile = function (fileName, fileId) {
    return c2FileSystem.saveBlobToDownload(fileName, fileId);
};
