// Models/NotifyDto.cs
namespace VsBridgeNet8.Models
{

    public class NotifyDto
    {
        public string SolutionName { get; set; }
        public string Path { get; init; } = string.Empty;
        public string? ContentSnippet { get; init; } // 推荐：snippet 已包含光标附近内容
        public string? FullHash { get; set; }
        public CursorInfo? Cursor { get; init; }
        public string? Selection { get; init; }
        public string? Project { get; init; }
        public DateTimeOffset? Timestamp { get; set; }
        public string? FullContent { get; init; } // 可选：若 VSIX 在 notify 时上传全文
    }

    public class CommandDto
    {
        public string SolutionName { get; set; }

        /// <summary>
        /// OpenFile, NavigateTo, etc.
        /// </summary>
        public string Command { get; init; } = string.Empty;
        public string Path { get; init; }
        public int Column { get; set; } = 1;
        public int Line { get; set; } = 1;
        public DateTimeOffset? Timestamp { get; init; }
    }
}
