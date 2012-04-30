Assumptions for sources

- source is the sibling directory of the solution being updated
- assemblies are produced by a csproj with the same name in a directory of the same name
- e.g. ../${source}/${assembly_name}/${assembly_name}.csproj
- if no version is given the assembly exists only as source
- if ../nugetine/master.nugetine.json exists it'll be loaded first and overriden by the local .nugetine.json