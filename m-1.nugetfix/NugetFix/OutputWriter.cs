using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EnvDTE;
using EnvDTE80;

namespace NugetFix
{
    internal sealed class OutputWriter
    {
        public static string RedPaneName = "Red";
        private OutputWindowPane _outputPane = null;
        private readonly DTE2 _appObj;

        public OutputWriter(DTE2 appObj)
        {
            _appObj = appObj;
            CreatePane(RedPaneName);
            _outputPane.Clear();
        }

        private void CreatePane(string name)
        {
            var window = _appObj.Windows.Item(Constants.vsWindowKindOutput);
            var outputWindow = (OutputWindow) window.Object;
            _outputPane = outputWindow.OutputWindowPanes.Add(name);
        }

        public void Write(string msg)
        {
            if (_outputPane == null) return;
            _outputPane.Activate();
            _outputPane.OutputString(msg + Environment.NewLine);
        }
    }
}
