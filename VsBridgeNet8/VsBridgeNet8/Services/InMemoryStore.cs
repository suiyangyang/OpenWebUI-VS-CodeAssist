// Services/InMemoryStore.cs
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using VsBridgeNet8.Models;

namespace VsBridgeNet8.Services
{
    public class InMemoryStore
    {
        private NotifyDto? _current;
        private readonly ConcurrentDictionary<string, NotifyDto> _files = new();
        private ConcurrentQueue<CommandDto> _commands = new();

        // 多 solution tree 存储：key 是 SolutionName（或 FullName）
        private readonly ConcurrentDictionary<string, object> _solutionTrees = new();

        // 最后两个活动文档（按时间顺序，最新在前）
        private readonly LinkedList<NotifyDto> _lastTwoActiveDocuments = new();

        // 用于快速查找最近的 solution tree
        private string? _lastActiveSolutionName;

        public void SetCurrent(NotifyDto dto)
        {
            _current = dto;
            // 更新最后活动的 solution name
            if (!string.IsNullOrEmpty(dto.SolutionName))
                _lastActiveSolutionName = dto.SolutionName;

            // 添加到最后两个文档链表中（去重：如果已存在则移除再添加）
            RemoveIfPresent(_lastTwoActiveDocuments, d => d.Path == dto.Path);
            _lastTwoActiveDocuments.AddFirst(dto);

            // 限制最多保留两个
            if (_lastTwoActiveDocuments.Count > 2)
                _lastTwoActiveDocuments.RemoveLast();
        }

        public NotifyDto? GetCurrent() => _current;

        public void StoreFile(string path, NotifyDto dto) => _files[path] = dto;

        public bool TryGetFile(string path, out NotifyDto? dto) => _files.TryGetValue(path, out dto);

        // 新增：获取最后一个活动文档所属的 solution tree
        public object? GetLastActiveSolutionTree()
        {
            if (string.IsNullOrEmpty(_lastActiveSolutionName))
                return null;

            return GetSolutionTree(_lastActiveSolutionName);
        }

        // 保存某个 solution tree（按 SolutionName 为 key）
        public void SetSolutionTree(string solutionName, object tree)
        {
            _solutionTrees[solutionName] = tree;
        }

        public object? GetSolutionTree(string solutionName) => _solutionTrees.TryGetValue(solutionName, out var tree) ? tree : null;

        // 获取所有已保存的 solution trees（用于调试或展示）
        public IReadOnlyDictionary<string, object> GetAllSolutionTrees() => _solutionTrees.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // 获取最后两个活动文档
        public IEnumerable<NotifyDto> GetLastTwoActiveDocuments()
        {
            return _lastTwoActiveDocuments.ToList();
        }

        // 获取最后一个活动文档
        public NotifyDto? GetLastActiveDocument() => _lastTwoActiveDocuments.First?.Value;

        // 根据文件名在最后活动的 solution tree 中查找第一个匹配的文件路径
        public string? FindFileInLastSolutionTree(string filename)
        {
            if (string.IsNullOrEmpty(_lastActiveSolutionName))
                return null;

            var tree = GetSolutionTree(_lastActiveSolutionName);
            if (tree == null) return null;

            // 假设 tree 是一个包含 projects 和 files 的结构
            // 我们用反射或简单 JSON 解析来遍历项目和文件
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(tree);
                var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                if (obj?.ContainsKey("projects") == true)
                {
                    var projects = obj["projects"] as IEnumerable<object>;
                    foreach (var project in projects)
                    {
                        var projObj = project as Dictionary<string, object>;
                        if (projObj?.ContainsKey("files") == true)
                        {
                            var files = projObj["files"] as IEnumerable<object>;
                            foreach (var file in files)
                            {
                                var path = file.ToString();
                                if (!string.IsNullOrEmpty(path) && Path.GetFileName(path).Equals(filename, StringComparison.OrdinalIgnoreCase))
                                {
                                    return path;
                                }
                            }
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public void AddCommand(CommandDto cmd) => _commands.Enqueue(cmd);

        public IEnumerable<CommandDto> DrainCommands()
        {
            while (_commands.TryDequeue(out var c)) yield return c;
        }

        /// <summary>
        /// 根据 SolutionName 获取该 solution 所有相关的 CommandDto 命令，并从队列中移除。
        /// 支持模糊匹配（如 "MyApp" 匹配 "MyApp.sln"）。
        /// </summary>
        /// <param name="solutionName">解决方案名称（如 "MyApp.sln" 或 "MyApp"）</param>
        /// <returns>该 solution 的命令列表，若无则返回空列表</returns>
        public List<CommandDto> GetCommandsBySolutionName(string solutionName)
        {
            if (string.IsNullOrEmpty(solutionName))
                return new List<CommandDto>();

            // 标准化：去除 .sln 后缀，转小写
            var normalizedSolution = solutionName.ToLowerInvariant();
            if (normalizedSolution.EndsWith(".sln"))
                normalizedSolution = normalizedSolution.Substring(0, normalizedSolution.Length - 4);

            var result = new List<CommandDto>();
            var tempQueue = new ConcurrentQueue<CommandDto>();

            // 遍历原始队列，只保留不匹配的命令
            while (_commands.TryDequeue(out var cmd))
            {
                if (cmd.SolutionName == null)
                {
                    tempQueue.Enqueue(cmd);
                    continue;
                }

                var cmdSol = cmd.SolutionName.ToLowerInvariant();
                if (cmdSol.EndsWith(".sln"))
                    cmdSol = cmdSol.Substring(0, cmdSol.Length - 4);

                if (string.Equals(cmdSol, normalizedSolution, StringComparison.OrdinalIgnoreCase))
                {
                    // ✅ 匹配：加入结果，不放回队列
                    result.Add(cmd);
                }
                else
                {
                    // ❌ 不匹配：放回临时队列
                    tempQueue.Enqueue(cmd);
                }
            }

            // ✅ 恢复未匹配的命令到主队列
            _commands = tempQueue;

            return result;
        }

        // 私有辅助方法：从链表中移除匹配项
        private static void RemoveIfPresent(LinkedList<NotifyDto> list, Predicate<NotifyDto> match)
        {
            var node = list.First;
            while (node != null)
            {
                var next = node.Next;
                if (match(node.Value))
                    list.Remove(node);
                node = next;
            }
        }

        /// <summary>
        /// 在最后一个活动的 solution tree 中查找指定文件名，若存在则返回 OpenFile 命令。
        /// 支持忽略大小写和路径分隔符差异（\ vs /）。
        /// </summary>
        /// <param name="fileName">要激活的文件名（如 "Program.cs" 或 "src/Program.cs"）</param>
        /// <returns>CommandDto，若未找到则返回 null</returns>
        public CommandDto? FindAndActivateFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;

            var tree = GetLastActiveSolutionTree();
            if (tree == null)
                return null;

            var projects = tree as JArray;
            if (projects == null || !projects.Any())
                return null;

            var normalizedFileName = NormalizePath(fileName);
            var fileNameOnly = Path.GetFileNameWithoutExtension(normalizedFileName);

            CommandDto? bestMatch = null;
            double bestScore = 0.0;

            foreach (var project in projects)
            {
                var filesArray = project["files"] as JArray;
                if (filesArray == null) continue;

                foreach (var fileObj in filesArray)
                {
                    var filePath = fileObj.ToString();
                    if (string.IsNullOrEmpty(filePath)) continue;

                    var normalizedPath = NormalizePath(filePath);
                    var fileObjName = Path.GetFileNameWithoutExtension(normalizedPath);

                    // 1) 精确匹配优先
                    if (string.Equals(normalizedPath, normalizedFileName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileObjName, fileNameOnly, StringComparison.OrdinalIgnoreCase))
                    {
                        return new CommandDto
                        {
                            SolutionName = _lastActiveSolutionName,
                            Command = "OpenFile",
                            Path = filePath,
                            Line = 1,
                            Column = 1
                        };
                    }

                    // 2) 计算相似度
                    double score = CalculateSimilarity(fileObjName, fileNameOnly);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = new CommandDto
                        {
                            SolutionName = _lastActiveSolutionName,
                            Command = "OpenFile",
                            Path = filePath,
                            Line = 1,
                            Column = 1
                        };
                    }
                }
            }

            // 如果相似度够高（比如 > 0.6），就返回最接近的
            return bestScore >= 0.6 ? bestMatch : null;
        }

        /// <summary>
        /// 计算字符串相似度（基于Levenshtein距离）
        /// </summary>
        private static double CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 1;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;

            int distance = LevenshteinDistance(s1.ToLowerInvariant(), s2.ToLowerInvariant());
            int maxLen = Math.Max(s1.Length, s2.Length);
            return 1.0 - (double)distance / maxLen;
        }

        private static int LevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }


        /// <summary>
        /// 标准化路径：统一使用 /，转小写
        /// </summary>
        private string NormalizePath(string path)
        {
            return path.Replace('\\', '/').ToLowerInvariant();
        }
    }
}