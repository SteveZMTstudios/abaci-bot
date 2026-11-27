using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace abaci_bot.Tests;

public class DemoConfigurationOptionsTests
{
    private static readonly string DemoConfigPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "appsettings.demo.jsonc"));

    [Fact]
    public void AppSettingsDemoJsonc_Exists()
    {
        Assert.True(File.Exists(DemoConfigPath), $"appsettings.demo.jsonc must exist at {DemoConfigPath}");
    }

    [Fact]
    public void AllOptionsProperties_ArePresentInDemoConfig_AndMatchDefaultValues()
    {
        Assert.True(File.Exists(DemoConfigPath), $"appsettings.demo.jsonc must exist at {DemoConfigPath}");

        var json = File.ReadAllText(DemoConfigPath);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var root = doc.RootElement;

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(DemoConfigPath, optional: false, reloadOnChange: false)
            .Build();

        var rootOptionTypes = DiscoverRootOptionsTypes();

        foreach (var type in rootOptionTypes)
        {
            var sectionName = type.Name[..^"Options".Length];
            Assert.True(
                root.TryGetProperty(sectionName, out var sectionElement),
                $"Section '{sectionName}' for options type '{type.Name}' is missing from appsettings.demo.jsonc");

            var defaultInstance = Activator.CreateInstance(type)!;
            var boundInstance = Activator.CreateInstance(type)!;
            configuration.GetSection(sectionName).Bind(boundInstance);

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .ToList();

            foreach (var prop in properties)
            {
                var fullPath = $"{sectionName}:{prop.Name}";

                // 1. Assert property is explicitly present in appsettings.demo.jsonc
                Assert.True(
                    sectionElement.TryGetProperty(prop.Name, out var propElement),
                    $"Property '{fullPath}' is declared in '{type.Name}' but missing from appsettings.demo.jsonc");

                // 2. Assert property has default value (or explicit required placeholder) in appsettings.demo.jsonc
                var configField = prop.GetCustomAttribute<ConfigFieldAttribute>();
                var defaultValue = configField?.Placeholder ?? prop.GetValue(defaultInstance);

                if (defaultValue == null)
                {
                    Assert.True(
                        propElement.ValueKind == JsonValueKind.Null,
                        $"Property '{fullPath}' has null default value, but demo config specifies '{propElement}'");
                }
                else if (defaultValue is IEnumerable<string> expectedList)
                {
                    Assert.True(
                        propElement.ValueKind == JsonValueKind.Array,
                        $"Property '{fullPath}' is a collection, but demo config is {propElement.ValueKind}");

                    var actualList = propElement.EnumerateArray()
                        .Select(e => e.GetString())
                        .ToList();

                    Assert.Equal(expectedList, actualList);
                }
                else if (IsComplexType(prop.PropertyType))
                {
                    // Nested options objects have reference equality only; compare
                    // their serialized shape instead of the instances themselves.
                    var expectedJson = JsonSerializer.Serialize(defaultValue, prop.PropertyType);
                    var actualJson = JsonSerializer.Serialize(prop.GetValue(boundInstance), prop.PropertyType);
                    Assert.Equal(expectedJson, actualJson);
                }
                else
                {
                    var actualBoundValue = prop.GetValue(boundInstance);
                    Assert.Equal(defaultValue, actualBoundValue);
                }
            }
        }
    }

    [Fact]
    public void AllOptionsProperties_HaveCompleteConfigFieldMetadata()
    {
        var discoveredRootOptions = DiscoverRootOptionsTypes().ToHashSet();

        var catalogKnownRoots = ConfigSchemaCatalog.GetKnownRootOptionsTypes().ToHashSet();
        Assert.Equal(discoveredRootOptions, catalogKnownRoots);

        var sections = ConfigSchemaCatalog.BuildSections();
        var expectedSectionNames = discoveredRootOptions
            .Select(t => t.Name[..^"Options".Length])
            .Concat(["GitHubApp", "Kestrel"])
            .ToHashSet(StringComparer.Ordinal);
        var actualSectionNames = sections
            .Select(s => s.SectionName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedSectionNames, actualSectionNames);

        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(DemoConfigPath)!));
        var failures = new List<string>(ConfigSchemaValidator.Validate(sections));
        foreach (var section in sections)
        {
            if (section.DefaultsSource.StartsWith("Options/", StringComparison.Ordinal))
            {
                var defaultsPath = Path.Combine(repoRoot, section.DefaultsSource.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(defaultsPath))
                {
                    failures.Add($"{section.SectionName}: DefaultsSource '{section.DefaultsSource}' does not exist at {defaultsPath}");
                }
            }

            if (!string.IsNullOrWhiteSpace(section.ValidatorSource))
            {
                var validatorPath = Path.Combine(repoRoot, section.ValidatorSource.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(validatorPath))
                {
                    failures.Add($"{section.SectionName}: ValidatorSource '{section.ValidatorSource}' does not exist at {validatorPath}");
                }
            }
        }

        // Verify key validator bounds stay reflected in [ConfigField] metadata using whole-token matching
        var rebuildSection = Assert.Single(sections, s => s.SectionName == "RebuildImpact");
        var guideLinksField = Assert.Single(rebuildSection.Fields, f => f.Name == nameof(RebuildImpactOptions.DocumentationLinks));
        AssertMatchesWord(DocumentationLinkValidator.MaxDocumentationLinks, guideLinksField.Format);

        var titleItemField = Assert.Single(guideLinksField.ItemFields, f => f.Name == nameof(DocumentationLink.Title));
        AssertMatchesWord(DocumentationLinkValidator.MaxLinkTitleLength, titleItemField.Format);

        // Verify property-level XML <summary> prose is not duplicated alongside [ConfigField]
        var optionsDir = Path.Combine(repoRoot, "Options");
        foreach (var optionFile in Directory.GetFiles(optionsDir, "*Options.cs", SearchOption.TopDirectoryOnly))
        {
            var lines = File.ReadAllLines(optionFile);
            for (var i = 1; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"^\s*\[ConfigField\(") && lines[i - 1].TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    failures.Add($"{Path.GetFileName(optionFile)}:{i + 1}: [ConfigField] property must not duplicate property-level XML doc comments");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Incomplete or inconsistent [ConfigField] metadata found:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void GeneratedDemo_MatchesRepositoryFile()
    {
        var updateRequested = string.Equals(
            Environment.GetEnvironmentVariable("UPDATE_DEMO_CONFIG"),
            "1",
            StringComparison.Ordinal);
        if (updateRequested
            && (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Fail("UPDATE_DEMO_CONFIG=1 cannot be used inside CI runs; regenerate appsettings.demo.jsonc locally and commit the updated file.");
        }

        var generated = ConfigDemoGenerator.Generate().Replace("\r\n", "\n", StringComparison.Ordinal);

        // Parse check and safe placeholder verification independent of disk state
        using (var doc = JsonDocument.Parse(generated, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = false
        }))
        {
            Assert.True(doc.RootElement.TryGetProperty("GitHubApp", out var githubApp));
            Assert.Equal(123456, githubApp.GetProperty("AppId").GetInt32());
            Assert.Equal("webhook_secret", githubApp.GetProperty("WebhookSecret").GetString());
        }

        Assert.DoesNotContain("-----BEGIN RSA PRIVATE KEY-----\nMII", generated, StringComparison.Ordinal);

        if (updateRequested)
        {
            var tempPath = $"{DemoConfigPath}.tmp";
            File.WriteAllText(tempPath, generated, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, DemoConfigPath, overwrite: true);
        }

        // Scope DocumentationLinks item-field assertions to its contiguous comment block so adding
        // additional ObjectArray settings never deadlocks UPDATE_DEMO_CONFIG=1 regeneration.
        var generatedLines = generated.Split('\n');
        var guideLinksComments = ExtractPrecedingComments(generatedLines, "\"GeneralDocumentationLinks\":");
        Assert.Contains(
            guideLinksComments,
            l => l.Contains("Item field Title:", StringComparison.Ordinal)
                 && l.Contains("; required;", StringComparison.Ordinal)
                 && !l.Contains("default: \"\"", StringComparison.Ordinal));
        Assert.Contains(
            guideLinksComments,
            l => l.Contains("Item field Url:", StringComparison.Ordinal)
                 && l.Contains("; required;", StringComparison.Ordinal)
                 && !l.Contains("default: \"\"", StringComparison.Ordinal));

        Assert.True(File.Exists(DemoConfigPath), $"appsettings.demo.jsonc must exist at {DemoConfigPath}");
        var repoContent = File.ReadAllText(DemoConfigPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        if (!string.Equals(generated, repoContent, StringComparison.Ordinal))
        {
            var genLines = generated.Split('\n');
            var repoLines = repoContent.Split('\n');
            var firstDiff = -1;
            var max = Math.Min(genLines.Length, repoLines.Length);
            for (var i = 0; i < max; i++)
            {
                if (!string.Equals(genLines[i], repoLines[i], StringComparison.Ordinal))
                {
                    firstDiff = i + 1;
                    break;
                }
            }

            if (firstDiff < 0)
            {
                firstDiff = max + 1;
            }

            Assert.Fail(
                $"appsettings.demo.jsonc differs from ConfigDemoGenerator output (first difference at line {firstDiff}). " +
                "Regenerate via PowerShell (`$env:UPDATE_DEMO_CONFIG='1'; dotnet test --filter GeneratedDemo_MatchesRepositoryFile; Remove-Item Env:UPDATE_DEMO_CONFIG`) " +
                "or POSIX (`UPDATE_DEMO_CONFIG=1 dotnet test --filter GeneratedDemo_MatchesRepositoryFile`).");
        }
    }

    [Fact]
    public void Generate_RendersRequiredFieldsWithAndWithoutPlaceholder_MultilineComments_AndNonEmptyObjectArrays()
    {
        var customSections = new List<ConfigSectionDescriptor>
        {
            new(
                SectionName: "Sample",
                Title: "Sample generated section",
                DefaultsSource: "unit-test",
                ValidatorSource: null,
                Fields:
                [
                    new ConfigFieldDescriptor(
                        Name: "Token",
                        Path: "Sample:Token",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: "",
                        PlaceholderValue: "sample-token",
                        Description: "Line one\nLine two",
                        Format: "opaque string",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "empty fails validation.",
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
                        Name: "Endpoint",
                        Path: "Sample:Endpoint",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: null,
                        PlaceholderValue: null,
                        Description: "Required setting without placeholder.",
                        Format: "https URL",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "null disables integration.",
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
                        Name: "Links",
                        Path: "Sample:Links",
                        Kind: ConfigFieldKind.ObjectArray,
                        ClrType: typeof(List<DocumentationLink>),
                        DefaultValue: new List<DocumentationLink>
                        {
                            new() { Title = "Docs", Url = "https://example.org/docs" },
                        },
                        PlaceholderValue: null,
                        Description: "Sample links.",
                        Format: "JSON array",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "omitted when empty.",
                        Example: "[{ \"Title\": \"Docs\", \"Url\": \"https://example.org/docs\" }]",
                        Required: false,
                        RequiredWhen: null,
                        Secret: false,
                        Multiline: false,
                        MapKeyFormat: null,
                        MapValueFormat: null,
                        NestedFields: [],
                        ItemFields:
                        [
                            new ConfigFieldDescriptor(
                                Name: "Title",
                                Path: "Sample:Links[]:Title",
                                Kind: ConfigFieldKind.String,
                                ClrType: typeof(string),
                                DefaultValue: "",
                                PlaceholderValue: null,
                                Description: "Link title.",
                                Format: "single-line title",
                                Unit: null,
                                Range: null,
                                AllowedValues: null,
                                TrueBehavior: null,
                                FalseBehavior: null,
                                EmptyBehavior: null,
                                Example: "\"Docs\"",
                                Required: true,
                                RequiredWhen: null,
                                Secret: false,
                                Multiline: false,
                                MapKeyFormat: null,
                                MapValueFormat: null,
                                NestedFields: [],
                                ItemFields: []),
                        ]),
                    new ConfigFieldDescriptor(
                        Name: "Locales",
                        Path: "Sample:Locales",
                        Kind: ConfigFieldKind.StringMap,
                        ClrType: typeof(Dictionary<string, string>),
                        DefaultValue: new Dictionary<string, string>
                        {
                            ["org/repo"] = "zh",
                        },
                        PlaceholderValue: null,
                        Description: "Per-repo locale map.",
                        Format: null,
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "uses default locale.",
                        Example: "{ \"org/repo\": \"zh\" }",
                        Required: false,
                        RequiredWhen: null,
                        Secret: false,
                        Multiline: false,
                        MapKeyFormat: "owner/repo",
                        MapValueFormat: "en or zh",
                        NestedFields: [],
                        ItemFields: []),
                ]),
        };

        var output = ConfigDemoGenerator.Generate(customSections);

        Assert.Contains("    // Line one\n    // Line two.\n", output, StringComparison.Ordinal);
        Assert.Contains("    // Required: true (no code default; placeholder shown below).\n    \"Token\": \"sample-token\",", output, StringComparison.Ordinal);
        Assert.Contains("    // Required: true (no code default).\n    \"Endpoint\": null,", output, StringComparison.Ordinal);
        Assert.Contains("    // Item field Title: Link title; format: single-line title; required; example: \"Docs\".\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Item field Title: Link title; format: single-line title; default: \"\"", output, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(output, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = false
        });
        var sample = doc.RootElement.GetProperty("Sample");
        Assert.Equal("sample-token", sample.GetProperty("Token").GetString());
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("Endpoint").ValueKind);
        Assert.Equal("Docs", sample.GetProperty("Links")[0].GetProperty("Title").GetString());
        Assert.Equal("zh", sample.GetProperty("Locales").GetProperty("org/repo").GetString());
    }

    [Fact]
    public void Generate_ThrowsWhenSecretFieldHasNonEmptyDefaultValue()
    {
        var invalidSections = new List<ConfigSectionDescriptor>
        {
            new(
                SectionName: "Unsafe",
                Title: "Unsafe secret default",
                DefaultsSource: "unit-test",
                ValidatorSource: null,
                Fields:
                [
                    new ConfigFieldDescriptor(
                        Name: "ApiKey",
                        Path: "Unsafe:ApiKey",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: "hardcoded-secret",
                        PlaceholderValue: null,
                        Description: "Secret field.",
                        Format: "API key",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "required.",
                        Example: null,
                        Required: false,
                        RequiredWhen: null,
                        Secret: true,
                        Multiline: false,
                        MapKeyFormat: null,
                        MapValueFormat: null,
                        NestedFields: [],
                        ItemFields: []),
                ]),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigDemoGenerator.Generate(invalidSections));
        Assert.Contains("Unsafe:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigSchemaValidator_RejectsInvalidPlaceholderAndSecretMetadata()
    {
        var invalidSections = new List<ConfigSectionDescriptor>
        {
            new(
                SectionName: "Invalid",
                Title: "Invalid schema metadata",
                DefaultsSource: "unit-test",
                ValidatorSource: null,
                Fields:
                [
                    new ConfigFieldDescriptor(
                        Name: "AppId",
                        Path: "GitHubApp:AppId",
                        Kind: ConfigFieldKind.Integer,
                        ClrType: typeof(int),
                        DefaultValue: null,
                        PlaceholderValue: "not-an-int",
                        Description: "Numeric ID with mismatched CLR placeholder type.",
                        Format: "integer",
                        Unit: null,
                        Range: ">= 1",
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "aborts startup.",
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
                        Name: "OptionalWithPlaceholder",
                        Path: "Invalid:OptionalWithPlaceholder",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: "en",
                        PlaceholderValue: "zh",
                        Description: "Non-required field must not define PlaceholderValue.",
                        Format: "locale code",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "uses default.",
                        Example: null,
                        Required: false,
                        RequiredWhen: null,
                        Secret: false,
                        Multiline: false,
                        MapKeyFormat: null,
                        MapValueFormat: null,
                        NestedFields: [],
                        ItemFields: []),
                    new ConfigFieldDescriptor(
                        Name: "WhitespacePlaceholder",
                        Path: "Invalid:WhitespacePlaceholder",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: "",
                        PlaceholderValue: "   ",
                        Description: "Required field must not define blank PlaceholderValue.",
                        Format: "non-empty string",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "fails validation.",
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
                        Name: "LeakedSecret",
                        Path: "Invalid:LeakedSecret",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: "leaked",
                        PlaceholderValue: null,
                        Description: "Secret field with default.",
                        Format: "token",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "required.",
                        Example: null,
                        Required: false,
                        RequiredWhen: null,
                        Secret: true,
                        Multiline: false,
                        MapKeyFormat: null,
                        MapValueFormat: null,
                        NestedFields: [],
                        ItemFields: []),
                ]),
        };

        var failures = ConfigSchemaValidator.Validate(invalidSections);

        Assert.Contains(failures, f => f.Contains("GitHubApp:AppId", StringComparison.Ordinal) && f.Contains("not assignable", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Invalid:OptionalWithPlaceholder", StringComparison.Ordinal) && f.Contains("Required", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Invalid:WhitespacePlaceholder", StringComparison.Ordinal) && f.Contains("empty or whitespace", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Invalid:LeakedSecret", StringComparison.Ordinal) && f.Contains("Secret field", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_NonRequiredOrWhitespacePlaceholder_FallsBackToDefaultValueWithoutPlaceholderComment()
    {
        var syntheticSections = new List<ConfigSectionDescriptor>
        {
            new(
                SectionName: "EdgePlaceholders",
                Title: "Placeholder guard edge cases",
                DefaultsSource: "unit-test",
                ValidatorSource: null,
                Fields:
                [
                    new ConfigFieldDescriptor(
                        Name: "OptionalSetting",
                        Path: "EdgePlaceholders:OptionalSetting",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: "en",
                        PlaceholderValue: "zh",
                        Description: "Optional setting.",
                        Format: "locale code",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "uses default.",
                        Example: null,
                        Required: false,
                        RequiredWhen: null,
                        Secret: false,
                        Multiline: false,
                        MapKeyFormat: null,
                        MapValueFormat: null,
                        NestedFields: [],
                        ItemFields: []),
                    new ConfigFieldDescriptor(
                        Name: "BlankRequired",
                        Path: "EdgePlaceholders:BlankRequired",
                        Kind: ConfigFieldKind.String,
                        ClrType: typeof(string),
                        DefaultValue: "fallback",
                        PlaceholderValue: "   ",
                        Description: "Required setting with blank placeholder.",
                        Format: "token",
                        Unit: null,
                        Range: null,
                        AllowedValues: null,
                        TrueBehavior: null,
                        FalseBehavior: null,
                        EmptyBehavior: "aborts.",
                        Example: null,
                        Required: true,
                        RequiredWhen: null,
                        Secret: false,
                        Multiline: false,
                        MapKeyFormat: null,
                        MapValueFormat: null,
                        NestedFields: [],
                        ItemFields: []),
                ]),
        };

        var output = ConfigDemoGenerator.Generate(syntheticSections);

        Assert.Contains("\"OptionalSetting\": \"en\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"OptionalSetting\": \"zh\"", output, StringComparison.Ordinal);
        Assert.Contains("\"BlankRequired\": \"fallback\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder shown below", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigSchemaValidator_EnforcesStrictlyLosslessNumericWideningOnly()
    {
        static ConfigFieldDescriptor MakeNumericField(string path, Type clrType, object placeholder) => new(
            Name: path.Split(':')[^1],
            Path: path,
            Kind: clrType == typeof(long) ? ConfigFieldKind.Integer : ConfigFieldKind.Number,
            ClrType: clrType,
            DefaultValue: null,
            PlaceholderValue: placeholder,
            Description: "Numeric test field.",
            Format: null,
            Unit: "units",
            Range: "> 0",
            AllowedValues: null,
            TrueBehavior: null,
            FalseBehavior: null,
            EmptyBehavior: "aborts.",
            Example: null,
            Required: true,
            RequiredWhen: null,
            Secret: false,
            Multiline: false,
            MapKeyFormat: null,
            MapValueFormat: null,
            NestedFields: [],
            ItemFields: []);

        var validSections = new List<ConfigSectionDescriptor>
        {
            new(
                SectionName: "LosslessNumeric",
                Title: "Lossless numeric widenings",
                DefaultsSource: "unit-test",
                ValidatorSource: null,
                Fields:
                [
                    MakeNumericField("LosslessNumeric:IntToDouble", typeof(double), 1),
                    MakeNumericField("LosslessNumeric:IntToLong", typeof(long), 42),
                    MakeNumericField("LosslessNumeric:FloatToDouble", typeof(double), 1.5f),
                ]),
        };

        Assert.Empty(ConfigSchemaValidator.Validate(validSections));

        var invalidSections = new List<ConfigSectionDescriptor>
        {
            new(
                SectionName: "InvalidNumeric",
                Title: "Lossy or narrowing conversions rejected",
                DefaultsSource: "unit-test",
                ValidatorSource: null,
                Fields:
                [
                    MakeNumericField("InvalidNumeric:IntToFloat", typeof(float), 1),
                    MakeNumericField("InvalidNumeric:LongToDouble", typeof(double), 1L),
                    MakeNumericField("InvalidNumeric:DoubleToInt", typeof(int), 1.5d),
                ]),
        };

        var failures = ConfigSchemaValidator.Validate(invalidSections);
        Assert.Equal(3, failures.Count);
        Assert.Contains(failures, f => f.Contains("InvalidNumeric:IntToFloat", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("InvalidNumeric:LongToDouble", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("InvalidNumeric:DoubleToInt", StringComparison.Ordinal));
    }

    private static List<Type> DiscoverRootOptionsTypes()
    {
        var optionsAssembly = typeof(TargetClassificationOptions).Assembly;
        var optionsTypes = optionsAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "abaci_bot.Options" && t.Name.EndsWith("Options"))
            .OrderBy(t => t.Name)
            .ToList();

        Assert.NotEmpty(optionsTypes);

        // Nested option types (for example PackageUpdate:Lookout) are properties of
        // another options type and do not own a root-level section.
        var nestedOptionTypes = optionsTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.PropertyType))
            .ToHashSet();

        return optionsTypes
            .Where(t => !nestedOptionTypes.Contains(t))
            .ToList();
    }

    private static void AssertMatchesWord(int expectedNumber, string? text)
    {
        var token = expectedNumber.ToString(CultureInfo.InvariantCulture);
        Assert.True(
            Regex.IsMatch(text ?? string.Empty, $@"\b{Regex.Escape(token)}\b"),
            $"Expected whole numeric token '{token}' in '{text}'.");
    }

    private static IReadOnlyList<string> ExtractPrecedingComments(string[] lines, string propertyToken)
    {
        var propIndex = Array.FindIndex(lines, line => line.Contains(propertyToken, StringComparison.Ordinal));
        Assert.True(propIndex >= 0, $"Expected property token '{propertyToken}' in generated output.");

        var comments = new List<string>();
        for (var i = propIndex - 1; i >= 0 && lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal); i--)
        {
            comments.Add(lines[i]);
        }

        comments.Reverse();
        return comments;
    }

    private static bool IsComplexType(Type type)
    {
        return type != typeof(string)
               && !type.IsPrimitive
               && !type.IsEnum
               && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }
}
