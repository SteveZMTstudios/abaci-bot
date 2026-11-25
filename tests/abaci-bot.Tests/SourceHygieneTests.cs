using System.Text;
using Xunit;

namespace abaci_bot.Tests;

public class SourceHygieneTests
{
    private static readonly string[] ScanDirectories =
    [
        "Services",
        "Modules",
        "Pipelines",
        "Options",
        "Contexts",
        "docs"
    ];

    private static readonly string[] ScanFiles =
    [
        "Program.cs",
        "README.md",
        "appsettings.demo.jsonc"
    ];

    internal static bool IsDisallowedEmojiRune(Rune rune)
    {
        var cp = rune.Value;
        return (cp >= 0x1F300 && cp <= 0x1FAFF) ||
               (cp >= 0x2600 && cp <= 0x27BF) ||
               (cp >= 0x23E9 && cp <= 0x23FA) ||
               cp == 0x203C ||
               cp == 0x2049 ||
               cp == 0x2139 ||
               cp == 0x2B50 ||
               cp == 0x2B55 ||
               cp == 0xFE0F;
    }

    [Fact]
    public void RepositorySourceAndDocs_ContainZeroEmojiCharacters()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(repoRoot, "Program.cs")), $"Expected repo root at {repoRoot}");

        var filesToScan = new List<string>();
        foreach (var dir in ScanDirectories)
        {
            var fullDir = Path.Combine(repoRoot, dir);
            Assert.True(Directory.Exists(fullDir), $"Expected directory {fullDir}");
            filesToScan.AddRange(Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories));
        }

        foreach (var file in ScanFiles)
        {
            var fullFile = Path.Combine(repoRoot, file);
            Assert.True(File.Exists(fullFile), $"Expected file {fullFile}");
            filesToScan.Add(fullFile);
        }

        var violations = new List<string>();
        foreach (var filePath in filesToScan.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                foreach (var rune in lines[lineIdx].EnumerateRunes())
                {
                    if (IsDisallowedEmojiRune(rune))
                    {
                        var relPath = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
                        violations.Add($"{relPath}:{lineIdx + 1} (U+{rune.Value:X4})");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Repository source/docs must contain 0 emojis. Found: {string.Join(", ", violations)}");
    }

    [Fact]
    public void EmojiPredicate_RejectsEmojiCodepoints_AndAllowsBoxDrawingAndArrows()
    {
        // Known disallowed Emoji scalar values (Warning Sign, Info Source, Rocket, Variation Selector-16)
        Assert.True(IsDisallowedEmojiRune(new Rune(0x26A0)));
        Assert.True(IsDisallowedEmojiRune(new Rune(0x2139)));
        Assert.True(IsDisallowedEmojiRune(new Rune(0x1F680)));
        Assert.True(IsDisallowedEmojiRune(new Rune(0xFE0F)));

        // Allowed technical Unicode punctuation (Box Drawing in docs/deployment.md and ASCII/Unicode arrows)
        Assert.False(IsDisallowedEmojiRune(new Rune(0x251C)));
        Assert.False(IsDisallowedEmojiRune(new Rune(0x2500)));
        Assert.False(IsDisallowedEmojiRune(new Rune(0x2192)));
    }
}
