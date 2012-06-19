This is experimental and not well tested, use at your own risk!

NuGetine attempts to enforce a consistent set of package references in a sln+csprojs workspace. It rewrites all references specified by the nugetine.json configuration to one version requested and makes them all non version specific. *NuGetine requires the use of NuGet Package Restore.*

NuGetine also fixes up several issues with NuGet.

- Projects have .nuspec files will have their dependencies sections updated to match the dependencies present as specificed as ProjectReferences if they depend on a project with a .nuspec file.
- NuGet package restore .targets file is updated to reference all package repos
- CsProj's reference $(SolutionDir)/packages (this allows for cross solution csproj references!)

Usage:

- Use `nugetine -g` to create the .nugetine.json file for a .sln
- Use `nugetine` to fix the .sln and .csprojs

There is further experimental support for switching from a package reference to a project reference in a sibling workspace:

- A source_index.nugetine.json file can be created/updated with `nugetine -i` in the directory above your repos. (Create a nugetine.ingore file in any non-repo directories to exclude them.)
- Add the assembly name to the source:[] array in the .nugetine.json array and run `nugetine`. The csproj will now be references in the .sln
- Remove the assmbly name from the source:[] array in the .nugetine.json array and run `nugetine`. The csproj will no longer be referenced.

Assumptions

- assemblies are produced by a csproj with the same name in a directory of the same name
  - e.g. ../${source}/${assembly_name}/${assembly_name}.csproj
- if no version is given 1.0 is assumed.
- Currently no error handling... use at your own risk, etc.
- A source_index.nugetine.json file has to be created before using source references. This should be updated when new packages sources are added and should live in the directory where your repos are checked out.

.nugetine.json format:
{
  "nuget": {
    "madsrv01":"http://madsrv01/nuget/api/v2",
    "main":"https://nuget.org/api/v2/",
  },
  "package": {
    "mongocsharpdriver": {
      "version": "1.4.2",
      "assembly": {
        "lib/net35": [
          "MongoDB.Bson",
          "MongoDB.Driver",
        ],
      },
    },
    "NUnit": {
      "version": "2.6.0.12054",
      "assembly": {
        "lib": [
          "nunit.framework",
        ],
      },
    },
    "Your.Package": {
      "version": "1.0.3",
      "assembly": {
        "lib/net40": [
          "Your.Package",
        ],
      },
    },
  },
  "source": [
    "Your.Package",
  ]
}

TODO:

- much better error checking and reporting
- normalize the csproj and sln output to minimize diffs when switching between package refs and source refs and back again.
