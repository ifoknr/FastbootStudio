using System.IO;
using System.IO.Compression;
using System.Text;
using FastbootEnhance.Core.Usb;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class GoogleDriverTests
    {
        const string Catalog = @"<?xml version=""1.0""?>
<addon:sdk-addon xmlns:addon=""http://schemas.android.com/sdk/android/repo/addon2/03"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <remotePackage path=""extras;google;admob_ads_sdk""><revision><major>11</major></revision>
    <archives><archive><complete><size>704512</size><checksum type=""sha1"">0102859d9575baa0bf4fd5eb422af2ad0fe6cb82</checksum><url>GoogleAdMobAdsSdkAndroid-6.4.1.zip</url></complete></archive></archives>
  </remotePackage>
  <remotePackage path=""extras;google;usb_driver""><revision><major>13</major></revision>
    <archives><archive><complete><size>8682039</size><checksum type=""sha1"">08a48c39084e9443f6146c239cbd3be6f91e681b</checksum><url>usb_driver_r13-windows.zip</url></complete><host-os>windows</host-os></archive></archives>
  </remotePackage>
</addon:sdk-addon>";

        [Fact]
        public void The_catalogue_names_the_windows_zip()
        {
            GoogleDriver.DriverArchive a = GoogleDriver.FromCatalog(Catalog);
            Assert.NotNull(a);
            Assert.Equal(13, a.Revision);
            Assert.Equal("https://dl.google.com/android/repository/usb_driver_r13-windows.zip", a.Url);
            Assert.Equal(8682039, a.Size);
            Assert.Equal(GoogleDriver.Known.Sha1, a.Sha1);
            Assert.Equal(GoogleDriver.Known.Sha256, a.Sha256);
        }

        [Theory]
        [InlineData("not xml")]
        [InlineData("<a/>")]
        [InlineData("<r><remotePackage path=\"extras;google;usb_driver\"><archives><archive><complete><size>10</size><checksum type=\"sha1\">08a48c39084e9443f6146c239cbd3be6f91e681b</checksum><url>https://evil.example/x.zip</url></complete></archive></archives></remotePackage></r>")]
        [InlineData("<r><remotePackage path=\"extras;google;usb_driver\"><archives><archive><complete><size>10</size><checksum type=\"sha1\">08a48c39084e9443f6146c239cbd3be6f91e681b</checksum><url>../x.zip</url></complete></archive></archives></remotePackage></r>")]
        [InlineData("<r><remotePackage path=\"extras;google;usb_driver\"><archives><archive><complete><size>10</size><url>usb_driver_r14-windows.zip</url></complete></archive></archives></remotePackage></r>")]
        public void Anything_else_gives_no_archive(string xml)
        {
            Assert.Null(GoogleDriver.FromCatalog(xml));
        }

        [Fact]
        public void A_new_revision_is_checked_by_its_sha1_only()
        {
            string xml = Catalog.Replace("usb_driver_r13-windows.zip", "usb_driver_r14-windows.zip");
            GoogleDriver.DriverArchive a = GoogleDriver.FromCatalog(xml);
            Assert.Null(a.Sha256);
        }

        static byte[] Zip(params (string name, string text)[] entries)
        {
            MemoryStream ms = new MemoryStream();
            using (ZipArchive z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach ((string name, string text) in entries)
                {
                    using (StreamWriter w = new StreamWriter(z.CreateEntry(name).Open()))
                        w.Write(text);
                }
            }
            return ms.ToArray();
        }

        [Fact]
        public void A_file_must_match_size_and_hashes()
        {
            byte[] data = Encoding.ASCII.GetBytes("hello");
            var archive = new GoogleDriver.DriverArchive();
            typeof(GoogleDriver.DriverArchive).GetProperty("Size").SetValue(archive, 5L);
            typeof(GoogleDriver.DriverArchive).GetProperty("Sha1").SetValue(archive, "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d");
            typeof(GoogleDriver.DriverArchive).GetProperty("Sha256").SetValue(archive, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
            Assert.Null(GoogleDriver.Check(new MemoryStream(data), archive));
            Assert.Contains("size", GoogleDriver.Check(new MemoryStream(Encoding.ASCII.GetBytes("hello!")), archive));
            Assert.Contains("SHA-1", GoogleDriver.Check(new MemoryStream(Encoding.ASCII.GetBytes("hellp")), archive));
            typeof(GoogleDriver.DriverArchive).GetProperty("Sha256").SetValue(archive, new string('0', 64));
            Assert.Contains("SHA-256", GoogleDriver.Check(new MemoryStream(data), archive));
        }

        [Fact]
        public void Extract_takes_the_driver_folder_and_finds_the_inf()
        {
            string folder = Path.Combine(Path.GetTempPath(), "gd-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                byte[] zip = Zip(("usb_driver/android_winusb.inf", "[Version]"), ("usb_driver/amd64/a.dll", "x"), ("other/readme.txt", "y"));
                string inf = GoogleDriver.Extract(new MemoryStream(zip), folder);
                Assert.Equal(Path.Combine(folder, "android_winusb.inf"), inf);
                Assert.True(File.Exists(Path.Combine(folder, "amd64", "a.dll")));
                Assert.False(Directory.Exists(Path.Combine(folder, "other")));
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Extract_refuses_an_entry_that_climbs_out()
        {
            string folder = Path.Combine(Path.GetTempPath(), "gd-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                byte[] zip = Zip(("usb_driver/android_winusb.inf", "x"), ("usb_driver/../../evil.txt", "y"));
                Assert.Throws<InvalidDataException>(() => GoogleDriver.Extract(new MemoryStream(zip), folder));
                Assert.Throws<InvalidDataException>(() => GoogleDriver.Extract(new MemoryStream(Zip(("usb_driver/a.txt", "x"))), folder + "2"));
            }
            finally
            {
                foreach (string f in new[] { folder, folder + "2" })
                    if (Directory.Exists(f))
                        Directory.Delete(f, true);
            }
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(3010, true)]
        [InlineData(259, true)]
        [InlineData(5, false)]
        [InlineData(-536870353, false)]
        public void Pnputil_exit_codes(int code, bool ok)
        {
            Assert.Equal(ok, GoogleDriver.PnputilSucceeded(code));
        }

        [Fact]
        public void Store_folder_and_vendor()
        {
            Assert.True(GoogleDriver.IsDriverStoreFolder("android_winusb.inf_amd64_6b2b9f1c3a1d2e4f"));
            Assert.False(GoogleDriver.IsDriverStoreFolder("ssudbus.inf_amd64_1"));
            Assert.True(GoogleDriver.Covers("18d1"));
            Assert.False(GoogleDriver.Covers("2717"));
        }
    }
}
