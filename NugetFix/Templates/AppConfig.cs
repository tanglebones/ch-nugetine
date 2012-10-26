using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/**
 * Because this is an AddIn, we can't read from files that aren't local to the solution running this code.
 */
namespace NugetFix.Templates
{
    internal sealed class AppConfig
    {
        internal static string[] EmptyConfig =
            {
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<configuration>",
                    "\t<runtime>",
                    "\t</runtime>",
                "</configuration>"
            };
    }
}
