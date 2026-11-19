#!/usr/bin/env bash
# Every page of the navigation rail (a top-level TabItem of main_tabs in MainWindow.xaml,
# named <page>_tab) needs two things the compiler does not check:
#   - its line on the tour's map: welcome_page_<page> in each language's .resx;
#   - its help: Help/<language>/<page>.md, for each language that has a Help folder.
# Run from the repository root. Exits 1 and names what is missing.
set -u

pages=$(grep -oE '^        <TabItem x:Name="[a-z_]+_tab"' MainWindow.xaml | sed -E 's/.*x:Name="([a-z_]+)_tab"/\1/')
if [ -z "$pages" ]; then
  echo "::error::No pages found in MainWindow.xaml; has the rail's layout changed?"
  exit 1
fi

missing=0
for page in $pages; do
  for resx in Properties/Resources.resx Properties/Resources.*.resx; do
    if ! grep -qF "<data name=\"welcome_page_$page\"" "$resx"; then
      echo "::error::$resx has no welcome_page_$page (the tour's line for the $page page)."
      missing=1
    fi
  done
  for folder in Help/*/; do
    if [ ! -f "$folder$page.md" ]; then
      echo "::error::$folder$page.md is missing (the help of the $page page)."
      missing=1
    fi
  done
done

[ "$missing" = 0 ] && echo "Pages checked: $(echo $pages | wc -w) with their tour text and help."
exit "$missing"
