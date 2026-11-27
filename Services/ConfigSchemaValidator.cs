using abaci_bot.Options;

namespace abaci_bot.Services;

/// <summary>
/// Validates <see cref="ConfigSectionDescriptor"/> and <see cref="ConfigFieldDescriptor"/> metadata
/// consistency (placeholder constraints, secret defaults, and per-kind metadata completeness).
/// </summary>
internal static class ConfigSchemaValidator
{
    private static readonly IReadOnlyDictionary<ConfigFieldKind, Action<ConfigFieldDescriptor, List<string>>> KindValidators =
        new Dictionary<ConfigFieldKind, Action<ConfigFieldDescriptor, List<string>>>
        {
            [ConfigFieldKind.Boolean] = ValidateBoolean,
            [ConfigFieldKind.Integer] = ValidateNumeric,
            [ConfigFieldKind.Number] = ValidateNumeric,
            [ConfigFieldKind.String] = ValidateStringOrEnum,
            [ConfigFieldKind.Enum] = ValidateStringOrEnum,
            [ConfigFieldKind.StringArray] = ValidateStringArray,
            [ConfigFieldKind.ObjectArray] = ValidateObjectArray,
            [ConfigFieldKind.StringMap] = ValidateStringMap,
            [ConfigFieldKind.NestedObject] = ValidateNestedObject,
        };

    /// <summary>
    /// Returns <c>true</c> when a <c>Secret</c> field carries a non-null, non-empty default value.
    /// </summary>
    public static bool HasNonEmptySecretDefault(object? defaultValue)
    {
        return defaultValue switch
        {
            null => false,
            string text => text.Length > 0,
            _ => true,
        };
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="field"/> is required and supplies a usable
    /// non-whitespace placeholder value.
    /// </summary>
    public static bool HasUsablePlaceholder(ConfigFieldDescriptor field)
    {
        if (!field.Required || field.PlaceholderValue == null)
        {
            return false;
        }

        return field.PlaceholderValue is not string text || !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    /// Validates all sections and fields recursively, returning human-readable failure descriptions.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<ConfigSectionDescriptor> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var failures = new List<string>();
        foreach (var section in sections)
        {
            if (section.Fields.Count == 0)
            {
                failures.Add($"{section.SectionName}: section must define at least one field");
                continue;
            }

            ValidateFields(section.Fields, failures);
        }

        return failures;
    }

    private static void ValidateFields(IReadOnlyList<ConfigFieldDescriptor> fields, List<string> failures)
    {
        foreach (var field in fields)
        {
            ValidateCommonMetadata(field, failures);

            if (KindValidators.TryGetValue(field.Kind, out var validator))
            {
                validator(field, failures);
            }
            else
            {
                failures.Add($"{field.Path}: unsupported ConfigFieldKind '{field.Kind}'");
            }
        }
    }

    private static void ValidateCommonMetadata(ConfigFieldDescriptor field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(field.Description))
        {
            failures.Add($"{field.Path}: missing Description");
        }

        if (field.Secret && HasNonEmptySecretDefault(field.DefaultValue))
        {
            failures.Add($"{field.Path}: Secret field must not have a non-empty DefaultValue");
        }

        if (field.PlaceholderValue == null)
        {
            return;
        }

        if (!field.Required)
        {
            failures.Add($"{field.Path}: PlaceholderValue is only allowed when Required is true");
        }

        if (field.PlaceholderValue is string placeholderText && string.IsNullOrWhiteSpace(placeholderText))
        {
            failures.Add($"{field.Path}: PlaceholderValue must not be empty or whitespace");
        }

        var expectedType = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;
        if (!IsCompatiblePlaceholderValue(expectedType, field.PlaceholderValue))
        {
            failures.Add(
                $"{field.Path}: PlaceholderValue type '{field.PlaceholderValue.GetType().Name}' is not assignable to '{expectedType.Name}'");
        }
    }

    private static bool IsCompatiblePlaceholderValue(Type expectedType, object value)
    {
        if (expectedType.IsInstanceOfType(value))
        {
            return true;
        }

        // Only strictly lossless CLR numeric widenings are accepted.
        return value switch
        {
            int => expectedType == typeof(long)
                   || expectedType == typeof(double)
                   || expectedType == typeof(decimal),
            long => expectedType == typeof(decimal),
            float => expectedType == typeof(double),
            _ => false,
        };
    }

    private static void ValidateBoolean(ConfigFieldDescriptor field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(field.TrueBehavior))
        {
            failures.Add($"{field.Path}: Boolean field must state TrueBehavior");
        }

        if (string.IsNullOrWhiteSpace(field.FalseBehavior))
        {
            failures.Add($"{field.Path}: Boolean field must state FalseBehavior");
        }
    }

    private static void ValidateNumeric(ConfigFieldDescriptor field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(field.Unit) && string.IsNullOrWhiteSpace(field.Range))
        {
            failures.Add($"{field.Path}: Numeric field must state Unit or Range");
        }
    }

    private static void ValidateStringOrEnum(ConfigFieldDescriptor field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(field.Format) && string.IsNullOrWhiteSpace(field.AllowedValues))
        {
            failures.Add($"{field.Path}: String/Enum field must state Format or AllowedValues");
        }
    }

    private static void ValidateStringArray(ConfigFieldDescriptor field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(field.Format))
        {
            failures.Add($"{field.Path}: StringArray field must state Format");
        }

        ValidateCollectionCommon(field, "StringArray", failures);
    }

    private static void ValidateObjectArray(ConfigFieldDescriptor field, List<string> failures)
    {
        if (field.ItemFields.Count == 0 && string.IsNullOrWhiteSpace(field.Format))
        {
            failures.Add($"{field.Path}: ObjectArray field must expose ItemFields or Format");
        }

        ValidateCollectionCommon(field, "ObjectArray", failures);
        ValidateFields(field.ItemFields, failures);
    }

    private static void ValidateStringMap(ConfigFieldDescriptor field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(field.MapKeyFormat))
        {
            failures.Add($"{field.Path}: StringMap field must state MapKeyFormat");
        }

        if (string.IsNullOrWhiteSpace(field.MapValueFormat))
        {
            failures.Add($"{field.Path}: StringMap field must state MapValueFormat");
        }

        ValidateCollectionCommon(field, "StringMap", failures);
    }

    private static void ValidateCollectionCommon(ConfigFieldDescriptor field, string kindLabel, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(field.EmptyBehavior))
        {
            failures.Add($"{field.Path}: {kindLabel} field must state EmptyBehavior");
        }

        if (string.IsNullOrWhiteSpace(field.Example))
        {
            failures.Add($"{field.Path}: {kindLabel} field must provide Example");
        }
    }

    private static void ValidateNestedObject(ConfigFieldDescriptor field, List<string> failures)
    {
        if (field.NestedFields.Count == 0)
        {
            failures.Add($"{field.Path}: NestedObject field must contain nested fields");
            return;
        }

        ValidateFields(field.NestedFields, failures);
    }
}
