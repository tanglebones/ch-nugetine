using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using Move.Ex.Bson;
using Move.Ex.DotNet;

namespace nugetine
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            var @out = Console.Out;
            var thisSln = Path.GetFileName(Environment.CurrentDirectory);
            IState state = new State(thisSln);
            var local = thisSln + ".nugetine.json";
            if (!File.Exists(local))
            {
                @out.WriteLine("Could not find: " + local);
                Environment.Exit(-1);
            }
            state.LoadConfig(local);

            @out.WriteLine(state.ToString());

            state.Index();

            foreach (var package in state.Packages)
            {
                var process =
                    Process.Start(
                        new ProcessStartInfo("nuget","install " + package["name"].AsString + " -Version " + package["version"].AsString)
                        {UseShellExecute = false}
                        );
                process.WaitForExit();
            }

            Directory.EnumerateFiles(".", "*.csproj", SearchOption.AllDirectories).AsParallel().ForAll(state.ProcessCsProj);
        }
    }

    internal class State : IState
    {
        private readonly BsonDocument _config = new BsonDocument();
        private readonly BsonDocument _assemblyMapping = new BsonDocument();
        private static readonly Regex RxReference =
            new Regex(
                @"<Reference\s+Include\s*=\s*""([^""]+)""\s*>(.*?)</Reference>",
                RegexOptions.Compiled|RegexOptions.Singleline|RegexOptions.IgnoreCase
                );
        private static readonly Regex RxProjectReference =
            new Regex(
                @"(<ProjectReference\s*Include="")([^""]+)(""\s*>)(.*?)(</ProjectReference>)",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
                );
        private readonly string _thisSln;

        public State(string thisSln)
        {
            _thisSln = thisSln;
        }

        public void LoadConfig(string path)
        {
            var source = File.ReadAllText(path);
            var doc = source.ParseBsonDocument();
            _config.Overlay(doc);
        }

        public void ProcessCsProj(string csprojFileName)
        {
            var csprojDirectory = Path.GetDirectoryName(csprojFileName);
            if (csprojDirectory == null) throw new ArgumentException(csprojFileName);
            var csprojContents = File.ReadAllText(csprojFileName);
            var packages = new List<string>();
            var newCsprojContents = RxReference.Replace(
                csprojContents,
                match=>
                    {
                        var include = match.Groups[1].Value;
                        var splitIndex = include.IndexOf(',');
                        if (splitIndex > 0)
                            include = include.Substring(0, splitIndex);
                        var assemblyInfo = _assemblyMapping[include.ToUpperInvariant()].AsBsonDocument;
                        var path = assemblyInfo["path"].AsString;
                        packages.Add("  <package id=\""+assemblyInfo["package"].AsString + "\" version=\""+assemblyInfo["version"].AsString+"\" />");
                        return
                            "<Reference Include=\"" + include + "\">"
                            + Environment.NewLine
                            + "      <SpecificVersion>False</SpecificVersion>"
                            + Environment.NewLine
                            + "      <HintPath>" + path + "</HintPath>"
                            + Environment.NewLine
                            + "    </Reference>";
                    }
                );
            File.WriteAllText(csprojFileName, newCsprojContents);
            var packagesConfigFileName = Path.Combine(csprojDirectory, "packages.config");
            var newPackagesConfigContents =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + Environment.NewLine
                + "<packages>"
                + Environment.NewLine
                + string.Join(Environment.NewLine, packages.RemoveDuplicatesOn(x=>x))
                + Environment.NewLine
                + "</packages>"
                ;
            File.WriteAllText(packagesConfigFileName, newPackagesConfigContents);
        }

        public void Index()
        {
            foreach(var package in _config["package"].AsBsonDocument)
                foreach(var dir in package.Value.AsBsonDocument["assembly"].AsBsonDocument)
                    foreach(var assembly in dir.Value.AsBsonArray)
                    {
                        var assemblyName = assembly.AsString;

                        var version = package.Value.AsBsonDocument["version","1.0"].AsString;
                        var source = package.Value.AsBsonDocument["source",_thisSln].AsString;
                        var assemblyInfo = new BsonDocument();

                        assemblyInfo["path"] =
                            Path.Combine(
                                "..",
                                "packages",
                                package.Name + "." + version,
                                dir.Name.Replace('/','\\'),
                                assemblyName + ".dll"
                                );
                        assemblyInfo["package"] = package.Name;
                        assemblyInfo["version"] = version;
                        assemblyInfo["source"] = source;

                        _assemblyMapping[assemblyName.ToUpperInvariant()] = assemblyInfo;
                    }
        }

        public IEnumerable<BsonDocument> Packages
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

        public override string ToString()
        {
            return _config.ToString();
        }
    }

    internal interface IState
    {
        void LoadConfig(string path);
        void ProcessCsProj(string csprojFileName);
        void Index();
        IEnumerable<BsonDocument> Packages { get; }
    }
}