using System;
using System.Collections.Generic;
using NugetFix.AssemblyClassifier.Interface;

namespace NugetFix.AssemblyClassifier
{
    internal sealed class Classifier : IClassifier
    {
        public void Classify(string dllFilePath, IDictionary<string, IDictionary<string, string>> results)
        {
            var asm = System.Reflection.Assembly.LoadFile(dllFilePath);
            var name = asm.GetName().Name;
            var dic = new Dictionary<string, string>
                {
                    {"name", name},
                    {"fullName", asm.GetName().FullName},
                    {"publicToken", BitConverter.ToString(asm.GetName().GetPublicKeyToken()).Replace("-", "").ToLower()}
                };
            results[name] = dic;
        }
    }
}
