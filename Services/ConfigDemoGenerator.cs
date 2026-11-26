using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using abaci_bot.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deterministic renderer that projects <see cref="ConfigSectionDescriptor"/> metadata
/// into the tracked <c>appsettings.demo.jsonc</c> reference file.
/// </summary>
internal static class ConfigDemoGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new(JsonOptions)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Generates the complete LF-normalized JSONC text for <c>appsettings.demo.jsonc</c>.
    /// </summary>
    public static string Generate(IReadOnlyList<ConfigSectionDescriptor>? sections = null)
    {
        var resolvedSections = sections ?? ConfigSchemaCatalog.BuildSections();
        var sb = new StringBuilder();

        sb.Append("/*\n");
        sb.Append(" * abaci-bot full reference configuration (appsettings.demo.jsonc).\n");
        sb.Append(" *\n");
        sb.Append(" * Generated deterministically from Options/*Options.cs ([ConfigField] metadata)\n");
        sb.Append(" * and Services/ConfigSchemaCatalog.cs. Do not hand-edit this file; update the\n");
        sb.Append(" * metadata source and regenerate with:\n");
        sb.Append(" *   PowerShell: $env:UPDATE_DEMO_CONFIG='1'; dotnet test --filter GeneratedDemo_MatchesRepositoryFile; Remove-Item Env:UPDATE_DEMO_CONFIG\n");
        sb.Append(" *   POSIX:      UPDATE_DEMO_CONFIG=1 dotnet test --filter GeneratedDemo_MatchesRepositoryFile\n");
        sb.Append(" *\n");
        sb.Append(" * Production appsettings.json should remain minimal and override only settings that\n");
        sb.Append(" * differ from code defaults. Replace placeholders in GitHubApp before startup,\n");
        sb.Append(" * and store secrets (PrivateKey, WebhookSecret) in environment variables or a\n");
        sb.Append(" * secret manager.\n");
        sb.Append(" */\n");
        sb.Append("{\n");

        for (var i = 0; i < resolvedSections.Count; i++)
        {
            var section = resolvedSections[i];
            if (i > 0)
            {
                sb.Append('\n');
            }

            AppendSectionBanner(sb, section, indentLevel: 1);
            AppendIndent(sb, indentLevel: 1);
            sb.Append(JsonSerializer.Serialize(section.SectionName, JsonOptions));
            sb.Append(": {\n");

            AppendFields(sb, section.Fields, indentLevel: 2);

            AppendIndent(sb, indentLevel: 1);
            sb.Append(i < resolvedSections.Count - 1 ? "},\n" : "}\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendSectionBanner(
        StringBuilder sb,
        ConfigSectionDescriptor section,
        int indentLevel)
    {
        AppendCommentLine(sb, indentLevel, "=========================================================================");
        AppendCommentLine(sb, indentLevel, $"{section.SectionName} -- {section.Title}");
        AppendCommentLine(sb, indentLevel, $"Defaults source: {section.DefaultsSource}");
        if (!string.IsNullOrWhiteSpace(section.ValidatorSource))
        {
            AppendCommentLine(sb, indentLevel, $"Bounds checked by: {section.ValidatorSource}");
        }

        AppendCommentLine(sb, indentLevel, "=========================================================================");
    }

    private static void AppendFields(
        StringBuilder sb,
        IReadOnlyList<ConfigFieldDescriptor> fields,
        int indentLevel)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            var field = fields[i];
            var isLast = i == fields.Count - 1;

            AppendFieldComments(sb, field, indentLevel);

            if (field.Secret && ConfigSchemaValidator.HasNonEmptySecretDefault(field.DefaultValue))
            {
                throw new InvalidOperationException(
                    $"Configuration field '{field.Path}' is marked Secret=true and must not define a non-empty DefaultValue.");
            }

            AppendIndent(sb, indentLevel);
            sb.Append(JsonSerializer.Serialize(field.Name, JsonOptions));
            sb.Append(": ");

            if (field.Kind == ConfigFieldKind.NestedObject)
            {
                if (field.NestedFields.Count == 0)
                {
                    sb.Append(isLast ? "{}\n" : "{},\n");
                }
                else
                {
                    sb.Append("{\n");
                    AppendFields(sb, field.NestedFields, indentLevel + 1);
                    AppendIndent(sb, indentLevel);
                    sb.Append(isLast ? "}\n" : "},\n");
                }
            }
            else
            {
                AppendJsonValue(sb, field, indentLevel);
                sb.Append(isLast ? "\n" : ",\n");
            }
        }
    }

    private static void AppendFieldComments(
        StringBuilder sb,
        ConfigFieldDescriptor field,
        int indentLevel)
    {
        if (!string.IsNullOrWhiteSpace(field.Description))
        {
            AppendCommentLine(sb, indentLevel, EnsureSentence(field.Description));
        }

        if (field.Kind == ConfigFieldKind.NestedObject)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(field.Format))
        {
            AppendCommentLine(sb, indentLevel, $"Format: {EnsureSentence(field.Format)}");
        }

        if (!string.IsNullOrWhiteSpace(field.Unit))
        {
            AppendCommentLine(sb, indentLevel, $"Unit: {EnsureSentence(field.Unit)}");
        }

        if (!string.IsNullOrWhiteSpace(field.Range))
        {
            AppendCommentLine(sb, indentLevel, $"Range: {EnsureSentence(field.Range)}");
        }

        if (!string.IsNullOrWhiteSpace(field.AllowedValues))
        {
            AppendCommentLine(sb, indentLevel, $"Allowed values: {EnsureSentence(field.AllowedValues)}");
        }

        if (!string.IsNullOrWhiteSpace(field.MapKeyFormat))
        {
            AppendCommentLine(sb, indentLevel, $"Key format: {EnsureSentence(field.MapKeyFormat)}");
        }

        if (!string.IsNullOrWhiteSpace(field.MapValueFormat))
        {
            AppendCommentLine(sb, indentLevel, $"Value format: {EnsureSentence(field.MapValueFormat)}");
        }

        foreach (var itemField in field.ItemFields)
        {
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(itemField.Description))
            {
                details.Add(TrimTrailingPeriod(itemField.Description));
            }

            if (!string.IsNullOrWhiteSpace(itemField.Format))
            {
                details.Add($"format: {TrimTrailingPeriod(itemField.Format)}");
            }

            if (!string.IsNullOrWhiteSpace(itemField.AllowedValues))
            {
                details.Add($"allowed values: {TrimTrailingPeriod(itemField.AllowedValues)}");
            }

            if (!string.IsNullOrWhiteSpace(itemField.EmptyBehavior))
            {
                details.Add($"empty/0/null: {TrimTrailingPeriod(itemField.EmptyBehavior)}");
            }

            if (itemField.Required)
            {
                details.Add(ConfigSchemaValidator.HasUsablePlaceholder(itemField)
                    ? $"required (placeholder: {FormatValueForComment(itemField.Kind, itemField.PlaceholderValue)})"
                    : "required");
            }
            else if (itemField.DefaultValue != null)
            {
                details.Add($"default: {FormatCommentValue(itemField)}");
            }

            if (!string.IsNullOrWhiteSpace(itemField.Example))
            {
                details.Add($"example: {itemField.Example.Trim()}");
            }

            AppendCommentLine(
                sb,
                indentLevel,
                $"Item field {itemField.Name}: {string.Join("; ", details)}.");
        }

        if (field.Kind == ConfigFieldKind.Boolean)
        {
            if (!string.IsNullOrWhiteSpace(field.TrueBehavior))
            {
                AppendCommentLine(sb, indentLevel, $"true: {EnsureSentence(field.TrueBehavior)}");
            }

            if (!string.IsNullOrWhiteSpace(field.FalseBehavior))
            {
                AppendCommentLine(sb, indentLevel, $"false: {EnsureSentence(field.FalseBehavior)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(field.RequiredWhen))
        {
            AppendCommentLine(sb, indentLevel, $"Required when: {EnsureSentence(field.RequiredWhen)}");
        }

        if (field.Secret)
        {
            AppendCommentLine(
                sb,
                indentLevel,
                "Secret: provide via environment variables or a secret manager rather than committing real credentials.");
        }

        if (field.Multiline)
        {
            AppendCommentLine(
                sb,
                indentLevel,
                "Multiline: encode embedded line breaks as literal \\n sequences inside the JSON string.");
        }

        if (!string.IsNullOrWhiteSpace(field.EmptyBehavior))
        {
            AppendCommentLine(sb, indentLevel, $"Empty/0/null: {EnsureSentence(field.EmptyBehavior)}");
        }

        if (!string.IsNullOrWhiteSpace(field.Example))
        {
            AppendCommentLine(sb, indentLevel, $"Example: {field.Example.Trim()}");
        }

        if (field.Required)
        {
            var requiredLine = ConfigSchemaValidator.HasUsablePlaceholder(field)
                ? "Required: true (no code default; placeholder shown below)."
                : "Required: true (no code default).";
            AppendCommentLine(sb, indentLevel, requiredLine);
        }
        else
        {
            AppendCommentLine(sb, indentLevel, $"Default: {FormatCommentValue(field)}");
        }
    }

    private static string FormatCommentValue(ConfigFieldDescriptor field)
    {
        return FormatValueForComment(field.Kind, field.DefaultValue);
    }

    private static string FormatValueForComment(ConfigFieldKind kind, object? value)
    {
        if (value == null)
        {
            return "null";
        }

        return kind switch
        {
            ConfigFieldKind.Boolean => (bool)value ? "true" : "false",
            ConfigFieldKind.Integer or ConfigFieldKind.Number => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            ConfigFieldKind.Enum => JsonSerializer.Serialize(value.ToString(), JsonOptions),
            ConfigFieldKind.String => JsonSerializer.Serialize((string)value, JsonOptions),
            ConfigFieldKind.StringArray => FormatInlineStringArray((IEnumerable<string>)value),
            ConfigFieldKind.ObjectArray => FormatInlineObjectArray((IEnumerable)value),
            ConfigFieldKind.StringMap => FormatInlineDictionary((IDictionary)value),
            _ => JsonSerializer.Serialize(value, JsonOptions),
        };
    }

    private static string FormatInlineStringArray(IEnumerable<string> values)
    {
        var items = values.Select(v => JsonSerializer.Serialize(v, JsonOptions)).ToList();
        return items.Count == 0 ? "[]" : $"[{string.Join(", ", items)}]";
    }

    private static string FormatInlineObjectArray(IEnumerable values)
    {
        var list = values.Cast<object?>().ToList();
        return list.Count == 0 ? "[]" : JsonSerializer.Serialize(list, JsonOptions);
    }

    private static string FormatInlineDictionary(IDictionary dict)
    {
        if (dict.Count == 0)
        {
            return "{}";
        }

        var entries = dict.Keys.Cast<object>()
            .Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)!)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k =>
            {
                var rawVal = dict[k];
                var serializedVal = rawVal is string s
                    ? JsonSerializer.Serialize(s, JsonOptions)
                    : JsonSerializer.Serialize(rawVal, JsonOptions);
                return $"{JsonSerializer.Serialize(k, JsonOptions)}: {serializedVal}";
            });

        return $"{{ {string.Join(", ", entries)} }}";
    }

    private static void AppendJsonValue(
        StringBuilder sb,
        ConfigFieldDescriptor field,
        int indentLevel)
    {
        var value = ConfigSchemaValidator.HasUsablePlaceholder(field)
            ? field.PlaceholderValue
            : field.DefaultValue;
        if (value == null)
        {
            sb.Append("null");
            return;
        }

        switch (field.Kind)
        {
            case ConfigFieldKind.Boolean:
                sb.Append((bool)value ? "true" : "false");
                break;

            case ConfigFieldKind.Integer:
            case ConfigFieldKind.Number:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;

            case ConfigFieldKind.Enum:
                sb.Append(JsonSerializer.Serialize(value.ToString(), JsonOptions));
                break;

            case ConfigFieldKind.String:
                sb.Append(JsonSerializer.Serialize((string)value, JsonOptions));
                break;

            case ConfigFieldKind.StringArray:
                AppendStringArrayJson(sb, (IEnumerable<string>)value, indentLevel);
                break;

            case ConfigFieldKind.ObjectArray:
                AppendObjectArrayJson(sb, (IEnumerable)value, indentLevel);
                break;

            case ConfigFieldKind.StringMap:
                AppendDictionaryJson(sb, (IDictionary)value, indentLevel);
                break;

            default:
                sb.Append(JsonSerializer.Serialize(value, JsonOptions));
                break;
        }
    }

    private static void AppendStringArrayJson(
        StringBuilder sb,
        IEnumerable<string> values,
        int indentLevel)
    {
        var items = values.ToList();
        if (items.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append("[\n");
        for (var i = 0; i < items.Count; i++)
        {
            AppendIndent(sb, indentLevel + 1);
            sb.Append(JsonSerializer.Serialize(items[i], JsonOptions));
            sb.Append(i < items.Count - 1 ? ",\n" : "\n");
        }

        AppendIndent(sb, indentLevel);
        sb.Append(']');
    }

    private static void AppendObjectArrayJson(
        StringBuilder sb,
        IEnumerable values,
        int indentLevel)
    {
        var items = values.Cast<object?>().ToList();
        if (items.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        var indented = JsonSerializer.Serialize(items, IndentedJsonOptions);
        var lines = indented.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        sb.Append(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            sb.Append('\n');
            AppendIndent(sb, indentLevel);
            sb.Append(lines[i]);
        }
    }

    private static void AppendDictionaryJson(
        StringBuilder sb,
        IDictionary dict,
        int indentLevel)
    {
        if (dict.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        var keys = dict.Keys.Cast<object>()
            .Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)!)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        sb.Append("{\n");
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            AppendIndent(sb, indentLevel + 1);
            sb.Append(JsonSerializer.Serialize(key, JsonOptions));
            sb.Append(": ");
            var rawVal = dict[key];
            sb.Append(rawVal is string s
                ? JsonSerializer.Serialize(s, JsonOptions)
                : JsonSerializer.Serialize(rawVal, JsonOptions));
            sb.Append(i < keys.Count - 1 ? ",\n" : "\n");
        }

        AppendIndent(sb, indentLevel);
        sb.Append('}');
    }

    private static void AppendCommentLine(StringBuilder sb, int indentLevel, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            AppendIndent(sb, indentLevel);
            sb.Append("// ");
            sb.Append(line);
            sb.Append('\n');
        }
    }

    private static void AppendIndent(StringBuilder sb, int indentLevel)
    {
        for (var i = 0; i < indentLevel; i++)
        {
            sb.Append("  ");
        }
    }

    private static string EnsureSentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var last = trimmed[^1];
        return last is '.' or '!' or '?' ? trimmed : $"{trimmed}.";
    }

    private static string TrimTrailingPeriod(string text)
    {
        return text.Trim().TrimEnd('.');
    }
}
