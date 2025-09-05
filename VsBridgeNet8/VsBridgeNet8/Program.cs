using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using VsBridgeNet8.Models;
using VsBridgeNet8.Services;

var builder = WebApplication.CreateBuilder(args);
// 移除 System.Text.Json 配置
// builder.Services.Configure<JsonOptions>(o => { o.SerializerOptions.WriteIndented = true; });
builder.Services.AddSingleton<InMemoryStore>();

var app = builder.Build();
app.Urls.Clear();
app.Urls.Add("http://0.0.0.0:5006"); // 仅本地访问

var store = app.Services.GetRequiredService<InMemoryStore>();
var expectedToken = Environment.GetEnvironmentVariable("BRIDGE_TOKEN") ?? "dev-token";

// POST /v1/notify
app.MapPost("/v1/notify", async (HttpContext ctx) =>
{
    if (!ValidateToken(ctx, expectedToken)) return Results.Unauthorized();
    // 使用 Newtonsoft.Json 解析
    NotifyDto? dto;
    if (!ctx.Request.Body.CanRead || ctx.Request.ContentLength == 0)
        return Results.BadRequest();
    using (var reader = new StreamReader(ctx.Request.Body))
    {
        var body = await reader.ReadToEndAsync();
        dto = JsonConvert.DeserializeObject<NotifyDto>(body);
    }
    if (dto == null) return Results.BadRequest();

    dto.Timestamp ??= DateTimeOffset.UtcNow;
    if (string.IsNullOrEmpty(dto.FullHash) && !string.IsNullOrEmpty(dto.ContentSnippet))
    {
        dto.FullHash = ComputeSha256(dto.ContentSnippet);
    }

    store.SetCurrent(dto);
    store.StoreFile(dto.Path, dto);
    return Results.Ok(new { ok = true });
});

// GET /v1/current-file
app.MapGet("/v1/current-file", (HttpRequest req) =>
{
    if (!ValidateToken(req.HttpContext, expectedToken)) return Results.Unauthorized();
    var full = req.Query["full"].ToString().ToLower() == "true";
    var dto = store.GetCurrent();
    if (dto == null) return Results.NotFound();
    if (!full)
    {
        // 使用 Newtonsoft.Json 序列化
        var result = new
        {
            path = dto.Path,
            contentSnippet = dto.ContentSnippet,
            selection = dto.Selection,
            cursor = dto.Cursor,
            project = dto.Project,
            timestamp = dto.Timestamp
        };
        return Results.Text(JsonConvert.SerializeObject(result), "application/json");
    }
    return Results.Text(JsonConvert.SerializeObject(dto), "application/json");
});

// GET /v1/current-context
app.MapGet("/v1/current-context", (HttpRequest req) =>
{
    if (!ValidateToken(req.HttpContext, expectedToken)) return Results.Unauthorized();
    var linesAround = 50;
    if (int.TryParse(req.Query["lines"], out var q)) linesAround = Math.Clamp(q, 5, 1000);

    var dto = store.GetCurrent();
    if (dto == null) return Results.NotFound();

    var snippet = dto.ContentSnippet ?? "";
    var result = new { path = dto.Path, context = snippet, cursor = dto.Cursor };
    return Results.Text(JsonConvert.SerializeObject(result), "application/json");
});

// GET /v1/file
app.MapGet("/v1/file", (HttpRequest req) =>
{
    if (!ValidateToken(req.HttpContext, expectedToken)) return Results.Unauthorized();
    var path = req.Query["path"].ToString();
    var full = req.Query["full"].ToString().ToLower() == "true";
    if (string.IsNullOrEmpty(path)) return Results.BadRequest("missing path");

    if (!store.TryGetFile(path, out var dto)) return Results.NotFound();

    if (full)
    {
        return Results.Text(JsonConvert.SerializeObject(dto), "application/json");
    }

    var result = new { path = dto.Path, contentSnippet = dto.ContentSnippet, cursor = dto.Cursor };
    return Results.Text(JsonConvert.SerializeObject(result), "application/json");
});

// POST /v1/solution-tree
app.MapPost("/v1/solution-tree", async (HttpContext ctx) =>
{
    if (!ValidateToken(ctx, expectedToken)) return Results.Unauthorized();

    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();

        var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
        if (obj == null)
            return Results.BadRequest("Invalid JSON");

        if (!obj.ContainsKey("solutionName") || !obj.ContainsKey("projects"))
            return Results.BadRequest("Missing 'solutionName' or 'solutionTree' field");

        var solutionName = obj["solutionName"]?.ToString();
        var tree = obj["projects"];

        if (string.IsNullOrEmpty(solutionName))
            return Results.BadRequest("Missing 'solutionName' value");

        // ✅ 正确调用：传入 solutionName 和 tree
        store.SetSolutionTree(solutionName, tree);

        return Results.Json(new { ok = true, received = "solutionTree", solutionName });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Failed to process solution-tree: {ex.Message}");
        return Results.StatusCode(500);
    }
});
// GET /v1/solution-tree
app.MapGet("/v1/solution-tree", (HttpRequest req) =>
{
    if (!ValidateToken(req.HttpContext, expectedToken)) return Results.Unauthorized();
    var tree = store.GetLastActiveSolutionTree();
    if (tree == null) return Results.NotFound();
    return Results.Text(JsonConvert.SerializeObject(tree), "application/json");
});

// POST /v1/command
app.MapPost("/v1/command", async (HttpContext ctx) =>
{
    if (!ValidateToken(ctx, expectedToken)) return Results.Unauthorized();
    CommandDto? cmd;
    using (var reader = new StreamReader(ctx.Request.Body))
    {
        var body = await reader.ReadToEndAsync();
        cmd = JsonConvert.DeserializeObject<CommandDto>(body);
    }
    if (cmd == null) return Results.BadRequest();
    store.AddCommand(cmd);
    return Results.Text(JsonConvert.SerializeObject(new { accepted = true }), "application/json");
});

// POST /v1/activate-file?name=Program.cs
app.MapPost("/v1/activate-file", (HttpRequest req) =>
{
    if (!ValidateToken(req.HttpContext, expectedToken)) return Results.Unauthorized();

    var fileName = req.Query["name"].ToString();
    if (string.IsNullOrEmpty(fileName))
        return Results.BadRequest("Missing 'name' query parameter");

    var command = store.FindAndActivateFile(fileName);
    if (command == null)
        return Results.NotFound($"File '{fileName}' not found in the last active solution tree.");

    // 将命令加入队列
    store.AddCommand(command);

    return Results.Json(new
    {
        activated = true,
        file = fileName
    });
});

app.MapGet("/v1/commands", (HttpRequest req) =>
{
    if (!ValidateToken(req.HttpContext, expectedToken)) return Results.Unauthorized();

    var solutionName = req.Query["solution"].ToString();

    if (string.IsNullOrEmpty(solutionName)) return Results.BadRequest();

    var commands = store.GetCommandsBySolutionName(solutionName);
    return Results.Text(JsonConvert.SerializeObject(commands), "application/json");
});

// POST /v1/files
app.MapPost("/v1/files", async (HttpContext ctx) =>
{
    if (!ValidateToken(ctx, expectedToken)) return Results.Unauthorized();

    List<string>? paths;
    bool full = true;

    using (var reader = new StreamReader(ctx.Request.Body))
    {
        var body = await reader.ReadToEndAsync();
        var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
        if (obj == null || !obj.ContainsKey("paths")) return Results.BadRequest("missing paths");

        paths = JsonConvert.DeserializeObject<List<string>>(obj["paths"].ToString() ?? "[]");
        if (obj.TryGetValue("full", out var f) && f is bool fb) full = fb;
    }

    if (paths == null || paths.Count == 0) return Results.BadRequest("no paths provided");

    var result = new List<object>();
    foreach (var path in paths)
    {
        if (store.TryGetFile(path, out var dto))
        {
            if (full)
            {
                result.Add(dto);
            }
            else
            {
                result.Add(new
                {
                    path = dto.Path,
                    contentSnippet = dto.ContentSnippet,
                    cursor = dto.Cursor
                });
            }
        }
    }

    return Results.Text(JsonConvert.SerializeObject(result), "application/json");
});

app.Run();

static string ComputeSha256(string s)
{
    using var sha = SHA256.Create();
    var b = Encoding.UTF8.GetBytes(s ?? "");
    return "sha256:" + BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
}

static bool ValidateToken(HttpContext ctx, string expected)
{
    if (!ctx.Request.Headers.TryGetValue("X-Local-Token", out var token)) return false;
    return token == expected;
}
