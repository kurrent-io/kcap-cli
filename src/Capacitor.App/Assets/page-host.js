// Relays the page frame's bridge messages to the app, stamped with this load's nonce. The frame is
// cross-origin to this document, so page script can reach neither the nonce nor this closure.
(function (nonce) {
    'use strict';
    var MAX_MESSAGE_LENGTH = 256 * 1024;
    var toApp = typeof window.invokeCSharpAction === 'function' ? window.invokeCSharpAction : null;
    var stringify = JSON.stringify;
    var assign = Object.assign;
    var frame = null;

    function pageWindow() {
        if (!frame) frame = document.getElementById('kcap-page');
        return frame ? frame.contentWindow : null;
    }

    function withinSize(data) {
        try {
            return stringify(data).length <= MAX_MESSAGE_LENGTH;
        } catch (e) {
            return false;
        }
    }

    window.addEventListener('message', function (event) {
        var data = event.data;
        if (!toApp || event.source === null || event.source !== pageWindow() || event.origin !== 'null') return;
        if (!data || typeof data !== 'object' || data.kcap !== 'v1' || typeof data.type !== 'string') return;
        if (!withinSize(data)) return;
        toApp(stringify({ nonce: nonce, data: data }));
    });

    // Opaque-origin frames only receive messages posted to '*'.
    window.kcapDeliver = function (message) {
        var target = pageWindow();
        if (target) target.postMessage(assign({ kcap: 'v1' }, message), '*');
    };

    if (document.currentScript) document.currentScript.remove();
})("__KCAP_NONCE__");
