# Scriveno API quickstart (.NET)

A .NET 9 console app covering the calls that make up the Scriveno developer API: submit a file,
poll for its status, download the result, list your jobs, cancel one. No third-party NuGet
packages - just `HttpClient` and `System.Text.Json` from the SDK, including for reading
`appsettings.json` itself. Needs only the .NET 9 SDK to build and run; if a client is on .NET 8
(LTS) instead, changing `<TargetFramework>` in `ScrivenoQuickstart.csproj` to `net8.0` is the only
change required - nothing in the code is 9-specific.

- **Config lives in `appsettings.json`**, not environment variables - your API key, base URL,
  input file, download formats, and download folder are all there, ready to edit in place like any
  other .NET app's config.
- **No CLI flags for the stages of a single job.** Run it with no arguments, and it shows you
  exactly what it's about to do, waits for you to confirm, then drives the whole submit -> poll ->
  download pipeline itself, printing live status as the job progresses. `--list` and `--cancel` are
  still there as separate utility modes (see below) - they're not stages of that one job.

## Get an API key

1. Sign in at [scriveno.com](https://scriveno.com) with an account that has Developer access.
2. Go to **Developer** and create a key. Copy it right away - it's only shown once.

## Configure it

Edit `appsettings.json`:

```json
{
  "Scriveno": {
    "ApiKey": "isk_live_YOUR_API_KEY_HERE",
    "BaseUrl": "https://api.scriveno.com",
    "InputFile": "path/to/notes.pdf",
    "Formats": [ "pdf" ],
    "DownloadDirectory": "./downloads"
  }
}
```

| Key                 | Purpose                                                                              |
|----------------------|---------------------------------------------------------------------------------------|
| `ApiKey`            | Your developer API key. Required.                                                    |
| `BaseUrl`           | `https://api.scriveno.com`, or a staging URL while testing.                          |
| `InputFile`         | Path to the PDF/JPG/PNG to convert. Relative paths resolve from wherever you run `dotnet run`. |
| `Formats`           | Which result format(s) to download once the job completes: any of `pdf`, `docx`, `markdown`, `latex`. |
| `DownloadDirectory` | Where to save the downloaded result(s). Created automatically if it doesn't exist.    |

Since this file is checked into source control, don't put a real key in it directly if you intend
to commit changes here. Instead, copy it to **`appsettings.Local.json`** (already gitignored) and
put your real values there - the app reads `appsettings.json` first and lets
`appsettings.Local.json` override it, so the tracked file can stay a safe placeholder template.

```bash
cp appsettings.json appsettings.Local.json
# edit appsettings.Local.json with your real API key, file path, etc.
```

## Run it

```bash
cd samples/dotnet-quickstart
dotnet run
```

It prints your configuration (the API key masked), waits for you to press Enter, then runs the
whole job automatically:

```
Scriveno API - Job Runner
==========================
  Base URL      : https://api.scriveno.com
  API key       : isk_live********ab12
  Input file    : notes.pdf (2.3 MB)
  Download to   : /home/you/scriveno-downloads
  Format(s)     : pdf, docx

Press Enter to start this conversion job (Ctrl+C to cancel)...

Submitting notes.pdf (2.3 MB)...
Quota: 96/100 pages remaining today.
Job 5e1c... queued - 4 page(s), 0 ahead of it.

  [00:00:08] processing  - 3/4 page(s) recognized

Job completed - 4 page(s) recognized.

Available downloads:
  pdf       https://api.scriveno.com/api/v1/convert/5e1c.../download/pdf
  docx      https://api.scriveno.com/api/v1/convert/5e1c.../download/docx
  markdown  https://api.scriveno.com/api/v1/convert/5e1c.../download/markdown
  latex     https://api.scriveno.com/api/v1/convert/5e1c.../download/latex

Saving pdf, docx to /home/you/scriveno-downloads:
  pdf        saved /home/you/scriveno-downloads/notes-result.pdf (412.6 KB)
  docx       saved /home/you/scriveno-downloads/notes-result.docx (58.1 KB)
```

The status line updates in place (via `\r`) instead of scrolling - `[elapsed] status - detail`,
where `detail` is the queue position while queued and pages recognized once processing starts.

If the job fails, or a configured format isn't available on your plan (e.g. `docx` disabled by an
admin feature flag), it says so plainly and moves on: a per-format download failure doesn't stop
the others from being saved, and a failed job explains why with no download step attempted at all.
Press Ctrl+C at any point during polling to stop watching - the job itself keeps running on
Scriveno's side regardless; note the job id printed after submit if you want to check on it later
with `--list` (below), directly via `GET /api/v1/convert/{jobId}` (see `{base URL}/swagger`), or
from the Developer page.

## List and cancel

List your own jobs, most recent first:

```bash
dotnet run -- --list
```

Cancel a still-queued/processing job (refunds its full page quota immediately):

```bash
dotnet run -- --cancel <jobId>
```

Both only need `Scriveno:ApiKey`/`Scriveno:BaseUrl` to be set - `InputFile`/`Formats`/
`DownloadDirectory` (only relevant to the default submit-and-download job) aren't required for
these two.

## Where to go from here

- Full API reference, including every error code: `{base URL}/swagger`.
- For production use, prefer the `webhookUrl` form field on submit over polling - Scriveno POSTs a
  signed notification to it when a job finishes. See the Developer page in your account for the
  payload shape and how to verify the `X-Scriveno-Signature` header.
- This sample always submits the file named by `Scriveno:InputFile` and exits after one job. For
  batch or long-running use, wrap `RunJobAsync` in your own loop/queue rather than editing
  `appsettings.json` between runs.
