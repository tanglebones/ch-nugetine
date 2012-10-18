using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EnvDTE80;
using Microsoft.Build.Evaluation;
using Project = EnvDTE.Project;
using ProjectItem = Microsoft.Build.Evaluation.ProjectItem;

namespace NugetFix
{
    internal sealed class NugetFixInternal
    {
        // we use the item reference map to keep track of the highest version for each item so that we can update packages config if necessary
        private readonly Dictionary<string, NuPackage> _items = new Dictionary<string, NuPackage>();
        private DTE2 _applicationObject;
        private OutputWriter _out;
        private readonly ISet<string> _outputList = new SortedSet<string>();

        public void SetApplicationObject(DTE2 applicationObject)
        {
            _applicationObject = applicationObject;
            _out = new OutputWriter(_applicationObject);
        }

        public void FixNugetReferences()
        {
            try
            {
                SaveSolution();

                GatherPackageAndReferences();

                UpdatePackageAndReferences();

                ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
                SaveSolution();
            }
            catch (Exception e)
            {
                _out.Write("Error: " + e.Message + "; StackTrace: " + e.StackTrace);
            }

            foreach (var item in _outputList)
            {
                _out.Write(item);
            }
            _out.Write("Modified " + _outputList.Count + " items.");

            _items.Clear();
        }

        private void SaveSolution()
        {
            _applicationObject.ExecuteCommand("File.SaveAll");
        }

        private void WalkTheSolution(Func<Microsoft.Build.Evaluation.Project, bool> augmentProject)
        {
            foreach (Project project in _applicationObject.Solution.Projects)
            {
                Microsoft.Build.Evaluation.Project buildProject;
                try
                {
                    buildProject = ProjectCollection.GlobalProjectCollection.LoadProject(project.FullName);
                }
                catch (Exception)
                {
                    continue;
                }

                if (augmentProject != null)
                {
                    var modified = augmentProject(buildProject);
                    UpdateIfModified(buildProject, modified);
                }
            }
        }

        private void GatherPackageAndReferences()
        {
            Func<Microsoft.Build.Evaluation.Project, bool> toGetTheReferences = project =>
                {
                    var modified = CheckAndSetSolutionDir(project);
                    GatherReferenceItems(project);
                    return modified;
                };
            Func<Microsoft.Build.Evaluation.Project, bool> toGetThePackageVersions = project =>
                {
                    GatherPackages(project.DirectoryPath);
                    return false;
                };

            WalkTheSolution(toGetTheReferences);
            WalkTheSolution(toGetThePackageVersions);
        }

        private bool CheckAndSetSolutionDir(Microsoft.Build.Evaluation.Project buildProject)
        {
            if (buildProject.GetProperty("SolutionDir").EvaluatedValue != "..\\")
            {
                buildProject.SetProperty("SolutionDir", "..\\");
                _outputList.Add("Modified project: " + buildProject.FullPath);
                return true;
            }
            return false;
        }

        /**
         * save the project if it has been modified.
         */
        private void UpdateIfModified(Microsoft.Build.Evaluation.Project buildProject, bool modified)
        {
            try
            {
                ProjectCollection.GlobalProjectCollection.TryUnloadProject(buildProject.Xml);
                if (!modified) return;
                buildProject.Save();
                _outputList.Add("Modified project: " + buildProject.FullPath);
            }
            catch (Exception e)
            {
                _out.Write("Error while trying to unload and save project: " + buildProject.FullPath + " : " + e.Message + "; Trace: " + e.StackTrace);
            }
        }

        private void GatherReferenceItems(Microsoft.Build.Evaluation.Project buildProject)
        {
            // avoid non reference items and system references that have nothing to modify.
            foreach (var item in buildProject.Items.Where(i => i.ItemType == "Reference" && i.DirectMetadataCount > 0))
            {
                FixItem(item);
                NuPackage nuPackage;
                if (_items.TryGetValue(item.EvaluatedInclude, out nuPackage))
                {
                    var currentVersion = nuPackage.Version;
                    var itemVersion = GetVersion(item);
                    if (currentVersion == null || String.Compare(itemVersion, currentVersion, StringComparison.Ordinal) > 0)
                    {
                        nuPackage.Version = itemVersion;
                        UpdateItem(nuPackage.Item, item);
                        nuPackage.Modified = true;
                    }
                }
                else
                {
                    nuPackage = new NuPackage
                        {
                            Item = item,
                            PackageName = GetItemReferenceName(item),
                            RefName = item.EvaluatedInclude,
                            Version = GetVersion(item),
                            Modified = false
                        };
                    _items[item.EvaluatedInclude] = nuPackage;
                }
            }
        }

        private string GetItemReferenceName(ProjectItem item)
        {
            var version = GetVersion(item);
            return Regex.Match(item.GetMetadata("HintPath").EvaluatedValue, "\\.?([^\\\\]*?)." + version + "\\\\lib\\\\").Groups[1].Value;
        }

        private void GatherPackages(string projectPath)
        {
            var filename = projectPath + @"\packages.config";
            if (!File.Exists(filename)) return;

            var packages = File.ReadAllText(filename);
            var packagesConfig = XDocument.Parse(packages);

            var packagesElement = packagesConfig.Element("packages");
            if (packagesElement == null) return;
            // attributes are set in a linked list structure:
            // e.g. package.FirstAttribute.NextAttribute.NextAttribute
            // we're assuming every attribute has at least an id and a version
            foreach (var package in packagesElement.Elements())
            {
                if (!package.HasAttributes) continue;

                var name = package.FirstAttribute;
                var version = name.NextAttribute;

                var nuPacakge = _items.FirstOrDefault(i => i.Value.PackageName == name.Value).Value;
                if (nuPacakge != null && (nuPacakge.Version == null || String.CompareOrdinal(version.Value, nuPacakge.Version) > 0))
                {
                    nuPacakge.Version = version.Value;
                    nuPacakge.Modified = true;
                }
            }
        }

        private bool FixItem(ProjectItem item)
        {
            var metaHint = item.GetMetadata("HintPath");
            var metaSpecific = item.GetMetadata("SpecificVersion");
            var hintValue = metaHint == null ? string.Empty : metaHint.UnevaluatedValue;

            var changed = false;
            if (item.EvaluatedInclude.Contains(","))
            {
                FixReferenceInclude(item);
                changed = true;
            }

            if (!hintValue.StartsWith(@"$(SolutionDir)") && hintValue.Contains("..\\packages\\"))
            {
                FixSolutionDir(item);
                changed = true;
            }
            if (metaSpecific == null)
            {
                SetSpecificVersion(item, false);
                changed = true;
            }
            return changed;
        }

        /**
         * Returns true if a's version is newer than b's, false otherwise.
         */ 
        private static bool VersionNewerThan(ProjectItem a, ProjectItem b)
        {
            return String.CompareOrdinal(GetVersion(a), GetVersion(b)) > 0;
        }

        /**
         * Returns the version of this reference item
         */
        private static string GetVersion(ProjectItem item)
        {
            return Regex.Match(item.GetMetadata("HintPath").EvaluatedValue, "\\.?([^\\\\a-zA-Z]*?)\\\\lib\\\\").Groups[1].Value;
        }

        /**
         * Assuming we have a ModifiedItems dictionary at this point, we can now walk the solution again
         * and update any stale references that need a version update.
         */
        private void UpdatePackageAndReferences()
        {
            Func<Microsoft.Build.Evaluation.Project, bool> toUpdatePackageAndReferences = project =>
                {
                    var modifiedProject = UpdatePackagesConfig(project.DirectoryPath);
                    modifiedProject |= UpdateReferenceItems(project);
                    modifiedProject |= UpdateAppConfig(project.DirectoryPath);
                    return modifiedProject;
                };

            WalkTheSolution(toUpdatePackageAndReferences);
        }

        private bool UpdateReferenceItems(Microsoft.Build.Evaluation.Project buildProject)
        {
            var modifiedProject = false;
            foreach (var item in buildProject.Items.Where(i => i.ItemType == "Reference" && i.DirectMetadataCount > 0))
            {
                modifiedProject |= FixItem(item);
                NuPackage nuPackage;
                if (_items.TryGetValue(item.EvaluatedInclude, out nuPackage)
                    && nuPackage.Item != null
                    && VersionNewerThan(nuPackage.Item, item))
                {
                    UpdateItem(item, nuPackage.Item);
                    modifiedProject = true;
                }
            }
            return modifiedProject;
        }

        private static void UpdateItem(ProjectItem item, ProjectItem updated)
        {
            // cean the item's meta items
            item.Metadata.Clear();

            // replace with the updated version
            foreach (var meta in updated.Metadata)
            {
                item.SetMetadataValue(meta.Name, meta.UnevaluatedValue);
            }
        }

        private static void SetSpecificVersion(ProjectItem item, bool isSpecificVersion)
        {
            item.SetMetadataValue("SpecificVersion", isSpecificVersion ? "True" : "False");
        }

        /**
         *  The path should be set to $(SolutionDir) if it's not already.
         */
        private void FixSolutionDir(ProjectItem item)
        {
            var metaHint = item.GetMetadata("HintPath");
            var solutionDir = @"\$(SolutionDir)".Substring(1) + @"\packages\";
            var newMetaHintValue = metaHint.EvaluatedValue.Replace("..\\\\packages\\", solutionDir)
                                                          .Replace("..\\packages\\", solutionDir);
            item.SetMetadataValue(metaHint.Name, newMetaHintValue);
        }

        /**
         *  The Reference Include= should only havet he package name.
         */
        private void FixReferenceInclude(ProjectItem item)
        {
            item.Rename(item.EvaluatedInclude.Split(',')[0]);
        }

        /**
         * Fixes the lowerbound cap on assembly version binding
         */
        public bool UpdateAppConfig(string projectPath)
        {
            var filename = projectPath + @"\App.config";
            if (!File.Exists(filename)) return false;

            var appConfig = File.ReadAllText(filename);
            var appConfigXml = XDocument.Parse(appConfig);

            var configurationItem = appConfigXml.Element("configuration");
            if (configurationItem == null) return false;
            var runtimeItem = configurationItem.Element("runtime");
            if (runtimeItem == null) return false;
            var modified = false;
            foreach (var assemblyBinding in runtimeItem.Elements().Where(i => i.Name.LocalName == "assemblyBinding"))
            {
                foreach (var dependentAssembly in assemblyBinding.Elements().Where(i => i.Name.LocalName == "dependentAssembly"))
                {
                    var assemblyIdentity = dependentAssembly.Descendants().Single(d => d.Name.LocalName == "assemblyIdentity");
                    var bindingRedirect = dependentAssembly.Descendants().Single(d => d.Name.LocalName == "bindingRedirect");

                    var oldVersion = bindingRedirect.FirstAttribute.Value;
                    var expectedOldVersion = Regex.Replace(oldVersion, "-(.*?)$", "-65535.65535.65535.65535");
                    if (oldVersion != expectedOldVersion)
                    {
                        bindingRedirect.SetAttributeValue("oldVersion", expectedOldVersion);
                        modified = true;
                    }

                    var newVersion = bindingRedirect.FirstAttribute.NextAttribute.Value;
                    var name = assemblyIdentity.FirstAttribute.Value;
                    var itemRefVersion = _items[name].Version;
                    if (String.CompareOrdinal(itemRefVersion, newVersion) > 0)
                    {
                        assemblyIdentity.SetAttributeValue("newVersion", itemRefVersion);
                        modified = true;
                    }
                }
            }
            if (modified)
            {
                _outputList.Add("Modified: " + filename);
                appConfigXml.Save(filename);
            }
            return modified;
        }


        /**
         *  The packages.config file should not explicitly list the target framework
         */
        private bool UpdatePackagesConfig(string projectPath)
        {
            var filename = projectPath + @"\packages.config";
            if (!File.Exists(filename)) return false;

            var packages = File.ReadAllText(filename);
            var packagesConfig = XDocument.Parse(packages);

            var packagesElement = packagesConfig.Element("packages");
            var modified = false;
            var refSet = new HashSet<string>();
            var deleteRefs = new List<string>();
            if (packagesElement != null)
            {
                // attributes are set in a linked list structure:
                // e.g. package.FirstAttribute.NextAttribute.NextAttribute
                // we're assuming every attribute has at least an id and a version
                foreach (var package in packagesElement.Elements())
                {
                    if (!package.HasAttributes) continue;

                    var name = package.FirstAttribute;
                    if (!refSet.Add(name.Value))
                    {
                        deleteRefs.Add(name.Value);
                    }
                    var version = name.NextAttribute;
                    var targetFramework = version.NextAttribute;

                    // remove targetFramework attribute if it exists
                    if (targetFramework != null)
                    {
                        package.SetAttributeValue("targetFramework", null);
                        modified = true;
                    }

                    // update the version if necessary
                    var nuPacakge = _items.FirstOrDefault(i => i.Value.PackageName == name.Value).Value;
                    if (nuPacakge != null && String.CompareOrdinal(nuPacakge.Version, version.Value) > 0)
                    {
                        package.SetAttributeValue("version", nuPacakge.Version);
                        modified = true;
                    }
                }
                // Remove duplicates from the package list
                var distinct = packagesElement.Elements().GroupBy(x => x.FirstAttribute.Value).Select(y => y.First()).ToList();
                packagesElement.RemoveNodes();
                packagesElement.Add(distinct);
            }
            if (modified)
            {
                _outputList.Add("Modified: " + filename);
                packagesConfig.Save(filename);
                return true;
            }
            return false;
        }
    }
}
