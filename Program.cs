// Scriveno API quickstart (.NET 9, no third-party packages).
//
// Config lives in appsettings.json, not environment variables/CLI args - your API key, base URL,
// input file, download formats, and download folder are all there, ready to edit in place like any
// other .NET app's config. Running it with no arguments does one full job automatically: shows the
// config, waits for you to confirm, then submits, polls with a live status line, and downloads the
// result - printing what it's doing at each stage rather than requiring you to invoke separate
// modes for each step.
//
// Two small utility modes, unrelated to that one job's own stages, are also available:
//   dotnet run -- --list             List your own jobs
//   dotnet run -- --cancel <jobId>   Cancel a still-queued/processing job (refunds its quota in full)
//
// Get an API key from https://scriveno.com/developer - it requires Developer access on your plan.
//
// Run it:
//   cd samples/dotnet-quickstart
//   1. Edit appsettings.json (or copy it to appsettings.Local.json - see README.md) with your key,
//      input file, and download folder.
//   2. dotnet run

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

if (args is ["--list"])
{
    return await RunWithConfigAsync(requireJobFields: false, (http, _, ct) => ListJobsAsync(http, jsonOptions, ct));
}

if (args is ["--cancel", var cancelArg])
{
    if (!Guid.TryParse(cancelArg, out var cancelJobId))
    {
        Console.Error.WriteLine("Usage: dotnet run -- --cancel <jobId>");
        return 1;
    }
    return await RunWithConfigAsync(requireJobFields: false, (http, _, ct) => CancelJobAsync(http, jsonOptions, cancelJobId, ct));
}

if (args is ["--cancel"])
{
    Console.Error.WriteLine("Usage: dotnet run -- --cancel <jobId>");
    return 1;
}

return await RunWithConfigAsync(requireJobFields: true, RunJobAsync);

// --- Shared setup: load config, build the HttpClient, wire up Ctrl+C, then hand off ----------------

// requireJobFields is false for --list/--cancel: those only ever need a valid ApiKey/BaseUrl to make
// a call, and forcing InputFile/Formats/DownloadDirectory to be valid too would block someone who
// just wants to check on or cancel a job with an appsettings.json that (quite reasonably) doesn't
// point at a real file yet.
static async Task<int> RunWithConfigAsync(bool requireJobFields, Func<HttpClient, ScrivenoOptions, CancellationToken, Task<int>> action)
{
    var options = LoadScrivenoOptions(AppContext.BaseDirectory);
    if (!TryValidate(options, requireJobFields, out var validationError, out var normalized))
    {
        Console.Error.WriteLine($"Configuration problem: {validationError}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Edit appsettings.json (or appsettings.Local.json) in this folder and try again.");
        return 1;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        // Let the current HTTP call/poll tick unwind via the token instead of the process dying mid-
        // request with a raw stack trace - a job already submitted keeps running server-side either
        // way, it's only this console that stops watching it.
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("Cancelling...");
        cts.Cancel();
    };

    using var http = new HttpClient { BaseAddress = new Uri(normalized.BaseUrl) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", normalized.ApiKey);

    try
    {
        return await action(http, normalized, cts.Token);
    }
    catch (OperationCanceledException)
    {
        return 130; // Conventional Unix exit code for SIGINT.
    }
}

// --- The default mode: submit, poll to a terminal state, then download what was asked for ----------

static async Task<int> RunJobAsync(HttpClient http, ScrivenoOptions options, CancellationToken cancellationToken)
{
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    PrintHeader();
    PrintConfigSummary(options);
    Console.WriteLine();
    Console.Write("Press Enter to start this conversion job (Ctrl+C to cancel)... ");
    Console.ReadLine();
    Console.WriteLine();

    var submitted = await SubmitAsync(http, jsonOptions, options.InputFile, cancellationToken);
    if (submitted is null)
    {
        return 1;
    }

    Console.WriteLine($"Job {submitted.JobId} queued - {submitted.TotalPages} page(s), {submitted.QueuePosition} ahead of it.");
    Console.WriteLine();

    var final = await PollUntilTerminalAsync(http, jsonOptions, submitted.JobId, cancellationToken);
    if (final is null)
    {
        return 1;
    }

    Console.WriteLine();
    return final.Status switch
    {
        "completed" => await HandleCompletedAsync(http, jsonOptions, options, final, cancellationToken),
        _ => HandleUnsuccessful(final),
    };
}

// --- 1. Submit -------------------------------------------------------------------------------------

static async Task<SubmitResponse?> SubmitAsync(HttpClient http, JsonSerializerOptions jsonOptions, string filePath, CancellationToken cancellationToken)
{
    Console.WriteLine($"Submitting {Path.GetFileName(filePath)} ({DescribeSize(new FileInfo(filePath).Length)})...");

    using var form = new MultipartFormDataContent();
    using var fileStream = File.OpenRead(filePath);
    using var fileContent = new StreamContent(fileStream);
    form.Add(fileContent, "file", Path.GetFileName(filePath));

    using var response = await http.PostAsync("/api/v1/convert", form, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        PrintApiError(response, body, jsonOptions);
        return null;
    }

    // Only the submit response carries these - the cheapest way to notice you're about to hit your
    // plan's daily/monthly cap before a future submit gets rejected with a 429.
    if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
        && response.Headers.TryGetValues("X-RateLimit-Limit", out var limit))
    {
        Console.WriteLine($"Quota: {remaining.First()}/{limit.First()} pages remaining today.");
    }

    return JsonSerializer.Deserialize<SubmitResponse>(body, jsonOptions)!;
}

// --- 2. Poll, printing a single live-updating status line instead of scrolling spam ----------------

static async Task<StatusResponse?> PollUntilTerminalAsync(HttpClient http, JsonSerializerOptions jsonOptions, Guid jobId, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var lastLineLength = 0;

    while (true)
    {
        using var response = await http.GetAsync($"/api/v1/convert/{jobId}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine();
            PrintApiError(response, body, jsonOptions);
            return null;
        }

        var status = JsonSerializer.Deserialize<StatusResponse>(body, jsonOptions)!;
        var line = BuildStatusLine(status, stopwatch.Elapsed);
        // Pad over whatever the previous line left behind (e.g. "processing 4/12" is shorter than
        // "queued, 3 ahead of it") so no stale characters trail off the end.
        Console.Write($"\r{line.PadRight(lastLineLength)}");
        lastLineLength = line.Length;

        if (status.Status is "completed" or "failed" or "cancelled")
        {
            Console.WriteLine();
            return status;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}

static string BuildStatusLine(StatusResponse status, TimeSpan elapsed)
{
    var detail = status.Status == "queued"
        ? $"{status.QueuePosition} job(s) ahead of it"
        : $"{status.CompletedPages}/{status.TotalPages} page(s) recognized";
    return $"  [{elapsed:hh\\:mm\\:ss}] {status.Status,-10} - {detail}";
}

// --- 3. A completed job: show what's available, then download the configured format(s) -------------

static async Task<int> HandleCompletedAsync(
    HttpClient http, JsonSerializerOptions jsonOptions, ScrivenoOptions options, StatusResponse status, CancellationToken cancellationToken)
{
    var downloads = status.Downloads!;
    Console.WriteLine($"Job completed - {status.TotalPages} page(s) recognized.");
    Console.WriteLine();
    Console.WriteLine("Available downloads:");
    Console.WriteLine($"  pdf       {downloads.Pdf}");
    Console.WriteLine($"  docx      {downloads.Docx}");
    Console.WriteLine($"  markdown  {downloads.Markdown}");
    Console.WriteLine($"  latex     {downloads.Latex}");
    Console.WriteLine();

    Directory.CreateDirectory(options.DownloadDirectory);
    Console.WriteLine($"Saving {string.Join(", ", options.Formats)} to {Path.GetFullPath(options.DownloadDirectory)}:");

    var allSaved = true;
    foreach (var format in options.Formats)
    {
        allSaved &= await DownloadOneAsync(http, jsonOptions, options, format, downloads, cancellationToken);
    }

    return allSaved ? 0 : 1;
}

static async Task<bool> DownloadOneAsync(
    HttpClient http, JsonSerializerOptions jsonOptions, ScrivenoOptions options, string format, DownloadUrls downloads, CancellationToken cancellationToken)
{
    var (url, extension) = format switch
    {
        "docx" => (downloads.Docx, "docx"),
        "markdown" => (downloads.Markdown, "md"),
        "latex" => (downloads.Latex, "tex"),
        _ => (downloads.Pdf, "pdf"),
    };

    var outputPath = Path.Combine(options.DownloadDirectory, $"{Path.GetFileNameWithoutExtension(options.InputFile)}-result.{extension}");

    using var response = await http.GetAsync(url, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        // Most likely an export format your plan doesn't have enabled (403 export_disabled) - report
        // it and keep going with the remaining formats rather than aborting the whole run.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Console.Write($"  {format,-10} ");
        PrintApiError(response, body, jsonOptions);
        return false;
    }

    // Scoped so the stream is flushed and closed (via DisposeAsync) before FileInfo below reads its
    // length back off disk - otherwise buffered-but-unflushed bytes make a just-written file look
    // like 0 B.
    await using (var outputStream = File.Create(outputPath))
    {
        await response.Content.CopyToAsync(outputStream, cancellationToken);
    }

    Console.WriteLine($"  {format,-10} saved {outputPath} ({DescribeSize(new FileInfo(outputPath).Length)})");
    return true;
}

// --- A job that ended without completing ------------------------------------------------------------

static int HandleUnsuccessful(StatusResponse status)
{
    Console.Error.WriteLine($"Job did not complete: {status.Status}" + (status.ErrorMessage is null ? "" : $" - {status.ErrorMessage}"));
    Console.Error.WriteLine("No downloads are available for a job that didn't reach \"completed\".");
    return 1;
}

// --- --list: your own jobs, most recent first ---------------------------------------------------

static async Task<int> ListJobsAsync(HttpClient http, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
{
    using var response = await http.GetAsync("/api/v1/convert?page=1&pageSize=20", cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        PrintApiError(response, body, jsonOptions);
        return 1;
    }

    var list = JsonSerializer.Deserialize<ListResponse>(body, jsonOptions)!;
    if (list.Jobs.Count == 0)
    {
        Console.WriteLine("No jobs yet.");
        return 0;
    }

    Console.WriteLine($"{list.TotalCount} job(s) total (showing page {list.Page}, up to {list.PageSize} per page):");
    foreach (var job in list.Jobs)
    {
        var queueNote = job.Status == "queued" ? $", {job.QueuePosition} ahead of it" : "";
        Console.WriteLine($"  {job.JobId}  {job.Status,-10} {job.CompletedPages}/{job.TotalPages} page(s){queueNote}  submitted {job.CreatedAt:u}");
    }
    return 0;
}

// --- --cancel <jobId>: a still-queued/processing job, refunding its quota in full ----------------

static async Task<int> CancelJobAsync(HttpClient http, JsonSerializerOptions jsonOptions, Guid jobId, CancellationToken cancellationToken)
{
    using var response = await http.PostAsync($"/api/v1/convert/{jobId}/cancel", null, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        // The most likely failure here is a 400 already_finished - the job completed/failed/was
        // already cancelled before this request reached it. PrintApiError below shows that either
        // way, no special-casing needed.
        PrintApiError(response, body, jsonOptions);
        return 1;
    }

    var status = JsonSerializer.Deserialize<StatusResponse>(body, jsonOptions)!;
    Console.WriteLine($"Job {status.JobId} is now {status.Status} - its full page quota has been refunded.");
    return 0;
}

// --- Configuration -----------------------------------------------------------------------------------

// Reads the "Scriveno" section of appsettings.json (required) and appsettings.Local.json (optional,
// gitignored - see README.md), merging field-by-field so a Local file only needs to carry the values
// it's actually overriding. Hand-rolled rather than Microsoft.Extensions.Configuration - this sample
// stays true to "no third-party packages": System.Text.Json's JsonDocument is already in the SDK and
// this app's config shape is flat enough that a real dependency isn't worth adding just for this.
static ScrivenoOptions? LoadScrivenoOptions(string baseDir)
{
    var basePath = Path.Combine(baseDir, "appsettings.json");
    if (!File.Exists(basePath))
    {
        return null;
    }

    var options = ReadScrivenoSection(basePath) ?? new ScrivenoOptions();

    var localPath = Path.Combine(baseDir, "appsettings.Local.json");
    if (File.Exists(localPath) && ReadScrivenoElement(localPath) is { } localScriveno)
    {
        if (localScriveno.TryGetProperty("ApiKey", out var v)) options = options with { ApiKey = v.GetString() ?? options.ApiKey };
        if (localScriveno.TryGetProperty("BaseUrl", out v)) options = options with { BaseUrl = v.GetString() ?? options.BaseUrl };
        if (localScriveno.TryGetProperty("InputFile", out v)) options = options with { InputFile = v.GetString() ?? options.InputFile };
        if (localScriveno.TryGetProperty("Formats", out v))
        {
            options = options with { Formats = v.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() };
        }
        if (localScriveno.TryGetProperty("DownloadDirectory", out v)) options = options with { DownloadDirectory = v.GetString() ?? options.DownloadDirectory };
    }

    return options;
}

static ScrivenoOptions? ReadScrivenoSection(string path)
{
    if (ReadScrivenoElement(path) is not { } scriveno)
    {
        return null;
    }

    return new ScrivenoOptions
    {
        ApiKey = scriveno.TryGetProperty("ApiKey", out var apiKey) ? apiKey.GetString() ?? "" : "",
        BaseUrl = scriveno.TryGetProperty("BaseUrl", out var baseUrl) ? baseUrl.GetString() ?? "" : "",
        InputFile = scriveno.TryGetProperty("InputFile", out var inputFile) ? inputFile.GetString() ?? "" : "",
        Formats = scriveno.TryGetProperty("Formats", out var formats)
            ? formats.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : [],
        DownloadDirectory = scriveno.TryGetProperty("DownloadDirectory", out var dir) ? dir.GetString() ?? "" : "",
    };
}

static JsonElement? ReadScrivenoElement(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    // Cloned so the value survives past `doc`'s disposal at the end of this using block - JsonElement
    // values from a JsonDocument are only valid while that document is alive otherwise.
    return doc.RootElement.TryGetProperty("Scriveno", out var scriveno) ? scriveno.Clone() : null;
}

// requireJobFields gates InputFile/Formats/DownloadDirectory - ApiKey/BaseUrl are always required
// (every mode needs a real API key and endpoint), but --list/--cancel have no use for the rest.
static bool TryValidate(ScrivenoOptions? options, bool requireJobFields, out string error, out ScrivenoOptions normalized)
{
    string[] knownFormats = ["pdf", "docx", "markdown", "latex"];
    normalized = null!;
    if (options is null)
    {
        error = "The \"Scriveno\" section is missing from appsettings.json.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(options.ApiKey) || options.ApiKey.Contains("YOUR_API_KEY", StringComparison.OrdinalIgnoreCase))
    {
        error = "Set Scriveno:ApiKey to a real key from https://scriveno.com/developer.";
        return false;
    }

    if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
    {
        error = $"Scriveno:BaseUrl (\"{options.BaseUrl}\") isn't a valid absolute URL.";
        return false;
    }

    if (!requireJobFields)
    {
        error = "";
        normalized = options;
        return true;
    }

    if (string.IsNullOrWhiteSpace(options.InputFile))
    {
        error = "Set Scriveno:InputFile to the path of a PDF, JPG, or PNG to convert.";
        return false;
    }

    var resolvedInputFile = Path.GetFullPath(options.InputFile);
    if (!File.Exists(resolvedInputFile))
    {
        error = $"Scriveno:InputFile (\"{resolvedInputFile}\") does not exist.";
        return false;
    }

    var formats = (options.Formats is { Length: > 0 } ? options.Formats : ["pdf"])
        .Select(f => f.Trim().ToLowerInvariant()).ToArray();
    var unknown = formats.Except(knownFormats).ToArray();
    if (unknown.Length > 0)
    {
        error = $"Scriveno:Formats has unrecognized value(s) [{string.Join(", ", unknown)}] - use pdf, docx, markdown, and/or latex.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(options.DownloadDirectory))
    {
        error = "Set Scriveno:DownloadDirectory to a folder path (created automatically if it doesn't exist).";
        return false;
    }

    error = "";
    normalized = options with { InputFile = resolvedInputFile, Formats = formats, DownloadDirectory = Path.GetFullPath(options.DownloadDirectory) };
    return true;
}

// --- Console output helpers --------------------------------------------------------------------------

static void PrintHeader()
{
    Console.WriteLine("Scriveno API - Job Runner");
    Console.WriteLine("==========================");
}

static void PrintConfigSummary(ScrivenoOptions options)
{
    var fileInfo = new FileInfo(options.InputFile);
    Console.WriteLine($"  Base URL      : {options.BaseUrl}");
    Console.WriteLine($"  API key       : {MaskSecret(options.ApiKey)}");
    Console.WriteLine($"  Input file    : {fileInfo.Name} ({DescribeSize(fileInfo.Length)})");
    Console.WriteLine($"  Download to   : {options.DownloadDirectory}");
    Console.WriteLine($"  Format(s)     : {string.Join(", ", options.Formats)}");
}

// Shows just enough of the key to confirm "yes, that's the right one" in the printed summary,
// without echoing a live credential to the terminal (and, by extension, to a screen recording or a
// terminal scrollback someone else might see).
static string MaskSecret(string key) =>
    key.Length <= 12 ? new string('*', key.Length) : $"{key[..8]}{new string('*', 8)}{key[^4..]}";

static string DescribeSize(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
    _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
};

static void PrintApiError(HttpResponseMessage response, string body, JsonSerializerOptions jsonOptions)
{
    try
    {
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(body, jsonOptions);
        Console.Error.WriteLine($"API error ({(int)response.StatusCode} {error?.ErrorCode}): {error?.Error}");
    }
    catch (JsonException)
    {
        Console.Error.WriteLine($"API error ({(int)response.StatusCode}): {body}");
    }
}

// --- Types -------------------------------------------------------------------------------------------

// Bound from the "Scriveno" section of appsettings.json/appsettings.Local.json. Formats/InputFile/
// DownloadDirectory are re-assigned (via `with`) to their normalized/resolved form in TryValidate -
// everything downstream reads only the normalized copy.
record ScrivenoOptions
{
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string InputFile { get; init; } = "";
    public string[] Formats { get; init; } = [];
    public string DownloadDirectory { get; init; } = "";
}

// Response shapes, trimmed to the fields this sample actually reads - see the full contract at
// {BaseUrl}/swagger.
record SubmitResponse(Guid JobId, string Status, int TotalPages, int QueuePosition);
record StatusResponse(
    Guid JobId, string Status, int TotalPages, int CompletedPages, string? ErrorMessage,
    DownloadUrls? Downloads, int QueuePosition, DateTimeOffset CreatedAt);
record ListResponse(List<StatusResponse> Jobs, int Page, int PageSize, int TotalCount);
record DownloadUrls(string Pdf, string Docx, string Markdown, string Latex);
record ApiErrorResponse(string Error, string ErrorCode);
