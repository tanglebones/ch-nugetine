using System;
using EnvDTE;
using EnvDTE80;
using NUnit.Framework;
using Shouldly;

namespace NugetFix.Test
{
    [TestFixture, Category("Unit")]
    public class NugetFixTests
    {
        public const string Vs10 = "VisualStudio.DTE.10.0";
        public const string Vs11 = "VisualStudio.DTE.11.0";
        private DTE2 _dte = null;

        private void SetupDte(string version)
        {
            if (_dte != null) return;
            var type = System.Type.GetTypeFromProgID(version);
            var inst = System.Activator.CreateInstance(type, true);
            _dte = (DTE2) inst;
        }

        // e.g. version = "VisualStudio.DTE.10.0"
        private void TestOnFakeSolution(Func<Solution2,bool> test)
        {
            var solution = (Solution2) _dte.Solution;
            solution.Create(@"../../Data", "FakeSolution.sln");
                
            if (test != null)
            {
                // Test and Assert
                test(solution).ShouldBe(true);
            }
        }

        [Test]
        public void Test_That_FakeSolution_Loads()
        {
            SetupDte(Vs10);
            Func<Solution2, bool> loadSolution = sol => !string.IsNullOrWhiteSpace(sol.FullName);
            TestOnFakeSolution(loadSolution);
        }
    }
}
