﻿using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.FileExecution;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Static class that processes M-codes in the control server
    /// </summary>
    public static class MCodes
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Process an M-code that should be interpreted by the control server
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Result of the code if the code completed, else null</returns>
        public static async Task<CodeResult> Process(Commands.Code code)
        {
            switch (code.MajorNumber)
            {
                // Cancel print
                case 0:
                case 1:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        using (await Print.LockAsync())
                        {
                            // Invalidate the print file and make sure no more codes are read from it
                            Print.Cancel();
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // List SD card
                case 20:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        CodeParameter pParam = code.Parameter('P');
                        string directory = await FilePath.ToPhysicalAsync(pParam ?? "", FileDirectory.GCodes);
                        int startAt = Math.Max(code.Parameter('R') ?? 0, 0);

                        // Check if JSON file lists were requested
                        CodeParameter sParam = code.Parameter('S', 0);
                        if (sParam == 2)
                        {
                            string json = FileLists.GetFiles(pParam, directory, startAt);
                            return new CodeResult(MessageType.Success, json);
                        }
                        if (sParam == 3)
                        {
                            string json = FileLists.GetFileList(pParam, directory, startAt);
                            return new CodeResult(MessageType.Success, json);
                        }

                        // Print standard G-code response
                        Compatibility compatibility;
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            compatibility = Model.Provider.Get.Channels[code.Channel].Compatibility;
                        }

                        StringBuilder result = new StringBuilder();
                        if (compatibility == Compatibility.Me || compatibility == Compatibility.RepRapFirmware)
                        {
                            result.AppendLine("GCode files:");
                        }
                        else if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                        {
                            result.AppendLine("Begin file list:");
                        }

                        int numItems = 0;
                        bool itemFound = false;
                        foreach (string file in Directory.EnumerateFileSystemEntries(directory))
                        {
                            if (numItems++ >= startAt)
                            {
                                string filename = Path.GetFileName(file);
                                if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                                {
                                    result.AppendLine(filename);
                                }
                                else
                                {
                                    if (itemFound)
                                    {
                                        result.Append(',');
                                    }
                                    result.Append($"\"{filename}\"");
                                }
                                itemFound = true;
                            }
                        }

                        if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                        {
                            if (!itemFound)
                            {
                                result.AppendLine("NONE");
                            }
                            result.Append("End file list");
                        }

                        return new CodeResult(MessageType.Success, result.ToString());
                    }
                    throw new OperationCanceledException();

                // Select a file to print
                case 23:
                case 32:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = code.GetUnprecedentedString();
                        if (string.IsNullOrWhiteSpace(file))
                        {
                            return new CodeResult(MessageType.Error, "Filename expected");
                        }

                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                        if (!File.Exists(physicalFile))
                        {
                            return new CodeResult(MessageType.Error, $"Could not find file {file}");
                        }

                        using (await Print.LockAsync())
                        {
                            if (code.Channel != CodeChannel.File && Print.IsPrinting)
                            {
                                return new CodeResult(MessageType.Error, "Cannot set file to print, because a file is already being printed");
                            }
                            await Print.SelectFile(physicalFile);
                        }

                        if (await code.EmulatingMarlin())
                        {
                            return new CodeResult(MessageType.Success, "File opened\nFile selected");
                        }
                        return new CodeResult(MessageType.Success, $"File {file} selected for printing");
                    }
                    throw new OperationCanceledException();


                // Resume a file print
                case 24:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        using (await Print.LockAsync())
                        {
                            if (!Print.IsFileSelected)
                            {
                                return new CodeResult(MessageType.Error, "Cannot print, because no file is selected!");
                            }
                        }

                        // Let RepRapFirmware process this request so it can invoke resume.g. When M24 completes, the file is resumed
                        break;
                    }
                    throw new OperationCanceledException();

                // Pause print
                case 25:
                case 226:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        using (await Print.LockAsync())
                        {
                            if (Print.IsPrinting && !Print.IsPaused)
                            {
                                // Invalidate the code's file position because it must not be used for pausing
                                code.FilePosition = null;

                                // Stop reading any more codes from the file being printed. Everything else is handled by RRF
                                Print.Pause();
                            }
                        }

                        // Let RepRapFirmware pause the print
                        break;
                    }
                    throw new OperationCanceledException();

                // Set SD position
                case 26:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        using (await Print.LockAsync())
                        {
                            if (!Print.IsPrinting)
                            {
                                return new CodeResult(MessageType.Error, "Not printing a file");
                            }

                            CodeParameter sParam = code.Parameter('S');
                            if (sParam != null)
                            {
                                Print.FilePosition = sParam;
                            }
                        }

                        // P is not supported yet

                        return new CodeResult();
                    }
                    throw new OperationCanceledException();

                // Report SD print status
                case 27:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        using (await Print.LockAsync())
                        {
                            if (Print.IsPrinting)
                            {
                                return new CodeResult(MessageType.Success, $"SD printing byte {Print.FilePosition}/{Print.FileLength}");
                            }
                            return new CodeResult(MessageType.Success, "Not SD printing.");
                        }
                    }
                    throw new OperationCanceledException();

                // Begin write to SD card
                case 28:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        int numChannel = (int)code.Channel;
                        using (await Commands.Code.FileLocks[numChannel].LockAsync())
                        {
                            if (Commands.Code.FilesBeingWritten[numChannel] != null)
                            {
                                return new CodeResult(MessageType.Error, "Another file is already being written to");
                            }

                            string file = code.GetUnprecedentedString();
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                return new CodeResult(MessageType.Error, "Filename expected");
                            }

                            string prefix = (await code.EmulatingMarlin()) ? "ok\n" : string.Empty;
                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                            try
                            {
                                FileStream fileStream = new FileStream(physicalFile, FileMode.Create, FileAccess.Write);
                                StreamWriter writer = new StreamWriter(fileStream);
                                Commands.Code.FilesBeingWritten[numChannel] = writer;
                                return new CodeResult(MessageType.Success, prefix + $"Writing to file: {file}");
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to open file for writing");
                                return new CodeResult(MessageType.Error, prefix + $"Can't open file {file} for writing.");
                            }
                        }
                    }
                    throw new OperationCanceledException();

                // End write to SD card
                case 29:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        int numChannel = (int)code.Channel;
                        using (await Commands.Code.FileLocks[numChannel].LockAsync())
                        {
                            if (Commands.Code.FilesBeingWritten[numChannel] != null)
                            {
                                Stream stream = Commands.Code.FilesBeingWritten[numChannel].BaseStream;
                                Commands.Code.FilesBeingWritten[numChannel].Dispose();
                                Commands.Code.FilesBeingWritten[numChannel] = null;
                                stream.Dispose();

                                if (await code.EmulatingMarlin())
                                {
                                    return new CodeResult(MessageType.Success, "Done saving file.");
                                }
                                return new CodeResult();
                            }
                            break;
                        }
                    }
                    throw new OperationCanceledException();

                // Delete a file on the SD card
                case 30:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = code.GetUnprecedentedString();
                        string physicalFile = await FilePath.ToPhysicalAsync(file);

                        try
                        {
                            File.Delete(physicalFile);
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to delete file");
                            return new CodeResult(MessageType.Error, $"Failed to delete file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // For case 32, see case 23

                // Return file information
                case 36:
                    if (code.Parameters.Count > 0)
                    {
                        if (await SPI.Interface.Flush(code.Channel))
                        {
                            string file = await FilePath.ToPhysicalAsync(code.GetUnprecedentedString());
                            try
                            {
                                ParsedFileInfo info = await FileInfoParser.Parse(file);

                                string json = JsonSerializer.Serialize(info, JsonHelper.DefaultJsonOptions);
                                return new CodeResult(MessageType.Success, "{\"err\":0," + json.Substring(1));
                            }
                            catch (Exception e)
                            {
                                _logger.Debug(e, "Failed to return file information");
                                return new CodeResult(MessageType.Success, "{\"err\":1}");
                            }
                        }
                        throw new OperationCanceledException();
                    }
                    break;

                // Simulate file
                case 37:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        CodeParameter pParam = code.Parameter('P');
                        if (pParam != null)
                        {
                            string file = pParam;
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                return new CodeResult(MessageType.Error, "Filename expected");
                            }

                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.GCodes);
                            if (!File.Exists(physicalFile))
                            {
                                return new CodeResult(MessageType.Error, $"GCode file \"{file}\" not found\n");
                            }

                            using (await Print.LockAsync())
                            {
                                if (code.Channel != CodeChannel.File && Print.IsPrinting)
                                {
                                    return new CodeResult(MessageType.Error, "Cannot set file to simulate, because a file is already being printed");
                                }

                                await Print.SelectFile(physicalFile, true);
                                // Simulation is started when M37 has been processed by the firmware
                            }
                        }
                        break;
                    }
                    throw new OperationCanceledException();

                // Compute SHA1 hash of target file
                case 38:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = code.GetUnprecedentedString();
                        string physicalFile = await FilePath.ToPhysicalAsync(file);

                        try
                        {
                            using FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read);

                            byte[] hash;
                            using System.Security.Cryptography.SHA1 sha1 = System.Security.Cryptography.SHA1.Create();
                            hash = await Task.Run(() => sha1.ComputeHash(stream), Program.CancelSource.Token);

                            return new CodeResult(MessageType.Success, BitConverter.ToString(hash).Replace("-", string.Empty));
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to compute SHA1 checksum");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            return new CodeResult(MessageType.Error, $"Could not compute SHA1 checksum for file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Report SD card information
                case 39:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            int index = code.Parameter('P', 0);
                            if (code.Parameter('S', 0) == 2)
                            {
                                if (index < 0 || index >= Model.Provider.Get.Storages.Count)
                                {
                                    return new CodeResult(MessageType.Success, $"{{\"SDinfo\":{{\"slot\":{index},present:0}}}}");
                                }

                                Storage storage = Model.Provider.Get.Storages[index];
                                var output = new
                                {
                                    SDinfo = new
                                    {
                                        slot = index,
                                        present = 1,
                                        capacity = storage.Capacity,
                                        free = storage.Free,
                                        speed = storage.Speed
                                    }
                                };
                                return new CodeResult(MessageType.Success, JsonSerializer.Serialize(output, JsonHelper.DefaultJsonOptions));
                            }
                            else
                            {
                                if (index < 0 || index >= Model.Provider.Get.Storages.Count)
                                {
                                    return new CodeResult(MessageType.Error, $"Bad SD slot number: {index}");
                                }

                                Storage storage = Model.Provider.Get.Storages[index];
                                return new CodeResult(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / (1000 * 1000 * 1000):F2}Gb, free space {storage.Free / (1000 * 1000 * 1000):F2}Gb, speed {storage.Speed / (1000 * 1000):F2}MBytes/sec");
                            }
                        }
                    }
                    throw new OperationCanceledException();

                // Emergency Stop - unconditional and interpreteted immediately when read
                case 112:
                    await SPI.Interface.RequestEmergencyStop();
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.State.Status = MachineStatus.Halted;
                    }
                    return new CodeResult();

                // Immediate DSF diagnostics
                case 122:
                    if (code.Parameter('B', 0) == 0 && code.GetUnprecedentedString() == "DSF")
                    {
                        CodeResult result = new CodeResult();
                        await Diagnostics(result);
                        return result;
                    }
                    break;

                // Display message and optionally wait for response
                case 291:
                    if (code.Parameter('S') == 2 || code.Parameter('S') == 3)
                    {
                        throw new NotSupportedException();
                    }
                    break;

                // Save heightmap
                case 374:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = code.Parameter('P', FilePath.DefaultHeightmapFile);
                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.System);

                        try
                        {
                            if (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                Heightmap map = await SPI.Interface.GetHeightmap();
                                await SPI.Interface.UnlockAll(code.Channel);

                                if (map.NumX * map.NumY > 0)
                                {
                                    await map.Save(physicalFile);
                                    using (await Model.Provider.AccessReadWriteAsync())
                                    {
                                        Model.Provider.Get.Move.HeightmapFile = await FilePath.ToVirtualAsync(physicalFile);
                                    }
                                    return new CodeResult(MessageType.Success, $"Height map saved to file {file}");
                                }
                                return new CodeResult();
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to save height map");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            return new CodeResult(MessageType.Error, $"Failed to save height map to file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Load heightmap
                case 375:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string file = code.Parameter('P', FilePath.DefaultHeightmapFile);
                        string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.System);

                        try
                        {
                            Heightmap map = new Heightmap();
                            await map.Load(physicalFile);

                            if (await SPI.Interface.LockMovementAndWaitForStandstill(code.Channel))
                            {
                                await SPI.Interface.SetHeightmap(map);
                                await SPI.Interface.UnlockAll(code.Channel);
                                using (await Model.Provider.AccessReadWriteAsync())
                                {
                                    Model.Provider.Get.Move.HeightmapFile = await FilePath.ToVirtualAsync(physicalFile);
                                }
                                return new CodeResult(MessageType.Success, $"Height map loaded from file {file}");
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to load height map");
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            return new CodeResult(MessageType.Error, $"Failed to load height map from file {file}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Create Directory on SD-Card
                case 470:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string path = code.Parameter('P');
                        if (path == null)
                        {
                            return new CodeResult(MessageType.Error, "Missing directory name");
                        }
                        string physicalPath = await FilePath.ToPhysicalAsync(path);

                        try
                        {
                            Directory.CreateDirectory(physicalPath);
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to create directory");
                            return new CodeResult(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Rename File/Directory on SD-Card
                case 471:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string from = code.Parameter('S');
                        string to = code.Parameter('T');

                        try
                        {
                            string source = await FilePath.ToPhysicalAsync(from);
                            string destination = await FilePath.ToPhysicalAsync(to);

                            if (File.Exists(source))
                            {
                                if (File.Exists(destination) && code.Parameter('D', false))
                                {
                                    File.Delete(destination);
                                }
                                File.Move(source, destination);
                            }
                            else if (Directory.Exists(source))
                            {
                                if (Directory.Exists(destination) && code.Parameter('D', false))
                                {
                                    // This could be recursive but at the moment we mimic RRF's behaviour
                                    Directory.Delete(destination);
                                }
                                Directory.Move(source, destination);
                            }
                            throw new FileNotFoundException();
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Failed to rename file or directory");
                            return new CodeResult(MessageType.Error, $"Failed to rename file or directory {from} to {to}: {e.Message}");
                        }
                    }
                    throw new OperationCanceledException();

                // Store parameters
                case 500:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        await Utility.ConfigOverride.Save(code);
                        return new CodeResult();
                    }
                    throw new OperationCanceledException();

                // Print settings
                case 503:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string configFile = await FilePath.ToPhysicalAsync(FilePath.ConfigFile, FileDirectory.System);
                        if (File.Exists(configFile))
                        {
                            string content = await File.ReadAllTextAsync(configFile);
                            return new CodeResult(MessageType.Success, content);
                        }

                        string configFileFallback = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, FileDirectory.System);
                        if (File.Exists(configFileFallback))
                        {
                            string content = await File.ReadAllTextAsync(configFileFallback);
                            return new CodeResult(MessageType.Success, content);
                        }
                        return new CodeResult(MessageType.Error, "Configuration file not found");
                    }
                    throw new OperationCanceledException();

                // Set configuration file folder
                case 505:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        string directory = code.Parameter('P'), physicalDirectory = await FilePath.ToPhysicalAsync(directory, "sys");
                        if (Directory.Exists(physicalDirectory))
                        {
                            string actualDirectory = await FilePath.ToVirtualAsync(physicalDirectory);
                            using (await Model.Provider.AccessReadWriteAsync())
                            {
                                Model.Provider.Get.Directories.System = actualDirectory;
                            }
                            return new CodeResult();
                        }
                        return new CodeResult(MessageType.Error, "Directory not found");
                    }
                    throw new OperationCanceledException();

                // Set Name
                case 550:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        // Verify the P parameter
                        string pParam = code.Parameter('P');
                        if (pParam.Length > 40)
                        {
                            return new CodeResult(MessageType.Error, "Machine name is too long");
                        }

                        // Strip letters and digits from the machine name
                        string machineName = string.Empty;
                        foreach (char c in Environment.MachineName)
                        {
                            if (char.IsLetterOrDigit(c))
                            {
                                machineName += c;
                            }
                        }

                        // Strip letters and digits from the desired name
                        string desiredName = string.Empty;
                        foreach (char c in pParam)
                        {
                            if (char.IsLetterOrDigit(c))
                            {
                                desiredName += c;
                            }
                        }

                        // Make sure the subset of letters and digits is equal
                        if (!machineName.Equals(desiredName, StringComparison.CurrentCultureIgnoreCase))
                        {
                            return new CodeResult(MessageType.Error, "Machine name must consist of the same letters and digits as configured by the Linux hostname");
                        }

                        // Hostname is legit - pretend we didn't see this code so RRF can interpret it
                        break;
                    }
                    throw new OperationCanceledException();

                // Filament management
                case 701:
                case 702:
                case 703:
                    // The machine model has to be in-sync for the filament functions to work...
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        await Model.Updater.WaitForFullUpdate();
                        break;
                    }
                    throw new OperationCanceledException();

                // Set current RTC date and time
                case 905:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        bool seen = false;

                        CodeParameter pParam = code.Parameter('P');
                        if (pParam != null)
                        {
                            if (DateTime.TryParseExact(pParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                            {
                                System.Diagnostics.Process.Start("timedatectl", $"set-time {date:yyyy-MM-dd}").WaitForExit();
                                seen = true;
                            }
                            else
                            {
                                return new CodeResult(MessageType.Error, "Invalid date format");
                            }
                        }

                        CodeParameter sParam = code.Parameter('S');
                        if (sParam != null)
                        {
                            if (DateTime.TryParseExact(sParam, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
                            {
                                System.Diagnostics.Process.Start("timedatectl", $"set-time {time:HH:mm:ss}").WaitForExit();
                                seen = true;
                            }
                            else
                            {
                                return new CodeResult(MessageType.Error, "Invalid time format");
                            }
                        }

                        if (!seen)
                        {
                            return new CodeResult(MessageType.Success, $"Current date and time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        }
                    }
                    throw new OperationCanceledException();

                // Start/stop event logging to SD card
                case 929:
                    if (await SPI.Interface.Flush(code.Channel))
                    {
                        CodeParameter sParam = code.Parameter('S');
                        if (sParam == null)
                        {
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                return new CodeResult(MessageType.Success, $"Event logging is {(Model.Provider.Get.State.LogFile != null ? "enabled" : "disabled")}");
                            }
                        }

                        if (sParam > 0)
                        {
                            string file = code.Parameter('P', Utility.Logger.DefaultLogFile);
                            if (string.IsNullOrWhiteSpace(file))
                            {
                                return new CodeResult(MessageType.Error, "Missing filename in M929 command");
                            }

                            string physicalFile = await FilePath.ToPhysicalAsync(file, FileDirectory.System);
                            await Utility.Logger.Start(physicalFile);
                        }
                        else
                        {
                            await Utility.Logger.Stop();
                        }

                        return new CodeResult();
                    }
                    throw new OperationCanceledException();

                // Update the firmware
                case 997:
                    if (((int[])code.Parameter('S', new int[] { 0 })).Contains(0) && code.Parameter('B', 0) == 0)
                    {
                        if (await SPI.Interface.Flush(code.Channel))
                        {
                            string iapFile, firmwareFile;
                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (!string.IsNullOrEmpty(Model.Provider.Get.Electronics.ShortName))
                                {
                                    // There are now two different IAP binaries, check which one to use
                                    iapFile = Model.Provider.Get.Electronics.Firmware.Version.Contains("3.0beta")
                                        ? $"Duet3iap_spi_{Model.Provider.Get.Electronics.ShortName}.bin"
                                        : $"Duet3_SBCiap_{Model.Provider.Get.Electronics.ShortName}.bin";
                                    firmwareFile = $"Duet3Firmware_{Model.Provider.Get.Electronics.ShortName}.bin";
                                }
                                else
                                {
                                    // ShortName field is not present - this must be a really old firmware version
                                    iapFile = $"Duet3iap_spi.bin";
                                    firmwareFile = "Duet3Firmware.bin";
                                }
                            }

                            iapFile = await FilePath.ToPhysicalAsync(iapFile, FileDirectory.System);
                            if (!File.Exists(iapFile))
                            {
                                return new CodeResult(MessageType.Error, $"Failed to find IAP file {iapFile}");
                            }

                            firmwareFile = await FilePath.ToPhysicalAsync(firmwareFile, FileDirectory.System);
                            if (!File.Exists(firmwareFile))
                            {
                                return new CodeResult(MessageType.Error, $"Failed to find firmware file {firmwareFile}");
                            }

                            using FileStream iapStream = new FileStream(iapFile, FileMode.Open, FileAccess.Read);
                            using FileStream firmwareStream = new FileStream(firmwareFile, FileMode.Open, FileAccess.Read);
                            await SPI.Interface.UpdateFirmware(iapStream, firmwareStream);
                            return new CodeResult();
                        }
                        throw new OperationCanceledException();
                    }
                    break;

                // Request resend of line
                case 998:
                    throw new NotSupportedException();

                // Reset controller - unconditional and interpreteted immediately when read
                case 999:
                    if (code.Parameters.Count == 0)
                    {
                        await SPI.Interface.RequestReset();
                        return new CodeResult();
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// React to an executed M-code before its result is returend
        /// </summary>
        /// <param name="code">Code processed by RepRapFirmware</param>
        /// <returns>Result to output</returns>
        /// <remarks>This method shall be used only to update values that are time-critical. Others are supposed to be updated via the object model</remarks>
        public static async Task CodeExecuted(Code code)
        {
            if (!code.Result.IsSuccessful)
            {
                return;
            }

            switch (code.MajorNumber)
            {
                // Resume print
                // Select file and start SD print
                // Simulate file
                case 24:
                case 32:
                case 37:
                    using (await Print.LockAsync())
                    {
                        // Start sending file instructions to RepRapFirmware
                        Print.Resume();
                    }
                    break;

                // Absolute extrusion
                case 82:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativeExtrusion = false;
                    }
                    break;

                // Relative extrusion
                case 83:
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        Model.Provider.Get.Channels[code.Channel].RelativeExtrusion = false;
                    }
                    break;

                // Diagnostics
                case 122:
                    if (code.Parameter('B', 0) == 0 && code.GetUnprecedentedString() != "DSF")
                    {
                        await Diagnostics(code.Result);
                    }
                    break;

                // Set compatibility
                case 555:
                    // Temporary until the machine model provides a field for this
                    if (code.Parameter('P') != null)
                    {
                        Compatibility compatibility = (Compatibility)(int)code.Parameter('P');
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            Model.Provider.Get.Channels[code.Channel].Compatibility = compatibility;
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Print the diagnostics
        /// </summary>
        /// <param name="result">Target to write to</param>
        /// <returns>Asynchronous task</returns>
        private static async Task Diagnostics(CodeResult result)
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("=== Duet Control Server ===");
            builder.AppendLine($"Duet Control Server v{Assembly.GetExecutingAssembly().GetName().Version}");
            await SPI.Interface.Diagnostics(builder);
            SPI.DataTransfer.Diagnostics(builder);
            await Print.Diagnostics(builder);

            result.Add(MessageType.Success, builder.ToString().TrimEnd());
        }
    }
}
