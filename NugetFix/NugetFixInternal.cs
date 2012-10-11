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
        private static readonly Dictionary<string, ProjectItem> ModifiedItems = new Dictionary<string, ProjectItem>();
        // we use the item reference map to keep track of the highest version for each item so that we can update packages config if necessary
        private static readonly Dictionary<string, ProjectItem> ItemReferenceMap = new Dictionary<string, ProjectItem>();
        private DTE2 _applicationObject;
        private static NugetFixInternal _instance;

        public static NugetFixInternal GetInstance()
        {
            return _instance ?? (_instance = new NugetFixInternal());
        }

        private NugetFixInternal()
        {
        }

        public void SetApplicationObject(DTE2 applicationObject)
        {
            _applicationObject = applicationObject;
        }

        public void FixNugetReferences()
        {
            // first pass: 
            // walk the solution projects and fix every issue encountered.
            // store item references for every modified item in a dictionary.
            WalkSolutionFixAndUpdate();

            // second pass:
            // walk the solution and update every item in the modified item dictionary
            UpdateVersionsThroughEntireSolution();

            ModifiedItems.Clear();
        }

        /**
         *  The first pass of the algorithm which goes through every project in the solution to do the following:
         *  update the items in the csproj references, 
         *  keeps track of modified items,
         *  updates packages.config
         *  and saves the project if it has been modified.
         */
        private void WalkSolutionFixAndUpdate()
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
                    // TODO: Handle cases such as incompatible project references
                    // but for now just skip any "project" that doesn't load.
                    continue;
                }

                var modifiedProject = CheckAndProcessProjectReferenceItems(buildProject);

                UpdateIfModified(buildProject, modifiedProject);
                ProjectCollection.GlobalProjectCollection.TryUnloadProject(buildProject.Xml);
            }
        }



        /**
         * save the project if it has been modified.
         */
        private void UpdateIfModified(Microsoft.Build.Evaluation.Project buildProject, bool modified)
        {
            if (!modified) return;
            ProjectCollection.GlobalProjectCollection.UnloadProject(buildProject);
            buildProject.Save();
        }

        /**
         * Part of the first pass of the algorithm.
         * Update and fix any bad item references and keep track of updated items so we can update the whole solution on the second pass.
         */
        private static bool CheckAndProcessProjectReferenceItems(Microsoft.Build.Evaluation.Project buildProject)
        {
            var modifiedProject = false;
            // avoid non reference items and system references that have nothing to modify.
            foreach (var item in buildProject.Items.Where(i => i.ItemType == "Reference" && i.DirectMetadataCount > 0))
            {
                ProjectItem updated;
                if (ModifiedItems.TryGetValue(item.EvaluatedInclude, out updated))
                {
                    UpdateItem(item, updated);
                    modifiedProject = true;
                    continue;
                }

                var metaHint = item.GetMetadata("HintPath");
                var metaSpecific = item.GetMetadata("SpecificVersion");
                var hintValue = metaHint == null ? string.Empty : metaHint.EvaluatedValue;
                var modifiedItem = false;
                if (!string.IsNullOrWhiteSpace(hintValue) && hintValue.Contains("\\packages\\"))
                {
                    if (item.EvaluatedInclude.Contains(","))
                    {
                        FixReferenceInclude(item, ref modifiedProject);
                        modifiedItem = true;
                    }
                    if (!hintValue.StartsWith(@"$(SolutionDir)") && hintValue.Contains("..\\packages\\"))
                    {
                        FixSolutionDir(item, metaHint, ref modifiedProject);
                        modifiedItem = true;
                    }
                    if (metaSpecific == null)
                    {
                        SetSpecificVersion(item, false, ref modifiedProject);
                        modifiedItem = true;
                    }
                }
                if (modifiedItem)
                {
                    // given the item name and reference we can walk all the other projects in the solution
                    // and simply replace any occurrence of this item with the updated version.
                    ModifiedItems[item.EvaluatedInclude] = item;
                }

                AddToReferenceMapIfDoesNotExistOrNewer(item);
            }
            return modifiedProject;
        }

        /**
         * Adds the item to the ItemReferenceMap if it is not already there 
         * or if its version number is newer than the existing item reference.
         */
        private static void AddToReferenceMapIfDoesNotExistOrNewer(ProjectItem item)
        {
            ProjectItem mapItem;
            if (!ItemReferenceMap.TryGetValue(item.EvaluatedInclude, out mapItem) || VersionNewerThan(item, mapItem))
            {
                ItemReferenceMap[item.EvaluatedInclude] = item;
            }
        }

        /**
         * Returns true if a's version is newer than b's, false otherwise.
         */ 
        private static bool VersionNewerThan(ProjectItem a, ProjectItem b)
        {
            return String.Compare(GetVersion(a), GetVersion(b), StringComparison.Ordinal) > 0;
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
        private void UpdateVersionsThroughEntireSolution()
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
                    // TODO: Handle cases such as incompatible project references
                    // but for now just skip any "project" that doesn't load.
                    continue;
                }

                var modifiedProject = UpdateModifiedItems(buildProject);
                UpdatePackagesConfig(buildProject.DirectoryPath);

                UpdateAppConfig(buildProject.DirectoryPath);

                UpdateIfModified(buildProject, modifiedProject);
                ProjectCollection.GlobalProjectCollection.TryUnloadProject(buildProject.Xml);
            }
        }

        private static bool UpdateModifiedItems(Microsoft.Build.Evaluation.Project buildProject)
        {
            var modifiedProject = false;
            foreach (var item in buildProject.Items.Where(i => i.ItemType == "Reference" && i.DirectMetadataCount > 0))
            {
                ProjectItem updated;
                if (ModifiedItems.TryGetValue(item.EvaluatedInclude, out updated))
                {
                    UpdateItem(item, updated);
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

        // ReSharper disable RedundantAssignment
        private static void SetSpecificVersion(ProjectItem item, bool isSpecificVersion, ref bool modified)
        // ReSharper restore RedundantAssignment
        {
            item.SetMetadataValue("SpecificVersion", isSpecificVersion ? "True" : "False");
            modified = true;
        }

        /**
         *  The path should be set to $(SolutionDir) if it's not already.
         */
        // ReSharper disable RedundantAssignment
        private static void FixSolutionDir(ProjectItem item, ProjectMetadata metaHint, ref bool modified)
        // ReSharper restore RedundantAssignment
        {
            var solutionDir = @"\$(SolutionDir)".Substring(1) + @"\packages\";
            var newMetaHintValue = metaHint.EvaluatedValue.Replace("..\\\\packages\\", solutionDir)
                                                          .Replace("..\\packages\\", solutionDir);
            item.SetMetadataValue(metaHint.Name, newMetaHintValue);
            modified = true;
        }

        /**
         *  The Reference Include= should only havet he package name.
         */
        // ReSharper disable RedundantAssignment
        private static void FixReferenceInclude(ProjectItem item, ref bool modified)
        // ReSharper restore RedundantAssignment
        {
            item.Rename(item.EvaluatedInclude.Split(',')[0]);
            modified = true;
        }

        /**
         * Fixes the lowerbound cap on assembly version binding
         */
        public static void UpdateAppConfig(string projectPath)
        {
            var filename = projectPath + @"\App.config";
            if (!File.Exists(filename)) return;

            var appConfig = File.ReadAllText(filename);
            var appConfigXml = XDocument.Parse(appConfig);

            var configurationItem = appConfigXml.Element("configuration");
            if (configurationItem == null) return;
            var runtimeItem = configurationItem.Element("runtime");
            if (runtimeItem == null) return;
            var modified = false;
            foreach (var assemblyBinding in runtimeItem.Elements().Where(i => i.Name.LocalName == "assemblyBinding"))
            {
                foreach (var dependentAssembly in assemblyBinding.Elements().Where(i => i.Name.LocalName == "dependentAssembly"))
                {
                    foreach (var bindingRedirect in dependentAssembly.Elements().Where(i => i.Name.LocalName == "bindingRedirect"))
                    {
                        var version = bindingRedirect.FirstAttribute.Value;
                        var modifiedVersion = Regex.Replace(version, "-(.*?)$", "-65535.65535.65535.65535");
                        bindingRedirect.SetAttributeValue("oldVersion", modifiedVersion);
                        modified = true;
                    }
                }
            }
            if (modified)
            {
                appConfigXml.Save(filename);
            }
        }


        /**
         *  The packages.config file should not explicitly list the target framework
         */
        private static void UpdatePackagesConfig(string projectPath)
        {
            var filename = projectPath + @"\packages.config";
            if (!File.Exists(filename)) return;

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
                    ProjectItem item;
                    if (ItemReferenceMap.TryGetValue(name.Value, out item) && version != null)
                    {
                        var highestVersion = GetVersion(item);
                        if (version.Value != highestVersion)
                        {
                            package.SetAttributeValue("version", highestVersion);
                            modified = true;
                        }
                    }
                }
                // Remove duplicates from the package list
                var distinct = packagesElement.Elements().GroupBy(x => x.FirstAttribute.Value).Select(y => y.First()).ToList();
                packagesElement.RemoveNodes();
                packagesElement.Add(distinct);
            }
            if (modified)
            {
                packagesConfig.Save(filename);
            }
        }
    }
}
