using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BattleNGDP.TACTChannel;

public struct TACTChannelPaths
{
    public string Path { get; set; }
    public TACTChannelPathData Public  { get; set; }
    public TACTChannelPathData? Private { get; set; }
}

public struct TACTChannelPathData
{
    public string Channel { get; set; }
}

public struct TACTChannelSummary
{
    public string Name { get; set; }
    public TACTChannelProduct[] Products { get; set; }
    
    [JsonPropertyName("json_version")]
    public uint JsonVersion { get; set; }
}

public struct TACTChannelProduct
{
    public string Product { get; set; }
    public string Variant { get; set; }
    public bool Infrastructure { get; set; }
    public TACTChannelProductDefinition[] Builds { get; set; }
    public TACTChannelProductDefinition[]? Preloads { get; set; }
    public TACTChannelProductCDN[] Regions { get; set; }
}

public struct TACTChannelProductDefinition
{
    public string[] Branches { get; set; }
    public string Definition { get; set; }
}

public struct TACTChannelProductCDN
{
    public string[] Branches { get; set; }
    public string Cdns { get; set; }
}

public struct TACTChannelProductDefinitionData
{
    public struct BuildInfo
    {
        public string Name { get; set; }
        public string Token { get; set; }
    }

    public struct ProductInfo
    {
        public string CdnPath { get; set; }
        public string Hash { get; set; }
        
        public JsonObject? Data { get; set; }
    }

    public string ArmadilloKey { get; set; }
    public BuildInfo Build { get; set; }
    public string BuildKey { get; set; }
    public string CdnKey { get; set; }
    public string KeyRing { get; set; }
    public string Path { get; set; }
    public int PipelineId { get; set; }
    public ProductInfo ProductConfig { get; set; }
    public uint VersionNumber { get; set; }
    public string VersionString { get; set; }
}

public struct TACTChannelProductCDNData
{
    public struct CDNEntry
    {
        public string Host { get; set; }
    }
    
    public CDNEntry[] Hosts { get; set; }
}