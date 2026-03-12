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
    openPopout(panel, width, height, agentUuid, agentName) {
        const params = new URLSearchParams();
        params.set('popout', panel);
        if (agentUuid) params.set('agent', agentUuid);
        if (agentName) params.set('name', agentName);
        const url = window.location.pathname + '?' + params.toString();
        const features = `width=${Math.round(width)},height=${Math.round(height)},menubar=no,toolbar=no,location=no,status=no`;
        window.open(url, '_blank', features);
    }
};

window.c2SaveFile = function (fileName, fileId) {
    return c2FileSystem.saveBlobToDownload(fileName, fileId);
};
