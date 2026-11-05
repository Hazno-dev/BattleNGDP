namespace BattleNGDP.Core;

public class ProductBuild
{
    public ProductBuildInfo BuildInfo { get; set; }
    

    //should be actual struct. temp
    private List<string> m_files;
    public IReadOnlyList<string> Files => m_files;
    
    
    
}

