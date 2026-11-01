const themeStorageKey = 'theme';

// 本页手动选择的主题；存储不可用时仍用于避免系统主题变化覆盖手动选择
let chosenTheme = null;

function getSystemTheme() {
    return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function getStoredTheme() {
    try {
        return localStorage.getItem(themeStorageKey);
    } catch {
        // 存储不可用（如隐私模式限制）时按未存储处理
        return null;
    }
}

function persistTheme(theme) {
    try {
        localStorage.setItem(themeStorageKey, theme);
    } catch {
        // 存储不可用时主题仅在当前页面生效
    }
}

function syncToggleState() {
    document.querySelector('[data-theme-toggle]')?.setAttribute(
        'aria-pressed',
        document.documentElement.dataset.bsTheme === 'dark' ? 'true' : 'false');
}

function applyTheme(theme) {
    document.documentElement.dataset.bsTheme = theme;
    syncToggleState();
}

function toggleTheme() {
    // 先应用再持久化，存储不可用时仍能切换当前页面主题
    const next = document.documentElement.dataset.bsTheme === 'dark' ? 'light' : 'dark';
    chosenTheme = next;
    applyTheme(next);
    persistTheme(next);
}

// 切换按钮使用事件委托，与 Blazor 渲染时机无关
document.addEventListener('click', (event) => {
    if (!(event.target instanceof Element) || !event.target.closest('[data-theme-toggle]')) {
        return;
    }
    toggleTheme();
});

// 用户未手动选择时跟随系统切换
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (!chosenTheme && !getStoredTheme()) {
        applyTheme(getSystemTheme());
    }
});

// 其他标签页修改主题时保持同步
window.addEventListener('storage', (event) => {
    if (event.key === themeStorageKey) {
        applyTheme(event.newValue ?? getSystemTheme());
    }
});

// 增强导航按服务器返回的 HTML 同步文档属性，会移除 JS 动态设置的主题；
// 导航完成后按 当前页手动选择 → 存储值 → 系统主题 重新应用
Blazor.addEventListener('enhancedload', () => {
    applyTheme(chosenTheme ?? getStoredTheme() ?? getSystemTheme());
});

// 预渲染 HTML 中的初始状态为浅色，模块加载后按实际主题同步
syncToggleState();
