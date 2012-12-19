This is experimental and not well tested, use at your own risk!

NuGetine attempts to enforce a consistent set of package references in a sln+csprojs workspace. It rewrites all references to the highest version found and makes them all non version specific. *NuGetine requires the use of NuGet Package Restore.* It also requires that .csproj names match their directory and match their assembly output name.

NuGetine also fixes up several issues with NuGet.

- Projects have .nuspec files will have their dependencies sections updated to match the dependencies present as ProjectReferences if they depend on a project with a .nuspec file.
- NuGet package restore .targets file is updated to reference all package repos
- CsProj's reference $(SolutionDir)/packages (this allows for cross solution csproj references!)

Usage:

- Use `nugetine` to fix the .sln (in the current directory) and .csprojs (in subdirs).

Assumptions

- assemblies are produced by a csproj with the same name in a directory of the same name
  - e.g. ../${source}/${assembly_name}/${assembly_name}.csproj
- if no version is given 1.0 is assumed.
- Currently no error handling... use at your own risk, etc.

