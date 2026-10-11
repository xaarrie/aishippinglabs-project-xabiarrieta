namespace ProjectTimeline.BusinessLogic;

public static class RoleCatalog
{
    public static IReadOnlySet<string> SelectableCodes { get; } = new HashSet<string>(StringComparer.Ordinal) { "Admin", "Consultant" };
}
