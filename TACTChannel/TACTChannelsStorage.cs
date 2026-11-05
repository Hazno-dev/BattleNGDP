using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BattleNGDP.BlizzTrack;
using BattleNGDP.Core;

namespace BattleNGDP.TACTChannel;


public struct TACTChannelDefinitionValuePair<T> where T : struct
{
    public string Definition { get; set; }
    public T Value;
}

public struct TACTChannelProductBuild : IComparable<TACTChannelProductBuild>
{
    public string Product { get; set; }
    public string Variant { get; set; }
    public bool Infrastructure { get; set; }
    public Dictionary<string, TACTChannelDefinitionValuePair<TACTChannelProductDefinitionData>> Builds { get; set; }
    public Dictionary<string, TACTChannelDefinitionValuePair<TACTChannelProductDefinitionData>>? Preloads { get; set; }
    public Dictionary<string, TACTChannelDefinitionValuePair<TACTChannelProductCDNData>> Regions { get; set; }
    
    public DateTime ObtainedDate { get; set; }

    public int CompareTo(TACTChannelProductBuild other)
    {
        return -ObtainedDate.CompareTo(other.ObtainedDate);
    }
}

public struct TACTChannelsStorage
{
    public Dictionary<string, List<TACTChannelProductBuild>> ProductBuilds { get; set; }
    public string LastHash { get; set; }

    private static TACTChannelsStorage? s_instance;
    
    private static JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true
    };

    public static bool HasChanged(in TACTChannelPaths curPaths)
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize TACT Channels Storage.");
        }

        var last = s_instance.Value.LastHash;
        var cur = $"{curPaths.Path}{curPaths.Public.Channel}";
        if (cur == last) {
            return false;
        }

        s_instance = s_instance.Value with { LastHash = cur };
        return true;
    }
    
    public static List<TACTChannelProductBuild> GetTact(string tact)
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize TACT Channels Storage.");
        }

        if (!s_instance.Value.ProductBuilds.TryGetValue(tact, out var res)) { 
            res = s_instance.Value.ProductBuilds[tact] = [];
        }
        
        return res;
    }
    
    public static void Save()
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize TACT Channels Storage.");
        }
        
        var path = Path.Combine(Config.Root, "TACTChannelsData.json");
        var data = JsonSerializer.Serialize(s_instance, s_options);
        File.WriteAllText(path, data);
    }
    
    [MemberNotNullWhen(true, nameof(s_instance))]
    private static bool EnsureLoaded()
    {
        if (s_instance != null) {
            return true;
        }

        var path = Path.Combine(Config.Root, "TACTChannelsData.json");

        if (!File.Exists(path)) {
            s_instance = new TACTChannelsStorage() {
                ProductBuilds = new Dictionary<string, List<TACTChannelProductBuild>>(),
                LastHash = string.Empty
            };
            return true;
        }

        var fileContent = File.ReadAllText(path);
        s_instance = JsonSerializer.Deserialize<TACTChannelsStorage>(fileContent, s_options);
        return true;
    }
}