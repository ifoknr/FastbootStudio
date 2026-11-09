## purpose
The app's settings: the language, the appearance, the text size, the glass backdrop, the drawing mode, update checks, USB drivers, and whether a connected phone is asked for root. The welcome tour, What's new and the keyboard shortcuts are here too.

## steps
1. **Language**: English or Arabic; the app restarts in it. Arabic turns the whole window right to left. (Chinese, Japanese and Korean return once their translations are complete.)
2. **Appearance**: Dark, Light, or As Windows. It applies after a restart.
3. **Text size**: Small, Default, Large or Larger. Every page follows it right away, so the page is its own preview.
   **Glass backdrop** (Windows 11): the desktop shows through the window, blurred; off, the window keeps its own gradient.
4. **Drawing**: if the window flickers, loses menu items or shows striped buttons (some graphics drivers and remote-desktop apps), choose **Software (steady)**.
5. **Updates**: leave **Check for updates automatically** on, or press **Check now**. A new version shows what changed and downloads only if you say so.
6. **USB drivers**: a green or red dot says whether Google's and Samsung's drivers are installed. **Install…** downloads the driver from its maker, checks it, and lets Windows add it. Google's fits Pixels and phones on Google's USB ids; Samsung's is for Download mode.
7. **Phone**: **Ask the phone for root when it connects** is off by default. On, a phone over ADB is asked for su so the Companion's report (root state, AVB, module conflicts) is read; Magisk shows a prompt on the phone the first time.
8. **Show the tour again** walks you through the app; **What's new** lists this version's changes.
9. **Keyboard shortcuts**: Ctrl+O opens a file on the page you are on, Ctrl+L the log, F1 this page's help, Ctrl+, the settings.

## tips
- An update download is kept only when its SHA-256 matches the release, so a damaged or swapped file never stays.
- The app never replaces itself: close it and unzip the new version, the same way you installed it.
- Questions and ideas: the Telegram group, or IFOKNR's GitHub.

## terms
SHA-256 | A fingerprint of a file; a single changed byte gives a different one
Drawing | How the window is painted: by the graphics card, or by the processor (steadier)
