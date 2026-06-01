# edl-ng

**A modern, user-friendly tool for interacting with Qualcomm devices in Emergency Download (EDL) mode.**

Built with .NET, `edl-ng` provides tools for both Sahara and Firehose protocols, enabling device flashing, partition management, bootloader unlocking, and low-level device interaction.

## Features

* **Cross-Platform:** Designed to run on Windows, Linux, and macOS with a single executable.
* **Sahara Protocol Support:**
  * Upload Firehose programmers (`.elf` files).
  * Device information retrieval (Serial Number, HWID, RKH).
* **Firehose Protocol Support:**
  * Automatic Firehose configuration.
  * **GPT Management:** Print GUID Partition Table.
  * **Partition Operations:**
    * Read partition to a file.
    * Write file to a partition.
    * Automatic LUN scanning to find partitions.
  * **Sector Operations:**
    * Read raw sectors to a file.
    * Write file to raw sectors.
  * **Device Control:** Reset or power off the device.
  * Get detailed storage information (sector size, LUN count).
* **Bootloader Unlock:** Unlock bootloader via devinfo partition edit over EDL.
* **OEM Unlock:** Enable OEM unlock toggle via EDL partition patch.
* **KG Scanner:** Scan LUNs for Samsung Knox Guard related partitions.
* **Flexible Device Detection:**
  * Specify USB VID/PID.
  * Uses COM ports on Windows or LibUsbDotNet (for all platforms, especially Linux/macOS).
* **Configurable:**
  * Specify memory type (UFS, eMMC/SD, NVMe, SPINOR etc.).
  * Set maximum payload size for Firehose.
  * Adjust logging levels.

## Commands

### Standard EDL Commands
| Command | Description |
|---------|-------------|
| `upload-loader` | Upload Firehose programmer via Sahara |
| `printgpt` | Print GPT from device |
| `read-part <name> <file>` | Read partition to file |
| `read-sector <start> <count> <file>` | Read sectors to file |
| `read-lun <file>` | Read entire LUN to file |
| `dump-rawprogram <dir>` | Dump all partitions + generate rawprogram XML |
| `write-part <name> <file>` | Write file to partition |
| `write-sector <start> <file>` | Write file to sectors |
| `erase-part <name>` | Erase partition |
| `erase-sector <start> <count>` | Erase sectors |
| `provision <xmlfile>` | UFS provisioning |
| `rawprogram <patterns>` | Flash using rawprogram XML files |
| `reset` | Reset/power off device |

### Bootloader & Security Commands (New)
| Command | Description |
|---------|-------------|
| `unlock-bootloader` | Unlock/relock bootloader via devinfo partition edit |
| `oem-unlock` | Attempt OEM unlock enable via Sahara/Firehose |
| `kg scan` | Scan all LUNs for KG-related partitions |
| `kg read <partition>` | Read and display KG state from a partition |

## Usage

```bash
edl-ng [global-options] <command> [command-options-and-arguments]
```

### Global Options
| Option | Description |
|--------|-------------|
| `--loader, -l` | Path to Firehose programmer .elf |
| `--vid` | USB Vendor ID (hex, e.g. 0x05C6) |
| `--pid` | USB Product ID (hex, e.g. 0x9008) |
| `--memory` | Storage type (UFS, Sdcc, Spinor, Nand, Nvme) |
| `--loglevel` | Logging level (Trace, Debug, Info, Warning, Error) |
| `--maxpayload` | Max payload size for Firehose |
| `--slot` | Slot number (0 or 1) |
| `--hostdev-as-target` | Treat host device as target |
| `--img-size` | Image size for host device mode |
| `--radxa-wos-platform` | Radxa WoS backend (Windows only) |

## Bootloader Unlock Guide

### Method 1: DevInfo Partition Unlock (Recommended)

**Works on:** Most Qualcomm devices with devinfo partition (pre-2018 MSM89xx, some newer).
**Risk:** Low — only modifies the devinfo partition.

```bash
# 1. Put device in EDL mode (9008)
# 2. Upload loader and unlock bootloader:
edl-ng --loader prog_firehose_ddr.elf --memory UFS unlock-bootloader

# 3. Verify: Check Developer Options for OEM Unlock toggle
# 4. Reboot to Download Mode and confirm unlock:
adb reboot download
# Long press Vol Up at the unlock prompt
```

**How it works:**
The `devinfo` partition stores bootloader unlock state at specific offsets:
- **Offset 0x10:** `01 00 00 00 00 00 00 00` = unlocked (byte 0x10 = 0x01)
- **Offset 0x10:** `00 00 00 00 00 00 00 00` = locked (byte 0x10 = 0x00)
- **Offset 0x08:** Set to `0x01` as secondary unlock flag

The command reads the devinfo partition, applies the unlock pattern, and writes it back.

**If unlock-bootloader says already unlocked but OEM toggle is missing:**
```bash
edl-ng --loader prog_firehose_ddr.elf --memory UFS unlock-bootloader --force
```

**To relock:**
```bash
edl-ng --loader prog_firehose_ddr.elf --memory UFS unlock-bootloader --relock
```

### Method 2: OEM Unlock via EDL (Experimental)

**Works on:** Some Samsung/OnePlus devices.
**Risk:** Moderate — may trigger EDL auth errors on newer SoCs.

```bash
edl-ng --loader prog_firehose_ddr.elf --memory UFS oem-unlock
```

This attempts to enable the OEM Unlock toggle by:
1. Patching the devinfo partition (same as Method 1)
2. Attempting Sahara EXECUTE command for OEM unlock (on compatible Sahara v2 devices)

### Method 3: KG Neutralization via Partition Edit (Samsung)

**Works on:** Samsung devices with KG lock (states 0-3).
**Risk:** Moderate — writing to wrong partition can brick.

#### Step 1: Scan for KG partitions
```bash
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg scan
```

#### Step 2: Read KG state from a specific partition
```bash
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg read persist
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg read param
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg read devinfo
```

#### Step 3: Manually edit KG state (advanced)
```bash
# Read param partition
edl-ng --loader prog_firehose_ddr.elf --memory UFS read-part param param.bin

# Hex-edit param.bin: change "kg_state=2" to "kg_state=3" (BROKEN)
# Or null out the knox_guard entries

# Write back
edl-ng --loader prog_firehose_ddr.elf --memory UFS write-part param param.bin
```

### Method 4: RPMB Clear via Signed Loader (Paid Tools Only)

**Works on:** All Samsung devices (hardware-level fix).
**Risk:** Low if using genuine tool.

RPMB (Replay Protected Memory Block) stores KG tokens at the hardware level. Clearing RPMB removes KG completely, but requires a signed Samsung loader which is only available through paid tools:
- Chimera Tool (~$120/yr)
- TSM Tool Pro (~$80/yr)
- SamsungTool (~$100/yr)
- Octoplus Samsung (~$90/yr)

**Free alternative:** The devinfo unlock method (Method 1) combined with KnoxPatch (root module) achieves the same result without clearing RPMB.

## Putting Device in EDL Mode

| Device | Method |
|--------|--------|
| Most Qualcomm | `adb reboot edl` (requires USB debugging) |
| Samsung (older) | Vol Up + Vol Down + USB cable |
| Samsung (newer) | `adb reboot edl` or EDL test points |
| Xiaomi | `fastboot oem edl` or `fastboot reboot-edl` |
| OnePlus | `adb reboot edl` or Vol Up + Vol Down + Power |
| LG | Vol Up + USB cable |
| Test points | Short EDL test points on motherboard |

Verify: Device Manager shows "Qualcomm HS-USB QDLoader 9008" or "QUSB_BULK".

## Prerequisites

* **.NET 8/9 SDK** (no need to install .NET runtime if using pre-built binaries).
* **Qualcomm USB Drivers:**
  * **Windows:** Both Qualcomm® USB Driver (QUD) and WinUSB driver (Zadig) are supported.
  * **Linux/macOS:** `libusb` is used. You may also need to configure udev rules on Linux to allow user access to the device.
* **Firehose Programmer:** An appropriate `.elf` programmer file for your specific device (e.g., `prog_firehose_*.elf` or `xbl_s_devprg_ns.melf`).

## Building

1. Clone the repository.
2. Ensure you have the .NET 8 SDK installed.
3. Build:

```bash
dotnet build QCEDL.CLI\QCEDL.CLI.csproj
```

4. The executable `edl-ng` will be located in `QCEDL.CLI/bin/<Configuration>/net8.0/`.

## Resources & References

- [Aleph Security: Exploiting Qualcomm EDL Programmers](https://alephsecurity.com/2018/01/22/qualcomm-edl-1/) — Foundational research on EDL/Firehose/Sahara
- [Giovix92/EDLUnlock](https://github.com/Giovix92/EDLUnlock) — Batch-based devinfo unlock (MSM8953)
- [lowendmains/edlunlock](https://github.com/lowendmains/edlunlock) — Shell-based devinfo unlock
- [bkerler/edl](https://github.com/bkerler/edl) — Python EDL tool (inspiration)
- [gus33000/QCEDL.NET](https://github.com/gus33000/QCEDL.NET) — Original .NET EDL implementation

## License

This project is licensed under the MIT license.
