using System.CommandLine;
using System.Text;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;

namespace QCEDL.CLI.Commands;

internal sealed class OemUnlockCommand
{
    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var command = new Command("oem-unlock", "Enable OEM unlock toggle via EDL partition patch.");
        command.SetHandler(ExecuteAsync, globalOptionsBinder);
        return command;
    }

    private static async Task<int> ExecuteAsync(GlobalOptionsBinder globalOptions)
    {
        Logging.Log("=== OEM Unlock Enable via EDL ===");
        Logging.Log("This patches the devinfo partition to enable the OEM Unlock toggle.");

        return await CommandExecutor.RunAsync("oem-unlock", async () =>
        {
            using var manager = new EdlManager(globalOptions);
            await manager.EnsureFirehoseModeAsync();
            if (manager.IsDirectMode)
            {
                Logging.Log("Direct host mode not supported for OEM unlock.", LogLevel.Error);
                return 1;
            }
            await manager.ConfigureFirehoseAsync();

            // Find devinfo partition across all LUNs
            (GptPartition partition, uint lun)? found = null;
            for (uint lun = 0; lun <= 5; lun++)
            {
                found = await manager.FindPartitionWithLunAsync("devinfo", lun);
                if (found.HasValue) { Logging.Log($"Found devinfo on LUN {found.Value.lun}"); break; }
            }

            if (!found.HasValue)
            {
                Logging.Log("devinfo partition not found. Trying 'persist' as fallback...", LogLevel.Warning);
                for (uint lun = 0; lun <= 5; lun++)
                {
                    found = await manager.FindPartitionWithLunAsync("persist", lun);
                    if (found.HasValue) { Logging.Log($"Found persist on LUN {found.Value.lun}"); break; }
                }
            }

            if (!found.HasValue)
            {
                Logging.Log("No suitable partition found for OEM unlock.", LogLevel.Error);
                Logging.Log("Try: edl-ng kg scan  (to list available partitions)");
                Logging.Log("Then: edl-ng unlock-bootloader --partition <name>");
                return 1;
            }

            var (part, lunIdx) = found.Value;
            var sectorSize = manager.GetSectorSize(lunIdx);
            var sectorCount = (uint)(part.LastLba - part.FirstLba + 1);

            Logging.Log($"Reading {sectorCount} sectors from {part.GetName()} on LUN {lunIdx}...");
            var data = await manager.ReadSectorsAsync(lunIdx, part.FirstLba, sectorCount);

            if (data == null || data.Length < 0x20)
            {
                Logging.Log("Partition too small or unreadable.", LogLevel.Error);
                return 1;
            }

            // Show current state
            Logging.Log($"Current byte at offset 0x10: 0x{data[0x10]:X2}");
            Logging.Log($"Current byte at offset 0x08: 0x{data[0x08]:X2}");

            // Apply unlock pattern
            data[0x10] = 0x01;
            for (int i = 0x11; i < 0x18 && i < data.Length; i++) data[i] = 0x00;
            data[0x08] = 0x01;

            Logging.Log("Unlock pattern applied. Writing back...");
            using var ms = new MemoryStream(data);
            await manager.WriteSectorsFromStreamAsync(lunIdx, part.FirstLba, ms, data.Length,
                padToSector: true, "oem_unlock.bin");

            Logging.Log("OEM unlock partition patched successfully!");
            Logging.Log("");
            Logging.Log("Next steps:");
            Logging.Log("1. Reboot device (edl-ng reset)");
            Logging.Log("2. Check Settings → Developer Options → OEM Unlock toggle");
            Logging.Log("3. If visible, enable it");
            Logging.Log("4. Reboot to Download Mode (adb reboot download)");
            Logging.Log("5. Long press Vol Up to confirm bootloader unlock");
            Logging.Log("");
            Logging.Log("If OEM Unlock toggle is still missing:");
            Logging.Log("- Device may need KG neutralization first (kg scan + kg read)");
            Logging.Log("- Try combination firmware (see README)");

            return 0;
        });
    }
}
