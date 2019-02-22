﻿using Newtonsoft.Json;
using NiceHashMiner.Configs;
using NiceHashMiner.Interfaces;
using NVIDIA.NVAPI;
using ManagedCuda.Nvml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Windows.Forms;
using NiceHashMiner.Devices.Querying;
using NiceHashMinerLegacy.Common.Enums;
using static NiceHashMiner.Translations;
using NiceHashMiner.Devices.OpenCL;
using NiceHashMiner.PInvoke;

namespace NiceHashMiner.Devices
{
    /// <summary>
    /// ComputeDeviceManager class is used to query ComputeDevices avaliable on the system.
    /// Query CPUs, GPUs [Nvidia, AMD]
    /// </summary>
    public class ComputeDeviceManager
    {
        public static class Query
        {
            private const string Tag = "ComputeDeviceManager.Query";

            private static readonly NvidiaSmiDriver NvidiaRecomendedDriver = new NvidiaSmiDriver(372, 54); // 372.54;
            private static readonly NvidiaSmiDriver NvidiaMinDetectionDriver = new NvidiaSmiDriver(362, 61); // 362.61;

            private static void ShowMessageAndStep(string infoMsg)
            {
                MessageNotifier?.SetMessageAndIncrementStep(infoMsg);
            }

            public static IMessageNotifier MessageNotifier { get; private set; }

            public static bool CheckVideoControllersCountMismath()
            {
                // this function checks if count of CUDA devices is same as it was on application start, reason for that is
                // because of some reason (especially when algo switching occure) CUDA devices are dissapiring from system
                // creating tons of problems e.g. miners stop mining, lower rig hashrate etc.

                /* commented because when GPU is "lost" windows still see all of them
                // first check windows video controlers
                List<VideoControllerData> currentAvaliableVideoControllers = new List<VideoControllerData>();
                WindowsDisplayAdapters.QueryVideoControllers(currentAvaliableVideoControllers, false);
                

                int GPUsOld = AvailableVideoControllers.Count;
                int GPUsNew = currentAvaliableVideoControllers.Count;

                Helpers.ConsolePrint("ComputeDeviceManager.CheckCount", "Video controlers GPUsOld: " + GPUsOld.ToString() + " GPUsNew:" + GPUsNew.ToString());
                */

                // check CUDA devices

                // TODO
                return false;

                //var currentCudaDevices = new List<CudaDevice>();
                //if (!Nvidia.IsSkipNvidia())
                //    Nvidia.QueryCudaDevices(ref currentCudaDevices);

                //var gpusOld = _cudaDevices.Count;
                //var gpusNew = currentCudaDevices.Count;

                //Helpers.ConsolePrint("ComputeDeviceManager.CheckCount",
                //    "CUDA GPUs count: Old: " + gpusOld + " / New: " + gpusNew);

                //return (gpusNew < gpusOld);
            }

            public static void QueryDevices(IMessageNotifier messageNotifier)
            {
                MessageNotifier = messageNotifier;
                // #0 get video controllers, used for cross checking
                SystemSpecs.QueryVideoControllers();
                // Order important CPU Query must be first
                // #1 CPU
                Cpu.QueryCpus();
                // #2 CUDA
                var numDevs = 0;
                NvidiaQuery nvQuery = null;
                if (ConfigManager.GeneralConfig.DeviceDetection.DisableDetectionNVIDIA)
                {
                    Helpers.ConsolePrint(Tag, "Skipping NVIDIA device detection, settings are set to disabled");
                }
                else
                {
                    ShowMessageAndStep(Tr("Querying CUDA devices"));
                    nvQuery = new NvidiaQuery();
                    nvQuery.QueryCudaDevices();
                }
                // OpenCL and AMD
                if (ConfigManager.GeneralConfig.DeviceDetection.DisableDetectionAMD)
                {
                    Helpers.ConsolePrint(Tag, "Skipping AMD device detection, settings set to disabled");
                    ShowMessageAndStep(Tr("Skip check for AMD OpenCL GPUs"));
                }
                else
                {
                    // #3 OpenCL
                    ShowMessageAndStep(Tr("Querying OpenCL devices"));
                    OpenCL.QueryOpenCLDevices();
                    // #4 AMD query AMD from OpenCL devices, get serial and add devices
                    ShowMessageAndStep(Tr("Checking AMD OpenCL GPUs"));
                    var amd = new AmdQuery(numDevs);
                    AmdDevices = amd.QueryAmd(_isOpenCLQuerySuccess, _openCLQueryResult);
                }
                // #5 uncheck CPU if GPUs present, call it after we Query all devices
                AvailableDevices.UncheckCpuIfGpu();

                // TODO update this to report undetected hardware
                // #6 check NVIDIA, AMD devices count
                bool nvCountMatched;
                {
                    var amdCount = 0;
                    var nvidiaCount = 0;
                    foreach (var vidCtrl in SystemSpecs.AvailableVideoControllers)
                    {
                        if (vidCtrl.Name.ToLower().Contains("nvidia") && CudaUnsupported.IsSupported(vidCtrl.Name))
                        {
                            nvidiaCount += 1;
                        }
                        else if (vidCtrl.Name.ToLower().Contains("nvidia"))
                        {
                            Helpers.ConsolePrint(Tag,
                                "Device not supported NVIDIA/CUDA device not supported " + vidCtrl.Name);
                        }
                        amdCount += (vidCtrl.Name.ToLower().Contains("amd")) ? 1 : 0;
                    }

                    nvCountMatched = nvidiaCount == nvQuery?.CudaDevices?.Count;

                    Helpers.ConsolePrint(Tag,
                        nvCountMatched
                            ? "Cuda NVIDIA/CUDA device count GOOD"
                            : "Cuda NVIDIA/CUDA device count BAD!!!");
                    Helpers.ConsolePrint(Tag,
                        amdCount == AmdDevices.Count ? "AMD GPU device count GOOD" : "AMD GPU device count BAD!!!");
                }
                // allerts
                // TODO: Too much GUI code here, should return list of errors to caller instead

                if (SystemSpecs.HasNvidiaVideoController)
                {
                    var currentDriver = NvidiaQuery.GetNvSmiDriverAsync().Result;

                    // if we have nvidia cards but no CUDA devices tell the user to upgrade driver
                    var isNvidiaErrorShown = false; // to prevent showing twice
                    var showWarning = ConfigManager.GeneralConfig.ShowDriverVersionWarning;
                    if (showWarning && !nvCountMatched && currentDriver < NvidiaMinDetectionDriver)
                    {
                        isNvidiaErrorShown = true;
                        var minDriver = NvidiaMinDetectionDriver.ToString();
                        var recomendDriver = NvidiaRecomendedDriver.ToString();
                        MessageBox.Show(string.Format(
                                Tr(
                                    "We have detected that your system has Nvidia GPUs, but your driver is older than {0}. In order for NiceHash Miner Legacy to work correctly you should upgrade your drivers to recommended {1} or newer. If you still see this warning after updating the driver please uninstall all your Nvidia drivers and make a clean install of the latest official driver from http://www.nvidia.com."),
                                minDriver, recomendDriver),
                            Tr("Nvidia Recomended driver"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }

                    // recomended driver
                    if (showWarning && currentDriver < NvidiaRecomendedDriver &&
                        !isNvidiaErrorShown && currentDriver.LeftPart > -1)
                    {
                        var recomendDrvier = NvidiaRecomendedDriver.ToString();
                        var nvdriverString = currentDriver.LeftPart > -1
                            ? string.Format(Tr(" (current {0})"), currentDriver)
                            : "";
                        MessageBox.Show(string.Format(
                                Tr(
                                    "We have detected that your Nvidia Driver is older than {0}{1}. We recommend you to update to {2} or newer."),
                                recomendDrvier, nvdriverString, recomendDrvier),
                            Tr("Nvidia Recomended driver"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                // no devices found
                if (AvailableDevices.Devices.Count <= 0)
                {
                    var result = MessageBox.Show(Tr("No supported devices are found. Select the OK button for help or cancel to continue."),
                        Tr("No Supported Devices"),
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (result == DialogResult.OK)
                    {
                        Process.Start(Links.NhmNoDevHelp);
                    }
                }

                SortBusIDs(DeviceType.NVIDIA);
                SortBusIDs(DeviceType.AMD);

                // get GPUs RAM sum
                // bytes
                var nvRamSum = 0ul;
                var amdRamSum = 0ul;
                foreach (var dev in AvailableDevices.Devices)
                {
                    if (dev.DeviceType == DeviceType.NVIDIA)
                    {
                        nvRamSum += dev.GpuRam;
                    }
                    else if (dev.DeviceType == DeviceType.AMD)
                    {
                        amdRamSum += dev.GpuRam;
                    }
                }
                // Make gpu ram needed not larger than 4GB per GPU
                var totalGpuRam = Math.Min((ulong) ((nvRamSum + amdRamSum) * 0.6 / 1024),
                    (ulong) AvailableDevices.AvailGpUs * 4 * 1024 * 1024);
                var totalSysRam = SystemSpecs.FreePhysicalMemory + SystemSpecs.FreeVirtualMemory;
                // check
                if (ConfigManager.GeneralConfig.ShowDriverVersionWarning && totalSysRam < totalGpuRam)
                {
                    Helpers.ConsolePrint(Tag, "virtual memory size BAD");
                    MessageBox.Show(Tr("NiceHash Miner Legacy recommends increasing virtual memory size so that all algorithms would work fine."),
                        Tr("Warning!"),
                        MessageBoxButtons.OK);
                }
                else
                {
                    Helpers.ConsolePrint(Tag, "virtual memory size GOOD");
                }

                // #x remove reference
                MessageNotifier = null;
            }

            #region Helpers

            private static void SortBusIDs(DeviceType type)
            {
                var devs = AvailableDevices.Devices.Where(d => d.DeviceType == type);
                var sortedDevs = devs.OrderBy(d => d.BusID).ToList();

                for (var i = 0; i < sortedDevs.Count; i++)
                {
                    sortedDevs[i].IDByBus = i;
                }
            }

            private static OpenCLDeviceDetectionResult _openCLQueryResult;
            private static bool _isOpenCLQuerySuccess = false;

            private static class OpenCL
            {
                public static void QueryOpenCLDevices()
                {

                    Helpers.ConsolePrint(Tag, "QueryOpenCLDevices START");

                    string _queryOpenCLDevicesString = "";
                    try
                    {
                        _queryOpenCLDevicesString = DeviceDetection.GetOpenCLDevices();
                        _openCLQueryResult = JsonConvert.DeserializeObject<OpenCLDeviceDetectionResult>(_queryOpenCLDevicesString, Globals.JsonSettings);
                    }
                    catch (Exception ex)
                    {
                        // TODO print AMD detection string
                        Helpers.ConsolePrint(Tag, "AMDOpenCLDeviceDetection threw Exception: " + ex.Message);
                        _openCLQueryResult = null;
                    }

                    if (_openCLQueryResult == null)
                    {
                        Helpers.ConsolePrint(Tag,
                            "AMDOpenCLDeviceDetection found no devices. AMDOpenCLDeviceDetection returned: " +
                            _queryOpenCLDevicesString);
                    }
                    else /*if(_openCLQueryResult.ErrorString == "" || _openCLQueryResult.Status == "OK")*/
                    {
                        _isOpenCLQuerySuccess = true;
                        var stringBuilder = new StringBuilder();
                        stringBuilder.AppendLine("");
                        stringBuilder.AppendLine("AMDOpenCLDeviceDetection found devices success:");
                        foreach (var oclElem in _openCLQueryResult.Platforms)
                        {
                            stringBuilder.AppendLine($"\tFound devices for platform: {oclElem.PlatformName}");
                            foreach (var oclDev in oclElem.Devices)
                            {
                                stringBuilder.AppendLine("\t\tDevice:");
                                stringBuilder.AppendLine($"\t\t\tDevice ID {oclDev.DeviceID}");
                                stringBuilder.AppendLine($"\t\t\tDevice NAME {oclDev._CL_DEVICE_NAME}");
                                stringBuilder.AppendLine($"\t\t\tDevice TYPE {oclDev._CL_DEVICE_TYPE}");
                            }
                        }
                        Helpers.ConsolePrint(Tag, stringBuilder.ToString());
                    }
                    Helpers.ConsolePrint(Tag, "QueryOpenCLDevices END");
                }
            }

            public static List<OpenCLDevice> AmdDevices = new List<OpenCLDevice>();

            #endregion Helpers
        }
    }
}
