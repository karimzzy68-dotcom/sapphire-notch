using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SapphireNotch.Windows;

/// <summary>One Codex or Claude login the notch tracks. The tokens stay in the tool's own folder; only ids and switches are saved here.</summary>
internal sealed class AiAccountRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "codex"; // "codex" or "claude"
    public string Home { get; set; } = "";
    public bool Connected { get; set; } = true;
    public bool AutoStart { get; set; }
}

internal sealed record AiUsage(double? SessionUsed, DateTime? SessionReset, double? WeekUsed, DateTime? WeekReset, string? Plan, string? Email, string? Error);

/// <summary>Reads the sign-in files Codex and Claude already keep on this PC and asks their servers how much of the 5-hour and weekly limits is used. Tokens are never logged or shown.</summary>
internal static class AiAccounts
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string StoreFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire NOTCh", "accounts.json");

    public static string ExtraHomes => Path.Combine(Path.GetDirectoryName(StoreFile)!, "accounts");

    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static List<AiAccountRecord> Load()
    {
        List<AiAccountRecord> list = new();
        try
        {
            if (File.Exists(StoreFile))
            {
                list = JsonSerializer.Deserialize<List<AiAccountRecord>>(File.ReadAllText(StoreFile)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        Detect(list);
        return list;
    }

    /// <summary>Adds the logins found on this PC (the default Codex and Claude folders and any extra Codex folders made by Add account).</summary>
    public static void Detect(List<AiAccountRecord> list)
    {
        AddIfFound(list, "codex-default", "codex", Path.Combine(UserHome, ".codex"), "auth.json");
        AddIfFound(list, "claude-default", "claude", Path.Combine(UserHome, ".claude"), ".credentials.json");
        if (Directory.Exists(ExtraHomes))
        {
            foreach (string dir in Directory.GetDirectories(ExtraHomes, "codex-*"))
            {
                AddIfFound(list, Path.GetFileName(dir), "codex", dir, "auth.json");
            }
            foreach (string dir in Directory.GetDirectories(ExtraHomes, "claude-*"))
            {
                AddIfFound(list, Path.GetFileName(dir), "claude", dir, ".credentials.json");
            }
        }
    }

    private static void AddIfFound(List<AiAccountRecord> list, string id, string kind, string home, string file)
    {
        if (File.Exists(Path.Combine(home, file)) && list.All(a => a.Id != id))
        {
            list.Add(new AiAccountRecord { Id = id, Kind = kind, Home = home });
        }
    }

    public static void Save(List<AiAccountRecord> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task<AiUsage> FetchAsync(AiAccountRecord account)
    {
        try
        {
            return account.Kind == "claude" ? await FetchClaudeAsync(account) : await FetchCodexAsync(account);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return Failed("Could not reach the server.");
        }
    }

    private static AiUsage Failed(string error) => new(null, null, null, null, null, null, error);

    private static async Task<AiUsage> FetchClaudeAsync(AiAccountRecord account)
    {
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(account.Home, ".credentials.json")));
        JsonElement oauth = file.RootElement.GetProperty("claudeAiOauth");
        string token = oauth.GetProperty("accessToken").GetString() ?? "";
        if (oauth.TryGetProperty("expiresAt", out JsonElement expires) && DateTimeOffset.FromUnixTimeMilliseconds(expires.GetInt64()) < DateTimeOffset.Now)
        {
            return Failed("Sign-in expired. Open Claude once to renew it.");
        }
        string? plan = oauth.TryGetProperty("subscriptionType", out JsonElement sub) ? sub.GetString() : null;
        string? email = await ClaudeEmailAsync(token);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using HttpResponseMessage response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return Failed(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "Sign-in expired. Open Claude once to renew it." : "Usage unavailable (" + (int)response.StatusCode + ").");
        }
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        (double? sessionUsed, DateTime? sessionReset) = ReadClaudeWindow(body.RootElement, "five_hour");
        (double? weekUsed, DateTime? weekReset) = ReadClaudeWindow(body.RootElement, "seven_day");
        return new AiUsage(sessionUsed, sessionReset, weekUsed, weekReset, plan, email, null);
    }

    private static async Task<string?> ClaudeEmailAsync(string token)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using HttpResponseMessage response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.TryGetProperty("account", out JsonElement account) && account.TryGetProperty("email", out JsonElement email) ? email.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static (double?, DateTime?) ReadClaudeWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement window) || window.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }
        double? used = window.TryGetProperty("utilization", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
        DateTime? reset = window.TryGetProperty("resets_at", out JsonElement r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out DateTimeOffset when) ? when.LocalDateTime : null;
        return (used, reset);
    }

    private static async Task<AiUsage> FetchCodexAsync(AiAccountRecord account)
    {
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(account.Home, "auth.json")));
        if (!file.RootElement.TryGetProperty("tokens", out JsonElement tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            return Failed("Not signed in with ChatGPT.");
        }
        string token = tokens.GetProperty("access_token").GetString() ?? "";
        string accountId = tokens.TryGetProperty("account_id", out JsonElement id) ? id.GetString() ?? "" : "";

        using HttpRequestMessage request = new(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accountId.Length > 0)
        {
            request.Headers.Add("ChatGPT-Account-Id", accountId);
        }
        using HttpResponseMessage response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return Failed(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "Sign-in expired. Open Codex once to renew it." : "Usage unavailable (" + (int)response.StatusCode + ").");
        }
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        (double? sessionUsed, DateTime? sessionReset) = (null, null);
        (double? weekUsed, DateTime? weekReset) = (null, null);
        if (root.TryGetProperty("rate_limit", out JsonElement limit) && limit.ValueKind == JsonValueKind.Object)
        {
            (sessionUsed, sessionReset) = ReadCodexWindow(limit, "primary_window");
            (weekUsed, weekReset) = ReadCodexWindow(limit, "secondary_window");
        }
        string? plan = root.TryGetProperty("plan_type", out JsonElement p) ? p.GetString() : null;
        string? email = root.TryGetProperty("email", out JsonElement e) ? e.GetString() : null;
        return new AiUsage(sessionUsed, sessionReset, weekUsed, weekReset, plan, email, null);
    }

    private static (double?, DateTime?) ReadCodexWindow(JsonElement limit, string name)
    {
        if (!limit.TryGetProperty(name, out JsonElement window) || window.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }
        double? used = window.TryGetProperty("used_percent", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
        DateTime? reset = window.TryGetProperty("reset_at", out JsonElement r) && r.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()).LocalDateTime : null;
        return (used, reset);
    }

    // ---- starting a session / signing in through the tool's own command line -----------------

    private static string? FindCodex()
    {
        // Windows does not let normal programs list the WindowsApps folder, but the install path is in the registry.
        try
        {
            using Microsoft.Win32.RegistryKey? packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            foreach (string name in (packages?.GetSubKeyNames() ?? Array.Empty<string>()).Where(n => n.StartsWith("OpenAI.Codex_")).OrderByDescending(n => n))
            {
                using Microsoft.Win32.RegistryKey? package = packages!.OpenSubKey(name);
                if (package?.GetValue("PackageRootFolder") is string root)
                {
                    string exe = Path.Combine(root, "app", "resources", "codex.exe");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return null;
    }

    /// <summary>Sends one tiny prompt through the account's own command line so a new 5-hour window starts. Returns an error text, or null when it went through.</summary>
    public static async Task<string?> StartSessionAsync(AiAccountRecord account)
    {
        ProcessStartInfo start;
        if (account.Kind == "claude")
        {
            start = new ProcessStartInfo("cmd.exe", "/c claude -p \"Reply with the single word ok.\"");
            if (!account.Home.EndsWith(".claude", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment["CLAUDE_CONFIG_DIR"] = account.Home;
            }
        }
        else
        {
            string? codex = FindCodex();
            if (codex == null)
            {
                return "Codex command line not found.";
            }
            start = new ProcessStartInfo(codex, "exec --skip-git-repo-check \"Reply with the single word ok.\"");
            start.Environment["CODEX_HOME"] = account.Home;
        }
        start.WorkingDirectory = Path.GetTempPath();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        try
        {
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            return process.ExitCode == 0 ? null : "The command line returned an error.";
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "Could not start the command line (" + ex.Message + ").";
        }
    }

    /// <summary>Opens the browser sign-in for a new Codex or Claude account, kept in its own folder so it never touches the main login.</summary>
    public static string? BeginSignIn(string kind, out string home)
    {
        int n = 2;
        string folder;
        Directory.CreateDirectory(ExtraHomes);
        while (Directory.Exists(folder = Path.Combine(ExtraHomes, kind + "-" + n)))
        {
            n++;
        }
        home = folder;
        ProcessStartInfo start;
        if (kind == "claude")
        {
            start = new ProcessStartInfo("cmd.exe", "/c claude auth login --claudeai");
            start.Environment["CLAUDE_CONFIG_DIR"] = folder;
        }
        else
        {
            string? codex = FindCodex();
            if (codex == null)
            {
                return "Codex command line not found.";
            }
            start = new ProcessStartInfo(codex, "login");
            start.Environment["CODEX_HOME"] = folder;
        }
        Directory.CreateDirectory(folder);
        start.UseShellExecute = false;
        // Claude may ask for a code pasted back after the browser step, so its sign-in gets a visible window; Codex finishes by itself
        start.CreateNoWindow = kind != "claude";
        try
        {
            Process.Start(start);
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return "Could not start sign-in (" + ex.Message + ").";
        }
    }
}
