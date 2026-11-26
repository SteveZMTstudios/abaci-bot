using System.Diagnostics.CodeAnalysis;

namespace abaci_bot.Options;

/// <summary>
/// Semantic kind of a configuration leaf or structural container in <see cref="ConfigFieldDescriptor"/>.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Schema field kinds intentionally align with standard JSON/CLR terminology (Integer, String).")]
public enum ConfigFieldKind
{
    Boolean = 0,
    Integer,
    Number,
    String,
    Enum,
    StringArray,
    ObjectArray,
    StringMap,
    NestedObject
}

/// <summary>
/// Strongly typed, immutable metadata descriptor for a single configuration field or nested section.
/// Decoupled from JSONC prose rendering so visual config editors and validators can consume it directly.
/// </summary>
public sealed record ConfigFieldDescriptor(
    string Name,
    string Path,
    ConfigFieldKind Kind,
    Type ClrType,
    object? DefaultValue,
    object? PlaceholderValue,
    string? Description,
    string? Format,
    string? Unit,
    string? Range,
    string? AllowedValues,
    string? TrueBehavior,
    string? FalseBehavior,
    string? EmptyBehavior,
    string? Example,
    bool Required,
    string? RequiredWhen,
    bool Secret,
    bool Multiline,
    string? MapKeyFormat,
    string? MapValueFormat,
    IReadOnlyList<ConfigFieldDescriptor> NestedFields,
    IReadOnlyList<ConfigFieldDescriptor> ItemFields);

/// <summary>
/// Strongly typed metadata descriptor for a root configuration section (backed either by an
/// <c>*Options</c> class or by an explicit <c>NonOptionsSectionDescriptor</c>).
/// </summary>
public sealed record ConfigSectionDescriptor(
    string SectionName,
    string Title,
    string DefaultsSource,
    string? ValidatorSource,
    IReadOnlyList<ConfigFieldDescriptor> Fields);
