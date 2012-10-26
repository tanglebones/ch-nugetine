using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EnvDTE80;
using Microsoft.Build.Evaluation;
using NugetFix.AssemblyClassifier;
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
            }
            catch (Exception e)
            {
                _out.Write("Error: " + e.Message + "; StackTrace: " + e.StackTrace);
            }
            finally
            {
                ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
                SaveSolution();
            }

            foreach (var item in _outputList)
            {
                _out.Write(item);
            }
            _out.Write("Modified " + _outputList.Count + " items.");

            _items.Clear();
            _outputList.Clear();
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
                    GatherReferenceItems(project);
                    return false;
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
                var csProjXml = XDocument.Parse(File.ReadAllText(buildProject.FullPath));
                if (csProjXml.Root != null)
                {
                    var rootNamespace = csProjXml.Root.Name.Namespace;
                    var projectTag = csProjXml.Elements().FirstOrDefault(e => e.Name.LocalName == "Project");
                    if (projectTag != null)
                    {
                        var propertyGroupTag = projectTag.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup");
                        if (propertyGroupTag != null)
                        {
                            var solutionDirTag = propertyGroupTag.Elements().FirstOrDefault(e => e.Name.LocalName == "SolutionDir");
                            if (solutionDirTag == null)
                            {
                                var sdElement = new XElement(rootNamespace + "SolutionDir", "..\\");
                                sdElement.SetAttributeValue("Condition", "$(SolutionDir) == '' Or $(SolutionDir) == '*Undefined*'");
                                propertyGroupTag.Add(sdElement);
                                csProjXml.Save(buildProject.FullPath);
                                _outputList.Add("Modified project: " + buildProject.FullPath);
                                return true;
                            }
                        }
                    }
                }
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
            foreach (var item in buildProject.Items.Where(i => i.ItemType == "Reference" 
                && i.DirectMetadataCount > 0 
                && !i.EvaluatedInclude.StartsWith("System.")
                && i.GetMetadataValue("HintPath").Contains("\\packages\\")))
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
                        if (nuPackage.Projects == null)
                        {
                            nuPackage.Projects = new HashSet<string>
                                {
                                    buildProject.DirectoryPath
                                };
                        } 
                        else
                        {
                            nuPackage.Projects.Add(buildProject.DirectoryPath);
                        }
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
                            Modified = false,
                            Projects = new HashSet<string>
                                {
                                    buildProject.DirectoryPath
                                }
                        };
                    _items[item.EvaluatedInclude] = nuPackage;
                }
                GetAssemblyAttributes(nuPackage, buildProject.DirectoryPath);
            }
        }

        private void GetAssemblyAttributes(NuPackage nuPackage, string currentDir)
        {
            if (nuPackage.AssemblyAttributes != null) return;
            var dllInfo = new Dictionary<string, IDictionary<string, string>>();
            var dllPath = nuPackage.Item.GetMetadataValue("HintPath");
            /*using (var isolated = new Isolated<Classifier>())
            {
                isolated.Value.Classify(dllPath, dllInfo);
            }*/

            var absoluteDllPath = dllPath.Contains("..") 
                ? Path.Combine(currentDir, dllPath)
                : dllPath;

            if (!File.Exists(absoluteDllPath))
            {
                _out.Write("Error. dll does not exist: " + absoluteDllPath);
                return;
            }
            var classifier = new Classifier();
            classifier.Classify(absoluteDllPath, dllInfo);
            IDictionary<string, string> dllParams;
            if (dllInfo.TryGetValue(nuPackage.RefName, out dllParams))
            {
                nuPackage.AssemblyAttributes = dllParams;
            }
        }

        private string GetItemReferenceName(ProjectItem item)
        {
            return Regex.Match(item.GetMetadata("HintPath").EvaluatedValue, "\\.?([^\\\\]*?)." + "((\\d\\.?){1,})" + "\\\\lib\\\\").Groups[1].Value;
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
                if (nuPacakge != null)
                {
                    nuPacakge.Projects.Add(projectPath);
                    if (nuPacakge.Version == null || String.CompareOrdinal(version.Value, nuPacakge.Version) > 0)
                    {
                        nuPacakge.Version = version.Value;
                        nuPacakge.Modified = true;
                    }
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
            var version = Regex.Match(item.GetMetadata("HintPath").EvaluatedValue, "\\.?([^\\\\a-zA-Z]*?)\\\\lib\\\\").Groups[1].Value;
            return version;
        }

        /**
         * Assuming we have a ModifiedItems dictionary at this point, we can now walk the solution again
         * and update any stale references that need a version update.
         */
        private void UpdatePackageAndReferences()
        {
            Func<Microsoft.Build.Evaluation.Project, bool> toUpdateSolutionDirIfNecessary = CheckAndSetSolutionDir;
            Func<Microsoft.Build.Evaluation.Project, bool> toUpdatePackageAndReferences = project =>
                {
                    var modifiedProject = UpdateReferenceItems(project);
                    modifiedProject |= UpdatePackagesConfig(project.DirectoryPath);
                    /* if (UpdateAppConfig(project.DirectoryPath))
                    {
                        modifiedProject = true;
                        AddAppConfigToCSProjIfNecessary(project);
                    }
                    */

                    return modifiedProject;
                };
            WalkTheSolution(toUpdateSolutionDirIfNecessary);
            SaveSolution();

            WalkTheSolution(toUpdatePackageAndReferences);
            SaveSolution();
        }

        private void AddAppConfigToCSProjIfNecessary(Microsoft.Build.Evaluation.Project project)
        {
            if (!project.Items.Any(i => i.ItemType == "None" && i.EvaluatedInclude == "App.config"))
            {
                project.AddItem("None", "App.config");
            }
        }

        private bool UpdateReferenceItems(Microsoft.Build.Evaluation.Project buildProject)
        {
            var modifiedProject = false;
            foreach (var item in buildProject.Items.Where(i => i.ItemType == "Reference" 
                && i.DirectMetadataCount > 0
                && !i.EvaluatedInclude.StartsWith("System.")))
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
            if (!_items.Values.Any(n => n.Projects.Contains(projectPath)))
            {
                return false;
            }

            var modified = false;
            var filename = projectPath + @"\App.config";
            if (!File.Exists(filename))
            {
                modified = true;
                if (File.Exists(projectPath + @"\app.config"))
                {
                    // rename
                    File.Move(projectPath + @"\app.config", projectPath + @"\App.config");
                }
                else
                {
                    // create new from template
                    var content = Templates.AppConfig.EmptyConfig.Aggregate("", (current, line) => current + (line + "\n"));
                    File.WriteAllText(filename, content);
                }
            }

            var appConfig = File.ReadAllText(filename);
            var appConfigXml = XDocument.Parse(appConfig);
            if (appConfigXml.Root != null)
            {
                var root = appConfigXml.Root.Name.Namespace;

                var configurationItem = appConfigXml.Element("configuration") ?? new XElement(root + "configuration");
                var runtimeItem = configurationItem.Element("runtime") ?? new XElement(root + "runtime");

                var assemblyBinding = runtimeItem.Descendants().FirstOrDefault(d => d.Name.LocalName == "assemblyBinding");
                if (assemblyBinding == null)
                {
                    assemblyBinding = new XElement(root + "assemblyBinding");
                    runtimeItem.Add(assemblyBinding);
                    modified = true;
                } 

                var modifiedPackages = UpdateDependentAssemblies(assemblyBinding);
                modified |= modifiedPackages.Count > 0;

                var dependentAssemblies = assemblyBinding.Elements().Where(e => e.Name.LocalName == "dependentAssembly");
                var assemblyIdentities = dependentAssemblies.Elements().Where(d => d.Name.LocalName == "assemblyIdentity");
                var names = assemblyIdentities.Select(
                    d =>
                    {
                        var name = d.Attribute("name");
                        return name != null ? name.Value : null;
                    });

                foreach (var depAsm in _items.Values.Where(
                    i =>  
                        !modifiedPackages.Contains(i.RefName) 
                        && !names.Contains(i.RefName)).Select(
                            nuPackage => 
                                CreateDependentAssemblyElement(root, nuPackage)))
                {
                    assemblyBinding.Add(depAsm);
                    modified = true;
                }
            }

            if (modified)
            {
                _outputList.Add("Modified: " + filename);
                appConfigXml.Save(filename);
            }
            return modified;
        }

        private XElement CreateDependentAssemblyElement(XNamespace rootNameSpace, NuPackage nuPackage)
        {
            var asmIdent = new XElement(rootNameSpace + "assemblyIdentity");
            SetAssemblyIdentityAttributes(nuPackage, asmIdent);

            var bindRedir = new XElement(rootNameSpace + "bindingRedirect");
            SetBindingRedirectAttributes(nuPackage, bindRedir);

            return new XElement(rootNameSpace + "dependentAssembly", new object[] { asmIdent, bindRedir });
        }

        private void SetBindingRedirectAttributes(NuPackage nuPackage, XElement bindRedir)
        {
            bindRedir.SetAttributeValue("oldVersion", "0.0.0.0-65535.65535.65535.65535");
            bindRedir.SetAttributeValue("newVersion", nuPackage.Version);
        }

        private void SetAssemblyIdentityAttributes(NuPackage nuPackage, XElement asmIdent)
        {
            asmIdent.SetAttributeValue("name", nuPackage.RefName);
            var name = nuPackage.RefName;
            var keyToken = nuPackage.AssemblyAttributes["publicToken"];
            const string culture = "neutral";
            asmIdent.SetAttributeValue("name", name);
            if (!string.IsNullOrWhiteSpace(keyToken))
            {
                asmIdent.SetAttributeValue("publicKeyToken", keyToken);
            }
            asmIdent.SetAttributeValue("culture", culture);
        }

        private HashSet<string> UpdateDependentAssemblies(XContainer assemblyBinding)
        {
            var result = new HashSet<string>();
            foreach (var dependentAssembly in assemblyBinding.Elements().Where(i => i.Name.LocalName == "dependentAssembly"))
            {
                var modified = false;
                var assemblyIdentity = dependentAssembly.Descendants().FirstOrDefault(d => d.Name.LocalName == "assemblyIdentity");
                var bindingRedirect = dependentAssembly.Descendants().FirstOrDefault(d => d.Name.LocalName == "bindingRedirect");
                if (assemblyIdentity == null || bindingRedirect == null) continue;

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

                if (modified)
                {
                    result.Add(name);
                }
            }
            return result;
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
