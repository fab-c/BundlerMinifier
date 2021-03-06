﻿using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace BundlerMinifierVsix.Commands
{
    internal sealed class ConvertToGulp
    {
        private readonly Package _package;
        private const string GULP_FILE_NAME = "gulpfile.js";

        private ConvertToGulp(Package package)
        {
            _package = package;

            var commandService = (OleMenuCommandService)ServiceProvider.GetService(typeof(IMenuCommandService));
            if (commandService != null)
            {
                var menuCommandID = new CommandID(PackageGuids.guidBundlerCmdSet, PackageIds.ConvertToGulp);
                var menuItem = new OleMenuCommand(Execute, menuCommandID);
                menuItem.BeforeQueryStatus += BeforeQueryStatus;
                commandService.AddCommand(menuItem);
            }
        }

        public static ConvertToGulp Instance { get; private set; }

        private IServiceProvider ServiceProvider
        {
            get { return _package; }
        }

        public static void Initialize(Package package)
        {
            Instance = new ConvertToGulp(package);
        }

        private void BeforeQueryStatus(object sender, EventArgs e)
        {
            var button = (OleMenuCommand)sender;
            var files = ProjectHelpers.GetSelectedItemPaths();
            button.Visible = button.Enabled = false;

            int count = files.Count();

            if (count == 0) // Project
            {
                var project = ProjectHelpers.GetActiveProject();

                if (project == null)
                    return;

                string config = project.GetConfigFile();

                if (!string.IsNullOrEmpty(config) && File.Exists(config))
                {
                    button.Visible = true;
                    var root = project.GetRootFolder();
                    var gulpFile = Path.Combine(root, GULP_FILE_NAME);
                    button.Enabled = !File.Exists(gulpFile);
                }
            }
            else
            {
                button.Visible = files.Count() == 1 && Path.GetFileName(files.First()) == Constants.CONFIG_FILENAME;

                if (button.Visible)
                {
                    var root = ProjectHelpers.GetActiveProject()?.GetRootFolder();
                    var gulpFile = Path.Combine(root, GULP_FILE_NAME);
                    button.Enabled = !File.Exists(gulpFile);
                }
            }

            if (button.Enabled)
            {
                button.Text = "Convert To Gulp...";
            }
            else
            {
                button.Text = $"Convert To Gulp... ({GULP_FILE_NAME} already exists)";
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            var question = $"This will generate {GULP_FILE_NAME} and install the npm packages needed (it will take a minute or two).\r\nNo files will be deleted.\r\n\r\nDo you wish to continue?";
            var answer = MessageBox.Show(question, Vsix.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
                return;

            var project = ProjectHelpers.GetActiveProject();
            var root = project.GetRootFolder();

            var bundleConfigFile = Path.Combine(root, Constants.CONFIG_FILENAME);
            StripCommentsFromJsonFile(bundleConfigFile);

            var packageFile = Path.Combine(root, "package.json");
            var gulpFile = Path.Combine(root, GULP_FILE_NAME);

            CreateFileAndIncludeInProject(project, packageFile);
            CreateFileAndIncludeInProject(project, gulpFile);

            BundlerMinifierPackage._dte.StatusBar.Text = "Installing node modules...";
            InstallNodeModules(Dispatcher.CurrentDispatcher, root, "del", "gulp", "gulp-concat", "gulp-cssmin", "gulp-htmlmin", "gulp-uglify", "merge-stream");
            BundleService.ToggleOutputProduction(project.GetConfigFile(), false);
        }

        private static void InstallNodeModules(Dispatcher dispatcher, string root, params string[] modules)
        {
            System.Threading.ThreadPool.QueueUserWorkItem((o) =>
            {
                bool hasErrors = false;

                for (int i = 0; i < modules.Length; i++)
                {
                    var module = modules[i];

                    try
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            BundlerMinifierPackage._dte.StatusBar.Progress(true, $"Installing {module}...", i + 1, modules.Length + 1);
                        }), DispatcherPriority.Normal, null);

                        var start = new ProcessStartInfo
                        {
                            WorkingDirectory = root,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true,
                            FileName = "cmd.exe",
                            Arguments = $"/c npm install {module} --save-dev",
                            RedirectStandardError = true,
                            RedirectStandardOutput = true,
                            StandardErrorEncoding = Encoding.UTF8,
                            StandardOutputEncoding = Encoding.UTF8,
                        };

                        ModifyPathVariable(start);

                        using (var p = System.Diagnostics.Process.Start(start))
                        {
                            p.BeginErrorReadLine();
                            p.BeginOutputReadLine();
                            p.OutputDataReceived += OutputDataReceived;
                            p.ErrorDataReceived += OutputDataReceived;

                            p.WaitForExit();

                            hasErrors |= p.ExitCode != 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex);
                    }
                }

                dispatcher.BeginInvoke(new Action(() =>
                {
                    BundlerMinifierPackage._dte.StatusBar.Progress(false, "Node modules installed", 1, 1);

                    if (!hasErrors)
                        BundlerMinifierPackage._dte.StatusBar.Text = "Node modules installed";
                    else
                        BundlerMinifierPackage._dte.StatusBar.Text = "Error installing node modules. See output window for details";
                }), DispatcherPriority.ApplicationIdle, null);
            });
        }

        private static void OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
                Logger.Log(e.Data);
        }

        private static void ModifyPathVariable(ProcessStartInfo start)
        {
            string path = ".\\node_modules\\.bin" + ";" + start.EnvironmentVariables["PATH"];

            var process = System.Diagnostics.Process.GetCurrentProcess();
            string ideDir = Path.GetDirectoryName(process.MainModule.FileName);

            if (Directory.Exists(ideDir))
            {
                string parent = Directory.GetParent(ideDir).Parent.FullName;

                string rc2Preview1Path = new DirectoryInfo(Path.Combine(parent, @"Web\External")).FullName;

                if (Directory.Exists(rc2Preview1Path))
                {
                    path += ";" + rc2Preview1Path;
                }
                else
                {
                    path += ";" + Path.Combine(ideDir, @"Extensions\Microsoft\Web Tools\External");
                }
            }

            start.EnvironmentVariables["PATH"] = path;
        }

        private static void StripCommentsFromJsonFile(string fileName)
        {
            string fileContent = File.ReadAllText(fileName);
            var loadSettings = new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore
            };
            string filteredFileContent = JArray.Parse(fileContent, loadSettings).ToString();

            if (fileContent != filteredFileContent)
            {
                ProjectHelpers.CheckFileOutOfSourceControl(fileName);
                File.WriteAllText(fileName, filteredFileContent, Encoding.UTF8);
            }
        }

        private static void CreateFileAndIncludeInProject(Project project, string fileName)
        {
            if (File.Exists(fileName))
                return;

            string resourceFile = Path.GetFileName(fileName);

            string assembly = Assembly.GetExecutingAssembly().Location;
            string folder = Path.GetDirectoryName(assembly);
            string sourceFile = Path.Combine(folder, "Resources\\Files\\", resourceFile);

            File.Copy(sourceFile, fileName);
            project.AddFileToProject(fileName, "None");
        }
    }
}
