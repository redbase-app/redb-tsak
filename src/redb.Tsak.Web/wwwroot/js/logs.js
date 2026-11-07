(function () {
    'use strict';

    window.tsakLogs = {
        // Scroll container to bottom if user is already near the bottom (within 50px)
        autoScroll: function (elementId) {
            var el = document.getElementById(elementId);
            if (!el) return;
            var atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 50;
            if (atBottom) {
                el.scrollTop = el.scrollHeight;
            }
        },
        // Force scroll to bottom
        scrollToBottom: function (elementId) {
            var el = document.getElementById(elementId);
            if (el) el.scrollTop = el.scrollHeight;
        }
    };
})();
