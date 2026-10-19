using System.Collections.Generic;
using FastbootEnhance.Core.Update;

namespace FastbootEnhance.Core.Tests
{
    public class ReleaseFeedTests
    {
        const string Hash = "3f2a5b9c0d1e4f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8";

        // The shape of api.github.com/repos/ifoknr/FastbootStudio/releases/latest, cut down.
        static string Release(string tag = "v2.6.0", string zipUrl = null, string digest = "sha256:" + Hash,
            string extra = "", string page = "https://github.com/ifoknr/FastbootStudio/releases/tag/v2.6.0")
        {
            string zip = "FastbootStudio-" + tag + "-win-x64.zip";
            zipUrl = zipUrl ?? "https://github.com/ifoknr/FastbootStudio/releases/download/" + tag + "/" + zip;
            string body = "- تفليش السلوتين\\n- إشعار التحديث\\n\\n**English**\\n- Flash both slots\\n- An [update](https://x) notice with `notes`\\n\\n---\\n- **Download** this\\n";
            return "{\"tag_name\":\"" + tag + "\",\"html_url\":\"" + page + "\",\"published_at\":\"2026-10-20T10:00:00Z\","
                + "\"draft\":false,\"prerelease\":false,\"body\":\"" + body + "\"," + extra
                + "\"assets\":[{\"name\":\"" + zip + "\",\"size\":102400000,\"digest\":\"" + digest + "\","
                + "\"browser_download_url\":\"" + zipUrl.Replace("\\", "\\\\") + "\"},"
                + "{\"name\":\"" + zip + ".sha256\",\"size\":100,\"browser_download_url\":\"https://github.com/ifoknr/FastbootStudio/releases/download/" + tag + "/" + zip + ".sha256\"}]}";
        }

        [Fact]
        public void Reads_the_version_notes_zip_and_its_hash()
        {
            ReleaseInfo info = ReleaseFeed.Parse(Release());
            Assert.NotNull(info);
            Assert.Equal("v2.6.0", info.Tag);
            Assert.Equal("2.6.0", info.Version);
            Assert.Equal("https://github.com/ifoknr/FastbootStudio/releases/tag/v2.6.0", info.Page);
            Assert.Equal(new[] { "تفليش السلوتين", "إشعار التحديث" }, info.NotesArabic);
            // Links, bold and code marks are taken out; the download notes after "---" are left out.
            Assert.Equal(new[] { "Flash both slots", "An update notice with notes" }, info.NotesEnglish);
            Assert.Equal("FastbootStudio-v2.6.0-win-x64.zip", info.ZipName);
            Assert.Equal(102400000, info.ZipBytes);
            Assert.Equal(Hash, info.ZipSha256);
            Assert.EndsWith(".zip.sha256", info.Sha256Url);
            Assert.Equal(2026, info.Published.Value.Year);
        }

        [Theory]
        [InlineData("https://evil.example/FastbootStudio-v2.6.0-win-x64.zip")]
        [InlineData("https://github.com/someone/FastbootStudio/releases/download/v2.6.0/FastbootStudio-v2.6.0-win-x64.zip")]
        [InlineData("https://github.com/ifoknr/FastbootStudio/releases/download/../../../../evil/x/releases/download/v2.6.0/FastbootStudio-v2.6.0-win-x64.zip")]
        [InlineData("https://github.com/ifoknr/FastbootStudio/releases/download/v2.6.0/other.zip")]
        [InlineData(@"C:\\Users\\me\\FastbootStudio-v2.6.0-win-x64.zip")]
        public void A_zip_from_anywhere_else_is_not_offered(string url)
        {
            ReleaseInfo info = ReleaseFeed.Parse(Release(zipUrl: url));
            Assert.NotNull(info);
            Assert.Null(info.ZipName);
            Assert.Null(info.Sha256Url);
        }

        [Fact]
        public void A_test_feed_may_point_at_a_local_zip()
        {
            ReleaseInfo info = ReleaseFeed.Parse(Release(zipUrl: @"C:\feed\FastbootStudio-v2.6.0-win-x64.zip"), allowLocal: true);
            Assert.Equal(@"C:\feed\FastbootStudio-v2.6.0-win-x64.zip", info.ZipUrl);
        }

        [Fact]
        public void The_page_opened_is_always_the_projects_own()
        {
            ReleaseInfo info = ReleaseFeed.Parse(Release(page: "https://evil.example/releases"));
            Assert.Equal(ReleaseFeed.Releases, info.Page);
        }

        [Theory]
        [InlineData("sha256:XYZ")]
        [InlineData("md5:" + Hash)]
        [InlineData("")]
        public void A_missing_or_odd_digest_reads_as_none(string digest)
        {
            Assert.Null(ReleaseFeed.Parse(Release(digest: digest)).ZipSha256);
        }

        [Theory]
        [InlineData("\"prerelease\":true,")]
        [InlineData("\"draft\":true,")]
        public void Drafts_and_pre_releases_are_never_offered(string extra)
        {
            string json = Release().Replace("\"draft\":false,\"prerelease\":false,", extra);
            Assert.Null(ReleaseFeed.Parse(json));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{\"tag_name\":\"latest\"}")]
        [InlineData("{\"message\":\"API rate limit exceeded\"}")]
        public void Anything_unreadable_is_no_release(string json)
        {
            Assert.Null(ReleaseFeed.Parse(json));
        }

        [Theory]
        [InlineData("2.6.0", "2.5.3", true)]
        [InlineData("2.5.10", "2.5.9", true)]
        [InlineData("2.5.3", "2.5.3", false)]
        [InlineData("2.5.2", "2.5.3", false)]
        [InlineData("3.0", "2.9.9", true)]
        [InlineData("x", "2.5.3", false)]
        public void Compares_versions_by_number(string latest, string installed, bool newer)
        {
            Assert.Equal(newer, ReleaseFeed.IsNewer(latest, installed));
        }

        [Theory]
        [InlineData(Hash + "  FastbootStudio-v2.6.0-win-x64.zip\n", Hash)]
        [InlineData(Hash + " *FastbootStudio-v2.6.0-win-x64.zip\r\n", Hash)]
        [InlineData(Hash + "\n", Hash)]
        [InlineData(Hash + "  something-else.zip\n", null)]
        [InlineData("nothing here", null)]
        public void Reads_a_sha256_file(string text, string expected)
        {
            Assert.Equal(expected, ReleaseFeed.ParseSha256File(text.ToUpperInvariant().Replace("FASTBOOTSTUDIO-V2.6.0-WIN-X64.ZIP", "FastbootStudio-v2.6.0-win-x64.zip").Replace("SOMETHING-ELSE.ZIP", "something-else.zip"), "FastbootStudio-v2.6.0-win-x64.zip"));
        }

        [Fact]
        public void Long_or_many_notes_are_cut()
        {
            string body = "";
            for (int i = 0; i < 30; i++)
                body += "- " + new string('x', 500) + "\n";
            List<string> arabic, english;
            ReleaseFeed.SplitNotes(body, out arabic, out english);
            Assert.Equal(12, arabic.Count);
            Assert.True(arabic[0].Length <= 301);
            Assert.Empty(english);
        }

        [Fact]
        public void Control_and_bidi_override_characters_are_dropped()
        {
            List<string> arabic, english;
            ReleaseFeed.SplitNotes("- a\u202Eb\u0007c\n", out arabic, out english);
            Assert.Equal("abc", arabic[0]);
        }
    }
}
