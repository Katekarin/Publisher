using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Markdig;

namespace ConfluencePublisher;

internal static class Program
{
    internal static readonly Regex ImageRegex = new(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)]+)\)", RegexOptions.Compiled);
    internal static readonly Regex MermaidBlockRegex = new(@"```mermaid\s*(?<code>[\s\S]*?)```", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static readonly Regex HtmlImgRegex = new(@"<img\b[^>]*\bsrc=""(?<src>[^""]+)""[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Load(args);
        var logger = Logger.Create(options.LogFile);

        logger.Info("Starting Confluence publish run.");
        logger.Info($"Markdown: {options.MarkdownPath}");
        logger.Info($"Base URL: {options.BaseUrl}");
        logger.Info($"Space: {options.SpaceKey}");
        logger.Info($"Title: {options.Title}");

        if (string.IsNullOrWhiteSpace(options.MarkdownPath))
        {
            logger.Error("Missing --markdown argument.");
            return 1;
        }

        if (!File.Exists(options.MarkdownPath))
        {
            logger.Error($"Markdown file not found: {options.MarkdownPath}");
            return 1;
        }

        var credentials = Credentials.Load(options.CredentialsFile, logger);
        credentials = credentials.OverrideWith(options);
        if (!credentials.IsValid())
        {
            logger.Error("Missing credentials. Provide username/apiToken via credentials file or command line.");
            return 1;
        }

        if (options.SaveCredentials)
        {
            credentials.Save(options.CredentialsFile, logger);
        }

        if (string.IsNullOrWhiteSpace(options.SpaceKey) || string.IsNullOrWhiteSpace(options.Title))
        {
            logger.Error("Missing required parameters: --space and --title are required.");
            return 1;
        }

        var markdownText = await File.ReadAllTextAsync(options.MarkdownPath);
        var markdownDir = Path.GetDirectoryName(Path.GetFullPath(options.MarkdownPath)) ?? Environment.CurrentDirectory;

        var mermaidConverter = new MermaidConverter(options.MermaidCli, logger);
        var mermaidResult = await mermaidConverter.ReplaceMermaidBlocksAsync(markdownText);

        logger.Info("=== MARKDOWN PO ZAMIANIE MERMAID (pierwsze 1500 znaków) ===");
        logger.Info(mermaidResult.Markdown.Substring(0, Math.Min(1500, mermaidResult.Markdown.Length)) + (mermaidResult.Markdown.Length > 1500 ? "..." : ""));

        logger.Info($"Liczba wygenerowanych załączników mermaid: {mermaidResult.Attachments.Count}");
        foreach (var att in mermaidResult.Attachments)
        {
            logger.Info($"  - {att.FileName} ← z {att.SourcePath}");
        }

        var attachments = new Dictionary<string, AttachmentInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var mermaidAttachment in mermaidResult.Attachments)
        {
            attachments[mermaidAttachment.FileName] = mermaidAttachment;
        }

        var imageMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ImageRegex.Matches(mermaidResult.Markdown))
        {
            var url = match.Groups["url"].Value.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (url.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
            {
                var attachmentName = url.Substring("attachment:".Length);
                imageMappings[url] = attachmentName;
                continue;
            }

            var imagePath = Path.IsPathRooted(url) ? url : Path.GetFullPath(Path.Combine(markdownDir, url));
            if (!File.Exists(imagePath))
            {
                logger.Warn($"Image not found, skipping: {imagePath}");
                continue;
            }

            var attachmentFileName = Path.GetFileName(imagePath);
            attachmentFileName = EnsureUniqueFileName(attachmentFileName, attachments);
            attachments[attachmentFileName] = new AttachmentInfo(imagePath, attachmentFileName, false);

            imageMappings[url] = attachmentFileName;
            imageMappings[EscapeUriForMatch(url)] = attachmentFileName;
        }

        var html = Markdown.ToHtml(mermaidResult.Markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        var confluenceStorage = ConvertImagesToConfluenceStorage(html, imageMappings);

        using var client = new ConfluenceClient(credentials, logger);

        var pageId = options.PageId;
        if (string.IsNullOrWhiteSpace(pageId))
        {
            var existingPage = await client.GetPageByTitleAsync(options.SpaceKey, options.Title);
            pageId = existingPage?.Id;
        }

        if (string.IsNullOrWhiteSpace(pageId))
        {
            logger.Info("Page not found, creating new page.");
            var createdPage = await client.CreatePageAsync(options.SpaceKey, options.Title, options.ParentId, "<p>Publishing content...</p>");
            pageId = createdPage.Id;
        }
        else
        {
            logger.Info($"Using existing page ID: {pageId}");
        }

        foreach (var attachment in attachments.Values)
        {
            await client.UploadAttachmentAsync(pageId, attachment);
        }

        var updatedPage = await client.UpdatePageAsync(pageId, options.SpaceKey, options.Title, options.ParentId, confluenceStorage);
        logger.Info($"Published page ID {updatedPage.Id} at version {updatedPage.Version?.Number}.");

        logger.Info("Publish run completed.");
        return 0;
    }

    private static string EnsureUniqueFileName(string fileName, Dictionary<string, AttachmentInfo> attachments)
    {
        if (!attachments.ContainsKey(fileName))
        {
            return fileName;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;
        string candidate;
        do
        {
            candidate = $"{baseName}-{index}{extension}";
            index++;
        } while (attachments.ContainsKey(candidate));

        return candidate;
    }

    private static string EscapeUriForMatch(string url)
    {
        try
        {
            return Uri.EscapeDataString(url);
        }
        catch
        {
            return url;
        }
    }

    private static string ConvertImagesToConfluenceStorage(string html, Dictionary<string, string> imageMappings)
    {
        if (imageMappings.Count == 0)
        {
            return html;
        }

        return HtmlImgRegex.Replace(html, match =>
        {
            var src = match.Groups["src"].Value;
            if (!imageMappings.TryGetValue(src, out var attachmentName))
            {
                return match.Value;
            }

            return $"<ac:image><ri:attachment ri:filename=\"{EscapeAttribute(attachmentName)}\" /></ac:image>";
        });
    }

    private static string EscapeAttribute(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? value;
    }
}

internal sealed class Options
{
    public string MarkdownPath { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;
    public string SpaceKey { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string ParentId { get; private set; } = string.Empty;
    public string PageId { get; private set; } = string.Empty;
    public string Username { get; private set; } = string.Empty;
    public string ApiToken { get; private set; } = string.Empty;
    public string CredentialsFile { get; private set; } = "credentials.json";
    public string LogFile { get; private set; } = string.Empty;
    public string MermaidCli { get; private set; } = "mmdc";
    public bool SaveCredentials { get; private set; }

    public static Options Load(string[] args)
    {
        var options = new Options();
        var argMap = ParseArgs(args);

        options.MarkdownPath = GetArg(argMap, "markdown") ?? options.MarkdownPath;
        options.BaseUrl = GetArg(argMap, "base-url") ?? options.BaseUrl;
        options.SpaceKey = GetArg(argMap, "space") ?? options.SpaceKey;
        options.Title = GetArg(argMap, "title") ?? options.Title;
        options.ParentId = GetArg(argMap, "parent-id") ?? options.ParentId;
        options.PageId = GetArg(argMap, "page-id") ?? options.PageId;
        options.Username = GetArg(argMap, "username") ?? options.Username;
        options.ApiToken = GetArg(argMap, "api-token") ?? options.ApiToken;
        options.CredentialsFile = GetArg(argMap, "credentials-file") ?? options.CredentialsFile;
        options.LogFile = GetArg(argMap, "log-file") ?? options.LogFile;
        options.MermaidCli = GetArg(argMap, "mermaid-cli") ?? options.MermaidCli;
        options.SaveCredentials = argMap.ContainsKey("save-credentials");

        if (string.IsNullOrWhiteSpace(options.LogFile))
        {
            var logDir = Path.Combine(Environment.CurrentDirectory, "logs");
            Directory.CreateDirectory(logDir);
            options.LogFile = Path.Combine(logDir, $"publish-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        }

        return options;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg.Substring(2);
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[i + 1];
                i++;
            }
            else
            {
                map[key] = string.Empty;
            }
        }

        return map;
    }

    private static string? GetArg(Dictionary<string, string> args, string key)
    {
        return args.TryGetValue(key, out var value) ? value : null;
    }
}

internal sealed class Credentials
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string ApiToken { get; init; } = string.Empty;

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(BaseUrl) &&
               !string.IsNullOrWhiteSpace(Username) &&
               !string.IsNullOrWhiteSpace(ApiToken);
    }

    public static Credentials Load(string filePath, Logger logger)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                logger.Info($"Credentials file not found: {filePath}");
                return new Credentials();
            }

            var json = File.ReadAllText(filePath);
            var creds = JsonSerializer.Deserialize<Credentials>(json);
            return creds ?? new Credentials();
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to load credentials file: {ex.Message}");
            return new Credentials();
        }
    }

    public Credentials OverrideWith(Options options)
    {
        return new Credentials
        {
            BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? BaseUrl : options.BaseUrl,
            Username = string.IsNullOrWhiteSpace(options.Username) ? Username : options.Username,
            ApiToken = string.IsNullOrWhiteSpace(options.ApiToken) ? ApiToken : options.ApiToken
        };
    }

    public void Save(string filePath, Logger logger)
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            logger.Info($"Saved credentials to {filePath}");
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to save credentials file: {ex.Message}");
        }
    }
}

internal sealed class Logger
{
    private readonly string _logFile;
    private readonly object _lock = new();

    private Logger(string logFile)
    {
        _logFile = logFile;
    }

    public static Logger Create(string logFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile) ?? Environment.CurrentDirectory);
        return new Logger(logFile);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{level}] {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
    }
}

internal sealed record AttachmentInfo(string SourcePath, string FileName, bool IsMermaid);

internal sealed class MermaidConversionResult
{
    public MermaidConversionResult(string markdown, List<AttachmentInfo> attachments)
    {
        Markdown = markdown;
        Attachments = attachments;
    }

    public string Markdown { get; }
    public List<AttachmentInfo> Attachments { get; }
}

internal sealed class MermaidConverter
{
    private readonly string _mermaidCli;
    private readonly Logger _logger;

    public MermaidConverter(string mermaidCli, Logger logger)
    {
        _mermaidCli = string.IsNullOrWhiteSpace(mermaidCli) ? "mmdc" : mermaidCli;
        _logger = logger;
    }

    public async Task<MermaidConversionResult> ReplaceMermaidBlocksAsync(string markdown)
    {
        var attachments = new List<AttachmentInfo>();
        var matches = Program.MermaidBlockRegex.Matches(markdown);
        if (matches.Count == 0)
        {
            return new MermaidConversionResult(markdown, attachments);
        }

        var builder = new StringBuilder(markdown.Length);
        var lastIndex = 0;
        var index = 1;

        foreach (Match match in matches)
        {
            builder.Append(markdown, lastIndex, match.Index - lastIndex);
            lastIndex = match.Index + match.Length;

            var code = match.Groups["code"].Value.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                builder.Append(match.Value);
                continue;
            }

            var outputFileName = $"mermaid-diagram-{index}.png";
            var tempDir = Path.Combine(Path.GetTempPath(), "confluence-publisher");
            Directory.CreateDirectory(tempDir);
            var inputPath = Path.Combine(tempDir, $"mermaid-{Guid.NewGuid()}.mmd");
            var outputPath = Path.Combine(tempDir, outputFileName);
            await File.WriteAllTextAsync(inputPath, code);

            if (await RenderMermaidAsync(inputPath, outputPath))
            {
                attachments.Add(new AttachmentInfo(outputPath, outputFileName, true));
                builder.Append($"![Mermaid diagram {index}](attachment:{outputFileName})");
                index++;
            }
            else
            {
                _logger.Warn("Mermaid conversion failed; leaving code block untouched.");
                builder.Append(match.Value);
            }
        }

        builder.Append(markdown, lastIndex, markdown.Length - lastIndex);
        return new MermaidConversionResult(builder.ToString(), attachments);
    }

    private async Task<bool> RenderMermaidAsync(string inputPath, string outputPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _mermaidCli,
                Arguments = $"-i \"{inputPath}\" -o \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.Warn("Failed to start mermaid-cli process.");
                return false;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.Warn($"mermaid-cli failed with exit code {process.ExitCode}: {stderr}");
                return false;
            }

            if (!File.Exists(outputPath))
            {
                _logger.Warn("mermaid-cli reported success but output file is missing.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                _logger.Info(stdout.Trim());
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"mermaid-cli error: {ex.Message}");
            return false;
        }
    }
}

internal sealed class ConfluenceClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Logger _logger;

    public ConfluenceClient(Credentials credentials, Logger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { BaseAddress = new Uri(credentials.BaseUrl.TrimEnd('/') + "/") };

        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.ApiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

       // _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);

        //_httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ConfluencePage?> GetPageByTitleAsync(string spaceKey, string title)
    {
        var url = $"rest/api/content?title={Uri.EscapeDataString(title)}&spaceKey={Uri.EscapeDataString(spaceKey)}&expand=version";
        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        _logger.Info($"GET {url} -> {(int)response.StatusCode} {response.ReasonPhrase}");

        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn(body);
            return null;
        }

        var result = JsonSerializer.Deserialize<ConfluenceSearchResult>(body);
        return result?.Results?.FirstOrDefault();
    }

    public async Task<ConfluencePage> CreatePageAsync(string spaceKey, string title, string parentId, string bodyStorage)
    {
        var payload = new
        {
            type = "page",
            title,
            space = new { key = spaceKey },
            ancestors = string.IsNullOrWhiteSpace(parentId) ? null : new[] { new { id = parentId } },
            body = new
            {
                storage = new
                {
                    value = bodyStorage,
                    representation = "storage"
                }
            }
        };

        var response = await _httpClient.PostAsync("rest/api/content", SerializeJson(payload));
        var body = await response.Content.ReadAsStringAsync();
        _logger.Info($"POST rest/api/content -> {(int)response.StatusCode} {response.ReasonPhrase}");

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to create page: {body}");
        }

        return JsonSerializer.Deserialize<ConfluencePage>(body) ?? throw new InvalidOperationException("Missing create page response.");
    }

public async Task<ConfluencePage> UpdatePageAsync(string pageId, string spaceKey, string title, string parentId, string bodyStorage)
{
    var current = await GetPageByIdAsync(pageId);
    
    _logger.Info($"W UpdatePageAsync: current == null? {current == null}");
    _logger.Info($"W UpdatePageAsync: current.Version == null? {current?.Version == null}");
    if (current?.Version != null)
    {
        _logger.Info($"W UpdatePageAsync: current.Version.Number = {current.Version.Number}");
    }

    if (current == null)
    {
        throw new InvalidOperationException("Nie udało się pobrać strony – brak odpowiedzi.");
    }

    if (current.Version == null)
    {
        throw new InvalidOperationException("Strona nie ma pola version – coś jest nie tak z API.");
    }

    int reportedVersion = current.Version.Number;
    _logger.Info($"Pobrana wersja strony: {reportedVersion} (raw z JSON)");

    int nextVersion;

    if (reportedVersion <= 0)
    {
        _logger.Warn("Wersja == 0 → prawdopodobnie mismatch camelCase/PascalCase → zakładamy wersję 1 i idziemy na 2");
        nextVersion = 2;
    }
    else
    {
        nextVersion = reportedVersion + 1;
    }

    _logger.Info($"Będziemy wysyłać wersję: {nextVersion}");

    var payload = new
    {
        id = pageId,
        type = "page",
        title,
        space = new { key = spaceKey },
        ancestors = string.IsNullOrWhiteSpace(parentId) ? null : new[] { new { id = parentId } },
        version = new { number = nextVersion },
        body = new
        {
            storage = new
            {
                value = bodyStorage,
                representation = "storage"
            }
        }
    };

    var response = await _httpClient.PutAsync($"rest/api/content/{pageId}", SerializeJson(payload));
    var responseBody = await response.Content.ReadAsStringAsync();

    _logger.Info($"PUT rest/api/content/{pageId} → {(int)response.StatusCode} {response.ReasonPhrase}");

    if (!response.IsSuccessStatusCode)
    {
        _logger.Error($"Błąd aktualizacji strony: {responseBody}");
        throw new InvalidOperationException($"Nie udało się zaktualizować strony (status {(int)response.StatusCode}): {responseBody}");
    }

    var updatedPage = JsonSerializer.Deserialize<ConfluencePage>(responseBody);
    if (updatedPage == null)
    {
        throw new InvalidOperationException("Odpowiedź z aktualizacji strony jest pusta lub nie da się zdeserializować.");
    }

    _logger.Info($"Strona zaktualizowana do wersji {updatedPage.Version?.Number ?? -1}");
    return updatedPage;
}

    public async Task UploadAttachmentAsync(string pageId, AttachmentInfo attachment)
    {
        _logger.Info($"Uploading attachment: {attachment.FileName}");

        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(attachment.SourcePath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(attachment.SourcePath));
        form.Add(fileContent, "file", attachment.FileName);

        var request = new HttpRequestMessage(HttpMethod.Post, $"rest/api/content/{pageId}/child/attachment")
        {
            Content = form
        };
        request.Headers.Add("X-Atlassian-Token", "no-check");

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        _logger.Info($"POST rest/api/content/{pageId}/child/attachment -> {(int)response.StatusCode} {response.ReasonPhrase}");

        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn(body);
        }
    }

    private async Task<ConfluencePage?> GetPageByIdAsync(string pageId)
{
    var url = $"rest/api/content/{pageId}?expand=version";
    var response = await _httpClient.GetAsync(url);
    var body = await response.Content.ReadAsStringAsync();
    _logger.Info($"GET {url} -> {(int)response.StatusCode} {response.ReasonPhrase}");

    if (!response.IsSuccessStatusCode)
    {
        _logger.Warn(body);
        return null;
    }

    _logger.Info("Surowy JSON z GET page version:");
    _logger.Info(body);

    var deserializedPage = JsonSerializer.Deserialize<ConfluencePage>(body);
    
    _logger.Info($"Po deserializacji: Version == null? {deserializedPage?.Version == null}");
    if (deserializedPage?.Version != null)
    {
        _logger.Info($"Po deserializacji: Version.Number = {deserializedPage.Version.Number}");
    }
    else
    {
        _logger.Warn("Version jest null po deserializacji – coś nie bangla z mapowaniem!");
    }

    return deserializedPage;
}

    private static StringContent SerializeJson(object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string GetMimeType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed class ConfluenceSearchResult
{
    public List<ConfluencePage>? Results { get; set; }
}

internal sealed class ConfluencePage
{
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public ConfluenceVersion? Version { get; set; }
}
internal sealed class ConfluenceVersion
{
    [JsonPropertyName("number")]
    public int Number { get; set; }
}