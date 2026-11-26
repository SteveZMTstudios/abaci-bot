namespace abaci_bot.Options;

/// <summary>
/// Single-source structured configuration metadata for an options property.
/// Used by <see cref="Services.ConfigSchemaCatalog"/> and <see cref="Services.ConfigDemoGenerator"/>
/// to deterministically generate <c>appsettings.demo.jsonc</c> and expose typed schemas to tooling.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ConfigFieldAttribute : Attribute
{
    public string? Description { get; init; }
    public string? Format { get; init; }
    public string? Unit { get; init; }
    public string? Range { get; init; }
    public string? AllowedValues { get; init; }
    public string? TrueBehavior { get; init; }
    public string? FalseBehavior { get; init; }
    public string? EmptyBehavior { get; init; }
    public string? Example { get; init; }
    public bool Required { get; init; }
    /// <summary>
    /// Optional non-default demo value emitted in <c>appsettings.demo.jsonc</c> when <see cref="Required"/> is <c>true</c>.
    /// </summary>
    public object? Placeholder { get; init; }
    public string? RequiredWhen { get; init; }
    public bool Secret { get; init; }
    public bool Multiline { get; init; }
    public string? MapKeyFormat { get; init; }
    public string? MapValueFormat { get; init; }
}
