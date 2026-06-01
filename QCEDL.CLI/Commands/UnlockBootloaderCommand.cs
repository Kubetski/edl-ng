using System.CommandLine;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose.Xml.Elements;

namespace QCEDL.CLI.Commands;

internal sealed class UnlockBootloaderCommand
{
    private static readonly Option<string> PartitionOption = new(
        aliases: ["--partition", "-p"],
        description: "Partition name to unlock via devinfo edit.",
        getDefaultValue: () => "devinfo"
    );

    private static readonly Option<uint> LunOption = new(
        aliases: ["--lun", "-l"],
        description: "LUN number to scan (default: auto-detect across LUNs 0-5).",
        getDefaultValue: () => 99
    );

    private static readonly Option<bool> ForceOption = new(
        aliases: ["--force", "-f"],
        description: "Force unlock even if bootloader appears already unlocked."
    );

    private static readonly Option<bool> RelockOption = new(
        aliases: ["--relock"],
        description: "Relock bootloader instead of unlocking."
    );

    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var command = new Command("unlock-bootloader", "Unlock or relock bootloader via devinfo partition edit over EDL.")
        {
            PartitionOption,
            LunOption,
            ForceOption,
            RelockOption
        };

        command.SetHandler(ExecuteAsync,
            globalOptionsBinder,
            PartitionOption,
            LunOption,
            ForceOption,
            RelockOption);

        return command;
    }

    private static async Task<int> ExecuteAsync(
        GlobalOptionsBinder globalOptions,
        string partitionName,
        uint specifiedLun,
        bool forceUnlock,
        bool relock)
    {
        Logging.Log("=== Bootloader Unlock via EDL ===");
        Logging.Log("Method: DevInfo partition hex-edit");

        return await CommandExecutor.RunAsync("unlock-bootloader", async () =>
        {
            using var manager = new EdlManager(globalOptions);
            await manager.EnsureFirehoseModeAsync();
            if (!manager.IsFirehoseMode) { Logging.Log("Not in Firehose mode.", LogLevel.Error); return 1; }

            if (!manager.IsDirectMode)
                await manager.ConfigureFirehoseAsync();

            // Search for devinfo partition across LUNs
            var lunsToSearch = specifiedLun <= 5
                ? new List<uint> { specifiedLun }
                : Enumerable.Range(0, 6).Select(i => (uint)i).ToList();

            (GptPartition partition, uint lun)? found = null;
            foreach (var lun in lunsToSearch)
            {
                Logging.Log($"Searching LUN {lun} for '{partitionName}'...");
                found = await manager.FindPartitionWithLunAsync(partitionName, lun);
                if (found.HasValue) { Logging.Log($"Found '{partitionName}' on LUN {found.Value.lun}"); break; }
            }

            if (!found.HasValue)
            {
                Logging.Log($"Partition '{partitionName}' not found on any LUN (0-5).", LogLevel.Error);
                Logging.Log("Note: This device may not have a devinfo partition, or may use a different unlock method.");
                Logging.Log("Alternative: Use 'oem-unlock' command for Sahara-based OEM unlock.");
                return 1;
            }

            var (part, lunIdx) = found.Value;
            var sectorSize = manager.GetSectorSize(lunIdx);
            var startLba = part.FirstLba;
            var sectorCount = (uint)(part.LastLba - part.FirstLba + 1);

            Logging.Log($"Reading {partitionName} (LUN {lunIdx}, sectors {startLba}-{startLba + sectorCount - 1}, sector size {sectorSize})...");

            var data = await manager.ReadSectorsAsync(lunIdx, startLba, sectorCount);

            if (data == null || data.Length == 0)
            {
                Logging.Log($"Failed to read {partitionName}.", LogLevel.Error);
                return 1;
            }

            Logging.Log($"Read {data.Length} bytes from {partitionName}.");

            // Display devinfo hex dump (first 64 bytes)
            Logging.Log("Raw devinfo header (first 64 bytes):");
            for (var i = 0; i < Math.Min(64, data.Length); i += 16)
            {
                var hex = BitConverter.ToString(data, i, Math.Min(16, data.Length - i)).Replace('-', ' ');
                var ascii = "";
                for (var j = i; j < Math.Min(i + 16, data.Length); j++)
                    ascii += data[j] >= 0x20 && data[j] <= 0x7E ? (char)data[j] : '.';
                Logging.Log($"  {i:X8}  {hex,-47}  {ascii}");
            }

            // Check current unlock state based on known patterns
            // Pattern from lowendmains/edlunlock:
            //   Offset 0x10: 01 00 00 00 = unlocked, 00 00 00 00 = locked
            var byte10 = data.Length > 0x10 ? data[0x10] : 0;
            var byte08 = data.Length > 0x08 ? data[0x08] : 0;
            var isUnlocked = byte10 == 0x01;

            if (isUnlocked && !forceUnlock)
            {
                Logging.Log("Bootloader appears already unlocked (byte at offset 0x10 = 0x01).");
                Logging.Log("Use --force to re-apply unlock pattern.");
                return 0;
            }

            if (relock)
            {
                Logging.Log("=== RELOCKING bootloader ===");
                // Set unlock bytes to locked state
                for (var i = 0x10; i < 0x18 && i < data.Length; i++)
                    data[i] = 0x00;
                data[0x08] = 0x00;
                // Clear LOCK / UNLOCK string patterns
                var lockStr = "LOCK"u8;
                var unlockStr = "UNLOCK"u8;
                for (var i = 0; i < data.Length - 4; i++)
                {
                    if (data.AsSpan(i, 4).SequenceEqual(lockStr) ||
                        data.AsSpan(i, 4).SequenceEqual(unlockStr))
                    {
                        Array.Clear(data, i, 4);
                        Logging.Log($"Cleared pattern at offset 0x{i:X}");
                    }
                }
                Logging.Log("Bootloader relock pattern applied.");
            }
            else
            {
                Logging.Log("=== UNLOCKING bootloader ===");
                // Apply unlock pattern
                data[0x10] = 0x01;
                data[0x11] = 0x00;
                data[0x12] = 0x00;
                data[0x13] = 0x00;
                data[0x14] = 0x01;
                data[0x15] = 0x00;
                data[0x16] = 0x00;
                data[0x17] = 0x00;
                // Also set byte at 0x08 (secondary unlock pattern)
                data[0x08] = 0x01;
                Logging.Log("Unlock pattern applied (offset 0x10-0x17, 0x08 → 0x01).");
            }

            // Write back patched devinfo
            Logging.Log($"Writing {data.Length} bytes back to {partitionName}...");
            using var ms = new MemoryStream(data);
            await manager.WriteSectorsFromStreamAsync(lunIdx, startLba, ms, data.Length,
                padToSector: true, $"unlock-bootloader-{partitionName}.bin");

            Logging.Log($"Bootloader {(relock ? "re" : "")}unlock complete!");
            Logging.Log("Reboot device to apply changes.");
            Logging.Log("If bootloader is still locked, try: edl-ng oem-unlock");

            return 0;
        });
    }
}
