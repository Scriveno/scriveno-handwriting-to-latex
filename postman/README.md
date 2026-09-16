# Scriveno API - Postman collection

A ready-to-import Postman collection covering the full public v1 API: submit a file, poll for
status, list your jobs, cancel one, and download results in all four formats.

## Import it

1. In Postman: **Import** -> select `Scriveno-Developer-API.postman_collection.json`.
2. Open the collection's **Variables** tab and set `apiKey` to your Developer API key
   (`isk_live_...`) from your [Developer page](https://scriveno.com/developer) - requires
   Developer access on your plan. Every request already inherits it via collection-level Bearer
   auth, so this is the only thing you must set to get started.
3. Run **Submit a file** with a real PDF/JPG/PNG attached (its `formdata` `file` field is empty by
   default - use Postman's file picker to choose one). Its test script automatically saves the
   returned job id into the `jobId` collection variable, so every other request in the collection
   already works with no further editing.

## Variables

| Variable  | Default                     | Purpose                                                      |
|-----------|------------------------------|---------------------------------------------------------------|
| `baseUrl` | `https://api.scriveno.com`  | Point at a different environment (e.g. your own staging) while testing. |
| `apiKey`  | *(empty - you set this)*    | Your Developer API key. Required.                             |
| `jobId`   | *(empty - auto-filled)*     | Set automatically after "Submit a file"; edit by hand to act on a different job. |

## Where to go from here

- Full API reference, including every error code: `{baseUrl}/swagger`.
- Prefer a runnable script instead? See `samples/dotnet-quickstart` for a minimal .NET console app
  covering the same calls.
- For production use, prefer the `webhookUrl` form field on submit over polling - see the "Submit a
  file" request's description, and the Developer page in your account for the payload shape and
  signature verification.
