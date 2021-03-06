using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using nugetine.Internal.Interface;

namespace nugetine.Internal
{
    internal class Gleaner : IGleaner
    {
        private static readonly Regex RxReferenceWithHintPath =
            new Regex(
                @"<Reference\s+Include\s*=\s*""([^""]+)""\s*>.*?<HintPath>[^<]*?packages[/\\]([^<]+)</HintPath>.*?</Reference>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxPackageSources =
            new Regex(
                @"<packageSources>(.*?)</packageSources>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private static readonly Regex RxAddKey =
            new Regex(
                @"<add\s+key=""([^""]+)""\s+value=""([^""]*)""\s*/>",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );

        private readonly TextWriter _out;

        private readonly Classifier _classifier = new Classifier();
        private readonly string _slnPrefix;

        private readonly JsonWriterSettings _settings =
            new JsonWriterSettings(
                true,
                Encoding.UTF8,
                GuidRepresentation.Standard,
                true,
                " ",
                Environment.NewLine,
                JsonOutputMode.Strict,
                new Version(1, 0)
                );

        public Gleaner(TextWriter @out, string slnPrefix)
        {
            _out = @out;
            _slnPrefix = slnPrefix;
        }

        public BsonDocument Run()
        {
            var csprojFiles = Directory.EnumerateFiles(".", "*.csproj", SearchOption.AllDirectories).ToSet();

            var packageRefs = new HashSet<PackageInfo>(PackageInfo.Comparer);
            var assemblies = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            var packageSources = new List<Tuple<string,string>>();

            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (appData != null)
            {
                try
                {
                    var nugetConfig = File.ReadAllText(Path.Combine(appData, "NuGet", "NuGet.Config"));
                    packageSources.AddRange(from Match psm in RxPackageSources.Matches(nugetConfig)
                                            from Match ak in RxAddKey.Matches(psm.Groups[1].Value)
                                            select Tuple.Create(ak.Groups[1].Value, ak.Groups[2].Value));
                }
                catch
                {
                    packageSources.Add(Tuple.Create("main","https://nuget.org/api/v2/"));
                }
            }

            foreach (var csprojFile in csprojFiles)
            {
                var csprojContents = File.ReadAllText(csprojFile);
                foreach (Match m in RxReferenceWithHintPath.Matches(csprojContents))
                {
                    var hintPath = m.Groups[2].Value;
                    var assemblyName = m.Groups[1].Value;
                    var commaOffset = assemblyName.IndexOf(',');
                    if (commaOffset > 0)
                        assemblyName = assemblyName.Substring(0,commaOffset);

                    var pathBits = hintPath.Split(Path.DirectorySeparatorChar);
                    if (pathBits.Length <= 2) continue;

                    var pathNameBits = (pathBits[0]).Split('.');
                    var i = pathNameBits.IndexOf(x => x.All(char.IsDigit));
                    if (i <= 0) continue;

                    var packageName = string.Join(".", pathNameBits.Take(i));
                    var version = string.Join(".", pathNameBits.Skip(i));
                    var libPath = string.Join(Path.DirectorySeparatorChar.ToString(CultureInfo.InvariantCulture),
                                                pathBits, 1, pathBits.Length - 2);
                    var assemblyInfo = GetAssemblyInfo(
                        Directory.GetCurrentDirectory() + @"\packages\" +
                        Path.Combine(packageName + "." + version, libPath, assemblyName + ".dll"));
                    packageRefs.Add(new PackageInfo(assemblyName, packageName, version, libPath, assemblyInfo));
                    assemblies.Add(assemblyName);
                }
            }

            foreach (var assemblyName in assemblies)
            {
                var name = assemblyName;
                var refs = packageRefs.Where(pi => pi.AssemblyName == name).ToArray();
                if (refs.Length <= 1) continue;

                _out.WriteLine("Muliple differing refs to " + name + " things might not work, oh well.");
                var byPreferred = refs.OrderByDescending(RefComparer).ToArray();
                _out.WriteLine("\tPicking " + byPreferred[0].Path);
                foreach (var r in byPreferred.Skip(1)) packageRefs.Remove(r);
            }

            var nuget = new BsonDocument();
            foreach(var packageSource in packageSources)
                nuget[packageSource.Item1] = packageSource.Item2;

            var package = new BsonDocument();

            foreach(var pi in packageRefs.OrderBy(p=>p.AssemblyName))
            {
                if (!package.Contains(pi.PackageName))
                {
                    package[pi.PackageName] =
                        new BsonDocument
                            {
                                {"version", pi.Version},
                                {"assembly", new BsonDocument()}
                            };
                }
                var adoc = package[pi.PackageName].AsBsonDocument["assembly"].AsBsonDocument;
                if (!adoc.Contains(pi.LibPath))
                    adoc[pi.LibPath] = new BsonArray();
                adoc[pi.LibPath].AsBsonArray.Add(pi.AssemblyName);
                if (!package[pi.PackageName].AsBsonDocument.Contains("assemblyInfo"))
                {
                    var assemblyInfoDoc = new BsonDocument();
                    string pkt;
                    if (pi.AssemblyInfo.TryGetValue("publicKeyToken", out pkt))
                    {
                        assemblyInfoDoc["publicKeyToken"] = pkt;
                    }
                    string assemblyVersion;
                    if (pi.AssemblyInfo.TryGetValue("assemblyVersion", out assemblyVersion))
                    {
                        assemblyInfoDoc["assemblyVersion"] = assemblyVersion;
                    }
                    if (assemblyInfoDoc.ElementCount > 0)
                    {
                        package[pi.PackageName].AsBsonDocument["assemblyInfo"] = assemblyInfoDoc;
                    }
                }
            }

            var assemblyBindingBlock = GenerateAssemblyBindingBlock(package);
            
            return
                new BsonDocument
                    {
                        {"nuget", nuget},
                        {"package", package},
                        {"source", new BsonArray()},
                        {"assemblyBindingBlock", assemblyBindingBlock}
                    };

        }

        private BsonDocument GenerateAssemblyBindingBlock(BsonDocument package)
        {
            var result = new BsonDocument();
            foreach (var p in package.Elements)
            {
                var names = new List<string>();
                foreach (var nameArray in p.Value.AsBsonDocument["assembly"].AsBsonDocument.Elements.Select(e => e.Value.AsBsonArray))
                {
                    names.AddRange(nameArray.Select(e => e.AsString));
                }
                foreach (var name in names)
                {
                    BsonValue assemblyInfo, assemblyToken, assemblyVersion;
                    if (p.Value.AsBsonDocument.TryGetValue("assemblyInfo", out assemblyInfo)
                        && assemblyInfo.AsBsonDocument.TryGetValue("publicKeyToken", out assemblyToken)
                        && assemblyInfo.AsBsonDocument.TryGetValue("assemblyVersion", out assemblyVersion))
                    {
                        var item = new BsonDocument();
                        item["token"] = assemblyToken.AsString;
                        item["version"] = assemblyVersion.AsString;
                        result[name] = item;
                    }
                }
            }
            return result;
        }

        private IDictionary<string, string> GetAssemblyInfo(string assemblyPath)
        {
            if (!File.Exists(assemblyPath))
            {
                return null;
            }
            return _classifier.Classify(assemblyPath);
        }

        private ComparableRef RefComparer(PackageInfo pi)
        {
            return new ComparableRef(pi);
        }

        private class ComparableRef : IComparable<ComparableRef>
        {
            private readonly bool _packagesRef;
            private readonly bool _client;
            private readonly long[] _ver;

            public ComparableRef(PackageInfo pi)
            {
                _packagesRef = pi.LibPath.ToUpperInvariant().Contains("PACKAGES");
                _client = pi.LibPath.ToUpperInvariant().Contains("CLIENT");
                _ver = pi.Version
                    .Split('.')
                    .Select(
                        x =>
                            {
                                long t;
                                return long.TryParse(x, out t) ? t : 0;
                            }).ToArray();
            }

            public int CompareTo(ComparableRef other)
            {
                if (_packagesRef && !other._packagesRef) return -1;

                for (var i = 0; i < _ver.Length; ++i)
                {
                    if (i >= other._ver.Length) return 1;
                    if (_ver[i] > other._ver[i]) return 1;
                    if (_ver[i] < other._ver[i]) return -1;
                }

                if (_client && !other._client) return -1;
                return 0;
            }
        }

        private class PackageInfo
        {
            private class ComparerImpl : IEqualityComparer<PackageInfo>
            {
                public bool Equals(PackageInfo x, PackageInfo y)
                {
                    return x.Path.ToUpperInvariant() == y.Path.ToUpperInvariant();
                }

                public int GetHashCode(PackageInfo obj)
                {
                    return obj.GetHashCode();
                }
            }
            private readonly int _hashCode;
            public static readonly IEqualityComparer<PackageInfo> Comparer = new ComparerImpl();

            public PackageInfo(string assemblyName, string package, string version, string libPath, IDictionary<string, string> assemblyInfo)
            {
                AssemblyName = assemblyName;
                PackageName = package;
                Version = version;
                LibPath = libPath;
                AssemblyInfo = assemblyInfo;

                _hashCode = string.Join(
                    "\0",
                    new[]
                        {
                            AssemblyName,
                            PackageName,
                            Version,
                            LibPath,
                            String.Join(" ", AssemblyInfo.Values)
                        }
                    )
                    .ToUpperInvariant()
                    .GetHashCode();
            }

            public string AssemblyName { get; private set; }
            public string PackageName { get; private set; }
            public string Version { get; private set; }
            public string LibPath { get; private set; }
            public IDictionary<string, string> AssemblyInfo { get; private set; } 

            public string Path
            {
                get { return System.IO.Path.Combine(PackageName + "." + Version, LibPath, AssemblyName + ".dll"); }
            }

            public override int GetHashCode()
            {
                return _hashCode;
            }
        }
    }
}