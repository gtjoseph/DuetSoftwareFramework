﻿using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer
{
    /// <summary>
    /// Main program class
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Version of this application
        /// </summary>
        public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Global cancellation source that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationTokenSource CancelSource = new CancellationTokenSource();

        /// <summary>
        /// Global cancellation token that is triggered when the program is supposed to terminate
        /// </summary>
        public static readonly CancellationToken CancellationToken = CancelSource.Token;

        /// <summary>
        /// Cancellation token to be called when the program has been terminated
        /// </summary>
        private static readonly CancellationTokenSource _programTerminated = new CancellationTokenSource();

        /// <summary>
        /// Entry point of the program
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        private static async Task Main(string[] args)
        {
            // Performing an update implies a reduced log level
            if (args.Contains("-u") && !args.Contains("--update"))
            {
                List<string> newArgs = new List<string>() { "--log-level", "error" };
                newArgs.AddRange(args);
                args = newArgs.ToArray();
            }
            else
            {
                Console.WriteLine($"Duet Control Server v{Version}");
                Console.WriteLine("Written by Christian Hammacher for Duet3D");
                Console.WriteLine("Licensed under the terms of the GNU Public License Version 3");
                Console.WriteLine();
            }

            // Initialize settings
            try
            {
                if (!Settings.Init(args))
                {
                    return;
                }
                _logger.Info("Settings loaded");
            }
            catch (Exception e)
            {
                _logger.Fatal(e, "Failed to load settings");
                return;
            }

            // Check if another instance is already running
            if (await CheckForAnotherInstance())
            {
                return;
            }

            // Initialize everything
            try
            {
                Model.Provider.Init();
                Model.Observer.Init();
                await Utility.FilamentManager.Init();
                _logger.Info("Environment initialized");
            }
            catch (Exception e)
            {
                _logger.Fatal(e, "Failed to initialize environment");
                return;
            }

            // Set up SPI subsystem and connect to RRF controller
            if (Settings.NoSpi)
            {
                _logger.Warn("SPI connection to Duet is disabled");
            }
            else
            {
                try
                {
                    SPI.Interface.Init();
                    SPI.DataTransfer.Init();
                    _logger.Info("Connection to Duet established");
                }
                catch (Exception e)
                {
                    _logger.Fatal("Could not connect to Duet ({0})", e.Message);
                    _logger.Debug(e);
                    return;
                }
            }

            // Start up IPC server
            try
            {
                IPC.Server.Init();
                _logger.Info("IPC socket created at {0}", Settings.FullSocketPath);
            }
            catch (Exception e)
            {
                _logger.Fatal(e, "Failed to initialize IPC socket");
                return;
            }

            // Start main tasks in the background
            Dictionary<Task, string> mainTasks = new Dictionary<Task, string>
            {
                { Task.Factory.StartNew(Model.Updater.Run, TaskCreationOptions.LongRunning).Unwrap(), "Update" },
                { Task.Factory.StartNew(SPI.Interface.Run, TaskCreationOptions.LongRunning).Unwrap(), "SPI" },
                { Task.Factory.StartNew(IPC.Server.Run, TaskCreationOptions.LongRunning).Unwrap(), "IPC" },
                { Task.Factory.StartNew(FileExecution.Job.Run, TaskCreationOptions.LongRunning).Unwrap(), "Job" },
                { Task.Factory.StartNew(Model.PeriodicUpdater.Run, TaskCreationOptions.LongRunning).Unwrap(), "Periodic updater" }
            };

            // Deal with program termination requests (SIGTERM and Ctrl+C)
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGTERM, shutting down...");
                    CancelSource.Cancel();
                }
            };
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!CancelSource.IsCancellationRequested)
                {
                    _logger.Warn("Received SIGINT, shutting down...");
                    e.Cancel = true;
                    CancelSource.Cancel();
                }
            };

            // Load plugin manifests
            foreach (string file in Directory.GetFiles(Settings.PluginDirectory))
            {
                if (file.EndsWith(".json"))
                {
                    try
                    {
                        using FileStream manifestStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        Plugin plugin = await JsonSerializer.DeserializeAsync<Plugin>(manifestStream, JsonHelper.DefaultJsonOptions);
                        plugin.Pid = -1;
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            Model.Provider.Get.Plugins.Add(plugin);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to load plugin manifest {0}", Path.GetFileName(file));
                    }
                }
            }

            // Start plugins that were started when DCS quit last time
            if (File.Exists(Settings.PluginsFilename))
            {
                using FileStream pluginsFile = new FileStream(Settings.PluginsFilename, FileMode.Open, FileAccess.Read, FileShare.Read);
                using StreamReader reader = new StreamReader(pluginsFile);

                string plugin;
                while ((plugin = await reader.ReadLineAsync()) != null)
                {
                    _logger.Info("Starting plugin {0}", plugin);
                    try
                    {
                        StartPlugin startPlugin = new StartPlugin
                        {
                            Plugin = plugin
                        };
                        await startPlugin.Execute();
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to start plugin {0}", plugin);
                    }
                }
            }

            // Wait for the first task to terminate.
            // In case this is an unusual shutdown, log this event and shut down the application
            Task terminatedTask = await Task.WhenAny(mainTasks.Keys);
            if (!CancelSource.IsCancellationRequested)
            {
                _logger.Fatal("Abnormal program termination");
                if (terminatedTask.IsFaulted)
                {
                    string taskName = mainTasks[terminatedTask];
                    _logger.Fatal(terminatedTask.Exception, "{0} task faulted", taskName);
                }
                CancelSource.Cancel();
            }

            // Wait for the other tasks to finish
            do
            {
                string taskName = mainTasks[terminatedTask];
                if (terminatedTask.IsFaulted && !terminatedTask.IsCanceled)
                {
                    foreach (Exception ie in terminatedTask.Exception.InnerExceptions)
                    {
                        _logger.Fatal(ie, "{0} task faulted", taskName);
                    }
                }
                else
                {
                    _logger.Debug("{0} task terminated", taskName);
                }

                mainTasks.Remove(terminatedTask);
                if (mainTasks.Count > 0)
                {
                    terminatedTask = await Task.WhenAny(mainTasks.Keys);
                }
            }
            while (mainTasks.Count > 0);

            // Stop the plugins again
            List<string> startedPlugins = new List<string>();
            foreach (Plugin plugin in Model.Provider.Get.Plugins)
            {
                if (plugin.Pid >= 0)
                {
                    startedPlugins.Add(plugin.Name);
                    if (plugin.Pid > 0)
                    {
                        _logger.Debug("Stopping plugin {0}", plugin.Name);
                        try
                        {
                            StopPlugin stopPlugin = new StopPlugin()
                            {
                                Plugin = plugin.Name
                            };
                            await stopPlugin.Execute();
                        }
                        catch (Exception e)
                        {
                            _logger.Error(e, "Failed to stop plugin {0}", plugin.Name);
                        }
                    }
                }
            }

            // Keep track of the started plugins
            _logger.Debug("Saving list of previously started plugins");
            using (FileStream pluginsFile = new FileStream(Settings.PluginsFilename, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using StreamWriter writer = new StreamWriter(pluginsFile);
                foreach (string plugin in startedPlugins)
                {
                    await writer.WriteLineAsync(plugin);
                }
            }

            // End
            _logger.Info("Application has shut down");
            NLog.LogManager.Shutdown();
            _programTerminated.Cancel();
        }

        /// <summary>
        /// Check if another instance is already running
        /// </summary>
        /// <returns>True if another instance is running</returns>
        private static async Task<bool> CheckForAnotherInstance()
        {
            using DuetAPIClient.CommandConnection connection = new DuetAPIClient.CommandConnection();
            try
            {
                await connection.Connect(Settings.FullSocketPath);
            }
            catch (SocketException)
            {
                return false;
            }

            if (Settings.UpdateOnly)
            {
                Console.Write("Sending update request to DCS... ");
                try
                {
                    await connection.PerformCode(new Code
                    {
                        Type = DuetAPI.Commands.CodeType.MCode,
                        MajorNumber = 997,
                        Flags = DuetAPI.Commands.CodeFlags.IsPrioritized
                    });
                    Console.WriteLine("Done!");
                }
                catch
                {
                    Console.WriteLine("Error: Failed to send update request");
                    throw;
                }
                finally
                {
                    CancelSource.Cancel();
                }
            }
            else
            {
                _logger.Fatal("Another instance is already running. Stopping.");
            }
            return true;
        }

        /// <summary>
        /// Terminate this program and kill it forcefully if required
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Terminate()
        {
            try
            {
                // Try to shut down this program normally
                CancelSource.Cancel();
                await Task.Delay(4000, _programTerminated.Token);

                // If that fails, kill it
                Environment.Exit(1);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }
}
