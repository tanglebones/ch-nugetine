using System.Collections.Generic;
using ProjectItem = Microsoft.Build.Evaluation.ProjectItem;
namespace NugetFix
{
    internal sealed class NuPackage
    {
// ReSharper disable UnusedMember.Local
        internal string RefName { get; set; }
        internal string PackageName { get; set; }
        internal IDictionary<string, string> AssemblyAttributes { get; set; }
        internal string Version { get; set; }
        internal ProjectItem Item { get; set; }
        internal bool Modified { get; set; }
        internal ISet<string> Projects { get; set; }
// ReSharper restore UnusedMember.Local
    }
}
