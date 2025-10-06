# DocFxParser

Utility for parsing `docfx.json` files to determine which files are required to build the documentation set.

## Features

- Parses `docfx.json` using Newtonsoft.Json just like DocFX.
- Resolves file mappings defined in the `build` section (`content`, `resource`, and `overwrite`) with the `Microsoft.Extensions.FileSystemGlobbing` matcher used by DocFX.
- Reads Markdown files with the Markdig pipeline to capture Markdown and embedded HTML links.
- Collects HTML style references (e.g., `<img src="...">`, `<a href="...">`) with HtmlAgilityPack.
- Produces a JSON report describing every matched file, the source type(s) that include it, its inferred file type, and the local references made inside Markdown/HTML files.

## Usage

```bash
# Assuming dotnet CLI is available
dotnet run -- ./path/to/docfx.json
```

The tool prints a JSON document similar to the following:

```json
{
  "files": [
    {
      "path": "articles/getting-started.md",
      "fileType": "markdown",
      "sourceTypes": ["content"],
      "references": [
        "images/overview.png",
        "includes/snippet.md"
      ]
    },
    {
      "path": "styles/site.css",
      "fileType": "resource",
      "sourceTypes": ["resource"],
      "references": []
    }
  ]
}
```

Paths are reported relative to the directory containing `docfx.json`. Files that live outside of that directory (because of the `src` setting) will appear with `../` segments.

## Requirements

- .NET 7 SDK (or newer) to build and run the console application.
- File system access to the directories referenced by the `src` values inside `docfx.json`.
- Network access is **not** required; all file resolution happens locally.

## Notes

The parser only reports references that resolve to files declared in the `docfx.json` file mappings. External links (HTTP/HTTPS), anchors, `mailto:` links, and `xref:` references are ignored.
