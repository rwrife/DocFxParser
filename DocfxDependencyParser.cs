using HtmlAgilityPack;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DocFxParser;

/// <summary>
/// Callback interface for file access operations.
/// Implement this to control how the parser accesses files.
/// </summary>
public interface IFileAccessCallback
{
    /// <summary>
    /// Called before a file is read. Return true to allow reading, false to skip.
    /// </summary>
    /// <param name="filePath">The full path to the file that will be read</param>
    /// <param name="purpose">The purpose of the file read (e.g., "config", "content", "toc", "markdown", etc.)</param>
    /// <returns>True to allow the file to be read, false to skip it</returns>
    bool OnBeforeFileRead(string filePath, string purpose);

    /// <summary>
    /// Called after a file has been successfully read.
    /// </summary>
    /// <param name="filePath">The full path to the file that was read</param>
    /// <param name="purpose">The purpose of the file read</param>
    void OnAfterFileRead(string filePath, string purpose);

    /// <summary>
    /// Called when a file read fails.
    /// </summary>
    /// <param name="filePath">The full path to the file that failed to read</param>
    /// <param name="purpose">The purpose of the file read</param>
    /// <param name="exception">The exception that occurred</param>
    void OnFileReadError(string filePath, string purpose, Exception exception);
}

/// <summary>
/// Default implementation that allows all file operations.
/// </summary>
public class DefaultFileAccessCallback : IFileAccessCallback
{
    public bool OnBeforeFileRead(string filePath, string purpose) => true;
    public void OnAfterFileRead(string filePath, string purpose) { }
    public void OnFileReadError(string filePath, string purpose, Exception exception) { }
}

public class DocfxDependencyParser
{
    private static readonly string[] MarkdownExtensions = new[] { ".md" };
    private static readonly string[] HtmlExtensions = new[] { ".html", ".htm" };
    private static readonly string[] YamlExtensions = new[] { ".yaml", ".yml" };
    private static readonly string[] TocFileNames = new[] { "toc.yml", "toc.yaml", "toc.md" };

    private readonly MarkdownPipeline _markdownPipeline;
    private readonly IFileAccessCallback _fileAccessCallback;

    public DocfxDependencyParser() : this(new DefaultFileAccessCallback())
    {
    }

    public DocfxDependencyParser(IFileAccessCallback fileAccessCallback)
    {
        _markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        _fileAccessCallback = fileAccessCallback ?? throw new ArgumentNullException(nameof(fileAccessCallback));
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
                foreach (var match in ExpandMapping(mapping, configDirectory, config.Build?.Exclude))
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
                    if (FileExistsWithCallback(candidate, sourceType))
                    {
                        RegisterFile(candidate, sourceType);
                    }
                    else if (Directory.Exists(candidate))
                    {
                        foreach (var file in Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories))
                        {
                            if (_fileAccessCallback.OnBeforeFileRead(file, sourceType))
                            {
                                RegisterFile(file, sourceType);
                            }
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
            var fileName = Path.GetFileName(fullPath);

            if (IsTocFile(fileName))
            {
                dependency.References = ExtractTocReferences(fullPath, configDirectory, knownFiles);
            }
            else if (MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                dependency.References = ExtractMarkdownReferences(fullPath, configDirectory, knownFiles);
            }
            else if (HtmlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                var htmlContent = ReadFileWithCallback(fullPath, "html");
                if (htmlContent != null)
                {
                    dependency.References = ExtractHtmlReferences(htmlContent, Path.GetDirectoryName(fullPath)!, configDirectory, knownFiles);
                }
            }
        }

        return new DocfxDependencyResult
        {
            Files = files.Values
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    /// <summary>
    /// Safely read a file with callback notification.
    /// </summary>
    private string? ReadFileWithCallback(string filePath, string purpose)
    {
        if (!_fileAccessCallback.OnBeforeFileRead(filePath, purpose))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(filePath);
            _fileAccessCallback.OnAfterFileRead(filePath, purpose);
            return content;
        }
        catch (Exception ex)
        {
            _fileAccessCallback.OnFileReadError(filePath, purpose, ex);
            throw;
        }
    }

    /// <summary>
    /// Check if a file exists and notify callback before accessing.
    /// </summary>
    private bool FileExistsWithCallback(string filePath, string purpose)
    {
        if (!_fileAccessCallback.OnBeforeFileRead(filePath, purpose))
        {
            return false;
        }

        return File.Exists(filePath);
    }

    private DocfxConfig LoadConfiguration(string configPath)
    {
        if (!_fileAccessCallback.OnBeforeFileRead(configPath, "config"))
        {
            throw new InvalidOperationException($"File access denied by callback: {configPath}");
        }

        try
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

            _fileAccessCallback.OnAfterFileRead(configPath, "config");
            return config;
        }
        catch (Exception ex)
        {
            _fileAccessCallback.OnFileReadError(configPath, "config", ex);
            throw;
        }
    }

    private static IEnumerable<string> ExpandMapping(FileMappingItem mapping, string configDirectory, List<string>? globalExclude = null)
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
            // Expand brace patterns like **/*.{md,yml} into separate patterns
            foreach (var expanded in ExpandBracePattern(include))
            {
                matcher.AddInclude(expanded);
            }
        }

        // Apply global exclude rules first
        if (globalExclude != null)
        {
            foreach (var exclude in globalExclude)
            {
                matcher.AddExclude(exclude);
            }
        }

        // Apply mapping-specific exclude rules
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

    /// <summary>
    /// Expands brace patterns like **/*.{md,yml} into separate patterns: **/*.md, **/*.yml
    /// </summary>
    private static IEnumerable<string> ExpandBracePattern(string pattern)
    {
        var openBrace = pattern.IndexOf('{');
        var closeBrace = pattern.IndexOf('}');

        // No brace pattern, return as-is
        if (openBrace == -1 || closeBrace == -1 || closeBrace <= openBrace)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern.Substring(0, openBrace);
        var suffix = pattern.Substring(closeBrace + 1);
        var options = pattern.Substring(openBrace + 1, closeBrace - openBrace - 1);

        foreach (var option in options.Split(','))
        {
            var expanded = prefix + option.Trim() + suffix;
            // Recursively expand in case there are nested braces
            foreach (var result in ExpandBracePattern(expanded))
            {
                yield return result;
            }
        }
    }

    private List<string> ExtractMarkdownReferences(string markdownPath, string docsetRoot, IDictionary<string, FileDependency> knownFiles)
    {
        var content = ReadFileWithCallback(markdownPath, "markdown");
        if (content == null)
        {
            return new List<string>();
        }

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

    private static bool IsTocFile(string fileName)
    {
        return TocFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    private List<string> ExtractTocReferences(string tocPath, string docsetRoot, IDictionary<string, FileDependency> knownFiles)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseDirectory = Path.GetDirectoryName(tocPath)!;
        var fileName = Path.GetFileName(tocPath);

        if (fileName.Equals("toc.md", StringComparison.OrdinalIgnoreCase))
        {
            // Parse markdown TOC
            var content = ReadFileWithCallback(tocPath, "toc");
            if (content != null)
            {
                var document = Markdig.Markdown.Parse(content, _markdownPipeline);

                foreach (var link in document.Descendants<LinkInline>())
                {
                    if (string.IsNullOrEmpty(link.Url))
                    {
                        continue;
                    }

                    ProcessTocReference(link.Url, baseDirectory, docsetRoot, knownFiles, references);
                }
            }
        }
        else if (fileName.Equals("toc.yml", StringComparison.OrdinalIgnoreCase) || 
                 fileName.Equals("toc.yaml", StringComparison.OrdinalIgnoreCase))
        {
            // Parse YAML TOC
            try
            {
                var yaml = ReadFileWithCallback(tocPath, "toc");
                if (yaml != null)
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();

                    var tocItems = deserializer.Deserialize<List<TocItem>>(yaml);
                    if (tocItems != null)
                    {
                        ProcessTocItems(tocItems, baseDirectory, docsetRoot, knownFiles, references);
                    }
                }
            }
            catch (Exception)
            {
                // If YAML parsing fails, skip this file
            }
        }

        return references
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ProcessTocItems(List<TocItem> items, string baseDirectory, string docsetRoot, IDictionary<string, FileDependency> knownFiles, HashSet<string> references)
    {
        foreach (var item in items)
        {
            // Process href (can be a file or another toc)
            if (!string.IsNullOrEmpty(item.Href))
            {
                ProcessTocReference(item.Href, baseDirectory, docsetRoot, knownFiles, references);
            }

            // Process topicHref (the landing page for a folder with a toc)
            if (!string.IsNullOrEmpty(item.TopicHref))
            {
                ProcessTocReference(item.TopicHref, baseDirectory, docsetRoot, knownFiles, references);
            }

            // Process homepage
            if (!string.IsNullOrEmpty(item.Homepage))
            {
                ProcessTocReference(item.Homepage, baseDirectory, docsetRoot, knownFiles, references);
            }

            // Recursively process child items
            if (item.Items != null && item.Items.Count > 0)
            {
                ProcessTocItems(item.Items, baseDirectory, docsetRoot, knownFiles, references);
            }
        }
    }

    private void ProcessTocReference(string href, string baseDirectory, string docsetRoot, IDictionary<string, FileDependency> knownFiles, HashSet<string> references)
    {
        var sanitized = SanitizeUrl(href);
        if (string.IsNullOrEmpty(sanitized))
        {
            return;
        }

        // First, try to resolve as a direct file reference
        foreach (var resolved in ResolveLinkTargets(sanitized, baseDirectory, docsetRoot, knownFiles))
        {
            references.Add(resolved);
        }

        // If the href is a directory reference, look for toc files in that directory
        var candidatePaths = new List<string>();
        
        if (sanitized.StartsWith("~/", StringComparison.Ordinal))
        {
            var relative = sanitized.Substring(2).TrimEnd('/');
            candidatePaths.Add(Path.GetFullPath(Path.Combine(docsetRoot, NormalizeForPath(relative))));
        }
        else if (sanitized.StartsWith("/", StringComparison.Ordinal))
        {
            var relative = sanitized.TrimStart('/').TrimEnd('/');
            candidatePaths.Add(Path.GetFullPath(Path.Combine(docsetRoot, NormalizeForPath(relative))));
        }
        else if (Path.IsPathRooted(sanitized))
        {
            candidatePaths.Add(Path.GetFullPath(NormalizeForPath(sanitized)));
        }
        else
        {
            var relative = NormalizeForPath(sanitized).TrimEnd(Path.DirectorySeparatorChar);
            candidatePaths.Add(Path.GetFullPath(Path.Combine(baseDirectory, relative)));
        }

        // Check if any candidate is a directory and look for TOC files
        foreach (var candidatePath in candidatePaths)
        {
            if (Directory.Exists(candidatePath))
            {
                foreach (var tocFileName in TocFileNames)
                {
                    var tocPath = Path.Combine(candidatePath, tocFileName);
                    var normalized = NormalizeFullPath(tocPath);
                    
                    if (knownFiles.TryGetValue(normalized, out var dependency))
                    {
                        references.Add(dependency.Path);
                    }
                }
            }
        }
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
        var fileName = Path.GetFileName(path);
        if (IsTocFile(fileName))
        {
            return "toc";
        }

        var extension = Path.GetExtension(path);
        if (MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return "markdown";
        }

        if (HtmlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return "html";
        }

        if (YamlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
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
