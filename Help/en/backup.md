## purpose
Saves the phone's partitions and any of its files to this computer, over adb. Take a backup before any flash: it is how the phone goes back to how it was.

## steps
1. The phone must be in Android with USB debugging on, or in a custom recovery (TWRP, OrangeFox) with adb. In fastboot, the page offers to start Android, the recovery, or a recovery image once.
2. **Partitions**: press **Read partition table** (it needs root: allow it on the phone), then **Select critical**, or tick what you want, and press **Back up selected**.
3. Every image is saved with its SHA-256 in a SHA256SUMS file. **Check a backup…** checks a backup folder again later.
4. **Files**: browse the phone (Internal storage, Camera, Download…), tick files or folders and press **Copy to computer**.
5. **Open folder** shows where everything went.

## danger
- A backup is only useful from **the same phone**: never flash one phone's modem, persist, efs or nvram images on another, or it loses its IMEI and network for good.
- Keep the backup folder somewhere safe; it holds your phone's unique data.

## tips
- Partitions need root (Magisk, KernelSU or APatch) in Android, or a custom recovery; files do not.
- **Select critical** picks the small partitions that cannot be downloaded again (modem data, persist, efs, nvram…), up to 1 GiB each.
- Partitions go to Documents\Fastboot Studio\Backups\<model>_<serial>_<date>, files to Documents\Fastboot Studio\Files. **Change…** moves both.
- To restore a partition, flash its image from Device → Partitions.

## terms
adb | The tool that talks to the phone in Android or recovery
Root (su) | Full access on the phone, needed to read partitions
Custom recovery | A recovery like TWRP or OrangeFox, with adb and root built in
SHA256SUMS | The file with every image's fingerprint, to prove nothing changed
