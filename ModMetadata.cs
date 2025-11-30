using SPTarkov.Server.Core.Models.Spt.Mod;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.sp-tarkov.jonjetjon.scavs4all";
    public override string Name { get; init; } = "Scavs 4 All";
    public override string Author { get; init; } = "jonjetjon";
    public override List<string>? Contributors { get; init; } = ["LeftHandedCat", "Croodile Jonesy"];
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");


    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string? License { get; init; } = "MIT";
}
