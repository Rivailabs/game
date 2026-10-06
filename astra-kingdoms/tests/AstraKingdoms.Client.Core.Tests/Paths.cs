namespace AstraKingdoms.Client.Tests;

internal static class Paths
{
    /// <summary>astra-kingdoms/ (found by walking up from the test binary to the folder holding AstraKingdoms.sln).</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AstraKingdoms.sln"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "AstraKingdoms.sln not found above the test directory");
            return dir.FullName;
        }
    }

    public static string Unity => Path.Combine(Root, "unity");
    public static string Localization => Path.Combine(Unity, "Assets", "Resources", "Localization");
    public static string Scripts => Path.Combine(Unity, "Assets", "Scripts");
}
