using System.Xml.Linq;

namespace AgentBridge.Core.Tests.Architecture;

public sealed class ProjectReferenceArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedReferences =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AgentBridge.Abstractions"] = [],
            ["AgentBridge.Core"] = ["AgentBridge.Abstractions"],
            ["AgentBridge.Infrastructure"] = ["AgentBridge.Abstractions", "AgentBridge.Core"],
            ["AgentBridge.UIAutomation"] = ["AgentBridge.Abstractions"],
            ["AgentBridge.Fakes"] = ["AgentBridge.Abstractions"],
            ["AgentBridge.App"] =
            [
                "AgentBridge.Abstractions",
                "AgentBridge.Core",
                "AgentBridge.Infrastructure",
                "AgentBridge.UIAutomation",
                "AgentBridge.Fakes",
            ],
        };

    [Fact]
    public void ProductionProjectReferences_RespectDeclaredDependencyDirection()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "src"),
            "*.csproj",
            SearchOption.AllDirectories);

        Assert.Equal(AllowedReferences.Count, projectFiles.Length);

        foreach (var projectFile in projectFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            Assert.True(AllowedReferences.TryGetValue(projectName, out var allowed),
                $"Production project '{projectName}' is not declared in the architecture policy.");

            var document = XDocument.Load(projectFile);
            var actualReferences = document
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFileNameWithoutExtension(value!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var forbidden = actualReferences.Except(allowed!, StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.True(forbidden.Length == 0,
                $"Project '{projectName}' has forbidden reference(s): {string.Join(", ", forbidden)}.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentBridge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing AgentBridge.slnx.");
    }
}
