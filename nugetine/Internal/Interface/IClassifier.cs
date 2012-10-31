using System.Collections.Generic;

namespace nugetine.Internal.Interface
{
    internal interface IClassifier
    {
        IDictionary<string, string> Classify(string dllFilePath);
    }
}
