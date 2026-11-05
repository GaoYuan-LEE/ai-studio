(() => {
    "use strict";

    const $ = (id) => document.getElementById(id);

    const dom = {
        chatScroll: $("chatScroll"),
        messages: $("messages"),
        emptyState: $("emptyState"),
        modelBadge: $("modelBadge"),
        input: $("input"),
        sendBtn: $("sendBtn"),
        stopBtn: $("stopBtn"),
        newChatBtn: $("newChatBtn"),
        settingsBtn: $("settingsBtn"),
        emptySettingsBtn: $("emptySettingsBtn"),
        settingsModal: $("settingsModal"),
        closeSettingsBtn: $("closeSettingsBtn"),
        cancelSettingsBtn: $("cancelSettingsBtn"),
        saveSettingsBtn: $("saveSettingsBtn"),
        baseUrl: $("baseUrl"),
        apiKey: $("apiKey"),
        model: $("model"),
        toggleKey: $("toggleKey"),
        settingsError: $("settingsError"),
        toast: $("toast")
    };

    const HISTORY_KEY = "ai-workbench.history.v1";
    const MAX_HISTORY = 100;

    const state = {
        config: { baseUrl: "", model: "", apiKey: "" },
        messages: [],
        streaming: false,
        requestId: null,
        streamText: "",
        assistantRow: null,
        assistantMd: null,
        renderTimer: null,
        firstRun: false,
        saveTimer: null
    };

    const presets = {
        deepseek: {
            baseUrl: "https://api.deepseek.com/v1",
            model: "deepseek-chat"
        },
        openai: {
            baseUrl: "https://api.openai.com/v1",
            model: "gpt-4o-mini"
        },
        ollama: {
            baseUrl: "http://localhost:11434/v1",
            model: "qwen2.5:7b"
        }
    };

    let toastTimer = null;

    marked.setOptions({
        gfm: true,
        breaks: true,
        headerIds: false,
        mangle: false
    });

    function bridgeAvailable() {
        return !!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage);
    }

    function postToHost(payload) {
        if (!bridgeAvailable()) {
            return false;
        }
        try {
            window.chrome.webview.postMessage(payload);
            return true;
        } catch (err) {
            return false;
        }
    }

    function escapeHtml(value) {
        return String(value)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    function renderMarkdown(text) {
        if (!text) {
            return "";
        }
        try {
            const raw = marked.parse(text, { async: false });
            return DOMPurify.sanitize(raw, {
                USE_PROFILES: { html: true },
                ADD_ATTR: ["target", "rel"]
            });
        } catch (err) {
            return escapeHtml(text).replace(/\n/g, "<br>");
        }
    }

    function enhanceLinks(container) {
        container.querySelectorAll("a").forEach((link) => {
            link.target = "_blank";
            link.rel = "noopener noreferrer";
        });
    }

    function createAvatar(role) {
        const avatar = document.createElement("div");
        avatar.className = "avatar " + (role === "user" ? "user" : "ai");
        avatar.textContent = role === "user" ? "你" : "AI";
        return avatar;
    }

    function userRow(text) {
        const row = document.createElement("article");
        row.className = "message user";

        const bubble = document.createElement("div");
        bubble.className = "bubble";
        bubble.textContent = text;

        row.appendChild(bubble);
        row.appendChild(createAvatar("user"));
        return row;
    }

    function assistantRow(text, streaming) {
        const row = document.createElement("article");
        row.className = "message assistant" + (streaming ? " streaming" : "");

        const avatar = createAvatar("assistant");
        const bubble = document.createElement("div");
        bubble.className = "bubble";

        const md = document.createElement("div");
        md.className = "markdown";
        if (text) {
            md.innerHTML = renderMarkdown(text);
            enhanceLinks(md);
        } else {
            md.classList.add("is-empty");
        }

        bubble.appendChild(md);
        row.appendChild(avatar);
        row.appendChild(bubble);
        return { row, md, bubble };
    }

    function addHistoryOrMessage(role, content) {
        if (role === "user") {
            dom.messages.appendChild(userRow(content));
            return;
        }

        const built = assistantRow(content, false);
        dom.messages.appendChild(built.row);
        if (content) {
            addMessageActions(built.bubble, content);
        }
    }

    function addMessageActions(bubble, text) {
        const existing = bubble.querySelector(".msg-actions");
        if (existing) {
            return;
        }

        const actions = document.createElement("div");
        actions.className = "msg-actions";

        const copy = document.createElement("button");
        copy.type = "button";
        copy.className = "action-btn";
        copy.textContent = "复制";
        copy.addEventListener("click", () => copyText(text, copy));

        actions.appendChild(copy);
        bubble.appendChild(actions);
    }

    async function copyText(text, button) {
        let ok = false;
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                await navigator.clipboard.writeText(text);
                ok = true;
            }
        } catch (err) {
            ok = false;
        }

        if (!ok) {
            try {
                const area = document.createElement("textarea");
                area.value = text;
                area.style.position = "fixed";
                area.style.opacity = "0";
                document.body.appendChild(area);
                area.select();
                ok = document.execCommand("copy");
                document.body.removeChild(area);
            } catch (err) {
                ok = false;
            }
        }

        if (ok) {
            button.textContent = "已复制";
            window.setTimeout(() => {
                button.textContent = "复制";
            }, 1500);
        } else {
            toast("复制失败");
        }
    }

    function addError(message) {
        const box = document.createElement("div");
        box.className = "error-box";
        const title = document.createElement("strong");
        title.textContent = "请求失败";
        const body = document.createElement("span");
        body.textContent = String(message || "未知错误").slice(0, 1200);
        box.appendChild(title);
        box.appendChild(body);
        dom.messages.appendChild(box);
        scrollToBottom(true);
    }

    function updateEmptyState() {
        const empty = state.messages.length === 0;
        dom.emptyState.classList.toggle("hidden", !empty);
    }

    function updateModelBadge() {
        if (!state.config.model) {
            dom.modelBadge.textContent = "未配置模型";
            dom.modelBadge.title = "点击“设置”配置 API";
            return;
        }
        dom.modelBadge.textContent = state.config.model;
        dom.modelBadge.title = state.config.baseUrl || state.config.model;
    }

    function updateComposerState() {
        dom.stopBtn.classList.toggle("hidden", !state.streaming);
        dom.stopBtn.disabled = false;
        dom.stopBtn.textContent = "停止生成";
        dom.sendBtn.disabled = state.streaming || dom.input.value.trim().length === 0;
    }

    function resizeInput() {
        dom.input.style.height = "auto";
        dom.input.style.height = Math.min(168, Math.max(51, dom.input.scrollHeight)) + "px";
    }

    function isNearBottom() {
        const el = dom.chatScroll;
        return el.scrollHeight - el.scrollTop - el.clientHeight < 160;
    }

    function scrollToBottom(force) {
        if (!force && !isNearBottom()) {
            return;
        }
        window.requestAnimationFrame(() => {
            dom.chatScroll.scrollTop = dom.chatScroll.scrollHeight;
        });
    }

    function saveHistory() {
        try {
            const messages = state.messages.slice(-MAX_HISTORY);
            localStorage.setItem(HISTORY_KEY, JSON.stringify(messages));
        } catch (err) {
            // 聊天内容过大或浏览器禁用了存储时，不影响当前会话。
        }
    }

    function loadHistory() {
        try {
            const raw = localStorage.getItem(HISTORY_KEY);
            if (!raw) {
                return;
            }
            const parsed = JSON.parse(raw);
            if (!Array.isArray(parsed)) {
                return;
            }

            state.messages = parsed
                .filter((item) => item && (item.role === "user" || item.role === "assistant") &&
                    typeof item.content === "string" && item.content.length > 0)
                .slice(-MAX_HISTORY)
                .map((item) => ({ role: item.role, content: item.content }));

            state.messages.forEach((message) => addHistoryOrMessage(message.role, message.content));
            updateEmptyState();
            if (state.messages.length > 0) {
                window.setTimeout(() => scrollToBottom(true), 60);
            }
        } catch (err) {
            state.messages = [];
        }
    }

    function streamMarkdown(force) {
        if (!state.assistantMd) {
            return;
        }

        const stick = isNearBottom();
        const text = state.streamText;
        state.assistantMd.innerHTML = renderMarkdown(text);
        state.assistantMd.classList.toggle("is-empty", text.trim().length === 0);
        enhanceLinks(state.assistantMd);
        if (stick || force) {
            scrollToBottom(true);
        }
    }

    function scheduleStreamRender() {
        if (state.renderTimer) {
            return;
        }
        state.renderTimer = window.setTimeout(() => {
            state.renderTimer = null;
            streamMarkdown(false);
        }, 45);
    }

    function resetStreamState() {
        if (state.renderTimer) {
            window.clearTimeout(state.renderTimer);
            state.renderTimer = null;
        }
        state.streaming = false;
        state.requestId = null;
        state.streamText = "";
        state.assistantRow = null;
        state.assistantMd = null;
        state.saveTimer = null;
        updateComposerState();
    }

    function finishStreaming(cancelled) {
        if (!state.streaming) {
            return;
        }

        if (state.renderTimer) {
            window.clearTimeout(state.renderTimer);
            state.renderTimer = null;
        }
        streamMarkdown(true);

        const text = state.streamText.trim();
        const row = state.assistantRow;
        if (row) {
            row.classList.remove("streaming");
        }

        if (text.length > 0) {
            state.messages.push({ role: "assistant", content: state.streamText });
            if (row) {
                const bubble = row.querySelector(".bubble");
                if (bubble) {
                    addMessageActions(bubble, state.streamText);
                }
            }
            saveHistory();
        } else if (row) {
            row.remove();
        }

        resetStreamState();
        updateEmptyState();
        updateComposerState();
        dom.input.focus();

        if (cancelled) {
            toast("已停止生成");
        } else if (text.length === 0) {
            addError("模型没有返回内容。请检查模型名称、接口地址或接口是否支持流式输出。");
        }
    }

    function failStreaming(message) {
        if (!state.streaming) {
            return;
        }

        if (state.renderTimer) {
            window.clearTimeout(state.renderTimer);
            state.renderTimer = null;
        }
        streamMarkdown(true);

        const row = state.assistantRow;
        if (row) {
            row.classList.remove("streaming");
        }
        const text = state.streamText.trim();
        if (text.length > 0) {
            state.messages.push({ role: "assistant", content: state.streamText });
            if (row) {
                const bubble = row.querySelector(".bubble");
                if (bubble) {
                    addMessageActions(bubble, state.streamText);
                }
            }
            saveHistory();
        } else if (row) {
            row.remove();
        }

        resetStreamState();
        updateEmptyState();
        addError(message || "请求失败，请检查 API 设置和网络连接。");
        dom.input.focus();
    }

    function requestId() {
        return Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 10);
    }

    function sendMessage() {
        if (state.streaming) {
            return;
        }

        const text = dom.input.value.trim();
        if (!text) {
            return;
        }

        if (!state.config.baseUrl || !state.config.model) {
            toast("请先配置 API 地址和模型");
            openSettings();
            return;
        }

        if (!bridgeAvailable()) {
            toast("请通过 AI聊天.exe 启动程序");
            return;
        }

        dom.emptyState.classList.add("hidden");
        dom.input.value = "";
        resizeInput();
        state.messages.push({ role: "user", content: text });
        dom.messages.appendChild(userRow(text));

        const built = assistantRow("", true);
        state.assistantRow = built.row;
        state.assistantMd = built.md;
        dom.messages.appendChild(built.row);

        state.streaming = true;
        state.streamText = "";
        state.requestId = requestId();
        updateComposerState();
        scrollToBottom(true);

        const sent = postToHost({
            type: "send",
            requestId: state.requestId,
            messages: state.messages.map((message) => ({
                role: message.role,
                content: message.content
            }))
        });

        if (!sent) {
            failStreaming("无法连接桌面程序。请确认程序没有异常退出。");
        }
    }

    function stopGeneration() {
        if (!state.streaming || !state.requestId) {
            return;
        }

        const currentId = state.requestId;
        postToHost({ type: "stop", requestId: currentId });
        dom.stopBtn.disabled = true;
        dom.stopBtn.textContent = "正在停止…";

        window.setTimeout(() => {
            if (state.streaming && state.requestId === currentId) {
                finishStreaming(true);
            }
        }, 6000);
    }

    function newChat() {
        if (state.messages.length > 0 && !window.confirm("清空当前对话？此操作不能撤销。")) {
            return;
        }

        if (state.streaming) {
            postToHost({ type: "stop", requestId: state.requestId });
            resetStreamState();
        }

        state.messages = [];
        dom.messages.innerHTML = "";
        try {
            localStorage.removeItem(HISTORY_KEY);
        } catch (err) {
            // ignore
        }
        updateEmptyState();
        updateComposerState();
        dom.input.focus();
    }

    function populateSettings() {
        dom.baseUrl.value = state.config.baseUrl || "";
        dom.apiKey.value = state.config.apiKey || "";
        dom.model.value = state.config.model || "";
        dom.settingsError.classList.add("hidden");
        dom.settingsError.textContent = "";
        dom.saveSettingsBtn.disabled = false;
        dom.saveSettingsBtn.textContent = "保存设置";
    }

    function openSettings() {
        populateSettings();
        dom.settingsModal.classList.remove("hidden");
        window.setTimeout(() => {
            if (!dom.baseUrl.value) {
                dom.baseUrl.focus();
            } else if (!dom.model.value) {
                dom.model.focus();
            } else {
                dom.apiKey.focus();
            }
        }, 40);
    }

    function closeSettings() {
        dom.settingsModal.classList.add("hidden");
        dom.saveSettingsBtn.disabled = false;
        dom.saveSettingsBtn.textContent = "保存设置";
    }

    function showSettingsError(message) {
        dom.settingsError.textContent = message;
        dom.settingsError.classList.remove("hidden");
        dom.saveSettingsBtn.disabled = false;
        dom.saveSettingsBtn.textContent = "保存设置";
    }

    function saveSettings() {
        const baseUrl = dom.baseUrl.value.trim().replace(/\/+$/, "");
        const apiKey = dom.apiKey.value.trim();
        const model = dom.model.value.trim();

        if (!/^https?:\/\/\S+$/i.test(baseUrl)) {
            showSettingsError("API 地址必须以 http:// 或 https:// 开头。");
            return;
        }
        if (!model) {
            showSettingsError("请填写模型名称，例如 deepseek-chat。");
            return;
        }
        if (!bridgeAvailable()) {
            showSettingsError("请通过 AI聊天.exe 启动程序后再保存。");
            return;
        }

        dom.settingsError.classList.add("hidden");
        dom.saveSettingsBtn.disabled = true;
        dom.saveSettingsBtn.textContent = "保存中…";

        const sent = postToHost({
            type: "saveSettings",
            baseUrl: baseUrl,
            apiKey: apiKey,
            model: model
        });

        if (!sent) {
            showSettingsError("无法连接桌面程序。");
            return;
        }

        window.setTimeout(() => {
            if (dom.saveSettingsBtn.disabled) {
                dom.saveSettingsBtn.disabled = false;
                dom.saveSettingsBtn.textContent = "保存设置";
            }
        }, 5000);
    }

    function applyConfig(config, openOnFirstRun) {
        state.config = {
            baseUrl: config.baseUrl || "",
            model: config.model || "",
            apiKey: config.apiKey || ""
        };
        state.firstRun = !!config.firstRun;
        updateModelBadge();

        if (openOnFirstRun && state.firstRun) {
            window.setTimeout(openSettings, 260);
        }
    }

    function handleHostMessage(event) {
        let message = event.data;
        if (typeof message === "string") {
            try {
                message = JSON.parse(message);
            } catch (err) {
                return;
            }
        }
        if (!message || typeof message !== "object") {
            return;
        }

        switch (message.type) {
            case "config":
                applyConfig(message, true);
                break;

            case "delta":
                if (state.streaming && message.requestId === state.requestId && typeof message.text === "string") {
                    state.streamText += message.text;
                    scheduleStreamRender();
                }
                break;

            case "done":
                if (state.streaming && message.requestId === state.requestId) {
                    finishStreaming(!!message.cancelled);
                }
                break;

            case "error":
                if (state.streaming && message.requestId === state.requestId) {
                    failStreaming(message.message || "请求失败");
                } else {
                    toast(message.message || "请求失败");
                }
                break;

            case "settingsSaved":
                state.config = {
                    baseUrl: message.baseUrl || state.config.baseUrl,
                    model: message.model || state.config.model,
                    apiKey: typeof message.apiKey === "string" ? message.apiKey : state.config.apiKey
                };
                state.firstRun = false;
                updateModelBadge();
                closeSettings();
                toast("API 设置已保存");
                break;

            case "settingsError":
                showSettingsError(message.message || "设置保存失败");
                break;

            default:
                break;
        }
    }

    function toast(message) {
        dom.toast.textContent = message;
        dom.toast.classList.remove("hidden");
        if (toastTimer) {
            window.clearTimeout(toastTimer);
        }
        toastTimer = window.setTimeout(() => {
            dom.toast.classList.add("hidden");
        }, 2400);
    }

    function bindEvents() {
        dom.input.addEventListener("input", () => {
            resizeInput();
            updateComposerState();
        });

        dom.input.addEventListener("keydown", (event) => {
            if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                sendMessage();
            }
        });

        dom.sendBtn.addEventListener("click", sendMessage);
        dom.stopBtn.addEventListener("click", stopGeneration);
        dom.newChatBtn.addEventListener("click", newChat);

        dom.settingsBtn.addEventListener("click", openSettings);
        dom.emptySettingsBtn.addEventListener("click", openSettings);
        dom.closeSettingsBtn.addEventListener("click", closeSettings);
        dom.cancelSettingsBtn.addEventListener("click", closeSettings);
        dom.saveSettingsBtn.addEventListener("click", saveSettings);

        dom.settingsModal.addEventListener("click", (event) => {
            if (event.target === dom.settingsModal) {
                closeSettings();
            }
        });

        dom.toggleKey.addEventListener("click", () => {
            const showing = dom.apiKey.type === "text";
            dom.apiKey.type = showing ? "password" : "text";
            dom.toggleKey.textContent = showing ? "显示" : "隐藏";
        });

        document.querySelectorAll("[data-preset]").forEach((button) => {
            button.addEventListener("click", () => {
                const preset = presets[button.dataset.preset];
                if (!preset) {
                    return;
                }
                dom.baseUrl.value = preset.baseUrl;
                dom.model.value = preset.model;
                if (button.dataset.preset === "ollama") {
                    dom.apiKey.value = "";
                }
                dom.settingsError.classList.add("hidden");
            });
        });

        document.addEventListener("keydown", (event) => {
            if (event.key === "Escape") {
                if (!dom.settingsModal.classList.contains("hidden")) {
                    closeSettings();
                }
            }
        });

        window.addEventListener("beforeunload", () => {
            if (state.streaming && state.requestId) {
                postToHost({ type: "stop", requestId: state.requestId });
            }
        });
    }

    function init() {
        bindEvents();
        loadHistory();
        updateEmptyState();
        updateModelBadge();
        updateComposerState();
        resizeInput();

        if (bridgeAvailable()) {
            window.chrome.webview.addEventListener("message", handleHostMessage);
            postToHost({ type: "ready" });
        } else {
            dom.modelBadge.textContent = "请从桌面程序打开";
            toast("请通过 AI聊天.exe 启动程序");
        }

        window.setTimeout(() => dom.input.focus(), 120);
    }

    init();
})();
