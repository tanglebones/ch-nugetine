using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using nugetine.Internal.Interface;

namespace nugetine.Internal
{
    internal class SourceIndexer: ISourceIndexer
    {
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

        private readonly TextWriter _out;

        public SourceIndexer(TextWriter @out)
        {
            _out = @out;
        }

        public void Run()
        {
            var doc = new BsonDocument();
            IDictionary<string,string> seen = new Dictionary<string,string>(StringComparer.InvariantCultureIgnoreCase);

            foreach(var dir in Directory.EnumerateDirectories(".", "*", SearchOption.AllDirectories).Where(d=>!Ignore(d)))
            {
                var directoryName = dir;

                var assemblyName = Path.GetFileName(dir);
                if (assemblyName == null) continue;

                var nuspecFile = Path.Combine(dir, assemblyName + ".nuspec");
                if(!File.Exists(nuspecFile)) continue;

                const string s = ".\\";
                if (directoryName.StartsWith(s))
                    directoryName = directoryName.Substring(s.Length);

                if (seen.ContainsKey(assemblyName))
                {
                    _out.WriteLine("Conflict: {0} in both '{1}' and '{2}'", assemblyName, directoryName, seen[assemblyName]);
                }
                seen[assemblyName] = directoryName;
                doc[assemblyName] = directoryName;
            }
            File.WriteAllText("source_index.nugetine.json", doc.ToJson(_settings));
        }

        // if nugetine.ignore exists in the directory, or any of it's parent directories, ignore it.
        private static bool Ignore(string dir)
        {
            while(!string.IsNullOrWhiteSpace(dir))
            {
                if (File.Exists(Path.Combine(dir, "nugetine.ignore"))) return true;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }
    }
}