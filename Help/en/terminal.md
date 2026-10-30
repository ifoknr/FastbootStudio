## purpose
Runs your own adb and fastboot commands with the tools that come with the app, without opening a command prompt.

## steps
1. Type a command such as fastboot devices or adb devices -l, or press one of the ready-made ones to fill the line.
2. Press **Run** (or Enter). **Stop** ends a command that keeps running; Up and Down bring back earlier ones.
3. Drop a file on the window to add its path. **Folder…** sets where bare file names are looked for.

## danger
- Commands here go straight to the phone. A flash or erase of a critical partition asks you to type its name, **lock** asks you to type lock, and dd to a partition asks you to type dd.
- Locking the bootloader on a phone that runs anything but its own stock software can leave it unable to start.
- fastboot -w, and erasing userdata, metadata or cache, wipe the phone; you are asked first.

## tips
- Only adb and fastboot run here; an interactive adb shell is not supported, so add the command after adb shell.
- Built in: help, clear and cd.

## terms
adb | Talks to a phone in Android or recovery
fastboot | Talks to a phone in the bootloader or fastbootd
getvar | Reads a value from the bootloader (fastboot getvar all lists them)
