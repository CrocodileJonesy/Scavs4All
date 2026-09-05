using Microsoft.AspNetCore.Mvc.ModelBinding;
using SPTarkov.Server.Core.Models.Spt.Mod;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.sp-tarkov.jonjetjon.scavs4all";
    public string Name { get; init; } = "Scavs 4 All";
    public string Author { get; init; } = "jonjetjon";
    public List<string>? Contributors { get; init; } = ["LeftHandedCat", "Croodile Jonesy"];
    public SemanticVersioning.Version Version { get; init; } = new("2.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");


    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public bool HasPrepatcher { get; init; }

    public string License { get; init; } = "MIT";
}
