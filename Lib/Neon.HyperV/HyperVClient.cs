﻿//-----------------------------------------------------------------------------
// FILE:	    HyperVClient.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:	Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32;

using Neon.Common;
using Neon.Net;
using Neon.Windows;

using Newtonsoft.Json.Linq;

namespace Neon.HyperV
{
    /// <summary>
    /// <para>
    /// Abstracts management of local Hyper-V virtual machines and components
    /// on Windows via PowerShell.
    /// </para>
    /// <note>
    /// This class requires elevated administrative rights.
    /// </note>
    /// </summary>
    /// <threadsafety instance="false"/>
    public class HyperVClient : IDisposable
    {
        //---------------------------------------------------------------------
        // Static members

        /// <summary>
        /// The Hyper-V cmdlet namespace prefix used to avoid conflicts with things
        /// like the VMware cmdlets.
        /// </summary>
        private const string HyperVNamespace = @"Hyper-V\";

        /// <summary>
        /// The Hyper-V namespace prefix for the TCP/IP related cmdlets.
        /// </summary>
        private const string NetTcpIpNamespace = @"NetTCPIP\";

        /// <summary>
        /// The Hyper-V namespace prefix for the NAT related cmdlets.
        /// </summary>
        private const string NetNatNamespace = @"NetNat\";

        /// <summary>
        /// Returns the path to the user's default Hyper-V virtual drive folder.
        /// </summary>
        public static string DefaultDriveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Hyper-V", "Virtual hard disks");

        //---------------------------------------------------------------------
        // Instance members

        private PowerShell      powershell;

        /// <summary>
        /// Default constructor to be used to manage Hyper-V objects
        /// on the local Windows machine.
        /// </summary>
        public HyperVClient()
        {
            if (!NeonHelper.IsWindows)
            {
                throw new NotSupportedException($"{nameof(HyperVClient)} is only supported on Windows.");
            }

            powershell = new PowerShell();
        }

        /// <summary>
        /// Releases all resources associated with the instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Releases all associated resources.
        /// </summary>
        /// <param name="disposing">Pass <c>true</c> if we're disposing, <c>false</c> if we're finalizing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (powershell != null)
                {
                    powershell.Dispose();
                    powershell = null;
                }

                GC.SuppressFinalize(this);
            }

            powershell = null;
        }

        /// <summary>
        /// Ensures that the instance has not been disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if the instance has been disposed.</exception>
        private void CheckDisposed()
        {
            if (powershell == null)
            {
                throw new ObjectDisposedException(nameof(HyperVClient));
            }
        }

        /// <summary>
        /// Extracts virtual machine properties from a dynamic PowerShell result.
        /// </summary>
        /// <param name="rawMachine">The dynamic machine properties.</param>
        /// <returns>The parsed <see cref="VirtualMachine"/>.</returns>
        private VirtualMachine ExtractVm(dynamic rawMachine)
        {
            var vm = new VirtualMachine();

            // Extract the VM name.

            vm.Name = rawMachine.Name;

            // Extract the VM state.

            switch ((string)rawMachine.State)
            {
                case "Off":

                    vm.State = VirtualMachineState.Off;
                    break;

                case "Starting":

                    vm.State = VirtualMachineState.Starting;
                    break;

                case "Running":

                    vm.State = VirtualMachineState.Running;
                    break;

                case "Paused":

                    vm.State = VirtualMachineState.Paused;
                    break;

                case "Saved":

                    vm.State = VirtualMachineState.Saved;
                    break;

                default:

                    vm.State = VirtualMachineState.Unknown;
                    break;
            }

            // Extract the connected switch name from the first network adapter (if any).

            // $note(jefflill):
            // 
            // We don't currently support VMs with multiple network adapters; we'll
            // only capture the name of the switch connected to the first adapter.

            var adapters = (JArray)rawMachine.NetworkAdapters;

            if (adapters.Count > 0)
            {
                vm.SwitchName = ((dynamic)adapters[0]).SwitchName;
            }

            return vm;
        }

        /// <summary>
        /// Determines whether the current machine is already running as a Hyper-V
        /// virtual machine and that any Hyper-V VMs deployed on this machine can
        /// be considered to be nested.
        /// </summary>
        /// <remarks>
        /// <para>
        /// We use the presence of this registry value to detect VM nesting:
        /// </para>
        /// <example>
        /// HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Virtual Machine\Auto\OSName
        /// </example>
        /// </remarks>
#pragma warning disable CA1416
        public bool IsNestedVirtualization => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Virtual Machine\Auto", "OSName", null) != null;
#pragma warning restore CA1416

        /// <summary>
        /// Creates a virtual machine. 
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <param name="memorySize">
        /// A string specifying the memory size.  This can be a long byte count or a
        /// byte count or a number with units like <b>512MiB</b>, <b>0.5GiB</b>, <b>2GiB</b>, 
        /// or <b>1TiB</b>.  This defaults to <b>2GiB</b>.
        /// </param>
        /// <param name="processorCount">
        /// The number of virutal processors to assign to the machine.  This defaults to <b>4</b>.
        /// </param>
        /// <param name="driveSize">
        /// A string specifying the primary disk size.  This can be a long byte count or a
        /// byte count or a number with units like <b>512MB</b>, <b>0.5GiB</b>, <b>2GiB</b>, 
        /// or <b>1TiB</b>.  Pass <c>null</c> to leave the disk alone.  This defaults to <c>null</c>.
        /// </param>
        /// <param name="drivePath">
        /// Optionally specifies the path where the virtual hard drive will be located.  Pass 
        /// <c>null</c> or empty to default to <b>MACHINE-NAME.vhdx</b> located in the default
        /// Hyper-V virtual machine drive folder.
        /// </param>
        /// <param name="checkpointDrives">Optionally enables drive checkpoints.  This defaults to <c>false</c>.</param>
        /// <param name="templateDrivePath">
        /// If this is specified and <paramref name="drivePath"/> is not <c>null</c> then
        /// the hard drive template at <paramref name="templateDrivePath"/> will be copied
        /// to <paramref name="drivePath"/> before creating the machine.
        /// </param>
        /// <param name="switchName">Optional name of the virtual switch.</param>
        /// <param name="extraDrives">
        /// Optionally specifies any additional virtual drives to be created and 
        /// then attached to the new virtual machine.
        /// </param>
        /// <remarks>
        /// <note>
        /// The <see cref="VirtualDrive.Path"/> property of <paramref name="extraDrives"/> may be
        /// passed as <c>null</c> or empty.  In this case, the drive name will default to
        /// being located in the standard Hyper-V virtual drivers folder and will be named
        /// <b>MACHINE-NAME-#.vhdx</b>, where <b>#</b> is the one-based index of the drive
        /// in the enumeration.
        /// </note>
        /// </remarks>
        public void AddVm(
            string                      machineName, 
            string                      memorySize        = "2GiB", 
            int                         processorCount    = 4,
            string                      driveSize         = null,
            string                      drivePath         = null,
            bool                        checkpointDrives  = false,
            string                      templateDrivePath = null, 
            string                      switchName        = null,
            IEnumerable<VirtualDrive>   extraDrives       = null)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            memorySize = ByteUnits.Parse(memorySize).ToString();

            if (driveSize != null)
            {
                driveSize = ByteUnits.Parse(driveSize).ToString();
            }

            var driveFolder = DefaultDriveFolder;

            if (string.IsNullOrEmpty(drivePath))
            {
                drivePath = Path.Combine(driveFolder, $"{machineName}-[0].vhdx");
            }
            else
            {
                driveFolder = Path.GetDirectoryName(Path.GetFullPath(drivePath));
            }

            if (VmExists(machineName))
            {
                throw new HyperVException($"Virtual machine [{machineName}] already exists.");
            }

            // Copy the template VHDX file.

            if (templateDrivePath != null)
            {
                File.Copy(templateDrivePath, drivePath);
            }

            // Resize the VHDX if requested.

            if (driveSize != null)
            {
                powershell.Execute($"{HyperVNamespace}Resize-VHD -Path '{drivePath}' -SizeBytes {driveSize}");
            }

            // Create the virtual machine.

            var command = $"{HyperVNamespace}New-VM -Name '{machineName}' -MemoryStartupBytes {memorySize}  -Generation 1";

            if (!string.IsNullOrEmpty(drivePath))
            {
                command += $" -VHDPath '{drivePath}'";
            }

            if (!string.IsNullOrEmpty(switchName))
            {
                command += $" -SwitchName '{switchName}'";
            }

            try
            {
                powershell.Execute(command);
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }

            // We need to configure the VM's processor count and min/max memory settings.

            try
            {
                powershell.Execute($"{HyperVNamespace}Set-VM -Name '{machineName}' -ProcessorCount {processorCount} -StaticMemory -MemoryStartupBytes {memorySize}");
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }

            // Create and attach any additional drives as required.

            if (extraDrives != null)
            {
                var diskNumber = 1;

                foreach (var drive in extraDrives)
                {
                    if (string.IsNullOrEmpty(drive.Path))
                    {
                        drive.Path = Path.Combine(driveFolder, $"{machineName}-[{diskNumber}].vhdx");
                    }

                    if (drive.Size <= 0)
                    {
                        throw new ArgumentException("Virtual drive size must be greater than 0.", nameof(drive));
                    }

                    NeonHelper.DeleteFile(drive.Path);

                    var fixedOrDynamic = drive.IsDynamic ? "-Dynamic" : "-Fixed";

                    try
                    {
                        powershell.Execute($"{HyperVNamespace}New-VHD -Path '{drive.Path}' {fixedOrDynamic} -SizeBytes {drive.Size} -BlockSizeBytes 1MB");
                        powershell.Execute($"{HyperVNamespace}Add-VMHardDiskDrive -VMName '{machineName}' -Path \"{drive.Path}\"");
                    }
                    catch (Exception e)
                    {
                        throw new HyperVException(e.Message, e);
                    }

                    diskNumber++;
                }
            }

            // Windows 10 releases since the August 2017 Creators Update enable automatic
            // virtual drive checkpointing (which is annoying).  We're going to disable this
            // by default.

            if (!checkpointDrives)
            {
                try
                {
                    powershell.Execute($"{HyperVNamespace}Set-VM -CheckpointType Disabled -Name '{machineName}'");
                }
                catch (Exception e)
                {
                    throw new HyperVException(e.Message, e);
                }
            }

            // We need to do some extra configuration for nested virtual machines:
            //
            //      https://docs.microsoft.com/en-us/virtualization/hyper-v-on-windows/user-guide/nested-virtualization

            if (IsNestedVirtualization)
            {
                // Enable nested virtualization for the VM.

                powershell.Execute($"{HyperVNamespace}Set-VMProcessor -VMName '{machineName}' -ExposeVirtualizationExtensions $true");

                // Enable MAC address spoofing for the VMs network adapter.

                powershell.Execute($"{HyperVNamespace}Set-VMNetworkAdapter -VMName '{machineName}' -MacAddressSpoofing On");
            }
        }

        /// <summary>
        /// Removes a named virtual machine and all of its drives (by default).
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <param name="keepDrives">Optionally retains the VM disk files.</param>
        public void RemoveVm(string machineName, bool keepDrives = false)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            var machine = GetVm(machineName);
            var drives  = GetVmDrives(machineName);

            // Remove the machine along with any of of its virtual hard drive files.

            try
            {
                powershell.Execute($"{HyperVNamespace}Remove-VM -Name '{machineName}' -Force");
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }

            if (!keepDrives)
            {
                foreach (var drivePath in drives)
                {
                    File.Delete(drivePath);
                }
            }
        }

        /// <summary>
        /// Lists the virtual machines.
        /// </summary>
        /// <returns><see cref="IEnumerable{VirtualMachine}"/>.</returns>
        public IEnumerable<VirtualMachine> ListVms()
        {
            CheckDisposed();

            try
            {
                var machines = new List<VirtualMachine>();
                var table    = powershell.ExecuteJson($"{HyperVNamespace}Get-VM");

                foreach (dynamic rawMachine in table)
                {
                    machines.Add(ExtractVm(rawMachine));
                }

                return machines;
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }

        }

        /// <summary>
        /// Gets the current status for a named virtual machine.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <returns>The <see cref="VirtualMachine"/> or <c>null</c> when the virtual machine doesn't exist..</returns>
        public VirtualMachine GetVm(string machineName)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            return ListVms().SingleOrDefault(vm => vm.Name.Equals(machineName, StringComparison.InvariantCultureIgnoreCase));
        }

        /// <summary>
        /// Determines whether a named virtual machine exists.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <returns><c>true</c> if the machine exists.</returns>
        public bool VmExists(string machineName)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            return ListVms().Count(vm => vm.Name.Equals(machineName, StringComparison.InvariantCultureIgnoreCase)) > 0;
        }

        /// <summary>
        /// Starts the named virtual machine.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        public void StartVm(string machineName)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            try
            {
                powershell.Execute($"{HyperVNamespace}Start-VM -Name '{machineName}'");
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Stops the named virtual machine.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <param name="turnOff">
        /// <para>
        /// Optionally just turns the VM off without performing a graceful shutdown first.
        /// </para>
        /// <note>
        /// <b>WARNING!</b> This could result in corruption or the the loss of unsaved data.
        /// </note>
        /// </param>
        public void StopVm(string machineName, bool turnOff = false)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            try
            {
                if (turnOff)
                {
                    powershell.Execute($"{HyperVNamespace}Stop-VM -Name '{machineName}' -TurnOff");
                }
                else
                {
                    powershell.Execute($"{HyperVNamespace}Stop-VM -Name '{machineName}'");
                }
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Persists the state of a running virtual machine and then stops it.  This is 
        /// equivalent to hibernation for a physical machine.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        public void SaveVm(string machineName)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            try
            {
                powershell.Execute($"{HyperVNamespace}Save-VM -Name '{machineName}'");
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Returns host file system paths to any virtual drives attached to
        /// the named virtual machine.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <returns>The list of fully qualified virtual drive file paths.</returns>
        public List<string> GetVmDrives(string machineName)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            try
            {
                var drives    = new List<string>();
                var rawDrives = powershell.ExecuteJson($"{HyperVNamespace}Get-VMHardDiskDrive -VMName '{machineName}'");

                foreach (dynamic rawDrive in rawDrives)
                {
                    drives.Add(rawDrive.Path.ToString());
                }

                return drives;
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Creates a new virtual drive and adds it to a virtual machine.
        /// </summary>
        /// <param name="machineName">The target virtual machine name.</param>
        /// <param name="drive">The new drive information.</param>
        public void AddVmDrive(string machineName, VirtualDrive drive)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            Covenant.Requires<ArgumentNullException>(drive != null, nameof(drive));
            CheckDisposed();

            // Delete the drive file if it already exists.

            NeonHelper.DeleteFile(drive.Path);

            var fixedOrDynamic = drive.IsDynamic ? "-Dynamic" : "-Fixed";

            powershell.Execute($"{HyperVNamespace}New-VHD -Path '{drive.Path}' {fixedOrDynamic} -SizeBytes {drive.Size} -BlockSizeBytes 1MB");
            powershell.Execute($"{HyperVNamespace}Add-VMHardDiskDrive -VMName '{machineName}' -Path '{drive.Path}'");
        }

        /// <summary>
        /// <para>
        /// Compacts a dynamic VHD or VHDX virtual disk file.
        /// </para>
        /// <note>
        /// The disk may be mounted to a VM but the VM cannot be running.
        /// </note>
        /// </summary>
        /// <param name="drivePath">Path to the virtual drive file.</param>
        public void CompactDrive(string drivePath)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(drivePath), nameof(drivePath));

            powershell.Execute($"Mount-VHD '{drivePath}' -ReadOnly");
            powershell.Execute($"Optimize-VHD '{drivePath}' -Mode Full");
            powershell.Execute($"Dismount-VHD '{drivePath}'");
        }

        /// <summary>
        /// Inserts an ISO file as the DVD/CD for a virtual machine, ejecting any
        /// existing disc.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <param name="isoPath">Path to the ISO file.</param>
        public void InsertVmDvd(string machineName, string isoPath)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            EjectVmDvd(machineName);
            powershell.Execute($"Add-VMDvdDrive -VMName '{machineName}' -Path '{isoPath}' -ControllerNumber 1 -ControllerLocation 0");
        }

        /// <summary>
        /// Ejects any DVD/CD that's currently inserted into a virtual machine.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        public void EjectVmDvd(string machineName)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            powershell.Execute($"Remove-VMDvdDrive -VMName '{machineName}' -ControllerNumber 1 -ControllerLocation 0");
        }

        /// <summary>
        /// Returns the virtual network switches.
        /// </summary>
        /// <returns>The list of switches.</returns>
        public List<VirtualSwitch> ListSwitches()
        {
            CheckDisposed();

            try
            {
                var switches    = new List<VirtualSwitch>();
                var rawSwitches = powershell.ExecuteJson($"{HyperVNamespace}Get-VMSwitch");

                foreach (dynamic rawSwitch in rawSwitches)
                {
                    var virtualSwitch
                        = new VirtualSwitch()
                        {
                            Name = rawSwitch.Name
                        };

                    switch (rawSwitch.SwitchType.Value)
                    {
                        case "Internal":

                            virtualSwitch.Type = VirtualSwitchType.Internal;
                            break;

                        case "External":

                            virtualSwitch.Type = VirtualSwitchType.External;
                            break;

                        case "Private":

                            virtualSwitch.Type = VirtualSwitchType.Private;
                            break;

                        default:

                            virtualSwitch.Type = VirtualSwitchType.Unknown;
                            break;
                    }

                    switches.Add(virtualSwitch);
                }

                return switches;
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Returns information for a Hyper-V switch by name.
        /// </summary>
        /// <param name="switchName">The switch name.</param>
        /// <returns>The <see cref="VirtualSwitch"/> when present or <c>null</c>.</returns>
        public VirtualSwitch GetSwitch(string switchName)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(switchName), nameof(switchName));
            CheckDisposed();

            return ListSwitches().FirstOrDefault(@switch => @switch.Name.Equals(switchName, StringComparison.InvariantCultureIgnoreCase));
        }

        /// <summary>
        /// Adds a virtual Hyper-V switch that has external connectivity.
        /// </summary>
        /// <param name="switchName">The new switch name.</param>
        /// <param name="gateway">Address of the LAN gateway, used to identify the connected network interface.</param>
        public void NewExternalSwitch(string switchName, IPAddress gateway)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(switchName), nameof(switchName));
            Covenant.Requires<ArgumentNullException>(gateway != null, nameof(gateway));
            CheckDisposed();

            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                throw new HyperVException($"No network connection detected.  Hyper-V provisioning requires a connected network.");
            }

            // We're going to look for an active (non-loopback) interface that is configured
            // to use the correct upstream gateway and also has at least one nameserver.

            // $todo(jefflill):
            //
            // This may be a problem for machines with multiple active network interfaces
            // because I may choose the wrong one (e.g. the slower card).  It might be
            // useful to have an optional cluster node definition property the explicitly
            // specifies the adapter to use for a given node.
            //
            // Another problem we'll see is for laptops with wi-fi adapters.  Lets say we
            // setup a cluster when wi-fi is connected and then the user docks the laptop,
            // connecting to a new wired adapter.  The cluster's virtual switch will still
            // be configured to use the wi-fi adapter.  The only workaround for this is
            // probably for the user to modify the virtual switch.
            //
            // This last issue is really just another indication that clusters aren't 
            // really portable in the sense that you can't expect to relocate a cluster 
            // from one network environment to another (that's why we bought the portable 
            // routers for motel use). So we'll consider this as by design.

            var connectedAdapter = (NetworkInterface)null;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var nicProperties = nic.GetIPProperties();

                if (nicProperties.DnsAddresses.Count > 0 &&
                    nicProperties.GatewayAddresses.Count(nicGateway => nicGateway.Address.Equals(gateway)) > 0)
                {
                    connectedAdapter = nic;
                    break;
                }
            }

            if (connectedAdapter == null)
            {
                throw new HyperVException($"Cannot identify a connected network adapter.");
            }

            try
            {
                var adapters      = powershell.ExecuteJson($"Get-NetAdapter");
                var targetAdapter = (string)null;

                foreach (dynamic adapter in adapters)
                {
                    if (((string)adapter.Name).Equals(connectedAdapter.Name, StringComparison.InvariantCultureIgnoreCase))
                    {
                        targetAdapter = adapter.Name;
                        break;
                    }
                }

                if (targetAdapter == null)
                {
                    throw new HyperVException($"Internal Error: Cannot identify a connected network adapter.");
                }

                powershell.Execute($"{HyperVNamespace}New-VMSwitch -Name '{switchName}' -NetAdapterName '{targetAdapter}'");
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Adds an internal Hyper-V switch configured for the specified subnet and gateway as well
        /// as an optional NAT enabling external connectivity.
        /// </summary>
        /// <param name="switchName">The new switch name.</param>
        /// <param name="subnet">Specifies the internal subnet.</param>
        /// <param name="addNat">Optionally configure a NAT to support external routing.</param>
        public void NewInternalSwitch(string switchName, NetworkCidr subnet, bool addNat = false)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(switchName), nameof(switchName));
            Covenant.Requires<ArgumentNullException>(subnet != null, nameof(subnet));
            CheckDisposed();

            var gatewayAddress = subnet.FirstUsableAddress;

            powershell.Execute($"{HyperVNamespace}New-VMSwitch -Name '{switchName}' -SwitchType Internal");
            powershell.Execute($"{NetTcpIpNamespace}New-NetIPAddress -IPAddress {subnet.FirstUsableAddress} -PrefixLength {subnet.PrefixLength} -InterfaceAlias 'vEthernet ({switchName})'");

            if (addNat)
            {
                if (GetNatByName(switchName) == null)
                {
                    powershell.Execute($"{NetNatNamespace}New-NetNAT -Name '{switchName}' -InternalIPInterfaceAddressPrefix {subnet}");
                }
            }
        }

        /// <summary>
        /// Removes a named virtual switch, it it exists as well as any associated NAT (with the same name).
        /// </summary>
        /// <param name="switchName">The target switch name.</param>
        /// <param name="ignoreMissing">Optionally ignore missing items.</param>
        public void RemoveSwitch(string switchName, bool ignoreMissing = false)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(switchName), nameof(switchName));
            CheckDisposed();

            if (ListSwitches().Any(@switch => @switch.Name.Equals(switchName, StringComparison.InvariantCultureIgnoreCase)))
            {
                try
                {
                    powershell.Execute($"{HyperVNamespace}Remove-VMSwitch -Name '{switchName}' -Force");
                }
                catch
                {
                    if (!ignoreMissing)
                    {
                        throw;
                    }
                }
            }

            if (ListNats().Any(nat => nat.Name.Equals(switchName, StringComparison.InvariantCultureIgnoreCase)))
            {
                try
                {
                    powershell.Execute($"{HyperVNamespace}Remove-NetNat -Name '{switchName}' -Force");
                }
                catch
                {
                    if (!ignoreMissing)
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Returns the virtual network adapters attached to the named virtual machine.
        /// </summary>
        /// <param name="machineName">The machine name.</param>
        /// <param name="waitForAddresses">Optionally wait until at least one adapter has been able to acquire at least one IPv4 address.</param>
        /// <returns>The list of network adapters.</returns>
        public List<VirtualNetworkAdapter> GetVmNetworkAdapters(string machineName, bool waitForAddresses = false)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(machineName), nameof(machineName));
            CheckDisposed();

            try
            {
                var stopwatch = new Stopwatch();

                while (true)
                {
                    var adapters    = new List<VirtualNetworkAdapter>();
                    var rawAdapters = powershell.ExecuteJson($"{HyperVNamespace}Get-VMNetworkAdapter -VMName '{machineName}'");

                    adapters.Clear();

                    foreach (dynamic rawAdapter in rawAdapters)
                    {
                        var adapter
                            = new VirtualNetworkAdapter()
                            {
                                Name           = rawAdapter.Name,
                                VMName         = rawAdapter.VMName,
                                IsManagementOs = ((string)rawAdapter.IsManagementOs).Equals("True", StringComparison.InvariantCultureIgnoreCase),
                                SwitchName     = rawAdapter.SwitchName,
                                MacAddress     = rawAdapter.MacAddress,
                                Status         = (string)((JArray)rawAdapter.Status).FirstOrDefault()
                            };

                        // Parse the IP addresses.

                        var addresses = (JArray)rawAdapter.IPAddresses;

                        if (addresses.Count > 0)
                        {
                            foreach (string address in addresses)
                            {
                                if (!string.IsNullOrEmpty(address))
                                {
                                    var ipAddress = IPAddress.Parse(address.Trim());

                                    if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
                                    {
                                        adapter.Addresses.Add(IPAddress.Parse(address.Trim()));
                                    }
                                }
                            }
                        }

                        adapters.Add(adapter);
                    }

                    var retry = false;

                    foreach (var adapter in adapters)
                    {
                        if (adapter.Addresses.Count == 0 && waitForAddresses)
                        {
                            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(30))
                            {
                                throw new TimeoutException($"Network adapter [{adapter.Name}] for virtual machine [{machineName}] was not able to acquire an IP address.");
                            }

                            retry = true;
                            break;
                        }
                    }

                    if (retry)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                        continue;
                    }

                    return adapters;
                }
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// <para>
        /// Lists the virtual IPv4 addresses.
        /// </para>
        /// <note>
        /// Only IPv4 addresses are returned.  IPv6 and any other address types will be ignored.
        /// </note>
        /// </summary>
        /// <returns>A list of <see cref="VirtualIPAddress"/>.</returns>
        public List<VirtualIPAddress> ListIPAddresses()
        {
            CheckDisposed();

            try
            {
                var addresses    = new List<VirtualIPAddress>();
                var rawAddresses = powershell.ExecuteJson($"{NetTcpIpNamespace}Get-NetIPAddress");
                var switchRegex  = new Regex(@"^.*\((?<switch>.+)\)$");

                foreach (dynamic rawAddress in rawAddresses)
                {
                    // We're only listing IPv4  addresses.

                    var address = (string)rawAddress.IPv4Address;

                    if (string.IsNullOrEmpty(address))
                    {
                        continue;
                    }

                    // Extract the interface/switch name from the [InterfaceAlias] field,
                    // which will look something like:
                    //
                    //      vEthernet (neonkube)
                    //
                    // We'll extract the name within the parens if present, otherwise we'll
                    // take the entire property value as the name.

                    var interfaceAlias = (string)rawAddress.InterfaceAlias;
                    var match          = switchRegex.Match(interfaceAlias);
                    var interfaceName  = string.Empty;

                    if (match.Success)
                    {
                        interfaceName = match.Groups["switch"].Value;
                    }
                    else
                    {
                        interfaceName = interfaceAlias;
                    }

                    var virtualIPAddress
                        = new VirtualIPAddress()
                        {
                            Address       = address,
                            Subnet        = NetworkCidr.Parse($"{address}/{rawAddress.PrefixLength}"),
                            InterfaceName = interfaceName
                        };

                    addresses.Add(virtualIPAddress);
                }

                return addresses;
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Returns information about a virtual IP address.
        /// </summary>
        /// <param name="address">The desired IP address.</param>
        /// <returns>The <see cref="VirtualIPAddress"/> or <c>null</c> when it doesn't exist.</returns>
        public VirtualIPAddress GetIPAddress(string address)
        {
            Covenant.Requires<ArgumentNullException>(!string.IsNullOrEmpty(address), nameof(address));
            CheckDisposed();

            return ListIPAddresses().SingleOrDefault(addr => addr.Address == address);
        }

        /// <summary>
        /// Lists the virtual NATs.
        /// </summary>
        /// <returns>A list of <see cref="VirtualNat"/>.</returns>
        public List<VirtualNat> ListNats()
        {
            CheckDisposed();

            try
            {
                var nats    = new List<VirtualNat>();
                var rawNats = powershell.ExecuteJson($"{NetNatNamespace}Get-NetNAT");

                foreach (dynamic rawNat in rawNats)
                {
                    var name   = (string)null;
                    var subnet = (string)null;

                    foreach (dynamic rawProperty in rawNat.CimInstanceProperties)
                    {
                        switch ((string)rawProperty.Name)
                        {
                            case "Name":

                                name = rawProperty.Value;
                                break;

                            case "InternalIPInterfaceAddressPrefix":

                                subnet = rawProperty.Value;
                                break;
                        }

                        if (name != null && subnet != null)
                        {
                            break;
                        }
                    }

                    var nat = new VirtualNat()
                    {
                        Name   = name,
                        Subnet = subnet
                    };

                    nats.Add(nat);
                }

                return nats;
            }
            catch (Exception e)
            {
                throw new HyperVException(e.Message, e);
            }
        }

        /// <summary>
        /// Looks for a virtual NAT by name.
        /// </summary>
        /// <param name="name">The desired NAT name.</param>
        /// <returns>The <see cref="VirtualNat"/> or <c>null</c> if the NAT doesn't exist.</returns>
        public VirtualNat GetNatByName(string name)
        {
            CheckDisposed();

            return ListNats().FirstOrDefault(nat => nat.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        }

        /// <summary>
        /// Looks for a virtual NAT by subnet.
        /// </summary>
        /// <param name="subnet">The desired NAT subnet.</param>
        /// <returns>The <see cref="VirtualNat"/> or <c>null</c> if the NAT doesn't exist.</returns>
        public VirtualNat GetNatBySubnet(string subnet)
        {
            CheckDisposed();

            return ListNats().FirstOrDefault(nat => nat.Subnet == subnet);
        }
    }
}
