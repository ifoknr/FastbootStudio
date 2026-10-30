## purpose
Builds one super.img from partition images (system, vendor, product…), the same way lpmake does, so fastboot can flash it in one go. It never touches a phone.

## steps
1. The easiest start: **Import from super.img…** with the phone's original super; its layout is copied, and images in a matching _unpacked folder are attached.
2. Or set it yourself: the layout (**Virtual A/B**, A/B or **Single slot**), the super size (**Read size from phone** reads it from a phone in fastboot), and the group.
3. Add the images with **Add images…** or **Add folder…**. The bar shows how full super is.
4. Fix anything in the red box: the build stays off until it is empty.
5. Press **Build super.img…** and choose where to save it. **Check the image after building** reads it back.

## danger
- Flashing a super built with the wrong size or layout can stop the phone from starting. Take the size and layout from this phone, and keep a backup.
- super holds system, vendor and the rest: flashing it replaces all of them at once.

## tips
- **Sparse** output is smaller and is what fastboot flashes; choose raw only for tools that need it.
- Flash it with: fastboot flash super super.img.

## terms
super | One partition holding the logical ones
Group | A set of logical partitions with a size limit inside super
Virtual A/B | One copy of each partition; updates use snapshots
Metadata | The table at the start of super that lists its partitions
