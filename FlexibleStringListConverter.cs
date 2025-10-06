using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DocFxParser;

public class FlexibleStringListConverter : JsonConverter<List<string>?>
{
    public override void WriteJson(JsonWriter writer, List<string>? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        serializer.Serialize(writer, value);
    }

    public override List<string>? ReadJson(JsonReader reader, Type objectType, List<string>? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonToken.String)
        {
            var value = reader.Value?.ToString();
            return string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { value };
        }

        if (reader.TokenType == JsonToken.StartArray)
        {
            var array = JArray.Load(reader);
            return array.Values<string>().Where(v => !string.IsNullOrEmpty(v)).ToList();
        }

        throw new JsonSerializationException($"Unexpected token {reader.TokenType} when parsing string list.");
    }
}
