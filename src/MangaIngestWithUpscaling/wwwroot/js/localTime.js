// Reformats UTC instants rendered by the LocalTime component into the browser's local time.
window.localTime = {
    formatElement: function (element) {
        if (!element) return;
        var milliseconds = Number(element.getAttribute('data-utc-ms'));
        if (!isFinite(milliseconds)) return;
        var date = new Date(milliseconds);
        var pad = function (value) { return String(value).padStart(2, '0'); };
        element.textContent =
            date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) + ' ' +
            pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());
    }
};
