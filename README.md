# edl-ng

**A modern, user-friendly tool for interacting with Qualcomm devices in Emergency Download (EDL) mode.**

Built with .NET, `edl-ng` provides tools for both Sahara and Firehose protocols, enabling device flashing, partition management, bootloader unlocking, Knox Guard removal, and low-level device interaction.

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
* **KG Clear:** Multi-method Knox Guard removal via partition patching, token zeroing, and bootloader unlock.
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

### Bootloader, KG & Security Commands
| Command | Description |
|---------|-------------|
| `unlock-bootloader` | Unlock/relock bootloader via devinfo partition edit |
| `oem-unlock` | Attempt OEM unlock enable via Sahara/Firehose |
| `kg scan` | Scan all LUNs for KG-related partitions |
| `kg read <partition>` | Read and display KG state from a partition |
| `kg-clear` | Clear/remove Samsung KG lock via multiple EDL methods |

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

## Samsung Knox Guard (KG) Bypass Guide

### Understanding KG States

KG state is stored in the bootloader and read from RPMB (TrustZone) during boot. Displayed in Download Mode:

| State | Value | Meaning |
|-------|-------|---------|
| **Prenormal** | 0 | Factory state; carrier can still activate a remote lock |
| **Checking** | 1 | Device has been online; carrier is verifying KG status |
| **Completed** / **Active** | 1 (alt flag) | Normal operational state |
| **Locked** | 2 | KG lock is active; device is restricted |
| **BROKEN** / **Error** | 3 | Tampered state from partition edits — device needs bypass |

### Method 1: Automated KG Clear (Recommended)

Uses all available EDL techniques to remove/bypass KG lock:

```bash
# Run all methods (param patch + persist zero + devinfo unlock):
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg-clear

# Specific method only:
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg-clear --method param-patch --state 3

# Multi-pass (Chimera-style, run 3 times with reboots):
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg-clear --method multi-pass
```

**What kg-clear does:**
1. **Param partition patch** — Searches for `kg_state=`, `knox_guard`, `locked` strings in the param partition and modifies them. Sets KG state to PASS (3).
2. **Persist/Persdata zeroing** — Finds KG-related tokens in persist/persdata partitions and nulls them out.
3. **DevInfo unlock** — Applies bootloader unlock via devinfo partition edit.
4. **Multi-pass mode** — Chimera-style 3-pass approach: apply patches, reboot to unlock bootloader, reconnect EDL and re-apply.

### Method 2: DevInfo Partition Unlock

**Works on:** Most Qualcomm devices with devinfo partition (pre-2018 MSM89xx, some newer).
**Risk:** Low.

```bash
edl-ng --loader prog_firehose_ddr.elf --memory UFS unlock-bootloader
```

**How it works:** The `devinfo` partition stores bootloader unlock state at offset 0x10 (0x01 = unlocked, 0x00 = locked). The command reads, applies unlock pattern, and writes back.

### Method 3: OEM Unlock via EDL

```bash
edl-ng --loader prog_firehose_ddr.elf --memory UFS oem-unlock
```

Patches the devinfo partition to enable the OEM Unlock toggle in Developer Options.

### Method 4: Param Partition Manual Edit

```bash
# 1. Scan for KG partitions
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg scan

# 2. Read KG state
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg read param

# 3. Read and edit param locally
edl-ng --loader prog_firehose_ddr.elf --memory UFS read-part param param.bin

# 4. Hex-edit param.bin (search for kg_state, knox_guard, locked strings)

# 5. Write back
edl-ng --loader prog_firehose_ddr.elf --memory UFS write-part param param.bin
```

### Method 5: RPMB Clear (Paid Tools Method)

RPMB (Replay Protected Memory Block) stores KG tokens at the hardware level in eMMC/UFS. This is where the authoritative KG state lives on modern Samsung devices. Clearing RPMB removes KG completely.

**How RPMB works:**
- Protected by a 256-bit HMAC-SHA256 authentication key fused into the SoC.
- Requires TrustZone SMC (Secure Monitor Call) or authenticated Firehose commands to write.
- Stores: anti-rollback counters, Knox security keys, bootloader unlock state.
- On UFS devices, the RPMB write counter can only be incremented, never reset.

**Paid tools that can clear RPMB:**
| Tool | Price | Method |
|------|-------|--------|
| Chimera Tool | ~$120/yr | Signed Samsung loader + multi-pass EDL |
| TSM Tool Pro | ~$80/yr | EDL + combination firmware QR |
| SamsungTool | ~$100/yr | EDL + KG Removal All FIXED |
| Octoplus Samsung | ~$90/yr | Partition Manager via EDL |
| F64 / Medusa / UFI | Varies | Physical ISP direct UFS access |

**Free alternatives (without RPMB clear):**
- The `kg-clear` command (Method 1) combined with KnoxPatch (Magisk module) bypasses KG at the OS level without clearing RPMB.
- The VBmeta rename exploit (Method 6) can bypass RPMB write protection on some SoCs.

### Method 6: VBmeta Rename Exploit (Advanced)

**Works on:** Qualcomm devices with ABL (Android Bootloader) verified boot.
**Risk:** HIGH — can brick if done incorrectly.

Exploits Qualcomm ABL protocol: rename `vbmeta` so ABL takes NO_AVB path, Keymaster TA no longer blocks RPMB writes, allowing `is_unlocked=1` and `is_unlock_critical=1` to be written.

```bash
# Reference implementation:
# github.com/atlas4381/qualcomm_avb_exploit_poc

# The kg-clear --method vbmeta-exploit option outputs step-by-step instructions
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg-clear --method vbmeta-exploit
```

**Steps:**
1. Read GPT, find vbmeta partition
2. Rename vbmeta -> vbmeta_bak via GPT write
3. Reboot to bootloader (ABL takes NO_AVB path)
4. Write `is_unlocked=1` to RPMB DeviceInfo
5. Restore vbmeta name in GPT

### Method 7: Chimera-Style Multi-Pass EDL

**Works on:** All Samsung Qualcomm devices.
**Risk:** Moderate.

ChimeraTool uses a documented 3-pass EDL procedure:
1. **Pass 1**: Connect EDL, run KG removal procedure (fails — bootloader locked). Cancel, unlock bootloader via Download Mode.
2. **Pass 2**: Reconnect EDL, run KG removal again. Rebooting to system.
3. **Pass 3**: Reconnect EDL, run KG removal a third time. Finishes successfully.

```bash
# Our implementation:
edl-ng --loader prog_firehose_ddr.elf --memory UFS kg-clear --method multi-pass
```

Run the command 2-3 times, rebooting the device between each pass.

### Method 8: ADB-Based KG Neutralization (Root Required)

If the device can boot with ADB:

```bash
# Disable KG client service
adb shell pm disable-user --user 0 com.samsung.android.kgclient
adb shell pm uninstall --user 0 com.samsung.android.kgclient

# Install KnoxPatch Magisk module for permanent bypass
# github.com/KnoxPatch/KnoxPatch
```

Reference: github.com/yinkev/Fold4-KG-Unlock

## KG State Map

### Partitions That Store KG Data

| Partition | LUN (Typical) | Role |
|-----------|--------------|------|
| `param` | LUN 2 | KG state string, boot flags |
| `persist` | LUN 2 | Persistent KG enrollment tokens |
| `persdata` | LUN 2 | Secondary KG configuration |
| `devinfo` | LUN 0 | Bootloader unlock state |
| `RPMB` | HW-protected | Authoritative KG state, ARB version |

### UFS LUN Layout (Typical Samsung Qualcomm)
- **LUN 0**: GPT + XBL, ABL, TZ, devinfo (boot chain)
- **LUN 1**: modem, fsg
- **LUN 2**: param, persist, persdata (KG-relevant)
- **LUN 4**: metadata, frp
- **LUN 5**: userdata

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

* **.NET 8 SDK**.
* **Qualcomm USB Drivers:**
  * **Windows:** Both Qualcomm USB Driver (QUD) and WinUSB driver (Zadig) are supported.
  * **Linux/macOS:** `libusb` is used. May need udev rules for user access.
* **Firehose Programmer:** An appropriate `.elf` file for your specific device (e.g., `prog_firehose_*.elf` or `xbl_s_devprg_ns.melf`).

## Building

```bash
dotnet build QCEDL.CLI\QCEDL.CLI.csproj
```

The executable `edl-ng` will be in `QCEDL.CLI/bin/<Configuration>/net8.0/`.

## Resources & References

- [Aleph Security: Exploiting Qualcomm EDL Programmers](https://alephsecurity.com/2018/01/22/qualcomm-edl-1/) — Foundational EDL/Firehose/Sahara research
- [Giovix92/EDLUnlock](https://github.com/Giovix92/EDLUnlock) — Batch-based devinfo unlock (MSM8953)
- [lowendmains/edlunlock](https://github.com/lowendmains/edlunlock) — Shell-based devinfo unlock
- [bkerler/edl](https://github.com/bkerler/edl) — Python EDL tool
- [gus33000/QCEDL.NET](https://github.com/gus33000/QCEDL.NET) — Original .NET EDL implementation
- [atlas4381/qualcomm_avb_exploit_poc](https://github.com/atlas4381/qualcomm_avb_exploit_poc) — RPMB DeviceInfo write via vbmeta rename
- [yinkev/Fold4-KG-Unlock](https://github.com/yinkev/Fold4-KG-Unlock) — ADB-based kgclient neutralizer
- [KnoxPatch](https://github.com/KnoxPatch/KnoxPatch) — Magisk module for permanent KG bypass
- [Alephgsm/SAMSUNG-EDL-Loaders](https://github.com/Alephgsm/SAMSUNG-EDL-Loaders) — Samsung EDL firehose loader collection

## License

MIT license.
