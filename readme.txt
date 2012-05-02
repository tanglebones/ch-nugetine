This is experimental and not well tested, use at your own risk!

NuGetine attempts to enforce a consistent set of package references in a sln+csprojs workspace. It rewrites all references specified by the nugetine.json configuration to one version requested and makes them all non version specific.

There is further experimental support for switching from a package reference to a project reference in a sibling workspace.

Assumptions

- foreign sources are in the sibling directory of the solution being updated (e.g. up one directory from sln file)
- assemblies are produced by a csproj with the same name in a directory of the same name
  - e.g. ../${source}/${assembly_name}/${assembly_name}.csproj
- if no version is given 1.0 is assumed.
- if multiple .nugetine.json files exist they'll be loaded and merged, overridden by the sln .nugetine.json
- Currently no error handling... use at your own risk, etc.

.nugetine.json format:
// the comments should be removed...
{
  // nuget servers
  "nuget": {
    "madsrv01":"http://madsrv01/nuget/api/v2",
    "main":"https://nuget.org/api/v2/",
  },

  // packages
  "package": {
    "mongocsharpdriver": {
      "version": "1.4.2",
      "assembly": {
		// directory in package install
        "lib/net35": [
	      // assembly name in directory
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
	...
	"Your.Package": {
      "version": "1.0.3",
      "assembly": {
        "lib/net40": [
          "Your.Package",
        ],
      },
	  // if used in the "source" section below this is the directory at $(SolutionDir)/../ that the source for the package is checked out into.
      "source": "your-package",
    },
	...
  },

  // packages to configure as source references instead of package references
  // very experimental!
  "source": [
  ]
}

TODO

- much better error checking and reporting
- a "glean" mode that attempts to create a .nugetine.json configuration based on an existing workspace

