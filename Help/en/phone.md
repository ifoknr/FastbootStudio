## purpose
The phone while it runs Android: its model, chip, Android version, security patch, slot, battery and storage, its kernel and security (bootloader lock, root, verified boot), and the backups the Companion made on it. Nothing is written to the phone from here.

## steps
1. On the phone, turn on **USB debugging** (Settings → About phone → tap Build number 7 times, then Developer options), plug it in and allow this computer.
2. The page fills in by itself. If the phone asks for root, allow it: only the Companion's report needs it.
3. With **Fastboot Studio Companion** installed on a rooted phone, you also see the bootloader's own verified-boot state and module conflicts. **Get the Companion** opens its download page.
4. **Backups on the phone**: **Copy and verify** brings a backup to this PC and checks every file against its SHA-256.
5. The restart buttons at the top take the phone to recovery, fastbootd or the bootloader.

## tips
- "Unauthorized" means the phone has not allowed this computer: unlock it and accept the prompt.
- A root-hiding module can make the phone report itself as locked and unrooted; the Companion shows when values are hidden.
- Copied backups go to Documents\Fastboot Studio\Backups.

## terms
USB debugging | Lets a computer talk to the phone over adb
adb | The tool that talks to a phone running Android or recovery
Verified boot | The phone's check that its software was not changed (green, yellow, orange)
Companion | The Fastboot Studio module on the phone that reports what adb alone cannot see
Module conflict | Two root modules changing the same thing
