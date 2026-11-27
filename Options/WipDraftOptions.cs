namespace abaci_bot.Options;

/// <summary>
/// Configuration for <see cref="Modules.WipDraftModule"/>, which posts and reconciles single-comment
/// GitHub TIP notices reminding contributors to align WIP pull request titles and draft status.
/// </summary>
public sealed class WipDraftOptions
{
    public const string SectionName = "WipDraft";

    [ConfigField(
        Description = "Enables or disables WIP-to-Draft TIP reminder comment reconciliation on pull requests.",
        TrueBehavior = "WipDraftModule reconciles a single bot-owned TIP comment against PR title WIP tokens, draft state, and terminal state.",
        FalseBehavior = "WipDraftModule is skipped cleanly with no TIP comments created, updated, or deleted.")]
    public bool Enabled { get; set; } = true;

    [ConfigField(
        Description = "Dry-run mode that logs intended TIP comment actions without mutating GitHub comments.",
        TrueBehavior = "logs intended TIP create/update/delete actions without calling GitHub comment mutation APIs.",
        FalseBehavior = "executes live TIP comment create, update, and delete operations via the pipeline.")]
    public bool DryRun { get; set; }

    [ConfigField(
        Description = "Module-specific documentation links rendered inside WIP/Ready TIP reminder comments.",
        Format = "JSON array of up to 5 objects { \"Title\": \"...\", \"TitleZh\": \"...\" (optional), \"Url\": \"https://...\" }",
        EmptyBehavior = "no external documentation links are appended to TIP comments.",
        Example = "[{ \"Title\": \"Pull Request Workflow\", \"TitleZh\": \"PR 工作流指南\", \"Url\": \"https://openruyi.org/docs/pr-workflow\" }]")]
    public List<DocumentationLink> DocumentationLinks { get; set; } = new();
}
