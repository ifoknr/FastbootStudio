## purpose
Roots the phone with Magisk, KernelSU or APatch in four steps: take the stock image, patch it on the phone, check it, and flash it to the right partition. The stock image is kept, to undo it.

## steps
1. **The root app and the stock image**: pick Magisk, KernelSU or APatch, then **Choose image or OTA…**: the boot.img or init_boot.img of the firmware the phone runs now, or the whole OTA (the right image is taken out).
2. **Send to phone**: with the phone in Android and USB debugging on, the image goes to its Download folder.
3. **Patch it on the phone** in the root app, as the page explains, then press **Get the patched image**: it is fetched and checked (the same kind of image, root inside, the same kernel).
4. **Restart to bootloader and flash**: type the partition's name; the same phone restarts to the bootloader and the patched image is written to the active slot. **Try it once first** starts a patched boot image without writing it.
5. Restart the phone and open the root app to finish its setup.

## danger
- The bootloader must be **unlocked**, and unlocking wipes the phone.
- The image must come from the **exact firmware the phone runs now**. An image from another version can keep the phone from starting (a bootloop).
- If the phone does not start after flashing, flash the stock image from the work folder to the same partition (Device → Partitions → Flash), or use the firmware's own.
- Root can break banking apps and Google Wallet, and an update over the air removes it.

## tips
- Phones launched with Android 13 or later root through **init_boot**; older ones through **boot**. APatch always patches boot.
- The work folder (Documents\Fastboot Studio\Root\<date>) keeps the stock and the patched image; **Open folder** shows it.
- Only the phone the image was sent to is flashed, never another phone left in fastboot.

## terms
Magisk | Root that patches the ramdisk (boot or init_boot)
KernelSU | Root inside the kernel (or as a module, LKM, through init_boot)
APatch | Root that patches the kernel in boot; needs a super key
Stock image | The image exactly as the firmware ships it
Bootloop | The phone restarts again and again without reaching Android
