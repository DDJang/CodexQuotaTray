using System.Reflection;

namespace CodexQuotaTray.Core;

public static class ProductVersion
{
    public static string Current { get; } = ReadFrom(
        Assembly.GetEntryAssembly() ?? typeof(ProductVersion).Assembly);

    private static string ReadFrom(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return RemoveBuildMetadata(informationalVersion);
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }

    private static string RemoveBuildMetadata(string version)
    {
        var metadataIndex = version.IndexOf('+');
        return (metadataIndex < 0 ? version : version[..metadataIndex]).Trim();
    }
}
