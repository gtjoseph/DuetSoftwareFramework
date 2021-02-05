﻿using DuetAPI.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network.Protocols
{
    /// <summary>
    /// Protocol management for SSH
    /// </summary>
    public static class SSH
    {
        /// <summary>
        /// Current SSH port
        /// </summary>
        public static int Port { get; private set; } = 22;

        /// <summary>
        /// Regex to capture the currently configured port
        /// </summary>
        private static Regex _portRegex = new Regex(@"^\s*Port\s*(\d+)");

        /// <summary>
        /// Regex to disable SSH
        /// </summary>
        private static Regex _disableSSHRegex = new Regex(@"^\s*ForceCommand\s+internal-sftp");

        /// <summary>
        /// Regex to enable SFTP
        /// </summary>
        private static Regex _enableSFTPRegex = new Regex(@"^\s*Subsystem\s+sftp\s+/usr/lib/openssh/sftp-server");

        /// <summary>
        /// Generic regex to capture the currently configured port
        /// </summary>
        private static Regex _genericPortRegex = new Regex(@"^\s*#?\s*Port\s*(\d+)");

        /// <summary>
        /// Generic regex to disable SSH
        /// </summary>
        private static Regex _genericDisableSSHRegex = new Regex(@"^\s*#?\s*ForceCommand\s+internal-sftp");

        /// <summary>
        /// Generic regex to enable SFTP
        /// </summary>
        private static Regex _genericEnableSFTPRegex = new Regex(@"^\s*#?\s*Subsystem\s+sftp\s+/usr/lib/openssh/sftp-server");

        /// <summary>
        /// Initialize the protocol configuration
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Init()
        {
            if (File.Exists("/etc/ssh/sshd_config"))
            {
                // Get the port and check if SSH and SFTP are enabled in the config
                bool sshEnabled = true, sftpEnabled = false;
                using FileStream sshdConfig = new FileStream("/etc/ssh/sshd_config", FileMode.Open, FileAccess.Read);
                using StreamReader reader = new StreamReader(sshdConfig);
                while (!reader.EndOfStream)
                {
                    string line = await reader.ReadLineAsync();

                    Match match = _portRegex.Match(line);
                    if (match.Success)
                    {
                        Port = int.Parse(match.Groups[1].Value);
                    }
                    else if (_disableSSHRegex.IsMatch(line))
                    {
                        sshEnabled = false;
                    }
                    else if (_enableSFTPRegex.IsMatch(line))
                    {
                        sftpEnabled = true;
                    }
                }

                // Register active protocols if the service is enabled
                if (await Command.ExecQuery("/usr/bin/systemctl", "is-enabled -q ssh.service"))
                {
                    if (sshEnabled)
                    {
                        await Manager.SetProtocol(NetworkProtocol.SSH, true);
                    }

                    if (sftpEnabled)
                    {
                        await Manager.SetProtocol(NetworkProtocol.SFTP, true);
                    }
                }
            }
        }

        /// <summarySFTP>
        /// Configure the SSH server
        /// </summary>
        /// <param name="enabled">Enable Telnet</param>
        /// <param name="port">Port</param>
        /// <param name="secure"></param>
        /// <returns>Configuration result</returns>
        public static Task<Message> Configure(bool? enabled, int? port) => Configure(enabled, null, port);

        /// <summary>
        /// Configure both SSH and SFTP
        /// </summary>
        /// <param name="enableSSH"></param>
        /// <param name="enableSFTP"></param>
        /// <param name="port"></param>
        /// <returns></returns>
        internal static async Task<Message> Configure(bool? enableSSH, bool? enableSFTP, int? port)
        {
            // Don't proceed if no SSH config is present
            if (!File.Exists("/etc/ssh/sshd_config"))
            {
                return new Message(MessageType.Error, "Cannot configure SSH because no configuration could be found");
            }

            // Check SSH/SFTP
            bool serviceWasDisabled = Manager.EnabledProtocols.Contains(NetworkProtocol.SSH) || Manager.EnabledProtocols.Contains(NetworkProtocol.SFTP);
            bool servicesChanged = false;
            if (enableSSH != null && enableSSH != Manager.EnabledProtocols.Contains(NetworkProtocol.SSH))
            {
                await Manager.SetProtocol(NetworkProtocol.SSH, enableSSH.Value);
                servicesChanged = true;
            }
            if (enableSFTP != null && enableSFTP != Manager.EnabledProtocols.Contains(NetworkProtocol.SFTP))
            {
                await Manager.SetProtocol(NetworkProtocol.SFTP, enableSFTP.Value);
                servicesChanged = true;
            }

            // Check common port
            bool portChanged = false;
            if (port != null)
            {
                Port = port.Value;
                portChanged = true;
            }

            // Don't do anything else if the config remains identical
            if (!servicesChanged && !portChanged)
            {
                return new Message();
            }
            bool serviceEnabled = Manager.EnabledProtocols.Contains(NetworkProtocol.SSH) || Manager.EnabledProtocols.Contains(NetworkProtocol.SFTP);

            // Modify the config file
            using (FileStream configStream = new FileStream("/etc/ssh/sshd_config", FileMode.Open, FileAccess.ReadWrite))
            {
                using MemoryStream newConfigStream = new MemoryStream((int)configStream.Length);
                using (StreamReader reader = new StreamReader(configStream))
                {
                    using StreamWriter writer = new StreamWriter(newConfigStream);

                    // Read the old config line by line and modify it as needed
                    bool portWritten = false, sshDisabled = false, sftpEnabled = false;
                    while (!reader.EndOfStream)
                    {
                        string line = await reader.ReadLineAsync();
                        if (_genericPortRegex.IsMatch(line))
                        {
                            if (portChanged || _portRegex.IsMatch(line))
                            {
                                // Change this line only if a custom port was or is suposed to be set
                                line = $"Port {Port}";
                            }
                            portWritten = true;
                        }
                        else if (serviceEnabled)
                        {
                            if (_genericDisableSSHRegex.IsMatch(line))
                            {
                                if (Manager.EnabledProtocols.Contains(NetworkProtocol.SSH))
                                {
                                    if (!line.TrimStart().StartsWith('#'))
                                    {
                                        // SSH should be enabled but this command would disable it; comment it out
                                        line = '#' + line;
                                    }
                                }
                                else
                                {
                                    // Uncomment the line if necessary to disable SSH
                                    line = line.TrimStart(' ', '\t', '#');
                                    sshDisabled = true;
                                }
                            }
                            else if (_genericEnableSFTPRegex.IsMatch(line))
                            {
                                if (Manager.EnabledProtocols.Contains(NetworkProtocol.SFTP))
                                {
                                    // Uncomment the line if necessary to enable SFTP
                                    line = line.TrimStart(' ', '\t', '#');
                                    sftpEnabled = true;
                                }
                                else
                                {
                                    if (!line.TrimStart().StartsWith('#'))
                                    {
                                        // SFTP should be disabled but this command would enable it; comment it out
                                        line = '#' + line;
                                    }
                                }
                            }
                        }
                        await writer.WriteLineAsync(line);
                    }

                    // Add missing options
                    if (portChanged && !portWritten)
                    {
                        await writer.WriteLineAsync($"Port {Port}");
                    }
                    if (servicesChanged)
                    {
                        if (!sshDisabled && !Manager.EnabledProtocols.Contains(NetworkProtocol.SSH))
                        {
                            // Disable SSH by allowing only SFTP
                            await writer.WriteLineAsync("ForceCommand internal-sftp");
                        }
                        if (!sftpEnabled && Manager.EnabledProtocols.Contains(NetworkProtocol.SFTP))
                        {
                            // Enable SFTP subsystem
                            await writer.WriteLineAsync("Subsystem\tsftp\t/usr/lib/openssh/sftp-server");
                        }
                    }
                }

                // Overwrite the previous config
                configStream.Seek(0, SeekOrigin.Begin);
                configStream.SetLength(newConfigStream.Length);
                await newConfigStream.CopyToAsync(configStream);
            }

            // Enable or disable the service
            if (serviceEnabled)
            {
                if (serviceWasDisabled)
                {
                    string startOutput = await Command.Execute("/usr/bin/systemctl", "start sshd.service");
                    string enableOutput = await Command.Execute("/usr/bin/systemctl", "enable sshd.service");
                    return new Message(MessageType.Success, string.Join('\n', startOutput.TrimEnd(), enableOutput).TrimEnd());
                }

                string restartOutput = await Command.Execute("/usr/bin/systemctl", "restart sshd.service");
                return new Message(MessageType.Success, restartOutput);
            }
            else if (!serviceWasDisabled)
            {
                string stopOutput = await Command.Execute("/usr/bin/systemctl", "stop sshd.service");
                string disableOutput = await Command.Execute("/usr/bin/systemctl", "disable sshd.service");
                return new Message(MessageType.Success, string.Join('\n', stopOutput.TrimEnd(), disableOutput).TrimEnd());
            }

            // Done
            return new Message();
        }

        /// <summary>
        /// Report the current state of the SSH protocol
        /// </summary>
        /// <param name="builder">String builder</param>
        public static void Report(StringBuilder builder)
        {
            if (Manager.EnabledProtocols.Contains(NetworkProtocol.SSH))
            {
                builder.AppendLine($"SSH is enabled on port {Port}");
            }
            else
            {
                builder.AppendLine("SSH is disabled");
            }
        }
    }
}