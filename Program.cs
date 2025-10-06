using DocFxParser;
using Newtonsoft.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: DocFxParser <path-to-docfx.json-or-directory>");
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
var docfxConfigPath = inputPath;

// If the path is a directory, append docfx.json
if (Directory.Exists(inputPath))
{
    docfxConfigPath = Path.Combine(inputPath, "docfx.json");
}

if (!File.Exists(docfxConfigPath))
{
    Console.Error.WriteLine($"docfx.json not found at '{docfxConfigPath}'");
    return 1;
}

try
{
    var parser = new DocfxDependencyParser();
    var result = parser.Collect(docfxConfigPath);
    var json = JsonConvert.SerializeObject(result, Formatting.Indented);
    Console.WriteLine(json);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to parse docfx configuration: {ex.Message}");
    return 2;
}
