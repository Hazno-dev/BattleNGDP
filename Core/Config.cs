namespace BattleNGDP.Core;

public static class Config
{
    public static readonly string Root;
    
    static Config()
    {
        var appData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (string.IsNullOrWhiteSpace(appData)) {
            throw new PlatformNotSupportedException("No native application-data directory is available.");
        }

        Root = Path.Combine(appData, "Hazno", "BattleNGDP");
        Directory.CreateDirectory(Root);
    }
}