using System.CommandLine;
using System.Text;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;
using QCEDL.NET.PartitionTable;

namespace QCEDL.CLI.Commands;

internal sealed class KgClearCommand
{
    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var methodOption = new Option<string>(
            aliases: ["--method", "-m"],
            description: "KG clear method to use. Options: all, param-patch, persist-zero, devinfo-unlock, multi-pass, vbmeta-exploit",
            getDefaultValue: () => "all"
        );

        var kgStateOption = new Option<uint>(
            aliases: ["--state", "-s"],
            description: "Target KG state to set (0=prenormal, 1=checking, 2=locked, 3=pass).",
            getDefaultValue: () => 3
        );

        var command = new Command("kg-clear", "Clear/remove Samsung Knox Guard lock via multiple EDL methods.")
        {
            methodOption,
            kgStateOption
        };

        command.SetHandler(ExecuteAsync, globalOptionsBinder, methodOption, kgStateOption);
        return command;
    }

    private static async Task<int> ExecuteAsync(
        GlobalOptionsBinder globalOptions,
        string method,
        uint targetState)
    {
        Logging.Log("=== Samsung KG Lock Clear / Bypass ===");
        Logging.Log($"Method: {method}, Target KG state: {targetState}");
        Logging.Log("WARNING: Modifying partitions can brick your device if done incorrectly.");
        Logging.Log("");

        var kgState = targetState > 3 ? (byte)3 : (byte)targetState;

        return await CommandExecutor.RunAsync("kg-clear", async () =>
        {
            using var manager = new EdlManager(globalOptions);
            await manager.EnsureFirehoseModeAsync();
            if (!manager.IsFirehoseMode)
            {
                Logging.Log("Not in Firehose mode.", LogLevel.Error);
                return 1;
            }
            if (!manager.IsDirectMode)
            {
                await manager.ConfigureFirehoseAsync();
            }

            var useAll = method.Equals("all", StringComparison.OrdinalIgnoreCase);

            // Method 1: Param partition KG state patch
            if (useAll || method.Equals("param-patch", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyParamKgPatch(manager, kgState);
            }

            // Method 2: Persist/Persdata zeroing for KG tokens
            if (useAll || method.Equals("persist-zero", StringComparison.OrdinalIgnoreCase))
            {
                await ZeroPersistPartitions(manager);
            }

            // Method 3: Combined devinfo unlock
            if (useAll || method.Equals("devinfo-unlock", StringComparison.OrdinalIgnoreCase))
            {
                await DevInfoUnlockBootloader(manager);
            }

            // Method 4: Multi-pass EDL (Chimera-style)
            if (method.Equals("multi-pass", StringComparison.OrdinalIgnoreCase))
            {
                Logging.Log("Multi-pass EDL method (Chimera-style):");
                Logging.Log("  Pass 1: Apply all KG patches (this session)");
                Logging.Log("  Pass 2: Reconnect EDL and re-apply after bootloader unlock");
                Logging.Log("  Pass 3: Final pass to neutralize KG service on next boot");
                await ApplyParamKgPatch(manager, kgState);
                await ZeroPersistPartitions(manager);
                Logging.Log("Multi-pass: Run this command 2-3 times, rebooting to system between each.");
            }

            // Method 5: vbmeta rename exploit documentation
            if (method.Equals("vbmeta-exploit", StringComparison.OrdinalIgnoreCase))
            {
                Logging.Log("VBmeta rename exploit for RPMB KG state clear:");
                Logging.Log("  This method exploits Qualcomm ABL Verified Boot protocol.");
                Logging.Log("  Steps:");
                Logging.Log("  1. Read GPT to find vbmeta partition");
                Logging.Log("  2. Rename vbmeta -> vbmeta_bak via writing GPT");
                Logging.Log("  3. Reboot to bootloader (ABL takes NO_AVB path)");
                Logging.Log("  4. Keymaster TA no longer blocks RPMB writes");
                Logging.Log("  5. Write is_unlocked=1 and is_unlock_critical=1 to RPMB");
                Logging.Log("  6. Restore vbmeta name in GPT");
                Logging.Log("  7. KG state is now bypassed at TrustZone level");
                Logging.Log("NOTE: vbmeta rename is HIGH RISK. Only attempt on test devices.");
                Logging.Log("Reference: github.com/atlas4381/qualcomm_avb_exploit_poc");
            }

            Logging.Log("");
            Logging.Log("=== KG Clear Summary ===");
            Logging.Log("1. Reboot device and check KG state in Download Mode");
            Logging.Log("2. If still KG locked, try combination firmware + QR method");
            Logging.Log("3. For persistent removal, RPMB clear via signed loader is needed");
            Logging.Log("4. After clearing, unlock bootloader: edl-ng unlock-bootloader");
            Logging.Log("5. Root with Magisk, install KnoxPatch module for permanent bypass");

            return 0;
        });
    }

    private static async Task ApplyParamKgPatch(EdlManager manager, byte kgState)
    {
        Logging.Log("--- Method 1: Param Partition KG State Patch ---");

        // Try to find param partition across LUNs
        var paramNames = new[] { "param", "param_a", "param_b" };
        (GptPartition partition, uint lun)? found = null;

        foreach (var name in paramNames)
        {
            if (found.HasValue) break;
            for (uint lun = 0; lun <= 5; lun++)
            {
                found = await manager.FindPartitionWithLunAsync(name, lun);
                if (found.HasValue)
                {
                    Logging.Log($"Found '{name}' on LUN {found.Value.lun}");
                    break;
                }
            }
        }

        if (!found.HasValue)
        {
            Logging.Log("No param partition found. Skipping param patch.", LogLevel.Warning);
            return;
        }

        var (paramPart, paramLun) = found.Value;
        var sectorSize = manager.GetSectorSize(paramLun);
        var sectorCount = (uint)Math.Min(64, paramPart.LastLBA - paramPart.FirstLBA + 1);

        Logging.Log($"Reading {sectorCount} sectors from param (LUN {paramLun})...");
        var data = await manager.ReadSectorsAsync(paramLun, paramPart.FirstLBA, sectorCount);

        if (data == null || data.Length < 256)
        {
            Logging.Log("Failed to read param partition or too small.", LogLevel.Warning);
            return;
        }

        // Strategy 1: Search for kg_state string in param and modify value
        var text = Encoding.ASCII.GetString(data);
        var kgStateIdx = text.IndexOf("kg_state", StringComparison.OrdinalIgnoreCase);
        var foundPatterns = false;

        if (kgStateIdx >= 0)
        {
            foundPatterns = true;
            // Format: "kg_state=X" where X is a digit
            // Look for pattern like kg_state=0, kg_state=1, etc.
            for (var p = kgStateIdx; p < Math.Min(kgStateIdx + 20, data.Length); p++)
            {
                if (data[p] == (byte)'=')
                {
                    var oldVal = (p + 1 < data.Length) ? (char)data[p + 1] : '?';
                    Logging.Log($"Found kg_state at offset 0x{p - 8:X}, current value: {oldVal}");
                    data[p + 1] = (byte)('0' + kgState);
                    Logging.Log($"Set kg_state to {kgState} at offset 0x{p + 1:X}");
                    break;
                }
            }
        }

        // Strategy 2: Search for knox_guard or knox strings
        var knoxGuardIdx = text.IndexOf("knox_guard", StringComparison.OrdinalIgnoreCase);
        if (knoxGuardIdx >= 0)
        {
            foundPatterns = true;
            Logging.Log($"Found knox_guard at offset 0x{knoxGuardIdx:X}");

            // Try to null out knox_guard entries
            var end = text.IndexOf('\0', knoxGuardIdx);
            if (end < 0) end = Math.Min(knoxGuardIdx + 64, data.Length);
            for (var p = knoxGuardIdx; p < end; p++)
            {
                data[p] = 0;
            }
            Logging.Log($"Nulled knox_guard data from 0x{knoxGuardIdx:X} to 0x{end:X}");
        }

        // Strategy 3: Search for locked state indicators
        var lockedIdx = text.IndexOf("locked", StringComparison.OrdinalIgnoreCase);
        if (lockedIdx >= 0)
        {
            foundPatterns = true;
            Logging.Log($"Found 'locked' at offset 0x{lockedIdx:X}");
            // Replace 'locked' with 'unlocked' or null
            var lockBytes = Encoding.UTF8.GetBytes("locked");
            var unlockBytes = Encoding.UTF8.GetBytes("unlock");
            for (var p = lockedIdx; p < Math.Min(lockedIdx + 8, data.Length); p++)
            {
                data[p] = 0;
            }
            Logging.Log($"Nulled 'locked' string at 0x{lockedIdx:X}");
        }

        // Strategy 4: Known offset patterns for specific SoCs
        // Exynos 2200+ stores KG state at specific offsets in param header
        // This is a best-effort scan for common patterns
        var knownPatterns = new[] {
            new byte[] { 0x6B, 0x67, 0x5F, 0x73, 0x74, 0x61, 0x74, 0x65 }, // "kg_state"
            new byte[] { 0x4B, 0x47, 0x5F, 0x53, 0x54, 0x41, 0x54, 0x45 }, // "KG_STATE"
            new byte[] { 0x6B, 0x6E, 0x6F, 0x78 },                          // "knox"
            new byte[] { 0x4B, 0x4E, 0x4F, 0x58 },                          // "KNOX"
        };

        foreach (var pattern in knownPatterns)
        {
            for (var p = 0; p < data.Length - pattern.Length; p++)
            {
                var match = true;
                for (var k = 0; k < pattern.Length; k++)
                {
                    if (data[p + k] != pattern[k])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    // Found pattern, try to modify surrounding state value
                    Logging.Log($"Found known KG pattern at offset 0x{p:X}");
                }
            }
        }

        if (!foundPatterns)
        {
            Logging.Log("No KG patterns found in param partition first sector(s).", LogLevel.Warning);
            Logging.Log("This device may store KG state in RPMB (read RPMB exploit method).");
            return;
        }

        // Write modified param back
        Logging.Log("Writing modified param partition...");
        using var ms = new MemoryStream(data);
        await manager.WriteSectorsFromStreamAsync(paramLun, paramPart.FirstLBA, ms, data.Length,
            padToSector: true, "param_kg_patched.bin");

        Logging.Log("Param partition patched for KG state change.");
    }

    private static async Task ZeroPersistPartitions(EdlManager manager)
    {
        Logging.Log("--- Method 2: Persist/Persdata Token Zeroing ---");

        var persistNames = new[] { "persist", "persdata", "persistent" };

        foreach (var pname in persistNames)
        {
            for (uint lun = 0; lun <= 5; lun++)
            {
                var part = await manager.FindPartitionWithLunAsync(pname, lun);
                if (!part.HasValue) continue;

                var (partition, lunIdx) = part.Value;
                var sectorSize = manager.GetSectorSize(lunIdx);
                var sectorCount = (uint)Math.Min(16, partition.LastLBA - partition.FirstLBA + 1);

                Logging.Log($"Reading {pname} on LUN {lunIdx} ({sectorCount} sectors)...");
                var data = await manager.ReadSectorsAsync(lunIdx, partition.FirstLBA, sectorCount);

                if (data == null || data.Length < 64)
                {
                    Logging.Log($"  Failed to read {pname}.", LogLevel.Warning);
                    continue;
                }

                // Check for KG tokens in first sectors
                var text = Encoding.ASCII.GetString(data);
                var hasKg = text.Contains("kg_state", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("knox", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("provisioned", StringComparison.OrdinalIgnoreCase);

                if (!hasKg)
                {
                    Logging.Log($"  No KG tokens in {pname} first sectors. Skipping.");
                    continue;
                }

                // Zero out data areas that contain KG tokens
                var modified = false;
                var patterns = new[] { "kg_state", "knox", "provisioned", "frp" };
                foreach (var pat in patterns)
                {
                    var idx = 0;
                    while ((idx = text.IndexOf(pat, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        var start = Math.Max(0, idx - 4);
                        var end = Math.Min(data.Length, idx + pat.Length + 16);
                        for (var p = start; p < end; p++)
                        {
                            data[p] = 0;
                        }
                        Logging.Log($"  Zeroed '{pat}' at offset 0x{idx:X} in {pname}");
                        modified = true;
                        idx = end;
                    }
                }

                if (modified)
                {
                    Logging.Log($"  Writing modified {pname} back...");
                    using var ms2 = new MemoryStream(data);
                    await manager.WriteSectorsFromStreamAsync(lunIdx, partition.FirstLBA, ms2, data.Length,
                        padToSector: true, $"{pname}_zeroed.bin");
                    Logging.Log($"  {pname} KG tokens cleared.");
                }
            }
        }
    }

    private static async Task DevInfoUnlockBootloader(EdlManager manager)
    {
        Logging.Log("--- Method 3: DevInfo Bootloader Unlock ---");

        for (uint lun = 0; lun <= 5; lun++)
        {
            var part = await manager.FindPartitionWithLunAsync("devinfo", lun);
            if (!part.HasValue) continue;

            var (partition, lunIdx) = part.Value;
            var sectorCount = (uint)(partition.LastLBA - partition.FirstLBA + 1);

            Logging.Log($"Reading devinfo on LUN {lunIdx} ({sectorCount} sectors)...");
            var data = await manager.ReadSectorsAsync(lunIdx, partition.FirstLBA, sectorCount);

            if (data == null || data.Length < 0x20)
            {
                Logging.Log("  devinfo too small or unreadable.", LogLevel.Warning);
                continue;
            }

            Logging.Log($"  Current byte at 0x10: 0x{data[0x10]:X2}");
            Logging.Log($"  Current byte at 0x08: 0x{data[0x08]:X2}");

            if (data[0x10] == 0x01 && data[0x08] == 0x01)
            {
                Logging.Log("  Bootloader appears already unlocked.");
                return;
            }

            // Apply unlock pattern
            data[0x10] = 0x01;
            for (var i = 0x11; i < 0x18 && i < data.Length; i++) data[i] = 0x00;
            data[0x14] = 0x01;
            data[0x08] = 0x01;

            Logging.Log("  Writing unlock pattern to devinfo...");
            using var ms = new MemoryStream(data);
            await manager.WriteSectorsFromStreamAsync(lunIdx, partition.FirstLBA, ms, data.Length,
                padToSector: true, "devinfo_unlock_kg_clear.bin");
            Logging.Log("  DevInfo unlock applied for KG clear operation.");
            return;
        }

        Logging.Log("  devinfo partition not found.", LogLevel.Warning);
    }
}
