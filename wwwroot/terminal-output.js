window.concertViewerTerminal = window.concertViewerTerminal || {
    scrollToBottom(element) {
        if (!element) {
            return;
        }

        element.scrollTop = element.scrollHeight;
    }
};
