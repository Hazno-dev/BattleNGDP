namespace BattleNGDP.TACT.CDN;

public class TACTProductConfig
{
    public string Name { get; init; }
    
    public bool Offline { get; init; }
    public string[]? Cdns { get; init; }
}