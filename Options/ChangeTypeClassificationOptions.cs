namespace abaci_bot.Options;

/// <summary>
/// Configuration options for PR change type static file-tree classification.
/// </summary>
public class ChangeTypeClassificationOptions
{
    [ConfigField(
        Description = "File extensions recognized as purely documentation.",
        Format = "file extensions including the leading dot",
        EmptyBehavior = "extension-based documentation detection is disabled.",
        Example = "[\".md\", \".txt\", \".rst\"]")]
    public List<string> DocumentationExtensions { get; set; } = new() { ".md", ".txt", ".rst" };

    [ConfigField(
        Description = "Directory prefixes recognized as documentation directories.",
        Format = "directory prefixes including the trailing slash",
        EmptyBehavior = "directory-based documentation detection is disabled.",
        Example = "[\"docs/\", \"doc/\"]")]
    public List<string> DocumentationDirectories { get; set; } = new() { "docs/", "doc/" };

    [ConfigField(
        Description = "File extensions recognized as RPM packaging changes.",
        Format = "file extensions including the leading dot",
        EmptyBehavior = "extension-based packaging detection is disabled.",
        Example = "[\".spec\", \".patch\", \".diff\"]")]
    public List<string> PackagingExtensions { get; set; } = new() { ".spec", ".patch", ".diff" };

    [ConfigField(
        Description = "Directory prefixes recognized as RPM packaging areas.",
        Format = "directory prefixes including the trailing slash",
        EmptyBehavior = "directory-based packaging detection is disabled.",
        Example = "[\"SPECS/\", \"SOURCES/\"]")]
    public List<string> PackagingDirectories { get; set; } = new() { "SPECS/", "SOURCES/" };

    [ConfigField(
        Description = "File extensions recognized as frontend code (evaluated only for HomepageRepositories).",
        Format = "file extensions including the leading dot",
        EmptyBehavior = "Type: Frontend is never applied.",
        Example = "[\".vue\", \".ts\", \".js\"]")]
    public List<string> FrontendExtensions { get; set; } = new() { ".vue", ".ts", ".js", ".jsx", ".tsx", ".html", ".css", ".scss", ".less" };

    [ConfigField(
        Description = "Repository names using the package strategy (others fall back to documentation/CI heuristics).",
        Format = "repository names",
        EmptyBehavior = "no repository uses the package strategy; unlisted repositories use the generic strategy.",
        Example = "[\"openRuyi\"]")]
    public List<string> PackageRepositories { get; set; } = new() { "openRuyi" };

    [ConfigField(
        Description = "Repository names using frontend change-type detection rules.",
        Format = "repository names",
        EmptyBehavior = "frontend rules never apply to any repository.",
        Example = "[\"homepage\"]")]
    public List<string> HomepageRepositories { get; set; } = new() { "homepage" };
}
