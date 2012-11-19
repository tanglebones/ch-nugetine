/**
 * Because this is an AddIn, we can't read from files that aren't local to the solution running this code.
 */
namespace nugetine.Internal.Templates
{
    internal sealed class AppConfig
    {
        internal static string[] EmptyConfig =
            {
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<configuration>",
                "  <runtime>",
                "  </runtime>",
                "</configuration>"
            };

        internal static string[] AssemblyBinding =
            {
                "    <assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">",
                "    </assemblyBinding>"
            };

        internal static string[] DependentAssembly =
            {
                "<dependentAssembly>",
                "        <assemblyIdentity name=\"$NAME\" publicKeyToken=\"$TOKEN\" culture=\"neutral\" />",
                "        <bindingRedirect oldVersion=\"0.0.0.0-65535.65535.65535.65535\" newVersion=\"$NEW_VERSION\" />",
                "      </dependentAssembly>"
            };

        internal static string[] DependentAssemblyBlock =
            {
                "      <dependentAssembly>",
                "        <assemblyIdentity name=\"$NAME\" publicKeyToken=\"$TOKEN\" culture=\"neutral\" />",
                "        <bindingRedirect oldVersion=\"0.0.0.0-65535.65535.65535.65535\" newVersion=\"$NEW_VERSION\" />",
                "      </dependentAssembly>"
            };
    }
}
