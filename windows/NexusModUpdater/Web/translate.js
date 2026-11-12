// 网页翻译：把一段段外文翻译后直接替换原文（评论区、简介、更新日志…），关掉翻译时恢复原文。
// 替换时不删网页原有的节点（N 网自己的脚本还引用着它们），只把原文的文字清空、在段首插一段译文。
// 只翻译滚动到附近的段落；网页后加载的内容（比如翻页的评论）也会自动翻译。
// 和程序通信：postMessage({nmu:'tr', id, texts}) → {nmu:'tr', id, result}；程序发 {nmu:'trOn'} / {nmu:'trOff'} 开关。
(function () {
  if (window.__nmuTr || !window.chrome || !window.chrome.webview) return;
  // failed：翻译失败过的原文 → 原因。点「重试」之前不再自动请求，免得页面每次重画都白发一次
  var S = window.__nmuTr = { on: false, seq: 0, pending: {}, failed: {}, queue: [], timer: 0, io: null, mo: null, scanTimer: 0, roots: null, target: 'zh-Hans' };
  var INLINE = { A: 1, ABBR: 1, B: 1, BDI: 1, BDO: 1, BR: 1, CITE: 1, CODE: 1, DATA: 1, EM: 1, FONT: 1, I: 1, KBD: 1, LABEL: 1, MARK: 1, Q: 1, S: 1, SAMP: 1, SMALL: 1, SPAN: 1, STRIKE: 1, STRONG: 1, SUB: 1, SUP: 1, TIME: 1, TT: 1, U: 1, VAR: 1, WBR: 1, IMG: 1 };
  var SKIP = { SCRIPT: 1, STYLE: 1, NOSCRIPT: 1, TEXTAREA: 1, INPUT: 1, SELECT: 1, OPTION: 1, BUTTON: 1, SVG: 1, CANVAS: 1, IFRAME: 1, PRE: 1, CODE: 1, NAV: 1, HEADER: 1, FOOTER: 1, TEMPLATE: 1, HEAD: 1 };
  var LETTERS = /\p{L}/u;
  var HAN = /\p{Script=Han}/gu, KANA = /[\p{Script=Hiragana}\p{Script=Katakana}]/gu, HANGUL = /\p{Script=Hangul}/gu, ANY = /\p{L}/gu;

  function css() {
    if (document.getElementById('nmu-tr-style')) return;
    var st = document.createElement('style');
    st.id = 'nmu-tr-style';
    // .nmu-tr：替换原文的译文（保留换行）；.nmu-err：翻译失败的提示；.nmu-hide：原文里的换行 <br>，替换后藏起来；
    // .nmu-wait：已经发出去、在等译文的段落，文字慢慢明暗闪烁（只改透明度，不动排版；不用 ::before/::after，免得和网页自己的样式冲突）
    st.textContent = '.nmu-tr{white-space:pre-line}' + '.nmu-wait{animation:nmu-wait 1s ease-in-out infinite alternate}@keyframes nmu-wait{from{opacity:1}to{opacity:.35}}@media (prefers-reduced-motion:reduce){.nmu-wait{animation:none;opacity:.55}}' +
      '.nmu-err{display:block;margin:.3em 0 .5em;padding:.15em 0 .15em .6em;border-left:3px solid rgba(248,113,113,.8);opacity:.7;font-size:.97em;white-space:pre-wrap;font-weight:normal;font-style:normal;text-align:left}' +
      '.nmu-hide{display:none!important}' +
      '.nmu-retry{display:inline-block;margin-left:.5em;padding:0 .45em;border:1px solid rgba(248,113,113,.6);border-radius:.7em;color:#f87171;font-size:.8em;line-height:1.5;cursor:pointer;font-weight:normal;font-style:normal;white-space:nowrap;vertical-align:baseline;text-decoration:none}' +
      '.nmu-retry:hover{background:rgba(248,113,113,.15)}';
    (document.head || document.documentElement).appendChild(st);
  }

  function skipped(el) {
    for (var e = el; e && e !== document.body; e = e.parentElement) {
      if (SKIP[e.tagName] || e.classList.contains('nmu-tr') || e.classList.contains('nmu-err') || e.classList.contains('nmu-retry') || e.getAttribute('translate') === 'no' || e.isContentEditable) return true;
      var role = e.getAttribute('role');
      if (role === 'navigation' || role === 'menu' || role === 'menubar' || role === 'toolbar' || role === 'tablist') return true;
    }
    return false;
  }

  // 一个“段落”的文字：直接的文字和行内元素，遇到块级子元素就跳过（它自己是另一个段落）
  function ownText(el) {
    var out = '';
    for (var n = el.firstChild; n; n = n.nextSibling) {
      if (n.nodeType === 3) out += n.nodeValue;
      else if (n.nodeType === 1) {
        if (n.tagName === 'BR') out += '\n';
        else if (n.classList && (n.classList.contains('nmu-tr') || n.classList.contains('nmu-err') || n.classList.contains('nmu-retry'))) continue;
        else if (INLINE[n.tagName] && !SKIP[n.tagName]) out += ownText(n);
        else if (INLINE[n.tagName] && n.tagName === 'CODE') out += n.textContent;
      }
    }
    return out;
  }

  function blockOf(node) {
    var el = node.nodeType === 3 ? node.parentElement : node;
    while (el && INLINE[el.tagName] && el.parentElement) el = el.parentElement;
    return el;
  }

  // 外文才翻：翻成中文时，以汉字为主的不翻；日文（有假名）、韩文、英文、俄文等都翻。其他目标语言交给程序判断
  function wanted(text) {
    var t = text.replace(/\s+/g, ' ').trim();
    if (t.length < 2 || t.length > 6000 || !LETTERS.test(t)) return false;
    if (/^(https?:\/\/|www\.)\S+$/.test(t)) return false;
    var n = function (re) { return (t.match(re) || []).length; };
    var han = n(HAN), kana = n(KANA), hangul = n(HANGUL), all = n(ANY);
    if (!/^zh/.test(S.target)) return all >= 2;
    if (kana >= 2 || hangul >= 2) return true;
    var foreign = all - han - kana - hangul;
    return foreign >= 2 && han * 2 < foreign;
  }

  function scan(root) {
    if (!S.on) return;
    root = root || document.body;
    if (!root) return;
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: function (n) { return n.nodeValue && LETTERS.test(n.nodeValue) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT; }
    });
    var seen = [];
    var n;
    while ((n = walker.nextNode())) {
      var b = blockOf(n);
      if (!b || b.__nmuSeen || b === document.body || b === document.documentElement) continue;
      b.__nmuSeen = true;
      seen.push(b);
    }
    seen.forEach(function (b) {
      if (skipped(b)) return;
      var text = ownText(b).replace(/[ \t ]+/g, ' ').replace(/\n\s*\n\s*\n+/g, '\n\n').trim();
      if (!wanted(text)) return;
      b.__nmuText = text;
      if (S.failed[text]) { fail(b, S.failed[text]); return; }
      S.io.observe(b);
    });
  }

  function enqueue(b) {
    if (b.__nmuQueued) return;
    b.__nmuQueued = true;
    b.classList.add('nmu-wait');
    S.queue.push(b);
    if (!S.timer) S.timer = setTimeout(flush, 120);
  }

  function flush() {
    S.timer = 0;
    while (S.queue.length) {
      var batch = S.queue.splice(0, 40);
      var id = ++S.seq;
      S.pending[id] = batch;
      window.chrome.webview.postMessage({ nmu: 'tr', id: id, texts: batch.map(function (b) { return b.__nmuText; }) });
    }
  }

  // 这一段自己的文字节点和 <br>（不进块级子元素，它们是别的段落）
  function ownNodes(el, out) {
    for (var n = el.firstChild; n; n = n.nextSibling) {
      if (n.nodeType === 3) out.push(n);
      else if (n.nodeType === 1 && !(n.classList && (n.classList.contains('nmu-tr') || n.classList.contains('nmu-retry')))) {
        if (n.tagName === 'BR') out.push(n);
        else if (INLINE[n.tagName] && (!SKIP[n.tagName] || n.tagName === 'CODE')) ownNodes(n, out);
      }
    }
    return out;
  }

  // 把原文换回来
  function restore(b) {
    var saved = b.__nmuSaved;
    if (!saved) return;
    b.__nmuSaved = null;
    saved.nodes.forEach(function (x) {
      if (x.br) x.br.classList.remove('nmu-hide');
      else if (x.node.nodeValue === '' || x.node.nodeValue === x.blank) x.node.nodeValue = x.value;
    });
    if (saved.span.parentNode) saved.span.parentNode.removeChild(saved.span);
  }

  function replace(b, text) {
    restore(b);
    var nodes = ownNodes(b, []);
    var texts = nodes.filter(function (n) { return n.nodeType === 3 && n.nodeValue.trim(); });
    if (!texts.length) return;
    // 译文插在这一段第一处文字所在的、b 的直接子节点前面（文字在链接里时，译文不会变成链接）
    var at = texts[0];
    while (at.parentNode !== b) at = at.parentNode;
    var span = document.createElement('span');
    span.className = 'nmu-tr';
    span.setAttribute('translate', 'no');
    span.title = b.__nmuText.length > 400 ? b.__nmuText.slice(0, 400) + '…' : b.__nmuText;
    span.textContent = text;
    var saved = { span: span, nodes: [] };
    nodes.forEach(function (n) {
      if (n.nodeType === 3) { saved.nodes.push({ node: n, value: n.nodeValue, blank: '' }); n.nodeValue = ''; }
      else { n.classList.add('nmu-hide'); saved.nodes.push({ br: n }); }
    });
    b.insertBefore(span, at);
    b.__nmuSaved = saved;
  }

  function show(b, text) {
    if (!b.isConnected) return;
    var old = b.querySelector(':scope > .nmu-retry');
    if (old) old.remove();
    if (!text || text === b.__nmuText) return;
    replace(b, text);
  }

  // 翻译失败：段尾放一个「翻译失败 · 重试」，鼠标停上去看原因，点一下把这一页失败的都重新翻
  function fail(b, msg) {
    if (!b.isConnected) return;
    var old = b.querySelector(':scope > .nmu-retry');
    if (old) old.remove();
    var s = document.createElement('span');
    s.className = 'nmu-retry';
    s.setAttribute('translate', 'no');
    s.setAttribute('role', 'button');
    s.title = (msg || '这段没翻译出来') + '\n点击重试（这一页翻译失败的段落会一起重试）';
    s.textContent = '翻译失败 · 重试';
    s.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); retryFailed(); });
    b.appendChild(s);
  }

  function retryFailed() {
    S.failed = {};
    document.querySelectorAll('.nmu-retry').forEach(function (s) {
      var b = s.parentElement;
      s.remove();
      if (b && b.__nmuText) { b.__nmuQueued = false; enqueue(b); }
    });
  }

  function restoreAll(root) {
    (root || document).querySelectorAll('.nmu-tr').forEach(function (d) { if (d.parentElement) restore(d.parentElement); });
    (root || document).querySelectorAll('.nmu-err, .nmu-retry').forEach(function (d) { d.remove(); });
    (root || document).querySelectorAll('.nmu-wait').forEach(function (b) { b.classList.remove('nmu-wait'); });
  }

  function enable() {
    if (S.on) return;
    S.on = true;
    css();
    S.io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) { S.io.unobserve(e.target); enqueue(e.target); }
      });
    }, { rootMargin: '800px 0px' });
    S.mo = new MutationObserver(function (list) {
      if (S.scanTimer) return;
      S.scanTimer = setTimeout(function () {
        S.scanTimer = 0;
        scanAll();
      }, 500);
    });
    S.mo.observe(document.body, { childList: true, subtree: true });
    scanAll();
  }

  // 程序内页只翻译 window.__nmuScope 选中的区域；N 网网页翻译整页
  function scanAll() {
    if (window.__nmuScope) document.querySelectorAll(window.__nmuScope).forEach(function (r) { scan(r); });
    else scan(document.body);
  }

  function disable() {
    S.on = false;
    if (S.io) S.io.disconnect();
    if (S.mo) S.mo.disconnect();
    S.queue = [];
    S.pending = {};
    S.failed = {};
    restoreAll();
    var all = document.querySelectorAll('*');
    for (var i = 0; i < all.length; i++) { all[i].__nmuSeen = all[i].__nmuQueued = false; }
  }

  window.chrome.webview.addEventListener('message', function (e) {
    var d = e.data;
    if (!d || typeof d !== 'object' || !d.nmu) return;
    if (d.nmu === 'trOn') { if (d.target) S.target = d.target; if (document.body) enable(); else document.addEventListener('DOMContentLoaded', enable); }
    else if (d.nmu === 'trOff') disable();
    else if (d.nmu === 'tr' && S.pending[d.id]) {
      var batch = S.pending[d.id];
      delete S.pending[d.id];
      batch.forEach(function (b) { b.classList.remove('nmu-wait'); });
      batch.forEach(function (b, i) {
        var r = d.error ? null : d.result && d.result[i];
        if (r == null) {
          var why = d.error ? (/^翻译失败/.test(d.error) ? d.error : '翻译失败：' + d.error) : '这段没翻译出来（翻译服务拒绝了这段文字）';
          S.failed[b.__nmuText] = why;
          fail(b, why);
        } else show(b, r);
      });
    }
  });

  // 页面自己（程序内页）可以直接调用
  S.enable = enable;
  S.retry = retryFailed;
  S.disable = disable;
  S.scan = scanAll;
  S.reset = function (root) {
    restoreAll(root);
    var all = (root || document).querySelectorAll('*');
    for (var i = 0; i < all.length; i++) { all[i].__nmuSeen = all[i].__nmuQueued = false; }
  };

  function ask() { window.chrome.webview.postMessage({ nmu: 'trState' }); }
  if (location.protocol === 'https:' && /(^|\.)nexusmods\.com$/.test(location.hostname)) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ask); else ask();
  }
})();
