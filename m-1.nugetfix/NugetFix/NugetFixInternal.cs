using System.Collections.Generic;
using System.Diagnostics;
using EnvDTE80;

namespace NugetFix
{
    internal sealed class NugetFixInternal
    {
        private DTE2 _applicationObject;
        private OutputWriter _out;
        private readonly ISet<string> _outputList = new SortedSet<string>();

        public void SetApplicationObject(DTE2 applicationObject)
        {
            _applicationObject = applicationObject;
            _out = new OutputWriter(_applicationObject);
        }

        public void Print(string msg)
        {
            _out.Write(msg);
        }

        public void RunNugetine()
        {
            SaveSolution();

            var solutionPath = _applicationObject.Solution.FullName;
            var consoleProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                        {
                            FileName = "nugetine.exe",
                            Arguments = solutionPath,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                };
            consoleProcess.Start();
            while (!consoleProcess.StandardOutput.EndOfStream)
            {
                var line = consoleProcess.StandardOutput.ReadLine();
                _outputList.Add(line);
            }

            SaveSolution();

            foreach (var item in _outputList)
            {
                _out.Write(item);
            }
            _outputList.Clear();
        }

        internal void SaveSolution()
        {
            _applicationObject.ExecuteCommand("File.SaveAll");
        }
    }
}
