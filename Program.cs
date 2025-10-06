using DocFxParser;
using Newtonsoft.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: DocFxParser <path-to-docfx.json>");
    return 1;
}

var docfxConfigPath = Path.GetFullPath(args[0]);
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
