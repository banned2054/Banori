const storageKey = 'banori.downloads.detailPaneHeight';
const minPaneHeight = 180;
const maxPaneRatio = 0.75;

function readStoredHeight() {
    try {
        const value = Number.parseFloat(localStorage.getItem(storageKey) ?? '');
        return Number.isFinite(value) && value > 0 ? value : null;
    } catch {
        return null;
    }
}

function persistHeight(height) {
    try {
        localStorage.setItem(storageKey, String(Math.round(height)));
    } catch {
        // 隐私模式或存储配额限制时只在当前页面生效。
    }
}

function clampHeight(requested, workspace, detail) {
    const available = workspace.getBoundingClientRect().height ||
        detail.getBoundingClientRect().height;
    const max = Math.max(minPaneHeight, available * maxPaneRatio);
    return Math.min(max, Math.max(minPaneHeight, requested));
}

/**
 * 绑定下面板的拖拽调高。拖拽完全在浏览器内完成，不经过 Blazor/SignalR。
 * 返回一个可释放的句柄，供组件销毁时移除事件监听。
 */
export function initializeDetailPane(root) {
    const empty = { dispose() {} };
    if (!root || typeof root.querySelector !== 'function') return empty;

    const workspace = root.querySelector('.downloads-workspace');
    const splitter = root.querySelector('.downloads-splitter');
    const detail = root.querySelector('.downloads-detail-pane');
    const list = root.querySelector('.downloads-list-pane');
    if (!workspace || !splitter || !detail || !list) return empty;

    const stored = readStoredHeight();
    if (stored !== null) {
        const restored = clampHeight(stored, workspace, detail);
        root.style.setProperty('--downloads-pane-height', `${restored}px`);
    }

    let dragging = false;
    let startY = 0;
    let startHeight = 0;
    let previousUserSelect = '';

    function suppressClick(event) {
        event.preventDefault();
        event.stopPropagation();
    }

    function onPointerDown(event) {
        if (event.button !== 0 || root.dataset.paneExpanded !== 'true') return;
        if (window.matchMedia('(max-width: 640.98px)').matches) return;

        dragging = true;
        startY = event.clientY;
        startHeight = detail.getBoundingClientRect().height;
        try {
            splitter.setPointerCapture(event.pointerId);
        } catch {
            // 旧浏览器没有指针捕获时仍可按 pointermove 正常拖拽。
        }
        previousUserSelect = document.body.style.userSelect;
        document.body.style.userSelect = 'none';
        document.addEventListener('click', suppressClick, true);
        event.preventDefault();
    }

    function onPointerMove(event) {
        if (!dragging) return;
        const next = clampHeight(startHeight + startY - event.clientY, workspace, detail);
        root.style.setProperty('--downloads-pane-height', `${next}px`);
        event.preventDefault();
    }

    function onPointerUp(event) {
        if (!dragging) return;
        dragging = false;
        try {
            if (splitter.hasPointerCapture(event.pointerId)) splitter.releasePointerCapture(event.pointerId);
        } catch {
            // 指针已释放时无需额外处理。
        }
        document.body.style.userSelect = previousUserSelect;
        document.removeEventListener('click', suppressClick, true);
        const finalHeight = detail.getBoundingClientRect().height;
        if (Math.round(finalHeight) !== Math.round(startHeight)) persistHeight(finalHeight);
        event.preventDefault();
    }

    splitter.addEventListener('pointerdown', onPointerDown);
    splitter.addEventListener('pointermove', onPointerMove);
    splitter.addEventListener('pointerup', onPointerUp);
    splitter.addEventListener('pointercancel', onPointerUp);
    splitter.addEventListener('lostpointercapture', onPointerUp);

    return {
        dispose() {
            if (dragging) document.body.style.userSelect = previousUserSelect;
            document.removeEventListener('click', suppressClick, true);
            splitter.removeEventListener('pointerdown', onPointerDown);
            splitter.removeEventListener('pointermove', onPointerMove);
            splitter.removeEventListener('pointerup', onPointerUp);
            splitter.removeEventListener('pointercancel', onPointerUp);
            splitter.removeEventListener('lostpointercapture', onPointerUp);
        }
    };
}

/** 复制任意文本：优先异步剪贴板 API，非 HTTPS 或不可用时回退隐藏 textarea。 */
export async function copyText(text) {
    if (typeof text !== 'string') return false;

    try {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch {
        // 权限或非安全上下文失败时走回退方案。
    }

    try {
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.setAttribute('readonly', '');
        textarea.style.position = 'fixed';
        textarea.style.top = '-1000px';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        textarea.select();
        const copied = document.execCommand('copy');
        textarea.remove();
        return copied;
    } catch {
        return false;
    }
}
