using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FastbootEnhance.Core.Help;
using FastbootEnhance.Core.Update;
using FastbootEnhance.Core.Usb;

namespace FastbootEnhance.Core.Tests
{
    public class HelpTests
    {
        // ---------------------------------------------------------------- the format

        [Fact]
        public void A_help_file_is_read_into_its_sections()
        {
            HelpDoc doc = HelpDoc.Parse(
                "## purpose\nOpens firmware\nand checks it.\n\n" +
                "## steps\n1. Open it.\n2. Check **MD5**,\n   then go on.\n10. Last.\n\n" +
                "## danger\n- CSC wipes the phone.\n- Knox trips.\n\n" +
                "## terms\nBL | the bootloader\nAP | the system\n");

            Assert.Empty(doc.Problems);
            Assert.Equal(new[] { HelpSectionKind.Purpose, HelpSectionKind.Steps, HelpSectionKind.Danger, HelpSectionKind.Terms },
                doc.Sections.Select(s => s.Kind));
            HelpItem purpose = Assert.Single(doc.Get(HelpSectionKind.Purpose).Items);
            Assert.Equal(HelpItemKind.Paragraph, purpose.Kind);
            Assert.Equal("Opens firmware and checks it.", purpose.Text);

            List<HelpItem> steps = doc.Get(HelpSectionKind.Steps).Items;
            Assert.Equal(3, steps.Count);
            Assert.All(steps, s => Assert.Equal(HelpItemKind.Numbered, s.Kind));
            Assert.Equal("Check **MD5**, then go on.", steps[1].Text);
            Assert.Equal("Last.", steps[2].Text);

            Assert.Equal(2, doc.Get(HelpSectionKind.Danger).Items.Count(i => i.Kind == HelpItemKind.Bullet));
            List<HelpItem> terms = doc.Get(HelpSectionKind.Terms).Items;
            Assert.Equal(new[] { "BL", "AP" }, terms.Select(t => t.Term));
            Assert.Equal("the system", terms[1].Text);
        }

        [Fact]
        public void Mistakes_in_a_help_file_are_reported()
        {
            HelpDoc doc = HelpDoc.Parse("before\n## purpose\nok\n## warnings\n- lost\n## purpose\nagain\n");
            // The bullet under the unknown heading has no section either.
            Assert.Equal(4, doc.Problems.Count);
            Assert.Contains(doc.Problems, p => p.Contains("outside a section"));
            Assert.Contains(doc.Problems, p => p.Contains("unknown heading"));
            Assert.Contains(doc.Problems, p => p.Contains("twice"));
        }

        [Fact]
        public void Bold_marks_are_cut_into_spans()
        {
            Assert.Equal(new[] { ("Use ", false), ("HOME_CSC", true), (" to keep data", false) },
                HelpDoc.Spans("Use **HOME_CSC** to keep data").Select(s => (s.Key, s.Value)));
            Assert.Equal(new[] { ("a **b", false) }, HelpDoc.Spans("a **b").Select(s => (s.Key, s.Value)));
            Assert.Empty(HelpDoc.Spans(""));
        }

        // ---------------------------------------------------------------- the files shipped with the app

        /// <summary>The pages with a ؟ button, as named in MainWindow.xaml (tab name without "_tab").</summary>
        static readonly string[] Pages =
            { "device", "phone", "flash", "payload", "images", "super", "samsung", "backup", "root", "terminal", "logs", "settings", "about" };

        static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "FastbootEnhance.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir;
        }

        [Theory]
        [InlineData("en")]
        [InlineData("ar")]
        public void Every_page_has_its_help_in_both_languages(string language)
        {
            string root = RepoRoot();
            foreach (string page in Pages)
            {
                string path = Path.Combine(root, "Help", language, page + ".md");
                Assert.True(File.Exists(path), path + " is missing");
                HelpDoc doc = HelpDoc.Parse(File.ReadAllText(path));
                Assert.True(doc.Problems.Count == 0, path + ": " + string.Join("; ", doc.Problems));
                Assert.True(doc.Get(HelpSectionKind.Purpose)?.Items.Count > 0, path + " has no purpose");
                Assert.True(doc.Get(HelpSectionKind.Steps)?.Items.Count > 0, path + " has no steps");
            }
            // Nothing stray: every file belongs to a page.
            foreach (string file in Directory.GetFiles(Path.Combine(root, "Help", language)))
                Assert.Contains(Path.GetFileNameWithoutExtension(file), Pages);
        }

        [Fact]
        public void Both_languages_have_the_same_sections_for_each_page()
        {
            string root = RepoRoot();
            foreach (string page in Pages)
            {
                HelpDoc en = HelpDoc.Parse(File.ReadAllText(Path.Combine(root, "Help", "en", page + ".md")));
                HelpDoc ar = HelpDoc.Parse(File.ReadAllText(Path.Combine(root, "Help", "ar", page + ".md")));
                Assert.True(en.Sections.Select(s => s.Kind).SequenceEqual(ar.Sections.Select(s => s.Kind)), page + ": sections differ");
                foreach (HelpSection s in en.Sections)
                    Assert.True(s.Items.Count == ar.Get(s.Kind).Items.Count, page + " " + s.Kind + ": " + s.Items.Count + " items in English, "
                        + ar.Get(s.Kind).Items.Count + " in Arabic");
            }
        }

        [Fact]
        public void Arabic_text_writes_its_numbers_in_western_digits()
        {
            // Arabic-Indic digits read in the wrong order next to MB, slots and versions.
            string root = RepoRoot();
            List<string> files = new List<string> { Path.Combine(root, "Properties", "Resources.ar.resx"), Path.Combine(root, "CHANGELOG.md") };
            files.AddRange(Directory.GetFiles(Path.Combine(root, "Help", "ar")));
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                Match digit = Regex.Match(text, "[\u0660-\u0669\u06F0-\u06F9]");
                Assert.False(digit.Success, Path.GetFileName(file) + " has \"" + digit.Value + "\" near: "
                    + (digit.Success ? text.Substring(Math.Max(0, digit.Index - 30), Math.Min(60, text.Length - Math.Max(0, digit.Index - 30))) : ""));
            }
        }

        // ---------------------------------------------------------------- what's new

        [Fact]
        public void A_version_s_notes_are_taken_from_the_changelog()
        {
            string changelog = "# Changelog\n\n## v2.8.0\n- جديد\n- ثاني\n\n**English**\n- New\n- Second\n\n## v2.7.1\n- old\n";
            string section = ReleaseFeed.ChangelogSection(changelog, "2.8.0");
            ReleaseFeed.SplitNotes(section, out List<string> arabic, out List<string> english);
            Assert.Equal(new[] { "جديد", "ثاني" }, arabic);
            Assert.Equal(new[] { "New", "Second" }, english);
            Assert.Null(ReleaseFeed.ChangelogSection(changelog, "2.9.0"));
            // "v2.8" is not "v2.8.0", and "## v2.8.0-beta" is not "## v2.8.0".
            Assert.Null(ReleaseFeed.ChangelogSection("## v2.8.0-beta\n- x\n", "2.8.0"));
        }

        [Fact]
        public void The_app_version_has_notes_in_both_languages()
        {
            string root = RepoRoot();
            string csproj = File.ReadAllText(Path.Combine(root, "FastbootEnhance.csproj"));
            string version = Regex.Match(csproj, @"<Version>([^<]+)</Version>").Groups[1].Value;
            string section = ReleaseFeed.ChangelogSection(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), version);
            Assert.NotNull(section);
            ReleaseFeed.SplitNotes(section, out List<string> arabic, out List<string> english);
            Assert.NotEmpty(arabic);
            Assert.Equal(arabic.Count, english.Count);
            // The sidebar shows the version too.
            Assert.Contains("v" + version + "\"", File.ReadAllText(Path.Combine(root, "Themes", "Styles.xaml")));
            Assert.Contains("\"" + version + "\"", File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs")));
        }

        // ---------------------------------------------------------------- Samsung's USB driver

        [Fact]
        public void The_driver_download_link_is_found_on_samsung_s_page()
        {
            string html = "<a class=\"btn\" href=\"/SDP/file/a2fcaf64-7aa3-49f7-b06f-9e646590d528\">Samsung_USB_Driver_for_Mobile_Phones.zip</a>";
            Assert.Equal("https://developer.samsung.com/SDP/file/a2fcaf64-7aa3-49f7-b06f-9e646590d528", SamsungDriver.FindDownloadLink(html));
            Assert.Null(SamsungDriver.FindDownloadLink("<a href=\"https://evil.example/SDP/file/a2fcaf64-7aa3-49f7-b06f-9e646590d528\">"));
            Assert.Null(SamsungDriver.FindDownloadLink(null));
        }

        [Fact]
        public void An_installed_driver_is_recognised()
        {
            Assert.True(SamsungDriver.IsDriverStoreFolder("ssudbus.inf_amd64_5f2b1c0e6d9a8f71"));
            Assert.True(SamsungDriver.IsDriverStoreFolder("SSUDMDM.INF_arm64_0123"));
            Assert.False(SamsungDriver.IsDriverStoreFolder("ssudadb.inf_amd64_0123"));
            Assert.False(SamsungDriver.IsDriverStoreFolder("android_winusb.inf_amd64_1"));
            Assert.True(SamsungDriver.IsProgramName("SAMSUNG USB Drivers for Mobile Phones"));
            Assert.True(SamsungDriver.IsProgramName("SAMSUNG USB Driver for Mobile Phones"));
            Assert.False(SamsungDriver.IsProgramName("Samsung Smart Switch"));
        }

        [Fact]
        public void Only_samsung_s_certificate_subject_passes()
        {
            // As Windows decodes the subject of the certificate on v1.9.5.0, one attribute per line.
            Assert.True(SamsungDriver.IsSamsungSubject(
                "SERIALNUMBER=130111-0006246\r\nCN=Samsung Electronics CO., LTD.\r\nO=Samsung Electronics CO., LTD.\r\nL=Suwon-si\r\nS=Gyeonggi-do\r\nC=KR\r\nOID.1.3.6.1.4.1.311.60.2.1.3=KR"));
            Assert.False(SamsungDriver.IsSamsungSubject("CN=Samsung Electronics CO., LTD.\nO=Evil Ltd\nC=KR"));
            Assert.False(SamsungDriver.IsSamsungSubject("O=Samsung Electronics CO., LTD. Fake\nC=KR"));
            Assert.False(SamsungDriver.IsSamsungSubject("O=Samsung Electronics CO., LTD.\nC=US"));
            // Two organisations, or two countries: unclear, so refused.
            Assert.False(SamsungDriver.IsSamsungSubject("O=Evil\nO=Samsung Electronics CO., LTD.\nC=KR"));
            Assert.False(SamsungDriver.IsSamsungSubject("O=Samsung Electronics CO., LTD.\nC=KR\nC=US"));
            // The comma form a value can hide another attribute in is not read as one.
            Assert.False(SamsungDriver.IsSamsungSubject("CN=a, O=Samsung Electronics CO., LTD., C=KR"));
            // The jurisdiction OID is not the country.
            Assert.False(SamsungDriver.IsSamsungSubject("O=Samsung Electronics CO., LTD.\nOID.1.3.6.1.4.1.311.60.2.1.3=KR"));
            Assert.False(SamsungDriver.IsSamsungSubject(null));
        }

        [Fact]
        public void The_installer_names_itself()
        {
            Assert.True(SamsungDriver.IsInstallerProduct("Samsung USB Driver for Mobile Phones"));
            Assert.True(SamsungDriver.IsInstallerProduct(" SAMSUNG USB Driver for Mobile Phones "));
            Assert.False(SamsungDriver.IsInstallerProduct("Samsung Smart Switch"));
            Assert.False(SamsungDriver.IsInstallerProduct(null));
        }
    }
}
