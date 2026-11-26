namespace abaci_bot.Options;

/// <summary>
/// Configuration options for PR target release branch classification.
/// </summary>
public class TargetClassificationOptions
{
    [ConfigField(
        Description = "Optional repositories dedicated exclusively to LTS releases.",
        Format = "repository names (\"owner/repo\" or bare repository name)",
        EmptyBehavior = "LTS classification is decided solely by LtsBranchPrefixes.",
        Example = "[\"openRuyi-LTS\"]")]
    public List<string> LtsRepositories { get; set; } = new();

    [ConfigField(
        Description = "Base branch name prefixes indicating LTS tracking.",
        Format = "branch name prefixes",
        EmptyBehavior = "no branch is classified as LTS by prefix.",
        Example = "[\"lts/\", \"lts-\"]")]
    public List<string> LtsBranchPrefixes { get; set; } = new() { "lts/", "lts-" };

    [ConfigField(
        Description = "Exact base branch names representing the standard Rolling track.",
        Format = "exact base branch names",
        EmptyBehavior = "no branch is explicitly Rolling; DefaultTarget applies.",
        Example = "[\"main\", \"master\", \"rolling\"]")]
    public List<string> RollingBranches { get; set; } = new() { "main", "master", "rolling" };

    [ConfigField(
        Description = "Fallback target classification when a base branch matches neither RollingBranches nor LtsBranchPrefixes.",
        AllowedValues = "\"Rolling\", \"LTS\", or null",
        EmptyBehavior = "null or unrecognized values leave unmatched branches without a Target label.")]
    public string? DefaultTarget { get; set; } = "Rolling";
}
