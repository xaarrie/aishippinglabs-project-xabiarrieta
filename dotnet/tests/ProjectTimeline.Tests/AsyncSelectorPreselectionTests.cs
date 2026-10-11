using Xunit;

namespace ProjectTimeline.Tests;

public sealed class AsyncSelectorPreselectionTests
{
    [Fact]
    public void Shared_selector_controls_value_and_reapplies_it_when_async_options_arrive()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var source = File.ReadAllText(Path.Combine(root, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "src", "App.jsx"));
        var selector = source[source.IndexOf("function SelectField", StringComparison.Ordinal)..source.IndexOf("function RoleSelect", StringComparison.Ordinal)];

        Assert.Contains("useState(\"\")", selector);
        Assert.Contains("const userChanged = useRef(false)", selector);
        var effect = selector[selector.IndexOf("useEffect(() =>", StringComparison.Ordinal)..selector.IndexOf("return <label", StringComparison.Ordinal)];
        Assert.Contains("userChanged.current", effect);
        Assert.Contains("selectedValue !== \"\"", effect);
        Assert.Contains("value === undefined", effect);
        Assert.Contains("options.some(option => String(option.value) === String(value))", effect);
        Assert.Contains("setSelectedValue(String(value))", effect);
        Assert.Contains("value={selectedValue}", selector);
        Assert.Contains("userChanged.current = true", selector);
        Assert.Contains("setSelectedValue(event.target.value)", selector);
        Assert.Contains("onChange?.(event)", selector);
        Assert.Contains("<RoleSelect value={record.idUserRole} />", source);
    }

    private static string FindRepositoryRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "dotnet", "ProjectTimeline.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
