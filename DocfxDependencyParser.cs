using System.Linq;
using HtmlAgilityPack;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Newtonsoft.Json;

namespace DocFxParser;

public class DocfxDependencyParser
{
    private static readonly string[] MarkdownExtensions = new[] { ".md", ".markdown", ".mdown", ".mkd" };
    private static readonly string[] HtmlExtensions = new[] { ".html", ".htm" };

    private readonly MarkdownPipeline _markdownPipeline;

    public DocfxDependencyParser()
    {
        _markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    }

    public DocfxDependencyResult Collect(string docfxConfigPath)
    {
        if (string.IsNullOrWhiteSpace(docfxConfigPath))
        {
            throw new ArgumentException("Configuration path cannot be null or empty", nameof(docfxConfigPath));
        }

        var fullConfigPath = Path.GetFullPath(docfxConfigPath);
        if (!File.Exists(fullConfigPath))
        {
            throw new FileNotFoundException("docfx.json not found", fullConfigPath);
        }

        var configDirectory = Path.GetDirectoryName(fullConfigPath)!;
        var config = LoadConfiguration(fullConfigPath);

        var files = new Dictionary<string, FileDependency>(StringComparer.OrdinalIgnoreCase);

        void RegisterFile(string fullPath, string sourceType)
        {
            var normalizedPath = NormalizeFullPath(fullPath);
            if (!files.TryGetValue(normalizedPath, out var dependency))
            {
                dependency = new FileDependency
                {
                    Path = ToOutputPath(configDirectory, normalizedPath),
                    FileType = DetermineFileType(normalizedPath)
                };
                files.Add(normalizedPath, dependency);
            }

            if (!dependency.SourceTypes.Any(st => st.Equals(sourceType, StringComparison.OrdinalIgnoreCase)))
            {
                dependency.SourceTypes.Add(sourceType);
            }
        }

        void CollectFromMapping(IEnumerable<FileMappingItem>? mappings, string sourceType)
        {
            if (mappings == null)
            {
                return;
            }

            foreach (var mapping in mappings)
            {
                foreach (var match in ExpandMapping(mapping, configDirectory))
                {
                    RegisterFile(match, sourceType);
                }
            }
        }

        CollectFromMapping(config.Build?.Content, "content");
        CollectFromMapping(config.Build?.Resource, "resource");
        CollectFromMapping(config.Build?.Overwrite, "overwrite");

        CollectAdditionalFiles(config.Build?.GlobalMetadataFiles, "globalMetadata");
        CollectAdditionalFiles(config.Build?.FileMetadataFiles, "fileMetadata");
        CollectAdditionalFiles(config.Build?.Template, "template");

        void CollectAdditionalFiles(IEnumerable<string>? entries, string sourceType)
        {
            if (entries == null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                foreach (var candidate in ExpandAdditionalEntry(entry, configDirectory))
                {
                    if (File.Exists(candidate))
                    {
                        RegisterFile(candidate, sourceType);
                    }
                    else if (Directory.Exists(candidate))
                    {
                        foreach (var file in Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories))
                        {
                            RegisterFile(file, sourceType);
                        }
                    }
                }
            }
        }

        var knownFiles = new Dictionary<string, FileDependency>(files, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in knownFiles)
        {
            var fullPath = kvp.Key;
            var dependency = kvp.Value;
            var extension = Path.GetExtension(fullPath);

            if (MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                dependency.References = ExtractMarkdownReferences(fullPath, configDirectory, knownFiles);
            }
            else if (HtmlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                dependency.References = ExtractHtmlReferences(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath)!, configDirectory, knownFiles);
            }
        }

        return new DocfxDependencyResult
        {
            Files = files.Values
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static DocfxConfig LoadConfiguration(string configPath)
    {
        using var stream = File.OpenRead(configPath);
        using var reader = new StreamReader(stream);
        using var jsonReader = new JsonTextReader(reader);
        var serializer = new JsonSerializer();
        var config = serializer.Deserialize<DocfxConfig>(jsonReader);
        if (config == null)
        {
            throw new InvalidOperationException("The docfx.json file could not be deserialized.");
        }

        return config;
    }

    private static IEnumerable<string> ExpandMapping(FileMappingItem mapping, string configDirectory)
    {
        var includes = mapping.Files ?? new List<string>();
        if (includes.Count == 0)
        {
            yield break;
        }

        var src = mapping.Src;
        var baseDirectory = string.IsNullOrWhiteSpace(src)
            ? configDirectory
            : Path.GetFullPath(Path.Combine(configDirectory, src));

        if (!Directory.Exists(baseDirectory))
        {
            yield break;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var include in includes)
        {
            matcher.AddInclude(include);
        }

        if (mapping.Exclude != null)
        {
            foreach (var exclude in mapping.Exclude)
            {
                matcher.AddExclude(exclude);
            }
        }

        var directoryInfo = new DirectoryInfo(baseDirectory);
        if (!directoryInfo.Exists)
        {
            yield break;
        }

        var result = matcher.Execute(new DirectoryInfoWrapper(directoryInfo));
        foreach (var file in result.Files)
        {
            var combined = Path.Combine(baseDirectory, file.Path);
            yield return Path.GetFullPath(combined);
        }
    }

    private static IEnumerable<string> ExpandAdditionalEntry(string entry, string configDirectory)
    {
        var trimmed = entry.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            yield break;
        }

        var relativeCandidate = trimmed.StartsWith("~/", StringComparison.Ordinal)
            ? trimmed.Substring(2)
            : trimmed;

        var normalized = NormalizeForPath(relativeCandidate);

        if (ContainsGlobCharacters(normalized))
        {
            var directoryInfo = new DirectoryInfo(configDirectory);
            if (!directoryInfo.Exists)
            {
                yield break;
            }

            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(normalized);
            var result = matcher.Execute(new DirectoryInfoWrapper(directoryInfo));
            foreach (var file in result.Files)
            {
                yield return Path.GetFullPath(Path.Combine(configDirectory, file.Path));
            }

            yield break;
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            yield return Path.GetFullPath(Path.Combine(configDirectory, normalized));
            yield break;
        }

        if (Path.IsPathRooted(trimmed))
        {
            yield return Path.GetFullPath(NormalizeForPath(trimmed));
            yield break;
        }

        yield return Path.GetFullPath(Path.Combine(configDirectory, normalized));
    }

    private static bool ContainsGlobCharacters(string value)
    {
        return value.IndexOfAny(new[] { '*', '?', '[', ']' }) >= 0;
    }

    private List<string> ExtractMarkdownReferences(string markdownPath, string docsetRoot, IDictionary<string, FileDependency> knownFiles)
    {
        var content = File.ReadAllText(markdownPath);
        var document = Markdig.Markdown.Parse(content, _markdownPipeline);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseDirectory = Path.GetDirectoryName(markdownPath)!;

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (string.IsNullOrEmpty(link.Url))
            {
                continue;
            }

            foreach (var resolved in ResolveLinkTargets(link.Url, baseDirectory, docsetRoot, knownFiles))
            {
                references.Add(resolved);
            }
        }

        var html = Markdig.Markdown.ToHtml(content, _markdownPipeline);
        references.UnionWith(ExtractHtmlReferences(html, baseDirectory, docsetRoot, knownFiles));

        return references
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> ExtractHtmlReferences(string htmlContent, string baseDirectory, string docsetRoot, IDictionary<string, FileDependency> knownFiles)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var document = new HtmlDocument();
        document.LoadHtml(htmlContent);

        var nodes = document.DocumentNode.SelectNodes("//*[@href or @src]");
        if (nodes == null)
        {
            return new List<string>();
        }

        foreach (var node in nodes)
        {
            if (node.Attributes["href"] != null)
            {
                foreach (var resolved in ResolveLinkTargets(node.Attributes["href"].Value, baseDirectory, docsetRoot, knownFiles))
                {
                    references.Add(resolved);
                }
            }

            if (node.Attributes["src"] != null)
            {
                foreach (var resolved in ResolveLinkTargets(node.Attributes["src"].Value, baseDirectory, docsetRoot, knownFiles))
                {
                    references.Add(resolved);
                }
            }
        }

        return references
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> ResolveLinkTargets(string url, string baseDirectory, string docsetRoot, IDictionary<string, FileDependency> knownFiles)
    {
        var sanitized = SanitizeUrl(url);
        if (string.IsNullOrEmpty(sanitized))
        {
            yield break;
        }

        if (Uri.TryCreate(sanitized, UriKind.Absolute, out var absoluteUri) && !string.Equals(absoluteUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var candidatePaths = new List<string>();

        if (sanitized.StartsWith("~/", StringComparison.Ordinal))
        {
            var relative = sanitized.Substring(2);
            candidatePaths.Add(Path.GetFullPath(Path.Combine(docsetRoot, NormalizeForPath(relative))));
        }
        else if (sanitized.StartsWith("/", StringComparison.Ordinal))
        {
            candidatePaths.Add(Path.GetFullPath(Path.Combine(docsetRoot, NormalizeForPath(sanitized.TrimStart('/')))));
        }
        else if (Path.IsPathRooted(sanitized))
        {
            candidatePaths.Add(Path.GetFullPath(NormalizeForPath(sanitized)));
        }
        else
        {
            var relative = NormalizeForPath(sanitized);
            candidatePaths.Add(Path.GetFullPath(Path.Combine(baseDirectory, relative)));
            candidatePaths.Add(Path.GetFullPath(Path.Combine(docsetRoot, relative)));
        }

        foreach (var candidate in candidatePaths)
        {
            var normalized = NormalizeFullPath(candidate);
            if (knownFiles.TryGetValue(normalized, out var dependency))
            {
                yield return dependency.Path;
            }
        }
    }

    private static string SanitizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("xref:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var withoutAnchor = trimmed.Split('#')[0];
        var withoutQuery = withoutAnchor.Split('?')[0];

        return Uri.UnescapeDataString(withoutQuery);
    }

    private static string DetermineFileType(string path)
    {
        var extension = Path.GetExtension(path);
        if (MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return "markdown";
        }

        if (HtmlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return "html";
        }

        if (extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return "yaml";
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return "json";
        }

        return "resource";
    }

    private static string NormalizeFullPath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static string NormalizeForPath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ToOutputPath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
        return normalized;
    }
}
