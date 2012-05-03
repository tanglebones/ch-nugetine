using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using nugetine.Internal.Interface;

namespace nugetine.Internal
{
    internal class ReWriter : IReWriter
    {
        private const string FilePostFix = ""; //".x";

        private static readonly Regex RxReference =
            new Regex(
                @"<Reference\s+Include\s*=\s*""([^""]+)""\s*>(.*?)</Reference>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxProjectReference =
            new Regex(
                @"<ProjectReference\s+Include\s*=""([^""]+)""\s*(.*?)</ProjectReference>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxStartOfReferences =
            new Regex(
                @"<Reference",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxAnyItemGroup =
            new Regex(
                @"<ItemGroup>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxStartOfProjectReferences =
            new Regex(
                @"<ProjectReference",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxProjectGuid =
            new Regex(
                @"<ProjectGuid>([^<]+)<",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline
                );

        private static readonly Regex RxForeignSlnProjectEntry =
            new Regex(
                @"Project\(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}""\)\s*=\s*""([^""]+)"",\s*""..\\([^""]+)"",\s*""([^""]+)"".*?EndProject",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline
                );

        private static readonly Regex RxSlnProjectEnd =
            new Regex(
                @"EndProject",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline
                );

        private static readonly Regex RxNugetTargetsSources =
            new Regex(
                @"<PackageSources>([^<]+)</PackageSources>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled 
                );

        private readonly BsonDocument _assemblyMapping = new BsonDocument();
        private readonly BsonDocument _config = new BsonDocument();

        private readonly IDictionary<string, string> _csProjGuids =
            new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        private readonly TextWriter _out;

        private readonly ISet<string> _reference = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private readonly string _slnFile;
        private readonly ISet<string> _source = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private IEnumerable<string> _localCsProjs;

        public ReWriter(TextWriter @out, string slnFile)
        {
            _out = @out;
            _slnFile = slnFile;
        }

        private IEnumerable<BsonDocument> Packages
        {
            get
            {
                return _config["package"].AsBsonDocument.Select(
                    x =>
                    new BsonDocument
                        {
                            {"name", x.Name},
                            {"version", x.Value.AsBsonDocument["version"]}
                        }
                    );
            }
        }

        private string Source
        {
            get { return string.Join(";", _config["nuget"].AsBsonDocument.Values.Select(x => x.AsString)); }
        }

        public void LoadConfig(string path)
        {
            var source = File.ReadAllText(path);
            var doc = BsonDocument.Parse(source);
            _config.Overlay(doc);
        }

        public void Run()
        {
            Index();
            DoPackageInstallsAndUpdateGlobalPackagesConfig();
            RewriteCsProjs();
            RewriteSln();
            RewriteNugetTargets();
        }

        private void RewriteNugetTargets()
        {
            const string nugetTargetsFilename = ".nuget/NuGet.targets";
            if (!File.Exists(nugetTargetsFilename)) return;
            var contents = File.ReadAllText(nugetTargetsFilename);
            var newContents = RxNugetTargetsSources.Replace(
                contents,
                m => "<PackageSources>\"" + Source + "\"</PackageSources>",
                1);
            if (newContents!=contents)
                File.WriteAllText(nugetTargetsFilename, newContents);
        }

        private void Index()
        {
            if (_config.Contains("source"))
                foreach (var source in _config["source"].AsBsonArray)
                    _source.Add(source.AsString);

            foreach (var package in _config["package"].AsBsonDocument)
                foreach (var dir in package.Value.AsBsonDocument["assembly"].AsBsonDocument)
                    foreach (var assembly in dir.Value.AsBsonArray)
                    {
                        var assemblyName = assembly.AsString;
                        if (!_source.Contains(assemblyName))
                            _reference.Add(assemblyName);

                        var version = package.Value.AsBsonDocument["version", "1.0"].AsString;
                        var source = package.Value.AsBsonDocument["source", string.Empty].AsString;
                        var assemblyInfo = new BsonDocument();

                        assemblyInfo["path"] =
                            Path.Combine(
                                "..",
                                "packages",
                                package.Name + "." + version,
                                dir.Name.Replace('/', '\\'),
                                assemblyName + ".dll"
                                );
                        assemblyInfo["package"] = package.Name;
                        assemblyInfo["version"] = version;
                        assemblyInfo["source"] = source;

                        _assemblyMapping[assemblyName.ToUpperInvariant()] = assemblyInfo;
                    }

            IndexCsProjs();
        }

        private void RewriteCsProjs()
        {
            foreach (var csproj in _localCsProjs)
                ProcessCsProj(csproj);
        }

        [EnvironmentPermission(SecurityAction.LinkDemand, Unrestricted = true)]
        private void DoPackageInstallsAndUpdateGlobalPackagesConfig()
        {
            var globalPackageConfigSb = new StringBuilder();
            globalPackageConfigSb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            globalPackageConfigSb.AppendLine("<packages>");
            foreach (var package in Packages)
            {
                var name = package["name"].AsString;
                var version = package["version"].AsString;
                var arguments =
                    "install \"" + name + "\""
                    + " -Version " + version
                    + " -Source \"" + Source + "\""
                    + " -OutputDirectory packages";

                globalPackageConfigSb.AppendLine("  <package id=\"" + name + "\" version=\"" + version + "\" />");
                _out.WriteLine("nuget " + arguments);
                var process =
                    Process.Start(
                        new ProcessStartInfo("nuget") {UseShellExecute = false, Arguments = arguments}
                        );
                process.WaitForExit();
            }
            globalPackageConfigSb.AppendLine("</packages>");
            foreach (
                var globalPackagesConfigFileName in new[] {"packages.config", Path.Combine(".nuget", "packages.config")}
                )
            {
                var contents = globalPackageConfigSb.ToString();
                if (File.Exists(globalPackagesConfigFileName))
                {
                    File.WriteAllText(globalPackagesConfigFileName, contents);
                }
            }
        }

        private void RewriteSln()
        {
            var contents = File.ReadAllText(_slnFile);
            var toAddToSln = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var item in _source)
                toAddToSln.Add(item);

            var newSlnContents = RxForeignSlnProjectEntry
                .Replace(
                    contents,
                    match =>
                        {
                            var assemblyName = match.Groups[1].Value;
                            // keep those in use
                            if (toAddToSln.Contains(assemblyName))
                            {
                                toAddToSln.Remove(assemblyName);
                                return match.Value;
                            }
                            // remove all others
                            return string.Empty;
                        }
                );
            var projects =
                string.Join(
                    Environment.NewLine,
                    toAddToSln.Select(
                        x =>
                            {
                                var assemblyInfo = _assemblyMapping[x.ToUpperInvariant()].AsBsonDocument;
                                return MakeSlnProjectReference(x,
                                                               Path.Combine("..", assemblyInfo["source"].AsString, x,
                                                                            x + ".csproj"));
                            }
                        )
                    );
            newSlnContents = RxSlnProjectEnd
                .Replace(
                    newSlnContents,
                    match => "EndProject" + Environment.NewLine + projects,
                    1
                );
            File.WriteAllText(_slnFile + FilePostFix, newSlnContents);
        }

        private void ProcessCsProj(string csprojFileName)
        {
            var csprojDirectory = Path.GetDirectoryName(csprojFileName);
            if (csprojDirectory == null) throw new ArgumentException(csprojFileName);
            var csprojContents = File.ReadAllText(csprojFileName);
            var packages = new List<string>();
            var toAddToProjectReferences = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var toAddToReferences = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            // re-write project references
            var newCsprojContents = RxProjectReference.Replace(
                csprojContents,
                match =>
                    {
                        var include = match.Groups[1].Value;
                        include = Path.GetFileNameWithoutExtension(include);
                        if (include == null)
                            return match.Value;
                        if (_source.Contains(include))
                        {
                            return match.Value;
                        }
                        if (_reference.Contains(include))
                        {
                            toAddToReferences.Add(include);
                            return string.Empty;
                        }
                        return match.Value;
                    }
                );


            // re-write package references
            newCsprojContents = RxReference.Replace(
                newCsprojContents,
                match =>
                    {
                        var include = match.Groups[1].Value;
                        var splitIndex = include.IndexOf(',');
                        if (splitIndex > 0)
                            include = include.Substring(0, splitIndex);

                        // if it's in source schedule it to be added to the project refs and remove it
                        if (_source.Contains(include))
                        {
                            toAddToProjectReferences.Add(include);
                            return string.Empty;
                        }

                        // if it's in reference rewrite it
                        if (_reference.Contains(include))
                        {
                            var includeUpper = include.ToUpperInvariant();
                            var assemblyInfo = _assemblyMapping[includeUpper].AsBsonDocument;
                            var path = assemblyInfo["path"].AsString;
                            packages.Add("  <package id=\"" + assemblyInfo["package"].AsString + "\" version=\"" +
                                         assemblyInfo["version"].AsString + "\" />");
                            return MakeReferenceSection(path, include);
                        }

                        // leave everything else alone.
                        return match.Value;
                    }
                );

            // add references for removed project references
            if (toAddToReferences.Any())
            {
                var references =
                    string.Join(
                        Environment.NewLine + "    ",
                        toAddToReferences
                            .Select(
                                assemblyName
                                =>
                                    {
                                        var assemblyInfo =
                                            _assemblyMapping[assemblyName.ToUpperInvariant()].AsBsonDocument;
                                        var path = assemblyInfo["path"].AsString;
                                        packages.Add("  <package id=\"" + assemblyInfo["package"].AsString +
                                                     "\" version=\"" + assemblyInfo["version"].AsString + "\" />");
                                        return MakeReferenceSection(path, assemblyName);
                                    }
                            )
                        );

                if (RxStartOfReferences.IsMatch(newCsprojContents))
                {
                    newCsprojContents = RxStartOfReferences.Replace(
                        newCsprojContents,
                        match => references
                                 + Environment.NewLine
                                 + "    <Reference",
                        1
                        );
                }
                else
                {
                    newCsprojContents = RxAnyItemGroup.Replace(
                        newCsprojContents,
                        match => "<ItemGroup>"
                                 + Environment.NewLine + "    "
                                 + references
                                 + Environment.NewLine
                                 + "  </ItemGroup>"
                                 + "  <ItemGroup>",
                        1
                        );
                }
            }

            // add project references for removed references
            if (toAddToProjectReferences.Any())
            {
                var projectReferences =
                    String.Join(
                        Environment.NewLine + "    ",
                        toAddToProjectReferences.Select(
                            assemblyName =>
                                {
                                    var assemblyInfo = _assemblyMapping[assemblyName.ToUpperInvariant()].AsBsonDocument;
                                    var path = Path.Combine("..", "..", assemblyInfo["source"].AsString, assemblyName,
                                                            assemblyName + ".csproj");
                                    return MakeProjectReferenceSection(path, assemblyName);
                                }));
                if (RxStartOfProjectReferences.IsMatch(newCsprojContents))
                {
                    newCsprojContents = RxStartOfProjectReferences.Replace(
                        newCsprojContents,
                        match => projectReferences
                                 + Environment.NewLine
                                 + "    <ProjectReference",
                        1
                        );
                }
                else
                {
                    newCsprojContents = RxAnyItemGroup.Replace(
                        newCsprojContents,
                        match => "<ItemGroup>"
                                 + Environment.NewLine + "    "
                                 + projectReferences
                                 + Environment.NewLine
                                 + "  </ItemGroup>"
                                 + Environment.NewLine
                                 + "  <ItemGroup>",
                        1
                        );
                }
            }

            if (csprojContents != newCsprojContents)
            {
                File.WriteAllText(csprojFileName + FilePostFix, newCsprojContents);
            }

            var packagesConfigFileName = Path.Combine(csprojDirectory, "packages.config");
            var newPackagesConfigContents =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + Environment.NewLine
                + "<packages>"
                + Environment.NewLine
                + string.Join(Environment.NewLine, packages.RemoveDuplicatesOn(x => x))
                + Environment.NewLine
                + "</packages>"
                ;
            File.WriteAllText(packagesConfigFileName, newPackagesConfigContents);
        }

        private string MakeProjectReferenceSection(string path, string assemblyName)
        {
            var csProjGuid = _csProjGuids[assemblyName];

            return "<ProjectReference Include=\"" + path + "\">"
                   + Environment.NewLine
                   + "      <ProjectGuid>" + csProjGuid + "</ProjectGuid>"
                   + Environment.NewLine
                   + "      <Name>" + assemblyName + "</Name>"
                   + Environment.NewLine
                   + "    </ProjectReference>";
        }

        private static string MakeReferenceSection(string path, string assemblyName)
        {
            return "<Reference Include=\"" + assemblyName + "\">"
                   + Environment.NewLine
                   + "      <SpecificVersion>False</SpecificVersion>"
                   + Environment.NewLine
                   + "      <HintPath>" + path + "</HintPath>"
                   + Environment.NewLine
                   + "    </Reference>";
        }

        private void IndexCsProjs()
        {
            _localCsProjs = Directory.EnumerateFiles(".", "*.csproj", SearchOption.AllDirectories);
            foreach (var csProjFile in _localCsProjs)
            {
                IndexCsProj(csProjFile);
            }
            foreach (var source in _source)
            {
                var assemblyInfo = _assemblyMapping[source.ToUpperInvariant()].AsBsonDocument;
                IndexCsProj(Path.Combine("..", assemblyInfo["source"].AsString, source, source + ".csproj"));
            }
        }

        private void IndexCsProj(string csProjFile)
        {
            if (!File.Exists(csProjFile))
            {
                _out.WriteLine("Could not find: " + csProjFile);
                Environment.Exit(-1);
            }

            var contents = File.ReadAllText(csProjFile);
            var match = RxProjectGuid.Match(contents);
            if (match.Success)
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(csProjFile);
                if (fileNameWithoutExtension != null) _csProjGuids[fileNameWithoutExtension] = match.Groups[1].Value;
            }
            else
            {
                _out.WriteLine("No Project Guid in: " + csProjFile);
                Environment.Exit(-1);
            }
        }

        private string MakeSlnProjectReference(string assembly, string path)
        {
            var csProjGuid = _csProjGuids[assembly];
            return
                string.Format(
                    "Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{0}\", \"{1}\", \"{2}\"{3}EndProject{3}",
                    assembly, path, csProjGuid, Environment.NewLine);
        }

        public override string ToString()
        {
// ReSharper disable SpecifyACultureInStringConversionExplicitly
            return _config.ToString();
// ReSharper restore SpecifyACultureInStringConversionExplicitly
        }
    }
}