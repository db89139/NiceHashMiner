﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using NiceHashMiner.Configs;
using NiceHashMiner.Interfaces;
using NiceHashMiner.Enums;
using NiceHashMiner.Miners;
using System.Diagnostics;
using Newtonsoft.Json;
using ATI.ADL;
using System.Runtime.InteropServices;
using System.Management;
using System.IO;
using System.Globalization;
using NiceHashMiner.Utils;
using NiceHashMiner.Miners.Grouping;

namespace NiceHashMiner.Devices
{
    /// <summary>
    /// ComputeDeviceManager class is used to query ComputeDevices avaliable on the system.
    /// Query CPUs, GPUs [Nvidia, AMD]
    /// </summary>
    public class ComputeDeviceManager
    {
        public static class Query {
            const string TAG = "ComputeDeviceManager.Query";

            // format 372.54;
            private class NVIDIA_SMI_DRIVER {
                public NVIDIA_SMI_DRIVER(int left, int right) {
                    leftPart = left;
                    rightPart = right;
                }
                public bool IsLesserVersionThan(NVIDIA_SMI_DRIVER b) {
                    if(leftPart < b.leftPart) {
                        return true;
                    }
                    if (getRightVal(rightPart) < getRightVal(b.rightPart)) {
                        return true;
                    }
                    return false;
                }

                public override string ToString() {
                    return String.Format("{0}.{1}", leftPart, rightPart);
                }

                public int leftPart;
                public int rightPart;
                private int getRightVal(int val) {
                    if(val >= 10) {
                        return val;
                    }
                    return val * 10;
                } 
            }

            static readonly NVIDIA_SMI_DRIVER NVIDIA_RECOMENDED_DRIVER = new NVIDIA_SMI_DRIVER(372, 54); // 372.54;
            static readonly NVIDIA_SMI_DRIVER NVIDIA_MIN_DETECTION_DRIVER = new NVIDIA_SMI_DRIVER(362, 61); // 362.61;
            static NVIDIA_SMI_DRIVER _currentNvidiaSMIDriver = new NVIDIA_SMI_DRIVER(-1, -1);
            static NVIDIA_SMI_DRIVER INVALID_SMI_DRIVER = new NVIDIA_SMI_DRIVER(-1, -1);

            // naming purposes
            private static int CPUCount = 0;
            private static int GPUCount = 0;

            static private NVIDIA_SMI_DRIVER GetNvidiaSMIDriver() {
                if (WindowsDisplayAdapters.HasNvidiaVideoController()) {
                    string stdOut, stdErr, args, smiPath;
                    stdOut = stdErr = args = String.Empty;
                    smiPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe";
                    if (smiPath.Contains(" (x86)")) smiPath = smiPath.Replace(" (x86)", "");
                    try {
                        Process P = new Process();
                        P.StartInfo.FileName = smiPath;
                        P.StartInfo.UseShellExecute = false;
                        P.StartInfo.RedirectStandardOutput = true;
                        P.StartInfo.RedirectStandardError = true;
                        P.StartInfo.CreateNoWindow = true;
                        P.Start();
                        P.WaitForExit();

                        stdOut = P.StandardOutput.ReadToEnd();
                        stdErr = P.StandardError.ReadToEnd();

                        const string FIND_STRING = "Driver Version: ";
                        using (StringReader reader = new StringReader(stdOut)) {
                            string line = string.Empty;
                            do {
                                line = reader.ReadLine();
                                if (line != null) {
                                    if(line.Contains(FIND_STRING)) {
                                        int start = line.IndexOf(FIND_STRING);
                                        string driverVer = line.Substring(start, start + 7);
                                        driverVer = driverVer.Replace(FIND_STRING, "").Substring(0, 7).Trim();
                                        double drVerDouble = Double.Parse(driverVer, CultureInfo.InvariantCulture);
                                        int dot = driverVer.IndexOf(".");
                                        int leftPart = Int32.Parse(driverVer.Substring(0, 3));
                                        int rightPart = Int32.Parse(driverVer.Substring(4, 2));
                                        return new NVIDIA_SMI_DRIVER(leftPart, rightPart);
                                    }
                                }
                            } while (line != null);
                        }

                    } catch (Exception ex) {
                        Helpers.ConsolePrint(TAG, "GetNvidiaSMIDriver Exception: " + ex.Message);
                        return INVALID_SMI_DRIVER;
                    }
                }
                return INVALID_SMI_DRIVER;
            }

            private static void showMessageAndStep(string infoMsg) {
                if (MessageNotifier != null) MessageNotifier.SetMessageAndIncrementStep(infoMsg);
            }

            public static IMessageNotifier MessageNotifier { get; private set; }

            public static void QueryDevices(IMessageNotifier messageNotifier) {
                MessageNotifier = messageNotifier;
                // #0 get video controllers, used for cross checking
                WindowsDisplayAdapters.QueryVideoControllers();
                // Order important CPU Query must be first
                // #1 CPU
                CPU.QueryCPUs();
                // #2 CUDA
                if (NVIDIA.IsSkipNVIDIA()) {
                    Helpers.ConsolePrint(TAG, "Skipping NVIDIA device detection, settings are set to disabled");
                } else {
                    showMessageAndStep(International.GetText("Compute_Device_Query_Manager_CUDA_Query"));
                    NVIDIA.QueryCudaDevices();
                }
                // OpenCL and AMD
                if (ConfigManager.GeneralConfig.DeviceDetection.DisableDetectionAMD) {
                    Helpers.ConsolePrint(TAG, "Skipping AMD device detection, settings set to disabled");
                    showMessageAndStep(International.GetText("Compute_Device_Query_Manager_AMD_Query_Skip"));
                } else {
                    // #3 OpenCL
                    showMessageAndStep(International.GetText("Compute_Device_Query_Manager_OpenCL_Query"));
                    OpenCL.QueryOpenCLDevices();
                    // #4 AMD query AMD from OpenCL devices, get serial and add devices
                    AMD.QueryAMD();
                }
                // #5 uncheck CPU if GPUs present, call it after we Query all devices
                Group.UncheckedCPU();

                // TODO update this to report undetected hardware
                // #6 check NVIDIA, AMD devices count
                int NVIDIA_count = 0;
                {
                    int AMD_count = 0;
                    foreach (var vidCtrl in AvaliableVideoControllers) {
                        if(vidCtrl.Name.ToLower().Contains("nvidia") && CUDA_Unsupported.IsSupported(vidCtrl.Name)) {
                            NVIDIA_count += 1;
                        } else if (vidCtrl.Name.ToLower().Contains("nvidia")) {
                            Helpers.ConsolePrint(TAG, "Device not supported NVIDIA/CUDA device not supported " + vidCtrl.Name);
                        }
                        AMD_count += (vidCtrl.Name.ToLower().Contains("amd")) ? 1 : 0;
                    }
                    if (NVIDIA_count == CudaDevices.Count) {
                        Helpers.ConsolePrint(TAG, "Cuda NVIDIA/CUDA device count GOOD");
                    } else {
                        Helpers.ConsolePrint(TAG, "Cuda NVIDIA/CUDA device count BAD!!!");
                    }
                    if (AMD_count == amdGpus.Count) {
                        Helpers.ConsolePrint(TAG, "AMD GPU device count GOOD");
                    } else {
                        Helpers.ConsolePrint(TAG, "AMD GPU device count BAD!!!");
                    }
                }
                // allerts
                _currentNvidiaSMIDriver = GetNvidiaSMIDriver();
                // if we have nvidia cards but no CUDA devices tell the user to upgrade driver
                bool isNvidiaErrorShown = false; // to prevent showing twice
                bool showWarning = ConfigManager.GeneralConfig.ShowDriverVersionWarning && WindowsDisplayAdapters.HasNvidiaVideoController();
                if (showWarning && CudaDevices.Count != NVIDIA_count && _currentNvidiaSMIDriver.IsLesserVersionThan(NVIDIA_MIN_DETECTION_DRIVER)) {
                    isNvidiaErrorShown = true;
                    var minDriver = NVIDIA_MIN_DETECTION_DRIVER.ToString();
                    var recomendDrvier = NVIDIA_RECOMENDED_DRIVER.ToString();
                    MessageBox.Show(String.Format(International.GetText("Compute_Device_Query_Manager_NVIDIA_Driver_Detection"),
                        minDriver, recomendDrvier),
                                                          International.GetText("Compute_Device_Query_Manager_NVIDIA_RecomendedDriver_Title"),
                                                          MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                // recomended driver
                if (showWarning && _currentNvidiaSMIDriver.IsLesserVersionThan(NVIDIA_RECOMENDED_DRIVER) && !isNvidiaErrorShown && _currentNvidiaSMIDriver.leftPart > -1) {
                    var recomendDrvier = NVIDIA_RECOMENDED_DRIVER.ToString();
                    var nvdriverString = _currentNvidiaSMIDriver.leftPart > -1 ? String.Format(International.GetText("Compute_Device_Query_Manager_NVIDIA_Driver_Recomended_PART"), _currentNvidiaSMIDriver.ToString())
                    : "";
                    MessageBox.Show(String.Format(International.GetText("Compute_Device_Query_Manager_NVIDIA_Driver_Recomended"),
                        recomendDrvier, nvdriverString, recomendDrvier),
                                                          International.GetText("Compute_Device_Query_Manager_NVIDIA_RecomendedDriver_Title"),
                                                          MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // #x remove reference
                MessageNotifier = null;
            }

            #region Helpers
            private class VideoControllerData {
                public string Name { get; set; }
                public string Description { get; set; }
                public string PNPDeviceID { get; set; }
                public string DriverVersion { get; set; }
                public string Status { get; set; }
            }
            private static List<VideoControllerData> AvaliableVideoControllers = new List<VideoControllerData>();
            static class WindowsDisplayAdapters {
                public static void QueryVideoControllers() {
                    StringBuilder stringBuilder = new StringBuilder();
                    stringBuilder.AppendLine("");
                    stringBuilder.AppendLine("QueryVideoControllers: ");
                    ManagementObjectCollection moc = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_VideoController").Get();
                    bool allVideoContollersOK = true;
                    foreach (var manObj in moc) {
                        var vidController = new VideoControllerData() {
                            Name = manObj["Name"] as string,
                            Description = manObj["Description"] as string,
                            PNPDeviceID = manObj["PNPDeviceID"] as string,
                            DriverVersion = manObj["DriverVersion"] as string,
                            Status = manObj["Status"] as string
                        };
                        stringBuilder.AppendLine("\tWin32_VideoController detected:");
                        stringBuilder.AppendLine(String.Format("\t\tName {0}", vidController.Name));
                        stringBuilder.AppendLine(String.Format("\t\tDescription {0}", vidController.Description));
                        stringBuilder.AppendLine(String.Format("\t\tPNPDeviceID {0}", vidController.PNPDeviceID));
                        stringBuilder.AppendLine(String.Format("\t\tDriverVersion {0}", vidController.DriverVersion));
                        stringBuilder.AppendLine(String.Format("\t\tStatus {0}", vidController.Status));

                        // check if controller ok
                        if (allVideoContollersOK && !vidController.Status.ToLower().Equals("ok")) {
                            allVideoContollersOK = false;
                        }

                        AvaliableVideoControllers.Add(vidController);
                    }
                    Helpers.ConsolePrint(TAG, stringBuilder.ToString());
                    if (ConfigManager.GeneralConfig.ShowDriverVersionWarning && !allVideoContollersOK) {
                        string msg = International.GetText("QueryVideoControllers_NOT_ALL_OK_Msg");
                        foreach (var vc in AvaliableVideoControllers) {
                            if (!vc.Status.ToLower().Equals("ok")) {
                                msg += Environment.NewLine
                                    + String.Format(International.GetText("QueryVideoControllers_NOT_ALL_OK_Msg_Append"), vc.Name, vc.Status, vc.PNPDeviceID);
                            }
                        }
                        MessageBox.Show(msg,
                                        International.GetText("QueryVideoControllers_NOT_ALL_OK_Title"),
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                public static bool HasNvidiaVideoController() {
                    foreach (var vctrl in AvaliableVideoControllers) {
                        if (vctrl.Name.ToLower().Contains("nvidia")) return true;
                    }
                    return false;
                }
            }

            static class CPU {
                public static void QueryCPUs() {
                    Helpers.ConsolePrint(TAG, "QueryCPUs START");
                    // get all CPUs
                    Avaliable.CPUsCount = CPUID.GetPhysicalProcessorCount();

                    // get all cores (including virtual - HT can benefit mining)
                    int ThreadsPerCPU = CPUID.GetVirtualCoresCount() / Avaliable.CPUsCount;

                    if (!Helpers.InternalCheckIsWow64()) {
                        MessageBox.Show(International.GetText("Form_Main_msgbox_CPUMining64bitMsg"),
                                        International.GetText("Warning_with_Exclamation"),
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Avaliable.CPUsCount = 0;
                    }

                    if (ThreadsPerCPU * Avaliable.CPUsCount > 64) {
                        MessageBox.Show(International.GetText("Form_Main_msgbox_CPUMining64CoresMsg"),
                                        International.GetText("Warning_with_Exclamation"),
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Avaliable.CPUsCount = 0;
                    }

                    // TODO important move this to settings
                    int ThreadsPerCPUMask = ThreadsPerCPU;
                    Globals.ThreadsPerCPU = ThreadsPerCPU;

                    if (CPUUtils.IsCPUMiningCapable()) {
                        if (Avaliable.CPUsCount == 1) {
                            Avaliable.AllAvaliableDevices.Add(
                                new ComputeDevice(0, "CPU0", CPUID.GetCPUName().Trim(), ThreadsPerCPU, (ulong)0, ++CPUCount)
                            );
                        } else if (Avaliable.CPUsCount > 1) {
                            for (int i = 0; i < Avaliable.CPUsCount; i++) {
                                Avaliable.AllAvaliableDevices.Add(
                                    new ComputeDevice(i, "CPU" + i, CPUID.GetCPUName().Trim(), ThreadsPerCPU, CPUID.CreateAffinityMask(i, ThreadsPerCPUMask), ++CPUCount)
                                );
                            }
                        }
                    }

                    Helpers.ConsolePrint(TAG, "QueryCPUs END");
                }

            }

            static List<CudaDevice> CudaDevices = new List<CudaDevice>();
            static class NVIDIA {
                static string QueryCudaDevicesString = "";
                static private void QueryCudaDevicesOutputErrorDataReceived(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) {
                        QueryCudaDevicesString += e.Data;
                    }
                }

                public static bool IsSkipNVIDIA() {
                    return ConfigManager.GeneralConfig.DeviceDetection.DisableDetectionNVIDIA;
                }

                static public void QueryCudaDevices() {
                    Helpers.ConsolePrint(TAG, "QueryCudaDevices START");
                    Process CudaDevicesDetection = new Process();
                    CudaDevicesDetection.StartInfo.FileName = "CudaDeviceDetection.exe";
                    CudaDevicesDetection.StartInfo.UseShellExecute = false;
                    CudaDevicesDetection.StartInfo.RedirectStandardError = true;
                    CudaDevicesDetection.StartInfo.RedirectStandardOutput = true;
                    CudaDevicesDetection.StartInfo.CreateNoWindow = true;
                    CudaDevicesDetection.OutputDataReceived += QueryCudaDevicesOutputErrorDataReceived;
                    CudaDevicesDetection.ErrorDataReceived += QueryCudaDevicesOutputErrorDataReceived;

                    const int waitTime = 30 * 1000; // 30seconds
                    try {
                        if (!CudaDevicesDetection.Start()) {
                            Helpers.ConsolePrint(TAG, "CudaDevicesDetection process could not start");
                        } else {
                            CudaDevicesDetection.BeginErrorReadLine();
                            CudaDevicesDetection.BeginOutputReadLine();
                            if (CudaDevicesDetection.WaitForExit(waitTime)) {
                                CudaDevicesDetection.Close();
                            }
                        }
                    } catch (Exception ex) {
                        // TODO
                        Helpers.ConsolePrint(TAG, "CudaDevicesDetection threw Exception: " + ex.Message);
                    } finally {
                        if (QueryCudaDevicesString != "") {
                            try {
                                CudaDevices = JsonConvert.DeserializeObject<List<CudaDevice>>(QueryCudaDevicesString, Globals.JsonSettings);
                            } catch { }
                        }
                    }
                    if (CudaDevices != null && CudaDevices.Count != 0) {
                        Avaliable.HasNVIDIA = true;
                        StringBuilder stringBuilder = new StringBuilder();
                        stringBuilder.AppendLine("");
                        stringBuilder.AppendLine("CudaDevicesDetection:");
                        foreach (var cudaDev in CudaDevices) {
                            // check sm vesrions
                            bool isUnderSM21;
                            {
                                bool isUnderSM2_major = cudaDev.SM_major < 2;
                                bool isUnderSM1_minor = cudaDev.SM_minor < 1;
                                isUnderSM21 = isUnderSM2_major && isUnderSM1_minor;
                            }
                            //bool isOverSM6 = cudaDev.SM_major > 6;
                            bool skip = isUnderSM21;
                            string skipOrAdd = skip ? "SKIPED" : "ADDED";
                            string isDisabledGroupStr = ""; // TODO remove
                            string etherumCapableStr = cudaDev.IsEtherumCapable() ? "YES" : "NO";
                            stringBuilder.AppendLine(String.Format("\t{0} device{1}:", skipOrAdd, isDisabledGroupStr));
                            stringBuilder.AppendLine(String.Format("\t\tID: {0}", cudaDev.DeviceID.ToString()));
                            stringBuilder.AppendLine(String.Format("\t\tNAME: {0}", cudaDev.GetName()));
                            stringBuilder.AppendLine(String.Format("\t\tVENDOR: {0}", cudaDev.VendorName));
                            stringBuilder.AppendLine(String.Format("\t\tUUID: {0}", cudaDev.UUID));
                            stringBuilder.AppendLine(String.Format("\t\tSM: {0}", cudaDev.SMVersionString));
                            stringBuilder.AppendLine(String.Format("\t\tMEMORY: {0}", cudaDev.DeviceGlobalMemory.ToString()));
                            stringBuilder.AppendLine(String.Format("\t\tETHEREUM: {0}", etherumCapableStr));

                            if (!skip) {
                                DeviceGroupType group;
                                switch (cudaDev.SM_major) {
                                    case 2:
                                        group = DeviceGroupType.NVIDIA_2_1;
                                        break;
                                    case 3:
                                        group = DeviceGroupType.NVIDIA_3_x;
                                        break;
                                    case 5:
                                        group = DeviceGroupType.NVIDIA_5_x;
                                        break;
                                    case 6:
                                        group = DeviceGroupType.NVIDIA_6_x;
                                        break;
                                    default:
                                        group = DeviceGroupType.NVIDIA_6_x;
                                        break;
                                }
                                Avaliable.AllAvaliableDevices.Add(
                                    new ComputeDevice(cudaDev, group, ++GPUCount)
                                    );
                            }
                        }
                        Helpers.ConsolePrint(TAG, stringBuilder.ToString());
                    } else {
                        Helpers.ConsolePrint(TAG, "CudaDevicesDetection found no devices. CudaDevicesDetection returned: " + QueryCudaDevicesString);
                    }
                    Helpers.ConsolePrint(TAG, "QueryCudaDevices END");
                }
            }

            class OpenCLJSONData_t {
                public string PlatformName = "NONE";
                public int PlatformNum = 0;
                public List<OpenCLDevice> Devices = new List<OpenCLDevice>();
            }
            static List<OpenCLJSONData_t> OpenCLJSONData = new List<OpenCLJSONData_t>();
            static bool IsOpenCLQuerrySuccess = false;
            static class OpenCL {
                static string QueryOpenCLDevicesString = "";
                static private void QueryOpenCLDevicesOutputErrorDataReceived(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) {
                        QueryOpenCLDevicesString += e.Data;
                    }
                }

                static public void QueryOpenCLDevices() {
                    Helpers.ConsolePrint(TAG, "QueryOpenCLDevices START");
                    Process OpenCLDevicesDetection = new Process();
                    OpenCLDevicesDetection.StartInfo.FileName = "AMDOpenCLDeviceDetection.exe";
                    OpenCLDevicesDetection.StartInfo.UseShellExecute = false;
                    OpenCLDevicesDetection.StartInfo.RedirectStandardError = true;
                    OpenCLDevicesDetection.StartInfo.RedirectStandardOutput = true;
                    OpenCLDevicesDetection.StartInfo.CreateNoWindow = true;
                    OpenCLDevicesDetection.OutputDataReceived += QueryOpenCLDevicesOutputErrorDataReceived;
                    OpenCLDevicesDetection.ErrorDataReceived += QueryOpenCLDevicesOutputErrorDataReceived;

                    const int waitTime = 30 * 1000; // 30seconds
                    try {
                        if (!OpenCLDevicesDetection.Start()) {
                            Helpers.ConsolePrint(TAG, "AMDOpenCLDeviceDetection process could not start");
                        } else {
                            OpenCLDevicesDetection.BeginErrorReadLine();
                            OpenCLDevicesDetection.BeginOutputReadLine();
                            if (OpenCLDevicesDetection.WaitForExit(waitTime)) {
                                OpenCLDevicesDetection.Close();
                            }
                        }
                    } catch (Exception ex) {
                        // TODO
                        Helpers.ConsolePrint(TAG, "AMDOpenCLDeviceDetection threw Exception: " + ex.Message);
                    } finally {
                        if (QueryOpenCLDevicesString != "") {
                            try {
                                OpenCLJSONData = JsonConvert.DeserializeObject<List<OpenCLJSONData_t>>(QueryOpenCLDevicesString, Globals.JsonSettings);
                            } catch {
                                OpenCLJSONData = null;
                            }
                        }
                    }

                    if (OpenCLJSONData == null) {
                        Helpers.ConsolePrint(TAG, "AMDOpenCLDeviceDetection found no devices. AMDOpenCLDeviceDetection returned: " + QueryOpenCLDevicesString);
                    } else {
                        IsOpenCLQuerrySuccess = true;
                        StringBuilder stringBuilder = new StringBuilder();
                        stringBuilder.AppendLine("");
                        stringBuilder.AppendLine("AMDOpenCLDeviceDetection found devices success:");
                        foreach (var oclElem in OpenCLJSONData) {
                            stringBuilder.AppendLine(String.Format("\tFound devices for platform: {0}", oclElem.PlatformName));
                            foreach (var oclDev in oclElem.Devices) {
                                stringBuilder.AppendLine("\t\tDevice:");
                                stringBuilder.AppendLine(String.Format("\t\t\tDevice ID {0}", oclDev.DeviceID));
                                stringBuilder.AppendLine(String.Format("\t\t\tDevice NAME {0}", oclDev._CL_DEVICE_NAME));
                                stringBuilder.AppendLine(String.Format("\t\t\tDevice TYPE {0}", oclDev._CL_DEVICE_TYPE));
                            }
                        }
                        Helpers.ConsolePrint(TAG, stringBuilder.ToString());
                    }
                    Helpers.ConsolePrint(TAG, "QueryOpenCLDevices END");
                }
            }

            static List<OpenCLDevice> amdGpus = new List<OpenCLDevice>();
            static class AMD {
                static public void QueryAMD() {
                    const int AMD_VENDOR_ID = 1002;
                    Helpers.ConsolePrint(TAG, "QueryAMD START");

                    #region AMD driver check, ADL returns 0
                    // check the driver version bool EnableOptimizedVersion = true;
                    Dictionary<string, bool> deviceDriverOld = new Dictionary<string, bool>();
                    string minerPath = MinerPaths.sgminer_5_5_0_general;
                    bool ShowWarningDialog = false;

                    foreach (var vidContrllr in AvaliableVideoControllers) {
                        Helpers.ConsolePrint(TAG, String.Format("Checking AMD device (driver): {0} ({1})", vidContrllr.Name, vidContrllr.DriverVersion));

                        deviceDriverOld[vidContrllr.Name] = false;
                        // TODO checking radeon drivers only?
                        if ((vidContrllr.Name.Contains("AMD") || vidContrllr.Name.Contains("Radeon")) && ShowWarningDialog == false) {
                            Version AMDDriverVersion = new Version(vidContrllr.DriverVersion);

                            if (AMDDriverVersion.Major < 15) {
                                ShowWarningDialog = true;
                                deviceDriverOld[vidContrllr.Name] = true;
                                Helpers.ConsolePrint(TAG, "WARNING!!! Old AMD GPU driver detected! All optimized versions disabled, mining " +
                                    "speed will not be optimal. Consider upgrading AMD GPU driver. Recommended AMD GPU driver version is 15.7.1.");
                            } else if (AMDDriverVersion.Major == 16 && AMDDriverVersion.Minor >= 150) {
                                if (MinersDownloadManager.IsMinerBinFolder()) {
                                    // TODO why this copy?
                                    string src = System.IO.Path.GetDirectoryName(Application.ExecutablePath) + "\\" +
                                                 minerPath.Split('\\')[0] + "\\" + minerPath.Split('\\')[1] + "\\kernel";

                                    foreach (var file in Directory.GetFiles(src)) {
                                        string dest = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Temp\\" + System.IO.Path.GetFileName(file);
                                        if (!File.Exists(dest)) File.Copy(file, dest, false);
                                    }
                                }
                            }
                        }
                    }
                    if (ConfigManager.GeneralConfig.ShowDriverVersionWarning && ShowWarningDialog == true) {
                        Form WarningDialog = new DriverVersionConfirmationDialog();
                        WarningDialog.ShowDialog();
                        WarningDialog = null;
                    }
                    #endregion // AMD driver check

                    // get platform version
                    showMessageAndStep(International.GetText("Compute_Device_Query_Manager_AMD_Query"));
                    List<OpenCLDevice> amdOCLDevices = new List<OpenCLDevice>();
                    string AMDOpenCLPlatformStringKey = "";
                    if (IsOpenCLQuerrySuccess) {
                        bool amdPlatformNumFound = false;
                        foreach (var oclEl in OpenCLJSONData) {
                            if (oclEl.PlatformName.Contains("AMD") || oclEl.PlatformName.Contains("amd")) {
                                amdPlatformNumFound = true;
                                AMDOpenCLPlatformStringKey = oclEl.PlatformName;
                                Avaliable.AMDOpenCLPlatformNum = oclEl.PlatformNum;
                                amdOCLDevices = oclEl.Devices;
                                Helpers.ConsolePrint(TAG, String.Format("AMD platform found: Key: {0}, Num: {1}",
                                    AMDOpenCLPlatformStringKey,
                                    Avaliable.AMDOpenCLPlatformNum.ToString()));
                                break;
                            }
                        }
                        if (amdPlatformNumFound) {
                            // get only AMD gpus
                            {
                                foreach (var oclDev in amdOCLDevices) {
                                    if (oclDev._CL_DEVICE_TYPE.Contains("GPU")) {
                                        amdGpus.Add(oclDev);
                                    }
                                }
                            }
                            if (amdGpus.Count == 0) {
                                Helpers.ConsolePrint(TAG, "AMD GPUs count is 0");
                            } else {
                                Helpers.ConsolePrint(TAG, "AMD GPUs count : " + amdGpus.Count.ToString());
                                Helpers.ConsolePrint(TAG, "AMD Getting device name and serial from ADL");
                                // ADL
                                bool isAdlInit = true;
                                // ADL does not get devices in order map devices by bus number
                                // bus id, <name, uuid>
                                Dictionary<int, Tuple<string, string>> _busIdsInfo = new Dictionary<int, Tuple<string, string>>();
                                List<string> _amdDeviceName = new List<string>();
                                List<string> _amdDeviceUUID = new List<string>();
                                try {
                                    int ADLRet = -1;
                                    int NumberOfAdapters = 0;
                                    if (null != ADL.ADL_Main_Control_Create)
                                        // Second parameter is 1: Get only the present adapters
                                        ADLRet = ADL.ADL_Main_Control_Create(ADL.ADL_Main_Memory_Alloc, 1);
                                    if (ADL.ADL_SUCCESS == ADLRet) {
                                        if (null != ADL.ADL_Adapter_NumberOfAdapters_Get) {
                                            ADL.ADL_Adapter_NumberOfAdapters_Get(ref NumberOfAdapters);
                                        }
                                        Helpers.ConsolePrint(TAG, "Number Of Adapters: " + NumberOfAdapters.ToString());

                                        if (0 < NumberOfAdapters) {
                                            // Get OS adpater info from ADL
                                            ADLAdapterInfoArray OSAdapterInfoData;
                                            OSAdapterInfoData = new ADLAdapterInfoArray();

                                            if (null != ADL.ADL_Adapter_AdapterInfo_Get) {
                                                IntPtr AdapterBuffer = IntPtr.Zero;
                                                int size = Marshal.SizeOf(OSAdapterInfoData);
                                                AdapterBuffer = Marshal.AllocCoTaskMem((int)size);
                                                Marshal.StructureToPtr(OSAdapterInfoData, AdapterBuffer, false);

                                                if (null != ADL.ADL_Adapter_AdapterInfo_Get) {
                                                    ADLRet = ADL.ADL_Adapter_AdapterInfo_Get(AdapterBuffer, size);
                                                    if (ADL.ADL_SUCCESS == ADLRet) {
                                                        OSAdapterInfoData = (ADLAdapterInfoArray)Marshal.PtrToStructure(AdapterBuffer, OSAdapterInfoData.GetType());
                                                        int IsActive = 0;

                                                        for (int i = 0; i < NumberOfAdapters; i++) {
                                                            // Check if the adapter is active
                                                            if (null != ADL.ADL_Adapter_Active_Get)
                                                                ADLRet = ADL.ADL_Adapter_Active_Get(OSAdapterInfoData.ADLAdapterInfo[i].AdapterIndex, ref IsActive);

                                                            if (ADL.ADL_SUCCESS == ADLRet) {
                                                                // we are looking for amd
                                                                // TODO check discrete and integrated GPU separation
                                                                var vendorID = OSAdapterInfoData.ADLAdapterInfo[i].VendorID;
                                                                var devName = OSAdapterInfoData.ADLAdapterInfo[i].AdapterName;
                                                                if (vendorID == AMD_VENDOR_ID
                                                                    || devName.ToLower().Contains("amd")
                                                                    || devName.ToLower().Contains("radeon")
                                                                    || devName.ToLower().Contains("firepro")) {

                                                                    string PNPStr = OSAdapterInfoData.ADLAdapterInfo[i].PNPString;
                                                                    var backSlashLast = PNPStr.LastIndexOf('\\');
                                                                    var serial = PNPStr.Substring(backSlashLast, PNPStr.Length - backSlashLast);
                                                                    var end_0 = serial.IndexOf('&');
                                                                    var end_1 = serial.IndexOf('&', end_0 + 1);
                                                                    // get serial
                                                                    serial = serial.Substring(end_0 + 1, (end_1 - end_0) - 1);

                                                                    var udid = OSAdapterInfoData.ADLAdapterInfo[i].UDID;
                                                                    var pciVen_id_strSize = 21; // PCI_VEN_XXXX&DEV_XXXX
                                                                    var uuid = udid.Substring(0, pciVen_id_strSize) + "_" + serial;
                                                                    int budId = OSAdapterInfoData.ADLAdapterInfo[i].BusNumber;
                                                                    if (!_amdDeviceUUID.Contains(uuid)) {
                                                                        try {
                                                                            Helpers.ConsolePrint(TAG, String.Format("ADL device added BusNumber:{0}  NAME:{1}  UUID:{2}"),
                                                                                budId,
                                                                                devName,
                                                                                uuid);
                                                                        } catch { }

                                                                        _amdDeviceUUID.Add(uuid);
                                                                        //_busIds.Add(OSAdapterInfoData.ADLAdapterInfo[i].BusNumber);
                                                                        _amdDeviceName.Add(devName);
                                                                        if (!_busIdsInfo.ContainsKey(budId)) {
                                                                            var nameUuid = new Tuple<string, string>(devName, uuid);
                                                                            _busIdsInfo.Add(budId, nameUuid);
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    } else {
                                                        Helpers.ConsolePrint(TAG, "ADL_Adapter_AdapterInfo_Get() returned error code " + ADLRet.ToString());
                                                    }
                                                }
                                                // Release the memory for the AdapterInfo structure
                                                if (IntPtr.Zero != AdapterBuffer)
                                                    Marshal.FreeCoTaskMem(AdapterBuffer);
                                            }
                                        }
                                        if (null != ADL.ADL_Main_Control_Destroy)
                                            ADL.ADL_Main_Control_Destroy();
                                    } else {
                                        // TODO
                                        Helpers.ConsolePrint(TAG, "ADL_Main_Control_Create() returned error code " + ADLRet.ToString());
                                        Helpers.ConsolePrint(TAG, "Check if ADL is properly installed!");
                                    }
                                } catch (Exception ex) {
                                    Helpers.ConsolePrint(TAG, "AMD ADL exception: " + ex.Message);
                                    isAdlInit = false;
                                }
                                if (isAdlInit) {
                                    if (amdGpus.Count == _amdDeviceUUID.Count) {
                                        Helpers.ConsolePrint(TAG, "AMD OpenCL and ADL AMD query COUNTS GOOD/SAME");
                                    } else {
                                        Helpers.ConsolePrint(TAG, "AMD OpenCL and ADL AMD query COUNTS DIFFERENT/BAD");
                                    }
                                    StringBuilder stringBuilder = new StringBuilder();
                                    stringBuilder.AppendLine("");
                                    stringBuilder.AppendLine("QueryAMD devices: ");
                                    for (int i_id = 0; i_id < amdGpus.Count; ++i_id) {
                                        Avaliable.HasAMD = true;

                                        int busID = amdGpus[i_id].AMD_BUS_ID;
                                        if (busID != -1 && _busIdsInfo.ContainsKey(busID)) {
                                            var deviceName = _busIdsInfo[busID].Item1;
                                            var newAmdDev = new AmdGpuDevice(amdGpus[i_id], deviceDriverOld[deviceName]);
                                            newAmdDev.DeviceName = deviceName;
                                            newAmdDev.UUID = _busIdsInfo[busID].Item2;
                                            bool isDisabledGroup = ConfigManager.GeneralConfig.DeviceDetection.DisableDetectionAMD;
                                            string skipOrAdd = isDisabledGroup ? "SKIPED" : "ADDED";
                                            string isDisabledGroupStr = isDisabledGroup ? " (AMD group disabled)" : "";
                                            string etherumCapableStr = newAmdDev.IsEtherumCapable() ? "YES" : "NO";

                                            Avaliable.AllAvaliableDevices.Add(
                                                new ComputeDevice(newAmdDev, ++GPUCount));
                                            // just in case 
                                            try {
                                                stringBuilder.AppendLine(String.Format("\t{0} device{1}:", skipOrAdd, isDisabledGroupStr));
                                                stringBuilder.AppendLine(String.Format("\t\tID: {0}", newAmdDev.DeviceID.ToString()));
                                                stringBuilder.AppendLine(String.Format("\t\tNAME: {0}", newAmdDev.DeviceName));
                                                stringBuilder.AppendLine(String.Format("\t\tCODE_NAME: {0}", newAmdDev.Codename));
                                                stringBuilder.AppendLine(String.Format("\t\tUUID: {0}", newAmdDev.UUID));
                                                stringBuilder.AppendLine(String.Format("\t\tMEMORY: {0}", newAmdDev.DeviceGlobalMemory.ToString()));
                                                stringBuilder.AppendLine(String.Format("\t\tETHEREUM: {0}", etherumCapableStr));
                                            } catch { }
                                        } else {
                                            stringBuilder.AppendLine(String.Format("\tDevice not added, Bus No. {0} not found:", busID));
                                        }
                                    }
                                    Helpers.ConsolePrint(TAG, stringBuilder.ToString());
                                }
                            }
                        }
                    }
                    Helpers.ConsolePrint(TAG, "QueryAMD END");
                }

            }
            #endregion Helpers
        }

        public static class Avaliable {
            public static bool HasNVIDIA = false;
            public static bool HasAMD = false;
            public static bool HasCPU = false;
            public static int CPUsCount = 0;
            public static int GPUsCount = 0;
            public static int AMDOpenCLPlatformNum = -1;
            
            public static List<ComputeDevice> AllAvaliableDevices = new List<ComputeDevice>();

            // methods
            public static ComputeDevice GetDeviceWithUUID(string uuid) {
                foreach (var dev in AllAvaliableDevices) {
                    if (uuid == dev.UUID) return dev;
                }
                return null;
            }

            public static List<ComputeDevice> GetSameDevicesTypeAsDeviceWithUUID(string uuid) {
                List<ComputeDevice> sameTypes = new List<ComputeDevice>();
                var compareDev = GetDeviceWithUUID(uuid);
                foreach (var dev in AllAvaliableDevices) {
                    if (uuid != dev.UUID && compareDev.DeviceType == dev.DeviceType) {
                        sameTypes.Add(GetDeviceWithUUID(dev.UUID));
                    }
                }
                return sameTypes;
            }

            public static ComputeDevice GetCurrentlySelectedComputeDevice(int index, bool unique) {
                return AllAvaliableDevices[index];
            }
        }

        public static class Group {
            public static void DisableCpuGroup() {
                foreach (var device in Avaliable.AllAvaliableDevices) {
                    if (device.DeviceType == DeviceType.CPU) {
                        device.Enabled = false;
                    }
                }
            }

            public static bool ContainsAMD_GPUs {
                get {
                    foreach (var device in Avaliable.AllAvaliableDevices) {
                        if (device.DeviceType == DeviceType.AMD) {
                            return true;
                        }
                    }
                    return false;
                }
            }

            public static bool ContainsGPUs {
                get {
                    foreach (var device in Avaliable.AllAvaliableDevices) {
                        if (device.DeviceType == DeviceType.NVIDIA
                            || device.DeviceType == DeviceType.AMD) {
                            return true;
                        }
                    }
                    return false;
                }
            }
            public static void UncheckedCPU() {
                // Auto uncheck CPU if any GPU is found
                if (ContainsGPUs) DisableCpuGroup();
            }
        }

    }
}
