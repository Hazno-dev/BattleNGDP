using System.Text.Json;
using System.Text.Json.Serialization;

namespace BattleNGDP.BlizzTrack;

public enum BlizzTrackType
{
    Unknown,
    Version,
    CDN,
    BGDL
}

public struct BlizzTrackResult<T> where T : struct
{
    public bool Success { get; set; }
    public T Result { get; set; }
}

public struct BlizzTrackSeqns
{
    public struct BlizzTrackSeqnsMetadata
    {
        public bool Encrypted { get; set; }
        public string File { get; set; }
        public string Name { get; set; }
        public string Tact { get; set; }
    }
    
    public List<BlizzTrackSeqn> Results { get; set; }
    public uint Page { get; set; }
    public uint PerPage { get; set; }
    public uint Total { get; set; }
    public uint TotalPages { get; set; }
    public BlizzTrackSeqnsMetadata Metadata { get; set; }
}

public struct BlizzTrackSeqn : IComparable<BlizzTrackSeqn>
{
    public uint Seqn { get; set; }
    public string CreatedAt { get; set; }
    public string Tact { get; set; }
    public string Type { get; set; }
    public Dictionary<string, string> Refs { get; set; }

    public int CompareTo(BlizzTrackSeqn other)
    {
        return -Seqn.CompareTo(other.Seqn);
    }
}

public struct BlizzTrackManifest<T> : IComparable<BlizzTrackManifest<T>> where T : struct
{
    public uint Seqn { get; set; }
    public string Tact { get; set; }
    public string Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Encrypted { get; set; }
    public string Name { get; set; }
    public T[] Data { get; set; }

    public int CompareTo(BlizzTrackManifest<T> other)
    {
        return -Seqn.CompareTo(other.Seqn);
    }
}

public struct BlizzTrackVersion : IBlizzTrackManifest, IEquatable<BlizzTrackVersion>
{
    public static BlizzTrackType TrackType => BlizzTrackType.Version;
    
    public string Name  { get; set; }
    public string BuildConfig  { get; set; }
    public uint BuildId  { get; set; }
    public string CdnConfig  { get; set; }
    public string? Keyring { get; set; }
    public string Region  { get; set; }
    public string VersionName  { get; set; }
    public string? ProductConfig  { get; set; }

    public static bool operator ==(BlizzTrackVersion v1, BlizzTrackVersion v2) => v1.Equals(v2);
    public static bool operator !=(BlizzTrackVersion v1, BlizzTrackVersion v2) => !v1.Equals(v2);
    
    public bool Equals(BlizzTrackVersion other)
    {
        return BuildConfig == other.BuildConfig && BuildId == other.BuildId && CdnConfig == other.CdnConfig && ProductConfig == other.ProductConfig;
    }

    public override bool Equals(object? obj)
    {
        return obj is BlizzTrackVersion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(BuildConfig, BuildId, CdnConfig, ProductConfig);
    }
}

public struct BlizzTrackCDN : IBlizzTrackManifest
{
    public static BlizzTrackType TrackType => BlizzTrackType.CDN;
    
    public string Name  { get; set; }
    public string Region  { get; set; }
    public string Path  { get; set; }
    public string Hosts  { get; set; }
    public string Servers  { get; set; }
    public string ConfigPath  { get; set; }
}

public interface IBlizzTrackManifest
{
    static abstract BlizzTrackType TrackType { get; }
}

public static class BlizzTrackDataExtensions
{
    extension(BlizzTrackType type)
    {
        public string GetString() => type switch {
            BlizzTrackType.Version => "versions",
            BlizzTrackType.CDN => "cdns",
            BlizzTrackType.BGDL => "bgdl",
            BlizzTrackType.Unknown => throw new NotImplementedException(),
            _ => throw new NotImplementedException()
        };
    }
}

public readonly struct BlizzTrackSeqnValue(uint seqn) : IComparable<BlizzTrackSeqnValue>
{
    private readonly uint m_seqn = seqn;
    
    public int CompareTo(BlizzTrackSeqnValue other)
    {
        return -m_seqn.CompareTo(other.m_seqn);
    }
    
    public class BlizzTrackSeqnValueConverter : JsonConverter<BlizzTrackSeqnValue>
    {
        public override BlizzTrackSeqnValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var seqn = reader.GetString();
            if (string.IsNullOrEmpty(seqn)) {
                return new BlizzTrackSeqnValue(0);
            }
            
            return new BlizzTrackSeqnValue(uint.Parse(seqn));
        }

        public override void Write(Utf8JsonWriter writer, BlizzTrackSeqnValue value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value.m_seqn);
        }
    
        public override BlizzTrackSeqnValue ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var name = reader.GetString() ?? throw new JsonException("Property name cannot be null.");
            return new BlizzTrackSeqnValue(uint.Parse(name));
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, BlizzTrackSeqnValue value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.m_seqn.ToString());
        }
    }
}
