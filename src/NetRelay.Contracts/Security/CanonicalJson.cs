using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;

namespace NetRelay.Contracts.Security;

public static class CanonicalJson
{
    public static string Serialize(IReadOnlyDictionary<string, object?> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                if (pair.Value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, pair.Value, pair.Value.GetType());
                }
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
