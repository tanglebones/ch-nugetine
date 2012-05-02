using System;
using System.IO;
using System.Linq;
using System.Security.Permissions;
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

            var slnPrefix = DetermineSlnPrefixAndSetupEnviroment(args, @out);

            var reWriter = SetupReWriter(@out, slnPrefix);

            @out.WriteLine(reWriter.ToString());

            reWriter.Run();
        }

        private static IReWriter SetupReWriter(TextWriter @out, string slnPrefix)
        {
            IReWriter reWriter = new ReWriter(@out, slnPrefix + ".sln");

            var slnNugetineFileName = slnPrefix + ".nugetine.json";
            if (!File.Exists(slnNugetineFileName))
            {
                @out.WriteLine("Could not find: " + slnNugetineFileName);
                Environment.Exit(-1);
            }

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
            string slnName;
            if (args.Length == 0)
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
            else
            {
                slnName = args[0];
            }
            var slnDir = Path.GetDirectoryName(slnName);
            if (slnDir != null)
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