// PDF Dropper — pdf-lib interop for injecting link annotations into PDFs
// Uses pdf-lib loaded from CDN on first use

let PDFLib = null;

async function ensureLib() {
    if (PDFLib) return;
    const mod = await import('https://cdn.jsdelivr.net/npm/pdf-lib@1.17.1/+esm');
    PDFLib = mod;
}

function toBase64(uint8) {
    const chunk = 0x8000;
    const parts = [];
    for (let i = 0; i < uint8.length; i += chunk) {
        parts.push(String.fromCharCode.apply(null, uint8.subarray(i, i + chunk)));
    }
    return btoa(parts.join(''));
}

window.c2PdfDropper = {

    /** Returns { pageCount: number } or { error: string } */
    async info(base64) {
        try {
            await ensureLib();
            const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
            const pdf = await PDFLib.PDFDocument.load(bytes, { ignoreEncryption: true });
            return { pageCount: pdf.getPageCount() };
        } catch (e) {
            return { error: e.message };
        }
    },

    /** Invisible full-page link annotation on every page */
    async injectLinkAnnotation(base64, url) {
        await ensureLib();
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const pdf = await PDFLib.PDFDocument.load(bytes);
        const { PDFName, PDFArray, PDFString } = PDFLib;

        const pages = pdf.getPages();
        for (const page of pages) {
            const { width, height } = page.getSize();

            const annot = pdf.context.obj({
                Type: 'Annot',
                Subtype: 'Link',
                Rect: [0, 0, width, height],
                Border: [0, 0, 0],
                A: {
                    Type: 'Action',
                    S: 'URI',
                    URI: PDFString.of(url),
                },
                F: 4,
                H: 'N',
            });
            const annotRef = pdf.context.register(annot);

            const existing = page.node.lookup(PDFName.of('Annots'));
            if (existing instanceof PDFArray) {
                existing.push(annotRef);
            } else {
                page.node.set(PDFName.of('Annots'), pdf.context.obj([annotRef]));
            }
        }

        const out = await pdf.save();
        return toBase64(new Uint8Array(out));
    },
};
