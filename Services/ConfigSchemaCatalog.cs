using System.Collections;
using System.Reflection;
using abaci_bot.Options;

namespace abaci_bot.Services;

/// <summary>
/// Single in-memory catalog of all configuration sections and field descriptors.
/// Combines explicit descriptors for non-Options sections (<c>GitHubApp</c>, <c>Kestrel</c>)
/// with reflection over <see cref="Options"/> types annotated with <see cref="ConfigFieldAttribute"/>.
/// </summary>
internal static class ConfigSchemaCatalog
{
    private sealed record OptionsSectionSpec(
        Type OptionsType,
        string SectionName,
        string Title,
        string DefaultsSource,
        string? ValidatorSource);

    private static readonly IReadOnlyList<OptionsSectionSpec> RootOptionsSpecs =
    [
        new(
            typeof(TargetClassificationOptions),
            "TargetClassification",
            "Target branch classification",
            "Options/TargetClassificationOptions.cs",
            null),
        new(
            typeof(ChangeTypeClassificationOptions),
            "ChangeTypeClassification",
            "Change type classification",
            "Options/ChangeTypeClassificationOptions.cs",
            null),
        new(
            typeof(StaleLifecycleOptions),
            "StaleLifecycle",
            "Stale lifecycle worker",
            "Options/StaleLifecycleOptions.cs",
            "Options/StaleLifecycleOptionsValidator.cs"),
        new(
            typeof(BotDetectionOptions),
            "BotDetection",
            "Bot detection and triage whitelist",
            "Options/BotDetectionOptions.cs",
            null),
        new(
            typeof(SpdxComplianceOptions),
            "SpdxCompliance",
            "SPDX license compliance",
            "Options/SpdxComplianceOptions.cs",
            null),
        new(
            typeof(CveSecurityOptions),
            "CveSecurity",
            "CVE detection and lifecycle",
            "Options/CveSecurityOptions.cs",
            null),
        new(
            typeof(RebuildImpactOptions),
            "RebuildImpact",
            "Rebuild impact analysis",
            "Options/RebuildImpactOptions.cs",
            "Options/RebuildImpactOptionsValidator.cs"),
        new(
            typeof(AbiComplianceOptions),
            "AbiCompliance",
            "Binary ABI compliance",
            "Options/AbiComplianceOptions.cs",
            null),
        new(
            typeof(FileCacheOptions),
            "FileCache",
            "In-memory file cache",
            "Options/FileCacheOptions.cs",
            null),
        new(
            typeof(PackageUpdateOptions),
            "PackageUpdate",
            "Automated upstream package update background service",
            "Options/PackageUpdateOptions.cs",
            "Options/PackageUpdateOptionsValidator.cs"),
        new(
            typeof(CveActiveWatchOptions),
            "CveActiveWatch",
            "Active repository-wide CVE vulnerability watch service",
            "Options/CveActiveWatchOptions.cs",
            "Options/CveActiveWatchOptionsValidator.cs"),
        new(
            typeof(MilestoneSyncOptions),
            "MilestoneSync",
            "Automated milestone assignment and backfill sweep service",
            "Options/MilestoneSyncOptions.cs",
            "Options/MilestoneSyncOptionsValidator.cs"),
        new(
            typeof(ConversationGuardOptions),
            "ConversationGuard",
            "Conversation locking, mention command moderation, and inactive PR sweep",
            "Options/ConversationGuardOptions.cs",
            "Options/ConversationGuardOptionsValidator.cs"),
        new(
            typeof(BotStateOptions),
            "BotState",
            "Unified persistent runtime state and localization policy",
            "Options/BotStateOptions.cs",
            "Options/BotStateOptionsValidator.cs"),
    ];

    /// <summary>
    /// Returns the ordered list of root <c>*Options</c> CLR types known to the catalog.
    /// </summary>
    public static IReadOnlyList<Type> GetKnownRootOptionsTypes()
    {
        return RootOptionsSpecs.Select(spec => spec.OptionsType).ToList();
    }

    /// <summary>
    /// Builds the complete, deterministically ordered configuration schema across
    /// non-Options sections and all root <c>*Options</c> sections.
    /// </summary>
    public static IReadOnlyList<ConfigSectionDescriptor> BuildSections()
    {
        var sections = new List<ConfigSectionDescriptor>(RootOptionsSpecs.Count + 2)
        {
            BuildGitHubAppSection(),
            BuildKestrelSection(),
        };

        foreach (var spec in RootOptionsSpecs)
        {
            sections.Add(BuildSectionFromSpec(spec));
        }

        return sections;
    }

    private static ConfigSectionDescriptor BuildSectionFromSpec(OptionsSectionSpec spec)
    {
        var defaultInstance = Activator.CreateInstance(spec.OptionsType);
        var fields = BuildFieldsForType(spec.OptionsType, defaultInstance, spec.SectionName);
        return new ConfigSectionDescriptor(
            spec.SectionName,
            spec.Title,
            spec.DefaultsSource,
            spec.ValidatorSource,
            fields);
    }

    private static IReadOnlyList<ConfigFieldDescriptor> BuildFieldsForType(
        Type type,
        object? defaultInstance,
        string parentPath)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.MetadataToken)
            .ToList();

        var descriptors = new List<ConfigFieldDescriptor>(properties.Count);

        foreach (var prop in properties)
        {
            var path = $"{parentPath}:{prop.Name}";
            var attr = prop.GetCustomAttribute<ConfigFieldAttribute>(inherit: true);
            var kind = ClassifyFieldKind(prop.PropertyType, out var elementOrValueType);
            var defaultValue = defaultInstance != null ? prop.GetValue(defaultInstance) : null;

            IReadOnlyList<ConfigFieldDescriptor> nestedFields = [];
            IReadOnlyList<ConfigFieldDescriptor> itemFields = [];

            if (kind == ConfigFieldKind.NestedObject)
            {
                var nestedInstance = defaultValue ?? TryCreateInstance(prop.PropertyType);
                nestedFields = BuildFieldsForType(prop.PropertyType, nestedInstance, path);
            }
            else if (kind == ConfigFieldKind.ObjectArray && elementOrValueType != null)
            {
                var sampleItem = TryCreateInstance(elementOrValueType);
                itemFields = BuildFieldsForType(elementOrValueType, sampleItem, $"{path}[]");
            }

            var allowedValues = attr?.AllowedValues;
            if (kind == ConfigFieldKind.Enum && string.IsNullOrWhiteSpace(allowedValues))
            {
                var effectiveEnumType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                allowedValues = string.Join(", ", Enum.GetNames(effectiveEnumType));
            }

            descriptors.Add(new ConfigFieldDescriptor(
                Name: prop.Name,
                Path: path,
                Kind: kind,
                ClrType: prop.PropertyType,
                DefaultValue: defaultValue,
                PlaceholderValue: attr?.Placeholder,
                Description: attr?.Description,
                Format: attr?.Format,
                Unit: attr?.Unit,
                Range: attr?.Range,
                AllowedValues: allowedValues,
                TrueBehavior: attr?.TrueBehavior,
                FalseBehavior: attr?.FalseBehavior,
                EmptyBehavior: attr?.EmptyBehavior,
                Example: attr?.Example,
                Required: attr?.Required ?? false,
                RequiredWhen: attr?.RequiredWhen,
                Secret: attr?.Secret ?? false,
                Multiline: attr?.Multiline ?? false,
                MapKeyFormat: attr?.MapKeyFormat,
                MapValueFormat: attr?.MapValueFormat,
                NestedFields: nestedFields,
                ItemFields: itemFields));
        }

        return descriptors;
    }

    internal static ConfigFieldKind ClassifyFieldKind(Type propertyType, out Type? elementOrValueType)
    {
        elementOrValueType = null;
        var effectiveType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (effectiveType == typeof(bool))
        {
            return ConfigFieldKind.Boolean;
        }

        if (effectiveType == typeof(int)
            || effectiveType == typeof(long)
            || effectiveType == typeof(short)
            || effectiveType == typeof(byte)
            || effectiveType == typeof(uint)
            || effectiveType == typeof(ulong)
            || effectiveType == typeof(ushort)
            || effectiveType == typeof(sbyte))
        {
            return ConfigFieldKind.Integer;
        }

        if (effectiveType == typeof(double)
            || effectiveType == typeof(float)
            || effectiveType == typeof(decimal))
        {
            return ConfigFieldKind.Number;
        }

        if (effectiveType.IsEnum)
        {
            return ConfigFieldKind.Enum;
        }

        if (effectiveType == typeof(string))
        {
            return ConfigFieldKind.String;
        }

        var dictionaryInterface = FindGenericInterface(effectiveType, typeof(IDictionary<,>))
            ?? FindGenericInterface(effectiveType, typeof(IReadOnlyDictionary<,>));
        if (dictionaryInterface != null)
        {
            elementOrValueType = dictionaryInterface.GetGenericArguments()[1];
            return ConfigFieldKind.StringMap;
        }

        var enumerableInterface = FindGenericInterface(effectiveType, typeof(IEnumerable<>));
        if (enumerableInterface != null)
        {
            var itemType = enumerableInterface.GetGenericArguments()[0];
            elementOrValueType = itemType;
            return itemType == typeof(string)
                ? ConfigFieldKind.StringArray
                : ConfigFieldKind.ObjectArray;
        }

        if (typeof(IEnumerable).IsAssignableFrom(effectiveType))
        {
            return ConfigFieldKind.StringArray;
        }

        return ConfigFieldKind.NestedObject;
    }

    private static Type? FindGenericInterface(Type type, Type genericInterfaceDefinition)
    {
        if (type.IsInterface
            && type.IsGenericType
            && type.GetGenericTypeDefinition() == genericInterfaceDefinition)
        {
            return type;
        }

        return type.GetInterfaces().FirstOrDefault(
            iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == genericInterfaceDefinition);
    }

    private static object? TryCreateInstance(Type type)
    {
        try
        {
            return Activator.CreateInstance(type);
        }
        catch (MissingMethodException)
        {
            return null;
        }
    }

    private static ConfigSectionDescriptor BuildGitHubAppSection()
    {
        return new ConfigSectionDescriptor(
            SectionName: "GitHubApp",
            Title: "GitHub App identity and webhook authentication",
            DefaultsSource: "none (validated by Program.cs at startup; no code defaults)",
            ValidatorSource: null,
            Fields:
            [
                new ConfigFieldDescriptor(
                    Name: "AppId",
                    Path: "GitHubApp:AppId",
                    Kind: ConfigFieldKind.Integer,
                    ClrType: typeof(int),
                    DefaultValue: null,
                    PlaceholderValue: 123456,
                    Description: "GitHub App numeric application ID.",
                    Format: "positive GitHub App integer ID",
                    Unit: null,
                    Range: ">= 1",
                    AllowedValues: null,
                    TrueBehavior: null,
                    FalseBehavior: null,
                    EmptyBehavior: "missing or 0 causes startup abort.",
                    Example: null,
                    Required: true,
                    RequiredWhen: null,
                    Secret: false,
                    Multiline: false,
                    MapKeyFormat: null,
                    MapValueFormat: null,
                    NestedFields: [],
                    ItemFields: []),
                new ConfigFieldDescriptor(
                    Name: "InstallationId",
                    Path: "GitHubApp:InstallationId",
                    Kind: ConfigFieldKind.Integer,
                    ClrType: typeof(long),
                    DefaultValue: null,
                    PlaceholderValue: 78901234L,
                    Description: "GitHub App installation ID for the target repository or organization.",
                    Format: "positive GitHub App installation ID",
                    Unit: null,
                    Range: ">= 1",
                    AllowedValues: null,
                    TrueBehavior: null,
                    FalseBehavior: null,
                    EmptyBehavior: "missing or 0 causes startup abort; environment override GitHubApp__InstallationId takes precedence.",
                    Example: null,
                    Required: true,
                    RequiredWhen: null,
                    Secret: false,
                    Multiline: false,
                    MapKeyFormat: null,
                    MapValueFormat: null,
                    NestedFields: [],
                    ItemFields: []),
                new ConfigFieldDescriptor(
                    Name: "PrivateKey",
                    Path: "GitHubApp:PrivateKey",
                    Kind: ConfigFieldKind.String,
                    ClrType: typeof(string),
                    DefaultValue: null,
                    PlaceholderValue: "-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----",
                    Description: "GitHub App RSA private key used to sign installation access tokens.",
                    Format: "PEM-encoded RSA private key (literal \"\\n\" sequences are converted to newlines at startup)",
                    Unit: null,
                    Range: null,
                    AllowedValues: null,
                    TrueBehavior: null,
                    FalseBehavior: null,
                    EmptyBehavior: "missing or unparsable value aborts startup.",
                    Example: null,
                    Required: true,
                    RequiredWhen: null,
                    Secret: true,
                    Multiline: true,
                    MapKeyFormat: null,
                    MapValueFormat: null,
                    NestedFields: [],
                    ItemFields: []),
                new ConfigFieldDescriptor(
                    Name: "WebhookSecret",
                    Path: "GitHubApp:WebhookSecret",
                    Kind: ConfigFieldKind.String,
                    ClrType: typeof(string),
                    DefaultValue: null,
                    PlaceholderValue: "webhook_secret",
                    Description: "Shared HMAC secret used to verify incoming GitHub webhook payloads.",
                    Format: "the secret configured on the GitHub App webhook",
                    Unit: null,
                    Range: null,
                    AllowedValues: null,
                    TrueBehavior: null,
                    FalseBehavior: null,
                    EmptyBehavior: "missing aborts startup; wrong value rejects every webhook delivery with HTTP 400.",
                    Example: null,
                    Required: true,
                    RequiredWhen: null,
                    Secret: true,
                    Multiline: false,
                    MapKeyFormat: null,
                    MapValueFormat: null,
                    NestedFields: [],
                    ItemFields: []),
                new ConfigFieldDescriptor(
                    Name: "TeamName",
                    Path: "GitHubApp:TeamName",
                    Kind: ConfigFieldKind.String,
                    ClrType: typeof(string),
                    DefaultValue: null,
                    PlaceholderValue: "captains",
                    Description: "GitHub organization team slug whose members hold captain privileges.",
                    Format: "organization team slug",
                    Unit: null,
                    Range: null,
                    AllowedValues: null,
                    TrueBehavior: null,
                    FalseBehavior: null,
                    EmptyBehavior: "missing aborts startup; unknown team causes captain-only transitions to fail.",
                    Example: null,
                    Required: true,
                    RequiredWhen: null,
                    Secret: false,
                    Multiline: false,
                    MapKeyFormat: null,
                    MapValueFormat: null,
                    NestedFields: [],
                    ItemFields: []),
            ]);
    }

    private static ConfigSectionDescriptor BuildKestrelSection()
    {
        return new ConfigSectionDescriptor(
            SectionName: "Kestrel",
            Title: "HTTP server (Kestrel)",
            DefaultsSource: "ASP.NET Core defaults, overridden by ASPNETCORE_HTTP_PORTS and the Docker image (port 3456)",
            ValidatorSource: null,
            Fields:
            [
                new ConfigFieldDescriptor(
                    Name: "Endpoints",
                    Path: "Kestrel:Endpoints",
                    Kind: ConfigFieldKind.NestedObject,
                    ClrType: typeof(object),
                    DefaultValue: null,
                    PlaceholderValue: null,
                    Description: "Named Kestrel endpoint bindings.",
                    Format: null,
                    Unit: null,
                    Range: null,
                    AllowedValues: null,
                    TrueBehavior: null,
                    FalseBehavior: null,
                    EmptyBehavior: null,
                    Example: null,
                    Required: false,
                    RequiredWhen: null,
                    Secret: false,
                    Multiline: false,
                    MapKeyFormat: null,
                    MapValueFormat: null,
                    ItemFields: [],
                    NestedFields:
                    [
                        new ConfigFieldDescriptor(
                            Name: "Http",
                            Path: "Kestrel:Endpoints:Http",
                            Kind: ConfigFieldKind.NestedObject,
                            ClrType: typeof(object),
                            DefaultValue: null,
                            PlaceholderValue: null,
                            Description: "Primary HTTP webhook listener endpoint.",
                            Format: null,
                            Unit: null,
                            Range: null,
                            AllowedValues: null,
                            TrueBehavior: null,
                            FalseBehavior: null,
                            EmptyBehavior: null,
                            Example: null,
                            Required: false,
                            RequiredWhen: null,
                            Secret: false,
                            Multiline: false,
                            MapKeyFormat: null,
                            MapValueFormat: null,
                            ItemFields: [],
                            NestedFields:
                            [
                                new ConfigFieldDescriptor(
                                    Name: "Url",
                                    Path: "Kestrel:Endpoints:Http:Url",
                                    Kind: ConfigFieldKind.String,
                                    ClrType: typeof(string),
                                    DefaultValue: "http://0.0.0.0:3456",
                                    PlaceholderValue: null,
                                    Description: "Listening URL bound by Kestrel for webhook HTTP traffic.",
                                    Format: "scheme://host:port",
                                    Unit: null,
                                    Range: null,
                                    AllowedValues: null,
                                    TrueBehavior: null,
                                    FalseBehavior: null,
                                    EmptyBehavior: "0.0.0.0 is reachable through container port mapping; 127.0.0.1 is loopback-only. Defaults to http://0.0.0.0:3456 in container.",
                                    Example: null,
                                    Required: false,
                                    RequiredWhen: null,
                                    Secret: false,
                                    Multiline: false,
                                    MapKeyFormat: null,
                                    MapValueFormat: null,
                                    NestedFields: [],
                                    ItemFields: []),
                            ]),
                    ]),
            ]);
    }
}
