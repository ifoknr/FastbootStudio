## purpose
Opens Samsung firmware (the AP, BL, CP, CSC and HOME_CSC files, or the zip they came in), checks every file, tells you whether it suits your phone, and prepares what Odin needs: images taken out, a Magisk root, or a tar for Odin. Nothing is written to the phone from this page.

## steps
1. Download the official firmware for your exact model (for example SM-S918B), and ideally your region.
2. Press **Open firmware…** and pick the files, their folder or the zip. Wait until every file shows **MD5 ✓**.
3. With the phone in Android and USB debugging on, press **Check the phone**: the model, bootloader binary, Android version and CSC are compared, and Knox is shown.
4. To take images out (boot, init_boot, super…), tick them under **Inside** and press **Extract selected (undoes lz4)**.
5. To root with Magisk, press **Root with Magisk…**, patch the file in Magisk on the phone, and the patched tar comes back checked.
6. Flash with Odin: AP, BL and CP, and **HOME_CSC** in the CSC slot to keep your data.

## danger
- **CSC wipes the phone**; HOME_CSC keeps your data. Use CSC only on purpose: a first Magisk install, a region change or a clean start.
- Flashing anything Samsung did not sign (a Magisk-patched AP, TWRP, a custom kernel) trips **Knox for good**: Samsung Wallet, Secure Folder and Samsung Pass stop working, and official firmware does not bring them back.
- Firmware with an **older bootloader binary** than the phone's is refused by the phone (SW REV CHECK FAIL), and firmware for another model can stop it from starting. This page warns about both.
- The **first Magisk install needs a full wipe**: the patched AP goes with BL, CP and **CSC**. Never flash AP alone.
- On a rooted Samsung, never put boot, init_boot, recovery or vbmeta back to stock on their own: flash the whole firmware.
- Keep the battery above 50%, and do not unplug the cable while Odin writes.

## tips
- To enter Download mode: switch the phone off, hold Volume up and Volume down together, and plug in the USB cable (most models since 2019).
- Download mode needs **Samsung's USB driver**; if it is missing, install it from the box on this page.
- Anything unofficial needs an unlocked bootloader: Developer options → OEM unlocking, then a long press on Volume up in Download mode. Unlocking wipes the phone.
- If Download mode shows **KG** or **RMM** as Prenormal or Locked, the phone refuses anything unofficial until that clears.
- Knox is shown as the phone reports it; a root-hiding module can make a tripped phone read 0x0.
- Flashing from this app itself is coming; for now, the flashing is done in Odin.

## terms
AP | The system: boot, init_boot, recovery, super and vbmeta
BL | The bootloader, the first thing that runs
CP | The modem: calls and mobile data
CSC | The region's settings; this file wipes the phone
HOME_CSC | The same settings, without the wipe
Binary | The bootloader's version (the 3 in S918BXXU3BWK5); a phone never goes back to a lower one
Knox | Samsung's security; it trips once, for good
Download mode | Samsung's flashing mode, where Odin works; Samsung phones have no fastboot
Odin | Samsung's flashing tool for Windows
