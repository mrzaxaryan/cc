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
    }
};

window.c2SaveFile = function (fileName, fileId) {
    return c2FileSystem.saveBlobToDownload(fileName, fileId);
};

// VNC panel — ResizeObserver to repaint SKCanvasView on container resize
window.c2Vnc = (() => {
    const ro = new ResizeObserver(entries => {
        for (const entry of entries) {
            const ref = entry.target._vncDotnetRef;
            if (ref) ref.invokeMethodAsync('OnContainerResized');
        }
    });
    return {
        observe(el, dotnetRef) {
            if (!el) return;
            el._vncDotnetRef = dotnetRef;
            ro.observe(el);
        },
        unobserve(el) {
            if (!el) return;
            ro.unobserve(el);
            delete el._vncDotnetRef;
        }
    };
})();
