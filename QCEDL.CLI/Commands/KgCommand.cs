using System.CommandLine;
using System.Text;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose.Xml.Elements;

namespace QCEDL.CLI.Commands;

internal sealed class KgCommand
{
    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var scanCommand = new Command("kg-scan", "Scan all LUNs for KG-related partitions (persist, param, devinfo, keystorage, etc.)");
        scanCommand.SetHandler(ExecuteScanAsync, globalOptionsBinder);

        var readCommand = new Command("kg-read", "Read and display KG state from a specific partition");
        readCommand.AddArgument(new Argument<string>("partition", "Partition name (e.g., persist, param, devinfo)"));
        readCommand.SetHandler(ExecuteReadAsync, globalOptionsBinder,
            new Argument<string>("partition"));

        var command = new Command("kg", "Knox Guard related operations for Samsung devices.");
        command.AddCommand(scanCommand);
        command.AddCommand(readCommand);

        return command;
    }

    private static readonly string[] KgPartitionNames =
    [
        "persist", "persdata", "persistent", "param",
        "keystorage", "devinfo", "sec", "secbin",
        "misc", "metadata", "config", "frp"
    ];

    private static async Task<int> ExecuteScanAsync(GlobalOptionsBinder globalOptions)
    {
        Logging.Log("=== Samsung KG Partition Scanner ===");
        Logging.Log("Scanning LUNs 0-5 for KG-related partitions...");

        return await CommandExecutor.RunAsync("kg-scan", async () =>
        {
            using var manager = new EdlManager(globalOptions);
            await manager.EnsureFirehoseModeAsync();
            if (!manager.IsDirectMode)
                await manager.ConfigureFirehoseAsync();

            var foundAny = false;

            for (uint lun = 0; lun <= 5; lun++)
            {
                foreach (var kgName in KgPartitionNames)
                {
                    var part = await manager.FindPartitionAsync(kgName, lun);
                    if (part.HasValue)
                    {
                        foundAny = true;
                        var sizeBytes = (part.Value.LastLba - part.Value.FirstLba + 1) * manager.GetSectorSize(lun);
                        
                        Logging.Log($"  [{kgName}] LUN {lun} | sectors {part.Value.FirstLba}-{part.Value.LastLba} | {sizeBytes:N0} bytes");

                        // Read first sector to check for KG data
                        var sectorData = await manager.ReadSectorsAsync(lun, part.Value.FirstLba, 1);
                        if (sectorData != null && sectorData.Length > 0)
                        {
                            var text = Encoding.ASCII.GetString(sectorData);
                            var hasKgData = text.Contains("kg_state", StringComparison.OrdinalIgnoreCase) ||
                                            text.Contains("knox_guard", StringComparison.OrdinalIgnoreCase) ||
                                            text.Contains("knox", StringComparison.OrdinalIgnoreCase);
                            
                            if (hasKgData)
                                Logging.Log($"    ⚠ KG DATA DETECTED in first sector!");
                        }
                    }
                }
            }

            if (!foundAny)
            {
                Logging.Log("No KG-related partitions found.", LogLevel.Warning);
                Logging.Log("This device may not have Samsung Knox Guard, or may use different partition names.");
            }

            return 0;
        });
    }

    private static async Task<int> ExecuteReadAsync(GlobalOptionsBinder globalOptions, string partitionName)
    {
        Logging.Log($"=== Reading KG state from partition: {partitionName} ===");

        return await CommandExecutor.RunAsync("kg-read", async () =>
        {
            using var manager = new EdlManager(globalOptions);
            await manager.EnsureFirehoseModeAsync();
            if (!manager.IsDirectMode)
                await manager.ConfigureFirehoseAsync();

            (GptPartition part, uint lun)? found = null;
            for (uint lun = 0; lun <= 5; lun++)
            {
                found = await manager.FindPartitionWithLunAsync(partitionName, lun);
                if (found.HasValue) { Logging.Log($"Found '{partitionName}' on LUN {found.Value.lun}"); break; }
            }

            if (!found.HasValue)
            {
                Logging.Log($"Partition '{partitionName}' not found.", LogLevel.Error);
                return 1;
            }

            var (partition, lunIdx) = found.Value;
            var sectorSize = manager.GetSectorSize(lunIdx);
            var sectorCount = (uint)Math.Min(8, partition.LastLba - partition.FirstLba + 1);

            Logging.Log($"Reading {sectorCount} sectors from {partitionName}...");
            var data = await manager.ReadSectorsAsync(lunIdx, partition.FirstLba, sectorCount);

            if (data == null || data.Length == 0)
            {
                Logging.Log("Failed to read partition data.", LogLevel.Error);
                return 1;
            }

            Logging.Log($"Raw hex dump ({data.Length} bytes):");
            for (var i = 0; i < data.Length; i += 16)
            {
                var hex = BitConverter.ToString(data, i, Math.Min(16, data.Length - i)).Replace('-', ' ');
                var ascii = "";
                for (var j = i; j < Math.Min(i + 16, data.Length); j++)
                    ascii += data[j] >= 0x20 && data[j] <= 0x7E ? (char)data[j] : '.';
                Logging.Log($"  {i:X8}  {hex,-47}  {ascii}");
            }

            // Search for KG state patterns
            var text = Encoding.ASCII.GetString(data);
            var patterns = new Dictionary<string, string>
            {
                ["kg_state"] = "Knox Guard state setting",
                ["knox_guard"] = "Knox Guard configuration",
                ["knox"] = "Generic Knox reference",
                ["oem_unlock"] = "OEM unlock setting",
                ["device_provisioned"] = "Device provisioned flag",
                ["bootloader"] = "Bootloader state",
                ["unlock"] = "Unlock related"
            };

            Logging.Log("");
            Logging.Log("String pattern search:");
            var foundPatterns = false;
            foreach (var (pattern, description) in patterns)
            {
                var idx = 0;
                while ((idx = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    foundPatterns = true;
                    var context = text.Substring(Math.Max(0, idx - 8), Math.Min(pattern.Length + 16, text.Length - Math.Max(0, idx - 8)));
                    Logging.Log($"  [{description}] Found '{pattern}' at offset 0x{idx:X}: ...{context}...");
                    idx += pattern.Length;
                }
            }

            if (!foundPatterns)
                Logging.Log("  No KG-related patterns found in scanned sectors.");

            return 0;
        });
    }
}
