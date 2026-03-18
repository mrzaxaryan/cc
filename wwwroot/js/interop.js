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

window.c2GetObjectUrl = function (fileName, fileId) {
    return c2FileSystem.createObjectUrl(fileName, fileId);
};

window.c2RevokeObjectUrl = function (url) {
    if (url) URL.revokeObjectURL(url);
};

// Sync scroll between two elements — used by dual-pane file manager
window.c2SyncScroll = function (sourceEl, targetEl) {
    if (!sourceEl || !targetEl || !sourceEl.addEventListener) return false;
    if (sourceEl._c2SyncCleanup) sourceEl._c2SyncCleanup();
    const onScroll = () => {
        if (targetEl._c2Scrolling) return;
        sourceEl._c2Scrolling = true;
        targetEl.scrollTop = sourceEl.scrollTop;
        requestAnimationFrame(() => { sourceEl._c2Scrolling = false; });
    };
    sourceEl.addEventListener('scroll', onScroll, { passive: true });
    sourceEl._c2SyncCleanup = () => { sourceEl.removeEventListener('scroll', onScroll); delete sourceEl._c2Scrolling; };
    return true;
};

window.downloadFile = function (fileName, base64Data) {
    const byteChars = atob(base64Data);
    const byteNumbers = new Array(byteChars.length);
    for (let i = 0; i < byteChars.length; i++) {
        byteNumbers[i] = byteChars.charCodeAt(i);
    }
    const byteArray = new Uint8Array(byteNumbers);
    const blob = new Blob([byteArray], { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};
