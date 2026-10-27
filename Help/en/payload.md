## purpose
Opens an OTA zip or a payload.bin on this computer to look inside and take out partition images (boot, init_boot, vendor_boot, system…). It never touches a phone.

## steps
1. Drop the OTA zip or payload.bin on the page, or press **Browse**.
2. **Basic Properties** shows the package: full or incremental, security patch, timestamp and how it is compressed.
3. In **Partitions**, select the images you want (Ctrl or Shift for several) and press **Extract Image**.
4. Pick a folder: each image is saved there as <name>.img and checked against its SHA-256.
5. **Dynamic Partition Metadata** shows the groups and partitions that live in super.

## tips
- To root, extract **init_boot** (or **boot** on older phones) and patch it, or let the Root page do all of it.
- **Allow incremental** and **Ignore unknown operations** give images that may be incomplete; **Ignore checksum** skips the SHA-256 check. Leave them off unless you know why.
- The images go wherever you choose; nothing is written to the phone.

## terms
payload.bin | The file in an OTA that holds every partition image
Incremental | An OTA with only the changes from one version; its images cannot be rebuilt alone
Manifest | The list inside the payload: partitions, sizes and fingerprints
init_boot | The partition holding the ramdisk on phones launched with Android 13 or later; root patches it
