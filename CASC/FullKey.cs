using System.Text.Json;
using System.Text.Json.Serialization;
using BattleNGDP.Core;

namespace BattleNGDP.CASC;

[JsonConverter(typeof(FullKeyJsonConverter))]
public readonly struct FullKey
{
    public readonly byte[] Data;

    public FullKey(byte[] data)
    {
        if (data.Length != 0x10) {
            throw new ArgumentException("Invalid length of input data", nameof(data));
        }

        Data = data;
    }

    public override string ToString() =>
        Convert.ToHexString(Data).ToLowerInvariant();

    public static FullKey FromString(string keyStr) =>
        new(Convert.FromHexString(keyStr));

    public static FullKey FromString(ReadOnlySpan<char> keyStr) =>
        new(Convert.FromHexString(keyStr));

    public ReadOnlySpan<byte> AsSpan() =>
        Data.AsSpan();

    public class FullKeyJsonConverter : JsonConverter<FullKey>
    {
        public override FullKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) {
                ReadOnlySpan<char> chars = reader.GetString();
                return FromString(chars);
            }

            if (reader.TokenType == JsonTokenType.StartObject) {
                while (reader.TokenType != JsonTokenType.EndObject) {
                    reader.Read();
                }

                return new FullKey();
            }

            if (reader.TokenType == JsonTokenType.Null) {
                return new FullKey();
            }

            throw new JsonException($"Expected string token for FullKey, but found {reader.TokenType}.");
        }

        public override void Write(Utf8JsonWriter writer, FullKey value, JsonSerializerOptions options)
        {
            if (value.Data == null) {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(value.ToString());
        }
    }
}