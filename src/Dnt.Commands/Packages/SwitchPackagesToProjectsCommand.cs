﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dnt.Commands.Infrastructure;
using Dnt.Commands.Packages.Switcher;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using NConsole;

namespace Dnt.Commands.Packages
{
    [Command(Name = "switch-to-projects")]
    public class SwitchPackagesToProjectsCommand : CommandBase
    {
        [Argument(Position = 1)]
        public string Configuration { get; set; }

        public override async Task<object> RunAsync(CommandLineProcessor processor, IConsoleHost host)
        {
            var configuration = ReferenceSwitcherConfiguration.Load(Configuration);

            await AddProjectsToSolutionAsync(configuration, host);
            SwitchToProjects(configuration, host);

            configuration.Save();

            return null;
        }

        private async Task AddProjectsToSolutionAsync(ReferenceSwitcherConfiguration configuration, IConsoleHost host)
        {
            var solution = SolutionFile.Parse(configuration.ActualSolution);
            var projects = new List<string>();
            foreach (var mapping in configuration.Mappings)
            {
                if (solution.ProjectsInOrder.All(p => p.ProjectName != mapping.Key)) // check that it's not already in the solution
                {
                    projects.Add("\"" + mapping.Value.ActualPath + "\"");
                }
            }

            if (projects.Any())
            {
                await ExecuteCommandAsync("dotnet sln \"" + configuration.ActualSolution + "\" add " + string.Join(" ", projects), host);
            }
        }

        private static void SwitchToProjects(ReferenceSwitcherConfiguration configuration, IConsoleHost host)
        {
            var solution = SolutionFile.Parse(configuration.ActualSolution);
            foreach (var mapping in configuration.Mappings)
            {
                var packageName = mapping.Key;
                var projectPath = mapping.Value.ActualPath;

                var switchedProjects = SwitchToProject(configuration, solution, packageName, projectPath, host);
                foreach (var s in switchedProjects)
                {
                    host.WriteMessage(Path.GetFileName(s.Key) + ": \n");
                    host.WriteMessage("   " + packageName + " v" + s.Value + " => " + Path.GetFileName(projectPath) + "\n");
                }

                if (switchedProjects.Any())
                {
                    mapping.Value.Version = switchedProjects.First().Value;
                }
            }
        }

        private static IReadOnlyDictionary<string, string> SwitchToProject(ReferenceSwitcherConfiguration configuration, SolutionFile solution, string packageName, string projectPath, IConsoleHost host)
        {
            var switchedProjects = new Dictionary<string, string>();
            foreach (var solutionProject in solution.ProjectsInOrder)
            {
                if (solutionProject.ProjectType != SolutionProjectType.SolutionFolder)
                {
                    try
                    {
                        var projectInformation = ProjectExtensions.GetProject(solutionProject.AbsolutePath);
                        var project = projectInformation.Project;
                        var projectDirectory = Path.GetFullPath(Path.GetDirectoryName(solutionProject.AbsolutePath));

                        foreach (var item in project.Items.Where(i => i.ItemType == "PackageReference" || i.ItemType == "Reference").ToList())
                        {
                            var packageReference = item.EvaluatedInclude.Split(',').First().Trim();

                            if (packageReference == packageName)
                            {
                                var isPackageReference = (item.ItemType == "PackageReference");
                                var packageVersion = item.Metadata.SingleOrDefault(m => m.Name == "Version")?.EvaluatedValue ?? null;

                                project.RemoveItem(item);
                                project.AddItem("ProjectReference", PathUtilities.ToRelativePath(projectPath, projectDirectory));

                                SetSwitchedProjectInformation(configuration, item, project.FullPath, isPackageReference, packageName, packageVersion);

                                switchedProjects[solutionProject.AbsolutePath] = packageVersion;
                            }
                        }

                        project.Save();
                    }
                    catch (Exception e)
                    {
                        host.WriteError("The project '" + solutionProject.AbsolutePath + "' could not be loaded: " + e.Message + "\n");
                    }
                }
            }

            return switchedProjects;
        }

        private static void SetSwitchedProjectInformation(ReferenceSwitcherConfiguration configuration, ProjectItem item, string projectFullPath, bool isPackageReference, string packageName, string packageVersion)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFullPath);

            var switchedProject =
                (from m in configuration.SwitchedProjects
                 where string.Equals(m.Name, projectName, StringComparison.OrdinalIgnoreCase)
                 select m).FirstOrDefault();

            if (switchedProject is null)
            {
                switchedProject = new SwitchedProject { Name = projectName };
                configuration.SwitchedProjects.Add(switchedProject);
            }

            SwitchedPackage switchedPackage = null;

            if (switchedProject.Packages != null)
            {
                switchedPackage = (
                    from r in switchedProject.Packages
                    where string.Equals(r.PackageName, packageName, StringComparison.OrdinalIgnoreCase)
                    select r).FirstOrDefault();
            }
            else
            {
                switchedProject.Packages = new List<SwitchedPackage>();
            }

            if (switchedPackage is null)
            {
                switchedPackage = new SwitchedPackage();
                switchedProject.Packages.Add(switchedPackage);
            }

            if (!isPackageReference)
            {
                switchedPackage.Include = item.EvaluatedInclude;

                foreach (var metadata in item.Metadata)
                {
                    switchedPackage.Metadata.Add(new KeyValuePair<string, string>(metadata.Name, metadata.EvaluatedValue));
                }
            }

            switchedPackage.PackageName = packageName;
            switchedPackage.PackageVersion = packageVersion;
        }
    }
}
