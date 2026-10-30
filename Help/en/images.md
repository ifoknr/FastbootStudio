## purpose
Reads and converts Android images on this computer: sparse and raw images, super images, boot images, and firmware split into pieces. It never touches a phone.

## steps
1. Press **Open image…** or drop the file. Split parts (.img_sparsechunk.0, .1…) are found together.
2. **Details** tells what the image is: its format, contents, sizes, and for a boot image its kernel, KMI, security patch and root.
3. Pick an action for the image: **Convert to raw (simg2img)**, **Convert to sparse (img2simg)**, **Split into parts (simg2simg)**, or for super **Extract partitions (lpunpack)** after ticking them in **Partitions in super**.
4. The result goes to the image's own folder unless you press **Change…**; **Open folder** shows it.

## tips
- A sparse image is smaller and is what fastboot sends; raw is the partition byte for byte, which some tools need.
- Splitting helps phones with a small max-download-size.
- Checksums inside the images (metadata, CRC-32) are verified; a broken output is deleted.

## terms
Sparse | A compact image format that skips empty space
Raw | The partition exactly as it is on the phone
super | One image holding the logical partitions (system, vendor…)
lpunpack | Takes the partitions out of a super image
KMI | The kernel's interface version
