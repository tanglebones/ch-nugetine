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
using nugetine.Internal.Templates;

namespace nugetine.Internal
{
    internal class ReWriter : IReWriter
    {
        private const string FilePostFix = ""; //".x";

        private static readonly Regex RxReference =
            new Regex(
                @"<Reference\s+Include\s*=\s*""([^""]+)""\s*(?:/>|>(.*?)</Reference>)",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxAssemblyBindingBlock =
            new Regex(
                @"<assemblyBinding(.*?)</assemblyBinding>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxItemGroupNoneInclude =
            new Regex(
                @"<ItemGroup>\s+(<None Include=.*?)</ItemGroup>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxProjectReference =
            new Regex(
                @"<ProjectReference\s+Include\s*=""([^""]+)""\s*(?:/>|>(.*?)</ProjectReference>)",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxStartOfReferences =
            new Regex(
                @"<Reference",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxStartOfAssemblyBinding =
            new Regex(
                @"<runtime>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxDependencies =
            new Regex(
                @"<dependencies>(.*?)</dependencies>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxDependency =
            new Regex(
                @"<dependency[^>]*?id=""([^""]+)""[^>]*?/>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxVersion =
            new Regex(
                @"<version>(.*?)</version>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxMetaData =
            new Regex(
                @"<metadata>(.*?)</metadata>",
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
                @"\bProject\b\(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}""\)\s*=\s*""([^""]+)"",\s*""..\\([^""]+)"",\s*""([^""]+)"".*?\bEndProject\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline
                );

        private static readonly Regex RxSlnProjectEnd =
            new Regex(
                @"\bEndProject\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline
                );

        private static readonly Regex RxNugetTargetsSourcesV18 =
            new Regex(
                @"(<ItemGroup\s+Condition=""\s+'\$\(PackageSources\)'\s*==\s*''\s*"">)(.*?)(</ItemGroup>)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline
                );

        private static readonly Regex RxNugetTargetsSourcesV17 =
            new Regex(
                @"<PackageSources>([^<]+)</PackageSources>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
                );

        private static readonly Regex RxSolutionDirHackInCsProj =
            new Regex(
                @"(<SolutionDir Condition=""\$\(SolutionDir\) == '' Or \$\(SolutionDir\) == '\*Undefined\*'"">)([^<]*)(</SolutionDir>)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled
                );

        private static readonly Regex RxEndOfProject =
            new Regex(
                @"\s+-->\s+</Project>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxItemGroupContentInclude =
            new Regex(
                @"<ItemGroup>\s+(<Content Include=.*?)</ItemGroup>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static string ParseTag(IEnumerable<string> tag)
        {
            var res = tag.Aggregate("", (current, line) => current + (line + Environment.NewLine));
            res = res.Substring(0, res.LastIndexOf(Environment.NewLine, StringComparison.Ordinal));
            return res;
        }

        private readonly BsonDocument _assemblyMapping = new BsonDocument();
        private readonly BsonDocument _config = new BsonDocument();

        private readonly IDictionary<string, string> _csProjGuids =
            new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        private readonly TextWriter _out;

        private readonly ISet<string> _reference = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private readonly string _slnFile;
        private readonly ISet<string> _source = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private IEnumerable<string> _localCsProjs;

        public ReWriter(TextWriter @out, string slnFile, BsonDocument config, BsonDocument sourceIndex)
        {
            _out = @out;
            _slnFile = slnFile;
            _sourceIndex = sourceIndex;
            _config = config;
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

        private IEnumerable<string> Sources
        {
            get { return _config["nuget"].AsBsonDocument.Values.Select(x => x.AsString); }
        } 
        private string Source
        {
            get { return string.Join(";", Sources); }
        }

        public void Run()
        {
            Index();
            DoPackageInstallsAndUpdateGlobalPackagesConfig();
            RewriteCsProjs();
            RewriteSln();
            RewriteNugetTargets();
            RewriteConfigs();
        }

        private void RewriteNugetTargets()
        {
            const string nugetTargetsFilename = ".nuget/NuGet.targets";
            if (!File.Exists(nugetTargetsFilename)) return;
            var contents = File.ReadAllText(nugetTargetsFilename);
            var newContents = RxNugetTargetsSourcesV17.Replace(
                contents,
                m => "<PackageSources>\"" + Source + "\"</PackageSources>",
                1);
            newContents = RxNugetTargetsSourcesV18.Replace(
                newContents,
                m => m.Groups[1].Value + Environment.NewLine +
                     string.Join(Environment.NewLine, Sources.Select(x => "<PackageSource Include=\"" + x + "\"/>")) + Environment.NewLine +
                     m.Groups[3].Value,
                1);
            if (newContents != contents)
                File.WriteAllText(nugetTargetsFilename, newContents);
        }

        private void Index()
        {
            if (_config.Contains("source"))
                foreach (var source in _config["source"].AsBsonArray)
                    _source.Add(source.AsString);

            var sourceBase = _sourceIndex["base"].AsString;
            foreach (var package in _config["package"].AsBsonDocument)
                foreach (var dir in package.Value.AsBsonDocument["assembly"].AsBsonDocument)
                    foreach (var assembly in dir.Value.AsBsonArray)
                    {
                        var assemblyName = assembly.AsString;
                        if (!_source.Contains(assemblyName))
                            _reference.Add(assemblyName);

                        var version = package.Value.AsBsonDocument["version", "1.0"].AsString;
                        var source = _sourceIndex["source"].AsBsonDocument[package.Name, string.Empty].AsString;
                        if (!string.IsNullOrEmpty(source)) source = Path.Combine(sourceBase, source);
                        var assemblyInfo = new BsonDocument();

                        assemblyInfo["path"] =
                            Path.Combine(
                                "$(SolutionDir)",
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

        private void RewriteConfigs()
        {
            foreach (var dir in _localCsProjs
                .Select(Path.GetDirectoryName)
                .Select(projectDir => Path.Combine(Directory.GetCurrentDirectory(), projectDir)))
            {
                ProcessAppConfig(dir);
                ProcessWebConfig(dir);
            }
        }

        private void ProcessAppConfig(string dir)
        {
            if (File.Exists(dir + @"\web.config") || File.Exists(dir + @"\Web.config")) return;
            ProcessConfig(dir, "App.config");
        }

        private void ProcessWebConfig(string dir)
        {
            if (File.Exists(dir + @"\app.config") || File.Exists(dir + @"\App.config")) return;
            ProcessConfig(dir, "Web.config");
        }

        private void ProcessConfig(string projectDir, string filename)
        {
            var lowerFilenamePath = projectDir + @"\" + char.ToLower(filename[0]) + filename.Substring(1);
            var filenamePath = projectDir + @"\" + filename;
            string content;
            var csProjFilename = GetCsProjFileName(projectDir);
            var directoryFiles = Directory.GetFiles(projectDir);
            if (directoryFiles.Contains(lowerFilenamePath) || directoryFiles.Contains(filenamePath))
            {
                if (directoryFiles.Contains(lowerFilenamePath))
                {
                    // Rename
                    File.Move(lowerFilenamePath, filenamePath);
                    var updatedCsProj = filename == "Web.config" 
                        ? AddIncludeToExistingItemGroup(File.ReadAllText(csProjFilename), filename, "Content", RxItemGroupContentInclude) 
                        : AddNoneTag(File.ReadAllText(csProjFilename), filename);
                    File.WriteAllText(csProjFilename, updatedCsProj);
                }

                content = File.ReadAllText(filenamePath);
            }
            else
            {
                // create new from template
                content = ParseTag(AppConfig.EmptyConfig);
            }
            //var modified = UpdateConfig(ref content);
            var modified = SetAssemblyContent(ref content);
            if (modified)
            {
                File.WriteAllText(filenamePath, content);
                var updatedCsProj = filename == "Web.config"
                        ? AddIncludeToExistingItemGroup(File.ReadAllText(csProjFilename), filename, "Content", RxItemGroupContentInclude)
                        : AddNoneTag(File.ReadAllText(csProjFilename), filename);
                File.WriteAllText(csProjFilename, updatedCsProj);
            }
        }

        private bool SetAssemblyContent(ref string content)
        {
            var result = new StringBuilder();
            foreach (var tag in from redirectItem in _config["assemblyBindingBlock"].AsBsonDocument 
                                let tag = ParseTag(AppConfig.DependentAssemblyBlock) 
                                let name = redirectItem.Name 
                                let version = redirectItem.Value.AsBsonDocument["version"].AsString 
                                let token = redirectItem.Value.AsBsonDocument["token"].AsString 
                                select tag.Replace("$NAME", name).Replace("$TOKEN", token).Replace("$NEW_VERSION", version))
            {
                result.Append(tag);
                result.Append(Environment.NewLine);
            }
            result.Length -= Environment.NewLine.Length;
            AddAssemblyBindingTagIfMissing(ref content);
            if (RxAssemblyBindingBlock.IsMatch(content))
            {
                content = RxAssemblyBindingBlock.Replace(
                    content,
                    match => "<assemblyBinding>" 
                        + Environment.NewLine 
                        + result.ToString()
                        + Environment.NewLine
                        + "    </assemblyBinding>",
                    1
                    );
            }
            return ParseTag(AppConfig.EmptyConfig) != content;
        }

        private void AddAssemblyBindingTagIfMissing(ref string content)
        {
            if (RxAssemblyBindingBlock.IsMatch(content)) return;
            var assemblyBindingTags = ParseTag(AppConfig.AssemblyBinding);
            if (RxStartOfAssemblyBinding.IsMatch(content))
            {
                content = RxStartOfAssemblyBinding.Replace(
                    content,
                    match => "<runtime>"
                             + Environment.NewLine
                             + assemblyBindingTags
                    );
            }
        }

        private string GetCsProjFileName(string projectDir)
        {
            return Directory.GetFiles(projectDir).FirstOrDefault(f => f.EndsWith(".csproj"));
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
                                                               Path.Combine(assemblyInfo["source"].AsString, x + ".csproj"));
                            }
                        )
                    );
            newSlnContents = RxSlnProjectEnd
                .Replace(
                    newSlnContents,
                    match => "EndProject" + projects,
                    1
                );
            File.WriteAllText(_slnFile + FilePostFix, newSlnContents);
        }

        private void ProcessCsProj(string csprojFileName)
        {
            var csprojDirectory = Path.GetDirectoryName(csprojFileName);
            if (csprojDirectory == null) throw new ArgumentException(csprojFileName);
            var csprojContents = File.ReadAllText(csprojFileName);
            var nuspecFile = Path.Combine(csprojDirectory, Path.GetFileNameWithoutExtension(csprojFileName) + ".nuspec");
            var nuspecFileExists = File.Exists(nuspecFile);
            var packages = new List<string>();
            var toAddToProjectReferences = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var toAddToReferences = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var projectRefs = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);


            // re-write project references
            var newCsprojContents = RewriteProjectReferences(csprojContents, nuspecFileExists, projectRefs, toAddToReferences);

            // re-write nuspec file if it exists
            RewriteNuspecReferences(nuspecFileExists, projectRefs, nuspecFile);

            // re-write nuget package restored SolutionDir hack to not depend on the check directory name
            newCsprojContents = FixSolutionDir(newCsprojContents);

            // re-write package references
            newCsprojContents = RewritePackageReferences(newCsprojContents, toAddToProjectReferences, packages);

            // Fix CodeAnalysis version dependency
            newCsprojContents = UpdateCodeAnalysisTag(newCsprojContents);

            // add references for removed project references
            if (toAddToReferences.Any())
            {
                var references =
                    string.Join(
                        Environment.NewLine + "    ",
                        toAddToReferences
                            .Select(
                                assemblyName
// ReSharper disable ImplicitlyCapturedClosure
                                =>
// ReSharper restore ImplicitlyCapturedClosure
                                    {
                                        var assemblyInfo =
                                            _assemblyMapping[assemblyName.ToUpperInvariant()].AsBsonDocument;
                                        var path = assemblyInfo["path"].AsString;
                                        packages.Add("  <package id=\"" + assemblyInfo["package"].AsString +
                                                     "\" version=\"" + assemblyInfo["version"].AsString + "\" />");
                                        return MakeReferenceSection(path.Replace("..\\packages", "$(SolutionDir)\\packages"), assemblyName);
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
                                    var path = Path.Combine(assemblyInfo["source"].AsString, assemblyName + ".csproj");
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

            // make sure packages.config is in the .csproj
            newCsprojContents = AddNoneTag(newCsprojContents, "packages.config");

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

        private string UpdateCodeAnalysisTag(string content)
        {
            const string oldRef = @"<RunCodeAnalysis>true</RunCodeAnalysis>";
            var newRef = @"<RunCodeAnalysis>true</RunCodeAnalysis>" 
                            + Environment.NewLine
                            + @"    <CodeAnalysisAdditionalOptions>/assemblyCompareMode:StrongNameIgnoringVersion</CodeAnalysisAdditionalOptions>";
            return content.Replace(oldRef, newRef);
        }

        private string RewritePackageReferences(string newCsprojContents, HashSet<string> toAddToProjectReferences, List<string> packages)
        {
            return RxReference.Replace(
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
                            return MakeReferenceSection(path.Replace("..\\packages","$(SolutionDir)\\packages"), include);
                        }

                        // leave everything else alone.
                        return match.Value;
                    }
                );
        }

        private static string FixSolutionDir(string newCsprojContents)
        {
            return RxSolutionDirHackInCsProj.Replace(
                newCsprojContents,
                match => match.Groups[1].Value + "..\\" + match.Groups[3].Value,
                1);
        }

        private void RewriteNuspecReferences(bool nuspecFileExists, HashSet<string> projectRefs, string nuspecFile)
        {
            if (nuspecFileExists)
            {
                if (projectRefs.Any())
                {
                    var nuspecContents = File.ReadAllText(nuspecFile);
                    string newNuspecContents;
                    if (RxDependencies.IsMatch(nuspecContents))
                    {
                        newNuspecContents = RxDependencies.Replace(
                            nuspecContents,
// ReSharper disable ImplicitlyCapturedClosure
                            match => "<dependencies>" +
// ReSharper restore ImplicitlyCapturedClosure
                                     Environment.NewLine +
                                     RemoveProjectRefs(match.Groups[1].Value, projectRefs) +
                                     MakeProjectDependencies(projectRefs) +
                                     Environment.NewLine +
                                     "</dependencies>"
                            );
                    }
                    else
                    {
                        newNuspecContents = RxMetaData.Replace(
                            nuspecContents,
// ReSharper disable ImplicitlyCapturedClosure
                            match => "<metadata>" +
// ReSharper restore ImplicitlyCapturedClosure
                                     match.Groups[1].Value +
                                     Environment.NewLine +
                                     "<dependencies>" +
                                     Environment.NewLine +
                                     MakeProjectDependencies(projectRefs) +
                                     Environment.NewLine +
                                     "</dependencies>" +
                                     Environment.NewLine +
                                     "</metadata>"
                            );
                    }

                    if (newNuspecContents != nuspecContents)
                    {
                        File.WriteAllText(nuspecFile, newNuspecContents);
                    }
                }
            }
        }

        private string RewriteProjectReferences(string csprojContents, bool nuspecFileExists, HashSet<string> projectRefs, HashSet<string> toAddToReferences)
        {
            return RxProjectReference.Replace(
                csprojContents,
                match =>
                    {
                        var includeDir = match.Groups[1].Value;
                        var include = Path.GetFileNameWithoutExtension(includeDir);
                        if (include == null)
                            return match.Value;

                        // project is a package and depends on a sibling project that is a package
                        if (nuspecFileExists && File.Exists(Path.Combine(include, include + ".nuspec")))
                            projectRefs.Add(include);

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
        }

        private static string AddIncludeToExistingItemGroup(string content, string include, string contentType, Regex rx)
        {
            content = content.Replace(include.ToLowerInvariant(), include);
            if (content.Contains("Include=\"" + include + "\""))
            {
                return content;
            }
            content = rx.Replace(
                content,
                match =>
                    {
                        var insideBlock = match.Groups[1].Value;
                        var result = "<ItemGroup>"
                                     + Environment.NewLine
                                     + "    "
                                     + insideBlock;
                        if (!insideBlock.Contains(include))
                        {
                            result += "  <" + contentType + " Include=\""
                                      + include + "\" />"
                                      + Environment.NewLine
                                      + "  ";
                        }
                        result += "</ItemGroup>";
                        return result;
                    },
                1
            );
            return content;
        }

        private string AddNoneTag(string content, string include)
        {
            if (content.Contains("Include=\"" + include + "\""))
            {
                return content;
            }
            // if there's already an include item, put it where it is
            if (RxItemGroupNoneInclude.IsMatch(content))
            {
                return AddIncludeToExistingItemGroup(content, include, "None", RxItemGroupNoneInclude);
            }

            // otherwise, put it in another ItemGroup at the end of the project
            content = RxEndOfProject.Replace(
                content,
                match =>
                    {
                        var result =
                        Environment.NewLine
                        + "  <ItemGroup>"
                        + Environment.NewLine
                        + "    <None Include=\"" + include + "\" />"
                        + Environment.NewLine
                        + "  </ItemGroup>"
                        + match.Groups[0].Value;
                        return result;
                    },
                1
            );
            return content;
        }

        private string RemoveProjectRefs(string value, ISet<string> projectRefs)
        {
            return RxDependency.Replace(
                value,
                match => projectRefs.Contains(match.Groups[1].Value) ? "" : match.Value
                );
        }

        private readonly IDictionary<string,string> _nuspecVersionCache = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        private readonly BsonDocument _sourceIndex;

        private string MakeProjectDependencies(IEnumerable<string> projectRefs)
        {
            var deps = from pref in projectRefs.OrderBy(x => x)
                       let version = _nuspecVersionCache
                           .GetOrAdd(
                               pref,
                               () =>
                                   {
// ReSharper disable AccessToModifiedClosure
                                       var contents = File.ReadAllText(Path.Combine(pref, pref + ".nuspec"));
// ReSharper restore AccessToModifiedClosure
                                       var match = RxVersion.Match(contents);
                                       return match.Groups[1].Value;
                                   })
                       select string.Format("<dependency id=\"{0}\" version=\"{1}\"/>", pref, version);
            return string.Join(Environment.NewLine, deps);
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
                IndexCsProj(Path.Combine(assemblyInfo["source"].AsString, source + ".csproj"));
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