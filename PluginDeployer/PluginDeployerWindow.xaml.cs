﻿using D365DeveloperExtensions.Core;
using D365DeveloperExtensions.Core.Connection;
using D365DeveloperExtensions.Core.ExtensionMethods;
using D365DeveloperExtensions.Core.Models;
using D365DeveloperExtensions.Core.Vs;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using NLog;
using PluginDeployer.Resources;
using PluginDeployer.Spkl;
using PluginDeployer.Spkl.Tasks;
using PluginDeployer.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Assembly = PluginDeployer.Crm.Assembly;
using Task = System.Threading.Tasks.Task;

namespace PluginDeployer
{
    public partial class PluginDeployerWindow : INotifyPropertyChanged
    {
        #region Private

        private readonly DTE _dte;
        private readonly EnvDTE.Solution _solution;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private bool _isIlMergeInstalled;
        private ObservableCollection<CrmSolution> _crmSolutions;
        private ObservableCollection<CrmAssembly> _crmAssemblies;

        #endregion

        #region Public

        public ObservableCollection<CrmSolution> CrmSolutions
        {
            get => _crmSolutions;
            set
            {
                _crmSolutions = value;
                OnPropertyChanged();
            }
        }
        public ObservableCollection<CrmAssembly> CrmAssemblies
        {
            get => _crmAssemblies;
            set
            {
                _crmAssemblies = value;
                OnPropertyChanged();
            }
        }

        #endregion

        #region Events

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        public PluginDeployerWindow()
        {
            InitializeComponent();
            DataContext = this;

            _dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (_dte == null)
                return;

            _solution = _dte.Solution;
            if (_solution == null)
                return;

            var events = _dte.Events;
            var windowEvents = events.WindowEvents;
            windowEvents.WindowActivated += WindowEventsOnWindowActivated;
        }

        private void WindowEventsOnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            //No solution loaded
            if (_solution.Count == 0)
            {
                ResetForm();
                return;
            }

            //WindowEventsOnWindowActivated in this project can be called when activating another window
            //so we don't want to contine further unless our window is active
            if (!HostWindow.IsD365DevExWindow(gotFocus))
                return;

            //Data is populated already
            if (_crmSolutions != null)
                return;

            if (ConnPane.CrmService?.IsReady == true)
                InitializeForm();
        }

        private void InitializeForm()
        {
            ResetCollections();
            LoadData();
            ProjectName.Content = ConnPane.SelectedProject.Name;
            SetWindowCaption(_dte.ActiveWindow.Caption);
        }

        private void ConnPane_OnConnected(object sender, ConnectEventArgs e)
        {
            InitializeForm();
        }

        private void ResetCollections()
        {
            _crmSolutions = new ObservableCollection<CrmSolution>();
            _crmAssemblies = new ObservableCollection<CrmAssembly>();
        }

        private async void LoadData()
        {
            if (DeploymentType.ItemsSource == null)
            {
                DeploymentType.ItemsSource = AssemblyDeploymentTypes.Types;
                DeploymentType.SelectedIndex = 0;
            }

            await GetCrmData();
        }

        private void SetWindowCaption(string currentCaption)
        {
            _dte.ActiveWindow.Caption = HostWindow.GetCaption(currentCaption, ConnPane.CrmService);
        }

        private void ConnPane_OnSolutionBeforeClosing(object sender, EventArgs e)
        {
            ResetForm();

            ClearConnection();
        }

        private void ConnPane_OnSolutionOpened(object sender, EventArgs e)
        {
            ClearConnection();
        }

        private void ClearConnection()
        {
            ConnPane.IsConnected = false;
            ConnPane.CrmService?.Dispose();
            ConnPane.CrmService = null;
        }

        private void ConnPane_OnSolutionProjectRemoved(object sender, SolutionProjectRemovedEventArgs e)
        {
            Project project = e.Project;
            if (ConnPane.SelectedProject == project)
                ResetForm();
        }

        private void ConnPane_OnSolutionProjectRenamed(object sender, SolutionProjectRenamedEventArgs e)
        {
            Project project = e.Project;
            if (ConnPane.SelectedProject == project)
                ProjectName.Content = ConnPane.SelectedProject.Name;
        }

        private void ResetForm()
        {
            ResetCollections();
            SolutionList.ItemsSource = null;
            DeploymentType.ItemsSource = null;
            ProjectName.Content = string.Empty;
            BackupFiles.IsChecked = false;
        }

        private async Task GetCrmData()
        {
            ConnPane.CollapsePane();

            bool result = false;

            try
            {
                Overlay.ShowMessage(_dte, $"{Resource.Message_RetrievingSolutions}...", vsStatusAnimation.vsStatusAnimationSync);

                var solutionTask = GetSolutions();
                await Task.WhenAll(solutionTask);
                result = solutionTask.Result;
            }
            finally
            {
                Overlay.HideMessage(_dte, vsStatusAnimation.vsStatusAnimationSync);

                if (!result)
                    MessageBox.Show(Resource.MessageBox_ErrorRetrievingSolutions);
            }
        }

        private async Task<bool> GetSolutions()
        {
            EntityCollection results = await Task.Run(() => Crm.Solution.RetrieveSolutionsFromCrm(ConnPane.CrmService));
            if (results == null)
                return false;

            _crmSolutions = ModelBuilder.CreateCrmSolutionView(results);

            SolutionList.ItemsSource = _crmSolutions;
            SolutionList.SelectedIndex = 0;

            return true;
        }

        private async void Publish_OnClick(object sender, RoutedEventArgs e)
        {
            int deploymentType = (int)DeploymentType.SelectedValue;
            CrmSolution solution = (CrmSolution)SolutionList.SelectedItem;

            switch (deploymentType)
            {
                case 1:
                    bool backupFiles = BackupFiles.ReturnValue();
                    await Task.Run(() => PublishAssemblySpklAsync(solution, backupFiles));
                    break;
                default:
                    await Task.Run(() => PublishAssemblyAsync(solution));
                    break;
            }
        }

        private async Task PublishAssemblyAsync(CrmSolution solution)
        {
            if (!ProjectWorker.BuildProject(ConnPane.SelectedProject))
                return;

            try
            {
                Overlay.ShowMessage(_dte, $"{Resource.Message_Deploying}...", vsStatusAnimation.vsStatusAnimationDeploy);

                string projectAssemblyName = ConnPane.SelectedProject.Properties.Item("AssemblyName").Value.ToString();
                string assemblyFolderPath = ProjectWorker.GetOutputPath(ConnPane.SelectedProject);
                if (!AssemblyValidation.ValidateAssemblyPath(assemblyFolderPath))
                    return;

                bool isWorkflow = ProjectWorker.IsWorkflowProject(ConnPane.SelectedProject);
                string assemblyFilePath = ProjectWorker.GetAssemblyPath(ConnPane.SelectedProject);
                string[] assemblyProperties = SpklHelpers.AssemblyProperties(assemblyFilePath, isWorkflow);

                var assembly = ModelBuilder.CreateCrmAssembly(projectAssemblyName, assemblyFilePath, assemblyProperties);

                Entity foundAssembly = Assembly.RetrieveAssemblyFromCrm(ConnPane.CrmService, projectAssemblyName);
                if (foundAssembly != null)
                {
                    Version projectAssemblyVersion = Versioning.StringToVersion(assemblyProperties[2]);

                    if (!AssemblyValidation.ValidateAssemblyVersion(ConnPane.CrmService, foundAssembly, projectAssemblyName, projectAssemblyVersion))
                        return;

                    assembly.AssemblyId = foundAssembly.Id;
                }

                Guid assemblyId = await Task.Run(() => Assembly.UpdateCrmAssembly(ConnPane.CrmService, assembly));
                if (assemblyId == Guid.Empty)
                    MessageBox.Show(Resource.MEssageBox_ErrorDeployingAssembly);

                if (foundAssembly == null)
                    CreatePluginType(assemblyProperties, assemblyId, assemblyFilePath, isWorkflow);

                if (solution.SolutionId == ExtensionConstants.DefaultSolutionId)
                    return;

                bool alreadyInSolution = Assembly.IsAssemblyInSolution(ConnPane.CrmService, projectAssemblyName, solution.UniqueName);
                if (alreadyInSolution)
                    return;

                bool result = Assembly.AddAssemblyToSolution(ConnPane.CrmService, assemblyId, solution.UniqueName);
                if (!result)
                    MessageBox.Show(Resource.MessageBox_ErrorAddingAssemblyToSolution);
            }
            finally
            {
                Overlay.HideMessage(_dte, vsStatusAnimation.vsStatusAnimationDeploy);
            }
        }

        private void CreatePluginType(string[] assemblyProperties, Guid assemblyId, string assemblyFilePath, bool isWorkflow)
        {
            List<CrmPluginRegistrationAttribute> crmPluginRegistrationAttributes = new List<CrmPluginRegistrationAttribute>();
            CrmPluginRegistrationAttribute crmPluginRegistrationAttribute =
                new CrmPluginRegistrationAttribute(ConnPane.SelectedProject.Name, Guid.NewGuid().ToString(),
                    String.Empty, $"{ConnPane.SelectedProject.Name} ({assemblyProperties[2]})", IsolationModeEnum.Sandbox);

            crmPluginRegistrationAttributes.Add(crmPluginRegistrationAttribute);
            PluginAssembly pluginAssembly = new PluginAssembly { Id = assemblyId };
            string assemblyFullName = SpklHelpers.AssemblyFullName(assemblyFilePath, isWorkflow);

            var service = (IOrganizationService)ConnPane.CrmService.OrganizationServiceProxy ?? ConnPane.CrmService.OrganizationWebProxyClient;
            var ctx = new OrganizationServiceContext(service);

            using (ctx)
            {
                PluginRegistraton pluginRegistraton = new PluginRegistraton(service, ctx, new TraceLogger());

                pluginRegistraton.RegisterActivities(crmPluginRegistrationAttributes, pluginAssembly, assemblyFullName);
            }
        }

        private async Task PublishAssemblySpklAsync(CrmSolution solution, bool backupFiles)
        {
            if (!ProjectWorker.BuildProject(ConnPane.SelectedProject))
                return;

            try
            {
                Overlay.ShowMessage(_dte, $"{Resource.Message_Deploying}...", vsStatusAnimation.vsStatusAnimationDeploy);

                PluginDeployConfig pluginDeployConfig = Config.Mapping.GetSpklPluginConfig(ConnPane.SelectedProject, ConnPane.SelectedProfile);
                if (!AssemblyValidation.ValidatePluginDeployConfig(pluginDeployConfig))
                    return;

                string projectPath = ProjectWorker.GetProjectPath(ConnPane.SelectedProject);
                string assemblyFolderPath = Path.Combine(projectPath, pluginDeployConfig.assemblypath);
                if (!AssemblyValidation.ValidateAssemblyPath(assemblyFolderPath))
                    return;

                bool isWorkflow = ProjectWorker.IsWorkflowProject(ConnPane.SelectedProject);
                string assemblyFilePath = Path.Combine(assemblyFolderPath, ConnPane.SelectedProject.Properties.Item("OutputFileName").Value.ToString());
                if (!AssemblyValidation.ValidateRegistraionDetails(assemblyFilePath, isWorkflow))
                    return;

                string[] assemblyProperties = SpklHelpers.AssemblyProperties(assemblyFilePath, isWorkflow);
                Version projectAssemblyVersion = Version.Parse(assemblyProperties[2]);

                string projectAssemblyName = ConnPane.SelectedProject.Properties.Item("AssemblyName").Value.ToString();
                Entity foundAssembly = Assembly.RetrieveAssemblyFromCrm(ConnPane.CrmService, projectAssemblyName);
                if (foundAssembly != null)
                {
                    if (!AssemblyValidation.ValidateAssemblyVersion(ConnPane.CrmService, foundAssembly, projectAssemblyName, projectAssemblyVersion))
                        return;
                }

                string solutionName = solution.SolutionId != ExtensionConstants.DefaultSolutionId
                    ? solution.UniqueName
                    : null;

                var service = (IOrganizationService)ConnPane.CrmService.OrganizationServiceProxy ?? ConnPane.CrmService.OrganizationWebProxyClient;
                var ctx = new OrganizationServiceContext(service);

                using (ctx)
                {
                    PluginRegistraton pluginRegistraton = new PluginRegistraton(service, ctx, new TraceLogger());

                    if (isWorkflow)
                    {
                        await Task.Run(() => pluginRegistraton.RegisterWorkflowActivities(assemblyFilePath, solutionName));
                    }
                    else
                    {
                        await Task.Run(() => pluginRegistraton.RegisterPlugin(assemblyFilePath, solutionName));
                    }

                    GetRegistrationDetailsWithContext(pluginDeployConfig.classRegex, backupFiles, ctx);
                }
            }
            finally
            {
                Overlay.HideMessage(_dte, vsStatusAnimation.vsStatusAnimationDeploy);
            }
        }

        private void IlMerge_OnClick(object sender, RoutedEventArgs e)
        {
            if (ConnPane.SelectedProject == null)
                return;

            _isIlMergeInstalled = IlMergeHandler.TogleIlMerge(_dte, ConnPane.SelectedProject, _isIlMergeInstalled);
        }

        private void AddRegistration_OnClick(object sender, RoutedEventArgs e)
        {
            PluginDeployConfig pluginDeployConfig = Config.Mapping.GetSpklPluginConfig(ConnPane.SelectedProject, ConnPane.SelectedProfile);
            if (pluginDeployConfig == null)
            {
                MessageBox.Show($"{Resource.MessageBox_MissingPluginsSpklConfig}: {ExtensionConstants.SpklConfigFile}");
                return;
            }

            bool hasRegAttributeClass = SpklHelpers.RegAttributeDefinitionExists(_dte, ConnPane.SelectedProject);
            if (!hasRegAttributeClass)
            {
                //TODO: If VB support is added this would need to be addressed
                TemplateHandler.AddFileFromTemplate(ConnPane.SelectedProject,
                    "CSharpSpklRegAttributes\\CSharpSpklRegAttributes", $"{ExtensionConstants.SpklRegAttrClassName}.cs");
            }

            GetRegistrationDetailsWithoutContext(pluginDeployConfig.classRegex, BackupFiles.ReturnValue());
        }

        private void GetRegistrationDetailsWithContext(string customClassRegex, bool backupFiles, OrganizationServiceContext ctx)
        {
            try
            {
                Overlay.ShowMessage(_dte, $"{Resource.Message_AddingRegistrationDetails}...", vsStatusAnimation.vsStatusAnimationSync);

                Project project = ConnPane.SelectedProject;
                ProjectWorker.BuildProject(project);

                string path = Path.GetDirectoryName(project.FullName);

                DownloadPluginMetadataTask downloadPluginMetadataTask = new DownloadPluginMetadataTask(ctx, new TraceLogger());
                downloadPluginMetadataTask.Execute(path, backupFiles, customClassRegex);
            }
            finally
            {
                Overlay.HideMessage(_dte, vsStatusAnimation.vsStatusAnimationSync);
            }
        }

        private void GetRegistrationDetailsWithoutContext(string customClassRegex, bool backupFiles)
        {
            var service = (IOrganizationService)ConnPane.CrmService.OrganizationServiceProxy ?? ConnPane.CrmService.OrganizationWebProxyClient;
            var ctx = new OrganizationServiceContext(service);

            GetRegistrationDetailsWithContext(customClassRegex, backupFiles, ctx);
        }

        private void RegistrationTool_OnClick(object sender, RoutedEventArgs e)
        {
            PrtHelper.OpenPrt();
        }

        private void ConnPane_SelectedProjectChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConnPane.SelectedProject == null)
                return;

            ProjectName.Content = ConnPane.SelectedProject.Name;
        }

        private void OpenInCrm_Click(object sender, RoutedEventArgs e)
        {
            CrmSolution solution = (CrmSolution)SolutionList.SelectedItem;

            D365DeveloperExtensions.Core.WebBrowser.OpenCrmPage(_dte, ConnPane.CrmService, $"tools/solution/edit.aspx?id=%7b{solution.SolutionId}%7d");
        }
    }
}