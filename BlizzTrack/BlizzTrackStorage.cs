using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BattleNGDP.Core;

namespace BattleNGDP.BlizzTrack;

public struct BlizzTrackProduct
{
    public SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackVersion>> Versions;
    public SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackCDN>> Cdns;

    public SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<T>> Get<T>() where T : struct, IBlizzTrackManifest
    {
        switch (T.TrackType) {
            case BlizzTrackType.Version:
                return Unsafe.As<SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackVersion>>, SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<T>>>(ref Versions);
            case BlizzTrackType.CDN:
                return Unsafe.As<SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackCDN>>, SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<T>>>(ref Cdns);
            case BlizzTrackType.BGDL:
            case BlizzTrackType.Unknown:
            default:
                throw new NotSupportedException($"BlizzTrackManifest<{typeof(T).Name}> is not supported.");
        }
    }
}

public struct BlizzTrackStorage()
{
    public Dictionary<string, BlizzTrackProduct> Products { get; set; } = new();

    private static BlizzTrackStorage? s_instance;

    private static JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IncludeFields = true,
        Converters = { new BlizzTrackSeqnValue.BlizzTrackSeqnValueConverter() }
    };
    
    public static bool TryGetProduct(string product, out BlizzTrackProduct data)
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize BlizzTrack Storage.");
        }

        if (!s_instance.Value.Products.TryGetValue(product, out var res)) {
            res = new BlizzTrackProduct() {
                Versions = new SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackVersion>>(),
                Cdns = new SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackCDN>>()
            };
            
            s_instance.Value.Products[product] = res;
        }

        data = res;
        return true;
    }
    
    public static BlizzTrackProduct GetTact(string tact)
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize BlizzTrack Storage.");
        }

        if (!s_instance.Value.Products.TryGetValue(tact, out var res)) {
            res = new BlizzTrackProduct() {
                Versions = new SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackVersion>>(),
                Cdns = new SortedList<BlizzTrackSeqnValue, BlizzTrackManifest<BlizzTrackCDN>>()
            };
            
            s_instance.Value.Products[tact] = res;
        }
        
        return res;
    }

    public static void Save()
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize BlizzTrack Storage.");
        }
        
        var path = Path.Combine(Config.Root, "BlizzTrackProducts.json");
        var data = JsonSerializer.Serialize(s_instance, s_options);
        File.WriteAllText(path, data);
    }

    [MemberNotNullWhen(true, nameof(s_instance))]
    private static bool EnsureLoaded()
    {
        if (s_instance != null) {
            return true;
        }

        var path = Path.Combine(Config.Root, "BlizzTrackProducts.json");

        if (!File.Exists(path)) {
            s_instance = new BlizzTrackStorage();
            return true;
        }

        var fileContent = File.ReadAllText(path);
        s_instance = JsonSerializer.Deserialize<BlizzTrackStorage>(fileContent, s_options);
        return true;
    }
}