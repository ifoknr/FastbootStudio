## purpose
Flashes a whole OTA (an OTA zip or a payload.bin) to the phone opened on the Device page. Every image is extracted first, in parallel, and checked against its SHA-256; nothing is written unless all of them are good.

## steps
1. Open the phone on the **Device** page. In **fastbootd** every partition can be written; the bootloader cannot write logical ones.
2. Back up first on the Backup page.
3. Press **Flash Payload.bin** and pick the OTA zip or payload.bin made for this phone.
4. Answer the checks: partitions the phone lacks (created empty in fastbootd), a payload without SHA-256, and room in super.
5. Watch the queue: Extracting, Verified, Flashing, Flashed. Partitions fastbootd refuses (a OnePlus modem, for one) are written from the bootloader at the end.
6. When it says done, restart the phone.

## danger
- This replaces the phone's whole software. A flash cut half way (cable, battery, closing the app) can leave the phone unable to start.
- An OTA for another model or region can stop the phone from starting, or break calls and mobile data.
- Only full OTAs can be flashed; an incremental one is refused.
- **Flash both slots** writes the firmware partitions to slot a and slot b; use it only when you mean both slots to carry this firmware.
- **Ignore Unknown Partitions** skips partitions the phone does not have; use it only when you know they are not needed.

## tips
- Images are written to the active slot.
- Nothing is kept on disk: each image is deleted once written.
- To flash one partition, use Device → Partitions → Flash.

## terms
OTA | A full system update package (a zip with payload.bin inside)
payload.bin | The file in an OTA that holds every partition image
Full / incremental | A full OTA holds whole images; an incremental one only the changes from one version
SHA-256 | A fingerprint each image is checked against before it is written
Active slot | The slot the phone starts from
