using System;
using System.IO;
using System.Linq;
using System.Security.Permissions;
using MongoDB.Bson;
using nugetine.Internal;
using nugetine.Internal.Interface;

namespace nugetine
{
    public static class Program
    {
        [EnvironmentPermission(SecurityAction.LinkDemand, Unrestricted = true)]
        public static void Main(string[] args)
        {
            var @out = Console.Out;
            var index = args.Any(a => a == "-i");
            if (index)
            {
                @out.WriteLine("Creating index.");
                Index(@out);
                Environment.Exit(0);
            }

            var slnPrefix = DetermineSlnPrefixAndSetupEnviroment(args, @out);

            var sourceIndex = LoadSourceIndex();
            var reWriter = SetupReWriter(@out, slnPrefix, sourceIndex);

            var gleen = args.Any(a => a == "-g");
            if (reWriter == null || gleen)
            {
                @out.WriteLine("Attempting to auto-generate " + slnPrefix + ".nugetine.json");
                Glean(@out, slnPrefix);
                @out.WriteLine("Verify " + slnPrefix + ".nugetine.json is correct and re-run.");
                Environment.Exit(0);
            }

            @out.WriteLine(reWriter.ToString());

            reWriter.Run();
        }

        private static BsonDocument LoadSourceIndex()
        {
            var indexFile = FindInParent(Path.GetFullPath(Environment.CurrentDirectory), "source_index.nugetine.json");
            return indexFile == null ?
                new BsonDocument
                    {
                        {"source",new BsonDocument()},
                        {"base", string.Empty}
                    } :
                new BsonDocument
                    {
                        {"source",BsonDocument.Parse(File.ReadAllText(indexFile))},
                        {"base",Path.GetDirectoryName(indexFile)??string.Empty},
                    };
        }

        private static string FindInParent(string dir, string filename)
        {
            while (!string.IsNullOrWhiteSpace(dir))
            {
                var candidateFile = Path.Combine(dir, filename);
                if (File.Exists(candidateFile)) return candidateFile;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static void Index(TextWriter @out)
        {
            new SourceIndexer(@out).Run();
        }

        private static void Glean(TextWriter @out, string slnPrefix)
        {
            new Gleaner(@out, slnPrefix).Run();
        }

        private static IReWriter SetupReWriter(TextWriter @out, string slnPrefix, BsonDocument sourceIndex)
        {
            var slnNugetineFileName = slnPrefix + ".nugetine.json";
            if (!File.Exists(slnNugetineFileName))
            {
                @out.WriteLine("Could not find: " + slnNugetineFileName);
                return null;
            }

            IReWriter reWriter = new ReWriter(@out, slnPrefix + ".sln", sourceIndex);

            foreach (var nugetineFile in
                Directory.EnumerateFiles(".", "*.nugetine.json")
                    .Where(x => !x.Equals(slnNugetineFileName, StringComparison.InvariantCultureIgnoreCase)))
                reWriter.LoadConfig(nugetineFile);

            // load the sln file last, just in case it overrides something in the other files
            reWriter.LoadConfig(slnNugetineFileName);

            return reWriter;
        }

        private static string DetermineSlnPrefixAndSetupEnviroment(string[] args, TextWriter @out)
        {
            var slnName = args.FirstOrDefault(a=>!a.StartsWith("-"));
            if (string.IsNullOrWhiteSpace(slnName))
            {
                var slns = Directory.EnumerateFiles(".", "*.sln").ToArray();
                if (slns.Length == 0)
                {
                    @out.WriteLine("Couldn't find any sln files.");
                    Environment.Exit(-1);
                }
                if (slns.Length > 1)
                {
                    @out.WriteLine("Found more than one sln file, specific one please.");
                    Environment.Exit(-1);
                }
                slnName = slns[0];
            }

            var slnDir = Path.GetDirectoryName(slnName);
            if (!string.IsNullOrWhiteSpace(slnDir))
            {
                if (slnDir != ".")
                    Environment.CurrentDirectory = slnDir;
                slnName = Path.GetFileName(slnName);
            }
            var slnPrefix = Path.GetFileNameWithoutExtension(slnName);
            return slnPrefix;
        }
    }
}