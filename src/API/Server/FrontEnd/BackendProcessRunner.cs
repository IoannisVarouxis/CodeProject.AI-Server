﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using CodeProject.SenseAI.API.Common;
using CodeProject.SenseAI.API.Server.Backend;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeProject.SenseAI.API.Server.Frontend
{
    /// <summary>
    /// This background process manages the startup and shutdown of the backend processes.
    /// </summary>
    public class BackendProcessRunner : BackgroundService
    {
        // marker for path substitution
        const string RootPathMarker       = "%ROOT_PATH%";
        const string ModulesPathMarker    = "%MODULES_PATH%";
        const string PlatformMarker       = "%PLATFORM%";
        const string PythonBasePathMarker = "%PYTHON_BASEPATH%";
        const string Python37PathMarker   = "%PYTHON37_PATH%";

        private readonly FrontendOptions               _options;
        private readonly IConfiguration                _config;
        private readonly ILogger<BackendProcessRunner> _logger;
        private readonly QueueServices                 _queueServices;
        private readonly Dictionary<string, string?>   _backendEnvironmentVars = new();
        private readonly List<Process>                 _runningProcesses = new();
        private readonly string?                       _appDataDirectory;

        private string _platform = OSPlatform.Linux.ToString();

        /// <summary>
        /// Gets a list of the startup processes.
        /// </summary>
        public StartupProcess[] StartupProcesses
        {
            get { return _options?.StartupProcesses ?? Array.Empty<StartupProcess>(); }
        }

        /// <summary>
        /// Gets a list of the processes names and statuses.
        /// </summary>
        public Dictionary<string, bool> ProcessStatuses
        {
            get
            {
                return StartupProcesses.ToDictionary(cmd => cmd.Name ?? "Unknown",
                                                     cmd => cmd.Running ?? false);
            }
        }

        /// <summary>
        /// Gets the backend process status for a queue.
        /// </summary>
        /// <param name="queueName">The Queue Name.</param>
        /// <returns>The status for the backend process, or false if the queue is invalid.</returns>
        public bool GetStatusForQueue(string queueName)
        {
            return StartupProcesses.FirstOrDefault(cmd => cmd.Queues!.Any(x => string.Compare(x, queueName, true) == 0))
                ?.Running ?? false;
        }

        /// <summary>
        /// Initialises a new instance of the BackendProcessRunner.
        /// </summary>
        /// <param name="options">The FrontendOptions</param>
        /// <param name="config">The application configuration.</param>
        /// <param name="queueServices">The Queue management service.</param>
        /// <param name="logger">The logger.</param>
        public BackendProcessRunner(IOptions<FrontendOptions> options,
                                    IConfiguration config,
                                    QueueServices queueServices,
                                    ILogger<BackendProcessRunner> logger)
        {
            _options          = options.Value;
            _config           = config;
            _logger           = logger;
            _queueServices    = queueServices;
            _appDataDirectory = config.GetValue<string>("ApplicationDataDir");

            ExpandOptions();
            BuildBackendEnvironmentVar();
        }

        /// <inheritdoc></inheritdoc>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("BackendProcessRunner Start");
            return base.StartAsync(cancellationToken);
        }

        /// <inheritdoc></inheritdoc>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("BackendProcessRunner Stop");

            /*
            foreach (var process in _runningProcesses)
            {
                if (!process.HasExited)
                {
                    _logger.LogInformation($"Killing process: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                    process.Kill(true);
                }
            }
            */

            // Doing the above in Parallel speeds things up
            Parallel.ForEach(_runningProcesses.Where(x => !x.HasExited), process =>
            {
                // TODO: First send a "Shutdown" message to each process and wait a second or two.
                //       If the process continues to run, then we get serious.
                process.Kill(true);
            });

            /* Not need at this point
            foreach (var cmdInfo in _options.StartupProcesses)
            {
                cmdInfo.Running = false;
            }
            */

            return base.StopAsync(cancellationToken);
        }

        /// <inheritdoc></inheritdoc>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            bool loggerIsValid = true;// You just never know, right?

            try
            {
                if (_options.StartupProcesses is null)
                {
                    _logger.LogInformation("No Background AI Modules specified");
                    Logger.Log("No Background AI Modules specified");
                    return;
                }

                _logger.LogInformation("Starting Background AI Modules");
                Logger.Log("Starting Background AI Modules");
            }
            catch
            {
                loggerIsValid = false;
            }

            // Let's make sure the front end is up and running before we start the backend 
            // analysis services
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            if (loggerIsValid)
            {
                _logger.LogInformation($"Root Path:      {_options.ROOT_PATH}");
                _logger.LogInformation($"Module Path:    {_options.MODULES_PATH} [full: {Path.GetFullPath(_options.MODULES_PATH!)}]");
                _logger.LogInformation($"Python3.7 Path: {_options.PYTHON37_PATH}");
                Logger.Log($"Root path is {_options.ROOT_PATH}");
            }

            foreach (var cmdInfo in _options.StartupProcesses!)
            {
                cmdInfo.Running = false;

                if (stoppingToken.IsCancellationRequested)
                    break;

                bool activate = cmdInfo.Activate ?? false;
                bool enabled = activate;
                foreach (var envVar in cmdInfo.EnableFlags)
                    enabled = enabled || _config.GetValue(envVar, false);

                if (!enabled)
                    Logger.Log($"Not starting {cmdInfo.Name}: Not set as enabled");

                if (enabled && !cmdInfo.Platforms!.Any(platform => platform.ToLower() == _platform.ToLower()))
                {
                    enabled = false;
                    Logger.Log($"Not starting {cmdInfo.Name}: Not anabled for {_platform}");
                }

                if (enabled && !string.IsNullOrEmpty(cmdInfo.Command))
                {
                    // _logger.LogError($"Starting {cmdInfo.Command}");

                    ProcessStartInfo? procStartInfo = new($"\"{cmdInfo.Command}\"", $"\"{cmdInfo.Args ?? ""}\"")
                    {
                        UseShellExecute = false
                    };

                    // setup the environment
                    foreach (var kv in _backendEnvironmentVars)
                        procStartInfo.Environment.TryAdd(kv.Key, kv.Value);

                    // create the required Queues
                    foreach (var queueName in cmdInfo.Queues)
                    if (!string.IsNullOrWhiteSpace(queueName))
                        _queueServices.EnsureQueueExists(queueName);

                    try
                    {
                        if (loggerIsValid)
                            _logger.LogDebug($"Starting {procStartInfo.FileName} {procStartInfo.Arguments}");

                        Process? process = Process.Start(procStartInfo);
                        if (process is not null)
                        {
                            if (loggerIsValid)
                                _logger.LogInformation($"Started {cmdInfo.Name} backend");

                            _runningProcesses.Add(process);
                            cmdInfo.Running = true;

                            Logger.Log($"Started {cmdInfo.Name}");
                        }
                        else
                        {
                            if (loggerIsValid)
                                _logger.LogError($"Unable to start {cmdInfo.Name} backend");
                            Logger.Log($"Unable to start {cmdInfo.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (loggerIsValid)
                            _logger.LogError(ex, $"Error trying to start { cmdInfo.Name} backend from {cmdInfo.Command} {cmdInfo.Args}");
                        Logger.Log($"Error running {cmdInfo.Command} {cmdInfo.Args}");
#if DEBUG
                        if (_platform == "windows")
                            Logger.Log($"    Run /Installers/Dev/setup_dev_env_win.bat");
                        else
                            Logger.Log($"    In /Installers/Dev/, run 'bash setup_dev_env_linux.sh'");
                            Logger.Log($" ** Did you setup the Development environment?");
#else
                        Logger.Log($"Please check the SenseAI installation completed successfully");
#endif
                    }
                }
            }
        }

        /// <summary>
        /// Expands all the directory markers in the options.
        /// </summary>
        private void ExpandOptions()
        {
            if (_options is null)
                return;

            // This is slightly inconvenient
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _platform = OSPlatform.Windows.ToString();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                _platform = OSPlatform.OSX.ToString();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _platform = OSPlatform.Linux.ToString();

            _platform = _platform.ToLower();

            // For Macro expansion in appsettings settings we have PYTHON37_PATH which depends on
            // PYTHON_BASEPATH which usually depends on MODULES_PATH and both depend on ROOT_PATH.
            // Get and expand each of these in the correct order.

            /*
            // It's assumed that this application will be under the /working-dir/src/API/FrontEnd 
            // directory, and will either be in the FrontEnd folder directly (Production) or buried
            // deeeep in the /bin/Debug/net/ etc etc bowels of the folder system. Dig to the surface.

            ** Disablng this because once this is installed in prod the directory structure is very
               different. Let's stick to hard-coding in appsettings and vigilence for the moment.

            DirectoryInfo currentDir = new(AppContext.BaseDirectory);
            if (_options.API_DIRNAME != null)
            {
                // Grab a shovel and dig up towards the API directory
                while (currentDir.Parent != null && currentDir.Name.ToLower() != _options.API_DIRNAME.ToLower())
                    currentDir = currentDir.Parent;

                // Up to the src directory
                if (currentDir != null && currentDir.Parent != null)
                    currentDir = currentDir.Parent;

                // Up to the root directory
                if (currentDir != null && currentDir.Parent != null)
                    currentDir = currentDir.Parent;
            }

            if (!string.IsNullOrEmpty(currentDir?.FullName))
                _options.ROOT_PATH = currentDir?.FullName;
            else
                _options.ROOT_PATH   = Path.GetFullPath(_options.ROOT_PATH!);
            */

            // Be careful with cross platform handling of \ vs /
            _options.ROOT_PATH       = _options.ROOT_PATH!.Replace('\\', Path.DirectorySeparatorChar);
            // ROOT_PATH points to the root directory of SenseAI, relative to the current app.
            // Change this so that it's an actual, physical path.
            _options.ROOT_PATH       = Path.Combine(AppContext.BaseDirectory, _options.ROOT_PATH!);
            // GetFullPath converts ".."'s to the correct relative path
            _options.ROOT_PATH       = Path.GetFullPath(_options.ROOT_PATH); 
                            
            _options.PYTHON_BASEPATH = Path.GetFullPath(ExpandOption(_options.PYTHON_BASEPATH)!);
            _options.PYTHON37_PATH   = Path.GetFullPath(ExpandOption(_options.PYTHON37_PATH)!);

            if (_options.StartupProcesses is not null)
            {
                foreach (var backend in _options.StartupProcesses)
                {
                    backend.Command = ExpandOption(backend.Command);
                    backend.Args    = ExpandOption(backend.Args);
                }
            }
        }

        /// <summary>
        /// Expands the directory markers in the string.
        /// </summary>
        /// <param name="value">The value to expand.</param>
        /// <returns>The expanded path.</returns>
        private string? ExpandOption(string? value)
        {
            if (value is null)
                return null;

            value = value.Replace(ModulesPathMarker,    _options.MODULES_PATH);
            value = value.Replace(RootPathMarker,       _options.ROOT_PATH);
            value = value.Replace(PlatformMarker,       _platform);
            value = value.Replace(PythonBasePathMarker, _options.PYTHON_BASEPATH);
            value = value.Replace(Python37PathMarker,   _options.PYTHON37_PATH);

            value = value.Replace('\\', Path.DirectorySeparatorChar);

            return value;
        }

        /// <summary>
        /// Creates the collection of backend environment variables.
        /// </summary>
        private void BuildBackendEnvironmentVar()
        {
            if (_options.BackendEnvironmentVariables != null)
            {
                foreach (var entry in _options.BackendEnvironmentVariables)
                    _backendEnvironmentVars.Add(entry.Key, ExpandOption(entry.Value.ToString()));

                // A bit of a hack for the Vision Python legacy module that requires a directory
                // for storing a SQLite DB. We'll force it to store the data in the standard
                // application data directory as per the current OS. This is required because the
                // app may very well be installed in a directory that doesn't provide write
                // permission. So: have the writes done in a spot where we know we have permission.
                _backendEnvironmentVars["DATA_DIR"] = _appDataDirectory;

                foreach (var envVar in _backendEnvironmentVars)
                {
                    _logger.LogDebug($"[{envVar.Key}] = [{envVar.Value}]");
                }
            }
        }
    }

    /// <summary>
    /// Extension methods for the BackendProcessRunner.
    /// </summary>
    public static class BackendProcessRunnerExtensions
    {
        /// <summary>
        /// Sets up the BackendProcessRunner.
        /// </summary>
        /// <param name="services">The ServiceCollection.</param>
        /// <param name="configuration">The Configuration.</param>
        /// <returns></returns>
        public static IServiceCollection AddBackendProcessRunner(this IServiceCollection services,
                                                                 IConfiguration configuration)
        {
            services.Configure<FrontendOptions>(configuration.GetSection("FrontEndOptions"));
            services.AddHostedService<BackendProcessRunner>();
            return services;
        }
    }
}
