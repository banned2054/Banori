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

/**
 * 绑定虚拟列表滚动、容器尺寸变化与页面可见性监听；通过 requestAnimationFrame 节流回传可视区范围。
 * 返回可释放句柄，在组件销毁时解绑全部监听。
 */
export function initializeVirtualList(listPane, dotNetRef, rowHeightPx) {
    const empty = { dispose() {} };
    if (!listPane || !dotNetRef || !Number.isFinite(rowHeightPx) || rowHeightPx <= 0) return empty;

    let rafId = 0;
    let disposed = false;
    let lastStart = -1;
    let lastCount = -1;

    function reportViewport(force = false) {
        if (disposed) return;
        const thead = listPane.querySelector('thead');
        const headerHeight = thead ? thead.getBoundingClientRect().height : 0;
        const bodyHeight = Math.max(0, listPane.clientHeight - headerHeight);
        const start = Math.max(0, Math.floor(listPane.scrollTop / rowHeightPx));
        const count = bodyHeight <= 0 ? 0 : Math.ceil(bodyHeight / rowHeightPx);
        if (!force && start === lastStart && count === lastCount) return;
        lastStart = start;
        lastCount = count;
        dotNetRef.invokeMethodAsync('OnViewportChangedAsync', start, count).catch(() => {});
    }

    function scheduleReport() {
        if (disposed || rafId !== 0) return;
        rafId = requestAnimationFrame(() => {
            rafId = 0;
            reportViewport(false);
        });
    }

    function onScroll() {
        scheduleReport();
    }

    function onKeyDown(event) {
        if (disposed || event.defaultPrevented) return;
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
        if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;

        const page = listPane.closest('.downloads-page');
        if (page?.dataset.batchMode === 'true') return;

        const target = event.target;
        if (target instanceof HTMLElement &&
            target.closest('input, textarea, select, [contenteditable="true"]')) {
            return;
        }

        event.preventDefault();
        const delta = event.key === 'ArrowDown' ? 1 : -1;
        dotNetRef.invokeMethodAsync('OnListKeyDownAsync', delta)
            .then(rowIndex => {
                if (disposed || typeof rowIndex !== 'number') return;
                ensureRowVisible(listPane, rowIndex, rowHeightPx);
            })
            .catch(() => {});
    }

    function onVisibilityChange() {
        if (disposed) return;
        dotNetRef.invokeMethodAsync('OnVisibilityChangedAsync', document.visibilityState === 'hidden').catch(() => {});
    }

    listPane.addEventListener('scroll', onScroll, { passive: true });
    listPane.addEventListener('keydown', onKeyDown);
    document.addEventListener('visibilitychange', onVisibilityChange);

    let resizeObserver = null;
    if (typeof ResizeObserver === 'function') {
        resizeObserver = new ResizeObserver(() => scheduleReport());
        resizeObserver.observe(listPane);
    }

    reportViewport(true);
    if (document.visibilityState === 'hidden') onVisibilityChange();

    return {
        dispose() {
            disposed = true;
            if (rafId !== 0) cancelAnimationFrame(rafId);
            listPane.removeEventListener('scroll', onScroll);
            listPane.removeEventListener('keydown', onKeyDown);
            document.removeEventListener('visibilitychange', onVisibilityChange);
            if (resizeObserver) resizeObserver.disconnect();
        }
    };
}

/** 键盘切换选中行后，若目标行超出当前可视区，仅滚动到刚好可见的位置。 */
export function ensureRowVisible(listPane, rowIndex, rowHeightPx) {
    if (!listPane || !Number.isInteger(rowIndex) || rowIndex < 0 || !Number.isFinite(rowHeightPx) || rowHeightPx <= 0) {
        return;
    }

    const thead = listPane.querySelector('thead');
    const headerHeight = thead ? thead.getBoundingClientRect().height : 0;
    const bodyHeight = Math.max(0, listPane.clientHeight - headerHeight);
    if (bodyHeight <= 0) return;

    const rowTop = rowIndex * rowHeightPx;
    const rowBottom = rowTop + rowHeightPx;
    const viewTop = listPane.scrollTop;
    const viewBottom = viewTop + bodyHeight;
    if (rowTop < viewTop) {
        listPane.scrollTop = rowTop;
    } else if (rowBottom > viewBottom) {
        listPane.scrollTop = Math.max(0, rowBottom - bodyHeight);
    }
}

/** 同步设置列表容器滚动位置（回顶或后台更新按锚点恢复）。 */
export function setScrollTop(listPane, scrollTop) {
    if (!listPane || typeof scrollTop !== 'number') return;
    listPane.scrollTop = Math.max(0, scrollTop);
}

/** 调整右键菜单位置，避免超出视口右边界或底边界。 */
export function positionContextMenu(menu, clientX, clientY) {
    if (!menu || typeof menu.getBoundingClientRect !== 'function') return;

    const margin = 8;
    const rect = menu.getBoundingClientRect();
    const maxLeft = Math.max(margin, window.innerWidth - rect.width - margin);
    const maxTop = Math.max(margin, window.innerHeight - rect.height - margin);
    const left = Math.min(Math.max(margin, clientX), maxLeft);
    const top = Math.min(Math.max(margin, clientY), maxTop);
    menu.style.left = `${left}px`;
    menu.style.top = `${top}px`;
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
