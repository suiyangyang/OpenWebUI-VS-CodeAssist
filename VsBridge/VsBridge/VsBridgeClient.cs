// VsBridgeClient.cs
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsBridge;
using Task = System.Threading.Tasks.Task;

namespace MyVsixBridge
{
    public static class VsBridgeClient
    {
        private static DTE2? _dte;
        private static WindowEvents? _windowEvents;
        private static DocumentEvents? _documentEvents;
        private static SolutionEvents? _solutionEvents;
        private static SelectionEvents? _selectionEvents;
        private static readonly Timer _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        private static readonly HttpClient _http = new HttpClient() { Timeout = TimeSpan.FromSeconds(5) };
        private static string _bridgeUrl = "http://127.0.0.1:5006/v1/notify";
        private static string _token = "dev-token";

        private static string? _lastPath;
        private static int _lastLine;

        private static bool _solutionUploaded = false;


        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _dte = (DTE2)await package.GetServiceAsync(typeof(DTE));
            if (_dte == null) return;

            // 读取选项配置
            var options = (VsBridgeOptions)package.GetDialogPage(typeof(VsBridgeOptions));
            _bridgeUrl = options.BridgeUrl;
            _token = options.Token;

            _windowEvents = _dte.Events.WindowEvents;
            _windowEvents.WindowActivated += OnWindowActivated;

            _documentEvents = _dte.Events.DocumentEvents;
            _documentEvents.DocumentSaved += OnDocumentSaved;

            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened += OnSolutionOpened;

            _selectionEvents = _dte.Events.SelectionEvents;
            _selectionEvents.OnChange += OnSelectionChange;

            // 可选：在初始化时上传 solution tree & deps（枚举）
            try
            {
                await TryPostSolutionTreeAsync();
            }
            catch { /* 忽略枚举失败 */ }

            // ✅ 启动命令轮询（关键新增）
            await StartPollingCommandsAsync();
        }

        private static void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = Task.Run(async () =>
            {
                try
                {
                    await TryPostSolutionTreeAsync();
                }
                catch { /* log */ }
            });
        }

        private static void OnWindowActivated(Window gotFocus, Window lostFocus)
        {
            System.Diagnostics.Debug.WriteLine("VsBridge:OnWindowActivated");
            ThreadHelper.ThrowIfNotOnUIThread();
            var doc = _dte?.ActiveDocument;
            if (doc == null || gotFocus == null) return;
            _ = Task.Run(() => SendCurrentDocumentAsync(doc));
        }

        private static void OnDocumentSaved(Document doc)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (doc == null) return;
            _ = Task.Run(() => SendCurrentDocumentAsync(doc));
        }

        private static async Task TryPostSolutionTreeAsync()
        {
            try
            {
                if (_solutionUploaded)
                    return;

                var tree = EnumerateSolutionTree();

                var json = JsonConvert.SerializeObject(tree);
                var req = new HttpRequestMessage(HttpMethod.Post, _bridgeUrl.Replace("/notify", "/solution-tree")); // we reuse command endpoint or implement /solution-tree server side
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                req.Headers.Add("X-Local-Token", _token);
                var response = await _http.SendAsync(req);
                response.EnsureSuccessStatusCode();
                _solutionUploaded = true;
            }
            catch { /* 忽略 */ }
        }

        private static async void OnDebounceElapsed(object state)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var doc = _dte?.ActiveDocument;
                if (doc == null) return;

                await SendCurrentDocumentAsync(doc);
            }
            catch { /* 忽略异常 */ }
        }

        private static void OnSelectionChange()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var doc = _dte?.ActiveDocument;
            if (doc == null) return;

            // 取消之前的定时器
            _debounceTimer.Change(3000, Timeout.Infinite); // 200ms 后触发
        }

        public static async Task SendCurrentDocumentAsync(Document doc)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (doc == null) return;
                // 只处理文本类型文件
                if (doc.Language == "HTML" || doc.Language == "Xaml" || doc.Language == "CSharp" || doc.Language == "C++" || doc.Language == "Basic" || doc.Language == "Text")
                {
                    TextDocument? textDoc = null;
                    try { textDoc = doc.Object("TextDocument") as TextDocument; } catch { }
                    if (textDoc == null) return;

                    var editPoint = textDoc.StartPoint.CreateEditPoint();
                    string full = editPoint.GetText(textDoc.EndPoint);

                    var solutionFullName = _dte.Solution.FullName;
                    var sel = textDoc.Selection;
                    var cursorLine = sel.ActivePoint.Line;
                    var cursorCol = sel.ActivePoint.DisplayColumn;
                    var snippet = GetAroundCursorSnippet(full, cursorLine, 50); // X 行上下

                    // ✅ 去重逻辑：相同文件 + 相同位置则跳过
                    if (_lastPath == doc.FullName && _lastLine == cursorLine)
                        return;

                    _lastPath = doc.FullName;
                    _lastLine = cursorLine;

                    await TryPostSolutionTreeAsync();

                    var dto = new
                    {
                        SolutionName = solutionFullName.Normalize(),
                        Path = doc.FullName,
                        ContentSnippet = snippet,
                        FullHash = ComputeSha256(snippet),
                        Cursor = new { Line = cursorLine, Column = cursorCol },
                        Selection = sel.Text,
                        Project = doc.ProjectItem?.ContainingProject?.Name,
                        Timestamp = DateTimeOffset.UtcNow,
                        FullContent = (full.Length <= 50_000 ? full : null) // 若文件不大则上传全文；超大不上传
                    };

                    var json = JsonConvert.SerializeObject(dto);
                    var req = new HttpRequestMessage(HttpMethod.Post, _bridgeUrl);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    req.Headers.Add("X-Local-Token", _token);

                    // 非阻塞发送并简单重试
                    var resp = await _http.SendAsync(req);
                    // 可日志 resp.StatusCode
                }
            }
            catch
            {
                // 不要抛到 UI thread
            }
        }

        private static string ComputeSha256(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var b = Encoding.UTF8.GetBytes(s ?? "");
            return "sha256:" + BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
        }

        private static string GetAroundCursorSnippet(string fullText, int cursorLine, int linesAround)
        {
            var lines = fullText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int start = Math.Max(0, cursorLine - 1 - linesAround);
            int end = Math.Min(lines.Length - 1, cursorLine - 1 + linesAround);

            return string.Join("\n", lines.Skip(start).Take(end - start + 1));
        }

        private static object EnumerateSolutionTree()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_dte?.Solution == null)
                return new { solutionName = string.Empty, projects = new List<object>() };

            var solutionFullName = _dte.Solution.FullName.Normalize();
            var projects = new List<object>();

            foreach (Project p in _dte.Solution.Projects)
            {
                try
                {
                    var projectNode = new
                    {
                        project = p.Name,
                        files = EnumerateProjectItems(p.ProjectItems)
                    };
                    projects.Add(projectNode);
                }
                catch
                {
                    // 忽略单个项目的异常
                }
            }

            return new
            {
                solutionName = solutionFullName,
                projects = projects
            };
        }


        private static System.Collections.Generic.List<string> EnumerateProjectItems(ProjectItems items)
        {
            var list = new System.Collections.Generic.List<string>();
            if (items == null) return list;
            foreach (ProjectItem it in items)
            {
                try
                {
                    if (it.Kind == EnvDTE.Constants.vsProjectItemKindPhysicalFile)
                    {
                        if (it.FileCount > 0)
                        {
                            list.Add(it.FileNames[1]);
                        }
                    }
                    if (it.ProjectItems != null && it.ProjectItems.Count > 0)
                    {
                        list.AddRange(EnumerateProjectItems(it.ProjectItems));
                    }
                }
                catch { }
            }
            return list;
        }

        private static CancellationTokenSource? _commandCts;
        private static Task? _pollingTask;

        // 新增：当前解决方案名称（用于命令过滤）
        private static string? _currentSolutionName;

        #region Commands: 轮询与执行

        /// <summary>
        /// 启动定时轮询，从服务端拉取并执行命令
        /// </summary>
        public static async Task StartPollingCommandsAsync()
        {
            if (_pollingTask != null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _currentSolutionName = _dte?.Solution?.FullName;
            if (string.IsNullOrEmpty(_currentSolutionName)) return;

            _commandCts = new CancellationTokenSource();
            _pollingTask = PollCommandsAsync(1000, _commandCts.Token);
        }

        /// <summary>
        /// 停止轮询
        /// </summary>
        public static void StopPollingCommands()
        {
            _commandCts?.Cancel();
            _pollingTask?.Wait(TimeSpan.FromSeconds(5));
            _commandCts?.Dispose();
            _commandCts = null;
            _pollingTask = null;
        }

        private static async Task PollCommandsAsync(int intervalMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var commands = await GetCommandsAsync(ct);
                    if (commands == null || commands.Count == 0) continue;

                    foreach (var cmd in commands)
                    {
                        await ExecuteCommandAsync(cmd, ct);
                        if (ct.IsCancellationRequested) break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VsBridge] Command polling error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(intervalMs, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 从服务端拉取命令（GET /v1/commands?solution=xxx）
        /// </summary>
        private static async Task<List<CommandDto>> GetCommandsAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_currentSolutionName))
                return new List<CommandDto>();

            var url = $"{_bridgeUrl.Replace("/notify", "/commands")}?solution={Uri.EscapeDataString(_currentSolutionName)}";
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Local-Token", _token);

                using var response = await _http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<List<CommandDto>>(json) ?? new List<CommandDto>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VsBridge] Failed to fetch commands: {ex.Message}");
            }

            return new List<CommandDto>();
        }

        /// <summary>
        /// 执行命令（OpenFile / NavigateTo）
        /// </summary>
        private static async Task ExecuteCommandAsync(CommandDto cmd, CancellationToken ct)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                switch (cmd.Command?.ToLowerInvariant())
                {
                    case "openfile":
                        if (!string.IsNullOrEmpty(cmd.Path))
                        {
                            OpenFileInVisualStudio(cmd.Path, cmd.Line, cmd.Column);
                        }
                        break;

                    case "navigateto":
                        if (!string.IsNullOrEmpty(cmd.Path))
                        {
                            NavigateToSymbol(cmd.Path);
                        }
                        break;

                    default:
                        System.Diagnostics.Debug.WriteLine($"[VsBridge] Unknown command type: {cmd.Command}");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VsBridge] Failed to execute command '{cmd.Command}': {ex.Message}");
            }
        }

        /// <summary>
        /// 使用 DTE2 打开文件并定位光标
        /// </summary>
        private static void OpenFileInVisualStudio(string filePath, int line, int column)
        {
            try
            {
                // 确保路径是绝对路径
                if (!Path.IsPathRooted(filePath))
                {
                    var solutionDir = _dte?.Solution?.FullName;
                    if (!string.IsNullOrEmpty(solutionDir))
                    {
                        var dir = Path.GetDirectoryName(solutionDir);
                        filePath = Path.Combine(dir, filePath);
                    }
                }

                // 检查文件是否存在
                if (!File.Exists(filePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[VsBridge] File not found: {filePath}");
                    return;
                }

                // 打开文档
                var window = _dte?.ItemOperations.OpenFile(filePath);
                if (window != null)
                {
                    window.Activate();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VsBridge] Failed to open file '{filePath}': {ex.Message}");
            }
        }

        /// <summary>
        /// 跳转到符号（如类、方法）
        /// </summary>
        private static void NavigateToSymbol(string symbolName)
        {
            try
            {
                //var find = _dte?.Find;
                //if (find == null) return;

                //find.Action = vsFindAction.vsFindActionNavigateTo;
                //find.Target = vsFindTarget.vsFindTargetCurrentDocument;
                //find.SearchFor = symbolName;

                //var result = find.Execute();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VsBridge] Failed to navigate to '{symbolName}': {ex.Message}");
            }
        }

        #endregion

        #region 命令 DTO 定义（新增）

        public class CommandDto
        {
            public string SolutionName { get; set; }

            /// <summary>
            /// OpenFile, NavigateTo, etc.
            /// </summary>
            public string Command { get; set; } = string.Empty;
            public string Path { get; set; }
            public int Column { get; set; } = 1;
            public int Line { get; set; } = 1;
            public DateTimeOffset? Timestamp { get; set; }
        }

        #endregion
    }
}
