using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using nugetine.Internal.Interface;

namespace nugetine.Internal
{
    internal sealed class Classifier : IClassifier
    {
        public IDictionary<string, string> Classify(string dllFilePath)
        {
            var asm = Assembly.LoadFrom(dllFilePath);
            var asmVersion = Regex.Matches(asm.FullName, "Version=(.*?),")[0].Groups[1].Value;
            var publicKeyToken = BitConverter.ToString(asm.GetName().GetPublicKeyToken()).Replace("-", "").ToLower();
            var result = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(publicKeyToken))
            {
                result.Add("publicKeyToken", publicKeyToken);
            }
            if (!string.IsNullOrWhiteSpace(asmVersion))
            {
                result.Add("assemblyVersion", asmVersion);
            }
            return result;
        }
    }
}
