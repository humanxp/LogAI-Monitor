// Lucide 图标（离线内置，替代原来的 Font Awesome）。
//
// base.html 末尾内联了一份 <svg> 精灵（id="lx-<name>"），这里只负责生成引用标记：
//   icon('bell')        -> '<svg class="lx"><use href="#lx-bell"></use></svg>'
//   setIcon(el, 'save') -> 把已有元素的内容换成该图标（原来是 class="fas fa-save"）
// 之所以内联而不是外链 sprite：同文档 <use> 兼容性最好，图标也能直接继承
// currentColor，跟着文字/主题变色，不额外发请求。
function icon(name, cls) {
    return '<svg class="lx' + (cls ? ' ' + cls : '') + '" aria-hidden="true">'
         + '<use href="#lx-' + name + '"></use></svg>';
}

function setIcon(el, name, cls) {
    if (!el) return;
    el.innerHTML = icon(name, cls);
}
