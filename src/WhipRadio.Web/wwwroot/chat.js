window.whipChat = {
    scrollToBottom: function (element, smooth) {
        if (!element) {
            return;
        }

        element.scrollTo({
            top: element.scrollHeight,
            behavior: smooth ? "smooth" : "auto"
        });
    },
    autogrow: function (element, maxRows) {
        if (!element) {
            return;
        }

        const lineHeight = parseFloat(window.getComputedStyle(element).lineHeight) || 20;
        element.style.height = "auto";
        element.style.height = Math.min(element.scrollHeight, lineHeight * maxRows) + "px";
    },
    focus: function (element) {
        if (element) {
            try {
                element.focus();
            } catch {
            }
        }
    },
    storageGet: function (key) {
        try {
            return localStorage.getItem(key);
        } catch {
            return null;
        }
    },
    storageSet: function (key, value) {
        try {
            localStorage.setItem(key, value);
        } catch {
        }
    },
    storageRemove: function (key) {
        try {
            localStorage.removeItem(key);
        } catch {
        }
    },
    wireComposer: function (element) {
        if (!element || element.dataset.whipWired) {
            return;
        }

        element.dataset.whipWired = "1";
        element.addEventListener("keydown", function (event) {
            // Enter sends, Shift+Enter inserts a newline; leave IME composition alone.
            if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                if (element.form) {
                    element.form.requestSubmit();
                }
            }
        });
        element.addEventListener("input", function () {
            // Autosave the draft per channel so it survives navigation away
            // from the chat page. Programmatic value changes (channel switch)
            // do not fire "input", so drafts never leak across channels.
            const channelId = element.dataset.channelId;
            if (!channelId) {
                return;
            }

            const key = "whipchat.draft." + channelId;
            try {
                if (element.value) {
                    localStorage.setItem(key, element.value);
                } else {
                    localStorage.removeItem(key);
                }
            } catch {
            }
        });
    }
};
