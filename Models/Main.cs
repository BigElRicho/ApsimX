﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using APSIM.Shared.JobRunning;
using APSIM.Shared.Utilities;
using CommandLine;
using Models.Core;
using Models.Core.ApsimFile;
using Models.Core.ConfigFile;
using Models.Core.Run;
using Models.Storage;

namespace Models
{

    /// <summary>Class to hold a static main entry point.</summary>
    public class Program
    {
        private static object lockObject = new object();
        private static int exitCode = 0;
        private static List<Exception> exceptionsWrittenToConsole = new List<Exception>();

        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <param name="args"> Command line arguments</param>
        /// <returns> Program exit code (0 for success)</returns>
        public static int Main(string[] args)
        {
            bool isApplyOptionPresent = false;
            // Resets exitCode to help with unit testing.
            exitCode = 0;
            // Required to allow the --apply switch functionality of not including
            // an apsimx file path on the command line.
            if (args.Length > 0 && args[0].Equals("--apply"))
            {
                isApplyOptionPresent = true;
                string[] empty = { " " };
                empty = empty.Concat(args).ToArray();
                args = empty;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            ReplaceObsoleteArguments(ref args);

            Parser parser = new Parser(config =>
            {
                config.AutoHelp = true;
                config.HelpWriter = Console.Out;
            });

            // Holds the switch(es) used in the command line call.
            ParserResult<Options> result = parser.ParseArguments<Options>(args);

            if (isApplyOptionPresent)
            {
                if (args.Length > 2)
                {
                    result.Value.Apply = args[2];
                }
                else
                {
                    string argsListString = string.Join(" ", args.ToList());
                    throw new Exception($"No config file was given with the --apply switch. Arguments given: {argsListString}");
                }
            }

            result.WithParsed(Run).WithNotParsed(HandleParseError);
            return exitCode;
        }

        /// <summary>
        /// Handles parser errors to ensure that a non-zero exit code
        /// is returned when parse errors are encountered.
        /// </summary>
        /// <param name="errors">Parse errors.</param>
        private static void HandleParseError(IEnumerable<Error> errors)
        {
            // To help with Jenkins only errors.
            foreach (var error in errors)
            {
                //We need to exclude these as the nuget package has a bug that causes them to appear even if there is no error.
                if (error as VersionRequestedError == null && error as HelpRequestedError == null) 
                {
                    Console.WriteLine("Console error output: " + error.ToString());
                    Trace.WriteLine("Trace error output: " + error.ToString());
                }
            }

            if (!(errors.IsHelp() || errors.IsVersion()))
                exitCode = 1;
        }

        /// <summary>
        /// Run Models with the given set of options.
        /// </summary>
        /// <param name="options"></param>
        public static void Run(Options options)
        {
            try
            {
                string[] files = options.Files.SelectMany(f => DirectoryUtilities.FindFiles(f, options.Recursive)).ToArray();
                // Required to warn user that file was not found even though --apply switch used.
                if (options.Files.ToList().Count > 0
                    && options.Files.ToList().First() != " "
                    && !string.IsNullOrEmpty(options.Apply)
                    && files.Length < 1)
                {
                    throw new ArgumentException($"One or more files included before the --apply switch where not found. The files are: {options.Files.ToList()}.");
                }
                if (files == null || files.Length < 1 && string.IsNullOrEmpty(options.Apply))
                    throw new ArgumentException($"No files were specified");
                if (options.NumProcessors == 0)
                    throw new ArgumentException($"Number of processors cannot be 0");
                if (options.Upgrade)
                {
                    foreach (string file in files)
                    {
                        UpgradeFile(file);
                        if (options.Verbose)
                            Console.WriteLine("Successfully upgraded " + file);
                    }
                }
                else if (options.ListSimulationNames)
                    foreach (string file in files)
                        ListSimulationNames(file, options.SimulationNameRegex);
                else if (options.ListReferencedFileNames)
                {
                    foreach (string file in files)
                        ListReferencedFileNames(file);
                }
                else if (options.ListReferencedFileNamesUnmodified)
                {
                    foreach (string file in files)
                        ListReferencedFileNames(file, false);
                }
                else if (options.MergeDBFiles)
                {
                    string[] dbFiles = files.Select(f => Path.ChangeExtension(f, ".db")).ToArray();
                    string outFile = Path.Combine(Path.GetDirectoryName(dbFiles[0]), "merged.db");
                    DBMerger.MergeFiles(dbFiles, outFile);
                }
                // --apply switch functionality.
                else if (!string.IsNullOrWhiteSpace(options.Apply))
                {
                    string configFileAbsolutePath = Path.GetFullPath(options.Apply);
                    string configFileDirectory = Directory.GetParent(configFileAbsolutePath).FullName;
                    List<string> commandsList = ParseConfigFileCommands(options);
                    DoCommands(options, files, configFileDirectory, commandsList);
                    CleanUpTempFiles(configFileDirectory);
                }
                else
                {
                    Runner runner = null;
                    if (string.IsNullOrEmpty(options.EditFilePath))
                    {
                        if (!string.IsNullOrWhiteSpace(options.Playlist))
                        {
                            runner = CreateRunnerForPlaylistOption(options, files);
                        }
                        else
                        {
                            runner = new Runner(files,
                                            options.RunTests,
                                            runType: options.RunType,
                                            numberOfProcessors: options.NumProcessors,
                                            simulationNamePatternMatch: options.SimulationNameRegex);
                        }
                        RunSimulations(runner, options);
                    }
                    else if (!string.IsNullOrEmpty(options.EditFilePath))
                    {
                        runner = new Runner(files.Select(f => ApplyConfigToApsimFile(f, options.EditFilePath)),
                                            true,
                                            true,
                                            options.RunTests,
                                            runType: options.RunType,
                                            numberOfProcessors: options.NumProcessors,
                                            simulationNamePatternMatch: options.SimulationNameRegex);

                        RunSimulations(runner, options);
                    }

                    // If errors occurred, write them to the console.
                    if (exitCode != 0)
                        Console.WriteLine("ERRORS FOUND!!");
                    if (options.Verbose)
                        Console.WriteLine("Elapsed time was " + runner.ElapsedTime.TotalSeconds.ToString("F1") + " seconds");
                }
            }
            catch (Exception err)
            {
                Console.WriteLine(err.ToString());
                exitCode = 1;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static List<string> ParseConfigFileCommands(Options options)
        {
            List<string> commands = ConfigFile.GetConfigFileCommands(options.Apply);
            List<string> commandsWithoutNulls = ConfigFile.GetListWithoutNullCommands(commands);
            List<string> commandsWithSpacesRemoved = ConfigFile.RemoveConfigFileWhitespace(commandsWithoutNulls.ToList());
            return ConfigFile.EncodeSpacesInCommandList(commandsWithSpacesRemoved);
        }



        /// <summary>
        /// Takes an array of commands and runs them in sequence.
        /// </summary>
        /// <param name="options">The flags/switches given when calling models.exe.</param>
        /// <param name="files">the file name strings provided.</param>
        /// <param name="configFileDirectory">The parent directory where the config file is located.</param>
        /// <param name="commandsList">Contains an string array element for each line in the configFile. </param>
        /// <exception cref="Exception"></exception>
        private static void DoCommands(Options options, string[] files, string configFileDirectory, List<string> commandsList)
        {
            if (files.Length > 0)
            {
                string originalFilePath = null;
                string savePath = null;
                string loadPath = null;
                Simulations tempSim = null;
                bool isSimToBeRun = false;
                string lastSaveFilePath = null;

                foreach (string file in files)
                {
                    foreach (string command in commandsList)
                    {
                        string[] splitCommand = command.Split(' ', '=');

                        ConfigureCommandRun(splitCommand, configFileDirectory, ref originalFilePath, ref savePath, ref loadPath, ref isSimToBeRun, ref tempSim);

                        // Set and create if not already a temporary sim to make changes to.
                        // You should never make changes to the original unless specified in save command.
                        if (savePath == null && loadPath == null)
                        {
                            if (tempSim == null)
                                tempSim = CreateTempApsimxFile(configFileDirectory, file, splitCommand);
                        }

                        Simulations sim = null;

                        if (!string.IsNullOrWhiteSpace(loadPath))
                        {
                            sim = FileFormat.ReadFromString<Simulations>(loadPath, e => throw e, false).NewModel as Simulations;
                            sim = ConfigFile.RunConfigCommands(sim, command, configFileDirectory) as Simulations;
                        }
                        else
                            sim = ConfigFile.RunConfigCommands(tempSim, command, configFileDirectory) as Simulations;

                        // Write to a specific file, if savePath is set use this instead of the file passed in through arguments.
                        // Otherwise 
                        if (!string.IsNullOrWhiteSpace(savePath))
                        {
                            sim.Write(savePath);
                            savePath = "";
                        }
                        else sim.Write(tempSim.FileName);

                        if (isSimToBeRun)
                        {
                            RunModifiedApsimxFile(options, file, tempSim, sim, originalFilePath, lastSaveFilePath);
                            isSimToBeRun = false;
                        }
                    }
                }
            }
            // If no apsimx file path included proceeding --apply switch...              
            else if (files.Length < 1)
            {
                string originalFilePath = null;
                string savePath = null;
                string loadPath = null;
                bool isSimToBeRun = false;
                Simulations tempSim = null;
                string lastSaveFilePath = null;

                foreach (string command in commandsList)
                {
                    string[] splitCommand = command.Split(' ', '=');
                    ConfigureCommandRun(splitCommand, configFileDirectory, ref originalFilePath, ref savePath, ref loadPath, ref isSimToBeRun, ref tempSim);

                    // Throw if the first command is not a save or load command.
                    if (String.IsNullOrEmpty(loadPath) && String.IsNullOrEmpty(savePath))
                    {
                        throw new Exception("First command in a config file can only be either a save or load command if no apsimx file is included.");
                    }

                    // As long as a file can be loaded any other command can be run.
                    if (!String.IsNullOrEmpty(loadPath))
                    {
                        // Temporary sim for holding changes.
                        Simulations sim = null;

                        if (tempSim != null)
                            sim = ConfigFile.RunConfigCommands(tempSim, command, configFileDirectory) as Simulations;

                        if (!String.IsNullOrEmpty(loadPath) && !String.IsNullOrEmpty(savePath))
                        {
                            sim.Write(sim.FileName, savePath);
                            lastSaveFilePath = savePath;
                            savePath = "";
                        }

                        if (isSimToBeRun)
                        {
                            RunModifiedApsimxFile(options, loadPath, tempSim, sim, originalFilePath, lastSaveFilePath);
                            isSimToBeRun = false;
                        }
                    }
                    else if (!string.IsNullOrEmpty(savePath))
                    {
                        // Create a new simulation as an existing apsimx file was not included.
                        Simulations sim = CreateMinimalSimulation();
                        sim.Write(sim.FileName, savePath);
                        savePath = "";
                    }
                    else throw new Exception("--apply switch used without apsimx file and no load command. Include a load command in the config file.");
                }

            }
        }

        /// <summary>
        /// Writes a temporary file into an existing file and runs the file.
        /// </summary>
        /// <param name="options">command line flags/switches.</param>
        /// <param name="filePath">The apsimx file to be overwritten.</param>
        /// <param name="tempSim">The apsimx file to overwrite original.</param>
        /// <param name="sim"></param>
        /// <param name="originalFilePath"></param>
        /// <param name="lastSaveFilePath"></param>
        private static void RunModifiedApsimxFile(Options options, string filePath, Simulations tempSim, Simulations sim, string originalFilePath, string lastSaveFilePath)
        {
            Runner runner = null;
            string extension = Path.GetExtension(filePath);
            if (extension != ".temp" || tempSim.FileName.Equals(originalFilePath))
            {
                File.Copy(tempSim.FileName, filePath, true);
                sim = FileFormat.ReadFromFile<Simulations>(tempSim.FileName, e => throw e, false).NewModel as Simulations;
            }
            else
            {
                if (!File.Exists(filePath))
                    sim.Write(filePath);
                File.Copy(filePath, lastSaveFilePath, true);

                sim = FileFormat.ReadFromFile<Simulations>(lastSaveFilePath, e => throw e, false).NewModel as Simulations;
            }
            if (!string.IsNullOrEmpty(options.Playlist))
            {
                runner = CreateRunnerForPlaylistOption(options, new string[] { sim.FileName });
            }
            else
            {
                runner = new Runner(sim,
                                    true,
                                    true,
                                    options.RunTests,
                                    runType: options.RunType,
                                    numberOfProcessors: options.NumProcessors,
                                    simulationNamePatternMatch: options.SimulationNameRegex);
            }
            RunSimulations(runner, options);
            //// An assumption is made here that once a simulation is run a temp file is no longer needed.
            //// Release database files and clean up. 
            runner.DisposeStorage();
        }

        /// <summary>
        /// Configures settings for running a single command.
        /// </summary>
        /// <param name="splitCommand">An array of the command splits.</param>
        /// <param name="configFileDirectory">A string path to directory housing the config file.</param>
        /// <param name="originalFilePath"></param>
        /// <param name="savePath">A reference to the savePath variable.</param>
        /// <param name="loadPath">A reference to the loadPath variable.</param>
        /// <param name="isSimToBeRun">A reference to the isSimToBeRun variable.</param>
        /// <param name="tempSim">A reference to tempSim simulation.</param>
        private static void ConfigureCommandRun(string[] splitCommand, string configFileDirectory, ref string originalFilePath, ref string savePath, ref string loadPath, ref bool isSimToBeRun, ref Simulations tempSim)
        {
            if (splitCommand[0] == "save")
                savePath = CreateFullSavePath(configFileDirectory, splitCommand);
            else if (splitCommand[0] == "load")
            {
                tempSim = CreateTempApsimxFile(configFileDirectory, splitCommand[0], splitCommand);
                loadPath = Path.Combine(configFileDirectory, tempSim.FileName);
                originalFilePath = Path.Combine(configFileDirectory, splitCommand[1]);
            }
            else if (splitCommand[0] == "run")
                isSimToBeRun = true;
        }

        /// <summary>
        /// Creates a temporary apsimx file for holding changes.
        /// </summary>
        /// <param name="configFileDirectory"></param>
        /// <param name="file"></param>
        /// <param name="splitCommand"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private static Simulations CreateTempApsimxFile(string configFileDirectory, string file, string[] splitCommand)
        {
            Simulations tempSim = null;
            string fullLoadPath = CreateFullLoadPath(configFileDirectory, file, splitCommand);
            List<string> filePathSplits = fullLoadPath.Split('.', '/', '\\').ToList();
            if (filePathSplits.Count >= 2)
            {
                tempSim = FileFormat.ReadFromFile<Simulations>(fullLoadPath, e => throw e, false).NewModel as Simulations;
                tempSim.FileName = Path.GetFileNameWithoutExtension(fullLoadPath) + "temp.apsimx.temp";
                File.WriteAllText(tempSim.FileName, FileFormat.WriteToString(tempSim));
            }
            else
                throw new Exception($"There was an error creating a new temporary file. The path causing issues was: {file}");
            return tempSim;
        }


        /// <summary>
        /// Creates a save path string.
        /// </summary>
        /// <param name="configFileDirectory"></param>
        /// <param name="firstSplitCommand"></param>
        /// <returns></returns>
        private static string CreateFullSavePath(string configFileDirectory, string[] firstSplitCommand)
        {
            return configFileDirectory + Path.DirectorySeparatorChar + firstSplitCommand[1];
        }

        /// <summary>
        /// Creates a full path to an apsimx file. 
        /// </summary>
        /// <param name="configFileDirectory">The directory where the configFile is located.</param>
        /// <param name="loadPath">The name of the file or a full path of apsimx file.</param>
        /// <param name="splitCommand">An array of substrings of a command.</param>
        /// <returns></returns>
        private static string CreateFullLoadPath(string configFileDirectory, string loadPath, string[] splitCommand)
        {
            string fileName = null;
            if (splitCommand[0] == "load")
                fileName = splitCommand[1];
            else
                fileName = Path.GetFileName(loadPath);
            return configFileDirectory + Path.DirectorySeparatorChar + fileName;
        }

        /// <summary>
        /// Creates a runner specifically for the playlist option/switch.
        /// </summary>
        /// <param name="options">Switches used on the command line</param>
        /// <param name="files">The files included when models is called. Can be null.</param>
        /// <returns>Runner that uses the Playlist model as the parameter 'relativeTo'.</returns>
        /// <exception cref="ArgumentException"></exception>
        private static Runner CreateRunnerForPlaylistOption(Options options, string[] files)
        {
            Runner runner;
            if (files != null)
            {
                if (files.Length > 1)
                    throw new ArgumentException("The playlist switch cannot be run with more than one file.");
            }
            Simulations file = FileFormat.ReadFromFile<Simulations>(files.First(), e => throw e, false).NewModel as Simulations;
            Playlist playlistModel = file.FindChild<Playlist>();
            if (playlistModel.Enabled == false)
                throw new ArgumentException("The specified playlist is disabled and cannot be run.");
            IEnumerable<Playlist> playlists = new List<Playlist> { file.FindChild<Playlist>(options.Playlist) };
            if (playlists.Any() && playlists.First() == null)
                throw new ArgumentException($"A playlist named {options.Playlist} could not be found in the {file.FileName}.");
            runner = new Runner(playlists,
                                runSimulations: true,
                                runPostSimulationTools: true,
                                options.RunTests,
                                runType: options.RunType,
                                numberOfProcessors: options.NumProcessors,
                                simulationNamePatternMatch: options.SimulationNameRegex);
            return runner;
        }

        private static IModel ApplyConfigToApsimFile(string fileName, string configFilePath)
        {
            Simulations file = FileFormat.ReadFromFile<Simulations>(fileName, e => throw e, false).NewModel as Simulations;
            var overrides = Overrides.ParseStrings(File.ReadAllLines(configFilePath));
            Overrides.Apply(file, overrides);
            return file;
        }

        private static void ReplaceObsoleteArguments(ref string[] args)
        {
            if (args == null)
                return;
            List<KeyValuePair<string, string>> replacements = new List<KeyValuePair<string, string>>()
            {
                new KeyValuePair<string, string>("/Recurse", "--recursive"),
                new KeyValuePair<string, string>("/SingleThreaded", "--single-threaded"),
                new KeyValuePair<string, string>("/RunTests", "--run-tests"),
                new KeyValuePair<string, string>("/Csv", "--csv"),
                new KeyValuePair<string, string>("/Version", "--version"),
                new KeyValuePair<string, string>("/Verbose", "--verbose"),
                new KeyValuePair<string, string>("/Upgrade", "--upgrade"),
                new KeyValuePair<string, string>("/MultiProcess", "--multi-process"),
                new KeyValuePair<string, string>("/NumberOfProcessors:", "--cpu-count="),
                new KeyValuePair<string, string>("/SimulationNameRegexPattern:", "--simulation-names="),
                new KeyValuePair<string, string>("/MergeDBFiles", "--merge-db-files"),
                new KeyValuePair<string, string>("/Edit", "--edit"),
                new KeyValuePair<string, string>("/ListSimulations", "--list-simulations"),
                new KeyValuePair<string, string>("/?", "--help"),
            };
            for (int i = 0; i < args.Length; i++)
                foreach (KeyValuePair<string, string> replacement in replacements)
                    args[i] = args[i].Replace(replacement.Key, replacement.Value);
        }

        /// <summary>
        /// Write the APSIM version to the console.
        /// </summary>
        private static void WriteVersion()
        {
            Console.WriteLine(Simulations.GetApsimVersion());
        }

        /// <summary>
        /// Upgrade a file to the latest APSIM version.
        /// </summary>
        /// <param name="file">The name of the file to upgrade.</param>
        private static void UpgradeFile(string file)
        {
            string contents = File.ReadAllText(file);
            ConverterReturnType converter = Converter.DoConvert(contents, fileName: file);
            if (converter.DidConvert)
                File.WriteAllText(file, converter.Root.ToString());
        }

        private static void ListSimulationNames(string fileName, string simulationNameRegex)
        {
            Simulations file = FileFormat.ReadFromFile<Simulations>(fileName, e => throw e, false).NewModel as Simulations;

            SimulationGroup jobFinder = new SimulationGroup(file, simulationNamePatternMatch: simulationNameRegex);
            jobFinder.FindAllSimulationNames(file, null).ForEach(name => Console.WriteLine(name));

        }

        private static void ListReferencedFileNames(string fileName, bool isAbsolute = true)
        {
            Simulations file = FileFormat.ReadFromFile<Simulations>(fileName, e => throw e, false).NewModel as Simulations;

            foreach (var referencedFileName in file.FindAllReferencedFiles(isAbsolute))
                Console.WriteLine(referencedFileName);
        }

        /// <summary>Job has completed</summary>
        private static void OnJobCompleted(object sender, JobCompleteArguments e)
        {
            if (e.ExceptionThrowByJob != null)
            {
                lock (lockObject)
                {
                    exceptionsWrittenToConsole.Add(e.ExceptionThrowByJob);
                    Console.WriteLine("----------------------------------------------");
                    Console.WriteLine(e.ExceptionThrowByJob.ToString());
                    exitCode = 1;
                }
            }
        }

        /// <summary>All jobs for a file have completed</summary>
        private static void OnSimulationGroupCompleted(object sender, EventArgs e)
        {
            if (sender is SimulationGroup group)
            {
                string fileName = Path.ChangeExtension(group.FileName, ".db");
                var storage = new Storage.DataStore(fileName);
                Report.WriteAllTables(storage, fileName);
                Console.WriteLine("Successfully created csv file " + Path.ChangeExtension(fileName, ".csv"));
            }
        }

        /// <summary>All jobs have completed</summary>
        private static void OnAllJobsCompleted(object sender, Runner.AllJobsCompletedArgs e)
        {
            if (sender is Runner runner)
                (sender as Runner).DisposeStorage();

            if (e.AllExceptionsThrown == null)
                return;

            foreach (Exception error in e.AllExceptionsThrown)
            {
                if (!exceptionsWrittenToConsole.Contains(error))
                {
                    Console.WriteLine("----------------------------------------------");
                    Console.WriteLine(error.ToString());
                    exitCode = 1;
                }
            }
        }

        /// <summary>
        /// Write a complete message to the console.
        /// </summary>
        /// <param name="sender">Sender object.</param>
        /// <param name="e">The event arguments of the completed job.</param>
        private static void WriteCompleteMessage(object sender, JobCompleteArguments e)
        {
            if (e.Job == null)
                return;

            var message = new StringBuilder(e.Job.Name);
            if (e.Job is SimulationDescription sim && !string.IsNullOrEmpty(sim.SimulationToRun?.FileName))
                message.Append($" ({sim.SimulationToRun.FileName})");
            string duration = e.ElapsedTime.TotalSeconds.ToString("F1");
            message.Append($" has finished. Elapsed time was {duration} seconds.");
            Console.WriteLine(message);
        }

        /// <summary>
        /// Runs a specified runner.
        /// </summary>
        /// <param cref="Runner" name="runner">The runner to be ran.</param>
        /// <param cref="Options" name="options">The command line switches/flags.</param>
        private static void RunSimulations(Runner runner, Options options)
        {
            runner.SimulationCompleted += OnJobCompleted;
            if (options.Verbose)
                runner.SimulationCompleted += WriteCompleteMessage;
            if (options.ExportToCsv)
                runner.SimulationGroupCompleted += OnSimulationGroupCompleted;
            runner.AllSimulationsCompleted += OnAllJobsCompleted;
            // Run simulations.
            runner.Run();
        }

        /// <summary>
        /// Creates an apsimx file that has a 'Simulations' model with a child 'DataStore'. 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private static Simulations CreateMinimalSimulation()
        {
            try
            {
                Simulations sims = new Simulations()
                {
                    Children = new List<IModel>()
                    {
                        new DataStore()
                    }
                };

                sims.Write("NewSimulation");
                return sims;
            }
            catch (Exception ex)
            {
                throw new Exception("An error occured when trying to create a new minimal simulation." + ex.Message);
            }

        }


        private static void CleanUpTempFiles(string configFileDirectoryPath)
        {
            // IMPORTANT: The order of these is important. Do not modify.
            string[] tempFileList = {
            "*temp.apsimx.temp.bak",
            @"*.temp",
            "*temp.apsimx.db-shm",
            "*temp.apsimx.db-wal",
            "*temp.apsimx.db",
            };

            bool isFileInUse = false;
            List<string> matchingTempFiles = new();
            foreach (string file in tempFileList)
                foreach (string match in Directory.GetFiles(configFileDirectoryPath, file))
                    if (match != null)
                        matchingTempFiles.Add(match);
            if (matchingTempFiles.Count > 0)
            {
                foreach (string matchingFile in matchingTempFiles)
                {
                    while (isFileInUse == true)
                        isFileInUse = IsFileLocked(matchingFile);
                    File.Delete(matchingFile);
                    isFileInUse = true;
                }
            }
        }

        /// <summary>
        /// Closes file if in use and returns true otherwise returns false.
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>bool</returns>
        private static bool IsFileLocked(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                using (FileStream stream = fileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                }
            }
            catch (IOException)
            {
                //the file is unavailable because it is:
                //still being written to
                //or being processed by another thread
                //or does not exist (has already been processed)
                return true;
            }

            //file is not locked
            return false;
        }
    }
}