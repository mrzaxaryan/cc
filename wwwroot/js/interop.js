// Minimal JS interop for Blazor — browser APIs that can't be done from C#

window.ccInterop = {
    setTheme(name) {
        document.documentElement.setAttribute('data-theme', name);
        document.documentElement.setAttribute('data-bs-theme', name);
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.content = name === 'light' ? '#eff1f5' : '#1e1e2e';
    }
};

window.ccSaveFile = function (fileName, fileId) {
    return ccFileSystem.saveBlobToDownload(fileName, fileId);
};
