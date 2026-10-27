## purpose
The phone while it is in fastboot (the bootloader or fastbootd): its state, its partitions, quick restarts, and flashing or erasing one partition. Open a phone here first; the Flash page writes to the phone picked here.

## steps
1. Put the phone in fastboot: from Android, **Restart to bootloader** on the Phone page, or the phone's key combination (often Power + Volume down).
2. Double-click the phone in **Connected devices**. If it is not listed, press **USB check**: it tells a missing driver, a download mode fastboot cannot reach, or a phone still in Android.
3. **Basic Properties** shows the phone's variables, the **Pre-flash checks** (bootloader unlocked, mode, pending update, slot) and the **Quick actions**: restarts, the other slot, and **Boot image once…**.
4. **Partitions** lists every partition. To flash one: filter by name, select one row, press **Flash** and pick the image. **Erase**, **Create**, **Resize** and **Delete** sit beside it.
5. **Disconnect** goes back to the list.

## danger
- Flashing or erasing the boot chain, the modem, persist, efs, nvram or similar can stop the phone from starting, or lose its IMEI and keys for good. The app asks you to type the partition's name first.
- **Erase** and **Delete** have no undo. Back up on the Backup page first.
- Switching to a slot that is empty or has never started can leave the phone unable to boot: the app refuses an empty slot and warns about the others.
- Flash only images made for this exact phone and its software version.

## tips
- **Create**, **Resize**, **Delete** and writing logical partitions (system, vendor, product…) need **fastbootd**; **Reboot to Fastbootd** gets there.
- vbmeta asks whether to disable verification; that is needed for custom or patched system images.
- A boot, init_boot, vendor_boot or recovery image is checked before it is written: the wrong kind, a kernel for another phone, an older security patch, root, or a recovery that could be tried with **Boot image once…** first.
- **Boot image once…** starts a boot or recovery image without writing it; restarting returns to what is installed. Some phones (Samsung and a few others) cannot do it.

## terms
Bootloader | The first program on the phone; its fastboot mode writes partitions
fastbootd | Fastboot inside recovery; it can write logical partitions in super
Slot A/B | Two copies of the system; the phone starts from the active one
super | One big partition holding the logical ones (system, vendor…)
vbmeta | Verified boot data; it checks the other partitions
Virtual A/B | Updates kept as snapshots until the next start finishes them
KMI | The kernel's interface version; a kernel built for another one does not start
