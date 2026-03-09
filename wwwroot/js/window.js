window.windowDrag = {
    _state: null,

    startDrag: function (dotnetHelper, windowId, startX, startY, currentLeft, currentTop) {
        this._state = { dotnetHelper, windowId, startX, startY, currentLeft, currentTop };

        document.addEventListener('mousemove', this._onMouseMove);
        document.addEventListener('mouseup', this._onMouseUp);
    },

    _onMouseMove: function (e) {
        const s = window.windowDrag._state;
        if (!s) return;
        e.preventDefault();
        const dx = e.clientX - s.startX;
        const dy = e.clientY - s.startY;
        s.dotnetHelper.invokeMethodAsync('OnDragMove', s.currentLeft + dx, s.currentTop + dy);
    },

    _onMouseUp: function (e) {
        document.removeEventListener('mousemove', window.windowDrag._onMouseMove);
        document.removeEventListener('mouseup', window.windowDrag._onMouseUp);
        window.windowDrag._state = null;
    }
};
