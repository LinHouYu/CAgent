using System.Text.Json;
using CAgent.Core.Adapters;
using CAgent.Core.Configuration;

namespace CAgent.Bridge.Cdp;

internal sealed class InputProbeResult
{
    public bool Found { get; set; }
    public bool IsLoginPage { get; set; }
    public string PageUrl { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public double Score { get; set; }
}

internal sealed class DomFallbackSnapshot
{
    public string Text { get; set; } = string.Empty;
    public bool IsGenerating { get; set; }
}

internal static class SemanticInputAndCalibrator
{
    public static async Task<(InputProbeResult Probe, string? MatchedSessionId)> ProbeAndFocusInputAsync(
        PersistentCdpClient client,
        AccountProfile account,
        EditionAdapterRule rule,
        bool focusAndClear,
        CancellationToken ct)
    {
        var sessionsToCheck = BuildSessionSearchOrder(client, account.CustomInputSelector?.FrameUrlHint);
        InputProbeResult? bestProbe = null;
        string? bestSession = null;

        foreach (var sessionId in sessionsToCheck)
        {
            try
            {
                var js = BuildProbeAndFocusScript(account.CustomInputSelector, rule.InputSelectorHints, focusAndClear);
                var probe = await client.EvaluateJsAsync<InputProbeResult>(js, sessionId, cancellationToken: ct, timeoutMs: 4000)
                    .ConfigureAwait(false);

                if (probe is null)
                {
                    continue;
                }

                if (probe.Found)
                {
                    if (bestProbe is null || probe.Score > bestProbe.Score)
                    {
                        bestProbe = probe;
                        bestSession = sessionId;
                        if (focusAndClear)
                        {
                            return (probe, sessionId);
                        }
                    }
                }
                else if (bestProbe is null)
                {
                    bestProbe = probe;
                    bestSession = sessionId;
                }
            }
            catch
            {
                // Ignore transient detached child frame errors
            }
        }

        return (bestProbe ?? new InputProbeResult(), bestSession);
    }

    public static async Task<bool> TryClickNewChatAsync(
        PersistentCdpClient client,
        EditionAdapterRule rule,
        CancellationToken ct)
    {
        var hintsJson = JsonSerializer.Serialize(rule.NewChatButtonHints ?? []);
        var clickNewChatJs = $$"""
        (function() {
            const hints = {{hintsJson}};
            function collectRoots(root, list) {
                list.push(root);
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
                let n = walker.currentNode;
                while (n) {
                    if (n.shadowRoot) collectRoots(n.shadowRoot, list);
                    if (n.tagName === 'IFRAME') {
                        try { if (n.contentDocument) collectRoots(n.contentDocument, list); } catch {}
                    }
                    n = walker.nextNode();
                }
            }
            const roots = [];
            collectRoots(document, roots);

            function isClickable(el) {
                if (!el || el.disabled || el.getAttribute?.('aria-disabled') === 'true') return false;
                const r = el.getBoundingClientRect();
                return r.width > 8 && r.height > 8;
            }

            for (const sel of hints) {
                for (const root of roots) {
                    try {
                        const matches = root.querySelectorAll(sel);
                        for (const newChatEl of matches) {
                            if (isClickable(newChatEl)) {
                                newChatEl.click();
                                return true;
                            }
                        }
                    } catch {}
                }
            }

            // Semantic fallback: match visible button/link labeled New chat / 新聊天 / 新建聊天 / 新对话
            for (const root of roots) {
                const buttons = root.querySelectorAll('button, a[role="button"], [role="button"]');
                for (const newChatEl of buttons) {
                    if (!isClickable(newChatEl)) continue;
                    const label = `${newChatEl.getAttribute('aria-label')||''} ${newChatEl.getAttribute('title')||''} ${(newChatEl.innerText||'').trim()}`;
                    if (/^(new chat|new topic|start new chat|新聊天|新建聊天|新对话|开启新对话)$/i.test(label.trim())) {
                        newChatEl.click();
                        return true;
                    }
                }
            }
            return false;
        })()
        """;

        foreach (var sid in BuildSessionSearchOrder(client, null))
        {
            try
            {
                var clicked = await client.EvaluateJsAsync<bool>(clickNewChatJs, sid, cancellationToken: ct, timeoutMs: 3000)
                    .ConfigureAwait(false);
                if (clicked)
                {
                    await Task.Delay(350, ct).ConfigureAwait(false);
                    return true;
                }
            }
            catch
            {
                // Try next frame
            }
        }

        return false;
    }

    public static async Task AttachFilesAsync(
        PersistentCdpClient client,
        string? activeSessionId,
        EditionAdapterRule rule,
        IReadOnlyList<string> filePaths,
        CancellationToken ct)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        var fullPaths = filePaths.Select(Path.GetFullPath).Where(File.Exists).ToArray();
        if (fullPaths.Length == 0)
        {
            return;
        }

        var attachedViaFileInput = false;

        // Strategy 1: Native CDP DOM.setFileInputFiles (piercing Shadow DOM)
        var sessions = activeSessionId is not null
            ? new[] { activeSessionId, null }.Distinct()
            : BuildSessionSearchOrder(client, null);

        foreach (var sid in sessions)
        {
            try
            {
                var findInputExpr = BuildFindFileInputScript(rule.FileInputHints);
                var evalRes = await client.SendCommandAsync(
                    "Runtime.evaluate",
                    new { expression = findInputExpr, returnByValue = false },
                    sid,
                    ct,
                    timeoutMs: 4000).ConfigureAwait(false);

                if (evalRes.ValueKind == JsonValueKind.Object &&
                    evalRes.TryGetProperty("result", out var rObj) &&
                    rObj.TryGetProperty("objectId", out var objIdProp) &&
                    objIdProp.ValueKind == JsonValueKind.String)
                {
                    var objectId = objIdProp.GetString();
                    if (!string.IsNullOrEmpty(objectId))
                    {
                        await client.SendCommandAsync(
                            "DOM.setFileInputFiles",
                            new { files = fullPaths, objectId },
                            sid,
                            ct,
                            timeoutMs: 6000).ConfigureAwait(false);
                        attachedViaFileInput = true;
                        break;
                    }
                }
            }
            catch
            {
                // Fall through to synthetic ClipboardEvent paste
            }
        }

        // Strategy 2: Synthetic Clipboard Paste Event onto active focused input (works universally on Copilot Web!)
        if (!attachedViaFileInput)
        {
            foreach (var path in fullPaths)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                    var b64 = Convert.ToBase64String(bytes);
                    var fileName = Path.GetFileName(path);
                    var mime = GuessMimeType(path);

                    var pasteJs = $$"""
                    (function() {
                        const target = document.activeElement || document.body;
                        const bin = atob({{JsonSerializer.Serialize(b64)}});
                        const arr = new Uint8Array(bin.length);
                        for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
                        const file = new File([arr], {{JsonSerializer.Serialize(fileName)}}, { type: {{JsonSerializer.Serialize(mime)}} });
                        const dt = new DataTransfer();
                        dt.items.add(file);
                        const evt = new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData: dt });
                        target.dispatchEvent(evt);
                        return true;
                    })()
                    """;

                    await client.EvaluateJsAsync<bool>(pasteJs, activeSessionId, cancellationToken: ct, timeoutMs: 6000)
                        .ConfigureAwait(false);
                    await Task.Delay(400, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort per-file paste so message text never fails to send
                }
            }
        }

        // Wait until upload progress indicators settle before pressing Send
        await WaitForAttachmentUploadReadyAsync(client, activeSessionId, ct).ConfigureAwait(false);
    }

    private static async Task WaitForAttachmentUploadReadyAsync(
        PersistentCdpClient client,
        string? sessionId,
        CancellationToken ct)
    {
        const string checkUploadingJs = """
        (function() {
            const spinners = document.querySelectorAll('[role="progressbar"], [aria-busy="true"], [class*="uploading" i], [data-testid*="loading" i]');
            for (const s of spinners) {
                const r = s.getBoundingClientRect();
                if (r.width > 4 && r.height > 4) return true;
            }
            return false;
        })()
        """;

        await Task.Delay(650, ct).ConfigureAwait(false);

        for (var i = 0; i < 12; i++)
        {
            try
            {
                var stillUploading = await client.EvaluateJsAsync<bool>(checkUploadingJs, sessionId, cancellationToken: ct, timeoutMs: 2000)
                    .ConfigureAwait(false);
                if (!stillUploading)
                {
                    return;
                }
            }
            catch
            {
                return;
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    public static async Task InjectTextAndSubmitAsync(
        PersistentCdpClient client,
        string? sessionId,
        EditionAdapterRule rule,
        string message,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(message))
        {
            await client.SendCommandAsync(
                "Input.insertText",
                new { text = message },
                sessionId,
                ct,
                timeoutMs: 10000).ConfigureAwait(false);

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        // Try clicking an enabled Send button first, otherwise dispatch native Enter key
        var sendHintsJson = JsonSerializer.Serialize(rule.SendButtonHints);
        var clickSendJs = $$"""
        (function() {
            const hints = {{sendHintsJson}};
            function queryAllDeep(root, sel, out) {
                try { root.querySelectorAll(sel).forEach(el => out.push(el)); } catch {}
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
                let n = walker.currentNode;
                while (n) {
                    if (n.shadowRoot) queryAllDeep(n.shadowRoot, sel, out);
                    n = walker.nextNode();
                }
            }
            for (const sel of hints) {
                const matches = [];
                queryAllDeep(document, sel, matches);
                for (const btn of matches) {
                    if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') continue;
                    const rect = btn.getBoundingClientRect();
                    if (rect.width > 8 && rect.height > 8) {
                        btn.click();
                        return true;
                    }
                }
            }
            return false;
        })()
        """;

        var clicked = await client.EvaluateJsAsync<bool>(clickSendJs, sessionId, cancellationToken: ct).ConfigureAwait(false);
        if (!clicked)
        {
            await client.SendCommandAsync(
                "Input.dispatchKeyEvent",
                new
                {
                    type = "rawKeyDown",
                    key = "Enter",
                    code = "Enter",
                    windowsVirtualKeyCode = 13,
                    nativeVirtualKeyCode = 13
                },
                sessionId,
                ct).ConfigureAwait(false);

            await client.SendCommandAsync(
                "Input.dispatchKeyEvent",
                new
                {
                    type = "keyUp",
                    key = "Enter",
                    code = "Enter",
                    windowsVirtualKeyCode = 13,
                    nativeVirtualKeyCode = 13
                },
                sessionId,
                ct).ConfigureAwait(false);
        }
    }

    public static async Task ArmDomFallbackBaselineAsync(
        PersistentCdpClient client,
        string? sessionId,
        CancellationToken ct)
    {
        const string script = """
        (function() {
            function extractRichText(el) {
                let txt = (el.innerText || '').trim();
                const pres = el.querySelectorAll ? el.querySelectorAll('pre') : [];
                pres.forEach(pre => {
                    const code = (pre.querySelector('code') || pre).innerText || '';
                    const trimmed = code.trim();
                    if (trimmed.startsWith('{') && trimmed.includes('"tool"') && !txt.includes('```cagent')) {
                        txt += '\n```cagent\n' + trimmed + '\n```';
                    }
                });
                return txt;
            }
            const rawList = Array.from(document.querySelectorAll('[data-content="ai-message"], [data-testid*="bot-message" i], [data-testid*="assistant" i], article, [role="article"], [class*="assistant"], [class*="bot"]'));
            const topLevel = rawList.filter(el => !rawList.some(other => other !== el && other.contains(el)));
            const blocks = new Set();
            topLevel.forEach(el => {
                const t = extractRichText(el);
                if (t.length > 0) blocks.add(t);
            });
            window.__cagentBaselineCount = topLevel.length;
            window.__cagentBaselineTexts = Array.from(blocks);
            return true;
        })()
        """;
        try
        {
            await client.EvaluateJsAsync<bool>(script, sessionId, cancellationToken: ct, timeoutMs: 3000).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort baseline
        }
    }

    public static async Task<DomFallbackSnapshot?> PollDomFallbackAsync(
        PersistentCdpClient client,
        string? sessionId,
        CancellationToken ct)
    {
        const string script = """
        (function() {
            const stopBtns = document.querySelectorAll('button[aria-label*="Stop" i], button[aria-label*="停止"], button[title*="Stop" i], button[title*="停止"], button[data-testid*="stop" i], [role="button"][aria-label*="Stop" i], [role="button"][aria-label*="停止"]');
            let isGenerating = false;
            stopBtns.forEach(b => {
                const r = b.getBoundingClientRect();
                if (r.width > 4 && r.height > 4 && !b.disabled) isGenerating = true;
            });

            function extractRichText(el) {
                let txt = (el.innerText || '').trim();
                const pres = el.querySelectorAll ? el.querySelectorAll('pre') : [];
                pres.forEach(pre => {
                    const code = (pre.querySelector('code') || pre).innerText || '';
                    const trimmed = code.trim();
                    if (trimmed.startsWith('{') && trimmed.includes('"tool"') && !txt.includes('```cagent')) {
                        txt += '\n```cagent\n' + trimmed + '\n```';
                    }
                });
                return txt;
            }

            const baseline = new Set(window.__cagentBaselineTexts || []);
            const rawList = Array.from(document.querySelectorAll('[data-content="ai-message"], [data-testid*="bot-message" i], [data-testid*="assistant" i], article, [role="article"], [class*="assistant"], [class*="bot"]'));
            const topLevel = rawList.filter(el => !rawList.some(other => other !== el && other.contains(el)));
            let latestText = '';
            for (const el of topLevel) {
                const txt = extractRichText(el);
                if (txt.length > 0 && !baseline.has(txt)) {
                    latestText = txt;
                }
            }
            return { text: latestText, isGenerating };
        })()
        """;
        try
        {
            return await client.EvaluateJsAsync<DomFallbackSnapshot>(script, sessionId, cancellationToken: ct, timeoutMs: 3000).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<CalibratedSelector> RunInteractiveCalibrationAsync(
        PersistentCdpClient client,
        TimeSpan timeout,
        CancellationToken ct)
    {
        const string injectCalibratorJs = """
        new Promise((resolve, reject) => {
            const existing = document.getElementById('__cagent_calibrator_banner');
            if (existing) existing.remove();

            const banner = document.createElement('div');
            banner.id = '__cagent_calibrator_banner';
            banner.style.cssText = 'position:fixed;top:16px;left:50%;transform:translateX(-50%);z-index:2147483647;background:#0f172a;color:#f8fafc;padding:12px 22px;border-radius:999px;font:600 14px system-ui,sans-serif;box-shadow:0 10px 30px rgba(0,0,0,0.35);border:2px solid #38bdf8;pointer-events:none;';
            banner.textContent = '🎯 CAgent 自愈校准：请直接鼠标左键点击一次 Copilot 聊天输入框（按 Esc 取消）';
            document.documentElement.appendChild(banner);

            let prevEl = null;
            let prevOutline = '';

            function cleanup() {
                if (prevEl) prevEl.style.outline = prevOutline;
                banner.remove();
                window.removeEventListener('mouseover', onHover, true);
                window.removeEventListener('click', onClick, true);
                window.removeEventListener('keydown', onKey, true);
            }

            function buildSelector(el) {
                if (el.id) return '#' + CSS.escape(el.id);
                const testId = el.getAttribute('data-testid');
                if (testId) return `${el.tagName.toLowerCase()}[data-testid="${CSS.escape(testId)}"]`;
                const aria = el.getAttribute('aria-label');
                if (aria) return `${el.tagName.toLowerCase()}[aria-label="${CSS.escape(aria)}"]`;
                const ph = el.getAttribute('placeholder');
                if (ph) return `${el.tagName.toLowerCase()}[placeholder="${CSS.escape(ph)}"]`;
                const role = el.getAttribute('role');
                if (role) return `${el.tagName.toLowerCase()}[role="${CSS.escape(role)}"]`;
                if (el.isContentEditable) return `${el.tagName.toLowerCase()}[contenteditable="true"]`;
                return el.tagName.toLowerCase();
            }

            function onHover(e) {
                const el = e.composedPath()[0];
                if (!el || el === prevEl || !el.style) return;
                if (prevEl) prevEl.style.outline = prevOutline;
                prevEl = el;
                prevOutline = el.style.outline;
                el.style.outline = '2px solid #38bdf8';
            }

            function onClick(e) {
                e.preventDefault();
                e.stopPropagation();
                let el = e.composedPath()[0];
                while (el && el !== document.body && !(el.tagName === 'TEXTAREA' || el.tagName === 'INPUT' || el.isContentEditable || el.getAttribute?.('role') === 'textbox')) {
                    if (el.parentElement) el = el.parentElement;
                    else break;
                }
                const chosen = el || e.composedPath()[0];
                cleanup();
                resolve({
                    cssSelector: buildSelector(chosen),
                    tagName: (chosen.tagName || '').toLowerCase(),
                    ariaLabel: chosen.getAttribute?.('aria-label') || '',
                    placeholder: chosen.getAttribute?.('placeholder') || '',
                    role: chosen.getAttribute?.('role') || '',
                    frameUrlHint: location.hostname + location.pathname
                });
            }

            function onKey(e) {
                if (e.key === 'Escape') {
                    cleanup();
                    reject(new Error('用户取消了校准'));
                }
            }

            window.addEventListener('mouseover', onHover, true);
            window.addEventListener('click', onClick, true);
            window.addEventListener('keydown', onKey, true);
        })
        """;

        try
        {
            await client.SendCommandAsync("Page.bringToFront", null, null, ct).ConfigureAwait(false);
        }
        catch { }

        var sessions = BuildSessionSearchOrder(client, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var tasks = sessions.Select(async sid =>
        {
            var calibrated = await client.EvaluateJsAsync<CalibratedSelector>(
                injectCalibratorJs,
                sid,
                awaitPromise: true,
                cancellationToken: cts.Token,
                timeoutMs: (int)timeout.TotalMilliseconds + 1000).ConfigureAwait(false);
            return calibrated;
        }).ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            try
            {
                var result = await finished.ConfigureAwait(false);
                if (result is not null && !string.IsNullOrWhiteSpace(result.CssSelector))
                {
                    result.CalibratedAtUtc = DateTimeOffset.UtcNow;
                    await cts.CancelAsync().ConfigureAwait(false);
                    return result;
                }
            }
            catch when (tasks.Count > 0 && !ct.IsCancellationRequested)
            {
                // Try remaining frames
            }
        }

        throw new TimeoutException("未能在校准时间内捕获输入框点击操作。");
    }

    private static List<string?> BuildSessionSearchOrder(PersistentCdpClient client, string? preferredFrameUrlHint)
    {
        var list = new List<string?>();
        var children = client.ChildSessions;

        if (!string.IsNullOrWhiteSpace(preferredFrameUrlHint))
        {
            foreach (var child in children.Where(c => c.Url.Contains(preferredFrameUrlHint, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(child.SessionId);
            }
        }

        list.Add(null); // Root page session
        foreach (var child in children)
        {
            if (!list.Contains(child.SessionId))
            {
                list.Add(child.SessionId);
            }
        }
        return list;
    }

    private static string BuildProbeAndFocusScript(
        CalibratedSelector? calibrated,
        IReadOnlyList<string> selectorHints,
        bool focusAndClear)
    {
        var customSelJson = JsonSerializer.Serialize(calibrated?.CssSelector ?? "");
        var hintsJson = JsonSerializer.Serialize(selectorHints);
        var focusFlag = focusAndClear ? "true" : "false";

        return $$"""
        (function() {
            const url = location.href || '';
            const isAuthHost = /login\.microsoftonline\.com|login\.live\.com|account\.live\.com|aadcdn/i.test(url);
            const hasPassword = !!document.querySelector('input[type="password"]:not([disabled])');

            function collectRoots(root, list) {
                list.push(root);
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
                let node = walker.currentNode;
                while (node) {
                    if (node.shadowRoot) collectRoots(node.shadowRoot, list);
                    if (node.tagName === 'IFRAME') {
                        try { if (node.contentDocument) collectRoots(node.contentDocument, list); } catch {}
                    }
                    node = walker.nextNode();
                }
            }

            const roots = [];
            collectRoots(document, roots);

            function isVisibleEditable(el) {
                if (!el || el.disabled || el.readOnly || el.getAttribute?.('aria-disabled') === 'true') return false;
                const rect = el.getBoundingClientRect();
                if (rect.width < 80 || rect.height < 16) return false;
                const style = window.getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') return false;
                return true;
            }

            let chosen = null;
            let bestScore = 0;

            // Priority 0: User's Click-to-Calibrate custom selector
            const customSel = {{customSelJson}};
            if (customSel) {
                for (const r of roots) {
                    try {
                        const m = r.querySelector(customSel);
                        if (m && isVisibleEditable(m)) { chosen = m; bestScore = 1000; break; }
                    } catch {}
                }
            }

            // Priority 1: Externalized adapters.json hints
            if (!chosen) {
                const hints = {{hintsJson}};
                for (const sel of hints) {
                    for (const r of roots) {
                        try {
                            const list = r.querySelectorAll(sel);
                            for (const el of list) {
                                if (isVisibleEditable(el)) { chosen = el; bestScore = 800; break; }
                            }
                        } catch {}
                        if (chosen) break;
                    }
                    if (chosen) break;
                }
            }

            // Priority 2: Pure Semantic Scoring (survives complete CSS class & ID rewrites!)
            if (!chosen && !isAuthHost) {
                const vh = Math.max(window.innerHeight || 800, 1);
                for (const r of roots) {
                    const candidates = r.querySelectorAll('textarea, [contenteditable="true"], [role="textbox"]');
                    for (const el of candidates) {
                        if (!isVisibleEditable(el)) continue;
                        const rect = el.getBoundingClientRect();
                        let score = 100;
                        if (rect.top >= vh * 0.35) score += 80;
                        score += Math.min(120, rect.width / 5);
                        const meta = `${el.getAttribute('aria-label')||''} ${el.getAttribute('placeholder')||''} ${el.id||''}`;
                        if (/copilot|ask|message|chat|prompt|输入|提问|消息|发消息/i.test(meta)) score += 150;
                        if (score > bestScore) { bestScore = score; chosen = el; }
                    }
                }
            }

            if (!chosen) {
                return { found: false, isLoginPage: isAuthHost || hasPassword, pageUrl: url, tagName: '', score: 0 };
            }

            if ({{focusFlag}}) {
                chosen.focus();
                if (chosen.tagName === 'TEXTAREA' || chosen.tagName === 'INPUT') {
                    const proto = chosen.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
                    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                    if (setter) setter.call(chosen, '');
                    else chosen.value = '';
                    chosen.dispatchEvent(new Event('input', { bubbles: true }));
                } else if (chosen.isContentEditable) {
                    const sel = window.getSelection();
                    const range = document.createRange();
                    range.selectNodeContents(chosen);
                    sel.removeAllRanges();
                    sel.addRange(range);
                    document.execCommand('delete', false, null);
                }
            }

            return {
                found: true,
                isLoginPage: false,
                pageUrl: url,
                tagName: (chosen.tagName || '').toLowerCase(),
                score: bestScore
            };
        })()
        """;
    }

    private static string BuildFindFileInputScript(IReadOnlyList<string> hints)
    {
        var hintsJson = JsonSerializer.Serialize(hints.Count > 0 ? hints : ["input[type='file']"]);
        return $$"""
        (function() {
            const hints = {{hintsJson}};
            function findDeep(root) {
                for (const s of hints) {
                    try {
                        const found = root.querySelector(s);
                        if (found) return found;
                    } catch {}
                }
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
                let n = walker.currentNode;
                while (n) {
                    if (n.shadowRoot) {
                        const sub = findDeep(n.shadowRoot);
                        if (sub) return sub;
                    }
                    n = walker.nextNode();
                }
                return null;
            }
            return findDeep(document);
        })()
        """;
    }

    private static string GuessMimeType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".json" => "application/json",
            _ => "text/plain"
        };
}
