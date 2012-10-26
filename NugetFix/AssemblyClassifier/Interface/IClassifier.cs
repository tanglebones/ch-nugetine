using System.Collections.Generic;

namespace NugetFix.AssemblyClassifier.Interface
{
    internal interface IClassifier
    {
        void Classify(string dllFilePath, IDictionary<string, IDictionary<string, string>> results);
    }
}
