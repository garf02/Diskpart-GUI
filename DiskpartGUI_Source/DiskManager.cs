using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DiskpartGUI
{
    public class DiskItem
    {
        public int Index { get; set; }
        public string Model { get; set; } = "";
        public long SizeBytes { get; set; }
        public double SizeGB { get; set; }
        public bool IsSystem { get; set; }

        public string SizeDisplay => SizeGB.ToString("F1") + " GB";
        public override string ToString() => $"Disk {Index}: {Model} ({SizeDisplay}) {(IsSystem ? "[SYSTEM]" : "")}";
    }

    public class DiskManager
    {
        public event Action<string>? OnOutputReceived;

        public List<DiskItem> GetDisks()
        {
            var disks = new List<DiskItem>();
            try
            {
                int systemDiskIndex = GetSystemDiskIndex();
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                foreach (ManagementObject drive in searcher.Get())
                {
                    int index = Convert.ToInt32(drive["Index"]);
                    long sizeBytes = Convert.ToInt64(drive["Size"]);
                    disks.Add(new DiskItem
                    {
                        Index = index,
                        Model = drive["Model"]?.ToString() ?? "Unknown",
                        SizeBytes = sizeBytes,
                        SizeGB = sizeBytes / (1024.0 * 1024.0 * 1024.0),
                        IsSystem = (index == systemDiskIndex)
                    });
                }
            }
            catch (Exception ex)
            {
                OnOutputReceived?.Invoke("Error listing disks: " + ex.Message);
            }
            return disks.OrderBy(d => d.Index).ToList();
        }

        public int GetSystemDiskIndex()
        {
            try
            {
                string systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";
                systemDrive = systemDrive.TrimEnd('\\');

                using var partitionSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{systemDrive}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                foreach (ManagementObject partition in partitionSearcher.Get())
                {
                    using var driveSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                    foreach (ManagementObject drive in driveSearcher.Get())
                    {
                        return Convert.ToInt32(drive["Index"]);
                    }
                }
            }
            catch { }
            return -1;
        }

        public bool RunDiskpart(string script, string label)
        {
            OnOutputReceived?.Invoke($"--- {label} ---");
            string scriptPath = Path.Combine(Path.GetTempPath(), "dp_script.txt");
            File.WriteAllText(scriptPath, script);

            Process process = new Process();
            process.StartInfo.FileName = "diskpart.exe";
            process.StartInfo.Arguments = $"/s \"{scriptPath}\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            process.OutputDataReceived += (s, e) => { if (e.Data != null) OnOutputReceived?.Invoke(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) OnOutputReceived?.Invoke("ERROR: " + e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            bool success = process.ExitCode == 0;
            OnOutputReceived?.Invoke($"--- {label} {(success ? "SUCCESS" : "FAILED")} ---");
            return success;
        }

        public List<char> GetDiskDriveLetters(int diskIndex)
        {
            var letters = new List<char>();
            try
            {
                using var partitionSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{diskIndex}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject partition in partitionSearcher.Get())
                {
                    using var logicalSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                    foreach (ManagementObject logical in logicalSearcher.Get())
                    {
                        string driveLetter = logical["DeviceID"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(driveLetter))
                        {
                            letters.Add(char.ToUpper(driveLetter[0]));
                        }
                    }
                }
            }
            catch { }
            return letters;
        }

        public char? GetAvailableLetter(char preferred = ' ', List<char>? exemptLetters = null)
        {
            try
            {
                var usedLetters = DriveInfo.GetDrives().Select(d => char.ToUpper(d.Name[0])).ToList();
                
                // If preferred letter is in exempt list (it belongs to the current disk), consider it free!
                if (preferred != ' ' && exemptLetters != null && exemptLetters.Contains(char.ToUpper(preferred)))
                    return char.ToUpper(preferred);

                if (preferred != ' ' && !usedLetters.Contains(char.ToUpper(preferred))) 
                    return char.ToUpper(preferred);

                for (char c = 'C'; c <= 'Z'; c++)
                {
                    if (!usedLetters.Contains(c)) return c;
                }
            }
            catch { }
            return null;
        }

        public string BuildFormatScript(int diskIndex, bool isGpt, int bootSize, int windowsSizeGb, bool createRecovery, int recoverySizeMb, double totalDiskSizeGB)
        {
            var sw = new StringWriter();
            sw.WriteLine($"select disk {diskIndex}");
            sw.WriteLine("clean");

            double usedMb = bootSize + (windowsSizeGb * 1024.0) + (createRecovery ? recoverySizeMb : 0);
            double totalMb = totalDiskSizeGB * 1024.0;
            bool spaceForData = (totalMb - usedMb) >= 1024; // Only create DATA if at least 1GB left

            if (isGpt)
            {
                sw.WriteLine("convert gpt");
                
                // EFI System Partition (Manual creation to bypass auto-MSR in Live Windows)
                sw.WriteLine($"create partition primary size={bootSize}");
                sw.WriteLine("format quick fs=fat32 label=\"EFI_BOOT\"");
                sw.WriteLine("set id=\"c12a7328-f81f-11d2-ba4b-00a0c93ec93b\" override");
                sw.WriteLine("gpt attributes=0x8000000000000001");
                
                // OS
                if (!spaceForData)
                {
                    // If no space for data, use the rest of the disk for OS
                    sw.WriteLine("create partition primary");
                }
                else
                {
                    sw.WriteLine($"create partition primary size={windowsSizeGb * 1024}");
                }
                sw.WriteLine("format quick fs=ntfs label=\"Windows\"");
                
                var currentDiskLetters = GetDiskDriveLetters(diskIndex);
                char? winLetter = GetAvailableLetter('C', currentDiskLetters);
                if (winLetter != null) sw.WriteLine($"assign letter={winLetter}");
                else sw.WriteLine("assign");

                if (createRecovery)
                {
                    sw.WriteLine($"create partition primary size={recoverySizeMb}");
                    sw.WriteLine("format quick fs=ntfs label=\"Recovery\"");
                    sw.WriteLine("set id=\"de94bba4-06d1-4d40-a16a-bfd50179d6ac\" override");
                    sw.WriteLine("gpt attributes=0x8000000000000001");
                }

                // DATA Partition (Remaining space)
                if (spaceForData)
                {
                    sw.WriteLine("create partition primary");
                    sw.WriteLine("format quick fs=ntfs label=\"DATA\"");
                    sw.WriteLine("assign"); 
                }
            }
            else
            {
                sw.WriteLine("convert mbr");
                // System Reserved
                sw.WriteLine($"create partition primary size={bootSize}");
                sw.WriteLine("format quick fs=ntfs label=\"MBR_BOOT\"");
                sw.WriteLine("active");
 
                // OS
                if (!spaceForData)
                {
                    // If no space for data, use the rest of the disk for OS
                    sw.WriteLine("create partition primary");
                }
                else
                {
                    sw.WriteLine($"create partition primary size={windowsSizeGb * 1024}");
                }
                sw.WriteLine("format quick fs=ntfs label=\"Windows\"");
                
                var currentDiskLettersMbr = GetDiskDriveLetters(diskIndex);
                char? winLetterMbr = GetAvailableLetter('C', currentDiskLettersMbr);
                if (winLetterMbr != null) sw.WriteLine($"assign letter={winLetterMbr}");
                else sw.WriteLine("assign");
 
                if (createRecovery)
                {
                    sw.WriteLine($"create partition primary size={recoverySizeMb}");
                    sw.WriteLine("format quick fs=ntfs label=\"Recovery\"");
                    sw.WriteLine("set id=27 override");
                }
 
                // DATA Partition (Remaining space)
                if (spaceForData)
                {
                    sw.WriteLine("create partition primary");
                    sw.WriteLine("format quick fs=ntfs label=\"DATA\"");
                    sw.WriteLine("assign");
                }
            }

            sw.WriteLine("rescan");
            return sw.ToString();
        }

        public string BuildVhdScript(string vhdPath, long sizeMb, bool isFixed, bool isVhdx)
        {
            // Dynamic (expandable) is preferred as per user request. 
            string type = isFixed ? "fixed" : "expandable";
            return $"create vdisk file=\"{vhdPath}\" maximum={sizeMb} type={type}\nattach vdisk";
        }
    }
}
