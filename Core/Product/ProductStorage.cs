using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using BattleNGDP.BlizzTrack;
using BattleNGDP.CASC;
using BattleNGDP.TACT;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Manifest;
using BattleNGDP.TACTChannel;
using Sharprompt;

namespace BattleNGDP.Core;

public class ProductBuildInfo() : IEquatable<BlizzTrackVersion>, IEquatable<TACTChannelProductDefinitionData>, IEquatable<ProductBuildInfo>
{
    public uint Version;
    public string Region = string.Empty;
    public string TACTName = string.Empty;

    public string? TACTPath = string.Empty;
    public string TACTBuildConfigHash = string.Empty;
    public string TACTCDNConfigHash = string.Empty;

    public string? ProductConfigHash = string.Empty;
    public string? ProductConfigPath = string.Empty;

    public bool Encrypted;
    public string? Armadillo;
    public DateTime CreatedAt;

    public BuildConfig? TACTBuildConfig;
    public CDNConfig? TACTCDNConfig;
    public JsonObject? ProductConfig;
    public InstallManifest? InstallManifest;

    public static bool operator ==(ProductBuildInfo left, BlizzTrackVersion right) => left.Equals(right);
    public static bool operator !=(ProductBuildInfo left, BlizzTrackVersion right) => !left.Equals(right);

    public static bool operator ==(ProductBuildInfo left, TACTChannelProductDefinitionData right) => left.Equals(right);
    public static bool operator !=(ProductBuildInfo left, TACTChannelProductDefinitionData right) => !left.Equals(right);

    public static bool operator ==(ProductBuildInfo? left, ProductBuildInfo? right) => left?.Equals(right) ?? right is null;
    public static bool operator !=(ProductBuildInfo? left, ProductBuildInfo? right) => !left?.Equals(right) ?? right is not null;

    public ProductBuildInfo(BlizzTrackManifest<BlizzTrackVersion> manifest, BlizzTrackVersion version, BlizzTrackCDN? cdn, bool encrypted) : this()
    {
        Version = version.BuildId;
        Region = version.Region;
        TACTName = manifest.Tact;

        TACTBuildConfigHash = version.BuildConfig;
        TACTCDNConfigHash = version.CdnConfig;
        ProductConfigHash = version.ProductConfig;

        TACTPath = cdn?.Path;
        ProductConfigPath = cdn?.ConfigPath;
        Encrypted = encrypted;
        CreatedAt = manifest.CreatedAt;
    }

    public ProductBuildInfo(TACTChannelProductDefinitionData definitionData, string region, string tactName, DateTime creation) : this()
    {
        Version = definitionData.VersionNumber;
        Region = region;
        TACTName = tactName;

        TACTBuildConfigHash = definitionData.BuildKey;
        TACTCDNConfigHash = definitionData.CdnKey;
        ProductConfigHash = definitionData.ProductConfig.Hash;
        ProductConfig = definitionData.ProductConfig.Data;

        TACTPath = definitionData.Path;
        ProductConfigPath = string.IsNullOrEmpty(definitionData.ProductConfig.CdnPath)
            ? "tpr/configs/data"
            : definitionData.ProductConfig.CdnPath;
        Armadillo = definitionData.ProductConfig.Data?["all"]?["config"]?["decryption_key_name"]?.GetValue<string?>();
        Encrypted = Armadillo != null;
        CreatedAt = creation;
    }

    public bool Equals(BlizzTrackVersion other) =>
        Version == other.BuildId && TACTBuildConfigHash == other.BuildConfig && TACTCDNConfigHash == other.CdnConfig;

    public bool Equals(TACTChannelProductDefinitionData other) =>
        Version == other.VersionNumber && TACTBuildConfigHash == other.BuildKey && TACTCDNConfigHash == other.CdnKey;

    public override bool Equals(object? obj)
    {
        if (obj is null) {
            return false;
        }

        if (ReferenceEquals(this, obj)) {
            return true;
        }

        if (obj.GetType() != GetType()) {
            return false;
        }

        return Equals((ProductBuildInfo) obj);
    }

    public override int GetHashCode() => HashCode.Combine(Version, TACTBuildConfigHash, TACTCDNConfigHash, ProductConfigHash);

    public bool Equals(ProductBuildInfo? other)
    {
        if (other is null) {
            return false;
        }

        if (ReferenceEquals(this, other)) {
            return true;
        }

        return Version == other.Version && TACTBuildConfigHash == other.TACTBuildConfigHash && TACTCDNConfigHash == other.TACTCDNConfigHash &&
               ProductConfigHash == other.ProductConfigHash;
    }

    public string CondensedString()
    {
        if (TACTBuildConfig == null) {
            if (!Encrypted) {
                return $"{Version,-8} {TACTBuildConfigHash}::{TACTCDNConfigHash}";
            }

            return $"{Version,-8} {TACTBuildConfigHash}::{TACTCDNConfigHash} (Encrypted)";
        }

        var product = TACTBuildConfig.Product;
        if (!string.IsNullOrEmpty(product) && !string.IsNullOrEmpty(TACTBuildConfig.Uid)) {
            product += $"::{TACTBuildConfig.Uid}";
        }

        var fileCount = InstallManifest?.Files?.Length > 0 ? $"({InstallManifest.Files.Length} Files)" : string.Empty;

        product ??= TACTBuildConfig.Uid;
        var buildVersion = TACTBuildConfig?.ClientVersion ?? TACTBuildConfig?.Branch ?? string.Empty;
        var comment = TACTBuildConfig?.Comments ?? string.Empty;

        return $"{Version,-8} {product} {buildVersion} {comment} {TACTBuildConfigHash}::{TACTCDNConfigHash} {fileCount}";
    }

    [MemberNotNullWhen(true, nameof(TACTBuildConfig), nameof(TACTCDNConfig))]
    public async Task<bool> LoadConfigs()
    {
        Logger.Log($"Loading configs for {TACTName} v{Version}");
        Logger.Log("[*] Loading build config...");
        TACTBuildConfig = await BuildConfig.LoadAsync(this);

        Logger.Log("[*] Loading cdn config...");
        TACTCDNConfig = await CDNConfig.LoadAsync(this);

        Logger.Log("[*] Loading install manifest...");
        InstallManifest = await InstallManifest.LoadAsync(this);

        if (ProductConfig == null && !string.IsNullOrEmpty(ProductConfigHash)) {
            if (string.IsNullOrEmpty(ProductConfigPath)) {
                Logger.Log("ProductConfigPath is null. Using default \"tpr/configs/data\"...");
            }

            Logger.Log("[*] Loading product config...");
            using var configResponse = await TACTAssetService.GetProductConfig(this, ProductConfigHash);
            if (configResponse != null) {
                ProductConfig = JsonNode.Parse(configResponse.Value.ReadOnlySpan)?.AsObject();
            }
        }

        Logger.Log("Loaded configs!");
        Logger.Log(" ");
        ProductStorage.Save();
        return TACTBuildConfig != null && TACTCDNConfig != null;
    }

    public async Task<bool> LoadProductConfig()
    {
        if (ProductConfig == null && !string.IsNullOrEmpty(ProductConfigHash)) {
            if (string.IsNullOrEmpty(ProductConfigPath)) {
                Logger.Log("ProductConfigPath is null. Using default \"tpr/configs/data\"...");
            }

            Logger.Log("[*] Loading product config...");
            using var configResponse = await TACTAssetService.GetProductConfig(this, ProductConfigHash);
            if (configResponse != null) {
                ProductConfig = JsonNode.Parse(configResponse.Value.ReadOnlySpan)?.AsObject();
            }
        }

        ProductStorage.Save();
        return ProductConfig != null;
    }

    public async Task<bool> EnsureTACTPath()
    {
        if (!string.IsNullOrEmpty(TACTPath)) {
            return true;
        }

        var path = Prompt.Input<string>($"v{Version} is missing TACT path. Please enter the TACT path");
        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        TACTPath = path;
        ProductStorage.Save();
        return true;
    }

    public async Task<bool> EnsureArmadillo()
    {
        if (!string.IsNullOrEmpty(Armadillo)) {
            return true;
        }

        if (ProductConfig == null) {
            await LoadProductConfig();
        }

        var armadilloName = ProductConfig?["all"]?["config"]?["decryption_key_name"]?.GetValue<string?>();
        if (armadilloName == null) {
            if (!Encrypted) {
                return true;
            }

            Logger.Log($"Failed to load armadillo for {TACTName} v{Version}");
            armadilloName = Prompt.Input<string>("Please enter the armadillo key name");
        }

        if (string.IsNullOrEmpty(armadilloName)) {
            return false;
        }

        Armadillo = armadilloName;
        ProductStorage.Save();
        return true;
    }
}

public class ProductInfo
{
    public string? Name { get; set; }
    public string TACTName { get; set; }

    public SortedList<uint, List<ProductBuildInfo>> ProductBuilds { get; set; }
}

public struct ProductStorage()
{
    public Dictionary<string, ProductInfo> Products { get; set; } = new();

    private static ProductStorage? s_instance;

    private static JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true
    };

    public static ProductInfo GetTact(string tact)
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize Product Storage.");
        }

        if (!s_instance.Value.Products.TryGetValue(tact, out var res)) {
            res = new ProductInfo {
                TACTName = tact,
                ProductBuilds = new SortedList<uint, List<ProductBuildInfo>>()
            };

            s_instance.Value.Products[tact] = res;
        }

        return res;
    }

    public static void Save()
    {
        if (!EnsureLoaded()) {
            throw new Exception("Failed to initialize Product Storage.");
        }

        var path = Path.Combine(Config.Root, "KnownProducts.json");
        var data = JsonSerializer.Serialize(s_instance, s_options);
        File.WriteAllText(path, data);
    }

    [MemberNotNullWhen(true, nameof(s_instance))]
    private static bool EnsureLoaded()
    {
        if (s_instance != null) {
            return true;
        }

        var path = Path.Combine(Config.Root, "KnownProducts.json");

        if (!File.Exists(path)) {
            s_instance = new ProductStorage();
            return true;
        }

        var fileContent = File.ReadAllText(path);
        s_instance = JsonSerializer.Deserialize<ProductStorage>(fileContent, s_options);
        return true;
    }
}