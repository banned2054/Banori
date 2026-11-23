const themeStorageKey = 'theme';
const sidebarStorageKey = 'sidebarCollapsed';

// 本页手动选择的主题与折叠状态；存储不可用时仍用于避免外部变化覆盖手动选择
let chosenTheme = null;
let chosenSidebarCollapsed = null;

function getSystemTheme() {
    return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function getStoredValue(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        // 存储不可用（如隐私模式限制）时按未存储处理
        return null;
    }
}

function persistValue(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch {
        // 存储不可用时仅在当前页面生效
    }
}

function syncThemeToggleState() {
    const isDark = document.documentElement.dataset.bsTheme === 'dark';
    const toggle = document.querySelector('[data-theme-toggle]');
    if (!toggle) return;

    toggle.setAttribute('aria-pressed', isDark ? 'true' : 'false');
    const label = isDark ? '切换到浅色模式' : '切换到深色模式';
    toggle.setAttribute('title', label);
    toggle.setAttribute('aria-label', label);
}

function applyTheme(theme) {
    document.documentElement.dataset.bsTheme = theme;
    syncThemeToggleState();
}

function toggleTheme() {
    // 先应用再持久化，存储不可用时仍能切换当前页面主题
    const next = document.documentElement.dataset.bsTheme === 'dark' ? 'light' : 'dark';
    chosenTheme = next;
    applyTheme(next);
    persistValue(themeStorageKey, next);
}

function getStoredSidebarCollapsed() {
    const stored = getStoredValue(sidebarStorageKey);
    return stored === null ? null : stored === 'true';
}

function syncSidebarToggleState() {
    const collapsed = document.documentElement.dataset.sidebarCollapsed === 'true';
    const toggle = document.querySelector('[data-sidebar-toggle]');
    if (!toggle) return;

    toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
    const label = collapsed ? '展开侧边栏' : '收起侧边栏';
    toggle.setAttribute('title', label);
    toggle.setAttribute('aria-label', label);
}

function applySidebarCollapsed(collapsed) {
    document.documentElement.dataset.sidebarCollapsed = collapsed ? 'true' : 'false';
    syncSidebarToggleState();
}

function toggleSidebar() {
    const next = document.documentElement.dataset.sidebarCollapsed !== 'true';
    chosenSidebarCollapsed = next;
    applySidebarCollapsed(next);
    persistValue(sidebarStorageKey, next ? 'true' : 'false');
}

function setUserMenuOpen(menu, open) {
    const trigger = menu.querySelector('[data-user-menu-toggle]');
    const panel = menu.querySelector('[data-user-menu-panel]');
    if (!trigger || !panel) return;

    panel.hidden = !open;
    menu.dataset.open = open ? 'true' : 'false';
    trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
}

function closeAllUserMenus() {
    document.querySelectorAll('[data-user-menu]').forEach((menu) => setUserMenuOpen(menu, false));
}

// 统一使用事件委托，与 Blazor 静态 SSR / 增强导航渲染时机解耦
document.addEventListener('click', (event) => {
    if (!(event.target instanceof Element)) return;

    if (event.target.closest('[data-theme-toggle]')) {
        toggleTheme();
        return;
    }

    if (event.target.closest('[data-sidebar-toggle]')) {
        toggleSidebar();
        return;
    }

    const userToggle = event.target.closest('[data-user-menu-toggle]');
    if (userToggle) {
        const menu = userToggle.closest('[data-user-menu]');
        if (!menu) return;

        const nextOpen = menu.dataset.open !== 'true';
        closeAllUserMenus();
        setUserMenuOpen(menu, nextOpen);
        return;
    }

    if (event.target.closest('[data-user-menu-close]') || !event.target.closest('[data-user-menu]')) {
        closeAllUserMenus();
    }
});

document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
        closeAllUserMenus();
    }
});

// 用户未手动选择时跟随系统切换
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (!chosenTheme && !getStoredValue(themeStorageKey)) {
        applyTheme(getSystemTheme());
    }
});

// 其他标签页修改主题或侧边栏状态时保持同步
window.addEventListener('storage', (event) => {
    if (event.key === themeStorageKey) {
        applyTheme(event.newValue ?? getSystemTheme());
    } else if (event.key === sidebarStorageKey) {
        applySidebarCollapsed(event.newValue === 'true');
    }
});

// 增强导航按服务器返回的 HTML 同步文档属性，会移除 JS 动态设置的属性；
// 导航完成后按 当前页手动选择 → 存储值 → 默认值 重新应用
Blazor.addEventListener('enhancedload', () => {
    applyTheme(chosenTheme ?? getStoredValue(themeStorageKey) ?? getSystemTheme());
    applySidebarCollapsed(chosenSidebarCollapsed ?? getStoredSidebarCollapsed() ?? false);
    closeAllUserMenus();
});

// 模块加载后同步按钮状态
syncThemeToggleState();
syncSidebarToggleState();
