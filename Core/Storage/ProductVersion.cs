namespace BattleNGDP.Core;

public struct ProductVersion()
{
    public string Name { get; set; }
    public string TACTName { get; set; }
    public List<ProductVersionEntry> Entries { get; set; } = [];
}

public struct ProductVersionEntry
{
    public string Name { get; set; }
    public string Version { get; set; }
    public string Region { get; set; }
    
    public string TACTPath { get; set; }
    public string BuildConfig { get; set; }
    public string CdnConfig { get; set; }
    
    public string CatalogPath { get; set; }
    public string ProductCatalog { get; set; }
}