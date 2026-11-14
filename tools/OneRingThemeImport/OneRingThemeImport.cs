// OneRing theme import: copies OneRing's theme token files, its CORA theme catalog and the fonts they use into
// Hymnal, so both desktop apps offer the same themes until they share an Eland.Themes package
// (OneRing/docs/superpowers/specs/2026-11-09-cross-app-theming-design.md, option B).
//
//   dotnet run tools/OneRingThemeImport/OneRingThemeImport.cs -- <onering checkout> [ref=origin/main]
//
// Reads, at that ref (so the checkout's working tree and branch don't matter):
//   src/OneRing.Desktop/Themes/Synthwave.Dark.axaml, Mordor.Dark.axaml   OneRing's own themes
//   src/OneRing.Desktop/Themes/Cora/*.axaml                              CORA's themes (generated in OneRing)
//   src/OneRing.Desktop/Services/CoraThemes.g.cs                         their catalog entries
//   src/OneRing.Desktop/Assets/Fonts/*                                   the bundled OFL faces
// Writes:
//   src/Hymnal/Themes/<Name>.axaml, src/Hymnal/Themes/Cora/<Name>.axaml  generated; edit OneRing or this tool
//   src/Hymnal/Theming/CoraThemes.g.cs
//   src/Hymnal/Assets/Fonts/*
//
// OneRing's branded themes (Foxen, Leading EDJE) and the faces only they use are not imported. OneRing's wording keys
// (Theme.Text.*) are dropped, since Hymnal has no sessions or worktrees to describe, except each theme's motto
// (Theme.Text.Motto), which Hymnal's status strip shows.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

var oneRing = args.Length > 0 ? args[0] : throw new ArgumentException("usage: OneRingThemeImport <onering checkout> [ref]");
var gitRef = args.Length > 1 ? args[1] : "origin/main";
var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory()) ?? FindRepoRoot(AppContext.BaseDirectory)
               ?? throw new InvalidOperationException("Run from inside the Hymnal repo.");
const string Desktop = "src/OneRing.Desktop";
string[] ownThemes = ["Synthwave.Dark", "Mordor.Dark"];
// Faces only the branded themes use
string[] brandOnlyFonts = ["LeagueSpartan-", "Mulish-", "Poppins-"];

var sha = Git(oneRing, $"rev-parse --short {gitRef}").Trim();
var themesDir = Path.Combine(repoRoot, "src", "Hymnal", "Themes");
var coraDir = Path.Combine(themesDir, "Cora");
Directory.CreateDirectory(coraDir);

foreach (var name in ownThemes)
    WriteTheme($"{Desktop}/Themes/{name}.axaml", Path.Combine(themesDir, name + ".axaml"));

// CORA's themes: every generated token file, never the hand-written Cora.Controls.axaml (Hymnal has its own)
foreach (var stale in Directory.GetFiles(coraDir, "*.axaml").Where(f => !f.EndsWith(".Controls.axaml"))) File.Delete(stale);
var coraFiles = Git(oneRing, $"ls-tree --name-only {gitRef} {Desktop}/Themes/Cora/")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Where(p => p.EndsWith(".axaml") && !p.EndsWith(".Controls.axaml"))
    .ToList();
foreach (var path in coraFiles) WriteTheme(path, Path.Combine(coraDir, Path.GetFileName(path)));

// The catalog entries, in Hymnal's namespace
var catalog = Git(oneRing, $"show {gitRef}:{Desktop}/Services/CoraThemes.g.cs");
catalog = Regex.Replace(catalog, @"// Written by tools/CoraTokenImport from (CORA \S+ \([^)]*\))\..*",
    $"// Written by OneRing's tools/CoraTokenImport from $1, then imported from OneRing {sha} by tools/OneRingThemeImport.\n// Re-run the importers instead of editing.");
// Hymnal has no implicit usings
catalog = catalog
    .Replace("namespace OneRing.Desktop.Services;", "using System;\nusing System.Collections.Generic;\n\nnamespace Hymnal.Theming;")
    .Replace("avares://OneRing/", "avares://Hymnal/");
File.WriteAllText(Path.Combine(repoRoot, "src", "Hymnal", "Theming", "CoraThemes.g.cs"), catalog, new UTF8Encoding(false));

// Fonts
var fontsDir = Path.Combine(repoRoot, "src", "Hymnal", "Assets", "Fonts");
Directory.CreateDirectory(fontsDir);
var fonts = Git(oneRing, $"ls-tree --name-only {gitRef} {Desktop}/Assets/Fonts/")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Where(p => !brandOnlyFonts.Any(b => Path.GetFileName(p).StartsWith(b, StringComparison.Ordinal)))
    .ToList();
foreach (var path in fonts) File.WriteAllBytes(Path.Combine(fontsDir, Path.GetFileName(path)), GitBytes(oneRing, $"show {gitRef}:{path}"));

Console.WriteLine($"Imported {ownThemes.Length + coraFiles.Count} themes and {fonts.Count} font files from OneRing {sha} ({gitRef}).");
return;

void WriteTheme(string source, string target)
{
    var lines = Git(oneRing, $"show {gitRef}:{source}").Replace("\r\n", "\n").Split('\n')
        .Where(l => !(l.Contains("x:Key=\"Theme.Text.") && !l.Contains("x:Key=\"Theme.Text.Motto\""))
                    && !l.TrimStart().StartsWith("<!-- Wording", StringComparison.Ordinal))
        .Select(l => l.Replace("avares://OneRing/", "avares://Hymnal/"));
    var header = $"<!-- Imported from OneRing {sha} ({source}) by tools/OneRingThemeImport. Re-run it rather than editing this file. -->";
    File.WriteAllText(target, header + "\n" + string.Join("\n", lines), new UTF8Encoding(false));
}

static string? FindRepoRoot(string start)
{
    for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Hymnal.slnx"))) return d.FullName;
    return null;
}

static string Git(string repo, string arguments) => Encoding.UTF8.GetString(GitBytes(repo, arguments));

static byte[] GitBytes(string repo, string arguments)
{
    var psi = new ProcessStartInfo("git", $"-C \"{repo}\" {arguments}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
    using var ms = new MemoryStream();
    var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
    var err = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    copy.Wait();
    if (p.ExitCode != 0) throw new InvalidOperationException($"git {arguments}: {err.Result.Trim()}");
    return ms.ToArray();
}
