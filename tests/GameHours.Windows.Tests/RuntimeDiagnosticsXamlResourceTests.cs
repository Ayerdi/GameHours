using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GameHours.Windows.Tests;

public sealed class RuntimeDiagnosticsXamlResourceTests
{
    [Fact]
    public void RuntimeDiagnosticsWindow_UsesOnlyGlobalStaticResourcesThatExist()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appXamlPath = Path.Combine(
            repositoryRoot,
            "src",
            "GameHours.Desktop",
            "App.xaml");
        var diagnosticsXamlPath = Path.Combine(
            repositoryRoot,
            "src",
            "GameHours.Desktop",
            "RuntimeDiagnosticsWindow.xaml");

        var xamlNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var globalKeys = XDocument.Load(appXamlPath)
            .Descendants()
            .Attributes(xamlNamespace + "Key")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);

        var diagnosticsXaml = File.ReadAllText(diagnosticsXamlPath);
        var referencedKeys = Regex.Matches(
                diagnosticsXaml,
                @"\{StaticResource\s+([^}\s]+)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(referencedKeys);
        Assert.All(referencedKeys, key => Assert.Contains(key, globalKeys));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameHours.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the GameHours repository root.");
    }
}
