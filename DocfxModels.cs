using Newtonsoft.Json;

namespace DocFxParser;

public class DocfxConfig
{
    [JsonProperty("build")]
    public DocfxBuildConfig? Build { get; set; }
}

public class DocfxBuildConfig
{
    [JsonProperty("content")]
    public List<FileMappingItem>? Content { get; set; }

    [JsonProperty("resource")]
    public List<FileMappingItem>? Resource { get; set; }

    [JsonProperty("overwrite")]
    public List<FileMappingItem>? Overwrite { get; set; }

    [JsonProperty("globalMetadataFiles")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? GlobalMetadataFiles { get; set; }

    [JsonProperty("fileMetadataFiles")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? FileMetadataFiles { get; set; }

    [JsonProperty("template")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? Template { get; set; }
}

public class FileMappingItem
{
    [JsonProperty("files")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? Files { get; set; }

    [JsonProperty("exclude")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? Exclude { get; set; }

    [JsonProperty("src")]
    public string? Src { get; set; }
}

public class DocfxDependencyResult
{
    [JsonProperty("files")]
    public List<FileDependency> Files { get; set; } = new();
}

public class FileDependency
{
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    [JsonProperty("fileType")]
    public string FileType { get; set; } = string.Empty;

    [JsonProperty("sourceTypes")]
    public List<string> SourceTypes { get; set; } = new();

    [JsonProperty("references")]
    public List<string> References { get; set; } = new();
}
